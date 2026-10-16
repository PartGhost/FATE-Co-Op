using System;
using System.Collections;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.Mono;
using HarmonyLib;
using Tableflip;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace FateCoop.Prototype;

[BepInPlugin("local.fatecoop.prototype", "FATE Co-Op", Version)]
public sealed partial class Plugin : BaseUnityPlugin
{
    private const string TestGame = "test-game";
    internal static Plugin? Active;
    private Harmony? harmony;
    private ConfigEntry<int>? playerCount;
    private string controlRoot = "";
    private string status = "Starting";
    private bool showPanel = true, testTools, transitioning, worldReady;
    private float monsterToughness;
    private Coroutine? respawn;
    private int worldEpoch, networkAccepted, networkRejected;
    private FileStream? runningLock;

    private void Awake()
    {
        try
        {
            testTools = Config.Bind("Development", "TestTools", false, "Command files and status telemetry for the automated tests.").Value;
            monsterToughness = Math.Max(1f, Config.Bind("Co-op", "MonsterToughness", 1.75f, "How much tougher monsters get for each extra player on the floor. 1 is the same as solo.").Value);
            playerCount = Config.Bind("Co-op", "Players", 2, new ConfigDescription("Lobby size when hosting. Change it with the arrows in Host co-op.", new AcceptableValueRange<int>(MinPlayers, MaxPlayers)));
            if (testTools)
            {
                var game = Path.GetFullPath(Paths.GameRootPath);
                runningLock = new FileStream(Path.Combine(game, "fate-coop-running.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                controlRoot = Path.Combine(Directory.GetParent(game)!.FullName, "artifacts", "runtime" + Path.GetFileName(game).Substring(TestGame.Length));
                Directory.CreateDirectory(Path.Combine(controlRoot, "commands"));
                Directory.CreateDirectory(Path.Combine(controlRoot, "processed"));
            }
            Active = this;
            Application.runInBackground = true;
            harmony = new Harmony("local.fatecoop.prototype");
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneLoad.OnSceneLoaded += OnGameSceneLoaded;
            PlayerManager.OnPlayerSpawn += OnPrimarySpawn;
            InstallWorldHooks();
            InstallEntityHooks();
            InstallCombatHooks();
            InstallLootHooks();
            InstallQuestHooks();
            InstallVisualHooks();
            InstallMenuHooks();
            InstallControlHooks();
            InstallReviveHooks();
            InstallAllySpellHooks();
            InstallPortalHooks();
            InstallSoundHooks();
            InstallRangedHooks();
            InstallQuestLogHooks();
            InstallTownNpcHooks();
            commandLineLobby = LobbyFromArguments(Environment.GetCommandLineArgs());
            SetStatus("Ready.");
            Snapshot();
        }
        catch (Exception exception)
        {
            Logger.LogError(exception);
            enabled = false;
        }
    }

    private void OnPrimarySpawn(Player player)
    {
        if (SceneLoad.CurrentScene == Tableflip.Scene.Town_Grove) SignalWorldReady();
    }

    private void SignalWorldReady()
    {
        worldReady = true;
        if (respawn != null) StopCoroutine(respawn);
        respawn = StartCoroutine(SettleWorld());
    }

    private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, LoadSceneMode mode)
    {
        if (borrowingNpcs && mode == LoadSceneMode.Additive) KeepBorrowedNpcs(scene);
        Snapshot();
    }

    private void OnGameSceneLoaded(Tableflip.Scene scene)
    {
        if (scene == Tableflip.Scene.MainMenu_Grove || scene == Tableflip.Scene.GameSelect || scene == Tableflip.Scene.Engagement) transitioning = false;
        else if (scene == Tableflip.Scene.Town_Grove)
        {
            SignalWorldReady();
            FurnishTown();
        }
        Snapshot();
    }

    private void BeginWorldChange()
    {
        worldEpoch++;
        transitioning = true;
        worldReady = false;
        if (pendingWorld == null) requestedWorld = "";
        layoutHash = "";
        worldKey = "";
        EndDowned();
        CloseChat();
        ResetMonsters();
        ResetGround();
        worldOwner = myId;
        DespawnPartners();
        if (respawn != null) StopCoroutine(respawn);
        respawn = null;
    }

    private IEnumerator SettleWorld()
    {
        var deadline = Time.unscaledTime + 60f;
        var stableFrames = 0;
        while (Time.unscaledTime < deadline)
        {
            yield return null;
            var player = Player.PlayerChar;
            var agent = player != null ? player.GetComponent<NavMeshAgent>() : null;
            var playable = SceneLoad.CurrentScene == Tableflip.Scene.Town_Grove || SceneLoad.CurrentScene == Tableflip.Scene.Dungeon;
            var ready = worldReady && playable && player != null && player.Alive() && agent != null && agent.enabled && agent.isOnNavMesh
                && (SceneLoad.CurrentScene != Tableflip.Scene.Dungeon || (LevelGenerator.currentInstance != null && LevelGenerator.currentInstance.HasActiveDungeon() && !LevelGenerator.currentInstance.IsCleaning));
            stableFrames = ready ? stableFrames + 1 : 0;
            if (stableFrames < 20) continue;
            transitioning = false;
            FinalizeWorld();
            respawn = null;
            Snapshot();
            yield break;
        }
        respawn = null;
        SetStatus("Waiting for a playable world timed out.");
    }

    private void Update()
    {
        try
        {
            var keyboard = Keyboard.current;
            UpdateSession();
            UpdateMenus();
            UpdatePartnerHud();
            UpdateNameplates();
            UpdateChat();
            if (keyboard != null && keyboard.f11Key.wasPressedThisFrame) showPanel = !showPanel;
            if (testTools) UpdateDevTools();
        }
        catch (Exception e) { Logger.LogError(e); }
    }

    private void LateUpdate() => UpdateRemoteEntities();

    private void HostSteam()
    {
        if (!SteamReady) throw new InvalidOperationException("Steam is not running for this game.");
        StartSession(SteamLink.Host(playerCount!.Value, Notify), true);
    }

    private void SetStatus(string text) { status = text; Logger.LogInfo(text); }

    private static bool MenuOpen() => PauseUI.IsShown || PromptUI.IsPromptActive || TutorialPopupUI.IsShown || InputBlocker.AnyBlockingUiMenuIsOpen
        || (SingletonBehaviour<Cleverous.Prodigy.Input.InputBroker>.InstanceExists() && SingletonBehaviour<Cleverous.Prodigy.Input.InputBroker>.Instance.ActiveInputMode != Cleverous.Prodigy.Input.InputBroker.InputMode.Game);

    private void OnGUI()
    {
        if (!enabled || !showPanel || PauseUI.IsShown || PromptUI.IsPromptActive) return;
        DrawChat(Screen.height - 20f);
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneLoad.OnSceneLoaded -= OnGameSceneLoaded;
        PlayerManager.OnPlayerSpawn -= OnPrimarySpawn;
        EndSession("");
        lobbyInvites?.Dispose();
        presenceJoins?.Dispose();
        harmony?.UnpatchSelf();
        runningLock?.Dispose();
        if (Active == this) Active = null;
    }
}
