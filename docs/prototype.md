# Grove co-op prototype — 0.21.0

Fixes from a full audit of the mod, the installer and the test tools.

- **Saves stay valid without the mod.** A Sagestone is saved as its source Ruby (`baseName`) with its own key kept in `name`; `Item.ApplyInstance` turns it back on load. Without the mod it loads as a Ruby, where before a socketed Sagestone threw while loading the item it sat in. Network item messages go through the same calls.
- **Installer.** Install unpacks into a staging folder in the game folder and only then replaces the old version, so a broken download or a full disk leaves the previous install working. Files that existed before FATE Co-Op (an existing BepInEx) are kept and never listed as ours. Remove deletes only listed files and the plugin's config, and removes BepInEx only if FATE Co-Op installed it and no other plugins or patchers remain. Before removing it reads the save and warns when the Grove has more than 3 side quests (the game's quest log has 3 rows and stops opening) or when Sagestones are still in the old format. Network and download errors get plain messages and a Try again button, the window can't be closed mid-install, and an empty save folder isn't reported as backed up.
- **Revive while alone.** The bleed-out and "no friend left" checks run even when the last friend has dropped, so a downed host can't stay down forever. Held travel also replays when nobody is left.
- **Session messages.** Only the host's `welcome` and `left` are honored, unreadable packets are rejected instead of breaking the update, owner-only messages (monsters, strikes, hurts, kills, damage numbers, ground, fixtures) are taken only from the floor owner and quest lists and rewards only from the host. A player welcomed again after a timeout resends their profile and quests, so they don't stay invisible. Ending a session mid-load clears the half-finished join, so solo play owns its floor. Leaving no longer pops each friend's bare name.
- **Ground items.** An owner who leaves while friends stay removes the items it already announced (the next owner turns its copies into the real ones), and an owner who yields sends its items to the winner instead of deleting them, so floor and town items are neither duplicated nor lost. Gold piles are detached before they're destroyed so a same-frame save can't keep them. Picking something up removes it from everyone's screen at once, a grant isn't world-scoped (a pickup while stepping onto stairs still arrives), and a drop that reaches an owner who already left bounces back to the sender's bag.
- **Chest keys** are spent when the owner opens the chest, not when the guest asks.
- **Stand-ins.** Swing, cast, arrow, level-up and spell counters are saved before playing them, and pets and summons are applied last, so one unknown spell or pet model can't stop a friend's stand-in from updating (or from showing them down). A mirror monster with no navmesh nearby is skipped for that update instead of dropping the rest of it.
- **Combat.** Party spells reach friends and count for their screens only when the cast would succeed (caster up, enough mana and stamina). Shift no longer offers pet-only spells. An expiring enemy summon gives no XP. A friend's hit that the game redirects to the stand-in's master (the local player) goes back to the friend.
- **Quests.** A guest who claims a quest locally skips the host's reward broadcast for it, and the host sends rewards to everyone, so two guests turning in at once are each paid once.
- **Startup.** A failure in `Plugin.Awake` removes every patch; each startup asset step (portal, dungeon props, Sagestones) fails on its own; borrowing town NPCs resets its flags in a `finally` and doesn't retry a town every Grove visit.
- **Bag and stash sort.** Items the stash sort drops are removed from the stash's list after the game's loop, which iterates that same list (the 0.20 fix still removed them mid-loop, and so does the game).
- **Menus.** The Escape menu's "Go to" buttons are rebuilt with the menu, so a rebuilt menu after a scene change doesn't throw every frame. They sit in a scrolling box of 4 rows (mouse wheel, or it follows the keyboard and gamepad selection) instead of growing the menu off screen with many friends, and read "Go to Floor 9 (name)" on one line so a long name is trimmed instead of the floor. The quest log and this box share one scroll list helper.
- **Party portraits with many friends.** The row gap shrinks with the portraits and the fit is measured against the space below the party panel, including the name plate under the last row, so 15 friends fit on screen (the gap used to stay full size and push the last rows off the bottom). Dev command `test-fake-friends <n>` adds n friends on other floors for layout checks, `test-goto-scroll` selects the last "Go to" button.
# Grove co-op prototype — 0.20.0

Fixes and requests from the 0.19 playtest.

- **Borrowed town scenes stay inert.** While Town_Ekbatan and Town_Temple_Fate2 load to borrow NPCs, every `Awake` and `OnEnable` declared in the game's two assemblies is skipped for objects in those scenes (one prefix patched onto all of them at startup, replacing the five hand-picked skips). The NPCs start up normally when they are copied into the Grove.
- **Town NPC icons and rings.** `MiniMapIconUI.Awake` divides an icon's scale by its parent's; the copied service icon gets its pre-Awake scale so it ends up Grove-sized (the borrowed merchants' icons were divided twice). Borrowed NPCs' `P_Marker_NPC` ring sits at their feet like the Grove NPCs' (it was under the ground, which the stencil marker treats as hidden and always draws), and the service NPCs stand on the navmesh height.
- **Skill reset.** Sakina the Priestess, next to Xerxes, refunds every spent skill point for 100 gold each. Stat and skill resets share one path: a table of starting values, a word for the prompts and the refund call.
- **Pets never die.** `CharacterVitals.Die` is skipped for `Pet`, which keeps at least 1 HP as `ModifyHP` intends. A pet flagged dead ignored heals and, through the 0.17 dead-monster state guard, could never leave its flee state or go to town.
- **Pet portraits.** Each player sends the name of the portrait texture their own game picked for their pet (shadowed when transformed), and the others look it up in `FateDB.PetProfilePictures`. A transformed pet (a Strider turned Flaming Mustang, Horse family) used to show nothing because only original pet families have portraits.
- **Friends on the map** use the pet minimap sprite (`Player.petMinimapIcon`) in their player color, set when their stand-in spawns, in town and in the dungeon.
- **Party spell effects for the caster.** A party spell plays its impact on every friend on the floor, not only on the caster's own party. Friendly single-target spells (`Spell_MinorHealTarget`, `Spell_HealingWardHeal`) target alignment All, which the game's cast check never accepts for a real character, so friends heal each other with Shift or party spells.
- **Spell buffs last at least 150 s.** Positive, timed spell effects on anyone who isn't evil. Health, mana and stamina over time, summons and transformations keep their length.
- **Portal back face.** At startup `FateDB.portalPrefab` becomes a copy with a second `SM_Portal` turned 180 degrees; `PortalBackFace` copies the front's transform and blend shape weights every frame.
- **Crystals, gold carts and campfires.** Dungeon props are matched by mesh, so recolored versions count. `SM_CrystalCluster` and `SM_VulcanoCrystal_1/2` props become breakable containers (urn shards in the crystal's material, a `PS_BoulderBreak` burst and the BoulderImpact sound, a 35% chance of a gem from the floor's gem table). Props with `SM_MineCartGold_01` become chests holding 2 to 6 times the item depth in gold that show an empty cart once opened (the plain `P_Cart` is left alone). `SM_CampfirePot` props get a `FountainInteractable` that restores 50% health and mana and puts the fire out (reach 2.5). The containers have reserved container items and prefabs registered in `FateDB`, so the floor's own container save keeps their state; after a floor is created or loaded, a prop with no saved container at its spot is turned into one. Co-op reuses the chest and barrel sync. Campfires, like fountains, are used per player.
- **Sagestone.** Five tiers copied from the Ruby family (same depths and rarity) and hue-shifted to magenta, giving 2, 4, 6, 8 or 10% more experience through a named passive effect that `AwardExperience` reads. Items carrying one say "N% Experience".
- **Bag and stash sort.** The game's sort releases every item's grid cells, re-places them, and drops what no longer fits, but the drop releases the item's old cells a second time, and those may already hold re-placed items; later pickups then land on top of them. During a sort, dropped items no longer release cells again.
- `Test-DungeonFixtures.ps1`: a guest breaks a crystal, opens a gold cart and rests at a campfire on the host's floor; friend map icons and colors; the host pet's portrait; a party heal shown for the caster. `Test-SoloContent.ps1`: skill reset, the pet surviving a death, buff length, the Sagestone bonus and the bag grid matching its items. New test commands: `test-dump`, `test-props`, `test-items`, `test-pets`, `test-templates`, `test-template` (forces the next newly generated floor), `test-fixture`, `test-vitals`, `test-give`, `test-socket`, `test-tooltip`, `test-close`, `test-trash`, `test-spin`, `test-dead`, `test-buff`, `test-party-heal`, `test-kill-pet`, `test-hurt-pet`.
# Grove co-op prototype — 0.19.0

Fixes and requests from the 0.18 playtest.

- **No quest limit.** `IsQuestLogFull` and `IsQuestLogFullActiveTownRealm` always say no, so you can take a quest from every giver (one per giver is the game's own rule). The side quest rows sit in a `ScrollRect` three rows tall; extra rows are clones of the first, and keyboard or gamepad selection scrolls the list to the selected row.
- **Sounds.** Item drops and landings, armed monster and partner swings, summons expiring and narrator lines are scoped to where they happen. Quest accept and reward sounds the host plays on a guest's behalf are muted.
- **Friend spell effects.** The replica's mana is raised to the spell's cost before a replay, so expensive spells (Greater Heal costs more than a replica's mana pool) now show. A friend's Shift heal or buff on you plays its impact on you instead of on them, and party spells play theirs on you.
- **Revive.** Bleed-out stops counting while a friend who is up stands within revive range. Taking damage pauses the revive bar for 0.75 s instead of resetting it; the reviver swinging or casting still resets it.
- **Map.** Friends' minimap icons draw above the fog and take the friend's color. Each friend reveals the fog around them on your map in real time. Minimap arrows and icons use one color per player: host red, then yellow, green, purple, blue and so on.
- **Summons.** Friendly summons (anything with a master that isn't Evil) are left out of monster sync, so they can't be hit or killed by friends and don't award kills when they expire. Every player sends their own summons in the frame (`summonJson`, a list of JSON strings because Unity's serializer skips nested mod types) and the others show inert copies, like pets.
- **New Grove NPCs.** The first Grove load each session loads Town_Ekbatan and Town_Temple_Fate2 additively, keeps the wanted NPCs (by `NpcVoiceLines.thisNpc`) in a hidden `DontDestroyOnLoad` cache, deactivates everything else before `Start` runs and unloads the scenes. While that happens, `RealmQuestGiver.OnEnable`, `DontDestroy.Awake`, `ProdigyCamera.Awake`, `ThatHurt.Awake` and `Game.Awake` are skipped so the borrowed scene can't touch quests or take over the camera and damage numbers.
  - Gort, Xander, Omar and Lizmo replace the Grove merchant with the same job (Samwen, Gann Gar, Dell Arness, Getts) and keep that NPC's spot and patrol route.
  - **Xerxes the Librarian** (by Jin the Seer) resets stat points for 100 gold per point. Points above the starting 25/20/25/10 come back as unspent points; equipped gear you no longer meet the requirements for goes to your bag, or to the ground if the bag is full.
  - **Sullivan the Reaper** (by the dungeon gate) resets the dungeons for free: every saved Grove floor is deleted except one with an open town portal, so they generate again. The reset goes to everyone in the session.
- `Test-MapAndSummons.ps1` covers a guest revealing the host's map, summons on both sides staying copies, a guest's heal showing on the host, and a reset reaching the guest. `Test-ReviveChat.ps1` checks the bleed-out timer holds while a friend is next to you. New test commands: `test-npc`, `test-warp`, `test-spend`, `test-heavy`, `test-summon`, `test-tab`, `test-quest-scroll`.

# Grove co-op prototype — 0.18.0

Fixes from the first 0.17 playtest.

- **No reload when following someone who is still loading.** Frames carry `travelDepth` while a player is in transit. A trip to a floor that a lower-id player is still loading waits (up to 20 s) for their world and joins it, instead of building a second copy and reloading into theirs.
- **Party portraits in town.** Partner slots are now children of the game's `PartyUI` vertical layout, inserted before the controller blocker, so the game places them under the last visible slot (fixed coordinates broke in town). Up to 7 rows; extra rows shrink with `childScaleHeight`.
- **Name tags in the game's style.** A clone of the interactable tooltip canvas (Gilland font, name tag backdrop, billboarding) over each partner, with two clones of the target bar for health and mana. They hide while menus are open. The IMGUI boxes are gone.
- **Minimap arrows.** The game's `Btn_Arrow-Yellow_Idle` sprite sits on the minimap edge pointing at each friend on your floor who is outside the minimap view.
- **Quest icons the right way up.** Minimap icons now turn around the vertical axis by the camera's change in yaw instead of rebuilding their tilt from Euler angles.
- **Sounds stay local.** Attacks, rolls, spells, chest and barrel sounds caused by other players or mirrored monsters are scoped to their position: normal within 10 units, positional out to 30, silent beyond. Monster sounds (their own AudioSource) and positional sounds beyond 30 units are dropped.
- **Spells from non-owners.** Local-source checks walk the master chain and skip stand-ins. The replica's profile is resent when level or attributes change, so the owner scales a partner's spells with current stats. Kills by damage over time (the no-argument `Die`) send the kill message, credited to the top threat.
- **Heal and buff friends.** The game tags friendly spells as targeting "All", not "Good", which is why hovering never worked; anything not aimed at Evil now counts as friendly. Hovering a friend works by screen distance (within 70 px). Holding Shift casts friendly self-buffs and heals on the friend nearest the mouse (the self hit and its effect are redirected; mana is still spent once). Morphs, portals and Discover stay on yourself.
- **Ranged.** Bows and crossbows turn toward the target before the shot. A transpiler makes the player's ranged hit roll always land; a roll that would have missed is a graze (half damage, quarter on a crit, no crit). Walls still block.
- **Animations.** `UpdatePathing` is skipped for partner stand-ins, so their walk and run bools survive to the animator. Mirrors animate from their real movement each frame, so monsters that wander off after you go down walk instead of sliding.
- **Friends' portals.** Portals show on the floor for everyone there and, in town, next to the portal spot with the owner's name. Stepping through a friend's town portal asks first, then loads that floor and puts you at their portal.
- `Test-PortalsAndPlates.ps1` covers name tags, portals on the floor and in town, and using a friend's portal. `Test-RangedAndHeals.ps1` gives the player a bow (20 shots, no misses, some grazes) and checks a Shift heal reaches the partner. `Enter-HostDungeon.ps1` moves to a fresh floor when the current one has fewer than 8 live monsters.

# Grove co-op prototype — 0.17.0

## Up to 16 players

- **Lobby size.** Main menu, Co-op: a "< 2 players >" picker (click either half, or left/right while it's selected) sets `[Co-op] Players` (2 to 16, default 2). Host co-op in the Escape menu uses the same setting.
- **Star network.** Guests talk only to the host; the host relays. The host stamps each guest message with that guest's id (0 is the host) and sends a `welcome` with the id when it first hears from a guest. Envelopes carry `from`, `to` (-1 for everyone) and an optional `world` key; world-scoped messages are dropped by anyone not in that world. Position frames are relayed at full rate only between players in the same world, otherwise at 2 Hz. A guest whose version differs gets a `mismatch` and the host keeps running. `left` tells guests someone dropped.
- **Per-player state.** `Partner` holds each player's frame, profile, replica, pet, pose trails, clock offset, animation counters, fishing pole, portal and HUD slots. Hooks find stand-ins with `PartnerOf` / `PartnerOfPet`.

## Floor owners instead of a host-owned world

- Whoever reaches a floor first runs it (monsters, loot, chests). Frames carry `worldKey` and `owner`. Arriving at a floor where someone already is loads their floor: `ReplaceDungeon` and `LoadAsync(Dungeon)` are held while the owner sends the snapshot, and replayed after 5 seconds if no answer comes. That removes the double load, the forced reload when coming back, and the reload after the Fate choice.
- When the owner leaves, the lowest id left on the floor takes over without reloading: mirrors become real monsters (they're registered with `AddMonsterToActiveMonsters` when spawned, so the new owner keeps tracking them), ground copies become real drops. Two owners in one world (a rejoin, a race into town) resolve to the lower id; the loser drops its ground copies and turns its monsters into mirrors.
- "Go to <name>" and following land you next to that player (`TeleportToPosition` after arrival). The follow prompt only appears once everyone has left your floor, never during a fight, while typing, or with a menu open.
- Hits, effects, strikes, hurts, kills (with the killer's id for kill credit), drops, takes and fixtures go to or come from the floor owner. Monster toughness scales with the number of other players on the floor.

## Shared quests

- The host's Grove side quests are everyone's quests, shown in each player's real quest log. Guests swap the host's list into their `QuestManager` (reinstalled after every `LoadSave`) and swap their own list back around `FlushSave`, so their save never holds host quests; it comes back when they leave.
- Kills and quest items count on whichever game sees them; guests report progress (lower kill counts, completed) and the host merges it. Taking, cancelling or turning in a quest on a guest goes to the host; a turn-in from either side rewards everyone once.
- `RemoveQuestItem` no longer logs "Failed to find Quest Item" when another player holds the item. The story quest stays personal.

## Fixes and polish

- Partners run instead of walk (`Running()` in the frame, `IsRunning` on the replica). Monster mirrors animate (bools set in a `Monster.Update` postfix so pathing can't clear them). Dead monsters can't be switched back to living states, and mirrors don't spawn their own minions.
- Threat: the floor owner tracks decaying damage per attacker (pets count for their master) and a hit monster switches to the top threat when it beats the current target by 20%.
- Heals and buffs: hover a partner and cast a friendly spell that needs a target; party-wide spells also reach partners on your floor. The effect is forwarded to the real player.
- Town portals show for everyone on the floor and anyone can step through; using a friend's portal records it as your own so you can come back.
- Boss health bars update on mirrors. Chests open and barrels break with their real animation and sound. The minimap and its icons (including the tilted quest "!") turn with the camera. Partners get the blue behind-wall marker; their pets lose theirs. Name tags hide while any menu is open. Party portraits keep their picture while a player travels. No "ally recovered" line when a partner gets up.
- Revive: any player on your floor can revive you. The bars use a clone of the game's target bar (green poisoned fill for reviving, red for bleeding out). Chat reads Enter and Escape from the game's input system, so Enter both opens and closes it, and messages float over the speaker.

## Tests

Three local copies (`test-game`, `-b`, `-c`; `tools/Instances.ps1` names their folders). `Start-Games.ps1`, `Connect-Games.ps1` and `Enter-HostDungeon.ps1` take `-Instances`. `Test-ThreePlayers.ps1` covers three players on one floor, hits from the third player, chat to all, the floor handover and rejoining the new owner. `Test-SharedQuests.ps1` also covers a guest turn-in. `Test-Lifecycle.ps1` and the single-player test guest were removed.

# Grove co-op prototype — 0.16.0

## Installer instead of the game copy

The friend kit (a 3 GB copy of the game, a save-redirecting assembly patch and a Steam launch option) confused players, so it's gone.

- **The mod runs in the real game.** The plugin no longer requires the patched `test-game` copy or checks save isolation at startup. Co-op uses the player's normal characters (the user's choice). The run lock and the test command folders only exist when `TestTools` is on. The two local test copies are still patched by `SandboxPatcher` and checked by `Start-TestGame.ps1`, because two games on one PC need separate saves.
- **`FateCoop-Installer.exe`** (`src/FateCoop.Installer`, WinForms on .NET Framework 4.8, so nothing extra to install; about 25 KB):
  - finds FATE from Steam's registry path, `libraryfolders.vdf` and `appmanifest_3030720.acf`, with a folder picker as a fallback;
  - reads the latest GitHub release and its `FateCoop-Mod.zip` asset;
  - **Install / Update** refuses to run while FATE is open, backs up `%USERPROFILE%\AppData\LocalLow\Tableflip\FATE Reawakened` to `Documents\FATE Co-Op save backups\<time>`, clears `BepInEx\plugins\FateCoop`, extracts the zip (rejecting paths outside the game folder), and writes `installed.txt` there (the version, then every file it placed);
  - **Remove** deletes `BepInEx\plugins\FateCoop`, then, only if no other plugins are left, the loader files at the game root from `installed.txt` and the `BepInEx` folder.
- **Release assets:** `tools\Build-Release.ps1` (was `Build-FriendKit.ps1`) builds `FateCoop-Mod.zip` (the BepInEx loader and core, plus `BepInEx\plugins\FateCoop` with the plugin, `LICENSE` and `THIRD-PARTY-NOTICES.txt`; forward-slash entry names) and `FateCoop-Installer.exe`.
- The installer's file logic was tested against a throwaway game folder: install, update, removal with and without another BepInEx plugin present, and the GitHub release lookup.

# Grove co-op prototype — 0.15.0

## Controls

- **WASD moves the character**, relative to the screen (W is up the screen). Click-to-move still works. Keyboard movement steps a short NavMesh hop ahead each frame through `InputBroker.SetMoveDestination`, and stops when the keys are released, while attacking, casting, fishing, or when a menu blocks input.
- The game's W/S zoom and A/D camera turn bindings are removed. The mouse wheel zooms, and **holding the middle mouse button turns the camera** (the cursor snaps back when released).
- `[Controls] KeyboardMovement` (on) and `CameraTurnSpeed` (0.25 degrees per pixel) are in the BepInEx config.

## Partner HUD in the game's style

The partner and their pet now use clones of the game's own `PartyMemberUI` and `PartyPetUI` slots under the party list: portrait, health and mana rings, name with level, and where they are when apart (Grove town, Floor N, Travelling, Down). The clones are display only (no clicks or navigation). The IMGUI partner panel is gone.

## Smoother syncing

- Position frames go over the unreliable channel with a sequence number, so a late frame is dropped instead of holding up newer ones. Everything else stays reliable.
- The partner and their pet are drawn from a short buffer of timed poses, 0.12 s behind, using a smoothed estimate of the sender's clock. Movement is interpolated between real samples instead of chasing the newest one, so stutters and rubber-banding are gone. A jump of more than 6 units (stairs, teleports) snaps.
- Every 5 seconds the host resends all monster states and the ground item list; the guest drops copies the host no longer has and asks again for ones it's missing.

## Independent floors (BG3 style)

- Players roam freely. Same place means a shared world: both in Grove, or both on the same dungeon depth (the guest is moved onto the host's version of that floor). Different depths mean each plays their own floor.
- When your partner leaves the place you were both in, the game's prompt asks "Ghost went to Floor 4. Follow them?". It waits until you're out of combat and not typing, and closes itself if you end up together anyway.
- **Go to partner** in the Escape menu (shown while apart) takes you to them. It is greyed out during a fight: 8 seconds after you last swung, cast or took damage.
- A guest joining a session is taken to the host automatically.
- The guest's own floor now has its own world key, so the host's HUD shows where the guest is.

## Revive

- In a shared world with your partner standing, lethal damage puts you **down** instead of dead: lying on the floor, health 0, monsters lose interest, input blocked, and a 30 second bleed-out bar.
- Your partner revives you by standing within 3 units for 4 seconds. Both see a progress bar. Walking away, taking damage, attacking or casting resets it. You get up with 25% health.
- If the timer runs out, your partner is also down, leaves the floor or disconnects, the game's normal death and Fate choice run.
- **Save & Quit while down counts as death**: it closes the menu and runs the real death (Hardcore loses the character). Closing the game window while down still skips the death, since the game doesn't save on that path.

## Chat

Press Enter to type, Enter to send, Escape to cancel. Game input is off while typing. Messages show bottom-left above the party quest list and fade after 10 seconds.

## Tests

`Test-SessionFlow.ps1` now covers independent floors, follow prompts both ways, Go to partner from town and the combat lockout. `Test-ReviveChat.ps1` covers chat both ways, going down, revive with a swing reset, bleed-out into the Fate choice, and Save & Quit while down. Dev commands: `follow-partner`, `test-down`, `test-save-quit`, `test-near-partner`, `test-chat <text>`, `test-fate-town`, `test-move <x> <y> <seconds>`, `test-turn <degrees>`.

# Grove co-op prototype — 0.14.0

## Co-op in the game's own menus

The IMGUI debug panel is gone. Co-op now lives in the game's menus, built by cloning the game's own buttons (the clones' `I2.Loc.Localize` component is removed so the text sticks):

- **Main menu** (Original FATE, `MainMenuUI.Start` postfix): a **Co-op** button under Continue Game opens **Host co-op**, **Join a friend** and **Back**.
  - **Host co-op** checks for a continue-able character and Steam, opens the Steam lobby, then continues the character.
  - **Join a friend** opens the Steam friends overlay.
  - Escape or gamepad B closes the sub-menu.
- **Auto-continue for invited players.** A guest who accepts an invite while on the Grove main menu goes straight into play with their current character, or is told to pick one.
- **Escape menu** (`PauseUI.OnMenuButton` postfix, built once): **Host co-op** when not in a session, **Invite friend** (Steam sessions) and **Leave co-op** when in one. The frame grows to fit, and explicit gamepad navigation is rebuilt over the visible buttons each time it opens.
- **Messages.** Partner joined or left, lobby ready, version mismatch, host went to town, and party quest rewards show as the game's floating white text above the player during play, or as the game's OK prompt on the main menu (queued, one at a time). Internal status still goes to the log and `status.json`.
- **Overlays.** The partner HUD (bottom-left), the party quest list (above it) and the partner/pet name labels hide while the Escape menu or a prompt is open. The pet label is sized to its text. F11 still hides all co-op overlays.

Dev commands `test-coop-menu` and `test-pause` open the sub-menu and the Escape menu for screenshots. All seven suites pass.

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
