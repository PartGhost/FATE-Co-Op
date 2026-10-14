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
    private bool movementSession;
    private float nextSafetyUpdate;
    private readonly Dictionary<Character, bool> protectedCharacters = new Dictionary<Character, bool>();

    [Serializable] private sealed class WorldSnapshot
    {
        public int version = 1;
        public string worldKey = "", layoutHash = "";
        public DungeonSaveData floor = null!;
    }

    private void InstallWorldHooks()
    {
        harmony!.Patch(AccessTools.Method(typeof(LevelGenerator), "LoadSaveData"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeLoadFloor)));
        harmony.Patch(AccessTools.Method(typeof(Monster), "UpdateAI"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeMonsterAI)));
        harmony.Patch(AccessTools.Method(typeof(DroppedItem), "Interact"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeSharedInteraction)));
        harmony.Patch(AccessTools.Method(typeof(ContainerInteractable), "Interact"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeSharedInteraction)));
    }
    private static bool BeforeSharedInteraction() => Active == null || !Active.movementSession;
    private static bool BeforeMonsterAI(Monster __instance)
    {
        if (Active == null || !Active.movementSession) return true;
        if (__instance == Active.remotePet) return false;
        if (__instance == Active.combatMonster && Active.instance == "a") return true;
        __instance.SetTarget(null);
        __instance.StopPathing();
        return false;
    }
    private static void BeforeLoadFloor(LevelGenerator __instance)
    {
        var plugin = Active;
        if (plugin?.pendingWorld == null || plugin.instance != "b") return;
        var world = plugin.pendingWorld;
        if (__instance.GetLevelDepth() != world.floor.depth) return;
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
        return instance == "a" ? sessionId + ":" + worldEpoch + ":" + layoutHash : loadedWorldKey;
    }
    private void FinalizeWorld()
    {
        if (SceneLoad.CurrentScene != Tableflip.Scene.Dungeon) { joiningWorld = null; return; }
        layoutHash = Fingerprint(LevelGenerator.currentInstance);
        if (instance == "b" && joiningWorld != null)
        {
            if (layoutHash == joiningWorld.layoutHash) loadedWorldKey = joiningWorld.worldKey;
            else { loadedWorldKey = ""; Logger.LogError("HOST_LAYOUT_MISMATCH expected=" + joiningWorld.layoutHash + " actual=" + layoutHash); }
            joiningWorld = null;
        }
        Logger.LogInfo("WORLD_LAYOUT " + layoutHash);
    }
    private void ExportWorld()
    {
        if (instance != "a" || !SharedWorldReady() || SceneLoad.CurrentScene != Tableflip.Scene.Dungeon)
            throw new InvalidOperationException("Only host A can export a ready dungeon.");
        var generator = LevelGenerator.currentInstance;
        generator.PreSaveTask(SaveData.CurrentSaveData);
        var floor = SaveData.CurrentSaveData.GetRealmSave(RealmData.RealmId.Grove).GetDungeonSave(generator.GetLevelDepth());
        var json = JsonUtility.ToJson(new WorldSnapshot { worldKey = CurrentWorldKey(), layoutHash = layoutHash, floor = floor });
        File.WriteAllText(Path.Combine(controlRoot, "world-export.json"), json);
        Logger.LogInfo("HOST_WORLD_EXPORTED depth=" + floor.depth);
    }
    private void JoinWorld(string checksum)
    {
        if (instance != "b" || Player.PlayerChar == null || Fate.CurrentFateVer != Fate.FateVer.Original)
            throw new InvalidOperationException("Joining requires instance B's selected Original FATE character.");
        var path = Path.Combine(controlRoot, "world-incoming.json");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 8 * 1024 * 1024 || !string.Equals(Hash(bytes), checksum, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Incoming world checksum or size is invalid.");
        var snapshot = JsonUtility.FromJson<WorldSnapshot>(System.Text.Encoding.UTF8.GetString(bytes));
        if (snapshot == null || snapshot.version != 1 || snapshot.floor == null || snapshot.floor.depth < 1 || snapshot.floor.depth > 10000
            || snapshot.floor.templateIndex < 0 || snapshot.floor.templateIndex >= SingletonBehaviour<FateDB>.Instance.allTemplates.Length
            || snapshot.layoutHash.Length != 64 || snapshot.worldKey.Length > 128 || snapshot.worldKey.Length == 0)
            throw new InvalidOperationException("Incoming world metadata is invalid.");
        // Clone only the world record; the joiner's player, inventory and pet records remain their own.
        pendingWorld = snapshot;
        movementSession = true;
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
    private void UpdateSessionSafety()
    {
        if (!movementSession || Time.unscaledTime < nextSafetyUpdate) return;
        nextSafetyUpdate = Time.unscaledTime + 0.25f;
        foreach (var monster in UnityEngine.Object.FindObjectsOfType<Monster>())
            if (monster != combatMonster || instance != "a") Protect(monster);
        if (Player.PlayerChar != null) Protect(Player.PlayerChar);
    }
    private void Protect(Character character)
    {
        if (!protectedCharacters.ContainsKey(character)) protectedCharacters.Add(character, character.Invincible);
        character.SetInvincible(true);
    }
    private void RestoreSessionSafety()
    {
        movementSession = false;
        ResetCombat();
        DespawnRemotePet();
        foreach (var entry in protectedCharacters) if (entry.Key != null) entry.Key.SetInvincible(entry.Value);
        protectedCharacters.Clear();
    }
}
