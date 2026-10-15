using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using HarmonyLib;
using Tableflip;
using UnityEngine;

namespace FateCoop.Prototype;

public sealed partial class Plugin
{
    private readonly string sessionId = Guid.NewGuid().ToString("N");
    private string layoutHash = "", loadedWorldKey = "";
    private WorldSnapshot? pendingWorld, joiningWorld;
    private bool sharedSession;

    [Serializable] private sealed class WorldSnapshot
    {
        public int version = 2;
        public string worldKey = "", layoutHash = "";
        public DungeonSaveData floor = null!;
        public List<int> monsterIds = new List<int>();
    }

    private void InstallWorldHooks()
    {
        harmony!.Patch(AccessTools.Method(typeof(LevelGenerator), "LoadSaveData"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeLoadFloor)));
        harmony.Patch(AccessTools.Method(typeof(Monster), "UpdateAI"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeMonsterAI)));
        harmony.Patch(AccessTools.Method(typeof(Fate), "SetPause"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforePause)));
    }
    private static bool BeforePause(bool paused) => !paused || Active == null || !Active.peerConnected;
    private static bool BeforeMonsterAI(Monster __instance)
    {
        var plugin = Active;
        if (plugin == null) return true;
        if (__instance == plugin.remotePet) return false;
        var hostOwned = plugin.monsterIds.ContainsKey(__instance) || plugin.pendingWorld != null || plugin.joiningWorld != null;
        return !plugin.sharedSession || plugin.isHost || !hostOwned || __instance.MyVitals.IsDead || (__instance.HasMaster() && __instance.Master() == Player.PlayerChar);
    }
    private static void BeforeLoadFloor(LevelGenerator __instance)
    {
        var plugin = Active;
        if (plugin?.pendingWorld == null || plugin.isHost) return;
        var world = plugin.pendingWorld;
        if (__instance.GetLevelDepth() != world.floor.depth) return;
        world.floor.droppedItems.Clear();
        world.floor.containers.RemoveAll(container => string.IsNullOrEmpty(container.containerItemId));
        var realm = SaveData.CurrentSaveData.GetRealmSave(RealmData.RealmId.Grove);
        realm.SetLastVisitedDungeonDepth(world.floor.depth);
        realm.SetDungeonSave(world.floor.depth, world.floor);
        plugin.pendingWorld = null;
        plugin.joiningWorld = world;
        plugin.Logger.LogInfo("HOST_FLOOR_INSTALLED depth=" + world.floor.depth);
    }
    private bool SharedWorldReady() => !transitioning && Player.PlayerChar != null && Player.PlayerChar.Alive()
        && (SceneLoad.CurrentScene == Tableflip.Scene.Town_Grove || (SceneLoad.CurrentScene == Tableflip.Scene.Dungeon && layoutHash.Length > 0 && CurrentWorldKey().Length > 0));
    private string CurrentWorldKey()
    {
        if (transitioning) return "";
        if (SceneLoad.CurrentScene == Tableflip.Scene.Town_Grove) return "GroveTown";
        if (SceneLoad.CurrentScene != Tableflip.Scene.Dungeon || layoutHash.Length == 0) return "";
        return isHost ? sessionId + ":" + worldEpoch + ":" + layoutHash : loadedWorldKey;
    }
    private void FinalizeWorld()
    {
        if (SceneLoad.CurrentScene != Tableflip.Scene.Dungeon) { joiningWorld = null; return; }
        layoutHash = Fingerprint(LevelGenerator.currentInstance);
        if (!isHost && joiningWorld != null)
        {
            if (layoutHash == joiningWorld.layoutHash)
            {
                loadedWorldKey = joiningWorld.worldKey;
                MatchMirrors(joiningWorld);
            }
            else { loadedWorldKey = ""; Logger.LogError("HOST_LAYOUT_MISMATCH expected=" + joiningWorld.layoutHash + " actual=" + layoutHash); }
            joiningWorld = null;
        }
        Logger.LogInfo("WORLD_LAYOUT " + layoutHash);
    }
    private WorldSnapshot ExportWorld()
    {
        var generator = LevelGenerator.currentInstance;
        generator.PreSaveTask(SaveData.CurrentSaveData);
        var floor = SaveData.CurrentSaveData.GetRealmSave(RealmData.RealmId.Grove).GetDungeonSave(generator.GetLevelDepth());
        Logger.LogInfo("HOST_WORLD_EXPORTED depth=" + floor.depth);
        return new WorldSnapshot { worldKey = CurrentWorldKey(), layoutHash = layoutHash, floor = floor, monsterIds = AssignExportIds(floor) };
    }
    private void JoinWorld(WorldSnapshot snapshot)
    {
        if (Player.PlayerChar == null || Fate.CurrentFateVer != Fate.FateVer.Original)
            throw new InvalidOperationException("Joining requires a selected Original FATE character.");
        if (snapshot == null || snapshot.version != 2 || snapshot.floor == null || snapshot.monsterIds.Count != snapshot.floor.monsters.Count || snapshot.floor.depth < 1 || snapshot.floor.depth > 10000
            || snapshot.floor.templateIndex < 0 || snapshot.floor.templateIndex >= SingletonBehaviour<FateDB>.Instance.allTemplates.Length
            || snapshot.layoutHash.Length != 64 || snapshot.worldKey.Length > 128 || snapshot.worldKey.Length == 0)
            throw new InvalidOperationException("Incoming world metadata is invalid.");
        // Clone only the world record; the joiner's player, inventory and pet records remain their own.
        pendingWorld = snapshot;
        if (SimpleSingletonBehaviour<PlayerUI>.Instance.IsFishing) SimpleSingletonBehaviour<PlayerUI>.Instance.HideFishingUI();
        Player.latestStairsUsedWasDown = true;
        Player.isComingFromMenu = false;
        Player.isEnteringDungeonFromTownDoor = true;
        SingletonBehaviour<PlayerStatus>.Instance.DungeonRealm = RealmData.RealmId.Grove;
        SaveData.CurrentSaveData.GetRealmSave(RealmData.RealmId.Grove).SetLastVisitedDungeonDepth(snapshot.floor.depth);
        if (SceneLoad.CurrentScene == Tableflip.Scene.Dungeon)
            LevelGenerator.currentInstance.ReplaceDungeon(snapshot.floor.worldLocationX, snapshot.floor.worldLocationZ, snapshot.floor.depth, (int)RealmData.RealmId.Grove, true, false);
        else SceneLoad.LoadAsync(Tableflip.Scene.Dungeon, true);
        Logger.LogInfo("JOINING_HOST_FLOOR depth=" + snapshot.floor.depth);
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
    private void LeaveSharedWorld()
    {
        sharedSession = false;
        ResetMonsters();
        ResetGround();
        DespawnRemotePet();
    }
}
