using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Tableflip;
using UnityEngine;
using UnityEngine.AI;

namespace FateCoop.Prototype;

public sealed partial class Plugin
{
    private const float MonsterInterval = 0.1f;
    private static readonly MethodInfo RollAttackMethod = AccessTools.Method(typeof(Character), "RollAttack");
    private static readonly Func<LevelGenerator, Character, Alignment, Transform, float, float, float, int, bool> LineOfSight = AccessTools.MethodDelegate<Func<LevelGenerator, Character, Alignment, Transform, float, float, float, int, bool>>(AccessTools.Method(typeof(LevelGenerator), "CheckValidLineSight"));
    private static readonly MethodInfo CastTimerSetter = AccessTools.PropertySetter(typeof(Character), "castTimer");
    private static readonly FieldInfo CurrentAttackField = AccessTools.Field(typeof(Character), "currentAttack");
    private static readonly AccessTools.FieldRef<PlayerUI, TargetBar> TargetBarRef = AccessTools.FieldRefAccess<PlayerUI, TargetBar>("targetBar");
    private static readonly AccessTools.FieldRef<TargetBar, CharacterVitals> TargetVitalsRef = AccessTools.FieldRefAccess<TargetBar, CharacterVitals>("activeVitals");
    private readonly Dictionary<int, Monster> monsters = new Dictionary<int, Monster>();
    private readonly Dictionary<Monster, int> monsterIds = new Dictionary<Monster, int>();
    private readonly Dictionary<int, MonsterState> monsterStates = new Dictionary<int, MonsterState>();
    private readonly HashSet<int> attackingMirrors = new HashSet<int>();
    private int nextMonsterId = 1;
    private float nextMonsterSync, nextMonsterRefresh;
    private const float FullRefreshInterval = 5f, ThreatHalfLife = 8f, ThreatSwitchMargin = 1.2f;
    private readonly Dictionary<Monster, Dictionary<Character, (float amount, float time)>> threat = new Dictionary<Monster, Dictionary<Character, (float, float)>>();
    private bool applyingStrike, confirmingDeath;
    private int hitsSent, hitsApplied, strikesSent, strikesTaken, killsSent, killsTaken, effectsSent, effectsTaken;

    [Serializable] private sealed class EffectMessage
    {
        public int target, source, level;
        public bool spell;
        public EffectData effect = null!;
    }
    private sealed class MonsterState
    {
        internal const int Walking = 1, Running = 2, Attacking = 4, Dead = 8;
        internal int Id, Flags;
        internal string Key = "", Effects = "";
        internal Vector3 Position;
        internal float Yaw, Hp, MaxHp;
        internal bool Gone;

        internal static MonsterState Of(int id, Monster monster)
        {
            var animator = monster.GetComponent<Animator>();
            var flags = monster.MyVitals.IsDead ? Dead : 0;
            if (animator != null)
                flags |= (animator.GetBool("IsWalking") ? Walking : 0) | (animator.GetBool("IsRunning") ? Running : 0) | (animator.GetBool("IsAttacking") ? Attacking : 0);
            return new MonsterState { Id = id, Key = monster.monsterData.displayName, Position = monster.transform.position, Yaw = monster.transform.eulerAngles.y, Hp = monster.hp, MaxHp = monster.MaxHP(), Flags = flags, Effects = ActiveEffectNames(monster) };
        }

        internal bool Differs(MonsterState other) => Flags != other.Flags || Effects != other.Effects || Math.Abs(Hp - other.Hp) > 0.01f || Math.Abs(MaxHp - other.MaxHp) > 0.01f
            || (Position - other.Position).sqrMagnitude > 0.0025f || Math.Abs(Mathf.DeltaAngle(Yaw, other.Yaw)) > 2f;

        internal string Encode() => string.Join("|", Id.ToString(CultureInfo.InvariantCulture), Key, Number(Position.x), Number(Position.y), Number(Position.z), Number(Yaw), Number(Hp), Number(MaxHp), Flags.ToString(CultureInfo.InvariantCulture), Effects);

        internal static MonsterState Decode(string line)
        {
            var parts = line.Split('|');
            if (parts.Length == 2 && parts[1] == "-") return new MonsterState { Id = ParseInt(parts[0]), Gone = true };
            if (parts.Length != 10 || parts[1].Length == 0 || parts[1].Length > 128 || parts[9].Length > 1000) throw new InvalidOperationException("Invalid monster state.");
            var state = new MonsterState
            {
                Id = ParseInt(parts[0]), Key = parts[1], Position = new Vector3(ParseFloat(parts[2]), ParseFloat(parts[3]), ParseFloat(parts[4])),
                Yaw = ParseFloat(parts[5]), Hp = ParseFloat(parts[6]), MaxHp = ParseFloat(parts[7]), Flags = ParseInt(parts[8]), Effects = parts[9]
            };
            if (!FinitePosition(state.Position) || state.Hp < 0 || state.MaxHp <= 0 || state.MaxHp > 1000000) throw new InvalidOperationException("Invalid monster state values.");
            return state;
        }

        private static string Number(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static int ParseInt(string text) => int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
    private static float ParseFloat(string text)
    {
        var value = float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (!Finite(value)) throw new InvalidOperationException("Number is not finite.");
        return value;
    }

    private void InstallCombatHooks()
    {
        harmony!.Patch(AccessTools.Method(typeof(CharacterVitals), "ApplyDamage"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeDamage)));
        harmony.Patch(RollAttackMethod, prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeRollAttack)));
        harmony.Patch(AccessTools.Method(typeof(CharacterVitals), "Die", new[] { typeof(CharacterVitals.Damage) }), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeVitalsDie)));
        harmony.Patch(AccessTools.Method(typeof(Monster), "Die", Type.EmptyTypes), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeMonsterDie)));
        harmony.Patch(AccessTools.Method(typeof(Character), "GetDistanceToPlayer"), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterDistanceToPlayer)));
        harmony.Patch(AccessTools.Method(typeof(LevelGenerator), "FindCharacterWithinView"), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterFindCharacter)));
        harmony.Patch(AccessTools.Method(typeof(Player), "StartAttack"), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterLocalSwing)));
        harmony.Patch(AccessTools.Method(typeof(Character), "Cast", Type.EmptyTypes), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterLocalCast)));
        harmony.Patch(AccessTools.Method(typeof(PartyMember), "AnimCallbackAttackEnd"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeReplicaAttackEnd)));
        harmony.Patch(AccessTools.Method(typeof(Character), "AddEffect", new[] { typeof(EffectData), typeof(int), typeof(Character), typeof(int), typeof(bool) }), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeAddEffect)));
        harmony.Patch(AccessTools.Method(typeof(Character), "SetAIState"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeSetAIState)));
        harmony.Patch(AccessTools.Method(typeof(LevelGenerator), "AttemptGenerateMinions"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeMirrorMinions)));
        harmony.Patch(AccessTools.Method(typeof(Monster), "Update"), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterMonsterUpdate)));
        harmony.Patch(AccessTools.Method(typeof(Monster), "RespondToAttack", new[] { typeof(Character) }), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterRespondToAttack)));
    }

    private static bool BeforeSetAIState(Character __instance, AIState newState) => !(__instance is Monster monster) || !monster.MyVitals.IsDead || newState == AIState.Dying || newState == AIState.Dead;

    private static bool BeforeMirrorMinions(ref int __result)
    {
        var plugin = Active;
        if (plugin == null || (plugin.OwnsWorld && plugin.pendingWorld == null && plugin.joiningWorld == null)) return true;
        __result = 0;
        return false;
    }

    private Character? ThreatSource(Character? source) => source == null || PartnerOf(source) != null || !source.HasMaster() ? source : source.Master();

    private static float Decayed((float amount, float time) entry) => entry.amount * Mathf.Pow(0.5f, (Time.unscaledTime - entry.time) / ThreatHalfLife);

    private void AddThreat(Monster monster, Character? source, int value)
    {
        var attacker = ThreatSource(source);
        if (attacker == null || attacker is Monster) return;
        if (!threat.TryGetValue(monster, out var table)) threat[monster] = table = new Dictionary<Character, (float, float)>();
        table[attacker] = (table.TryGetValue(attacker, out var entry) ? Decayed(entry) + value : value, Time.unscaledTime);
    }

    private static void AfterRespondToAttack(Monster __instance)
    {
        var plugin = Active;
        if (plugin == null || !plugin.OwnsWorld || !plugin.threat.TryGetValue(__instance, out var table) || !__instance.Alive() || __instance.PerformingAttack() || __instance.PerformingCast()) return;
        var reach = __instance.monsterData.followRadius * 0.25f;
        var best = table.Where(e => e.Key != null && e.Key.Alive() && !e.Key.IsPassedOut() && e.Key.GetAlignment() != __instance.GetAlignment()
            && (e.Key.transform.position - __instance.transform.position).magnitude < reach).OrderByDescending(e => Decayed(e.Value)).FirstOrDefault();
        var current = __instance.Target();
        if (best.Key == null || best.Key == current) return;
        var currentThreat = current != null && table.TryGetValue(current, out var held) ? Decayed(held) : 0f;
        if (Decayed(best.Value) < currentThreat * ThreatSwitchMargin) return;
        __instance.SetTarget(best.Key);
        if (!__instance.InAttackRange()) __instance.SetAIState(AIState.Hunt);
    }

    private static bool BeforeDamage(CharacterVitals __instance, CharacterVitals.Damage data)
    {
        var plugin = Active;
        if (plugin == null || !plugin.Connected) return true;
        var target = __instance.Char;
        if (target == Player.PlayerChar && data.value > 0) plugin.lastCombatTime = Time.unscaledTime;
        var standIn = plugin.PartnerOf(target);
        if (plugin.OwnsWorld)
        {
            if (standIn != null && data.value > 0 && (data.source == null || data.source.Alignment != __instance.Alignment) && standIn.InWorld(plugin.LocalFrameWorld()))
                plugin.Send("hurt", string.Join("|", plugin.MonsterIdOf(data.source?.Char), data.value, (int)data.damageType, data.isCritical ? 1 : 0), standIn.Id, worldScoped: true);
            if (target is Monster monster && !monster.IsPet() && data.value > 0)
            {
                var players = plugin.WorldPartners().Count();
                if (players > 0) data.value = Math.Max(1, Mathf.RoundToInt(data.value / (1f + (plugin.monsterToughness - 1f) * players)));
                plugin.AddThreat(monster, data.source?.Char, data.value);
            }
            return standIn == null && plugin.PartnerOfPet(target) == null;
        }
        if (standIn != null || plugin.PartnerOfPet(target) != null) return false;
        if (!(target is Monster mirror) || !plugin.monsterIds.TryGetValue(mirror, out var id)) return true;
        var source = data.source != null ? data.source.Char : null;
        if (data.value > 0 && source != null && (source == Player.PlayerChar || (source.HasMaster() && source.Master() == Player.PlayerChar)))
        {
            plugin.SendToOwner("hit", string.Join("|", id, data.value, (int)data.damageType, data.isCritical ? 1 : 0));
            plugin.hitsSent++;
        }
        return false;
    }

    private int MonsterIdOf(Character? character) => character is Monster monster && monsterIds.TryGetValue(monster, out var id) ? id : 0;

    private static int PlayerTarget(int peer) => -(peer + 1);

    private static bool BeforeAddEffect(Character __instance, EffectData effect, int sourceLevel, Character source, int itemSlotIndex, bool isSpellEffect, ref AppliedEffect __result)
    {
        var plugin = Active;
        if (plugin == null || !plugin.Connected) return true;
        if (plugin.VisualOnly)
        {
            __result = new AppliedEffect(effect, sourceLevel, source, itemSlotIndex, isSpellEffect);
            return false;
        }
        var standIn = plugin.PartnerOf(__instance);
        var mirrorId = !plugin.OwnsWorld ? plugin.MonsterIdOf(__instance) : 0;
        if (standIn == null && mirrorId == 0 && plugin.PartnerOfPet(__instance) == null) return true;
        var local = source != null && (source == Player.PlayerChar || (source.HasMaster() && source.Master() == Player.PlayerChar));
        if (standIn != null && source != null && (local || (plugin.OwnsWorld && source is Monster)))
        {
            plugin.Send("effect", JsonUtility.ToJson(new EffectMessage { target = 0, source = local ? PlayerTarget(plugin.myId) : plugin.MonsterIdOf(source), level = sourceLevel, spell = isSpellEffect, effect = effect }), standIn.Id, worldScoped: true);
            plugin.effectsSent++;
        }
        else if (mirrorId != 0 && local)
        {
            plugin.SendToOwner("effect", JsonUtility.ToJson(new EffectMessage { target = mirrorId, source = PlayerTarget(plugin.myId), level = sourceLevel, spell = isSpellEffect, effect = effect }));
            plugin.effectsSent++;
        }
        __result = new AppliedEffect(effect, sourceLevel, source, itemSlotIndex, isSpellEffect);
        return false;
    }

    private static bool BeforeRollAttack(Character __instance, Character target, ref bool __result)
    {
        var plugin = Active;
        if (plugin == null || !plugin.Connected) return true;
        var standIn = plugin.PartnerOf(target);
        if (plugin.OwnsWorld)
        {
            if (standIn == null && plugin.PartnerOfPet(target) == null) return true;
            if (standIn != null && __instance is Monster monster && plugin.monsterIds.TryGetValue(monster, out var id) && standIn.InWorld(plugin.LocalFrameWorld()))
            {
                plugin.Send("strike", id.ToString(CultureInfo.InvariantCulture), standIn.Id, worldScoped: true);
                plugin.strikesSent++;
            }
            __result = false;
            return false;
        }
        if (plugin.applyingStrike || !(__instance is Monster mirror) || !plugin.monsterIds.ContainsKey(mirror)) return true;
        __result = false;
        return false;
    }

    private static void BeforeVitalsDie(CharacterVitals __instance, CharacterVitals.Damage finalData)
    {
        var plugin = Active;
        if (plugin == null || !plugin.OwnsWorld || !plugin.WorldPartners().Any() || !(__instance.Char is Monster monster) || !plugin.monsterIds.TryGetValue(monster, out var id)) return;
        var killer = plugin.PartnerOf(finalData?.source?.Char)?.Id ?? plugin.myId;
        plugin.SendWorld("kill", string.Join("|", id, monster.GetExperienceReward(), monster.GetFameReward(), killer));
        plugin.killsSent++;
    }

    private static bool BeforeMonsterDie(Monster __instance)
    {
        var plugin = Active;
        return plugin == null || plugin.OwnsWorld || plugin.confirmingDeath || !plugin.monsterIds.ContainsKey(__instance);
    }

    private static void AfterDistanceToPlayer(Character __instance, ref float __result)
    {
        var plugin = Active;
        if (plugin == null || !plugin.OwnsWorld) return;
        foreach (var partner in plugin.WorldPartners())
            if (partner.Actor != null) __result = Math.Min(__result, (partner.Actor.transform.position - __instance.transform.position).sqrMagnitude);
    }

    private static void AfterFindCharacter(LevelGenerator __instance, Transform transformation, Alignment alignment, float motionRadius, float coneRadius, float radius, ref Character __result)
    {
        var plugin = Active;
        if (__result != null || plugin == null || !plugin.OwnsWorld) return;
        var mask = ~((1 << LayerMask.NameToLayer("Player")) | (1 << LayerMask.NameToLayer("Player Damagable")));
        foreach (var partner in plugin.WorldPartners().Where(p => p.Actor != null).OrderBy(p => (p.Actor!.transform.position - transformation.position).sqrMagnitude))
        {
            if (!partner.Actor!.Alive() || !LineOfSight(__instance, partner.Actor, alignment, transformation, motionRadius, coneRadius, radius, mask)) continue;
            __result = partner.Actor;
            return;
        }
    }

    private static void AfterLocalSwing(Player __instance)
    {
        var plugin = Active;
        if (plugin == null || __instance != Player.PlayerChar) return;
        plugin.localSwings++;
        plugin.lastCombatTime = Time.unscaledTime;
        plugin.localSwingSpeed = __instance.attackTimer;
    }

    private static void AfterLocalCast(Character __instance)
    {
        var plugin = Active;
        if (plugin == null || __instance != Player.PlayerChar || __instance.activeSpell == null) return;
        plugin.localCasts++;
        plugin.lastCombatTime = Time.unscaledTime;
        plugin.localCastSpell = __instance.activeSpell.spellName;
    }

    private static bool BeforeReplicaAttackEnd(PartyMember __instance)
    {
        if (Active == null || Active.PartnerOf(__instance) == null) return true;
        var animator = __instance.GetComponent<Animator>();
        animator.SetBool("IsAttacking", false);
        animator.speed = 1f;
        __instance.attackTimer = 0f;
        CastTimerSetter.Invoke(__instance, new object[] { 0f });
        CurrentAttackField.SetValue(__instance, null);
        return false;
    }

    private bool InOwnersWorld => !OwnsWorld && LocalFrameWorld().Length > 0;

    private IEnumerable<Monster> LiveMonsters()
    {
        var generator = LevelGenerator.currentInstance;
        if (generator == null) yield break;
        var objects = new List<GameObject>();
        generator.FillListWithActiveCharacters(ref objects);
        foreach (var obj in objects)
            if (obj != null && obj.TryGetComponent<Monster>(out var monster) && monster.monsterData != null && monster != Player.PlayerChar.GetPet()) yield return monster;
    }

    private int RegisterMonster(Monster monster)
    {
        if (monsterIds.TryGetValue(monster, out var id)) return id;
        id = nextMonsterId++;
        monsters[id] = monster;
        monsterIds[monster] = id;
        return id;
    }

    private void RegisterMirror(int id, Monster monster)
    {
        monsters[id] = monster;
        monsterIds[monster] = id;
        monster.allowDropInventory = false;
        monster.allowDropEquipment = false;
        monster.SetTarget(null);
        monster.StopPathing();
    }

    private void ForgetMonster(int id)
    {
        if (monsters.TryGetValue(id, out var monster)) monsterIds.Remove(monster);
        monsters.Remove(id);
        monsterStates.Remove(id);
        attackingMirrors.Remove(id);
    }

    private void ResetMonsters()
    {
        foreach (var monster in monsters.Values) shownEffects.Remove(monster);
        threat.Clear();
        monsters.Clear();
        monsterIds.Clear();
        monsterStates.Clear();
        attackingMirrors.Clear();
        nextMonsterId = 1;
    }

    private List<int> AssignExportIds(DungeonSaveData floor)
    {
        var live = LiveMonsters().ToList();
        var ids = new List<int>();
        foreach (var saved in floor.monsters)
        {
            var match = string.IsNullOrEmpty(saved.key) ? null : live.FirstOrDefault(m => m.monsterData.displayName == saved.key && (m.transform.position - saved.position).sqrMagnitude < 0.0001f);
            if (match != null) live.Remove(match);
            ids.Add(match != null ? RegisterMonster(match) : 0);
        }
        return ids;
    }

    private void MatchMirrors(WorldSnapshot world)
    {
        var unmatched = LiveMonsters().Where(m => !(m.HasMaster() && m.Master() == Player.PlayerChar)).ToList();
        for (var i = 0; i < world.floor.monsters.Count; i++)
        {
            var saved = world.floor.monsters[i];
            if (world.monsterIds[i] == 0) continue;
            var match = unmatched.Where(m => m.monsterData.displayName == saved.key).OrderBy(m => (m.transform.position - saved.position).sqrMagnitude).FirstOrDefault();
            if (match == null || (match.transform.position - saved.position).sqrMagnitude > 9f) continue;
            unmatched.Remove(match);
            RegisterMirror(world.monsterIds[i], match);
        }
        foreach (var stray in unmatched) DestroyMonster(stray);
        Logger.LogInfo($"MIRRORS_MATCHED matched={monsters.Count} removed={unmatched.Count}");
    }

    private static void DestroyMonster(Monster monster)
    {
        LevelGenerator.currentInstance.RemoveActiveCharacter(monster.gameObject);
        monster.gameObject.SetActive(false);
        UnityEngine.Object.Destroy(monster.gameObject);
    }

    private void SyncMonsters()
    {
        if (Time.unscaledTime < nextMonsterSync) return;
        nextMonsterSync = Time.unscaledTime + MonsterInterval;
        if (SceneLoad.CurrentScene != Tableflip.Scene.Dungeon || !SharedWorldReady()) return;
        var inWorld = WorldPartners().Any();
        if (!inWorld || Time.unscaledTime >= nextMonsterRefresh)
        {
            monsterStates.Clear();
            nextMonsterRefresh = Time.unscaledTime + FullRefreshInterval;
        }
        var lines = new List<string>();
        var seen = new HashSet<int>();
        foreach (var monster in LiveMonsters())
        {
            var id = RegisterMonster(monster);
            seen.Add(id);
            if (!inWorld) continue;
            var state = MonsterState.Of(id, monster);
            if (monsterStates.TryGetValue(id, out var last) && !state.Differs(last)) continue;
            monsterStates[id] = state;
            lines.Add(state.Encode());
        }
        foreach (var id in monsters.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            ForgetMonster(id);
            lines.Add(id.ToString(CultureInfo.InvariantCulture) + "|-");
        }
        if (lines.Count > 0) SendWorld("monsters", string.Join(";", lines));
    }

    private void ReceiveMonsters(string body)
    {
        if (OwnsWorld || SceneLoad.CurrentScene != Tableflip.Scene.Dungeon) return;
        foreach (var line in body.Split(';'))
        {
            var state = MonsterState.Decode(line);
            if (state.Gone)
            {
                if (monsters.TryGetValue(state.Id, out var gone) && gone != null) DestroyMonster(gone);
                ForgetMonster(state.Id);
                continue;
            }
            if (!SharedWorldReady()) continue;
            if (!monsters.TryGetValue(state.Id, out var monster) || monster == null) monster = SpawnMirror(state);
            monsterStates[state.Id] = state;
            monster.maxHpBase = Mathf.RoundToInt(state.MaxHp);
            monster.hp = state.Hp;
            RefreshHealthBars(monster);
            ShowEffects(monster, state.Effects);
            if ((state.Flags & MonsterState.Dead) != 0 && !monster.MyVitals.IsDead) ConfirmDeath(monster);
        }
    }

    private void RefreshHealthBars(Monster monster)
    {
        monster.MyVitals.OnResourcesUpdated();
        if (hudOwner == null) return;
        var bar = TargetBarRef(hudOwner);
        if (!bar.gameObject.activeSelf || TargetVitalsRef(bar) != monster.MyVitals) return;
        var scale = new Vector3(monster.MyVitals.GetHealthPct(), 1f, 1f);
        bar.hpBarFill.localScale = scale;
        bar.poisonBarFill.localScale = scale;
    }

    private Monster SpawnMirror(MonsterState state)
    {
        var data = SingletonBehaviour<FateDB>.Instance.allMonsters.FirstOrDefault(m => m != null && m.displayName == state.Key);
        if (data == null || data.modelPrefab == null) throw new InvalidOperationException("Monster model is unavailable: " + state.Key);
        if (!NavMesh.SamplePosition(state.Position, out var hit, 3f, NavMesh.AllAreas)) throw new InvalidOperationException("Monster position has no navigation surface.");
        var obj = UnityEngine.Object.Instantiate(data.modelPrefab, hit.position, Quaternion.Euler(0, state.Yaw, 0));
        var monster = obj.GetComponent<Monster>();
        if (monster == null) { UnityEngine.Object.Destroy(obj); throw new InvalidOperationException("Monster prefab is invalid: " + state.Key); }
        monster.monsterData = data;
        monster.Init();
        monster.SetPositionProper(hit.position);
        LevelGenerator.currentInstance.AddMonsterToActiveMonsters(monster);
        RegisterMirror(state.Id, monster);
        return monster;
    }

    private void UpdateMirrors()
    {
        if (OwnsWorld) return;
        foreach (var entry in monsterStates)
        {
            if (!monsters.TryGetValue(entry.Key, out var monster) || monster == null || monster.MyVitals.IsDead) continue;
            var state = entry.Value;
            var previous = monster.transform.position;
            var next = Vector3.Distance(previous, state.Position) > 4f ? state.Position : Vector3.Lerp(previous, state.Position, 1f - Mathf.Exp(-15f * Time.unscaledDeltaTime));
            var agent = monster.GetComponent<NavMeshAgent>();
            if (agent != null && agent.enabled && agent.isOnNavMesh) agent.Warp(next);
            else monster.transform.position = next;
            monster.transform.rotation = Quaternion.Slerp(monster.transform.rotation, Quaternion.Euler(0, state.Yaw, 0), 1f - Mathf.Exp(-20f * Time.unscaledDeltaTime));
        }
    }

    private static void AfterMonsterUpdate(Monster __instance)
    {
        var plugin = Active;
        if (plugin == null || plugin.OwnsWorld || __instance.MyVitals.IsDead || !plugin.monsterIds.TryGetValue(__instance, out var id) || !plugin.monsterStates.TryGetValue(id, out var state)) return;
        var animator = __instance.GetComponent<Animator>();
        if (animator == null) return;
        var attacking = (state.Flags & MonsterState.Attacking) != 0;
        if (attacking && plugin.attackingMirrors.Add(id))
        {
            __instance.attackTimer = 0f;
            __instance.SelectAttack();
        }
        else if (!attacking)
        {
            plugin.attackingMirrors.Remove(id);
            animator.speed = 1f;
        }
        var walking = (state.Flags & (MonsterState.Walking | MonsterState.Running)) != 0 || (__instance.transform.position - state.Position).sqrMagnitude > 0.04f;
        animator.SetBool("IsWalking", walking && !attacking);
        animator.SetBool("IsRunning", walking && !attacking && (state.Flags & MonsterState.Running) != 0);
        animator.SetBool("IsAttacking", attacking);
    }

    private void ApplyHit(Partner partner, string body)
    {
        if (!OwnsWorld) return;
        var parts = body.Split('|');
        if (parts.Length != 4) throw new InvalidOperationException("Invalid hit.");
        var value = ParseInt(parts[1]);
        var type = ParseInt(parts[2]);
        if (value <= 0 || value > 1000000 || !Enum.IsDefined(typeof(DamageType), type)) throw new InvalidOperationException("Invalid hit values.");
        if (!monsters.TryGetValue(ParseInt(parts[0]), out var monster) || monster == null || monster.MyVitals.IsDead || partner.Actor == null) return;
        monster.MyVitals.ApplyDamage(new CharacterVitals.Damage { value = value, damageType = (DamageType)type, isCritical = parts[3] == "1", source = partner.Actor.MyVitals });
        hitsApplied++;
    }

    private void TakeStrike(string body)
    {
        if (OwnsWorld) return;
        var player = Player.PlayerChar;
        if (!monsters.TryGetValue(ParseInt(body), out var monster) || monster == null || monster.MyVitals.IsDead || player == null || !player.Alive()) return;
        monster.attackTimer = 0f;
        monster.SelectAttack();
        if (CurrentAttackField.GetValue(monster) == null) return;
        applyingStrike = true;
        try { RollAttackMethod.Invoke(monster, new object[] { player }); }
        finally { applyingStrike = false; }
        strikesTaken++;
    }

    private void TakeHurt(Partner partner, string body)
    {
        if (partner.Id != worldOwner) return;
        var parts = body.Split('|');
        if (parts.Length != 4) throw new InvalidOperationException("Invalid damage.");
        var value = ParseInt(parts[1]);
        var type = ParseInt(parts[2]);
        if (value <= 0 || value > 1000000 || !Enum.IsDefined(typeof(DamageType), type)) throw new InvalidOperationException("Invalid damage values.");
        var player = Player.PlayerChar;
        if (player == null || !player.Alive()) return;
        monsters.TryGetValue(ParseInt(parts[0]), out var source);
        player.MyVitals.ApplyDamage(new CharacterVitals.Damage { value = value, damageType = (DamageType)type, isCritical = parts[3] == "1", source = source != null ? source.MyVitals : null }, false);
    }

    private void TakeEffect(Partner partner, string body)
    {
        var message = JsonUtility.FromJson<EffectMessage>(body);
        if (message?.effect == null || message.level < 0 || message.level > 1000) throw new InvalidOperationException("Invalid effect.");
        if (message.target != 0)
        {
            if (OwnsWorld && monsters.TryGetValue(message.target, out var monster) && monster != null && !monster.MyVitals.IsDead)
                monster.AddEffect(message.effect, message.level, partner.Actor, -1, message.spell);
        }
        else
        {
            var player = Player.PlayerChar;
            if (player == null || !player.Alive()) return;
            Character? source = message.source > 0 && monsters.TryGetValue(message.source, out var attacker) ? attacker
                : message.source < 0 && partners.TryGetValue(-message.source - 1, out var caster) ? caster.Actor : null;
            player.AddEffect(message.effect, message.level, source, -1, message.spell);
        }
        effectsTaken++;
    }

    private void TakeKill(string body)
    {
        if (OwnsWorld) return;
        var parts = body.Split('|');
        if (parts.Length != 4) throw new InvalidOperationException("Invalid kill.");
        var experience = ParseInt(parts[1]);
        var fame = ParseInt(parts[2]);
        if (experience < 0 || fame < 0) throw new InvalidOperationException("Invalid kill rewards.");
        if (!monsters.TryGetValue(ParseInt(parts[0]), out var monster) || monster == null || monster.MyVitals.IsDead) return;
        var player = Player.PlayerChar;
        if (player != null && player.Alive())
        {
            player.MyVitals.AddExperience(experience);
            player.AwardFame((uint)fame);
            if (ParseInt(parts[3]) == myId) player.KilledEnemy();
        }
        ConfirmDeath(monster);
        killsTaken++;
    }

    private void ConfirmDeath(Monster monster)
    {
        confirmingDeath = true;
        try
        {
            monster.hp = 0f;
            monster.MyVitals.Die();
        }
        finally { confirmingDeath = false; }
    }

    private void PlayReplicaSwing(Partner partner, float speed)
    {
        var actor = partner.Actor;
        if (actor == null || !Finite(speed) || speed <= 0f || speed > 20f) return;
        actor.attackTimer = 0f;
        actor.SelectAttack();
        actor.attackTimer = speed;
        actor.GetComponent<Animator>().SetBool("IsAttacking", true);
        swingsPlayed++;
    }

    private void PlayReplicaCast(Partner partner, string spellName)
    {
        var actor = partner.Actor;
        if (actor == null) return;
        var spell = SingletonBehaviour<FateDB>.Instance.allSpells.FirstOrDefault(s => s != null && s.spellName == spellName);
        if (spell == null) throw new InvalidOperationException("Unknown spell: " + spellName);
        actor.activeSpell = spell;
        PlayReplicaCastGlow(actor, spell);
        actor.Cast();
        castsPlayed++;
    }

    private Monster? NearestEnemy(Vector3 position) => LiveMonsters()
        .Where(m => !m.MyVitals.IsDead && !m.IsPet() && m.GetAlignment() == Alignment.Evil && (OwnsWorld || monsterIds.ContainsKey(m)))
        .OrderBy(m => (m.transform.position - position).sqrMagnitude).FirstOrDefault();
}
