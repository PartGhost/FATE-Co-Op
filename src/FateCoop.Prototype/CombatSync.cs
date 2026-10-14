using System;
using System.Collections.Generic;
using System.Linq;
using Tableflip;
using UnityEngine;
using UnityEngine.AI;

namespace FateCoop.Prototype;
public sealed partial class Plugin
{
    private Monster? combatMonster;
    private string combatId = "";
    private CombatSnapshot replicaCombat = new CombatSnapshot();
    private bool applyingCombatDamage, replicaDeathApplied;
    private int hitsA, hitsB, petHits, combatDeaths, attackRejections;
    private long attackSequence, lastRemoteAttack;
    private AttackIntent? pendingAttack;
    private readonly Dictionary<string, float> nextHit = new Dictionary<string, float>();
    [Serializable] public sealed class AttackIntent
    {
        public long id;
        public string worldKey = "", target = "", source = "player";
    }
    [Serializable] public sealed class CombatSnapshot
    {
        public bool present, dead, walking, attacking;
        public string id = "", model = "";
        public Vector3 position;
        public float yaw, hp, maxHp;
        public int hitsA, hitsB, petHits, deaths, rejected;
        public long acknowledgedAttack;
    }
    private static bool BeforeCombatDamage(CharacterVitals __instance, CharacterVitals.Damage data)
    {
        var plugin = Active;
        if (plugin == null || !plugin.movementSession) return true;
        if (__instance.Char == plugin.combatMonster)
        {
            if (plugin.applyingCombatDamage) return true;
            var source = data.source != null ? data.source.Char : null;
            if (data.value > 0 && source != null && (source == Player.PlayerChar || (source.IsPet() && source.Master() == Player.PlayerChar)))
                plugin.RequestTestAttack(source.IsPet() ? "pet" : "player");
            return false;
        }
        // Keep the fixture's players, pets and unrelated monsters safe while testing one shared enemy.
        return false;
    }
    private void StartCombat()
    {
        if (instance != "a" || !movementSession || !SharedWorldReady() || SceneLoad.CurrentScene != Tableflip.Scene.Dungeon)
            throw new InvalidOperationException("Start the shared monster test from host A's connected dungeon.");
        ResetCombat();
        var source = UnityEngine.Object.FindObjectsOfType<Monster>().Where(m => !m.IsPet() && m != remotePet && m.monsterData != null && !m.IsBoss && !m.isUnique && m.GetAlignment() == Alignment.Evil)
            .OrderBy(m => Vector3.Distance(m.transform.position, Player.PlayerChar.transform.position)).FirstOrDefault();
        if (source == null) throw new InvalidOperationException("No ordinary monster model is available on this floor.");
        if (!NavMesh.SamplePosition(Player.PlayerChar.transform.position + Player.PlayerChar.transform.forward * 3f, out var hit, 5f, NavMesh.AllAreas))
            throw new InvalidOperationException("No nearby navigation surface for the test monster.");
        combatMonster = SpawnCombatModel(source.monsterData.displayName, hit.position);
        combatId = Guid.NewGuid().ToString("N");
        combatMonster.SetInvincible(false);
        combatMonster.SetTarget(Player.PlayerChar);
        combatMonster.SetAIState(AIState.Hunt);
        SetStatus("Shared monster ready. Click it normally in either game. Prototype hits deal 10 damage.");
        Logger.LogInfo("SHARED_MONSTER_STARTED " + combatId);
    }
    private Monster SpawnCombatModel(string key, Vector3 position)
    {
        var data = SingletonBehaviour<FateDB>.Instance.allMonsters.FirstOrDefault(m => m != null && m.displayName == key);
        if (data == null || data.modelPrefab == null) throw new InvalidOperationException("Shared monster model unavailable.");
        var obj = UnityEngine.Object.Instantiate(data.modelPrefab, position, Quaternion.identity);
        var monster = obj.GetComponent<Monster>();
        if (monster == null) { UnityEngine.Object.Destroy(obj); throw new InvalidOperationException("Shared monster prefab is invalid."); }
        obj.name = "FateCoop_Shared_Test_Monster";
        monster.monsterData = data;
        monster.Init();
        monster.maxHpBase = 80; monster.hp = 80;
        monster.allowDropInventory = false; monster.allowDropEquipment = false;
        monster.SetPositionProper(position);
        if (instance == "b") monster.UnRegisterKilled();
        return monster;
    }
    private void RequestTestAttack(string source)
    {
        if (!movementSession || !SharedWorldReady() || combatMonster == null || combatId.Length == 0) throw new InvalidOperationException("No shared test monster is ready.");
        if (source != "player" && source != "pet") throw new InvalidOperationException("Attack source must be player or pet.");
        if (instance == "a")
        {
            var attacker = source == "pet" ? Player.PlayerChar.GetPet() : Player.PlayerChar;
            ConfirmHit("a", source, attacker != null ? attacker.transform.position : Vector3.one * 9999);
        }
        else if (pendingAttack == null && !replicaCombat.dead)
            pendingAttack = new AttackIntent { id = ++attackSequence, worldKey = CurrentWorldKey(), target = combatId, source = source };
    }
    private void ApplyRemoteAttack(AttackIntent? attack, PetSnapshot pet)
    {
        if (attack == null || attack.id <= lastRemoteAttack) return;
        lastRemoteAttack = attack.id;
        if (attack.worldKey != CurrentWorldKey() || attack.target != combatId || (attack.source != "player" && attack.source != "pet")) { attackRejections++; return; }
        var position = attack.source == "pet" ? (pet.present ? pet.position : Vector3.one * 9999) : mirrorTarget;
        ConfirmHit("b", attack.source, position);
    }
    private void ConfirmHit(string owner, string source, Vector3 position)
    {
        var key = owner + source;
        if (combatMonster == null || combatMonster.MyVitals.IsDead || !FinitePosition(position) || Vector3.Distance(position, combatMonster.transform.position) > 8f
            || (nextHit.TryGetValue(key, out var ready) && Time.unscaledTime < ready)) { attackRejections++; return; }
        nextHit[key] = Time.unscaledTime + .35f;
        applyingCombatDamage = true;
        try
        {
            // Fixed host-computed fixture damage. Character stats/spells and XP distribution follow later.
            combatMonster.MyVitals.ApplyDamage(new CharacterVitals.Damage { value = 10 }, false, true);
            if (owner == "a") hitsA++; else hitsB++;
            if (source == "pet") petHits++;
            if (combatMonster.MyVitals.IsDead) combatDeaths = 1;
        }
        finally { applyingCombatDamage = false; }
    }
    private CombatSnapshot CombatState()
    {
        if (instance == "b") return replicaCombat;
        if (combatMonster == null) return new CombatSnapshot();
        var animator = combatMonster.GetComponent<Animator>();
        return new CombatSnapshot { present = true, id = combatId, model = combatMonster.monsterData.displayName, position = combatMonster.transform.position,
            yaw = combatMonster.transform.eulerAngles.y, hp = combatMonster.hp, maxHp = combatMonster.MaxHP(), dead = combatMonster.MyVitals.IsDead,
            walking = animator != null && animator.GetBool("IsWalking"), attacking = animator != null && animator.GetBool("IsAttacking"),
            hitsA = hitsA, hitsB = hitsB, petHits = petHits, deaths = combatDeaths, rejected = attackRejections, acknowledgedAttack = lastRemoteAttack };
    }
    private void ApplyCombatSnapshot(CombatSnapshot snapshot)
    {
        if (!snapshot.present) { ResetCombat(); return; }
        if (!FinitePosition(snapshot.position) || !Finite(snapshot.hp) || !Finite(snapshot.maxHp) || snapshot.id.Length != 32 || snapshot.hp < 0 || snapshot.hp > snapshot.maxHp || snapshot.maxHp > 10000)
            throw new InvalidOperationException("Invalid shared monster snapshot.");
        if (combatMonster == null || combatId != snapshot.id)
        {
            ResetCombat(); combatId = snapshot.id;
            combatMonster = SpawnCombatModel(snapshot.model, snapshot.position);
            combatMonster.SetInvincible(true); combatMonster.SetAIState(AIState.Inactive);
        }
        replicaCombat = snapshot;
        if (pendingAttack != null && snapshot.acknowledgedAttack >= pendingAttack.id) pendingAttack = null;
    }
    private void UpdateCombatReplica()
    {
        if (instance != "b" || combatMonster == null || !replicaCombat.present) return;
        var agent = combatMonster.GetComponent<NavMeshAgent>();
        if (agent != null && agent.enabled && agent.isOnNavMesh) agent.Warp(replicaCombat.position);
        combatMonster.transform.rotation = Quaternion.Euler(0, replicaCombat.yaw, 0);
        combatMonster.maxHpBase = (int)replicaCombat.maxHp;
        combatMonster.hp = replicaCombat.hp;
        var animator = combatMonster.GetComponent<Animator>();
        if (animator != null)
        {
            animator.SetBool("IsWalking", replicaCombat.walking); animator.SetBool("IsAttacking", replicaCombat.attacking);
            animator.SetBool("IsDead", replicaCombat.dead);
        }
        if (replicaCombat.dead && !replicaDeathApplied)
        {
            replicaDeathApplied = true;
            combatMonster.StopPathing(); combatMonster.SetAIState(AIState.Dead); combatMonster.MyVitals.Die();
            if (agent != null) agent.enabled = false;
            Logger.LogInfo("SHARED_MONSTER_DEATH_APPLIED " + combatId);
        }
    }
    private void ResetCombat()
    {
        if (combatMonster != null) { combatMonster.gameObject.SetActive(false); UnityEngine.Object.Destroy(combatMonster.gameObject); }
        combatMonster = null; combatId = ""; pendingAttack = null; replicaCombat = new CombatSnapshot(); replicaDeathApplied = false;
        hitsA = hitsB = petHits = combatDeaths = attackRejections = 0; nextHit.Clear();
    }
}
