using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Steamworks;
using Tableflip;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

namespace FateCoop.Prototype;

public sealed partial class Plugin
{
    private const float InterpolationDelay = 0.12f;
    private const string PlayerMarker = "P_Marker_Player", PartnerMapIcon = "PlayerIcon";
    private static readonly AccessTools.FieldRef<Player, GameObject> PetMapIconRef = AccessTools.FieldRefAccess<Player, GameObject>("petMinimapIcon");
    private const int MaxSummons = 16;
    private static readonly AccessTools.FieldRef<Character, List<Character>> PetsRef = AccessTools.FieldRefAccess<Character, List<Character>>("petCharacters");
    private static readonly AccessTools.FieldRef<Character, AIState> AIStateRef = AccessTools.FieldRefAccess<Character, AIState>("eAIState");
    private string sentProfileKey = "", localCastSpell = "";
    private float nextProfileCheck, localSwingSpeed;
    private int localSwings, localCasts, spawnCount, suppressedAI, swingsPlayed, castsPlayed;

    private sealed class Partner
    {
        internal readonly int Id;
        internal string Name = "", ProfileJson = "";
        internal string? SlotPetPortrait;
        internal PeerFrame? Frame;
        internal float LastMessage, ClockOffset;
        internal bool ClockSynced, Down;
        internal PartyMember? Actor;
        internal readonly Companion Pet = new Companion();
        internal readonly List<Companion> Summons = new List<Companion>();
        internal readonly PoseTrail Trail = new PoseTrail();
        internal int Swings = -1, Casts = -1, Arrows = -1, LevelUps = -1, Spells = -1, Fishing;
        internal FishingPole? Pole;
        internal LineRenderer? ArrowLine;
        internal GameObject? Portal, TownPortal, Plate;
        internal RectTransform? HpFill, ManaFill, Arrow;
        internal SpriteRenderer? MapIcon;
        internal PartyCharacterUI? Slot, PetSlot;
        internal Texture? Portrait;

        internal Partner(int id) { Id = id; }

        internal float RenderTime => Time.unscaledTime + ClockOffset - InterpolationDelay;

        internal bool InWorld(string key) => key.Length > 0 && Frame != null && Frame.worldKey == key;

        internal void SyncClock(float senderTime)
        {
            var sample = senderTime - Time.unscaledTime;
            ClockOffset = !ClockSynced ? sample : Mathf.Lerp(ClockOffset, sample, sample > ClockOffset ? 0.5f : 0.02f);
            ClockSynced = true;
        }

        internal void DestroyHud()
        {
            if (Slot != null) Object.Destroy(Slot.gameObject);
            if (PetSlot != null) Object.Destroy(PetSlot.gameObject);
            if (Arrow != null) Object.Destroy(Arrow.gameObject);
            Slot = PetSlot = null;
            Arrow = null;
        }

        internal void DespawnCompanions()
        {
            Pet.Despawn();
            foreach (var summon in Summons) summon.Despawn();
            Summons.Clear();
        }
    }

    private sealed class Companion
    {
        internal Character? Copy;
        internal string Model = "";
        internal readonly PoseTrail Trail = new PoseTrail();

        internal void Despawn()
        {
            if (Copy != null)
            {
                Copy.gameObject.SetActive(false);
                Object.Destroy(Copy.gameObject);
            }
            Copy = null;
            Model = "";
            Trail.Clear();
        }
    }

    [Serializable] private sealed class Profile
    {
        public string name = "";
        public PlayerSaveData player = null!;
        public List<ItemSaveData> equipment = new List<ItemSaveData>();
    }

    [Serializable] public sealed class PetSnapshot
    {
        public bool present;
        public string model = "", name = "", portrait = "";
        public Vector3 position;
        public float yaw;
        public int hp, maxHp;
    }

    private void InstallEntityHooks()
    {
        harmony!.Patch(AccessTools.Method(typeof(Pet), "UpdateAI"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforePetAI)));
        harmony.Patch(AccessTools.Method(typeof(Monster), "UpdateAI"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforePetAI)));
        harmony.Patch(AccessTools.Method(typeof(PartyMember), "UpdateAI"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforePartyAI)));
        harmony.Patch(AccessTools.Method(typeof(PartyMember), "RecoverFromPassOut"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforePartyAI)));
    }

    private static bool BeforePetAI(Monster __instance) => Active == null || Active.PartnerOfPet(__instance) == null;

    private static bool BeforePartyAI(PartyMember __instance)
    {
        if (Active == null || Active.PartnerOf(__instance) == null) return true;
        Active.suppressedAI++;
        return false;
    }

    private Partner? PartnerOf(Character? character) => character == null ? null : partners.Values.FirstOrDefault(p => p.Actor == character);

    private Partner? PartnerOfPet(Character? character) => character == null ? null : partners.Values.FirstOrDefault(p => p.Pet.Copy == character || p.Summons.Any(s => s.Copy == character));

    private bool IsStandIn(Character? character) => PartnerOf(character) != null || PartnerOfPet(character) != null;

    private static bool LocalRunning() => Player.PlayerChar != null && Player.PlayerChar.Running();

    private static IEnumerable<Character> LocalCompanions() =>
        Player.PlayerChar == null ? Enumerable.Empty<Character>() : PetsRef(Player.PlayerChar).Where(c => c != null && c.gameObject.activeInHierarchy && c.monsterData != null);

    private static PetSnapshot Snapshot(Character pet) =>
        new PetSnapshot
        {
            present = true, model = pet.monsterData.displayName, name = pet.CharacterName ?? "Pet", portrait = pet is Pet { profilePicture: { } picture } ? picture.name : "",
            position = pet.transform.position, yaw = pet.transform.eulerAngles.y, hp = pet.HP(), maxHp = pet.MaxHP()
        };

    private static PetSnapshot LocalPetSnapshot() => LocalCompanions().FirstOrDefault(c => c is Pet) is Character pet ? Snapshot(pet) : new PetSnapshot();

    private static List<PetSnapshot> LocalSummonSnapshots() => LocalCompanions().Where(c => !(c is Pet)).Select(Snapshot).ToList();

    private void ApplyPeerFrame(Partner partner, PeerFrame frame)
    {
        if (!FinitePosition(frame.primaryPosition) || !Finite(frame.primaryYaw)) throw new InvalidOperationException("Invalid player pose.");
        if (!NavMesh.SamplePosition(frame.primaryPosition, out var hit, 2f, NavMesh.AllAreas)) throw new InvalidOperationException("Partner is outside the navigation mesh.");
        if (partner.Actor == null)
        {
            if (partner.ProfileJson.Length == 0 || transitioning) return;
            Spawn(partner, hit.position);
            if (partner.Actor == null) return;
        }
        partner.Trail.Add(frame.time, hit.position, frame.primaryYaw);
        var swung = partner.Swings >= 0 && frame.swings != partner.Swings;
        var cast = partner.Casts >= 0 && frame.casts != partner.Casts;
        partner.Swings = frame.swings;
        partner.Casts = frame.casts;
        ShowReplicaDown(partner, PartnerDown(partner));
        PlayReplicaVisuals(partner, frame);
        if (swung) PlayReplicaSwing(partner, frame.swingSpeed);
        if (cast) PlayReplicaCast(partner, frame.castSpell);
        ApplyCompanion(partner, partner.Pet, JsonUtility.FromJson<PetSnapshot>(frame.petJson), frame.time);
        ApplySummons(partner, frame.summonJson.Select(JsonUtility.FromJson<PetSnapshot>).ToList(), frame.time);
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
        var save = new PlayerSaveData();
        player.FillPlayerSaveData(ref save);
        var key = name + player.GetActiveWeaponSet + player.Level + JsonUtility.ToJson(new ItemStorage { items = equipment }) + string.Join(",", save.attributes.Select(JsonUtility.ToJson));
        if (key == sentProfileKey) return;
        save = save.CreatePartyMemberCopy();
        save.playerName = name;
        save.lineage = 0;
        save.activeWeaponSet = player.GetActiveWeaponSet;
        Send("profile", JsonUtility.ToJson(new Profile { name = name, player = save, equipment = equipment }));
        sentProfileKey = key;
    }

    private void ReceiveProfile(Partner partner, string body)
    {
        var profile = JsonUtility.FromJson<Profile>(body);
        if (profile?.player == null || profile.name.Length == 0 || profile.name.Length > 64 || profile.equipment.Count > 20 || profile.equipment.Any(item => !Fate.IsItemSlotInEquipmentSlot(item.slotIndex)))
            throw new InvalidOperationException("Invalid partner profile.");
        if (partner.ProfileJson.Length == 0) Notify(profile.name + " joined the co-op game.");
        partner.ProfileJson = body;
        partner.Name = profile.name;
        if (partner.Actor != null) Despawn(partner);
    }

    private void Spawn(Partner partner, Vector3 position)
    {
        var player = Player.PlayerChar;
        if (player == null || Fate.CurrentFateVer != Fate.FateVer.Original) return;
        var profile = JsonUtility.FromJson<Profile>(partner.ProfileJson);
        var configuration = profile.player;
        configuration.health = Math.Max(configuration.health, 10f);
        var spawned = Object.Instantiate(SingletonBehaviour<FateDB>.Instance.partyMemberPrefab, position, Quaternion.identity);
        var actor = spawned.GetComponent<PartyMember>();
        spawned.name = "FateCoop_Partner_" + partner.Id;
        partner.Actor = actor;
        actor.SetMasterCharacter(player);
        actor.onProfilePictureChanged += picture => KeepPortrait(partner, picture);
        buildingActor = true;
        try { actor.Init(configuration, new ItemStorage { items = profile.equipment }); }
        finally { buildingActor = false; }
        var customization = spawned.GetComponentInChildren<CharacterCustomizationDefaults>(true);
        if (customization != null)
        {
            customization.SetGender(configuration.male);
            customization.SetRace(configuration.playerRace);
            customization.SetHair(configuration.hairIndex);
            customization.SetHead(configuration.headIndex);
        }
        var marker = player.transform.Find(PlayerMarker);
        if (marker != null) Object.Instantiate(marker.gameObject, actor.transform, false).name = PlayerMarker;
        partner.MapIcon = actor.GetComponentsInChildren<SpriteRenderer>(true).First(r => r.name == PartnerMapIcon);
        partner.MapIcon.sprite = PetMapIconRef((Player)player).GetComponent<SpriteRenderer>().sprite;
        partner.MapIcon.color = PlayerColor(partner.Id);
        actor.SetInvincible();
        actor.SetPositionProper(position);
        actor.SetTarget(null);
        actor.SetAIState(AIState.Inactive);
        actor.StopPathing();
        spawnCount++;
        Logger.LogInfo($"SPAWN_OK partner={partner.Id} actor={actor.GetInstanceID()} position={position}");
    }

    private void Despawn(Partner partner)
    {
        if (partner.Actor != null)
        {
            shownEffects.Remove(partner.Actor);
            partner.Actor.gameObject.SetActive(false);
            Object.Destroy(partner.Actor.gameObject);
            Logger.LogInfo("DESPAWN_OK partner=" + partner.Id);
        }
        partner.DespawnCompanions();
        partner.Actor = null;
        partner.Trail.Clear();
        partner.Down = false;
        partner.Swings = partner.Casts = partner.Arrows = partner.LevelUps = partner.Spells = -1;
        partner.Fishing = 0;
        partner.Pole = null;
        partner.ArrowLine = null;
        partner.MapIcon = null;
    }

    private void DespawnPartners()
    {
        foreach (var partner in partners.Values) Despawn(partner);
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static bool FinitePosition(Vector3 p) => Finite(p.x) && Finite(p.y) && Finite(p.z) && Math.Abs(p.x) < 2000 && Math.Abs(p.y) < 1000 && Math.Abs(p.z) < 2000;

    private static void MoveReplica(Character character, Vector3 target, float yaw, bool live, bool running)
    {
        var agent = character.GetComponent<NavMeshAgent>();
        if (agent == null || !agent.enabled || !agent.isOnNavMesh) return;
        var previous = character.transform.position;
        agent.Warp(target);
        character.transform.rotation = Quaternion.Slerp(character.transform.rotation, Quaternion.Euler(0, yaw, 0), 1f - Mathf.Exp(-20f * Time.unscaledDeltaTime));
        var animator = character.GetComponent<Animator>();
        if (animator == null) return;
        var moving = live && (target - previous).sqrMagnitude > 0.0001f;
        animator.SetBool("IsWalking", moving);
        animator.SetBool("IsRunning", moving && running);
    }

    private void ApplySummons(Partner partner, List<PetSnapshot> summons, float time)
    {
        if (summons.Count > MaxSummons) throw new InvalidOperationException("Too many summons.");
        while (partner.Summons.Count > summons.Count)
        {
            partner.Summons[partner.Summons.Count - 1].Despawn();
            partner.Summons.RemoveAt(partner.Summons.Count - 1);
        }
        while (partner.Summons.Count < summons.Count) partner.Summons.Add(new Companion());
        for (var i = 0; i < summons.Count; i++) ApplyCompanion(partner, partner.Summons[i], summons[i], time);
    }

    private void ApplyCompanion(Partner partner, Companion companion, PetSnapshot pet, float time)
    {
        if (pet == null || !pet.present) { companion.Despawn(); return; }
        if (!FinitePosition(pet.position) || !Finite(pet.yaw) || pet.model.Length > 128 || pet.name.Length > 64) throw new InvalidOperationException("Invalid pet state.");
        if (!NavMesh.SamplePosition(pet.position, out var hit, 3f, NavMesh.AllAreas)) { companion.Despawn(); return; }
        if (companion.Copy == null || companion.Model != pet.model)
        {
            companion.Despawn();
            var data = SingletonBehaviour<FateDB>.Instance.allMonsters.FirstOrDefault(m => m != null && m.displayName == pet.model);
            if (data == null || data.modelPrefab == null) throw new InvalidOperationException("Partner pet model is unavailable: " + pet.model);
            var obj = Object.Instantiate(data.modelPrefab, hit.position, Quaternion.Euler(0, pet.yaw, 0));
            var copy = obj.GetComponent<Character>();
            if (copy == null) { Object.Destroy(obj); throw new InvalidOperationException("Pet prefab has no Character."); }
            obj.name = "FateCoop_Partner_Pet_" + partner.Id;
            companion.Copy = copy;
            companion.Model = pet.model;
            copy.SetMasterCharacter(partner.Actor);
            copy.monsterData = data;
            copy.Init();
            if (copy is Monster monster) monster.RemoveGroundMarker();
            copy.CharacterName = pet.name;
            copy.SetInvincible(true);
            copy.allowDropEquipment = false;
            copy.allowDropInventory = false;
            copy.SetTarget(null);
            copy.SetAIState(AIState.Inactive);
            copy.StopPathing();
            foreach (var collider in obj.GetComponentsInChildren<Collider>()) collider.enabled = false;
            copy.SetPositionProper(hit.position);
        }
        companion.Trail.Add(time, hit.position, pet.yaw);
    }

    private void UpdateRemoteEntities()
    {
        foreach (var partner in partners.Values)
        {
            var running = partner.Frame != null && partner.Frame.running;
            if (partner.Actor != null && !partner.Down && partner.Trail.Sample(partner.RenderTime, out var position, out var yaw, out var live)) MoveReplica(partner.Actor, position, yaw, live, running);
            foreach (var companion in partner.Summons.Prepend(partner.Pet))
                if (companion.Copy != null && companion.Trail.Sample(partner.RenderTime, out var petPosition, out var petYaw, out var petLive)) MoveReplica(companion.Copy, petPosition, petYaw, petLive, running);
        }
        UpdateMirrors();
    }
}
