# Grove co-op prototype — 0.13.0

## Shared visuals, partner HUD and fishing

- **Weapon glow.** The replica is built with the partner's real equipment, so `EquipmentHandle.SetEquipment` adds the same elemental and unique particles. The profile now also carries `activeWeaponSet`, so a partner on their secondary set shows the right weapon. Changing set rebuilds the replica.
- **Effect particles.** Each frame lists the player's active effects that have a `PS_<name>` prefab, and the host's monster states list each monster's effects. The receiver adds or removes the same particles on the replica or monster copy with the game's own `AddPSEffect`/`RemovePSEffect`. This covers buffs, poison, freeze, potions and equipment auras, and it stays correct after a respawn or rejoin.
- **Damage numbers.** The host reports final damage on shared monsters (`pop`). The guest shows it through the game's own `ThatHurtTrigger.OnHurt` on its copy, so a guest's hits now show numbers.
- **Spells.** Each player's spell release (`CompleteSpell`: spell, aim point, target) is replayed by their replica with full visuals: cast glow, projectiles, impacts. It runs inside a visual-only scope. `AddEffect` gets a detached result, `RespondToAttack` and barrel breaking are skipped, camera shake is skipped, mana is topped up, and the master link is cleared during the call. Projectile impacts check whether their caster is a stand-in.
- **Arrow trails and level-ups.** A ranged trail's end point is sent and drawn from the replica's weapon. A level-up plays the game's level-up particle on the replica.
- **Fishing.** Fishing is local to each player (spots never run out, catches are personal), so both can fish at once. Each frame carries fishing, bite, pull and caught flags. The replica plays the matching animator states, holds a copy of the local player's fishing pole, hides its weapons, and plays the fishing sounds where it stands. A guest pulled to the host's next floor while fishing gets its fishing screen closed first (it used to stay stuck open).
- **Partner HUD.** Bottom-left: the partner's Steam name, level, where they are (Grove town, floor, travelling, down), health and mana with numbers, an XP bar, and their pet's name and health. The pet label in the world stays the pet's own name.

`Test-SharedVisuals.ps1` checks weapon glow (matching particle counts), a player aura and a monster aura, guest damage numbers, spells both ways, an arrow trail, a level-up, and both players fishing at once. All seven suites pass.

Not covered yet: monster spell visuals on the guest's copies (their damage arrives; the projectile isn't drawn), and Elite/Legendary rank auras on monsters the guest spawns from host updates.

# Grove co-op prototype — 0.12.0

## Co-op monster toughness

While the partner is on the host's floor, the host divides all damage to non-pet monsters by `MonsterToughness` (`[Co-op]` in `BepInEx\config\local.fatecoop.prototype.cfg`, default 1.75, minimum 1, read from the host's config). In effect monsters have 1.75× health. Nothing is stored: the saved floor keeps normal health, and solo play is unchanged. XP per kill is unchanged for each player. The reduction applies to every damage source on the host (both players, pets, forwarded hits and effects). Damage over time that bypasses `ApplyDamage` isn't reduced. Measured: a 10-damage effect took off 6 with the partner present.

# Grove co-op prototype — 0.11.0

## Second audit

- **Shared effects.** Each game treats `Character.AddEffect` on a stand-in (the partner's replica, its pet, or on the guest a host-owned monster) as a request. The effect goes to the game that owns the target and is applied there with the right source (`effect` message, `EffectData` as JSON). The stand-in gets a detached `AppliedEffect`, because spell code uses the return value. This covers:
  - the guest's poison, slows, stuns and weapon on-hit effects on monsters;
  - monster spells and damage over time on the guest;
  - heals and buffs either player casts on the other.
  
  Instant damage from an effect now happens only where the target lives, so nothing is counted twice.
- **No ghost items or monsters.** The host keeps scanning ground items and monsters while the guest is dead or between states, and sends removals right away. Before, an item picked up or a summon that expired while the guest was dead could stay visible on the guest.
- **No lost items in edge cases.**
  - A granted item that arrives after the guest left gameplay goes back to the host's ground.
  - A guest drop that has no valid spot is placed at the host's feet.
- **Quest item clean-up.** When the host turns in a party quest, the guest drops any copy of that quest's item from its bag.
- **FATE updates.**
  - `Setup-FateCoop.ps1` compares `version.txt` with the installed game. On a mismatch it rebuilds the co-op copy and removes the patcher's old assembly backups; co-op saves are kept.
  - `tools\Refresh-TestGames.ps1` does the same for the dev copies.
- **Smaller fixes.**
  - Line-of-sight checks for monsters targeting the replica use a compiled delegate instead of reflection.
  - The test harness (commands, routes, telemetry) moved from `Plugin.cs` to `DevTools.cs`.
  - The "Host Steam co-op" button calls `HostSteam()` directly.

`Test-SharedCombat.ps1` now also checks a guest effect landing on a host monster and a monster effect on the replica landing on the guest. All six suites pass.

# Grove co-op prototype — 0.10.0

## Session flow, party quests and audit fixes

- **Rejoining is safe.** When the host's link connects, it sends `resync`. The guest then forgets the host's floor (monster and item IDs, copies) and requests it again. On every `world-request` the host resends all monster states, ground items and chest/barrel states. Before this, a rejoin within the 10-second timeout, or a long freeze on one side, could leave the two sides with different IDs and duplicate monsters.
- **The guest moves freely.**
  - The guest stays in town when the host goes down, and is no longer pulled out of a fight when the host goes to town.
  - Entering the dungeon, or taking stairs alone, lands the guest on the host's current floor.
  - When the host goes to town, the guest keeps the floor solo: monster control is released and the shared ground copies are removed. The guest joins the host's next floor when the host comes back down.
  - On the guest, monster AI is frozen only for the host's monsters (or while joining the host's floor), so a floor that isn't shared plays normally.
- **Party quests.** The host's quests are the party's quests, and kills by either player advance them on the host.
  - The guest's panel lists them with their floor.
  - When the host turns one in, the guest gets the same gold, XP and fame, plus a copy of the reward item. If the guest's bag is full, the item drops for the guest alone, not as shared loot.
  - A guest picking up the host's quest item completes the host's quest.
  - On the host's floors the guest's own quest system is switched off. This stops the guest's find-item quest items leaking into shared loot, and its quest monsters being spawned and deleted. The guest's own quests stay in its save for solo play.
  - Escort and herd characters clicked on the guest are forwarded to the host.
- **Loot fixes.**
  - Locked chests use the guest's own key.
  - Gold the guest drops on death lands in the host's world.
  - Chest and barrel requests can be retried after 2 seconds.
  - Item drops are blocked while the partner's replica is built (insurance against duplicating their gear).
  - Guest drops, chests and barrels go to the host only while the guest is on the host's floor or in town with them.
- **No pausing in co-op.** While connected, nothing pauses the game, so menus, tutorial popups, portals and the death screen no longer freeze the shared world.
- **Partner vitals and Steam join.**
  - The partner's label shows red health and blue mana bars.
  - The host sets Steam rich presence `connect`, so friends can use **Join Game** from the friends list. `GameRichPresenceJoinRequested_t` and `+connect_lobby` both join.
- **Release clean-up.**
  - Test tooling (command files, `status.json`/`telemetry.jsonl`, F8–F10) only runs with `TestTools = true` in `BepInEx\config\local.fatecoop.prototype.cfg`. `Build-Prototype.ps1` sets it for both dev copies; the friend kit leaves it off.
  - The panel lost the old guest test buttons and stopped overwriting statuses.
  - The plugin is now named "FATE Co-Op".

Tests: `Test-SessionFlow.ps1` (quick rejoin with matching monster counts, guest stairs, guest staying in town, guest keeping the floor when the host leaves) and `Test-SharedQuests.ps1` (party quest list, shared reward on turn-in). Combat, loot, dungeon and movement tests pass. Tests now send both players into the dungeon and back to town.

# Grove co-op prototype — 0.9.0

## Shared loot

Ground loot is shared: first to grab it wins. The host holds the only real copy of every item and gold pile on the ground, in the dungeon and in Grove town.

- **Announcing items.** Four times a second the host scans dropped items and gold piles. Anything that has stayed put for 0.6 seconds (its drop bounce is over) gets an ID and is sent to the guest with its full `ItemSaveData` and position. Anything that disappears is announced as gone. The guest shows copies that sit outside the level's item list and the town's container parent, so the game never saves them and they can't be duplicated through a save. The floor snapshot the guest installs has its ground items and gold piles removed, so it shows only the host's announced copies.
- **Picking up.** On the guest, clicking an item, walking over gold, or a pet fetching an item sends a request. The host removes the real item and grants it, and the guest adds it to the bag or hotbar with the game's normal loot rules. If the bag is full, the item goes back to the host and is dropped where the guest stands. The host picks up normally, and the item vanishes for the guest.
- **Dropping for each other.** Every guest drop path (the inventory drop slot, gamepad drop, overflow re-drops) ends in `Item.Drop`. On the guest that call is sent to the host, which drops the item for real, so both players see it.
- **Chests and barrels.** A guest opening a chest or breaking a barrel asks the host to do it (matched by position), so contents, gold and traps come from the host's world. The host announces opened chests and broken barrels, and the guest shows them in that state.

`Test-SharedLoot.ps1` checks a guest drop showing for both players, a host pickup, a host drop and guest pickup with the combined bag count unchanged, and a guest chest and barrel. A guest-to-host trade in Grove town also kept both bags at 11 items in total. Combat, dungeon and movement tests still pass.

# Grove co-op prototype — 0.8.0

## Shared combat

The 0.6 test monster is gone. During a session every monster is real and shared:

- **The host owns all monsters.** It runs their AI, health and deaths. The floor snapshot now carries a monster ID for each saved monster. The host assigns IDs by matching its live monsters to the save it just wrote. The guest matches its loaded monsters by name and position, removes any it can't match, and spawns a model for monsters the host adds later (summons, curses, trapped chests). The host sends changed monster states (position, facing, health, walking, running, attacking, dead) 10 times a second.
- **The guest's hits go to the host.** On the guest, damage the player or its pets deal to a shared monster is sent to the host instead of being applied, and the host applies it with the partner's replica as the source. Misses and blocks show locally, as before.
- **Monsters hurt the guest.** Host monsters can target the guest's replica: target search includes it, and AI stays awake near it. A monster swing at the replica is forwarded, and the guest replays it with its own copy of that monster against its real player, so the guest's armor, block and resistances apply. Spell and effect damage on the replica is forwarded as raw damage and runs through the guest's own resistances. Damage over time on the replica isn't forwarded yet.
- **Both players get XP.** When a shared monster dies, the host sends its XP and fame, and the guest awards them to its own player (if alive) and plays the death. The host gets XP the normal way, or through the replica when the guest landed the kill.
- **Names and gear.** Each game sends a profile: its Steam name (or the character name if Steam isn't running), appearance and equipped items. The partner's replica is built from it, so it carries their name, look and weapon. It is rebuilt when their equipment changes. Labels show the partner's name and their pet's name.
- **Attack animations.** Swings (Player.StartAttack) and casts (Character.Cast) are counted into the frame. The replica plays the matching clip, and its own strike or spell at the end of the animation is suppressed.

`Test-SharedCombat.ps1` checks matching monsters and health totals, a guest hit, a guest kill and a host kill with XP for both players, a monster swing at the guest, and replica swings in both directions with no missing clip. Test commands: `test-hit` (local player rolls an attack on the nearest enemy), `test-swing`, `test-aggro` / `test-calm` (host sends one monster with high health at the replica, then restores it). 0.8 acceptance passed together with `Test-SharedDungeon.ps1` and `Test-TwoPlayerMovement.ps1`. Repeated monster strikes took the guest from 122 to 72 HP while the host stayed at 122.

## Steam lobbies and the direct game link

The games now talk to each other directly. The outside relay, the single-game debug client and its TCP command port are gone. Each game sends a frame 20 times a second (world key, scene, player pose, pet, host combat state or guest attack intent). The guest follows the host: when the host is on a dungeon floor the guest doesn't have, the guest sends `world-request` and the host replies with its floor snapshot as a message. When the host returns to Grove, the guest goes back to town. Host and guest are session roles, not folders A and B, so a friend's `test-game` can join as the guest.

The same messages travel over two links. The local link is a loopback TCP connection from the joining game straight to the host game, for one-PC testing. The Steam link is a friends-only, two-player Steam lobby plus `SteamNetworkingMessages`; the host only accepts network sessions from the lobby member who joined. Messages carry the mod version, and a game drops the session if its partner runs a different version. A partner who stops sending for 10 seconds is dropped, which removes their player and pet and lifts the test protections.

- **Steam:** start FATE from Steam (its launch option runs `test-game`), continue a character, press **Host Steam co-op** on the panel, then **Invite friend** or invite from the Shift+Tab overlay. Accepting the invite joins the lobby, whether the friend's game is already running or Steam launches it with `+connect_lobby`. **Leave co-op** ends the session.
- **One PC:** `Start-TwoGames.ps1` launches both copies, opens Grove and runs `Connect-TwoGames.ps1`, which sends `host-local` to A and `join-local <port>` to B. Send `leave` to either game to disconnect.

**Friend kit:** `tools\Build-FriendKit.ps1` rebuilds the mod and writes `artifacts\FateCoop-Kit.zip` (about 33 MB): BepInEx, the plugin, the sandbox patcher and a portable .NET 8 runtime. It contains no game files. The friend extracts it outside OneDrive and runs `Setup-FateCoop.cmd`, which copies their own Steam install into `test-game`, patches saves into the kit folder, installs the mod and copies the Steam launch option to the clipboard. Both games must run the same mod and game version (`Application.version`); a mismatch ends the session with both versions shown. If the plugin can't confirm save isolation at startup, it quits the game, because a Steam launch skips `Start-TestGame.ps1`'s hash check.

Command files also accept `host-steam`, `join-steam <lobby id>`, `invite` and `leave`. `status.json` reports `sessionRole`, `localPort` and `networkConnected`.

0.7 acceptance on the local link passed: two-player movement (A 4.52, B 3.01 units, replicas within 0.25 units, no rejected messages); shared floors 1–3 and back to Grove; shared combat with both players and the guest pet; leaving removed the remote player and pet on both sides, and rejoining restored them on the same floor. On Steam, a game started from Steam created the lobby and opened the invite dialog. Joining needs a second Steam account and hasn't been tested yet.

## Remote pets and one shared monster (0.6, replaced by shared combat)

Both games now display the other player's actual pet model, position and facing. The replica belongs to the remote guest and is never inserted into the primary player's pet/save list. Its AI and collision are disabled; the owning game's real pet continues its native behavior. Town/floor transitions and disconnects remove replicas before rebuilding the shared world.

In a connected dungeon, use A's **Start / reset shared monster test** button. Click the marked monster normally in either game. A owns its AI and health; B sends attack intents and renders the confirmed state. This fixture gives accepted nearby player/pet hits a fixed 10 damage, with an 80-HP starting monster and a per-source cooldown. Repeated intent IDs are deduplicated; wrong-world/target and distant attacks are rejected. Both games apply one confirmed death. The test monster is ephemeral and has drops disabled. Players, pets and other enemies remain protected.

`Test-SharedCombat.ps1` checks contributions from both players and the joining pet, matching health, one death and pet/guest counts. A normal mouse attack in B was also verified to reduce the same host-owned monster's health in both games. Pet combat animations, pet health/inventory, character-stat damage, spells, player damage, shared XP/quests and loot are not implemented. Native quest callbacks on the host are not yet replaced by a shared quest system; use disposable characters.

Pet/combat DTOs use separately serialized JSON strings because Unity omitted nested plugin DTOs in live testing.

0.6 acceptance passed: combat health/death checks; independent navigation and matching layouts on floors 1–3; one local and one remote pet in each game across all transitions; zero remote pets after disconnect and exactly one after rejoin. Pet replica position errors after settling were 0.063 and 0.001 units. The movement telemetry verifier passed both directions without duplicate guests, and original save/assembly hashes remained unchanged. Reports are in `artifacts/shared-combat-verification.json`, `shared-dungeon-verification.json`, `pet-disconnect-verification.json`, `pet-rejoin-verification.json`, and `runtime/verification.json`.

## Shared dungeon floor prototype

Instance A now owns floor selection. B follows A into the Grove dungeon, down floors and back to town. The host exports `DungeonSaveData` and sends it to B, which installs the record immediately before the game's saved-floor loader and retains its own player/inventory/pet records. Both instances hash maze cell data and staircase positions. Movement replication resumes only when their verified world identities match.

Importing real characters is not implemented. During a connected session, unrelated monster AI is stopped and ground/container pickups are disabled. Disconnect releases the test protections and removes guests and remote pets. Items dropped manually are still local; shared drops, XP and quest rewards are future work.

To test: use `Start-TwoGames.ps1`, keep A as the host, enter the Grove dungeon in A, and let B follow. Only A selects floors for the shared session. `Test-SharedDungeon.ps1` automates matching floor 1, independent movement, floors 2 and 3, and return to town. Its tests use disposable saves. `Open-TestGrove.ps1` returns test characters to town before connecting.

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

Switch between windows and move normally. The top panel identifies A or B; the guest label identifies the remote player. A selects the shared floor; B follows its verified snapshot.

If both games are already open, use `.\tools\Connect-TwoGames.ps1`. `Test-TwoPlayerMovement.ps1` requires a connected pair and moves each primary through its navigation agent, then checks both replicas, unchanged primary identities and one guest per instance. This fixture complements manual mouse/keyboard testing.

The final 0.4 acceptance run passed: A moved 4.51 units, B moved 3.01 units, both guests matched the opposite primary within 0.25 units after settling, primary identities stayed intact, one guest appeared per game and no requests were rejected. Stopping the relay removed both guests. The combined telemetry check observed walking animations, independent movement in both directions and rendered meshes without duplicate actors. Original save and assembly hashes matched their recorded values. Builds passed with zero warnings/errors; protocol checks covered 6 valid and 12 invalid frames.

Reports: `artifacts/two-player-verification.json` and each instance's runtime folder. Avoid F9 scripted guest routes while connected; the session owns the guest.

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

`Test-Lifecycle.ps1` tests three spawn/despawn cycles, Grove floor 1, floor 2 and return to town. It temporarily protects the test host from damage. Run only on disposable test characters. `test-resume` dismisses tutorial prompts and resumes the sandbox for testing; normal guest respawning preserves pause state.

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
