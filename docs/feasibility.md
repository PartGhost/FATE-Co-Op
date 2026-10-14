# Second-character feasibility assessment

Inspected October 8, 2026. Verdict: a separate humanoid actor with independent movement is strongly supported by the code. A second fully independent player is not established; runtime verification is still required. The existing PartyMember is the smallest practical starting point.

## Inspected installation

- Installation: `C:\Program Files (x86)\Steam\steamapps\common\FATE Reawakened`
- Game version: `1.1.3-97fd91e7`; Steam build ID: `25478876`.
- Unity: `2022.3.62f2` from existing Player.log.
- Backend: Mono runtime present, with managed `Assembly-CSharp.dll` and `Assembly-CSharp-firstpass.dll`.
- Assembly-CSharp SHA256: `4590187091E0E8F064759CE9BE534EBAC862281744A19BDB030B8293DAE5F72B`.
- No BepInEx directory or loader configuration found in the installation root.

Evidence links below refer to local ILSpy output, which is intentionally excluded from source control. Decompiler output is sufficient for architecture inspection, but prefab wiring and actual behavior need a running game.

## Why another Player is unsafe as the first experiment

`PlayerManager.RequestPlayerSpawn` maintains one `activePlayer`; subsequent requests teleport it, or destroy and replace it when forceRespawn is true. It does not create an additional player. See `research/Tableflip.PlayerManager.cs:22`.

`Player.Awake` calls Init immediately. `Player.Init` overwrites static `Player.PlayerChar`, loads the current save, registers FlushPlayerSave, and spawns pets/summons/party members. The appearance overload also reads the global save despite accepting a PlayerSaveData argument. See `research/Tableflip.Player.cs:183`, `:233`, `:307`.

`ProdigyPlayer` collects input through one singleton InputBroker. `ProdigyAgentLocomotion.Motion` rotates Player.PlayerChar rather than its own character in two branches. Cloning the live player and restoring the static reference afterward would not undo initialization side effects. See `research/Cleverous.Prodigy.ProdigyPlayer.cs:84` and `research/Cleverous.Prodigy.ProdigyAgentLocomotion.cs:49`.

Networking-looking names are not evidence of implemented multiplayer: `ProdigyPlayer.RpcInputToClients` has an empty body. Do not assume the Prodigy classes supply a usable transport.

## The useful existing second-character system

`PartyMember : Character` accepts explicit PlayerSaveData and ItemStorage, applies appearance, initializes individual attributes, XP, inventory, spells, health and navigation. It does not assign Player.PlayerChar. Its inventory grid is 10 by 4. See `research/Tableflip.PartyMember.cs:95` and `research/Tableflip.Character.cs:759`.

`FateDB.partyMemberPrefab` is available in the managed API. `Player.SummonPartyMember` instantiates it, assigns a master, initializes it and places it on the world navigation mesh. This factory explicitly returns null outside Cursed King. Start there; prefab availability and behavior in the other three campaigns are unverified. See `research/Tableflip.FateDB.cs:156` and `research/Tableflip.Player.cs:1553`.

`Character.SetDestination(float x, float z)` drives the actor's own NavMeshAgent and walking/running animation. Independent navigation is already implemented. However, PartyMember.UpdateAI chooses destinations and changes Inactive back to Wander. Merely setting Inactive will not disable companion AI. A Harmony prefix scoped to the designated prototype actor can bypass UpdateAI while retaining Character.Update and its pathing/timers. This is an implementation hypothesis, not a tested result. See `research/Tableflip.Character.cs:4665`, `:4274` and `research/Tableflip.PartyMember.cs:436`.

This actor remains a companion for game rules. Its merchant access, knockout behavior, XP sharing and inventory ownership are not equivalent to a standalone player's. Movement success would not prove independent progression.

## Other systems identified

| System | Main types | Implication |
|---|---|---|
| Monsters/combat | Monster, Character, CharacterVitals | Existing actor-based targets and damage; some special monster logic reads PlayerChar. Monster distance throttling also uses the primary player. |
| Dungeons | LevelGenerator, ActiveLevel, DungeonSaveData | Generation supports seed/currentSeed and serialized Random.State plus template/depth/realm. Saved monster and item state matters; sharing a seed alone does not establish world synchronization. |
| Player/progression | Player, PlayerStatus, PlayerSaveData | One global player/status; gold, UI, quests and scene changes require later ownership work. |
| Persistence | SaveData, FateSaveData, SaveManagerStandalone | FateSaveData contains one primary player plus party saves. Player.FlushPlayerSave serializes the party, so a test actor must not silently become permanent save content. |

Standalone Windows backend writes serialized JSON wrapped in a binary header (magic/version/length) to `Application.persistentDataPath/<fileName>.save`. SaveData uses `FateMainSave`. The existing file was located at `%USERPROFILE%\AppData\LocalLow\Tableflip\FATE Reawakened\FateMainSave.save`; its contents were not opened or modified. See `research/Tivola.Framework.SaveManagerStandalone.cs` and `research/Tableflip.SaveData.cs:65`.

Town and dungeon both have periodic-save routines. A copied executable still targets the same persistentDataPath. Before launch, redirect the standalone backend's GetFilePath to per-instance workspace test saves, verify load and write paths, and account for Steam Cloud and any other persistent settings/stat writes. Keep a backup and record hashes before and after runtime tests. No save isolation has been installed yet.

## Smallest prototype to implement next

1. Prepare an isolated test game copy, BepInEx 6 Mono loader, and early save-path redirection. Use a fresh throwaway Cursed King character. Confirm writes stay in the test directory before spawning anything.
2. Add a spawn/despawn hotkey. Instantiate the partyMemberPrefab once using a deep-copied character configuration and separate empty ItemStorage. Give it a clear test name and validate NavMesh placement. Preserve the primary player reference and camera.
3. Mark only this actor as externally controlled. Suppress its PartyMember.UpdateAI while alive for this movement-only test. Feed destination commands from a simulated client using a scripted route; later add a separate click modifier or loopback sender. Invoke Unity APIs on the main thread.
4. Log actor identity, positions, NavMesh status, primary-player identity, spawn count and exceptions. Despawn on scene transition until lifecycle support is implemented.
5. Once independent movement is proven, add a Steam-independent loopback transport and explicit actor IDs. A simulated sender tests command delivery, but two game instances are needed to prove bidirectional visible synchronization. Use a fixed town scene first to avoid dungeon replication during this milestone.

Acceptance: exactly two visible humanoids; the second can move while the first stands still and vice versa; walking animation and navigation work; companion AI does not pull it back; primary player/camera stay attached; repeated spawn/despawn leaves no duplicates or exceptions; original save hash is unchanged and all test writes are isolated.

No combat, loot sharing, dungeon replication or permanent progression changes belong in this first test. Networking and second-instance support have not been tested. The separate development workspace and inspection tooling are prepared; no gameplay implementation was made during this assessment.

## Existing mod investigation

[FateTweaks source](https://github.com/Drahsid/FateTweaks) confirms a BepInEx 6 Unity Mono plugin with Harmony patches. Its README reports testing an older build (#733), so it is a pattern reference, not compatibility proof for this installation. Its project expects local publicized game assemblies.

[FATE Reawakened Trainer author page](https://www.nexusmods.com/fatereawakened/mods/9?tab=description) provides a Steam-only trainer and bundled loader instructions. This assessment examined the public page, not its binary or private implementation; it does not establish a character-spawning API. No existing mod was installed.
