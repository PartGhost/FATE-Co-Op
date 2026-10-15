using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Unity.Mono;
using HarmonyLib;
using Steamworks;
using Tableflip;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace FateCoop.Prototype;

[BepInPlugin("local.fatecoop.prototype", "FATE Co-Op Movement Prototype", Version)]
public sealed partial class Plugin : BaseUnityPlugin
{
    internal static Plugin? Active;
    private Harmony? harmony;
    private PartyMember? actor;
    private Player? primary;
    private Coroutine? route;
    private string controlRoot = "";
    private string status = "Starting";
    private float nextPoll, nextSnapshot;
    private int spawnCount, suppressedAI;
    private bool showPanel = true;
    private bool guestWanted, transitioning, worldReady;
    private Coroutine? respawn;
    private int worldEpoch;
    private int networkAccepted, networkRejected;
    private bool mirroring;
    private Vector3 mirrorTarget;
    private float mirrorYaw, lastMirrorTime;
    private string instance = "a";
    private Coroutine? testWalk;
    private FileStream? runningLock;
    private Monster? aggroMonster;

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
            controlRoot = Path.Combine(workspace, "artifacts", instance == "a" ? "runtime" : "runtime-b");
            Directory.CreateDirectory(Path.Combine(controlRoot, "commands"));
            Directory.CreateDirectory(Path.Combine(controlRoot, "processed"));
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
            commandLineLobby = LobbyFromCommandLine();
            SetStatus("Ready. Save isolation verified; F8 spawns, F9 walks, F10 stops.");
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
        layoutHash = "";
        loadedWorldKey = "";
        ResetMonsters();
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
            if (keyboard != null)
            {
                if (keyboard.f8Key.wasPressedThisFrame) { if (actor != null) Despawn(); else Spawn(); }
                if (keyboard.f9Key.wasPressedThisFrame) StartRoute();
                if (keyboard.f10Key.wasPressedThisFrame) StopRoute();
                if (keyboard.f11Key.wasPressedThisFrame) showPanel = !showPanel;
            }
            if (Time.unscaledTime >= nextPoll)
            {
                nextPoll = Time.unscaledTime + 0.2f;
                foreach (var file in Directory.GetFiles(Path.Combine(controlRoot, "commands"), "*.cmd").OrderBy(p => p, StringComparer.Ordinal))
                {
                    var command = File.ReadAllText(file).Trim();
                    File.Move(file, Path.Combine(controlRoot, "processed", Path.GetFileName(file)));
                    try { Execute(command); }
                    catch (Exception e) { SetStatus("Command failed: " + e.Message); Logger.LogError(e); }
                }
            }
            if (actor != null && (primary == null || Player.PlayerChar != primary))
            {
                Logger.LogError("Primary player identity changed; despawning test actor.");
                BeginWorldChange();
            }
            if (Time.unscaledTime >= nextSnapshot) { nextSnapshot = Time.unscaledTime + 1f; Snapshot(); }
        }
        catch (Exception e) { Logger.LogError(e); nextPoll = Time.unscaledTime + 1f; }
    }

    private void Execute(string command)
    {
        Logger.LogInfo("COMMAND " + command);
        var parts = command.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;
        switch (parts[0])
        {
            case "spawn": Spawn(); break;
            case "despawn": Despawn(); break;
            case "route": StartRoute(); break;
            case "stop": StopRoute(); break;
            case "status": Snapshot(); break;
            case "save": SaveData.RequestSave(); break;
            case "test-hit":
                var attacker = Player.PlayerChar ?? throw new InvalidOperationException("No primary character.");
                var victim = NearestEnemy(attacker.transform.position) ?? throw new InvalidOperationException("No enemy to hit.");
                attacker.SelectAttack();
                RollAttackMethod.Invoke(attacker, new object[] { victim });
                attacker.attackTimer = 0f;
                CurrentAttackField.SetValue(attacker, null);
                break;
            case "test-swing":
                if (Player.PlayerChar == null) throw new InvalidOperationException("No primary character.");
                Player.PlayerChar.SelectAttack();
                Player.PlayerChar.StartAttack();
                break;
            case "test-aggro":
                if (!isHost || actor == null) throw new InvalidOperationException("test-aggro needs the host with a connected partner.");
                if (aggroMonster == null || aggroMonster.MyVitals.IsDead)
                {
                    aggroMonster = NearestEnemy(actor.transform.position) ?? throw new InvalidOperationException("No enemy near the partner.");
                    if (!NavMesh.SamplePosition(actor.transform.position + actor.transform.forward * 2f, out var beside, 3f, NavMesh.AllAreas))
                        throw new InvalidOperationException("No navigation surface beside the partner.");
                    aggroMonster.SetPositionProper(beside.position);
                    aggroMonster.maxHpBase = 100000;
                    aggroMonster.hp = 100000f;
                }
                aggroMonster.SetTarget(actor);
                aggroMonster.SetAIState(AIState.Hunt);
                break;
            case "test-calm":
                if (aggroMonster != null && !aggroMonster.MyVitals.IsDead)
                {
                    aggroMonster.maxHpBase = 1;
                    aggroMonster.hp = 1f;
                    aggroMonster.SetTarget(null);
                    aggroMonster.SetAIState(AIState.Wander);
                }
                aggroMonster = null;
                break;
            case "host-local": StartSession(LocalLink.Listen(), true); break;
            case "join-local":
                if (parts.Length != 2) throw new ArgumentException("join-local <port>");
                StartSession(LocalLink.Connect(int.Parse(parts[1], CultureInfo.InvariantCulture)), false);
                break;
            case "host-steam":
                if (!SteamReady) throw new InvalidOperationException("Steam is not running for this game.");
                StartSession(SteamLink.Host(SetStatus), true);
                break;
            case "join-steam":
                if (parts.Length != 2) throw new ArgumentException("join-steam <lobby id>");
                JoinSteam(new CSteamID(ulong.Parse(parts[1], CultureInfo.InvariantCulture)));
                break;
            case "invite":
                if (!(link is SteamLink { CanInvite: true } steam)) throw new InvalidOperationException("Host a Steam lobby first.");
                steam.Invite();
                break;
            case "leave": EndSession("Left co-op."); break;
            case "test-walk":
                if (parts.Length != 3 || !SharedWorldReady())
                    throw new InvalidOperationException("test-walk <dx> <dz> requires a ready Grove world.");
                if (testWalk != null) StopCoroutine(testWalk);
                testWalk = StartCoroutine(TestPrimaryWalk(new Vector3(float.Parse(parts[1], CultureInfo.InvariantCulture), 0, float.Parse(parts[2], CultureInfo.InvariantCulture))));
                break;
            case "test-resume":
                var tutorial = UnityEngine.Object.FindObjectOfType<TutorialPopupUI>();
                for (var i = 0; tutorial != null && TutorialPopupUI.IsShown && i < 32; i++)
                    AccessTools.Method(typeof(TutorialPopupUI), "OnConfirmButtonClicked").Invoke(tutorial, null);
                Fate.SetPause(false);
                break;
            case "test-invincible":
                if (Player.PlayerChar == null) throw new InvalidOperationException("No primary character.");
                Player.PlayerChar.SetInvincible(parts.Length == 2 && parts[1] == "on");
                break;
            case "enter-dungeon":
                if (SceneLoad.CurrentScene != Tableflip.Scene.Town_Grove || Player.PlayerChar == null)
                    throw new InvalidOperationException("Dungeon test starts in Grove town.");
                Player.latestStairsUsedWasDown = true;
                Player.isEnteringDungeonFromTownDoor = true;
                SingletonBehaviour<PlayerStatus>.Instance.DungeonRealm = RealmData.RealmId.Grove;
                SaveData.CurrentSaveData.GetRealmSave(RealmData.RealmId.Grove).SetLastVisitedDungeonDepth(1);
                SceneLoad.LoadAsync(Tableflip.Scene.Dungeon, true);
                break;
            case "next-floor":
                if (LevelGenerator.currentInstance == null || transitioning) throw new InvalidOperationException("Dungeon is not ready.");
                Player.latestStairsUsedWasDown = true;
                Fate.SetPause(true);
                LevelGenerator.currentInstance.ReplaceDungeon(0, 0, LevelGenerator.currentInstance.GetLevelDepth() + 1, (int)RealmData.RealmId.Grove, true, false);
                break;
            case "town":
                if (LevelGenerator.currentInstance == null || transitioning) throw new InvalidOperationException("Dungeon is not ready.");
                LevelGenerator.currentInstance.UpdateSaveData(false);
                Town.UpdateSpawnIndex(RealmData.RealmId.Grove, Town.EntryMethod.Stairs);
                SceneLoad.LoadAsync(Tableflip.Scene.Town_Grove, true);
                break;
            case "menu":
                if (!SaveData.HasLoaded || Player.PlayerChar != null) throw new InvalidOperationException("Wait for startup, and use menu only before entering gameplay.");
                EngagementUI.hasEngaged = true;
                Fate.CurrentFateVer = Fate.FateVer.Original;
                SceneLoad.LoadAsync(Tableflip.Scene.MainMenu_Grove, false);
                break;
            case "new-game":
                if (!SceneLoad.IsInMainMenuScene || Fate.CurrentFateVer != Fate.FateVer.Original)
                    throw new InvalidOperationException("Open the Original FATE / Grove main menu first.");
                if (SaveData.GetActiveSaveSlotForGame(Fate.FateVer.Original, false) != null)
                    throw new InvalidOperationException("A test character already exists; continue it from the game menu.");
                SaveData.CreateNewSaveSlot(new NewgameUI.CharacterCreation
                {
                    male = true, race = Race.Human, hairIndex = 0, headIndex = 0,
                    playerName = "Coop Test Host", petName = "Test Cat", petMonsterId = "Pet_Cat",
                    difficulty = Fate.Difficulty.Adventurer
                });
                MainMenuUI.NewGameRequested();
                break;
            case "continue":
                if (!SceneLoad.IsInMainMenuScene || Fate.CurrentFateVer != Fate.FateVer.Original || SaveData.CurrentSaveData?.playerSaveData == null)
                    throw new InvalidOperationException("Open Grove's menu with a test character first.");
                MainMenuUI.ContinueRequested();
                break;
            case "skip-story":
                if (SingletonBehaviour<StoryUI>.InstanceExists())
                    AccessTools.Method(typeof(StoryUI), "OnContinueButton").Invoke(SingletonBehaviour<StoryUI>.Instance, null);
                break;
            case "move":
            case "primary-move":
                if (parts.Length != 3) throw new ArgumentException("move <x> <z>");
                var target = parts[0] == "move" ? (Character?)actor : Player.PlayerChar;
                if (target == null) throw new InvalidOperationException("Requested actor does not exist.");
                if (target == actor) StopRoute();
                Move(target, new Vector3(float.Parse(parts[1], CultureInfo.InvariantCulture), 0, float.Parse(parts[2], CultureInfo.InvariantCulture)));
                break;
            case "quit": Despawn(); Application.Quit(); break;
            default: throw new ArgumentException("Unknown prototype command: " + parts[0]);
        }
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
            actor.Init(configuration, new ItemStorage { items = equipment });
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
            SetStatus("Guest spawned. F9: walking route. F8: despawn.");
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

    private void Move(Character character, Vector3 desired)
    {
        if (!NavMesh.SamplePosition(desired, out var hit, 10f, NavMesh.AllAreas))
            throw new InvalidOperationException("Destination is outside the navigation mesh.");
        var agent = character.GetComponent<NavMeshAgent>();
        var path = new NavMeshPath();
        if (!agent.CalculatePath(hit.position, path) || path.status != NavMeshPathStatus.PathComplete)
            throw new InvalidOperationException("Destination is unreachable.");
        character.SetDestination(hit.position.x, hit.position.z);
        Logger.LogInfo($"MOVE actor={character.GetInstanceID()} destination={hit.position}");
    }

    private void StartRoute()
    {
        if (actor == null) Spawn();
        StopRoute();
        route = StartCoroutine(WalkRoute());
    }

    private IEnumerator WalkRoute()
    {
        var guest = actor!;
        var host = primary!;
        var origin = guest.transform.position;
        var primaryOrigin = host.transform.position;
        var hostId = host.GetInstanceID();
        var peakGuestTravel = 0f;
        var peakHostTravel = 0f;
        SetStatus("Scripted client is walking the guest.");
        // A short out-and-back route is easier to verify than unconstrained wandering.
        var offsets = new[] { Vector3.right * 8f, Vector3.forward * 8f, Vector3.left * 8f, Vector3.zero };
        foreach (var offset in offsets)
        {
            if (guest == null || host == null) yield break;
            try { Move(guest, origin + offset); }
            catch (Exception e) { Logger.LogWarning("ROUTE_WAYPOINT_SKIPPED " + e.Message); continue; }
            var agent = guest.GetComponent<NavMeshAgent>();
            var deadline = Time.unscaledTime + 12f;
            do
            {
                yield return null;
                if (guest == null || host == null) yield break;
                peakGuestTravel = Math.Max(peakGuestTravel, Vector3.Distance(origin, guest.transform.position));
                peakHostTravel = Math.Max(peakHostTravel, Vector3.Distance(primaryOrigin, host.transform.position));
            } while (Time.unscaledTime < deadline && (agent.pathPending || (agent.hasPath && agent.remainingDistance > Math.Max(agent.stoppingDistance, 0.4f))));
        }
        guest.StopPathing();
        route = null;
        var result = new RouteResult { guestTravel = peakGuestTravel, primaryTravel = peakHostTravel, primaryIdentityPreserved = Player.PlayerChar != null && Player.PlayerChar.GetInstanceID() == hostId, aiCallsSuppressed = suppressedAI };
        File.WriteAllText(Path.Combine(controlRoot, "route-result.json"), JsonUtility.ToJson(result, true));
        Logger.LogInfo("ROUTE_RESULT " + JsonUtility.ToJson(result));
        SetStatus("Walking route complete. Check route-result.json for measured independence.");
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

    private IEnumerator TestPrimaryWalk(Vector3 direction)
    {
        // Test fixture: move the local primary through its real navigation agent, not a transform teleport.
        if (float.IsNaN(direction.x) || float.IsNaN(direction.z) || float.IsInfinity(direction.x) || float.IsInfinity(direction.z)) yield break;
        var host = Player.PlayerChar;
        var start = host.transform.position;
        var id = host.GetInstanceID();
        var agent = host.GetComponent<NavMeshAgent>();
        var end = Time.unscaledTime + 3f;
        while (Time.unscaledTime < end && host != null && Player.PlayerChar == host && !transitioning)
        {
            yield return new WaitForEndOfFrame();
            if (agent != null && agent.isOnNavMesh && !Fate.isPaused)
            {
                agent.Move(direction.normalized * 1.5f * Time.deltaTime);
                host.TurnTowardPosition(host.transform.position + direction);
            }
        }
        File.WriteAllText(Path.Combine(controlRoot, "primary-walk-result.json"), JsonUtility.ToJson(new RouteResult { primaryTravel = host != null ? Vector3.Distance(start, host.transform.position) : 0, primaryIdentityPreserved = Player.PlayerChar != null && Player.PlayerChar.GetInstanceID() == id }, true));
        testWalk = null;
    }

    private void Despawn(bool preserveIntent = false)
    {
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
        remoteSwings = remoteCasts = -1;
        SetStatus("Guest despawned.");
    }

    private void Snapshot()
    {
        if (string.IsNullOrEmpty(controlRoot)) return;
        var host = Player.PlayerChar;
        var agent = actor != null ? actor.GetComponent<NavMeshAgent>() : null;
        var animator = actor != null ? actor.GetComponent<Animator>() : null;
        var renderers = actor != null ? actor.GetComponentsInChildren<Renderer>(true) : Array.Empty<Renderer>();
        var visibleMeshes = renderers.OfType<SkinnedMeshRenderer>().Where(r => r.enabled && r.gameObject.activeInHierarchy && r.sharedMesh != null && r.sharedMesh.vertexCount > 0).ToArray();
        var telemetry = new Telemetry
        {
            time = Time.unscaledTime, scene = SceneManager.GetActiveScene().name, status = status,
            primaryId = host != null ? host.GetInstanceID() : 0, guestId = actor != null ? actor.GetInstanceID() : 0,
            primaryPosition = host != null ? host.transform.position : Vector3.zero,
            guestPosition = actor != null ? actor.transform.position : Vector3.zero,
            guestOnNavMesh = agent != null && agent.isOnNavMesh, guestVelocity = agent != null ? agent.velocity : Vector3.zero,
            guestWalking = animator != null && animator.GetBool("IsWalking"), spawnedTotal = spawnCount,
            aiCallsSuppressed = suppressedAI, saveLoaded = SaveData.HasLoaded,
            steamInitialized = SteamReady,
            campaign = Fate.CurrentFateVer.ToString(), renderers = renderers.Length,
            worldEpoch = worldEpoch, transitioning = transitioning, dungeonDepth = LevelGenerator.currentInstance != null ? LevelGenerator.currentInstance.GetLevelDepth() : 0,
            networkConnected = peerConnected, networkAccepted = networkAccepted, networkRejected = networkRejected,
            sessionRole = link == null ? "" : isHost ? "host" : "guest", localPort = link is LocalLink local ? local.Port : 0,
            worldReady = worldReady, paused = Fate.isPaused, primaryAlive = host != null && host.Alive(),
            layoutHash = layoutHash, worldKey = CurrentWorldKey(), sharedSession = sharedSession,
            localPetJson = JsonUtility.ToJson(LocalPetSnapshot()), remotePetId = remotePet != null ? remotePet.GetInstanceID() : 0,
            remotePetPosition = remotePet != null ? remotePet.transform.position : Vector3.zero,
            remotePetModel = remotePetModel, localPetCount = host != null ? host.GetPetCount() : 0,
            remotePetActors = Object.FindObjectsOfType<Character>().Count(c => c.name == "FateCoop_Remote_Pet"),
            remotePetMeshes = remotePet != null ? remotePet.GetComponentsInChildren<SkinnedMeshRenderer>().Count(r => r.enabled && r.isVisible) : 0,
            remoteName = actor != null ? actor.CharacterName : "", monstersTracked = monsters.Count, aggro = aggroMonster == null ? "" : $"{aggroMonster.GetAIState()} target={(aggroMonster.Target() != null ? aggroMonster.Target().name : "none")} distance={(actor != null ? Vector3.Distance(aggroMonster.transform.position, actor.transform.position) : -1f):0.0} range={aggroMonster.InAttackRange()} facing={aggroMonster.FacingTarget()} attacking={aggroMonster.GetComponent<Animator>().GetBool("IsAttacking")} key={aggroMonster.monsterData.displayName} attack={CurrentAttackField.GetValue(aggroMonster) != null} timer={aggroMonster.attackTimer:0.00} paused={Fate.isPaused}",
            trackedHp = Mathf.RoundToInt(monsters.Values.Where(m => m != null).Sum(m => m.hp)), trackedDead = monsters.Values.Count(m => m != null && m.MyVitals.IsDead),
            hitsSent = hitsSent, hitsApplied = hitsApplied, strikesSent = strikesSent, strikesTaken = strikesTaken, killsSent = killsSent, killsTaken = killsTaken,
            swingsPlayed = swingsPlayed, castsPlayed = castsPlayed, primaryHp = host != null ? host.hp : 0f, primaryExperience = host != null ? (long)host.experience : 0,
            primaryOnNavMesh = host != null && host.GetComponent<NavMeshAgent>() != null && host.GetComponent<NavMeshAgent>().isOnNavMesh,
            gameScene = SceneLoad.CurrentScene.ToString(),
            activeMeshes = visibleMeshes.Length, meshesSeenByCamera = visibleMeshes.Count(r => r.isVisible),
            prototypeActors = Object.FindObjectsOfType<PartyMember>().Count(p => p.name == "FateCoop_Prototype_Guest")
        };
        var json = JsonUtility.ToJson(telemetry);
        File.WriteAllText(Path.Combine(controlRoot, "status.json"), json);
        File.AppendAllText(Path.Combine(controlRoot, "telemetry.jsonl"), json + Environment.NewLine);
    }

    private void SetStatus(string text) { status = text; Logger.LogInfo(text); }

    private void OnGUI()
    {
        if (!enabled || !showPanel) return;
        if (actor != null && Camera.main != null)
        {
            var screenPoint = Camera.main.WorldToScreenPoint(actor.transform.position + Vector3.up * 2.4f);
            if (screenPoint.z > 0)
                GUI.Box(new Rect(screenPoint.x - 70, Screen.height - screenPoint.y - 12, 140, 24), mirroring ? actor.CharacterName : "CO-OP TEST GUEST");
        }
        if (remotePet != null && Camera.main != null) DrawEntityLabel(remotePet.transform.position, remotePet.CharacterName);
        GUILayout.BeginArea(new Rect(12, 12, 440, 215), GUI.skin.box);
        GUILayout.Label("FATE CO-OP | Grove | instance " + instance.ToUpperInvariant() + " | isolated saves");
        GUILayout.Label(status);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button(actor != null ? "Despawn (F8)" : "Spawn guest (F8)"))
            try { if (actor != null) Despawn(); else Spawn(); } catch (Exception e) { SetStatus(e.Message); }
        if (GUILayout.Button("Walk route (F9)"))
            try { StartRoute(); } catch (Exception e) { SetStatus(e.Message); }
        if (GUILayout.Button("Stop (F10)")) StopRoute();
        GUILayout.EndHorizontal();
        GUILayout.Label(!sharedSession ? "F11 hides this panel. Prototype test controls." : isHost ? "Shared combat on. Loot drops for the host only." : "Shared combat on. Pickups and chests are host-only for now.");
        GUILayout.Label(link == null ? "Not in co-op." : (isHost ? "Hosting co-op: " : "Joined co-op: ") + (peerConnected ? "partner connected" : "waiting for partner"));
        GUILayout.BeginHorizontal();
        if (link == null && SteamReady && GUILayout.Button("Host Steam co-op"))
            try { Execute("host-steam"); } catch (Exception e) { SetStatus(e.Message); }
        if (link is SteamLink { CanInvite: true } steam && GUILayout.Button("Invite friend")) steam.Invite();
        if (link != null && GUILayout.Button("Leave co-op")) EndSession("Left co-op.");
        GUILayout.EndHorizontal();
        GUILayout.EndArea();
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneLoad.OnSceneLoaded -= OnGameSceneLoaded;
        PlayerManager.OnPlayerSpawn -= OnPrimarySpawn;
        EndSession("Game closed.");
        lobbyInvites?.Dispose();
        LeaveSharedWorld();
        Despawn();
        harmony?.UnpatchSelf();
        runningLock?.Dispose();
        if (Active == this) Active = null;
    }

    [Serializable] private sealed class RouteResult
    {
        public float guestTravel, primaryTravel;
        public bool primaryIdentityPreserved;
        public int aiCallsSuppressed;
    }
    [Serializable] private sealed class Telemetry
    {
        public float time;
        public string scene = "", status = "", campaign = "", gameScene = "", layoutHash = "", worldKey = "", sessionRole = "";
        public int primaryId, guestId, spawnedTotal, aiCallsSuppressed, prototypeActors, renderers, activeMeshes, meshesSeenByCamera, worldEpoch, dungeonDepth, networkAccepted, networkRejected, localPort;
        public Vector3 primaryPosition, guestPosition, guestVelocity;
        public bool guestOnNavMesh, guestWalking, saveLoaded, steamInitialized, transitioning, networkConnected, worldReady, paused, primaryAlive, primaryOnNavMesh, sharedSession;
        public string localPetJson = "";
        public int remotePetId, remotePetActors, localPetCount, remotePetMeshes;
        public Vector3 remotePetPosition;
        public string remotePetModel = "";
        public string remoteName = "", aggro = "";
        public int monstersTracked, trackedHp, trackedDead, hitsSent, hitsApplied, strikesSent, strikesTaken, killsSent, killsTaken, swingsPlayed, castsPlayed;
        public float primaryHp;
        public long primaryExperience;
    }
}
