# FATE Co-Op

Online two-player co-op for **FATE: Reawakened** (Original FATE, Grove), played through Steam with a friend.

> Unofficial fan mod. Not made, endorsed or supported by the developers or publisher of FATE: Reawakened. Both players need their own copy of the game on Steam.

## Features

- Shared dungeon floors: same monsters, same loot, same chests and barrels. Kills give XP to both players.
- Your partner shows with their Steam name, their look, their gear and their pet, plus a party portrait with health and mana.
- Attacks, spells, effects, damage numbers, level-ups and fishing show for both players.
- Party quests: the host's quests are shared, and both players get the reward.
- Go where you like. Same floor (or both in town) means you play together. When your partner leaves you're asked if you want to follow, and "Go to partner" in the Escape menu takes you to them (not during a fight).
- Revive: dying near your partner puts you down instead. They stand next to you for 4 seconds to get you up. After 30 seconds you die for real.
- Text chat.
- WASD movement and a middle-mouse camera.
- Monsters get tougher while your partner is on the floor.

## Requirements

- Windows, Steam, and FATE: Reawakened installed through Steam.
- Your friend has the same version of this mod. Each release says which one.

## Install

1. Download `FateCoop-Kit.zip` from the [latest release](../../releases/latest).
2. Unzip it into a folder outside OneDrive, for example `C:\FATE Co-Op`.
3. Close FATE, then run `Setup-FateCoop.cmd`. It copies your game (about 3 GB) into a separate co-op copy with the mod. **Your real install and saves are not touched.**
4. In Steam, right-click FATE Reawakened > Properties > General > Launch Options and paste the line the setup put on your clipboard.
5. Start FATE from Steam and make a new Original FATE character. The co-op copy keeps its own saves.

After a FATE update, run the setup again. Your co-op character is kept.

To play normal FATE again, clear the launch option. To uninstall, clear it and delete the folder.

## Playing

- **Host:** on the main menu choose **Co-op > Host co-op**, or press Escape in game and choose **Host co-op**. Then **Invite friend** from the Escape menu.
- **Join:** accept the Steam invite, or choose **Co-op > Join a friend** and right-click your friend > Join Game. You're taken into the game and to the host automatically.
- **Leave:** Escape > **Leave co-op**.

| Key | Action |
|---|---|
| WASD | Move (up the screen is up). Click-to-move still works |
| Mouse wheel | Zoom |
| Hold middle mouse | Turn the camera |
| Enter | Chat (Enter sends, Escape cancels) |
| F11 | Hide the co-op overlays |

`BepInEx\config\local.fatecoop.prototype.cfg` in the co-op copy has a few settings: keyboard movement, camera turn speed and monster toughness.

## Known limits

- Two players only, Grove (Original FATE) only.
- Closing the game window while you're down skips the death. Save & Quit while down counts as dying.
- Monster spells don't draw their projectiles on the joining player's screen (the damage still lands).

## Development

- `src/FateCoop.Prototype`: the BepInEx plugin.
- `src/SandboxPatcher`: patches the co-op copy so its saves and settings stay separate from the real game.
- `tools/`: build, kit and two-instance test scripts. `docs/prototype.md` has the version-by-version design notes.

Builds need the local tool folders listed in `.gitignore` (portable .NET SDK, BepInEx, Mono.Cecil). Game assemblies, decompiled sources and saves are never committed or shipped.

Licensed under [MIT](LICENSE). Bundled third-party components are listed in `tools/kit/THIRD-PARTY-NOTICES.txt`.
