using System;
using System.IO;
using BepInEx;
using BepInEx.Unity.Mono;
using HarmonyLib;
using Tableflip;
using UnityEngine.SceneManagement;

namespace FateCoop.Core;

[BepInPlugin(Guid, "FATE Co-Op Core", SuiteVersion.Value)]
public sealed class CorePlugin : BaseUnityPlugin
{
    public const string Guid = "local.fatecoop.core";
    private const string TestGame = "test-game";
    private FileStream? runningLock;

    private void Awake()
    {
        if (Config.Bind("Development", "TestTools", false, "Command files and status telemetry for the automated tests.").Value)
        {
            var game = Path.GetFullPath(Paths.GameRootPath);
            runningLock = new FileStream(Path.Combine(game, "fate-coop-running.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            TestChannel.Root = Path.Combine(Directory.GetParent(game)!.FullName, "artifacts", "runtime" + Path.GetFileName(game).Substring(TestGame.Length));
            Directory.CreateDirectory(Path.Combine(TestChannel.Root, "commands"));
            Directory.CreateDirectory(Path.Combine(TestChannel.Root, "processed"));
        }
        new Harmony(Guid).Patch(AccessTools.Method(typeof(I2Loca), "Get", new[] { typeof(string) }), prefix: new HarmonyMethod(typeof(GameAssets), nameof(GameAssets.BeforeTextLookup)));
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, LoadSceneMode mode)
    {
        try { GameAssets.Prepare(); }
        catch (Exception e) { Logger.LogError(e); }
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        runningLock?.Dispose();
    }
}
