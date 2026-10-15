using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Unity.Mono;
using HarmonyLib;
using Tableflip;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace FateCoop.Prototype;

[BepInPlugin("local.fatecoop.prototype", "FATE Co-Op", Version)]
public sealed partial class Plugin : BaseUnityPlugin
{
    internal static Plugin? Active;
    private Harmony? harmony;
    private PartyMember? actor;
    private Player? primary;
    private string controlRoot = "";
    private string status = "Starting";
    private int spawnCount, suppressedAI;
    private bool showPanel = true, testTools;
    private float monsterToughness;
    private bool guestWanted, transitioning, worldReady;
    private Coroutine? respawn;
    private int worldEpoch;
    private int networkAccepted, networkRejected;
    private bool mirroring;
    private Vector3 mirrorTarget;
    private float mirrorYaw, lastMirrorTime;
    private string instance = "a";
    private FileStream? runningLock;

    private void Awake()
    {
        var sandboxVerified = false;
        try
        {
            var game = Path.GetFullPath(Paths.GameRootPath);
            var workspace = Directory.GetParent(game)!.FullName;
            instance = Path.GetFileName(game) == "test-game-b" ? "b" : "a";
            var expected = Path.Combine(workspace, "test-saves", "instance-" + instance);
            if ((Path.GetFileName(game) != "test-game" && Path.GetFileName(game) != "test-game-b") || !File.Exists(Path.Combine(game, "fate-coop-sandbox.json")))
                throw new InvalidOperationException("Prototype must run from the prepared test-game copy.");
            var backend = new Tivola.Framework.SaveManagerStandalone();
            var actual = (string)AccessTools.Method(typeof(Tivola.Framework.SaveManagerStandalone), "GetFilePath").Invoke(backend, new object[] { "isolation-check" });
            if (!string.Equals(Path.GetDirectoryName(actual), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Save isolation check failed: " + actual);
            sandboxVerified = true;
            runningLock = new FileStream(Path.Combine(game, "fate-coop-running.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            testTools = Config.Bind("Development", "TestTools", false, "Command files, status telemetry and the F8-F10 test keys.").Value;
            monsterToughness = Math.Max(1f, Config.Bind("Co-op", "MonsterToughness", 1.75f, "When hosting, how much tougher monsters are while your partner is on the floor. 1 is the same as solo.").Value);
            if (testTools)
            {
                controlRoot = Path.Combine(workspace, "artifacts", instance == "a" ? "runtime" : "runtime-b");
                Directory.CreateDirectory(Path.Combine(controlRoot, "commands"));
                Directory.CreateDirectory(Path.Combine(controlRoot, "processed"));
            }
            Active = this;
            Application.runInBackground = true;
            harmony = new Harmony("local.fatecoop.prototype");
            harmony.Patch(AccessTools.Method(typeof(PartyMember), "UpdateAI"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforePartyAI)));
            // Cleanup happens before scene saving/loading, not after scene objects disappear.
            harmony.Patch(AccessTools.Method(typeof(SceneLoad), "LoadAsync", new[] { typeof(int), typeof(bool), typeof(bool), typeof(Action), typeof(bool), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeSceneLoad)));
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneLoad.OnSceneLoaded += OnGameSceneLoaded;
            PlayerManager.OnPlayerSpawn += OnPrimarySpawn;
            harmony.Patch(AccessTools.Method(typeof(LevelGenerator), "ReplaceDungeon"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeWorldChange)));
            harmony.Patch(AccessTools.Method(typeof(LevelGenerator), "Create"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeWorldChange)));
            harmony.Patch(AccessTools.Method(typeof(LevelGenerator), "OnFinishCreation"), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterWorldReady)));
            InstallWorldHooks();
            InstallEntityHooks();
            InstallCombatHooks();
            InstallLootHooks();
            InstallQuestHooks();
            InstallVisualHooks();
            commandLineLobby = LobbyFromArguments(Environment.GetCommandLineArgs());
            SetStatus("Ready.");
            Logger.LogInfo("ISOLATION_OK " + actual);
            Snapshot();
        }
        catch (Exception exception)
        {
            Logger.LogError(exception);
            enabled = false;
            if (!sandboxVerified) Application.Quit();
        }
    }

    private static bool BeforePartyAI(PartyMember __instance)
    {
        if (Active == null || Active.actor != __instance || !__instance.Alive()) return true;
        Active.suppressedAI++;
        return false;
    }

    private static void BeforeSceneLoad() => Active?.BeginWorldChange();
    private static void BeforeWorldChange() => Active?.BeginWorldChange();
    private static void AfterWorldReady() => Active?.SignalWorldReady();
    private void OnPrimarySpawn(Player player)
    {
        if (SceneLoad.CurrentScene == Tableflip.Scene.Town_Grove) SignalWorldReady();
    }
    private void SignalWorldReady()
    {
        worldReady = true;
        ScheduleRespawn();
    }
    private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, LoadSceneMode mode)
    {
        Snapshot();
    }
    private void OnGameSceneLoaded(Tableflip.Scene scene)
    {
        if (scene == Tableflip.Scene.MainMenu_Grove || scene == Tableflip.Scene.GameSelect || scene == Tableflip.Scene.Engagement)
        {
            guestWanted = false;
            transitioning = false;
        }
        else if (scene == Tableflip.Scene.Town_Grove) SignalWorldReady();
        Snapshot();
    }

    private void BeginWorldChange()
    {
        worldEpoch++;
        transitioning = true;
        worldReady = false;
        if (pendingWorld == null) requestedWorld = "";
        layoutHash = "";
        loadedWorldKey = "";
        ResetMonsters();
        ResetGround();
        if (actor != null) guestWanted = true;
        Despawn(preserveIntent: true);
        if (respawn != null) StopCoroutine(respawn);
        respawn = null;
    }

    private void ScheduleRespawn()
    {
        if (respawn != null) StopCoroutine(respawn);
        respawn = StartCoroutine(RespawnWhenReady());
    }

    private IEnumerator RespawnWhenReady()
    {
        var deadline = Time.unscaledTime + 60f;
        var stableFrames = 0;
        while (Time.unscaledTime < deadline)
        {
            yield return null;
            var host = Player.PlayerChar;
            var agent = host != null ? host.GetComponent<NavMeshAgent>() : null;
            var playable = SceneLoad.CurrentScene == Tableflip.Scene.Town_Grove || SceneLoad.CurrentScene == Tableflip.Scene.Dungeon;
            var ready = worldReady && playable && host != null && host.Alive() && agent != null && agent.enabled && agent.isOnNavMesh
                && (SceneLoad.CurrentScene != Tableflip.Scene.Dungeon || (LevelGenerator.currentInstance != null && LevelGenerator.currentInstance.HasActiveDungeon() && !LevelGenerator.currentInstance.IsCleaning));
            stableFrames = ready ? stableFrames + 1 : 0;
            if (!ready && Time.frameCount % 300 == 0)
                Logger.LogInfo($"WORLD_WAIT signaled={worldReady} gameScene={SceneLoad.CurrentScene} native={SceneManager.GetActiveScene().name} alive={host != null && host.Alive()} paused={Fate.isPaused} nav={agent != null && agent.enabled && agent.isOnNavMesh}");
            if (stableFrames < 20) continue;
            transitioning = false;
            FinalizeWorld();
            respawn = null;
            if (guestWanted && actor == null)
            {
                try { Spawn(); Logger.LogInfo("RESPAWN_OK epoch=" + worldEpoch); }
                catch (Exception e) { Logger.LogError(e); SetStatus("Guest respawn failed: " + e.Message); }
            }
            Snapshot();
            yield break;
        }
        respawn = null;
        SetStatus("Waiting for a playable world timed out; guest remains despawned.");
    }

    private void Update()
    {
        try
        {
            var keyboard = Keyboard.current;
            UpdateSession();
            if (keyboard != null && keyboard.f11Key.wasPressedThisFrame) showPanel = !showPanel;
            if (testTools) UpdateDevTools(keyboard);
            if (actor != null && (primary == null || Player.PlayerChar != primary))
            {
                Logger.LogError("Primary player identity changed; despawning the partner replica.");
                BeginWorldChange();
            }
        }
        catch (Exception e) { Logger.LogError(e); }
    }

    private void Spawn()
    {
        if (actor != null) { SetStatus("Second character already exists."); return; }
        var host = Player.PlayerChar;
        if (transitioning) throw new InvalidOperationException("Wait for the world transition to finish.");
        if (host == null || !host.Alive() || Fate.CurrentFateVer != Fate.FateVer.Original)
            throw new InvalidOperationException("Enter Original FATE / Grove gameplay with a living character first.");
        var prefab = SingletonBehaviour<FateDB>.Instance.partyMemberPrefab;
        if (prefab == null) throw new InvalidOperationException("Party-member prefab is unavailable.");
        var desired = host.transform.position + host.transform.right * 3f;
        if (!NavMesh.SamplePosition(desired, out var hit, 10f, NavMesh.AllAreas))
            throw new InvalidOperationException("No navigation mesh near the primary player.");
        PlayerSaveData configuration;
        List<ItemSaveData> equipment;
        if (remoteProfileJson.Length > 0)
        {
            var profile = JsonUtility.FromJson<Profile>(remoteProfileJson);
            configuration = profile.player;
            equipment = profile.equipment;
        }
        else
        {
            configuration = new PlayerSaveData();
            host.FillPlayerSaveData(ref configuration);
            configuration = configuration.CreatePartyMemberCopy();
            configuration.playerName = "Coop Test Guest";
            configuration.lineage = 0;
            equipment = new List<ItemSaveData>();
        }
        configuration.health = Math.Max(configuration.health, 10f);
        // This test character is deliberately not added to the host's saved party collection.
        GameObject? spawned = null;
        try
        {
            spawned = Object.Instantiate(prefab, hit.position, host.transform.rotation);
            actor = spawned.GetComponent<PartyMember>();
            if (actor == null) throw new InvalidOperationException("Prefab has no PartyMember component.");
            primary = host;
            spawned.name = "FateCoop_Prototype_Guest";
            actor.SetMasterCharacter(host);
            buildingActor = true;
            try { actor.Init(configuration, new ItemStorage { items = equipment }); }
            finally { buildingActor = false; }
            // PartyMember.Init omits gender. Apply the explicit player configuration here.
            var customization = spawned.GetComponentInChildren<CharacterCustomizationDefaults>(true);
            if (customization != null)
            {
                customization.SetGender(configuration.male);
                customization.SetRace(configuration.playerRace);
                customization.SetHair(configuration.hairIndex);
                customization.SetHead(configuration.headIndex);
            }
            actor.SetInvincible();
            actor.SetPositionProper(hit.position);
            actor.SetTarget(null);
            actor.SetAIState(AIState.Inactive);
            actor.StopPathing();
            if (Player.PlayerChar != host) throw new InvalidOperationException("Spawn replaced the primary player reference.");
            var agent = actor.GetComponent<NavMeshAgent>();
            if (!agent.isOnNavMesh) throw new InvalidOperationException("Second character did not bind to the navigation mesh.");
            spawnCount++;
            guestWanted = true;

            Logger.LogInfo($"SPAWN_OK primary={host.GetInstanceID()} guest={actor.GetInstanceID()} position={hit.position} speed={agent.speed} layer={LayerMask.LayerToName(spawned.layer)}");
            Snapshot();
        }
        catch
        {
            actor = null;
            primary = null;
            if (spawned != null) Object.Destroy(spawned);
            throw;
        }
    }

    private void StopRoute()
    {
        mirroring = false;
        if (route != null) StopCoroutine(route);
        route = null;
        if (actor != null) actor.StopPathing();
    }

    private void LateUpdate()
    {
        UpdateRemoteEntities();
        if (!mirroring || actor == null) return;
        var agent = actor.GetComponent<NavMeshAgent>();
        if (agent == null || !agent.isOnNavMesh) return;
        var fresh = Time.unscaledTime - lastMirrorTime < 0.5f;
        var previous = actor.transform.position;
        var desired = fresh ? mirrorTarget : previous;
        var next = Vector3.Distance(previous, desired) > 4f ? desired : Vector3.Lerp(previous, desired, 1f - Mathf.Exp(-30f * Time.unscaledDeltaTime));
        agent.Warp(next);
        actor.transform.rotation = Quaternion.Slerp(actor.transform.rotation, Quaternion.Euler(0, mirrorYaw, 0), 1f - Mathf.Exp(-20f * Time.unscaledDeltaTime));
        var animator = actor.GetComponent<Animator>();
        if (animator != null) animator.SetBool("IsWalking", fresh && Vector3.Distance(previous, next) > 0.001f);
    }

    private void HostSteam()
    {
        if (!SteamReady) throw new InvalidOperationException("Steam is not running for this game.");
        StartSession(SteamLink.Host(SetStatus), true);
    }

    private void Despawn(bool preserveIntent = false)
    {
        ForgetShownEffects();
        DespawnRemotePet();
        if (!preserveIntent) guestWanted = false;
        StopRoute();
        if (actor != null)
        {
            var old = actor;
            old.gameObject.SetActive(false);
            actor = null;
            Object.Destroy(old.gameObject);
            Logger.LogInfo("DESPAWN_OK " + old.GetInstanceID());
        }
        primary = null;
        remoteSwings = remoteCasts = remoteArrows = remoteLevelUps = remoteSpells = -1;
        replicaFishing = 0;
        replicaPole = null;
        replicaArrow = null;
    }

    private void SetStatus(string text) { status = text; Logger.LogInfo(text); }

    private void OnGUI()
    {
        if (!enabled || !showPanel) return;
        if (actor != null && Camera.main != null)
        {
            var screenPoint = Camera.main.WorldToScreenPoint(actor.transform.position + Vector3.up * 2.4f);
            if (screenPoint.z > 0)
            {
                var label = new Rect(screenPoint.x - 70, Screen.height - screenPoint.y - 12, 140, 24);
                GUI.Box(label, mirroring ? actor.CharacterName : "CO-OP TEST GUEST");
                if (mirroring)
                {
                    DrawBar(new Rect(label.x, label.yMax + 2, label.width, 6), remoteHp, remoteMaxHp, Color.red);
                    DrawBar(new Rect(label.x, label.yMax + 9, label.width, 4), remoteMana, remoteMaxMana, new Color(0.25f, 0.5f, 1f));
                }
            }
        }
        if (remotePet != null && Camera.main != null) DrawEntityLabel(remotePet.transform.position, remotePet.CharacterName);
        DrawPartnerFrame();
        GUILayout.BeginArea(new Rect(12, 12, 440, 150), GUI.skin.box);
        GUILayout.Label("FATE CO-OP " + Version + (testTools ? " | instance " + instance.ToUpperInvariant() : ""));
        GUILayout.Label(status);
        GUILayout.Label(!sharedSession ? "F11 hides this panel." : isHost ? "Shared combat and loot on. You host the world." : "Shared combat and loot on. First to grab gets it.");
        GUILayout.Label(link == null ? "Not in co-op." : (isHost ? "Hosting co-op: " : "Joined co-op: ") + (peerConnected ? "partner connected" : "waiting for partner"));
        GUILayout.BeginHorizontal();
        if (link == null && SteamReady && GUILayout.Button("Host Steam co-op"))
            try { HostSteam(); } catch (Exception e) { SetStatus(e.Message); }
        if (link is SteamLink { CanInvite: true } steam && GUILayout.Button("Invite friend")) steam.Invite();
        if (link != null && GUILayout.Button("Leave co-op")) EndSession("Left co-op.");
        GUILayout.EndHorizontal();
        GUILayout.EndArea();
        if (!isHost && partyQuests.Length > 0)
        {
            var lines = partyQuests.Split('\n');
            GUILayout.BeginArea(new Rect(12, 166, 440, 30 + 22 * lines.Length), GUI.skin.box);
            GUILayout.Label("Party quests (the host's):");
            foreach (var line in lines) GUILayout.Label(line);
            GUILayout.EndArea();
        }
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneLoad.OnSceneLoaded -= OnGameSceneLoaded;
        PlayerManager.OnPlayerSpawn -= OnPrimarySpawn;
        EndSession("Game closed.");
        lobbyInvites?.Dispose();
        presenceJoins?.Dispose();
        LeaveSharedWorld();
        Despawn();
        harmony?.UnpatchSelf();
        runningLock?.Dispose();
        if (Active == this) Active = null;
    }
}
