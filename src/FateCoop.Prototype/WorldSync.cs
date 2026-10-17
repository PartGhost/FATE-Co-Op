using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using HarmonyLib;
using Tableflip;
using UnityEngine;
using UnityEngine.AI;

namespace FateCoop.Prototype;

public sealed partial class Plugin
{
    private const string TownKey = "GroveTown";
    private const float JoinWait = 5f, ArrivalWait = 20f, ArrivalSpacing = 1.5f;
    private readonly string sessionId = Guid.NewGuid().ToString("N");
    private string layoutHash = "", worldKey = "", requestedWorld = "";
    private int worldOwner = HostId, joiningOwner, arriveNear = Unassigned, travelDepth, heldDepth;
    private WorldSnapshot? pendingWorld, joiningWorld;
    private Action? heldTravel;
    private Vector3? arrivePoint;
    private float heldUntil;
    private bool heldGoesDown = true, replayingTravel;

    [Serializable] private sealed class WorldSnapshot
    {
        public int version = 4;
        public RealmData.RealmId realm;
        public string worldKey = "", layoutHash = "";
        public DungeonSaveData floor = null!;
        public List<int> monsterIds = new List<int>();
    }

    private bool OwnsWorld => worldOwner == myId;

    private void InstallWorldHooks()
    {
        harmony!.Patch(AccessTools.Method(typeof(SceneLoad), "LoadAsync", new[] { typeof(int), typeof(bool), typeof(bool), typeof(Action), typeof(bool), typeof(bool) }), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeSceneLoad)));
        harmony.Patch(AccessTools.Method(typeof(LevelGenerator), "ReplaceDungeon"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeReplaceDungeon)));
        harmony.Patch(AccessTools.Method(typeof(LevelGenerator), "Create"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeWorldChange)));
        harmony.Patch(AccessTools.Method(typeof(LevelGenerator), "OnFinishCreation"), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterWorldReady)));
        harmony.Patch(AccessTools.Method(typeof(LevelGenerator), "LoadSaveData"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeLoadFloor)));
        harmony.Patch(AccessTools.Method(typeof(Monster), "UpdateAI"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeMonsterAI)));
        harmony.Patch(AccessTools.Method(typeof(Fate), "SetPause"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforePause)));
    }

    private static bool BeforePause(bool paused) => !paused || Active == null || !Active.Connected;

    private static bool BeforeSceneLoad(int sceneIndex, bool save, bool withLoadingScreen, Action onDone, bool fadeIn, bool fadeOut)
    {
        var plugin = Active;
        if (plugin == null) return true;
        if (plugin.furnishing)
        {
            plugin.travelAfterBorrowing = () => SceneLoad.LoadAsync(sceneIndex, save, withLoadingScreen, onDone, fadeIn, fadeOut);
            return false;
        }
        var realm = SingletonBehaviour<PlayerStatus>.Instance.DungeonRealm;
        var depth = sceneIndex == (int)Tableflip.Scene.Dungeon ? RunFloor(realm, SaveData.CurrentSaveData.GetLatestDepth(realm)) : 0;
        if (depth > 0 && plugin.HoldTravel(depth, true, () => SceneLoad.LoadAsync(sceneIndex, save, withLoadingScreen, onDone, fadeIn, fadeOut))) return false;
        plugin.travelDepth = depth;
        plugin.BeginWorldChange();
        return true;
    }

    private static bool BeforeReplaceDungeon(LevelGenerator __instance, float newWorldLocationX, float newWorldLocationZ, int newLevelDepth, int newLevelRealm, bool goesDown, bool useSavedPosition, bool ignoreSavedState, bool skipSave)
    {
        var plugin = Active;
        if (plugin == null) return true;
        var runFloor = RunFloor((RealmData.RealmId)newLevelRealm, newLevelDepth);
        if (plugin.HoldTravel(runFloor, goesDown, () => __instance.ReplaceDungeon(newWorldLocationX, newWorldLocationZ, newLevelDepth, newLevelRealm, goesDown, useSavedPosition, ignoreSavedState, skipSave))) return false;
        plugin.travelDepth = runFloor;
        plugin.BeginWorldChange();
        return true;
    }

    private static void BeforeWorldChange() => Active?.BeginWorldChange();
    private static void AfterWorldReady() => Active?.SignalWorldReady();

    private bool HoldTravel(int depth, bool goesDown, Action travel)
    {
        if (replayingTravel || pendingWorld != null || heldTravel != null) return false;
        var ready = ReadyAt(depth);
        if (ready != null)
        {
            RequestWorld(ready);
            heldDepth = 0;
            heldUntil = Time.unscaledTime + JoinWait;
        }
        else if (partners.Values.Any(p => p.Id < myId && p.Frame != null && p.Frame.worldKey.Length == 0 && p.Frame.travelDepth == depth))
        {
            heldDepth = depth;
            heldUntil = Time.unscaledTime + ArrivalWait;
        }
        else return false;
        heldTravel = travel;
        heldGoesDown = goesDown;
        return true;
    }

    private PeerFrame? ReadyAt(int depth) => partners.Values.Select(p => p.Frame)
        .Where(f => f != null && f.scene == Tableflip.Scene.Dungeon.ToString() && f.depth == depth && f.worldKey.Length > 0 && f.owner != myId).OrderBy(f => f!.owner).FirstOrDefault();

    private void RequestWorld(PeerFrame target)
    {
        requestedWorld = target.worldKey;
        Send("world-request", target.worldKey, target.owner);
    }

    private void UpdateHeldTravel()
    {
        if (heldTravel != null && heldDepth > 0 && ReadyAt(heldDepth) is PeerFrame arrived)
        {
            RequestWorld(arrived);
            heldDepth = 0;
            heldUntil = Time.unscaledTime + JoinWait;
        }
        if (heldTravel == null || Time.unscaledTime < heldUntil) return;
        var travel = heldTravel;
        heldTravel = null;
        requestedWorld = "";
        replayingTravel = true;
        try { travel(); }
        finally { replayingTravel = false; }
    }

    private static bool BeforeMonsterAI(Monster __instance)
    {
        var plugin = Active;
        if (plugin == null) return true;
        if (plugin.PartnerOfPet(__instance) != null) return false;
        if (__instance.MyVitals.IsDead) return __instance.GetAIState() == AIState.Dying;
        if (plugin.OwnsWorld) return true;
        var mirror = plugin.monsterIds.ContainsKey(__instance) || plugin.pendingWorld != null || plugin.joiningWorld != null;
        return !mirror || (__instance.HasMaster() && __instance.Master() == Player.PlayerChar);
    }

    private static void BeforeLoadFloor(LevelGenerator __instance)
    {
        var plugin = Active;
        if (plugin?.pendingWorld == null) return;
        var world = plugin.pendingWorld;
        if (__instance.GetLevelDepth() != world.floor.depth || SingletonBehaviour<PlayerStatus>.Instance.DungeonRealm != world.realm) return;
        world.floor.droppedItems.Clear();
        world.floor.containers.RemoveAll(container => string.IsNullOrEmpty(container.containerItemId));
        var realm = SaveData.CurrentSaveData.GetRealmSave(world.realm);
        realm.SetLastVisitedDungeonDepth(world.floor.depth);
        realm.SetDungeonSave(world.floor.depth, world.floor);
        plugin.pendingWorld = null;
        plugin.joiningWorld = world;
        plugin.Logger.LogInfo("FLOOR_INSTALLED depth=" + world.floor.depth);
    }

    private bool SharedWorldReady() => !transitioning && Player.PlayerChar != null && (Player.PlayerChar.Alive() || downed)
        && (SceneLoad.CurrentScene == Tableflip.Scene.Town_Grove || (SceneLoad.CurrentScene == Tableflip.Scene.Dungeon && worldKey.Length > 0));

    private string CurrentWorldKey()
    {
        if (transitioning) return "";
        if (SceneLoad.CurrentScene == Tableflip.Scene.Town_Grove) return TownKey;
        return SceneLoad.CurrentScene == Tableflip.Scene.Dungeon ? worldKey : "";
    }

    private IEnumerable<Partner> WorldPartners()
    {
        var key = LocalFrameWorld();
        return key.Length == 0 ? Enumerable.Empty<Partner>() : partners.Values.Where(p => p.InWorld(key));
    }

    private void FinalizeWorld()
    {
        if (SceneLoad.CurrentScene == Tableflip.Scene.Dungeon)
        {
            layoutHash = Fingerprint(LevelGenerator.currentInstance);
            if (joiningWorld != null && layoutHash == joiningWorld.layoutHash)
            {
                worldKey = joiningWorld.worldKey;
                worldOwner = joiningOwner;
                MatchMirrors(joiningWorld);
            }
            else
            {
                if (joiningWorld != null) Logger.LogError("FLOOR_LAYOUT_MISMATCH expected=" + joiningWorld.layoutHash + " actual=" + layoutHash);
                worldKey = sessionId + ":" + worldEpoch + ":" + layoutHash;
                worldOwner = myId;
            }
        }
        else worldOwner = partners.Values.Where(p => p.InWorld(TownKey)).Select(p => p.Frame!.owner).DefaultIfEmpty(myId).Min();
        joiningWorld = null;
        requestedWorld = "";
        travelDepth = 0;
        ArriveNearPartner();
        Logger.LogInfo($"WORLD_READY key={CurrentWorldKey()} owner={worldOwner}");
    }

    private void ArriveNearPartner()
    {
        var target = arriveNear;
        var point = arrivePoint;
        arriveNear = Unassigned;
        arrivePoint = null;
        if (partners.TryGetValue(target, out var partner) && partner.Frame != null && partner.Frame.worldKey == CurrentWorldKey())
            point = partner.Frame.primaryPosition + Quaternion.Euler(0, partner.Frame.primaryYaw, 0) * Vector3.right * ArrivalSpacing;
        if (point.HasValue && NavMesh.SamplePosition(point.Value, out var hit, 3f, NavMesh.AllAreas)) Player.PlayerChar.TeleportToPosition(hit.position);
    }

    private WorldSnapshot ExportWorld()
    {
        var generator = LevelGenerator.currentInstance;
        generator.PreSaveTask(SaveData.CurrentSaveData);
        var realm = SingletonBehaviour<PlayerStatus>.Instance.DungeonRealm;
        var floor = SaveData.CurrentSaveData.GetRealmSave(realm).GetDungeonSave(generator.GetLevelDepth());
        return new WorldSnapshot { realm = realm, worldKey = CurrentWorldKey(), layoutHash = layoutHash, floor = floor, monsterIds = AssignExportIds(floor) };
    }

    private void SendWorldTo(Partner partner, string key)
    {
        if (!OwnsWorld || key != CurrentWorldKey() || SceneLoad.CurrentScene != Tableflip.Scene.Dungeon || !SharedWorldReady()) return;
        monsterStates.Clear();
        announcedGround.Clear();
        announcedFixtures.Clear();
        Send("world", JsonUtility.ToJson(ExportWorld()), partner.Id);
        Logger.LogInfo("FLOOR_SENT to=" + partner.Id);
    }

    private void ReceiveWorld(Partner partner, string body)
    {
        var snapshot = JsonUtility.FromJson<WorldSnapshot>(body);
        if (snapshot == null || snapshot.worldKey != requestedWorld) return;
        if (transitioning) { requestedWorld = ""; return; }
        JoinWorld(partner.Id, snapshot);
    }

    private void JoinWorld(int owner, WorldSnapshot snapshot)
    {
        if (Player.PlayerChar == null || Fate.CurrentFateVer != Fate.FateVer.Original)
            throw new InvalidOperationException("Joining requires a selected Original FATE character.");
        if (snapshot.version != 4 || !InRun(snapshot.realm) || !RunRealmOpen(snapshot.realm) || snapshot.floor == null || snapshot.monsterIds.Count != snapshot.floor.monsters.Count || snapshot.floor.depth < 1 || snapshot.floor.depth > 10000
            || snapshot.floor.templateIndex < 0 || snapshot.floor.templateIndex >= SingletonBehaviour<FateDB>.Instance.allTemplates.Length
            || snapshot.layoutHash.Length != 64 || snapshot.worldKey.Length > 128 || snapshot.worldKey.Length == 0)
            throw new InvalidOperationException("Incoming floor is invalid.");
        pendingWorld = snapshot;
        joiningOwner = owner;
        heldTravel = null;
        if (SimpleSingletonBehaviour<PlayerUI>.Instance.IsFishing) SimpleSingletonBehaviour<PlayerUI>.Instance.HideFishingUI();
        Player.latestStairsUsedWasDown = heldGoesDown;
        Player.isComingFromMenu = false;
        Player.isEnteringDungeonFromTownDoor = true;
        if (SceneLoad.CurrentScene == Tableflip.Scene.Dungeon)
            LevelGenerator.currentInstance.ReplaceDungeon(snapshot.floor.worldLocationX, snapshot.floor.worldLocationZ, snapshot.floor.depth, (int)snapshot.realm, heldGoesDown, false);
        else
        {
            EnterFloorFromTown(snapshot.realm, snapshot.floor.depth);
            SceneLoad.LoadAsync(Tableflip.Scene.Dungeon, true);
        }
        heldGoesDown = true;
        Logger.LogInfo("JOINING_FLOOR depth=" + snapshot.floor.depth + " owner=" + owner);
    }

    private void UpdateOwnership()
    {
        var key = LocalFrameWorld();
        if (key.Length == 0) return;
        if (!OwnsWorld)
        {
            if (partners.TryGetValue(worldOwner, out var owner) && owner.InWorld(key)) return;
            var successor = WorldPartners().Select(p => p.Id).Append(myId).Min();
            if (successor == myId) TakeOwnership();
            else worldOwner = successor;
            return;
        }
        var rival = WorldPartners().Where(p => p.Frame!.owner == p.Id && p.Id < myId).Select(p => p.Id).DefaultIfEmpty(Unassigned).Max();
        if (rival != Unassigned) { YieldOwnership(rival); return; }
        if (SceneLoad.CurrentScene != Tableflip.Scene.Dungeon || WorldPartners().Any() || requestedWorld.Length > 0 || heldTravel != null) return;
        var depth = RunFloorHere();
        var other = partners.Values.Select(p => p.Frame).Where(f => f != null && f.scene == Tableflip.Scene.Dungeon.ToString() && f.depth == depth && f.worldKey.Length > 0 && f.worldKey != key && f.owner < myId)
            .OrderBy(f => f!.owner).FirstOrDefault();
        if (other != null) RequestWorld(other);
    }

    private void TakeOwnership()
    {
        worldOwner = myId;
        foreach (var monster in monsters.Values.Where(m => m != null))
        {
            monster.allowDropInventory = true;
            monster.allowDropEquipment = true;
        }
        monsterStates.Clear();
        attackingMirrors.Clear();
        nextMonsterId = monsters.Keys.DefaultIfEmpty(0).Max() + 1;
        RealizeGround();
        Logger.LogInfo("OWNERSHIP_TAKEN key=" + CurrentWorldKey());
    }

    private void YieldOwnership(int owner)
    {
        SendGroundTo(owner);
        worldOwner = owner;
        ResetGround();
        foreach (var monster in monsters.Values.Where(m => m != null))
        {
            monster.allowDropInventory = false;
            monster.allowDropEquipment = false;
        }
        monsterStates.Clear();
        Logger.LogInfo("OWNERSHIP_YIELDED to=" + owner);
    }

    private static string Hash(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
    }

    private static string Fingerprint(LevelGenerator generator)
    {
        var maze = (MazeCreation)AccessTools.Field(typeof(LevelGenerator), "activeDungeon").GetValue(generator);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(maze.GetMazeWidth()); writer.Write(maze.GetMazeHeight());
        for (var y = 0; y < maze.GetMazeHeight(); y++)
        for (var x = 0; x < maze.GetMazeWidth(); x++)
        {
            var c = maze.GetCellDataFromPosition(x, y);
            writer.Write(c.cellheightData); writer.Write(c.wallTypeData); writer.Write(c.terrainTypeData);
            writer.Write(c.roomIndexData); writer.Write(c.pitIndexData); writer.Write((int)c.cellType);
            writer.Write(c.heightData); writer.Write(c.xOffsetData); writer.Write(c.yOffsetData);
            writer.Write(c.cellHasWater); writer.Write(c.cellIsFishable); writer.Write(c.cellHasCollideableProp);
        }
        foreach (var p in new[] { generator.GetSpawnPointUp(), generator.GetSpawnPointDown() })
        { writer.Write(p.x); writer.Write(p.y); writer.Write(p.z); }
        writer.Flush();
        return Hash(stream.ToArray());
    }
}
