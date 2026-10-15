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
    private readonly Dictionary<int, Monster> monsters = new Dictionary<int, Monster>();
    private readonly Dictionary<Monster, int> monsterIds = new Dictionary<Monster, int>();
    private readonly Dictionary<int, MonsterState> monsterStates = new Dictionary<int, MonsterState>();
    private readonly HashSet<int> attackingMirrors = new HashSet<int>();
    private int nextMonsterId = 1;
    private float nextMonsterSync;
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
        internal string Key = "";
        internal Vector3 Position;
        internal float Yaw, Hp, MaxHp;
        internal bool Gone;

        internal static MonsterState Of(int id, Monster monster)
        {
            var animator = monster.GetComponent<Animator>();
            var flags = monster.MyVitals.IsDead ? Dead : 0;
            if (animator != null)
                flags |= (animator.GetBool("IsWalking") ? Walking : 0) | (animator.GetBool("IsRunning") ? Running : 0) | (animator.GetBool("IsAttacking") ? Attacking : 0);
            return new MonsterState { Id = id, Key = monster.monsterData.displayName, Position = monster.transform.position, Yaw = monster.transform.eulerAngles.y, Hp = monster.hp, MaxHp = monster.MaxHP(), Flags = flags };
        }

        internal bool Differs(MonsterState other) => Flags != other.Flags || Math.Abs(Hp - other.Hp) > 0.01f || Math.Abs(MaxHp - other.MaxHp) > 0.01f
            || (Position - other.Position).sqrMagnitude > 0.0025f || Math.Abs(Mathf.DeltaAngle(Yaw, other.Yaw)) > 2f;

        internal string Encode() => string.Join("|", Id.ToString(CultureInfo.InvariantCulture), Key, Number(Position.x), Number(Position.y), Number(Position.z), Number(Yaw), Number(Hp), Number(MaxHp), Flags.ToString(CultureInfo.InvariantCulture));

        internal static MonsterState Decode(string line)
        {
            var parts = line.Split('|');
            if (parts.Length == 2 && parts[1] == "-") return new MonsterState { Id = ParseInt(parts[0]), Gone = true };
            if (parts.Length != 9 || parts[1].Length == 0 || parts[1].Length > 128) throw new InvalidOperationException("Invalid monster state.");
            var state = new MonsterState
            {
                Id = ParseInt(parts[0]), Key = parts[1], Position = new Vector3(ParseFloat(parts[2]), ParseFloat(parts[3]), ParseFloat(parts[4])),
                Yaw = ParseFloat(parts[5]), Hp = ParseFloat(parts[6]), MaxHp = ParseFloat(parts[7]), Flags = ParseInt(parts[8])
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
        harmony.Patch(AccessTools.Method(typeof(Character), "AddEffect", new[] { typeof(EffectData), typeof(int), typeof(Character), typeof(int), typeof(bool) }), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeAddEffect)));    }

    private static bool BeforeDamage(CharacterVitals __instance, CharacterVitals.Damage data)
    {
        var plugin = Active;
        if (plugin == null || !plugin.sharedSession) return true;
        var target = __instance.Char;
        if (plugin.isHost)
        {
            if (target == plugin.actor && data.value > 0 && (data.source == null || data.source.Alignment != __instance.Alignment) && plugin.PeerInWorld())
            {
                var sourceId = data.source != null && data.source.Char is Monster attacker && plugin.monsterIds.TryGetValue(attacker, out var attackerId) ? attackerId : 0;
                plugin.Send("hurt", string.Join("|", sourceId, data.value, (int)data.damageType, data.isCritical ? 1 : 0));
            }
            if (target is Monster monster && !monster.IsPet() && data.value > 0 && plugin.PeerInWorld())
                data.value = Math.Max(1, Mathf.RoundToInt(data.value / plugin.monsterToughness));
            return target != plugin.actor && target != plugin.remotePet;
        }
        if (!(target is Monster mirror) || !plugin.monsterIds.TryGetValue(mirror, out var id)) return true;
        var source = data.source != null ? data.source.Char : null;
        if (data.value > 0 && source != null && (source == Player.PlayerChar || (source.HasMaster() && source.Master() == Player.PlayerChar)))
        {
            plugin.Send("hit", string.Join("|", id, data.value, (int)data.damageType, data.isCritical ? 1 : 0));
            plugin.hitsSent++;
        }
        return false;
    }

    private static bool BeforeAddEffect(Character __instance, EffectData effect, int sourceLevel, Character source, int itemSlotIndex, bool isSpellEffect, ref AppliedEffect __result)
    {
        var plugin = Active;
        if (plugin == null || !plugin.sharedSession) return true;
        var mirrorId = 0;
        var proxy = __instance == plugin.actor || __instance == plugin.remotePet || (!plugin.isHost && __instance is Monster mirror && plugin.monsterIds.TryGetValue(mirror, out mirrorId));
        if (!proxy) return true;
        if (__instance != plugin.remotePet && source != null && source != plugin.actor && plugin.peerConnected)
        {
            var local = source == Player.PlayerChar || (source.HasMaster() && source.Master() == Player.PlayerChar);
            var sourceId = source is Monster attacker && plugin.monsterIds.TryGetValue(attacker, out var attackerId) ? attackerId : 0;
            if (plugin.isHost || local)
            {
                plugin.Send("effect", JsonUtility.ToJson(new EffectMessage { target = mirrorId, source = plugin.isHost ? sourceId : 0, level = sourceLevel, spell = isSpellEffect, effect = effect }));
                plugin.effectsSent++;
            }
        }
        __result = new AppliedEffect(effect, sourceLevel, source, itemSlotIndex, isSpellEffect);
        return false;
    }

    private static bool BeforeRollAttack(Character __instance, Character target, ref bool __result)
    {
        var plugin = Active;
        if (plugin == null || !plugin.sharedSession) return true;
        if (plugin.isHost)
        {
            if (target != plugin.actor && target != plugin.remotePet) return true;
            if (target == plugin.actor && __instance is Monster monster && plugin.monsterIds.TryGetValue(monster, out var id) && plugin.PeerInWorld())
            {
                plugin.Send("strike", id.ToString(CultureInfo.InvariantCulture));
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
        if (plugin == null || !plugin.isHost || !plugin.PeerInWorld() || !(__instance.Char is Monster monster) || !plugin.monsterIds.TryGetValue(monster, out var id)) return;
        var byGuest = finalData?.source != null && finalData.source.Char == plugin.actor;
        plugin.Send("kill", string.Join("|", id, monster.GetExperienceReward(), monster.GetFameReward(), byGuest ? 1 : 0));
        plugin.killsSent++;
    }

    private static bool BeforeMonsterDie(Monster __instance)
    {
        var plugin = Active;
        return plugin == null || plugin.isHost || plugin.confirmingDeath || !plugin.monsterIds.ContainsKey(__instance);
    }

    private static void AfterDistanceToPlayer(Character __instance, ref float __result)
    {
        var plugin = Active;
        if (plugin?.actor == null || !plugin.isHost) return;
        __result = Math.Min(__result, (plugin.actor.transform.position - __instance.transform.position).sqrMagnitude);
    }

    private static void AfterFindCharacter(LevelGenerator __instance, Transform transformation, Alignment alignment, float motionRadius, float coneRadius, float radius, ref Character __result)
    {
        var plugin = Active;
        if (__result != null || plugin == null || !plugin.isHost || !plugin.sharedSession || plugin.actor == null) return;
        var mask = ~((1 << LayerMask.NameToLayer("Player")) | (1 << LayerMask.NameToLayer("Player Damagable")));
        if (LineOfSight(__instance, plugin.actor, alignment, transformation, motionRadius, coneRadius, radius, mask)) __result = plugin.actor;
    }

    private static void AfterLocalSwing(Player __instance)
    {
        var plugin = Active;
        if (plugin == null || __instance != Player.PlayerChar) return;
        plugin.localSwings++;
        plugin.localSwingSpeed = __instance.attackTimer;
    }

    private static void AfterLocalCast(Character __instance)
    {
        var plugin = Active;
        if (plugin == null || __instance != Player.PlayerChar || __instance.activeSpell == null) return;
        plugin.localCasts++;
        plugin.localCastSpell = __instance.activeSpell.spellName;
    }

    private static bool BeforeReplicaAttackEnd(PartyMember __instance)
    {
        if (Active == null || __instance != Active.actor) return true;
        var animator = __instance.GetComponent<Animator>();
        animator.SetBool("IsAttacking", false);
        animator.speed = 1f;
        __instance.attackTimer = 0f;
        CastTimerSetter.Invoke(__instance, new object[] { 0f });
        CurrentAttackField.SetValue(__instance, null);
        return false;
    }

    private bool InHostWorld() => !isHost && peerConnected && remoteFrame != null && remoteFrame.worldKey.Length > 0 && remoteFrame.worldKey == CurrentWorldKey();

    private bool PeerInWorld() => peerConnected && remoteFrame != null && remoteFrame.worldKey.Length > 0 && remoteFrame.worldKey == CurrentWorldKey() && SharedWorldReady();

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
        var inWorld = PeerInWorld();
        if (!inWorld) monsterStates.Clear();
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
        if (lines.Count > 0) Send("monsters", string.Join(";", lines));
    }

    private void ReceiveMonsters(string body)
    {
        if (isHost) throw new InvalidOperationException("Only the host sends monsters.");
        if (SceneLoad.CurrentScene != Tableflip.Scene.Dungeon) return;
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
            if ((state.Flags & MonsterState.Dead) != 0 && !monster.MyVitals.IsDead) ConfirmDeath(monster);
        }
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
        RegisterMirror(state.Id, monster);
        return monster;
    }

    private void UpdateMirrors()
    {
        if (isHost) return;
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
            var animator = monster.GetComponent<Animator>();
            if (animator == null) continue;
            var attacking = (state.Flags & MonsterState.Attacking) != 0;
            if (attacking && attackingMirrors.Add(entry.Key))
            {
                monster.attackTimer = 0f;
                monster.SelectAttack();
            }
            else if (!attacking) attackingMirrors.Remove(entry.Key);
            animator.SetBool("IsWalking", (state.Flags & MonsterState.Walking) != 0);
            animator.SetBool("IsRunning", (state.Flags & MonsterState.Running) != 0);
            animator.SetBool("IsAttacking", attacking);
        }
    }

    private void ApplyHit(string body)
    {
        if (!isHost) throw new InvalidOperationException("Only the host applies hits.");
        var parts = body.Split('|');
        if (parts.Length != 4) throw new InvalidOperationException("Invalid hit.");
        var value = ParseInt(parts[1]);
        var type = ParseInt(parts[2]);
        if (value <= 0 || value > 1000000 || !Enum.IsDefined(typeof(DamageType), type)) throw new InvalidOperationException("Invalid hit values.");
        if (!monsters.TryGetValue(ParseInt(parts[0]), out var monster) || monster == null || monster.MyVitals.IsDead || actor == null) return;
        monster.MyVitals.ApplyDamage(new CharacterVitals.Damage { value = value, damageType = (DamageType)type, isCritical = parts[3] == "1", source = actor.MyVitals });
        hitsApplied++;
    }

    private void TakeStrike(string body)
    {
        if (isHost) throw new InvalidOperationException("Only the guest takes forwarded strikes.");
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

    private void TakeHurt(string body)
    {
        if (isHost) throw new InvalidOperationException("Only the guest takes forwarded damage.");
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

    private void TakeEffect(string body)
    {
        var message = JsonUtility.FromJson<EffectMessage>(body);
        if (message?.effect == null || message.level < 0 || message.level > 1000) throw new InvalidOperationException("Invalid effect.");
        if (message.target != 0)
        {
            if (!isHost) throw new InvalidOperationException("Only the host applies effects to monsters.");
            if (monsters.TryGetValue(message.target, out var monster) && monster != null && !monster.MyVitals.IsDead)
                monster.AddEffect(message.effect, message.level, actor, -1, message.spell);
        }
        else
        {
            var player = Player.PlayerChar;
            if (player == null || !player.Alive()) return;
            Character? source = message.source != 0 && monsters.TryGetValue(message.source, out var attacker) ? attacker : actor;
            player.AddEffect(message.effect, message.level, source, -1, message.spell);
        }
        effectsTaken++;
    }

    private void TakeKill(string body)
    {
        if (isHost) throw new InvalidOperationException("Only the guest takes forwarded kills.");
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
            if (parts[3] == "1") player.KilledEnemy();
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

    private void PlayReplicaSwing(float speed)
    {
        if (actor == null || !Finite(speed) || speed <= 0f || speed > 20f) return;
        actor.attackTimer = 0f;
        actor.SelectAttack();
        actor.attackTimer = speed;
        actor.GetComponent<Animator>().SetBool("IsAttacking", true);
        swingsPlayed++;
    }

    private void PlayReplicaCast(string spellName)
    {
        if (actor == null) return;
        var spell = SingletonBehaviour<FateDB>.Instance.allSpells.FirstOrDefault(s => s != null && s.spellName == spellName);
        if (spell == null) throw new InvalidOperationException("Unknown spell: " + spellName);
        actor.activeSpell = spell;
        actor.Cast();
        castsPlayed++;
    }

    private Monster? NearestEnemy(Vector3 position) => LiveMonsters()
        .Where(m => !m.MyVitals.IsDead && !m.IsPet() && m.GetAlignment() == Alignment.Evil && (isHost || monsterIds.ContainsKey(m)))
        .OrderBy(m => (m.transform.position - position).sqrMagnitude).FirstOrDefault();
}
