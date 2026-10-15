using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using Steamworks;
using Tableflip;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace FateCoop.Prototype;

public sealed partial class Plugin
{
    private Coroutine? route, testWalk;
    private float nextPoll, nextSnapshot;
    private Monster? aggroMonster;

    private static EffectData TestBlast() => new EffectData(EffectType.HP, positive: false, ActivationType.Usage, -900, -10, 0f);

    private void UpdateDevTools(Keyboard? keyboard)
    {
        if (keyboard != null)
        {
            if (keyboard.f8Key.wasPressedThisFrame) { if (actor != null) Despawn(); else Spawn(); }
            if (keyboard.f9Key.wasPressedThisFrame) StartRoute();
            if (keyboard.f10Key.wasPressedThisFrame) StopRoute();
        }
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
            case "spawn": Spawn(); break;
            case "despawn": Despawn(); break;
            case "route": StartRoute(); break;
            case "stop": StopRoute(); break;
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
                if (!isHost || actor == null) throw new InvalidOperationException("test-aggro needs the host with a connected partner.");
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
                var dropped = dropper.inventory.FirstOrDefault(item => item != null && !Fate.IsItemSlotInEquipmentSlot(item.slotIndex)) ?? throw new InvalidOperationException("Nothing in the bag to drop.");
                dropper.RemoveItemFromInventory(dropped, RemoveItemReason.Drop);
                dropped.Drop(dropper.transform.position);
                break;
            case "test-take":
                var taker = Player.PlayerChar ?? throw new InvalidOperationException("No primary character.");
                var nearestItem = UnityEngine.Object.FindObjectsOfType<DroppedItem>().Where(d => d.Item != null).OrderBy(d => (d.transform.position - taker.transform.position).sqrMagnitude).FirstOrDefault()
                    ?? throw new InvalidOperationException("No item on the ground.");
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
                var giver = UnityEngine.Object.FindObjectsOfType<QuestGiver>().FirstOrDefault() ?? throw new InvalidOperationException("No quest giver here.");
                var offered = SingletonBehaviour<QuestManager>.Instance.GetQuestForQuestGiver(giver.CharacterName);
                if (offered.Status == Quest.QuestStatus.None) SingletonBehaviour<QuestManager>.Instance.AcceptQuest(offered, giver.CharacterName);
                break;
            case "test-finish-quest":
                var finished = ActiveQuests().FirstOrDefault(q => q.realmQuestData == null) ?? throw new InvalidOperationException("No side quest to finish.");
                finished.Status = Quest.QuestStatus.Completed;
                SingletonBehaviour<QuestManager>.Instance.ClaimRewards(finished.QuestGiverName);
                break;
            case "test-effect":
                var poisoner = Player.PlayerChar ?? throw new InvalidOperationException("No primary character.");
                (NearestEnemy(poisoner.transform.position) ?? throw new InvalidOperationException("No enemy to hit.")).AddEffect(TestBlast(), poisoner.Level, poisoner);
                break;
            case "test-effect-partner":
                if (!isHost || actor == null) throw new InvalidOperationException("test-effect-partner needs the host with a connected partner.");
                actor.AddEffect(TestBlast(), 1, NearestEnemy(actor.transform.position) ?? throw new InvalidOperationException("No enemy near the partner."));
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
                Player.latestStairsUsedWasDown = true;
                Player.isEnteringDungeonFromTownDoor = true;
                SingletonBehaviour<PlayerStatus>.Instance.DungeonRealm = RealmData.RealmId.Grove;
                SaveData.CurrentSaveData.GetRealmSave(RealmData.RealmId.Grove).SetLastVisitedDungeonDepth(1);
                SceneLoad.LoadAsync(Tableflip.Scene.Dungeon, true);
                break;
            case "next-floor":
                if (LevelGenerator.currentInstance == null || transitioning) throw new InvalidOperationException("Dungeon is not ready.");
                Player.latestStairsUsedWasDown = true;
                Fate.SetPause(true);
                LevelGenerator.currentInstance.ReplaceDungeon(0, 0, LevelGenerator.currentInstance.GetLevelDepth() + 1, (int)RealmData.RealmId.Grove, true, false);
                break;
            case "town":
                if (LevelGenerator.currentInstance == null || transitioning) throw new InvalidOperationException("Dungeon is not ready.");
                LevelGenerator.currentInstance.UpdateSaveData(false);
                Town.UpdateSpawnIndex(RealmData.RealmId.Grove, Town.EntryMethod.Stairs);
                SceneLoad.LoadAsync(Tableflip.Scene.Town_Grove, true);
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
            case "move":
            case "primary-move":
                if (parts.Length != 3) throw new ArgumentException("move <x> <z>");
                var target = parts[0] == "move" ? (Character?)actor : Player.PlayerChar;
                if (target == null) throw new InvalidOperationException("Requested actor does not exist.");
                if (target == actor) StopRoute();
                Move(target, new Vector3(float.Parse(parts[1], CultureInfo.InvariantCulture), 0, float.Parse(parts[2], CultureInfo.InvariantCulture)));
                break;
            case "quit": Despawn(); Application.Quit(); break;
            default: throw new ArgumentException("Unknown prototype command: " + parts[0]);
        }
    }

    private void Move(Character character, Vector3 desired)
    {
        if (!NavMesh.SamplePosition(desired, out var hit, 10f, NavMesh.AllAreas))
            throw new InvalidOperationException("Destination is outside the navigation mesh.");
        var agent = character.GetComponent<NavMeshAgent>();
        var path = new NavMeshPath();
        if (!agent.CalculatePath(hit.position, path) || path.status != NavMeshPathStatus.PathComplete)
            throw new InvalidOperationException("Destination is unreachable.");
        character.SetDestination(hit.position.x, hit.position.z);
        Logger.LogInfo($"MOVE actor={character.GetInstanceID()} destination={hit.position}");
    }

    private void StartRoute()
    {
        if (actor == null) Spawn();
        StopRoute();
        route = StartCoroutine(WalkRoute());
    }

    private IEnumerator WalkRoute()
    {
        var guest = actor!;
        var host = primary!;
        var origin = guest.transform.position;
        var primaryOrigin = host.transform.position;
        var hostId = host.GetInstanceID();
        var peakGuestTravel = 0f;
        var peakHostTravel = 0f;
        SetStatus("Scripted client is walking the guest.");
        // A short out-and-back route is easier to verify than unconstrained wandering.
        var offsets = new[] { Vector3.right * 8f, Vector3.forward * 8f, Vector3.left * 8f, Vector3.zero };
        foreach (var offset in offsets)
        {
            if (guest == null || host == null) yield break;
            try { Move(guest, origin + offset); }
            catch (Exception e) { Logger.LogWarning("ROUTE_WAYPOINT_SKIPPED " + e.Message); continue; }
            var agent = guest.GetComponent<NavMeshAgent>();
            var deadline = Time.unscaledTime + 12f;
            do
            {
                yield return null;
                if (guest == null || host == null) yield break;
                peakGuestTravel = Math.Max(peakGuestTravel, Vector3.Distance(origin, guest.transform.position));
                peakHostTravel = Math.Max(peakHostTravel, Vector3.Distance(primaryOrigin, host.transform.position));
            } while (Time.unscaledTime < deadline && (agent.pathPending || (agent.hasPath && agent.remainingDistance > Math.Max(agent.stoppingDistance, 0.4f))));
        }
        guest.StopPathing();
        route = null;
        var result = new RouteResult { guestTravel = peakGuestTravel, primaryTravel = peakHostTravel, primaryIdentityPreserved = Player.PlayerChar != null && Player.PlayerChar.GetInstanceID() == hostId, aiCallsSuppressed = suppressedAI };
        File.WriteAllText(Path.Combine(controlRoot, "route-result.json"), JsonUtility.ToJson(result, true));
        Logger.LogInfo("ROUTE_RESULT " + JsonUtility.ToJson(result));
        SetStatus("Walking route complete. Check route-result.json for measured independence.");
    }

    private IEnumerator TestPrimaryWalk(Vector3 direction)
    {
        // Test fixture: move the local primary through its real navigation agent, not a transform teleport.
        if (float.IsNaN(direction.x) || float.IsNaN(direction.z) || float.IsInfinity(direction.x) || float.IsInfinity(direction.z)) yield break;
        var host = Player.PlayerChar;
        var start = host.transform.position;
        var id = host.GetInstanceID();
        var agent = host.GetComponent<NavMeshAgent>();
        var end = Time.unscaledTime + 3f;
        while (Time.unscaledTime < end && host != null && Player.PlayerChar == host && !transitioning)
        {
            yield return new WaitForEndOfFrame();
            if (agent != null && agent.isOnNavMesh && !Fate.isPaused)
            {
                agent.Move(direction.normalized * 1.5f * Time.deltaTime);
                host.TurnTowardPosition(host.transform.position + direction);
            }
        }
        File.WriteAllText(Path.Combine(controlRoot, "primary-walk-result.json"), JsonUtility.ToJson(new RouteResult { primaryTravel = host != null ? Vector3.Distance(start, host.transform.position) : 0, primaryIdentityPreserved = Player.PlayerChar != null && Player.PlayerChar.GetInstanceID() == id }, true));
        testWalk = null;
    }

    private void Snapshot()
    {
        if (string.IsNullOrEmpty(controlRoot)) return;
        var host = Player.PlayerChar;
        var agent = actor != null ? actor.GetComponent<NavMeshAgent>() : null;
        var animator = actor != null ? actor.GetComponent<Animator>() : null;
        var renderers = actor != null ? actor.GetComponentsInChildren<Renderer>(true) : Array.Empty<Renderer>();
        var visibleMeshes = renderers.OfType<SkinnedMeshRenderer>().Where(r => r.enabled && r.gameObject.activeInHierarchy && r.sharedMesh != null && r.sharedMesh.vertexCount > 0).ToArray();
        var telemetry = new Telemetry
        {
            time = Time.unscaledTime, scene = SceneManager.GetActiveScene().name, status = status,
            primaryId = host != null ? host.GetInstanceID() : 0, guestId = actor != null ? actor.GetInstanceID() : 0,
            primaryPosition = host != null ? host.transform.position : Vector3.zero,
            guestPosition = actor != null ? actor.transform.position : Vector3.zero,
            guestOnNavMesh = agent != null && agent.isOnNavMesh, guestVelocity = agent != null ? agent.velocity : Vector3.zero,
            guestWalking = animator != null && animator.GetBool("IsWalking"), spawnedTotal = spawnCount,
            aiCallsSuppressed = suppressedAI, saveLoaded = SaveData.HasLoaded,
            steamInitialized = SteamReady,
            campaign = Fate.CurrentFateVer.ToString(), renderers = renderers.Length,
            worldEpoch = worldEpoch, transitioning = transitioning, dungeonDepth = LevelGenerator.currentInstance != null ? LevelGenerator.currentInstance.GetLevelDepth() : 0,
            networkConnected = peerConnected, networkAccepted = networkAccepted, networkRejected = networkRejected,
            sessionRole = link == null ? "" : isHost ? "host" : "guest", localPort = link is LocalLink local ? local.Port : 0,
            worldReady = worldReady, paused = Fate.isPaused, primaryAlive = host != null && host.Alive(),
            layoutHash = layoutHash, worldKey = CurrentWorldKey(), sharedSession = sharedSession,
            localPetJson = JsonUtility.ToJson(LocalPetSnapshot()), remotePetId = remotePet != null ? remotePet.GetInstanceID() : 0,
            remotePetPosition = remotePet != null ? remotePet.transform.position : Vector3.zero,
            remotePetModel = remotePetModel, localPetCount = host != null ? host.GetPetCount() : 0,
            remotePetActors = Object.FindObjectsOfType<Character>().Count(c => c.name == "FateCoop_Remote_Pet"),
            remotePetMeshes = remotePet != null ? remotePet.GetComponentsInChildren<SkinnedMeshRenderer>().Count(r => r.enabled && r.isVisible) : 0,
            remoteName = actor != null ? actor.CharacterName : "", monstersTracked = monsters.Count, worldMonsters = Object.FindObjectsOfType<Monster>().Count(m => !m.IsPet() && m != remotePet), aggro = aggroMonster == null ? "" : $"{aggroMonster.GetAIState()} target={(aggroMonster.Target() != null ? aggroMonster.Target().name : "none")} distance={(actor != null ? Vector3.Distance(aggroMonster.transform.position, actor.transform.position) : -1f):0.0} range={aggroMonster.InAttackRange()} facing={aggroMonster.FacingTarget()} attacking={aggroMonster.GetComponent<Animator>().GetBool("IsAttacking")} key={aggroMonster.monsterData.displayName} attack={CurrentAttackField.GetValue(aggroMonster) != null} timer={aggroMonster.attackTimer:0.00} paused={Fate.isPaused}",
            trackedHp = Mathf.RoundToInt(monsters.Values.Where(m => m != null).Sum(m => m.hp)), trackedDead = monsters.Values.Count(m => m != null && m.MyVitals.IsDead),
            hitsSent = hitsSent, hitsApplied = hitsApplied, strikesSent = strikesSent, strikesTaken = strikesTaken, killsSent = killsSent, killsTaken = killsTaken,
            swingsPlayed = swingsPlayed, castsPlayed = castsPlayed, groundItems = ground.Count(entry => entry.Value != null && (!isHost || announcedGround.Contains(entry.Key))),
            lootGiven = lootGiven, lootTaken = lootTaken, questRewardsSent = questRewardsSent, questRewardsTaken = questRewardsTaken, effectsSent = effectsSent, effectsTaken = effectsTaken, partyQuests = partyQuests, dropsSent = dropsSent, bagItems = host != null ? host.inventory.Count : 0,
            openChests = UnityEngine.Object.FindObjectsOfType<ContainerInteractable>().Count(c => c.IsOpen), brokenBarrels = UnityEngine.Object.FindObjectsOfType<ContainerDestroyable>().Count(b => b.IsDestroyed), primaryHp = host != null ? host.hp : 0f, primaryExperience = host != null ? (long)host.experience : 0,
            primaryOnNavMesh = host != null && host.GetComponent<NavMeshAgent>() != null && host.GetComponent<NavMeshAgent>().isOnNavMesh,
            gameScene = SceneLoad.CurrentScene.ToString(),
            activeMeshes = visibleMeshes.Length, meshesSeenByCamera = visibleMeshes.Count(r => r.isVisible),
            prototypeActors = Object.FindObjectsOfType<PartyMember>().Count(p => p.name == "FateCoop_Prototype_Guest")
        };
        var json = JsonUtility.ToJson(telemetry);
        File.WriteAllText(Path.Combine(controlRoot, "status.json"), json);
        File.AppendAllText(Path.Combine(controlRoot, "telemetry.jsonl"), json + Environment.NewLine);
    }

    [Serializable] private sealed class RouteResult
    {
        public float guestTravel, primaryTravel;
        public bool primaryIdentityPreserved;
        public int aiCallsSuppressed;
    }
    [Serializable] private sealed class Telemetry
    {
        public float time;
        public string scene = "", status = "", campaign = "", gameScene = "", layoutHash = "", worldKey = "", sessionRole = "";
        public int primaryId, guestId, spawnedTotal, aiCallsSuppressed, prototypeActors, renderers, activeMeshes, meshesSeenByCamera, worldEpoch, dungeonDepth, networkAccepted, networkRejected, localPort;
        public Vector3 primaryPosition, guestPosition, guestVelocity;
        public bool guestOnNavMesh, guestWalking, saveLoaded, steamInitialized, transitioning, networkConnected, worldReady, paused, primaryAlive, primaryOnNavMesh, sharedSession;
        public string localPetJson = "";
        public int remotePetId, remotePetActors, localPetCount, remotePetMeshes;
        public Vector3 remotePetPosition;
        public string remotePetModel = "";
        public string remoteName = "", aggro = "", partyQuests = "";
        public int monstersTracked, worldMonsters, trackedHp, trackedDead, hitsSent, hitsApplied, strikesSent, strikesTaken, killsSent, killsTaken, swingsPlayed, castsPlayed, groundItems, lootGiven, lootTaken, questRewardsSent, questRewardsTaken, effectsSent, effectsTaken, dropsSent, bagItems, openChests, brokenBarrels;
        public float primaryHp;
        public long primaryExperience;
    }
}
