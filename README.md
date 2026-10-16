# FATE Co-Op

Online co-op for **FATE: Reawakened** (Original FATE, Grove) for 2 to 16 players, played through Steam with friends.

> Unofficial fan mod. Not made, endorsed or supported by the developers or publisher of FATE: Reawakened. Every player needs their own copy of the game on Steam.

## Features

- Shared dungeon floors: same monsters, same loot, same chests and barrels. Kills give XP to everyone on the floor.
- Friends show with their Steam names, their looks, their gear and their pets, plus party portraits with health and mana.
- Attacks, spells, effects, damage numbers, level-ups and fishing show for both players.
- Shared quests in the real quest log: anyone's kills and quest items count, and everyone gets the reward.
- Go where you like. Same floor (or town) means you play together, and joining a floor someone is already on never reloads it. When everyone leaves your floor you're asked if you want to follow, and "Go to <name>" in the Escape menu takes you next to them (not during a fight).
- Revive: dying near a friend puts you down instead. A friend stands next to you for 4 seconds to get you up. After 30 seconds you die for real.
- Heal and buff friends by hovering them and casting. Town portals work for everyone.
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
   - **Remove** takes everything back out (BepInEx stays if other mods use it).
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
| F11 | Hide the co-op overlays |

`BepInEx\config\local.fatecoop.prototype.cfg` in the game folder has a few settings: lobby size, keyboard movement, camera turn speed and monster toughness.

## Known limits

- Grove (Original FATE) only. Tested with up to 3 players; bigger groups need a good upload speed on the host.
- Closing the game window while you're down skips the death. Save & Quit while down counts as dying.
- Monster spells don't draw their projectiles on the joining player's screen (the damage still lands).

## Development

- `src/FateCoop.Prototype`: the BepInEx plugin.
- `src/FateCoop.Installer`: the installer (WinForms on the .NET Framework built into Windows).
- `src/SandboxPatcher`: patches the two local test copies of the game so their saves stay separate. Development only.
- `tools/`: build, release (`Build-Release.ps1`) and multi-instance test scripts. `docs/prototype.md` has the version-by-version design notes.

Builds need the local tool folders listed in `.gitignore` (portable .NET SDK, BepInEx, Mono.Cecil). Game assemblies, decompiled sources and saves are never committed or shipped.

Licensed under [MIT](LICENSE). Bundled third-party components are listed in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
