# FATE Co-Op

Online co-op for **FATE: Reawakened** (Original FATE, Grove) for 2 to 16 players, played through Steam with friends.

> Unofficial fan mod. Not made, endorsed or supported by the developers or publisher of FATE: Reawakened. Every player needs their own copy of the game on Steam.

## Features

- Shared dungeon floors: same monsters, same loot, same chests and barrels. Kills give XP to everyone on the floor.
- Friends show with their Steam names, their looks, their gear and their pets, plus party portraits with health and mana.
- Attacks, spells, effects, damage numbers, level-ups and fishing show for both players.
- Shared quests in the real quest log: anyone can accept them, anyone's kills and quest items count, and everyone gets the reward.
- Go where you like. Same floor (or town) means you play together, and whoever reaches a floor first keeps it: joining never reloads the player who got there first. When everyone leaves your floor you're asked if you want to follow, and "Go to Floor 9 (<name>)" in the Escape menu takes you next to them (not during a fight).
- Revive: dying near a friend puts you down instead. A friend stands next to you for 4 seconds to get you up. After 30 seconds you die for real, but the clock stops while a friend is next to you.
- Heal and buff friends: hold Shift while casting a self buff or heal to put it on the friend nearest your mouse, and party spells reach every friend on your floor. Buff spells last at least two and a half minutes. Town portals work for everyone, in the dungeon and in town, and look the same from both sides.
- Bows and crossbows aim at your target and always land when it's in range; a shot that would have missed grazes for half damage.
- Every friend shows on the minimap as a diamond in their own color, through the fog, and reveals the map for you as they explore. An arrow on the minimap edge points to friends who are off the map.
- Friends' summons fight alongside you and can't be hit by you.
- No limit on quests: take one from every quest giver and scroll through them in the quest log.
- New Grove townsfolk from the other FATE games: Gort, Xander, Omar and Lizmo take over the shops, Xerxes the Librarian resets your stat points and Sakina the Priestess your skill points (100 gold per point), and Sullivan the Reaper resets the dungeons so new floors generate.
- Crystals in the dungeon break when you hit them and sometimes hold a gem, gold carts open like chests, and campfires restore half your health and mana once.
- The dungeon keeps going: below the Grove's last floor the stairs lead on into Druantia, Typhon and the Temple of Fate from FATE: Undiscovered Realms, then The Traitor Soul's Chamber of Trials, The Cursed King's Caverns of Living Fire, Tkala Crypt and Haunted Palace, and finally Prometheus if everyone in your game owns that DLC, with monsters and loot that keep getting stronger. Past floor 100, where the game's best gear runs out, monsters level up at half speed so the late realms stay beatable, and shops stock gear for the deepest floor you've reached in the whole run. Grove stays your town, and once you reach a realm its story giver (Twigger, Cyrus, Sullivan for the Temple, Malkondra for the trials, Xerxes, Awadji, Sakina and Kurosh for The Cursed King) comes to Grove with its quests. Xerxes, Sakina and Sullivan keep their resets too: say No to a story and they offer the reset. The quest log's arrows page between the realms' stories. More of the expansions are coming.
- Sagestones, a new magenta gem, give 2 to 10% more experience when socketed.
- Day and night in Grove: a full day takes 20 minutes, dusk turns the town golden, and at night the lamps light the streets. In co-op everyone sees the host's time of day.
- Capes, cloaks, mantles and drapes move: they bend and trail behind you as you run and turn, on you and your friends.
- Text chat with speech bubbles.
- WASD movement and a middle-mouse camera.
- Monsters get tougher for each extra player on the floor, and they go after whoever is doing the most damage.

## Requirements

- Windows 10 or 11, Steam, and FATE: Reawakened from Steam.
- Your friend has the same version of this mod.

## Install

1. Download `FateCoop-Installer.exe` from the [latest release](../../releases/latest).
2. Close FATE and run it. Windows may say "Windows protected your PC" because the installer isn't code-signed: click **More info**, then **Run anyway**.
3. It finds FATE through Steam (or click **Change folder...** and pick the folder with `FateReawakened.exe`), then:
   - **Install** downloads the latest version and adds BepInEx and the mod to your game folder. No game files are changed.
   - **Update** installs the latest version. You and your friend need the same version, and the installer tells you when a new one is out.
   - **Remove** takes out everything FATE Co-Op installed. BepInEx stays if other mods use it or it was there before. If your save has more than 3 Grove side quests, Remove warns first: FATE without the mod can't show more than 3.
4. Start FATE from Steam as usual. Co-op uses your normal characters.

Every install and update first backs up your saves to `Documents\FATE Co-Op save backups`. FATE updates from Steam don't remove the mod. If anything ever goes wrong, Remove the mod, or use Steam's "Verify integrity of game files".

## Playing

- **Host:** on the main menu choose **Co-op**, set the number of players with the arrows, then **Host co-op**. Or press Escape in game and choose **Host co-op**. Then **Invite friend** from the Escape menu.
- **Join:** accept the Steam invite, or choose **Co-op > Join a friend** and right-click your friend > Join Game. You're taken into the game and to the host automatically.
- **Leave:** Escape > **Leave co-op**.

| Key | Action |
|---|---|
| WASD | Move (up the screen is up). Click-to-move still works |
| Mouse wheel | Zoom |
| Hold middle mouse | Turn the camera |
| Enter | Chat (Enter sends, Escape cancels) |
| F10 | Connection panel: each friend's ping and freezes on your side |
| F11 | Hide the co-op overlays |

`BepInEx\config\local.fatecoop.prototype.cfg` in the game folder has a few settings: lobby size, keyboard movement, camera turn speed, monster toughness and the length of a day in Grove (0 keeps it day).

## Known limits

- Original FATE characters only. The Chamber of Trials ends after the Nemesis on its floor 40; its optional Shadow on floor 90 isn't part of the run. Tested with up to 3 players; bigger groups need a good upload speed on the host.
- Closing the game window while you're down skips the death. Save & Quit while down counts as dying.
- Monster spells don't draw their projectiles on the joining player's screen (the damage still lands).

## Development

- `src/FateCoop.Prototype`: the BepInEx plugin.
- `src/FateCoop.Installer`: the installer (WinForms on the .NET Framework built into Windows).
- `src/SandboxPatcher`: patches the two local test copies of the game so their saves stay separate. Development only.
- `tools/`: build, release (`Build-Release.ps1`) and multi-instance test scripts. `docs/prototype.md` has the version-by-version design notes.

Builds need the local tool folders listed in `.gitignore` (portable .NET SDK, BepInEx, Mono.Cecil). Game assemblies, decompiled sources and saves are never committed or shipped.

Licensed under [MIT](LICENSE). Bundled third-party components are listed in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
