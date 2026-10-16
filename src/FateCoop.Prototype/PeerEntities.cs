using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Steamworks;
using Tableflip;
using UnityEngine;
using UnityEngine.AI;

namespace FateCoop.Prototype;
public sealed partial class Plugin
{
    private Character? remotePet;
    private string remotePetModel = "";
    private const float InterpolationDelay = 0.12f;
    private readonly PoseTrail partnerTrail = new PoseTrail(), petTrail = new PoseTrail();
    private float clockOffset;
    private bool clockSynced;
    private string remoteProfileJson = "", sentProfileKey = "", partnerName = "";
    private float nextProfileCheck, localSwingSpeed;
    private int localSwings, localCasts, remoteSwings = -1, remoteCasts = -1, swingsPlayed, castsPlayed, remoteHp, remoteMaxHp, remoteMana, remoteMaxMana;
    private string localCastSpell = "";
    [Serializable] private sealed class Profile
    {
        public string name = "";
        public PlayerSaveData player = null!;
        public List<ItemSaveData> equipment = new List<ItemSaveData>();
    }
    [Serializable] public sealed class PetSnapshot
    {
        public bool present;
        public string model = "", name = "";
        public Vector3 position;
        public float yaw;
        public int hp, maxHp;
    }
    private void InstallEntityHooks()
    {
        harmony!.Patch(AccessTools.Method(typeof(Pet), "UpdateAI"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforePetAI)));
    }
    private static bool BeforePetAI(Pet __instance) => Active == null || Active.remotePet != __instance;
    private PetSnapshot LocalPetSnapshot()
    {
        var pet = Player.PlayerChar != null ? Player.PlayerChar.GetPet() : null;
        if (pet == null || !pet.gameObject.activeInHierarchy || pet.monsterData == null) return new PetSnapshot();
        return new PetSnapshot { present = true, model = pet.monsterData.displayName, name = pet.CharacterName ?? "Pet", position = pet.transform.position, yaw = pet.transform.eulerAngles.y, hp = pet.HP(), maxHp = pet.MaxHP() };
    }
    private void ApplyPeerFrame(PeerFrame frame)
    {
        if (!FinitePosition(frame.primaryPosition) || !Finite(frame.primaryYaw)) throw new InvalidOperationException("Invalid player pose.");
        if (!NavMesh.SamplePosition(frame.primaryPosition, out var hit, 2f, NavMesh.AllAreas)) throw new InvalidOperationException("Remote player is outside the navigation mesh.");
        if (actor == null)
        {
            if (remoteProfileJson.Length == 0) return;
            Spawn();
        }
        if (!mirroring) StopRoute();
        partnerTrail.Add(frame.time, hit.position, frame.primaryYaw);
        mirroring = true;
        remoteHp = frame.hp; remoteMaxHp = frame.maxHp; remoteMana = frame.mana; remoteMaxMana = frame.maxMana;
        ApplyRemotePet(JsonUtility.FromJson<PetSnapshot>(frame.petJson), frame.time);
        if (remoteSwings >= 0 && frame.swings != remoteSwings) PlayReplicaSwing(frame.swingSpeed);
        if (remoteCasts >= 0 && frame.casts != remoteCasts) PlayReplicaCast(frame.castSpell);
        ShowReplicaDown(frame.hp <= 0 && frame.maxHp > 0);
        remoteSwings = frame.swings;
        remoteCasts = frame.casts;
        PlayReplicaVisuals(frame);
    }
    private static string LocalName() => SteamReady ? SteamFriends.GetPersonaName() : Player.PlayerChar.CharacterName;
    private void SendProfileIfChanged()
    {
        var player = Player.PlayerChar;
        if (Time.unscaledTime < nextProfileCheck || player == null) return;
        nextProfileCheck = Time.unscaledTime + 1f;
        var equipment = player.inventory.Where(item => item != null && Fate.IsItemSlotInEquipmentSlot(item.slotIndex)).Select(item =>
        {
            var data = new ItemSaveData();
            item.FillInstance(data);
            return data;
        }).ToList();
        var name = LocalName();
        var key = name + player.GetActiveWeaponSet + JsonUtility.ToJson(new ItemStorage { items = equipment });
        if (key == sentProfileKey) return;
        var save = new PlayerSaveData();
        player.FillPlayerSaveData(ref save);
        save = save.CreatePartyMemberCopy();
        save.playerName = name;
        save.lineage = 0;
        save.activeWeaponSet = player.GetActiveWeaponSet;
        Send("profile", JsonUtility.ToJson(new Profile { name = name, player = save, equipment = equipment }));
        sentProfileKey = key;
    }
    private void ReceiveProfile(string body)
    {
        var profile = JsonUtility.FromJson<Profile>(body);
        if (profile?.player == null || profile.name.Length == 0 || profile.name.Length > 64 || profile.equipment.Count > 20 || profile.equipment.Any(item => !Fate.IsItemSlotInEquipmentSlot(item.slotIndex)))
            throw new InvalidOperationException("Invalid partner profile.");
        if (remoteProfileJson.Length == 0) Notify(profile.name + " joined your co-op game.");
        remoteProfileJson = body;
        partnerName = profile.name;
        if (actor != null && mirroring) Despawn(preserveIntent: true);
    }
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static bool FinitePosition(Vector3 p) => Finite(p.x) && Finite(p.y) && Finite(p.z) && Math.Abs(p.x) < 2000 && Math.Abs(p.y) < 1000 && Math.Abs(p.z) < 2000;
    private float RenderTime => Time.unscaledTime + clockOffset - InterpolationDelay;

    private void SyncClock(float senderTime)
    {
        var sample = senderTime - Time.unscaledTime;
        clockOffset = !clockSynced ? sample : Mathf.Lerp(clockOffset, sample, sample > clockOffset ? 0.5f : 0.02f);
        clockSynced = true;
    }

    private static void MoveReplica(Character character, Vector3 target, float yaw, bool live)
    {
        var agent = character.GetComponent<NavMeshAgent>();
        if (agent == null || !agent.enabled || !agent.isOnNavMesh) return;
        var previous = character.transform.position;
        agent.Warp(target);
        character.transform.rotation = Quaternion.Slerp(character.transform.rotation, Quaternion.Euler(0, yaw, 0), 1f - Mathf.Exp(-20f * Time.unscaledDeltaTime));
        var animator = character.GetComponent<Animator>();
        if (animator != null) animator.SetBool("IsWalking", live && (target - previous).sqrMagnitude > 0.0001f);
    }

    private void ApplyRemotePet(PetSnapshot pet, float time)
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
        petTrail.Add(time, hit.position, pet.yaw);
    }
    private void UpdateRemoteEntities()
    {
        if (remotePet != null && petTrail.Sample(RenderTime, out var petPosition, out var petYaw, out var petLive)) MoveReplica(remotePet, petPosition, petYaw, petLive);
        UpdateMirrors();
    }
    private void DespawnRemotePet()
    {
        if (remotePet != null) { remotePet.gameObject.SetActive(false); UnityEngine.Object.Destroy(remotePet.gameObject); }
        remotePet = null; remotePetModel = "";
        petTrail.Clear();
    }
    private static void DrawBar(Rect area, int value, int max, Color fill) => DrawFill(area, max > 0 ? (float)value / max : 0f, fill);

    private static void DrawFill(Rect area, float fraction, Color fill)
    {
        GUI.color = Color.black;
        GUI.DrawTexture(area, Texture2D.whiteTexture);
        GUI.color = fill;
        GUI.DrawTexture(new Rect(area.x, area.y, area.width * Mathf.Clamp01(fraction), area.height), Texture2D.whiteTexture);
        GUI.color = Color.white;
    }

    private static void DrawEntityLabel(Vector3 position, string label)
    {
        var p = Camera.main.WorldToScreenPoint(position + Vector3.up * 1.5f);
        if (p.z <= 0) return;
        var width = GUI.skin.box.CalcSize(new GUIContent(label)).x + 16f;
        GUI.Box(new Rect(p.x - width / 2f, Screen.height - p.y - 12, width, 24), label);
    }
}
