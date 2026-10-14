# Grove co-op prototype — 0.6.0

## Remote pets and one shared monster

Both games now display the other player's actual pet model, position and facing. The replica belongs to the remote guest and is never inserted into the primary player's pet/save list. Its AI and collision are disabled; the owning game's real pet continues its native behavior. Town/floor transitions and disconnects remove replicas before rebuilding the shared world.

In a connected dungeon, use A's **Start / reset shared monster test** button. Click the marked monster normally in either game. A owns its AI and health; B sends attack intents and renders the confirmed state. This fixture gives accepted nearby player/pet hits a fixed 10 damage, with an 80-HP starting monster and a per-source cooldown. Repeated intent IDs are deduplicated; wrong-world/target and distant attacks are rejected. Both games apply one confirmed death. The test monster is ephemeral and has drops disabled. Players, pets and other enemies remain protected.

`Test-SharedCombat.ps1` checks contributions from both players and the joining pet, matching health, one death and pet/guest counts. A normal mouse attack in B was also verified to reduce the same host-owned monster's health in both games. Pet combat animations, pet health/inventory, character-stat damage, spells, player damage, shared XP/quests and loot are not implemented. Native quest callbacks on the host are not yet replaced by a shared quest system; use disposable characters.

Peer entity snapshots currently travel through checksum-verified, bounded workspace files alongside the localhost TCP control channel. Pet/combat DTOs use separately serialized JSON strings because Unity omitted nested plugin DTOs in live testing. Second-PC transport and Steam joining remain future work.

0.6 acceptance passed: combat health/death checks; independent navigation and matching layouts on floors 1–3; one local and one remote pet in each game across all transitions; zero remote pets after disconnect and exactly one after rejoin. Pet replica position errors after settling were 0.063 and 0.001 units. The movement telemetry verifier passed both directions without duplicate guests, and original save/assembly hashes remained unchanged. Reports are in `artifacts/shared-combat-verification.json`, `shared-dungeon-verification.json`, `pet-disconnect-verification.json`, `pet-rejoin-verification.json`, and `runtime/verification.json`.

## Shared dungeon floor prototype

Instance A now owns floor selection. B follows A into the Grove dungeon, down floors and back to town. The host exports `DungeonSaveData`; the local relay copies its serialized snapshot into B's runtime directory and sends a checksum-verified join request. B installs the record immediately before the game's saved-floor loader and retains its own player/inventory/pet records. Both instances hash maze cell data and staircase positions. Movement replication resumes only when their verified world identities match.

World snapshot delivery currently uses workspace files, so second-PC joining requires a bounded network snapshot transfer. Steam invitation handling and importing real characters are not implemented. During a connected session, unrelated monster AI is stopped and ground/container pickups are disabled. Disconnect releases the test protections and removes guests and remote pets. Items dropped manually are still local; shared drops, XP and quest rewards are future work.

To test: use `Start-TwoGames.ps1`, keep A as the host, enter the Grove dungeon in A, and let B follow. Only A selects floors for the shared session. `Test-SharedDungeon.ps1` automates matching floor 1, independent movement, floors 2 and 3, and return to town. Its tests use disposable saves. Existing `Open-TestGrove.ps1` returns test characters to town before starting a new relay.

Acceptance passed: both layouts matched on floors 1, 2 and a newly generated floor 3; both characters moved over 4.5 units independently on floor 1 and their replicas converged; one guest appeared per instance; both returned to Grove. Joining a host already on floor 1 and disconnect/rejoin on that floor also passed. See `artifacts/shared-dungeon-verification.json` and `artifacts/shared-floor-rejoin-verification.json`. Original save and assembly hashes remained unchanged. Builds passed without warnings/errors; parser checks covered 11 valid and 15 invalid requests. Primary identity is checked within each loaded world; the game legitimately recreates its primary character during floor changes.

Agreed gameplay rules and code findings are in [co-op-foundation.md](co-op-foundation.md).

## Two game instances

0.4 established two real game processes through a local TCP relay in Grove town. Each retains its own primary player, camera, normal controls, save folder and settings prefix. Its guest represents the other instance's primary player, with interpolated position and facing. 0.5 added verified dungeon layouts; 0.6 adds pets and one shared monster. Remote player appearance currently uses the test guest configuration rather than the peer's equipment.

First preparation copies the disposable game to `test-game-b` and seeds B once from A's disposable save. Subsequent saves are separate: `test-saves/instance-a` and `test-saves/instance-b`. Original saves are never used as the seed. Unity's single-instance boot setting is removed in both disposable copies.

Prepare/build with both games closed:

```powershell
.\tools\Prepare-SecondInstance.ps1
```

Launch both games, continue their test characters in Grove and connect them:

```powershell
.\tools\Start-TwoGames.ps1
```

Switch between windows and move normally. The top panel identifies A or B; the guest label identifies the remote player. A selects the shared floor; B follows its verified snapshot. Ctrl+C stops the relay and removes reachable guests. A broken socket removes that instance's remote guest. Only localhost connections are supported.

If both games are already open, use `.\tools\Start-TwoPlayerRelay.ps1`. Add `-Background` to run a hidden helper; `Stop-TwoPlayerRelay.ps1` requests graceful shutdown. `Test-TwoPlayerMovement.ps1` requires an active relay and moves each primary through its navigation agent, then checks both replicas, unchanged primary identities and one guest per instance. This fixture complements manual mouse/keyboard testing.

The final 0.4 acceptance run passed: A moved 4.51 units, B moved 3.01 units, both guests matched the opposite primary within 0.25 units after settling, primary identities stayed intact, one guest appeared per game and no requests were rejected. Stopping the relay removed both guests. The combined telemetry check observed walking animations, independent movement in both directions and rendered meshes without duplicate actors. Original save and assembly hashes matched their recorded values. Builds passed with zero warnings/errors; protocol checks covered 6 valid and 12 invalid frames.

Reports: `artifacts/pair-result.json`, `artifacts/pair-telemetry.jsonl`, `artifacts/two-player-verification.json`, and each instance's runtime folder. Avoid F9 scripted guest routes while the relay owns the guest.

The requested target is Original FATE, starting in Grove. Cross-expansion dungeons and quests are deferred until co-op works here.

## What this build does

- Spawns a separate humanoid from the existing PartyMember prefab in Original FATE.
- Keeps the primary player reference and camera ownership intact.
- Disables companion decision-making only on the marked guest actor.
- Accepts separate guest destination commands and can run a scripted walking route.
- Applies the guest's gender/appearance explicitly and displays a guest label.
- Logs renderer visibility, navigation, animation, both characters' positions and actor counts.
- Despawns the guest before scene/floor transitions and respawns after the world is ready. The guest is not added to the saved party list.
- Accepts movement over TCP from a separate console client bound to localhost, using a per-run session token and world epoch.

The earlier single-game console client remains available for debugging. Combat synchronization is limited to the marked test monster; saved co-op progression is not implemented. F9 is an automated movement test. Internet connections and Steam joining are not implemented.

## Run and controls

From PowerShell in this workspace:

```powershell
.\tools\Build-Prototype.ps1
.\tools\Start-TestGame.ps1
```

Choose Original FATE / Grove in the test game and create or continue a disposable character.

- F8: spawn/despawn the guest.
- F9: run the guest walking route.
- F10: stop the guest.
- F11: hide/show the test panel.

Runtime logs and measurements are in `artifacts/runtime`. A command file can control the simulated client without injecting game keyboard input:

```powershell
.\tools\Send-PrototypeCommand.ps1 'spawn'
.\tools\Send-PrototypeCommand.ps1 'move 10 5'
.\tools\Send-PrototypeCommand.ps1 'route'
.\tools\Verify-Prototype.ps1
```

Open another PowerShell window in the workspace and run `.\tools\Start-LocalClient.ps1`. Commands: `spawn`, `step 5 0`, `step 0 5`, `move X Z`, `stop`, `status`, `despawn`, and `exit`. Destinations use world X/Z coordinates. The client refreshes its world epoch before commands; destinations without a complete navigation path are rejected. Disconnecting stops the guest.

Automated checks: `Start-LocalClient.ps1 -Probe` checks invalid protocol requests; `-Demo` measures independent guest movement; `-DisconnectTest` closes during movement. `Test-Lifecycle.ps1` tests three spawn/despawn cycles, Grove floor 1, floor 2 and return to town. It temporarily protects the test host from damage. Run only on disposable test characters. `test-resume` dismisses tutorial prompts and resumes the sandbox for testing; normal guest respawning preserves pause state.

## Verified 0.3.0 results

The lifecycle suite passed with exactly one guest on the navigation mesh after each transition. The separate TCP client moved the guest 2.14 units while the host moved zero units and retained its identity. Live protocol rejection checks passed. After abrupt client disconnection, the guest stopped and its position remained unchanged across subsequent samples. Original game assembly and save hashes remained unchanged. Builds passed with zero warnings/errors; parser tests covered 5 valid and 10 invalid frames.

The combined telemetry verifier still requires a fresh primary-player movement interval with the guest stationary. A file-injected primary destination was overridden by the game's normal input logic, so that reverse check remains for manual gameplay in this build; the earlier 0.2 run verified it. The new TCP guest movement and lifecycle checks above passed independently.

`menu` opens the Grove main menu; `new-game` creates an Original FATE test character only when none exists; `continue` resumes that campaign's current test character. Use these only before gameplay. `skip-story`, `status`, `save`, `stop`, `despawn`, and `quit` are also available.

## Safety and verification

Only the test copy's two game-owned managed assemblies are patched. The original game assembly and save SHA256 hashes are recorded and compared by Verify-Prototype. The original save backup is in `artifacts/save-backup`.

The save root is fixed inside the workspace before launch, independent of plugin loading. The plugin checks the actual save backend path at startup as well. Test saves, settings and previous Cursed King test characters are confined to `test-saves/instance-a`; they are not the user's original characters. Game-owned PlayerPrefs keys use a test prefix; Steam is enabled in the disposable copy for invites and networking; achievements count normally. The game never calls Steam Cloud directly, and the redirected saves sit outside Steam's synced folders. `Start-TestGame.ps1` supplies the app ID from Steam's manifest, and `status.json` reports `steamInitialized`. Do not launch the copied executable after replacing its patched assemblies with originals; use the guarded launcher.

Runtime verification distinguishes a renderer seen by any camera from user-confirmed visibility in the main game view. The user confirmed the guest is visible in Grove on October 8, 2026, and a game screenshot also showed the guest label and model. The 0.2.0 build compiled with no warnings or errors.

The completed Grove route recorded 9.28 units of guest displacement and preserved the primary player's identity. The primary also moved during that run, so this route alone is not a stationary-host test. Verify-Prototype separately checks telemetry intervals where one character moves while the other stays still. Original save and Assembly-CSharp hashes matched the pre-test values after the run.

The earlier Cursed King build was not accepted as visibly working by the user, despite spawn/movement logs. Do not treat that run as proof of a successful player-visible experience.
