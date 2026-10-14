using System;
using System.Linq;
using HarmonyLib;
using Tableflip;
using UnityEngine;
using UnityEngine.AI;

namespace FateCoop.Prototype;
public sealed partial class Plugin
{
    private Character? remotePet;
    private string remotePetModel = "";
    private Vector3 remotePetTarget;
    private float remotePetYaw, remotePetTime;
    [Serializable] public sealed class PetSnapshot
    {
        public bool present;
        public string model = "", name = "";
        public Vector3 position;
        public float yaw;
    }
    private void InstallEntityHooks()
    {
        harmony!.Patch(AccessTools.Method(typeof(Pet), "UpdateAI"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforePetAI)));
        harmony.Patch(AccessTools.Method(typeof(CharacterVitals), "ApplyDamage"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeCombatDamage)));
    }
    private static bool BeforePetAI(Pet __instance) => Active == null || Active.remotePet != __instance;
    private PetSnapshot LocalPetSnapshot()
    {
        var pet = Player.PlayerChar != null ? Player.PlayerChar.GetPet() : null;
        if (pet == null || !pet.gameObject.activeInHierarchy || pet.monsterData == null) return new PetSnapshot();
        return new PetSnapshot { present = true, model = pet.monsterData.displayName, name = pet.CharacterName ?? "Pet", position = pet.transform.position, yaw = pet.transform.eulerAngles.y };
    }
    private void ApplyPeerFrame(PeerFrame frame)
    {
        if (!FinitePosition(frame.primaryPosition) || !Finite(frame.primaryYaw)) throw new InvalidOperationException("Invalid player pose.");
        if (!NavMesh.SamplePosition(frame.primaryPosition, out var hit, 2f, NavMesh.AllAreas)) throw new InvalidOperationException("Remote player is outside the navigation mesh.");
        if (actor == null) Spawn();
        if (!mirroring) StopRoute();
        mirrorTarget = hit.position; mirrorYaw = frame.primaryYaw; lastMirrorTime = Time.unscaledTime; mirroring = true;
        var pet = JsonUtility.FromJson<PetSnapshot>(frame.petJson);
        ApplyRemotePet(pet);
        if (isHost) ApplyRemoteAttack(string.IsNullOrEmpty(frame.attackJson) ? null : JsonUtility.FromJson<AttackIntent>(frame.attackJson), pet);
        else ApplyCombatSnapshot(JsonUtility.FromJson<CombatSnapshot>(frame.combatJson));
    }
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static bool FinitePosition(Vector3 p) => Finite(p.x) && Finite(p.y) && Finite(p.z) && Math.Abs(p.x) < 2000 && Math.Abs(p.y) < 1000 && Math.Abs(p.z) < 2000;
    private void ApplyRemotePet(PetSnapshot pet)
    {
        if (pet == null || !pet.present) { DespawnRemotePet(); return; }
        if (!FinitePosition(pet.position) || !Finite(pet.yaw) || pet.model.Length > 128 || pet.name.Length > 64) throw new InvalidOperationException("Invalid pet state.");
        if (!NavMesh.SamplePosition(pet.position, out var hit, 3f, NavMesh.AllAreas)) { DespawnRemotePet(); return; }
        if (remotePet == null || remotePetModel != pet.model)
        {
            DespawnRemotePet();
            var data = SingletonBehaviour<FateDB>.Instance.allMonsters.FirstOrDefault(m => m != null && m.displayName == pet.model);
            if (data == null || data.modelPrefab == null) throw new InvalidOperationException("Remote pet model is unavailable: " + pet.model);
            var obj = UnityEngine.Object.Instantiate(data.modelPrefab, hit.position, Quaternion.Euler(0, pet.yaw, 0));
            remotePet = obj.GetComponent<Character>();
            if (remotePet == null) { UnityEngine.Object.Destroy(obj); throw new InvalidOperationException("Pet prefab has no Character."); }
            obj.name = "FateCoop_Remote_Pet";
            remotePetModel = pet.model;
            remotePet.SetMasterCharacter(actor);
            remotePet.monsterData = data;
            remotePet.Init();
            remotePet.CharacterName = pet.name;
            remotePet.SetInvincible(true);
            remotePet.allowDropEquipment = false; remotePet.allowDropInventory = false;
            remotePet.SetTarget(null); remotePet.SetAIState(AIState.Inactive); remotePet.StopPathing();
            foreach (var collider in obj.GetComponentsInChildren<Collider>()) collider.enabled = false;
            remotePet.SetPositionProper(hit.position);
            Logger.LogInfo("REMOTE_PET_SPAWNED model=" + pet.model + " localPetCount=" + Player.PlayerChar.GetPetCount());
        }
        remotePetTarget = hit.position; remotePetYaw = pet.yaw; remotePetTime = Time.unscaledTime;
    }
    private void UpdateRemoteEntities()
    {
        if (remotePet != null)
        {
            var previous = remotePet.transform.position;
            var fresh = Time.unscaledTime - remotePetTime < .5f;
            var desired = fresh ? remotePetTarget : previous;
            var next = Vector3.Distance(previous, desired) > 5 ? desired : Vector3.Lerp(previous, desired, 1 - Mathf.Exp(-25 * Time.unscaledDeltaTime));
            var agent = remotePet.GetComponent<NavMeshAgent>();
            if (agent != null && agent.enabled && agent.isOnNavMesh) agent.Warp(next);
            remotePet.transform.rotation = Quaternion.Slerp(remotePet.transform.rotation, Quaternion.Euler(0, remotePetYaw, 0), 1 - Mathf.Exp(-25 * Time.unscaledDeltaTime));
            var animator = remotePet.GetComponent<Animator>();
            if (animator != null) animator.SetBool("IsWalking", fresh && Vector3.Distance(previous, next) > .001f);
        }
        UpdateCombatReplica();
    }
    private void DespawnRemotePet()
    {
        if (remotePet != null) { remotePet.gameObject.SetActive(false); UnityEngine.Object.Destroy(remotePet.gameObject); }
        remotePet = null; remotePetModel = "";
    }
    private static void DrawEntityLabel(Vector3 position, string label)
    {
        var p = Camera.main.WorldToScreenPoint(position + Vector3.up * 1.5f);
        if (p.z > 0) GUI.Box(new Rect(p.x - 140, Screen.height - p.y - 12, 280, 24), label);
    }
}
