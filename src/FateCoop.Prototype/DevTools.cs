using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Cleverous.VaultInventory;
using HarmonyLib;
using Steamworks;
using Tableflip;
using TMPro;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace FateCoop.Prototype;

public sealed partial class Plugin
{
    private const int MainHandSlot = 6;
    private static readonly AccessTools.FieldRef<LevelGenerator, TemplateData> TemplateRef = AccessTools.FieldRefAccess<LevelGenerator, TemplateData>("activeTemplate");
    private static readonly AccessTools.FieldRef<LevelGenerator, int> TemplateIndexRef = AccessTools.FieldRefAccess<LevelGenerator, int>("activeTemplateIndex");
    private static readonly AccessTools.FieldRef<PromptUI, TextMeshProUGUI> PromptBodyRef = AccessTools.FieldRefAccess<PromptUI, TextMeshProUGUI>("bodyText");
    private static readonly System.Reflection.MethodInfo ItemDepthGetter = AccessTools.PropertyGetter(typeof(LevelGenerator), "GetAdjustedItemDepth");
    private static TemplateData? forcedTemplate;
    private static TemplateData[]? allTemplates;

    private void InstallDevHooks()
    {
        harmony!.Patch(AccessTools.Method(typeof(LevelGenerator), "Create"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeLevelCreate)), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterLevelCreate)));
    }

    private static void BeforeLevelCreate(int newLevelDepth, int newLevelRealm)
    {
        if (forcedTemplate == null || (SaveData.CurrentSaveData.GetRealmSave((RealmData.RealmId)newLevelRealm).dungeonSave?.dungeons?.Any(floor => floor.depth == newLevelDepth) ?? false)) return;
        var db = SingletonBehaviour<FateDB>.Instance;
        allTemplates = db.allTemplates;
        db.allTemplates = new[] { forcedTemplate };
        forcedTemplate = null;
    }

    private static void AfterLevelCreate(LevelGenerator __instance)
    {
        if (allTemplates == null) return;
        SingletonBehaviour<FateDB>.Instance.allTemplates = allTemplates;
        TemplateIndexRef(__instance) = Array.IndexOf(allTemplates, TemplateRef(__instance));
        allTemplates = null;
    }
    private Coroutine? testWalk;
    private float nextPoll, nextSnapshot;
    private Monster? aggroMonster;

    private IEnumerator HoldMove(Vector2 input, float seconds)
    {
        testMove = input;
        yield return new WaitForSecondsRealtime(seconds);
        testMove = Vector2.zero;
    }

    private IEnumerator StopTurnNextFrame()
    {
        yield return null;
        testTurn = 0f;
    }

    private static EffectData TestBlast() => new EffectData(EffectType.HP, positive: false, ActivationType.Usage, -900, -10, 0f);

    private Partner? FirstPartner => partners.Values.FirstOrDefault(p => p.Actor != null);

    private void UpdateDevTools()
    {
        if (Time.unscaledTime >= nextPoll)
        {
            nextPoll = Time.unscaledTime + 0.2f;
            foreach (var file in Directory.GetFiles(Path.Combine(controlRoot, "commands"), "*.cmd").OrderBy(p => p, StringComparer.Ordinal))
            {
                var command = File.ReadAllText(file).Trim();
                File.Move(file, Path.Combine(controlRoot, "processed", Path.GetFileName(file)));
                try { Execute(command); }
                catch (Exception e) { SetStatus("Command failed: " + e.Message); Logger.LogError(e); }
            }
        }
        if (Time.unscaledTime >= nextSnapshot) { nextSnapshot = Time.unscaledTime + 1f; Snapshot(); }
    }
    private void Execute(string command)
    {
        Logger.LogInfo("COMMAND " + command);
        var parts = command.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;
        switch (parts[0])
        {
            case "status": Snapshot(); break;
            case "save": SaveData.RequestSave(); break;
            case "test-hit":
                var attacker = Player.PlayerChar ?? throw new InvalidOperationException("No primary character.");
                var victim = NearestEnemy(attacker.transform.position) ?? throw new InvalidOperationException("No enemy to hit.");
                attacker.SelectAttack();
                RollAttackMethod.Invoke(attacker, new object[] { victim });
                attacker.attackTimer = 0f;
                CurrentAttackField.SetValue(attacker, null);
                break;
            case "test-swing":
                if (Player.PlayerChar == null) throw new InvalidOperationException("No primary character.");
                Player.PlayerChar.SelectAttack();
                Player.PlayerChar.StartAttack();
                break;
            case "test-aggro":
                var actor = FirstPartner?.Actor;
                if (!OwnsWorld || actor == null) throw new InvalidOperationException("test-aggro needs the floor owner with a partner nearby.");
                if (aggroMonster == null || aggroMonster.MyVitals.IsDead)
                {
                    aggroMonster = NearestEnemy(actor.transform.position) ?? throw new InvalidOperationException("No enemy near the partner.");
                    if (!NavMesh.SamplePosition(actor.transform.position + actor.transform.forward * 2f, out var beside, 3f, NavMesh.AllAreas))
                        throw new InvalidOperationException("No navigation surface beside the partner.");
                    aggroMonster.SetPositionProper(beside.position);
                    aggroMonster.maxHpBase = 100000;
                    aggroMonster.hp = 100000f;
                }
                aggroMonster.SetTarget(actor);
                aggroMonster.SetAIState(AIState.Hunt);
                break;
            case "test-drop":
                var dropper = Player.PlayerChar ?? throw new InvalidOperationException("No primary character.");
                var dropped = dropper.inventory.FirstOrDefault(item => item != null && !Fate.IsItemSlotInEquipmentSlot(item.slotIndex) && (parts.Length < 2 || item.itemName == parts[1])) ?? throw new InvalidOperationException("Nothing in the bag to drop.");
                dropper.RemoveItemFromInventory(dropped, RemoveItemReason.Drop);
                dropped.Drop(dropper.transform.position);
                break;
            case "test-take":
                var taker = Player.PlayerChar ?? throw new InvalidOperationException("No primary character.");
                var nearestItem = UnityEngine.Object.FindObjectsOfType<DroppedItem>().Where(d => d.Item != null).OrderBy(d => (d.transform.position - taker.transform.position).sqrMagnitude).FirstOrDefault()
                    ?? throw new InvalidOperationException("No item on the ground.");
                if (!taker.CanFitItemInInventoryGrid(nearestItem.Item, out _)) throw new InvalidOperationException($"No room in the bag for {nearestItem.Item.itemName} ({nearestItem.Item.slotsTall}x{nearestItem.Item.slotsWide}).");
                nearestItem.Interact(taker);
                break;
            case "test-open":
                var opener = Player.PlayerChar ?? throw new InvalidOperationException("No primary character.");
                var chest = UnityEngine.Object.FindObjectsOfType<ContainerInteractable>().Where(c => !c.IsOpen && !c.IsLocked).OrderBy(c => (c.transform.position - opener.transform.position).sqrMagnitude).FirstOrDefault()
                    ?? throw new InvalidOperationException("No closed chest.");
                chest.Interact(opener);
                break;
            case "test-break":
                var breaker = Player.PlayerChar ?? throw new InvalidOperationException("No primary character.");
                var barrel = UnityEngine.Object.FindObjectsOfType<ContainerDestroyable>().Where(b => !b.IsDestroyed).OrderBy(b => (b.transform.position - breaker.transform.position).sqrMagnitude).FirstOrDefault()
                    ?? throw new InvalidOperationException("No intact barrel.");
                barrel.Destroyed();
                break;
            case "test-accept-quest":
                var giver = UnityEngine.Object.FindObjectsOfType<QuestGiver>().OrderBy(g => g.CharacterName, StringComparer.Ordinal).ElementAtOrDefault(parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0) ?? throw new InvalidOperationException("No quest giver here.");
                var offered = SingletonBehaviour<QuestManager>.Instance.GetQuestForQuestGiver(giver.CharacterName);
                if (offered.Status == Quest.QuestStatus.None) SingletonBehaviour<QuestManager>.Instance.AcceptQuest(offered, giver.CharacterName);
                break;
            case "test-finish-quest":
                var finished = SideQuests[QuestRealm].FirstOrDefault(q => q != null && (q.Status == Quest.QuestStatus.Accepted || q.Status == Quest.QuestStatus.Completed)) ?? throw new InvalidOperationException("No side quest to finish.");
                finished.Status = Quest.QuestStatus.Completed;
                if (parts.Length < 2 || parts[1] != "only") Quests.ClaimRewards(finished.QuestGiverName);
                break;
            case "test-effect":
                var poisoner = Player.PlayerChar ?? throw new InvalidOperationException("No primary character.");
                (NearestEnemy(poisoner.transform.position) ?? throw new InvalidOperationException("No enemy to hit.")).AddEffect(TestBlast(), poisoner.Level, poisoner);
                break;
            case "test-effect-partner":
                var poisoned = FirstPartner?.Actor;
                if (!OwnsWorld || poisoned == null) throw new InvalidOperationException("test-effect-partner needs the floor owner with a partner nearby.");
                poisoned.AddEffect(TestBlast(), 1, NearestEnemy(poisoned.transform.position) ?? throw new InvalidOperationException("No enemy near the partner."));
                break;
            case "test-glow":
                var glowing = Player.PlayerChar ?? throw new InvalidOperationException("No primary character.");
                var weapon = glowing.activeWeapon ?? throw new InvalidOperationException("No weapon equipped.");
                if (weapon.damageBonuses == null) weapon.damageBonuses = new List<ElementAmount>();
                weapon.damageBonuses.Add(new ElementAmount { type = DamageType.Fire, value = 5 });
                (weapon.IsInMainHand ? glowing.rightWeaponHandle : glowing.leftWeaponHandle).SetEquipment(weapon);
                break;
            case "test-aura":
                var aura = new EffectData(EffectType.PercentSpeed, positive: true, ActivationType.Usage, 30, 0, 0f) { name = "Weapon_Fire" };
                if (parts.Length > 1 && parts[1] == "monster") (NearestEnemy(Player.PlayerChar.transform.position) ?? throw new InvalidOperationException("No enemy.")).AddEffect(aura, 1, null);
                else Player.PlayerChar.AddEffect(aura, 1, null);
                break;
            case "test-cast":
                var mage = Player.PlayerChar ?? throw new InvalidOperationException("No primary character.");
                var spellTarget = NearestEnemy(mage.transform.position) ?? throw new InvalidOperationException("No enemy to cast at.");
                var castSpell = SingletonBehaviour<FateDB>.Instance.allSpells.FirstOrDefault(s => s != null && s.sphereType == SpellSphere.Attack) ?? throw new InvalidOperationException("No attack spell.");
                mage.mana = mage.MaxMana();
                mage.stamina = mage.MaxStamina();
                mage.CompleteSpell(spellTarget.transform.position, null, spellTarget, null, castSpell);
                break;
            case "test-arrow":
                var archer = Player.PlayerChar ?? throw new InvalidOperationException("No primary character.");
                var trail = Object.Instantiate(SingletonBehaviour<FateDB>.Instance.rangedAttackLineRendererFallbackPrefab, archer.transform);
                trail.positionCount = 2;
                trail.SetPosition(0, archer.transform.position + Vector3.up);
                trail.SetPosition(1, archer.transform.position + archer.transform.forward * 8f + Vector3.up);
                archer.StartCoroutine((IEnumerator)ArrowFadeMethod.Invoke(archer, new object[] { trail }));
                break;
            case "test-levelup":
                var learner = Player.PlayerChar ?? throw new InvalidOperationException("No primary character.");
                learner.AwardExperience(50000, fromMaster: false);
                break;
            case "test-fish":
                ((Player)(Player.PlayerChar ?? throw new InvalidOperationException("No primary character."))).SetIsFishing(parts.Length < 2 || parts[1] != "off");
                break;
            case "test-coop-menu": ShowCoopMenu(parts.Length < 2 || parts[1] != "off"); break;
            case "test-pause": SingletonBehaviour<PauseUI>.Instance.OnMenuButton(); break;
            case "test-move":
                if (parts.Length != 4) throw new ArgumentException("test-move <x> <y> <seconds>");
                StartCoroutine(HoldMove(new Vector2(float.Parse(parts[1], CultureInfo.InvariantCulture), float.Parse(parts[2], CultureInfo.InvariantCulture)), float.Parse(parts[3], CultureInfo.InvariantCulture)));
                break;
            case "test-turn":
                if (parts.Length != 2) throw new ArgumentException("test-turn <degrees>");
                testTurn = float.Parse(parts[1], CultureInfo.InvariantCulture);
                StartCoroutine(StopTurnNextFrame());
                break;
            case "test-calm":
                if (aggroMonster != null && !aggroMonster.MyVitals.IsDead)
                {
                    aggroMonster.maxHpBase = 1;
                    aggroMonster.hp = 1f;
                    aggroMonster.SetTarget(null);
                    aggroMonster.SetAIState(AIState.Wander);
                }
                aggroMonster = null;
                break;
            case "host-local": StartSession(LocalLink.Listen(), true); break;
            case "join-local":
                if (parts.Length != 2) throw new ArgumentException("join-local <port>");
                StartSession(LocalLink.Connect(int.Parse(parts[1], CultureInfo.InvariantCulture)), false);
                break;
            case "host-steam": HostSteam(); break;

            case "join-steam":
                if (parts.Length != 2) throw new ArgumentException("join-steam <lobby id>");
                JoinSteam(new CSteamID(ulong.Parse(parts[1], CultureInfo.InvariantCulture)));
                break;
            case "invite":
                if (!(link is SteamLink { CanInvite: true } steam)) throw new InvalidOperationException("Host a Steam lobby first.");
                steam.Invite();
                break;
            case "leave": EndSession("Left co-op."); break;
            case "test-walk":
                if (parts.Length != 3 || !SharedWorldReady())
                    throw new InvalidOperationException("test-walk <dx> <dz> requires a ready Grove world.");
                if (testWalk != null) StopCoroutine(testWalk);
                testWalk = StartCoroutine(TestPrimaryWalk(new Vector3(float.Parse(parts[1], CultureInfo.InvariantCulture), 0, float.Parse(parts[2], CultureInfo.InvariantCulture))));
                break;
            case "test-resume":
                var tutorial = UnityEngine.Object.FindObjectOfType<TutorialPopupUI>();
                for (var i = 0; tutorial != null && TutorialPopupUI.IsShown && i < 32; i++)
                    AccessTools.Method(typeof(TutorialPopupUI), "OnConfirmButtonClicked").Invoke(tutorial, null);
                Fate.SetPause(false);
                break;
            case "test-invincible":
                if (Player.PlayerChar == null) throw new InvalidOperationException("No primary character.");
                Player.PlayerChar.SetInvincible(parts.Length == 2 && parts[1] == "on");
                break;
            case "enter-dungeon":
                if (SceneLoad.CurrentScene != Tableflip.Scene.Town_Grove || Player.PlayerChar == null)
                    throw new InvalidOperationException("Dungeon test starts in Grove town.");
                TravelToFloor(1);
                break;
            case "next-floor":
                if (LevelGenerator.currentInstance == null || transitioning) throw new InvalidOperationException("Dungeon is not ready.");
                Fate.SetPause(true);
                TravelToFloor(RunFloorHere() + 1);
                break;
            case "test-run-floor":
                TravelToFloor(int.Parse(parts[1], CultureInfo.InvariantCulture));
                break;
            case "test-stairs":
                var goesDown = parts[1] == "down";
                Object.FindObjectsOfType<MoveUpOrDownDungeon>().First(stairs => stairs.goesDown == goesDown).Interact(Player.PlayerChar);
                AnswerPrompt(SingletonBehaviour<PromptUI>.Instance, true);
                break;
            case "town": TravelToTown(); break;
            case "test-down":
                if (Player.PlayerChar == null) throw new InvalidOperationException("No primary character.");
                Player.PlayerChar.MyVitals.Die();
                break;
            case "test-near-partner":
                var near = (parts.Length > 1 && partners.TryGetValue(int.Parse(parts[1], CultureInfo.InvariantCulture), out var chosen) ? chosen : FirstPartner)?.Actor;
                if (Player.PlayerChar == null || near == null) throw new InvalidOperationException("No partner replica to walk to.");
                Player.PlayerChar.GetComponent<NavMeshAgent>().Warp(near.transform.position + near.transform.right * 1.5f);
                break;
            case "test-npc":
                var talker = Object.FindObjectsOfType<NpcVoiceLines>().First(voice => voice.thisNpc.ToString() == parts[1]);
                Player.PlayerChar.GetComponent<NavMeshAgent>().Warp(talker.transform.position + talker.transform.forward * 2f);
                talker.GetComponent<InteractableSceneObject>().Interact(Player.PlayerChar);
                if (parts.Length > 2) AnswerPrompt(SingletonBehaviour<PromptUI>.Instance, true);
                break;
            case "test-quest-page":
                var pagedLog = Object.FindObjectOfType<QuestUI>();
                (parts[1] == "right" ? QuestLogRightRef(pagedLog) : QuestLogLeftRef(pagedLog)).onClick.Invoke();
                break;
            case "test-navgrid":
                var area = parts.Skip(1).Select(value => float.Parse(value, CultureInfo.InvariantCulture)).ToArray();
                var rows = new List<string>();
                for (var z = area[3]; z >= area[1]; z -= 1f)
                    rows.Add($"{z,6:0} " + string.Concat(Enumerable.Range(0, (int)(area[2] - area[0]) + 1).Select(step => Physics.Raycast(new Vector3(area[0] + step, 30f, z), Vector3.down, out var ground, 60f) && NavMesh.SamplePosition(ground.point, out _, 0.4f, NavMesh.AllAreas) ? '.' : '#')));
                rows.Add("       x from " + area[0] + " to " + area[2]);
                File.WriteAllLines(Path.Combine(controlRoot, "dump.txt"), rows);
                break;
            case "test-lighting":
                var renderers = Object.FindObjectsOfType<Renderer>();
                File.WriteAllLines(Path.Combine(controlRoot, "dump.txt"), new[]
                {
                    $"LIGHTMAPS {LightmapSettings.lightmaps.Length} | renderers using them {renderers.Count(r => r.lightmapIndex >= 0 && r.lightmapIndex < 65534)}/{renderers.Length}",
                    $"AMBIENT {RenderSettings.ambientMode} sky={RenderSettings.ambientSkyColor} equator={RenderSettings.ambientEquatorColor} ground={RenderSettings.ambientGroundColor} flat={RenderSettings.ambientLight} intensity={RenderSettings.ambientIntensity}",
                    $"FOG {RenderSettings.fog} {RenderSettings.fogMode} color={RenderSettings.fogColor} start={RenderSettings.fogStartDistance} end={RenderSettings.fogEndDistance} density={RenderSettings.fogDensity} skybox={(RenderSettings.skybox != null ? RenderSettings.skybox.name : "-")} sun={(RenderSettings.sun != null ? RenderSettings.sun.name : "-")}"
                }.Concat(Object.FindObjectsOfType<Light>(true).Select(light =>
                    $"LIGHT {PathOf(light.transform)} | {light.type} | on={light.isActiveAndEnabled} | baked={light.bakingOutput.lightmapBakeType} isBaked={light.bakingOutput.isBaked} | intensity={light.intensity} range={light.range} color={light.color} | shadows={light.shadows} | scene={light.gameObject.scene.name}")));
                break;
            case "test-materials":
                File.WriteAllLines(Path.Combine(controlRoot, "dump.txt"), Object.FindObjectsOfType<Renderer>().Where(r => PathOf(r.transform).IndexOf(parts[1], StringComparison.OrdinalIgnoreCase) >= 0).Take(10).SelectMany(r => new[] { "RENDERER " + PathOf(r.transform) }
                    .Concat(r.sharedMaterials.Where(m => m != null).SelectMany(m => new[] { $"  MATERIAL {m.name} | shader={m.shader.name} | queue={m.renderQueue} | keywords={string.Join(",", m.shaderKeywords)}" }
                        .Concat(Enumerable.Range(0, m.shader.GetPropertyCount()).Select(i => (name: m.shader.GetPropertyName(i), type: m.shader.GetPropertyType(i))).Select(p => p.type switch
                        {
                            UnityEngine.Rendering.ShaderPropertyType.Color => $"    {p.name} = {m.GetColor(p.name)}",
                            UnityEngine.Rendering.ShaderPropertyType.Float or UnityEngine.Rendering.ShaderPropertyType.Range => $"    {p.name} = {m.GetFloat(p.name)}",
                            UnityEngine.Rendering.ShaderPropertyType.Vector => $"    {p.name} = {m.GetVector(p.name)}",
                            _ => $"    {p.name} : {p.type} {(m.GetTexture(p.name) != null ? m.GetTexture(p.name).name : "-")}"
                        }))))));
                break;
            case "test-day":
                dayOffset = ((float.Parse(parts[1], CultureInfo.InvariantCulture) - Time.realtimeSinceStartup / dayLengthSeconds) % 1f + 1f) % 1f;
                break;
            case "test-dlcs":
                ownedDlcs = int.Parse(parts[1], CultureInfo.InvariantCulture);
                break;
            case "test-answer":
                AnswerPrompt(SingletonBehaviour<PromptUI>.Instance, parts[1] == "yes");
                break;
            case "test-warp":
                Player.PlayerChar.GetComponent<NavMeshAgent>().Warp(new Vector3(float.Parse(parts[1], CultureInfo.InvariantCulture), Player.PlayerChar.transform.position.y, float.Parse(parts[2], CultureInfo.InvariantCulture)));
                break;
            case "test-chat":
                SendChat(string.Join(" ", parts, 1, parts.Length - 1));
                break;
            case "test-save-quit":
                AccessTools.Method(typeof(PauseUI), "OnSaveAndQuitButton").Invoke(SingletonBehaviour<PauseUI>.Instance, null);
                break;
            case "test-fate-town":
                AccessTools.Method(typeof(FateChoice), "OnThirdChoice").Invoke(SingletonBehaviour<FateChoiceUI>.Instance.choice, null);
                break;
            case "test-bow":
                var archer2 = Player.PlayerChar ?? throw new InvalidOperationException("No primary character.");
                var bowData = SingletonBehaviour<FateDB>.Instance.AllItems.Find(i => i.itemType == ItemType.Bow) ?? throw new InvalidOperationException("No bow in the item database.");
                var held = archer2.inventory.FirstOrDefault(i => i != null && i.slotIndex == MainHandSlot);
                if (held != null) archer2.RemoveItemFromInventory(held, RemoveItemReason.Drop);
                var bow = new Item(bowData);
                if (!archer2.AddItemToInventory(bow, AddItemReason.Loot, MainHandSlot, desiredSlotIsGlobal: true)) throw new InvalidOperationException("Couldn't equip the bow.");
                if (archer2.activeWeapon != bow) archer2.EquipItem(bow, archer2.GetActiveWeaponSet);
                break;
            case "test-tab":
                ((UITabs)AccessTools.Field(typeof(PlayerUI), "uiTabs").GetValue(SimpleSingletonBehaviour<PlayerUI>.Instance)).SetTab((UITabs.TabType)Enum.Parse(typeof(UITabs.TabType), parts[1]));
                break;
            case "test-quest-scroll":
                var questLog = Object.FindObjectOfType<QuestUI>();
                UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(SideQuestRowsRef(questLog).Last(row => row.gameObject.activeSelf).questButton.gameObject);
                break;
            case "test-goto-scroll":
                UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(goToButtons.Last(button => button.gameObject.activeSelf).gameObject);
                break;
            case "test-summon":
                var summoner = Player.PlayerChar;
                var summon = SingletonBehaviour<FateDB>.Instance.allSpells.First(s => s != null && s.IsSummonSpell());
                summoner.activeSpell = summon;
                summoner.mana = summoner.MaxMana();
                summoner.TryCastCurrentSpell();
                break;
            case "test-unsummon":
                foreach (var summoned in PetsRef(Player.PlayerChar).Where(c => c != null && c.IsSummoned()).ToList()) summoned.MyVitals.Die();
                SaveData.RequestSave();
                break;
            case "test-spend":
                var stat = (Tableflip.Attribute)Enum.Parse(typeof(Tableflip.Attribute), parts[1]);
                for (var i = int.Parse(parts[2], CultureInfo.InvariantCulture); i > 0; i--) Player.PlayerChar.SpendPoint(stat);
                break;
            case "test-heavy":
                var lifter = Player.PlayerChar;
                var heavyData = SingletonBehaviour<FateDB>.Instance.AllItems.Find(i => i.ItemCategory == ItemCategory.Weapon && i.requirements != null
                    && i.requirements.Any(r => r.attribute == Tableflip.Attribute.Strength && r.value > StartingStats[Tableflip.Attribute.Strength] && r.value <= lifter.attributes.Strength)
                    && i.requirements.All(r => r.attribute == Tableflip.Attribute.Strength) && lifter.CanFitItemInInventoryGrid(new Item(i), out _)) ?? throw new InvalidOperationException("No weapon fits the current strength.");
                var wielded = lifter.inventory.FirstOrDefault(i => i != null && i.slotIndex == MainHandSlot);
                if (wielded != null) lifter.RemoveItemFromInventory(wielded, RemoveItemReason.Drop);
                var heavy = new Item(heavyData);
                if (!lifter.AddItemToInventory(heavy, AddItemReason.Loot, MainHandSlot, desiredSlotIsGlobal: true)) throw new InvalidOperationException("Couldn't equip the weapon.");
                if (lifter.activeWeapon != heavy) lifter.EquipItem(heavy, lifter.GetActiveWeaponSet);
                break;
            case "test-shift-heal":
                var healer = Player.PlayerChar ?? throw new InvalidOperationException("No primary character.");
                var heal = SingletonBehaviour<FateDB>.Instance.allSpells.FirstOrDefault(s => s != null && s.IsHealSpell() && Friendly(s) && s.targetType == ItemTarget.User) ?? throw new InvalidOperationException("No friendly self heal.");
                healer.activeSpell = heal;
                healer.mana = healer.MaxMana();
                testShift = true;
                try { healer.TryCastCurrentSpell(); }
                finally { testShift = false; }
                break;
            case "test-portal":
                if (!SingletonBehaviour<PortalManager>.Instance.TryCreatePortal(Player.PlayerChar)) throw new InvalidOperationException("Couldn't open a portal here.");
                break;
            case "test-use-portal":
                var friendPortals = partners.Values.SelectMany(p => new[] { p.TownPortal, p.Portal }).Where(p => p != null).ToList();
                var usedPortal = parts.Length > 1 && parts[1] == "own"
                    ? Object.FindObjectsOfType<EnterPortal>().FirstOrDefault(p => p.portalType == EnterPortal.PortalType.Dungeon && !friendPortals.Contains(p.gameObject)) ?? throw new InvalidOperationException("No portal of yours here.")
                    : (partners.Values.Select(p => p.TownPortal).FirstOrDefault(p => p != null) ?? throw new InvalidOperationException("No friend portal here.")).GetComponent<EnterPortal>();
                usedPortal.Interact(Player.PlayerChar);
                AnswerPrompt(SingletonBehaviour<PromptUI>.Instance, true);
                break;
            case "follow-partner":
                if (followAnswer != null) AnswerPrompt(SingletonBehaviour<PromptUI>.Instance, true);
                else GoToPartner((parts.Length > 1 && partners.TryGetValue(int.Parse(parts[1], CultureInfo.InvariantCulture), out var leader) ? leader : PartnersElsewhere().FirstOrDefault()) ?? throw new InvalidOperationException("Nobody to go to."));
                break;
            case "menu":
                if (!SaveData.HasLoaded || Player.PlayerChar != null) throw new InvalidOperationException("Wait for startup, and use menu only before entering gameplay.");
                EngagementUI.hasEngaged = true;
                Fate.CurrentFateVer = Fate.FateVer.Original;
                SceneLoad.LoadAsync(Tableflip.Scene.MainMenu_Grove, false);
                break;
            case "new-game":
                if (!SceneLoad.IsInMainMenuScene || Fate.CurrentFateVer != Fate.FateVer.Original)
                    throw new InvalidOperationException("Open the Original FATE / Grove main menu first.");
                if (SaveData.GetActiveSaveSlotForGame(Fate.FateVer.Original, false) != null)
                    throw new InvalidOperationException("A test character already exists; continue it from the game menu.");
                SaveData.CreateNewSaveSlot(new NewgameUI.CharacterCreation
                {
                    male = true, race = Race.Human, hairIndex = 0, headIndex = 0,
                    playerName = "Coop Test Host", petName = "Test Cat", petMonsterId = "Pet_Cat",
                    difficulty = Fate.Difficulty.Adventurer
                });
                MainMenuUI.NewGameRequested();
                break;
            case "continue":
                if (!SceneLoad.IsInMainMenuScene || Fate.CurrentFateVer != Fate.FateVer.Original || SaveData.CurrentSaveData?.playerSaveData == null)
                    throw new InvalidOperationException("Open Grove's menu with a test character first.");
                MainMenuUI.ContinueRequested();
                break;
            case "skip-story":
                if (SingletonBehaviour<StoryUI>.InstanceExists())
                    AccessTools.Method(typeof(StoryUI), "OnContinueButton").Invoke(SingletonBehaviour<StoryUI>.Instance, null);
                break;
            case "test-dump":
                Dump(string.Join(" ", parts, 1, parts.Length - 1));
                break;
            case "test-items":
                var itemType = (ItemType)Enum.Parse(typeof(ItemType), parts[1]);
                File.WriteAllLines(Path.Combine(controlRoot, "dump.txt"), SingletonBehaviour<FateDB>.Instance.AllItems.Where(i => i.itemType == itemType).Select(i =>
                    $"{i.displayName} | ref={i.refId} | icon={i.iconPath} ({i.IconName}) | model={i.modelPath} | rarity={i.rarity} depth={i.minimumDepth}-{i.maximumDepth} | value={i.value} | expansion={i.expansion} | effects=[{string.Join("; ", i.itemEffects.Select(e => $"{e.buffType} {e.value}/{e.value2}/{e.value3} dur={e.duration} {e.activationType} {e.target}"))}] | desc={i.description}"));
                break;
            case "test-fixture":
                var visitor = Player.PlayerChar;
                var kind = (FixtureKind)Enum.Parse(typeof(FixtureKind), parts[1]);
                var nearest = FixtureObjects().Where(f => f.kind == kind && f.usable).OrderBy(f => (f.obj.transform.position - visitor.transform.position).sqrMagnitude).FirstOrDefault().obj
                    ?? throw new InvalidOperationException("No usable " + kind + " here.");
                NavMesh.SamplePosition(nearest.transform.position, out var nearFixture, 4f, NavMesh.AllAreas);
                visitor.GetComponent<NavMeshAgent>().Warp(nearFixture.position);
                if (parts.Length > 2) break;
                if (nearest is ContainerDestroyable crystalToBreak) crystalToBreak.Destroyed();
                else ((Cleverous.VaultInventory.InteractableSceneObject)nearest).Interact(visitor);
                break;
            case "test-vitals":
                var share = float.Parse(parts[1], CultureInfo.InvariantCulture) / 100f;
                Player.PlayerChar.hp = Player.PlayerChar.MaxHP() * share;
                Player.PlayerChar.mana = Player.PlayerChar.MaxMana() * share;
                break;
            case "test-close": SimpleSingletonBehaviour<PlayerUI>.Instance.CloseTabs(); break;
            case "test-trash":
                var trasher = Player.PlayerChar;
                foreach (var junk in trasher.inventory.FindAll(i => i.InInventoryGrid).AsEnumerable().Reverse().Take(int.Parse(parts[1], CultureInfo.InvariantCulture)).ToList()) trasher.RemoveItemFromInventory(junk, RemoveItemReason.Consumed);
                break;
            case "test-dead": SetStatus("Dead handlers: " + DeadHandlers()); break;
            case "test-spin":
                GameObject.Find(parts[1]).transform.Rotate(Vector3.up, float.Parse(parts[2], CultureInfo.InvariantCulture), Space.World);
                break;
            case "test-tooltip":
                var described = Player.PlayerChar.inventory.First(i => i.itemName == parts[1] || (i.sockedtedItems?.Any(gem => gem.itemName == parts[1]) ?? false));
                SimpleSingletonBehaviour<PlayerUI>.Instance.Tooltip.ShowTooltip(described, false, Player.PlayerChar);
                break;
            case "test-give":
                var receiver = Player.PlayerChar;
                if (!receiver.AddItemToInventory(new Item(SingletonBehaviour<FateDB>.Instance.AllItems.First(i => i.displayName == parts[1])), AddItemReason.Loot)) throw new InvalidOperationException("No room for " + parts[1]);
                break;
            case "test-bag":
                File.WriteAllLines(Path.Combine(controlRoot, "dump.txt"), Player.PlayerChar.inventory.SelectMany(i => new[] { i }.Concat(i.sockedtedItems ?? Enumerable.Empty<Item>()))
                    .Select(i => $"{i.itemBaseName} | name={i.itemName} | display={i.itemData?.displayName} | effects=[{string.Join("; ", i.itemData?.itemEffects.Select(e => e.buffType + " " + e.value) ?? Enumerable.Empty<string>())}]"));
                break;
            case "test-socket":
                var socketer = Player.PlayerChar;
                var socketed = socketer.inventory.First(i => i.equipped && i.IsInMainHand);
                socketed.SetIdentified(true);
                socketed.sockets = Math.Max(socketed.sockets, (socketed.sockedtedItems?.Count ?? 0) + 1);
                socketed.SocketItem(new Item(SingletonBehaviour<FateDB>.Instance.AllItems.First(i => i.displayName == parts[1])));
                break;
            case "test-buff":
                var buffer = Player.PlayerChar;
                buffer.activeSpell = SingletonBehaviour<FateDB>.Instance.allSpells.First(s => s != null && Friendly(s) && s.targetType == ItemTarget.User && s.effects != null
                    && s.effects.Any(e => e != null && e.IsPositive && e.duration > 0 && !UnstretchedEffects.Contains(e.buffType)));
                buffer.mana = buffer.MaxMana();
                buffer.TryCastCurrentSpell();
                SetStatus("Cast " + buffer.activeSpell.spellName);
                break;
            case "test-kill-pet":
                LocalCompanions().OfType<Pet>().First().MyVitals.Die();
                break;
            case "test-party-heal":
                var medic = Player.PlayerChar;
                medic.activeSpell = SingletonBehaviour<FateDB>.Instance.allSpells.First(s => s != null && s.IsHealSpell() && Friendly(s) && PartyTargets.Contains(s.targetType));
                medic.mana = Math.Max(medic.MaxMana(), medic.activeSpell.manaCost);
                medic.stamina = Math.Max(medic.MaxStamina(), medic.activeSpell.staminaCost);
                medic.TryCastCurrentSpell();
                SetStatus("Cast " + medic.activeSpell.spellName);
                break;
            case "test-hurt-pet":
                var hurtPet = LocalCompanions().OfType<Pet>().First();
                hurtPet.MyVitals.ApplyDamage(new CharacterVitals.Damage { value = Mathf.CeilToInt(hurtPet.hp) + 10, damageType = DamageType.Slashing, source = NearestEnemy(hurtPet.transform.position)?.MyVitals });
                break;
            case "test-pets":
                var db = SingletonBehaviour<FateDB>.Instance;
                File.WriteAllLines(Path.Combine(controlRoot, "dump.txt"), db.PetProfilePictures.Select(p => $"PICTURE {p.family} | {(p.regular != null ? p.regular.name : "-")} | {(p.shadowed != null ? p.shadowed.name : "-")}")
                    .Concat(db.allMonsters.Where(m => m != null && m.displayName.IndexOf(parts.Length > 1 ? parts[1] : "", StringComparison.OrdinalIgnoreCase) >= 0).Select(m => $"MONSTER {m.displayName} | family={m.family} | prefab={(m.modelPrefab != null ? m.modelPrefab.name : "-")}")));
                break;
            case "test-template":
                forcedTemplate = parts[1] == "off" ? null : SingletonBehaviour<FateDB>.Instance.allTemplates.First(t => t != null && t.templateName == parts[1]);
                break;
            case "test-templates":
                File.WriteAllLines(Path.Combine(controlRoot, "dump.txt"), SingletonBehaviour<FateDB>.Instance.allTemplates.Where(t => t != null).Select(t => $"{t.realmId} | {t.templateName} | {string.Join(", ", t.props.Where(p => p != null && p.modelPrefab != null).Select(p => p.modelPrefab.name).Where(n => FixturesByProp.ContainsKey(n) || n.Contains("Campfire")).Distinct())}"));
                break;
            case "test-realms":
                var realmDb = SingletonBehaviour<FateDB>.Instance;
                File.WriteAllLines(Path.Combine(controlRoot, "dump.txt"),
                    realmDb.realmQuests.Where(q => q != null).OrderBy(q => q.targetRealm).ThenBy(q => q.id)
                        .Select(q => $"QUEST {q.targetGame} | {q.targetRealm} | id={q.id} | depth={q.depth} | minLevel={q.minLevel} | block={q.block} | required={q.required} | hidden={q.hidden} | {q.title} | type={q.questType} | monster={q.monster} | target={q.target} | item={q.item} | race={q.raceReq} | needs=[{string.Join(",", q.keysRequired ?? new List<string>())}] | gives=[{string.Join(",", q.keysToProgress ?? new List<string>())}] | flag={q.progressFlag}")
                    .Concat(realmDb.allMonsters.Where(m => m != null && m.alignment == Alignment.Evil && !m.reserved).OrderBy(m => m.minimumDepth)
                        .Select(m => $"MONSTER {m.displayName} | realms={string.Join(",", m.realms ?? new List<int>())} | depth={m.minimumDepth}-{m.maximumDepth} | baseLevel={m.baseLevel} | expansion={m.expansion} | rarity={m.rarity}"))
                    .Concat(new[] { Fate.FateVer.Original, Fate.FateVer.UndiscoveredRealms, Fate.FateVer.TraitorSoul, Fate.FateVer.CursedKing }
                        .Select(game => $"DISALLOW {game} | types=[{string.Join(", ", realmDb.GetDisallowsByGame(game).disallowedTypes)}] | items={realmDb.GetDisallowsByGame(game).disallowedItemDatas.Length}")));
                break;
            case "test-town-scan":
                StartCoroutine(ScanTown((Tableflip.Scene)Enum.Parse(typeof(Tableflip.Scene), parts[1])));
                break;
            case "test-props":
                var templates = Resources.FindObjectsOfTypeAll<TemplateData>();
                File.WriteAllLines(Path.Combine(controlRoot, "dump.txt"), Resources.FindObjectsOfTypeAll<PropData>().Where(p => p.modelPrefab != null).OrderBy(p => p.modelPrefab.name).Select(p =>
                    $"{p.propName} | {p.modelPrefab.name} | [{string.Join(", ", p.modelPrefab.GetComponentsInChildren<Component>(true).Where(c => c != null && !(c is Transform)).Select(c => c.GetType().Name).Distinct())}] | {string.Join(", ", templates.Where(t => t.props != null && t.props.Contains(p)).Select(t => t.templateName))}"));
                break;
            case "test-fake-friends":
                for (var friend = 1; friend <= int.Parse(parts[1], CultureInfo.InvariantCulture); friend++)
                {
                    var fake = PartnerFor(HostId + friend);
                    fake.Name = friend % 3 == 0 ? "AVeryLongSteamNameForTesting" + friend : "Friend " + friend;
                    fake.LastMessage = float.PositiveInfinity;
                    fake.Frame = friend % 4 == 0
                        ? new PeerFrame { worldKey = TownKey, scene = Tableflip.Scene.Town_Grove.ToString(), hp = 80, maxHp = 100, mana = 40, maxMana = 100, level = friend }
                        : new PeerFrame { worldKey = "fake-" + friend, scene = Tableflip.Scene.Dungeon.ToString(), depth = friend * 3, hp = 50, maxHp = 100, mana = 70, maxMana = 100, level = friend };
                }
                break;
            case "quit": Application.Quit(); break;
            default: throw new ArgumentException("Unknown prototype command: " + parts[0]);
        }
    }

    private static string BagCheck(Character owner)
    {
        var cells = new int[owner.InventoryGridHeight, owner.InventoryGridWidth];
        foreach (var item in owner.inventory.Where(i => i.InInventoryGrid))
            for (var row = (item.slotIndex - 20) / owner.InventoryGridWidth; row < (item.slotIndex - 20) / owner.InventoryGridWidth + item.slotsTall; row++)
            for (var column = (item.slotIndex - 20) % owner.InventoryGridWidth; column < (item.slotIndex - 20) % owner.InventoryGridWidth + item.slotsWide; column++)
                cells[row, column]++;
        var all = Enumerable.Range(0, owner.InventoryGridHeight).SelectMany(row => Enumerable.Range(0, owner.InventoryGridWidth).Select(column => (row, column))).ToList();
        return $"overlaps={all.Count(c => cells[c.row, c.column] > 1)} mismatched={all.Count(c => (cells[c.row, c.column] > 0) == owner.IsSpaceAvailable(c.row, c.column, 1, 1))}";
    }

    private static string DeadHandlers()
    {
        const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
        var owners = Object.FindObjectsOfType<MonoBehaviour>().Where(b => b.gameObject.scene.name == "DontDestroyOnLoad" || b is Character).Cast<object>();
        var statics = new[] { typeof(Player).Assembly, typeof(SingletonBehaviour<>).Assembly }.SelectMany(AccessTools.GetTypesFromAssembly).Where(t => !t.ContainsGenericParameters)
            .SelectMany(t => t.GetFields(any | System.Reflection.BindingFlags.Static)).Where(f => typeof(Delegate).IsAssignableFrom(f.FieldType)).Select(f => (field: f, owner: (object?)null));
        var members = owners.SelectMany(o => o.GetType().GetFields(any | System.Reflection.BindingFlags.Instance).Concat(o.GetType().BaseType?.GetFields(any | System.Reflection.BindingFlags.Instance) ?? Array.Empty<System.Reflection.FieldInfo>())
            .Where(f => typeof(Delegate).IsAssignableFrom(f.FieldType)).Select(f => (field: f, owner: (object?)o)));
        var staticRefs = new[] { typeof(Player).Assembly, typeof(SingletonBehaviour<>).Assembly }.SelectMany(AccessTools.GetTypesFromAssembly).Where(t => !t.ContainsGenericParameters)
            .SelectMany(t => t.GetFields(any | System.Reflection.BindingFlags.Static)).Where(f => !typeof(Delegate).IsAssignableFrom(f.FieldType) && !f.IsLiteral)
            .SelectMany(f =>
            {
                var value = f.GetValue(null);
                var items = value is Object single ? new object[] { single } : value is System.Collections.IEnumerable list && !(value is string) ? list.Cast<object>().SelectMany(e => e is System.Collections.DictionaryEntry d ? new[] { d.Key, d.Value } : new[] { e }) : Array.Empty<object>();
                return items.Where(item => item is Object unity && unity == null && !ReferenceEquals(unity, null)).Select(_ => $"{f.DeclaringType.Name}.{f.Name}");
            });
        return string.Join("; ", statics.Concat(members)
            .SelectMany(entry => ((entry.field.GetValue(entry.owner) as Delegate)?.GetInvocationList() ?? Array.Empty<Delegate>()).Where(h => h.Target is Object target && target == null).Select(h => $"{entry.field.DeclaringType.Name}.{entry.field.Name}->{h.Method.DeclaringType?.Name}.{h.Method.Name}"))
            .Concat(staticRefs).GroupBy(s => s).Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key));
    }

    private static IEnumerable<(Component obj, FixtureKind kind, bool usable)> FixtureObjects() =>
        Object.FindObjectsOfType<ContainerDestroyable>().Where(c => FixtureOf(c.Item) != null).Select(c => ((Component)c, FixtureKind.Crystal, !c.IsDestroyed))
            .Concat(Object.FindObjectsOfType<ContainerInteractable>().Where(c => FixtureOf(c.Item) != null).Select(c => ((Component)c, FixtureKind.GoldCart, !c.IsOpen)))
            .Concat(Object.FindObjectsOfType<FountainInteractable>().Where(f => Campfires.Contains(FountainNameRef(f))).Select(f => ((Component)f, FixtureKind.Campfire, !f.IsUsed)));

    private void Dump(string filter)
    {
        var lines = new List<string>();
        var matches = Resources.FindObjectsOfTypeAll<GameObject>().Where(o => o.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).Take(300).ToList();
        foreach (var match in matches)
        {
            lines.Add((match.scene.IsValid() ? "SCENE " + match.scene.name : "ASSET") + " " + PathOf(match.transform));
            DumpObject(match.transform, lines, 1, 3);
        }
        File.WriteAllLines(Path.Combine(controlRoot, "dump.txt"), lines);
        SetStatus($"Dumped {matches.Count} objects matching {filter}");
    }

    private static void DumpObject(Transform node, List<string> lines, int depth, int maxDepth)
    {
        var indent = new string(' ', depth * 2);
        lines.Add($"{indent}{node.name} active={node.gameObject.activeSelf}/{node.gameObject.activeInHierarchy} layer={LayerMask.LayerToName(node.gameObject.layer)} pos={node.position} local={node.localPosition} scale={node.localScale} lossy={node.lossyScale} rot={node.eulerAngles}" + (node is RectTransform rect ? $" rect={rect.rect}" : ""));
        foreach (var component in node.GetComponents<Component>())
        {
            if (component == null || component is Transform) continue;
            var detail = component switch
            {
                Renderer r => $" enabled={r.enabled} materials=[{string.Join(", ", r.sharedMaterials.Where(m => m != null).Select(m => m.name + "/" + m.shader.name + "/" + (m.mainTexture != null ? m.mainTexture.name : "-") + "/" + (m.HasProperty("_Color") ? m.color.ToString() : "-")))}]" + (r is SpriteRenderer s ? $" sprite={(s.sprite != null ? s.sprite.name : "-")} color={s.color} size={s.size}" : ""),
                MeshFilter f => " mesh=" + (f.sharedMesh != null ? f.sharedMesh.name + " bounds=" + f.sharedMesh.bounds : "-"),
                Collider c => $" enabled={c.enabled} bounds={c.bounds}",
                MonoBehaviour m when m.GetType().Namespace is nameof(Tableflip) or "UnityEngine.UI" =>" enabled=" + m.enabled + " json=" + JsonUtility.ToJson(m),
                Behaviour b => " enabled=" + b.enabled,
                _ => ""
            };
            lines.Add($"{indent}  [{component.GetType().FullName}]{detail}");
        }
        if (depth >= maxDepth) return;
        foreach (Transform child in node) DumpObject(child, lines, depth + 1, maxDepth);
    }

    private IEnumerator ScanTown(Tableflip.Scene town)
    {
        quietScene = (int)town;
        try
        {
            yield return SceneManager.LoadSceneAsync((int)town, LoadSceneMode.Additive);
            var roots = SceneManager.GetSceneByBuildIndex((int)town).GetRootGameObjects();
            var lines = roots.SelectMany(root => root.GetComponentsInChildren<Component>(true)).Select(component => component switch
            {
                EnterDungeon entrance => $"ENTRANCE {PathOf(entrance.transform)} | realm={entrance.dungeonRealmOverride} | requirement={entrance.requirement}",
                EnterPortal portal => $"PORTAL {PathOf(portal.transform)} | type={portal.portalType} | realm={portal.destinationRealm} | spawn={portal.destinationSpawnPointIndex} | requirement={portal.portalRequirement}",
                RealmQuestGiver story => $"STORY {PathOf(story.transform)} | {string.Join(", ", story.realmQuests.Where(q => q != null).Select(q => $"{q.targetGame}/{q.targetRealm}/{q.id}"))}",
                QuestGiver giver => $"QUESTS {giver.CharacterName} | {giver.GetType().Name}",
                Merchant merchant => $"MERCHANT {merchant.CharacterName} | {merchant.merchantType}",
                NpcVoiceLines voice => $"VOICE {voice.thisNpc} | {PathOf(voice.transform)}",
                _ => null
            }).Where(line => line != null);
            File.WriteAllLines(Path.Combine(controlRoot, "dump.txt"), lines!);
            foreach (var root in roots) root.SetActive(false);
            yield return SceneManager.UnloadSceneAsync((int)town);
            SetStatus("Scanned " + town);
        }
        finally { quietScene = NoScene; }
    }

    private static string PathOf(Transform node) => node.parent == null ? node.name : PathOf(node.parent) + "/" + node.name;

    private IEnumerator TestPrimaryWalk(Vector3 direction)
    {
        var player = Player.PlayerChar;
        var agent = player.GetComponent<NavMeshAgent>();
        var end = Time.unscaledTime + 3f;
        while (Time.unscaledTime < end && player != null && Player.PlayerChar == player && !transitioning)
        {
            yield return new WaitForEndOfFrame();
            if (agent != null && agent.isOnNavMesh && !Fate.isPaused)
            {
                agent.Move(direction.normalized * 1.5f * Time.deltaTime);
                player.TurnTowardPosition(player.transform.position + direction);
            }
        }
        testWalk = null;
    }

    private void Snapshot()
    {
        if (string.IsNullOrEmpty(controlRoot)) return;
        var player = Player.PlayerChar;
        var partner = FirstPartner;
        var actor = partner?.Actor;
        var pet = partner?.Pet.Copy;
        var agent = actor != null ? actor.GetComponent<NavMeshAgent>() : null;
        var animator = actor != null ? actor.GetComponent<Animator>() : null;
        var telemetry = new Telemetry
        {
            time = Time.unscaledTime, scene = SceneManager.GetActiveScene().name, status = status,
            primaryId = player != null ? player.GetInstanceID() : 0, guestId = actor != null ? actor.GetInstanceID() : 0,
            primaryPosition = player != null ? player.transform.position : Vector3.zero,
            guestPosition = actor != null ? actor.transform.position : Vector3.zero,
            guestOnNavMesh = agent != null && agent.isOnNavMesh, guestWalking = animator != null && animator.GetBool("IsWalking"), guestRunning = animator != null && animator.GetBool("IsRunning"),
            spawnedTotal = spawnCount, aiCallsSuppressed = suppressedAI, saveLoaded = SaveData.HasLoaded, steamInitialized = SteamReady, campaign = Fate.CurrentFateVer.ToString(),
            worldEpoch = worldEpoch, transitioning = transitioning, dungeonDepth = RunFloorHere(),
            localDepth = LevelGenerator.currentInstance != null ? LevelGenerator.currentInstance.GetLevelDepth() : 0,
            dungeonRealm = SingletonBehaviour<PlayerStatus>.InstanceExists() ? SingletonBehaviour<PlayerStatus>.Instance.DungeonRealm.ToString() : "",
            effectiveDepth = LevelGenerator.effectiveLevelDepth,
            runSections = string.Join(",", RunSections.Select(section => section.realm + ":" + section.offset)), dlcs = ownedDlcs,
            dayPhase = DayPhase(), sunIntensity = groveSun != null ? groveSun.intensity : 0f,
            prompt = PromptUI.IsPromptActive ? PromptBodyRef(SingletonBehaviour<PromptUI>.Instance).text : "",
            itemDepth = LevelGenerator.currentInstance != null ? (int)ItemDepthGetter.Invoke(LevelGenerator.currentInstance, null) : 0,
            monsterLevels = string.Join(",", Object.FindObjectsOfType<Monster>().Where(m => !m.IsPet() && m.GetMonsterRank == Rank.Normal && !m.isUnique && PartnerOfPet(m) == null).Select(m => m.Level).OrderBy(level => level).Distinct()),
            networkConnected = Connected, networkAccepted = networkAccepted, networkRejected = networkRejected, myId = myId, worldOwner = worldOwner, ownsWorld = OwnsWorld,
            partnerCount = partners.Count, partnersInWorld = WorldPartners().Count(),
            sessionRole = link == null ? "" : isHost ? "host" : "guest", localPort = link is LocalLink local ? local.Port : 0,
            worldReady = worldReady, paused = Fate.isPaused, primaryAlive = player != null && player.Alive(),
            layoutHash = layoutHash, worldKey = CurrentWorldKey(),
            localPetCount = player != null ? player.GetPetCount() : 0,
            remotePetActors = partners.Values.Count(p => p.Pet.Copy != null), remotePetModel = partner?.Pet.Model ?? "",
            remotePetPosition = pet != null ? pet.transform.position : Vector3.zero,
            remoteName = partner?.Name ?? "", monstersTracked = monsters.Count, worldMonsters = Object.FindObjectsOfType<Monster>().Count(m => !m.IsPet() && PartnerOfPet(m) == null),
            aggro = aggroMonster == null ? "" : $"{aggroMonster.GetAIState()} target={(aggroMonster.Target() != null ? aggroMonster.Target().name : "none")}",
            trackedHp = Mathf.RoundToInt(monsters.Values.Where(m => m != null).Sum(m => m.hp)), trackedDead = monsters.Values.Count(m => m != null && m.MyVitals.IsDead),
            hitsSent = hitsSent, hitsApplied = hitsApplied, strikesSent = strikesSent, strikesTaken = strikesTaken, killsSent = killsSent, killsTaken = killsTaken,
            swingsPlayed = swingsPlayed, castsPlayed = castsPlayed, groundItems = ground.Count(entry => entry.Value != null && (!OwnsWorld || announcedGround.Contains(entry.Key))),
            lootGiven = lootGiven, lootTaken = lootTaken, questRewardsSent = questRewardsSent, questRewardsTaken = questRewardsTaken, effectsSent = effectsSent, effectsTaken = effectsTaken, damagePopups = damagePopups, spellsReplayed = spellsReplayed, arrowsDrawn = arrowsDrawn, levelUpsPlayed = levelUpsPlayed,
            remoteEffects = actor != null && shownEffects.TryGetValue(actor, out var actorEffects) ? string.Join(",", actorEffects) : "", mirrorEffects = shownEffects.Where(entry => entry.Key is Monster).Sum(entry => entry.Value.Count),
            replicaFishing = partner?.Fishing ?? 0, localFishing = LocalFishing(), cameraZoom = Cleverous.Prodigy.ProdigyCamera.Instance != null ? Cleverous.Prodigy.ProdigyCamera.Instance.GameCam.transform.localPosition.z : 0f, cameraYaw = Cleverous.Prodigy.ProdigyCamera.Instance != null ? Cleverous.Prodigy.ProdigyCamera.Instance.transform.eulerAngles.y : 0f,
            localWeaponParticles = WeaponParticles(player), replicaWeaponParticles = WeaponParticles(actor), partyQuests = SingletonBehaviour<QuestManager>.InstanceExists() && Quests.HasLoaded && SideQuests.TryGetValue(QuestRealm, out var side) ? string.Join("\n", side.Where(q => q != null && q.Status != Quest.QuestStatus.None).Select(QuestTitle)) : "", dropsSent = dropsSent, bagItems = player != null ? player.inventory.Count : 0, bagFreeCells = player != null ? Enumerable.Range(0, player.InventoryGridHeight * player.InventoryGridWidth).Count(cell => player.IsSpaceAvailable(cell / player.InventoryGridWidth, cell % player.InventoryGridWidth, 1, 1)) : 0,
            openChests = Object.FindObjectsOfType<ContainerInteractable>().Count(c => c.IsOpen), brokenBarrels = Object.FindObjectsOfType<ContainerDestroyable>().Count(b => b.IsDestroyed), primaryHp = player != null ? player.hp : 0f, primaryExperience = player != null ? (long)player.experience : 0,
            primaryOnNavMesh = player != null && player.GetComponent<NavMeshAgent>() != null && player.GetComponent<NavMeshAgent>().isOnNavMesh,
            gameScene = SceneLoad.CurrentScene.ToString(), followPrompt = followAnswer != null, inCombat = InCombat, partnerElsewhere = PartnersElsewhere().Any(), downed = downed, replicaDown = partner != null && partner.Down, reviveProgress = reviveProgress, chatReceived = chatReceived, lastChat = chatLines.Count > 0 ? chatLines[chatLines.Count - 1].line : "", fateChoice = SingletonBehaviour<FateChoiceUI>.InstanceExists() && FateChoiceUI.FateChoiceActive,
            prototypeActors = partners.Values.Count(p => p.Actor != null), partnerTownPortals = partners.Values.Count(p => p.TownPortal != null), partnerPortals = partners.Values.Count(p => p.Portal != null), partnerPlates = partners.Values.Count(p => p.Plate != null), rangedRolls = rangedRolls, rangedMisses = rangedMisses, grazes = grazes, rangedEquipped = player != null && UsingRanged(player),
            townNpcs = string.Join("; ", Object.FindObjectsOfType<NpcVoiceLines>().Select(voice => voice.thisNpc + (voice.TryGetComponent<Merchant>(out var shop) ? ":" + shop.inventory.Count : ""))),
            mainHand = player != null ? player.inventory.FirstOrDefault(i => i != null && i.slotIndex == MainHandSlot)?.itemName ?? "" : "",
            bleedOut = downed ? downedUntil - Time.unscaledTime : 0f, friendImpacts = friendImpacts, localSummons = LocalSummonSnapshots().Count,
            partnerSummons = partners.Values.Sum(p => p.Summons.Count(s => s.Copy != null)),
            mapOpenCells = SingletonBehaviour<MinimapFogGridUI>.InstanceExists() ? FogCellsRef(SingletonBehaviour<MinimapFogGridUI>.Instance)?.Count(open => open) ?? 0 : 0,
            statPoints = player != null ? player.UnusedStatPoint : 0, gold = SingletonBehaviour<PlayerStatus>.InstanceExists() ? (long)SingletonBehaviour<PlayerStatus>.Instance.gold : 0,
            dungeonFloors = RunSections.Sum(section => SaveData.CurrentSaveData?.GetRealmSave(section.realm)?.dungeonSave?.dungeons?.Count ?? 0),
            localPet = LocalCompanions().OfType<Pet>().FirstOrDefault() is Pet localPet ? $"{localPet.GetAIState()} hp={localPet.hp:0.0} dead={localPet.MyVitals.IsDead} owned={player!.HasPet(localPet)}" : "",
            fixtures = string.Join(" ", FixtureObjects().GroupBy(f => f.kind).Select(g => $"{g.Key}={g.Count(f => f.usable)}/{g.Count()}")),
            replicaImpacts = replicaImpacts, skillPoints = player != null ? player.UnusedSkillPoints : 0,
            partnerIcon = partner?.MapIcon != null ? partner.MapIcon.sprite.name + " " + ColorUtility.ToHtmlStringRGB(partner.MapIcon.color) : "",
            partnerPetPortrait = partner?.PetSlot != null && SlotPictureRef(partner.PetSlot).texture != null ? SlotPictureRef(partner.PetSlot).texture.name : "",
            buffs = player != null && EffectsRef(player).TryGetValue(ActivationType.Usage, out var usage) ? string.Join(",", usage.Where(e => e.Positive && !e.Permanent && e.duration > 0).Select(e => e.BuffType + ":" + e.duration)) : "",
            bagCheck = player != null ? BagCheck(player) : "", vitals = player != null ? $"{player.hp:0}/{player.MaxHP()} {player.mana:0}/{player.MaxMana()}" : "",
            template = LevelGenerator.currentInstance != null ? TemplateRef(LevelGenerator.currentInstance)?.templateName ?? "" : ""
        };
        var json = JsonUtility.ToJson(telemetry);
        File.WriteAllText(Path.Combine(controlRoot, "status.json"), json);
        File.AppendAllText(Path.Combine(controlRoot, "telemetry.jsonl"), json + Environment.NewLine);
    }

    [Serializable] private sealed class Telemetry
    {
        public float time, reviveProgress, primaryHp, cameraZoom, cameraYaw;
        public string scene = "", status = "", campaign = "", gameScene = "", layoutHash = "", worldKey = "", sessionRole = "", lastChat = "", remotePetModel = "", remoteName = "", aggro = "", partyQuests = "", remoteEffects = "";
        public int primaryId, guestId, spawnedTotal, aiCallsSuppressed, prototypeActors, worldEpoch, dungeonDepth, networkAccepted, networkRejected, localPort, myId, worldOwner, partnerCount, partnersInWorld, chatReceived;
        public Vector3 primaryPosition, guestPosition, remotePetPosition;
        public bool guestOnNavMesh, guestWalking, guestRunning, saveLoaded, steamInitialized, transitioning, networkConnected, worldReady, paused, primaryAlive, primaryOnNavMesh, ownsWorld, followPrompt, inCombat, partnerElsewhere, downed, replicaDown, fateChoice;
        public int remotePetActors, localPetCount, partnerTownPortals, partnerPortals, partnerPlates, rangedRolls, rangedMisses, grazes;
        public bool rangedEquipped;
        public string dungeonRealm = "", monsterLevels = "", prompt = "", runSections = "";
        public int localDepth, effectiveDepth, itemDepth, dlcs;
        public float dayPhase, sunIntensity;
        public string townNpcs = "", mainHand = "", localPet = "", fixtures = "", template = "", vitals = "", bagCheck = "", partnerIcon = "", partnerPetPortrait = "", buffs = "";
        public int replicaImpacts, skillPoints;
        public int statPoints, dungeonFloors, friendImpacts, localSummons, partnerSummons, mapOpenCells, bagFreeCells;
        public float bleedOut;
        public long gold;
        public int monstersTracked, worldMonsters, trackedHp, trackedDead, hitsSent, hitsApplied, strikesSent, strikesTaken, killsSent, killsTaken, swingsPlayed, castsPlayed, groundItems, lootGiven, lootTaken, questRewardsSent, questRewardsTaken, effectsSent, effectsTaken, damagePopups, spellsReplayed, arrowsDrawn, levelUpsPlayed, mirrorEffects, replicaFishing, localFishing, localWeaponParticles, replicaWeaponParticles, dropsSent, bagItems, openChests, brokenBarrels;
        public long primaryExperience;
    }
}
