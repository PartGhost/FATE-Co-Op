# Co-op rules and code findings

## Accepted gameplay rules

- Target Original FATE / Grove first; cross-campaign progression comes later.
- Host owns the shared world, monsters, damage, quest events and ground loot.
- One shared ground drop can be claimed by either player. Players can drop items for each other.
- Both living players on the same floor receive the full normal XP award.
- Quest objectives/progression are shared; each player receives their own quest reward, including an item reward.
- Existing characters enter through protected co-op copies, preserving solo saves.
- Select a character before joining. An invitation accepted earlier can wait for character selection.
- Intended Steam flow: host enters the game, opens Shift+Tab and invites a friend. Exact overlay integration remains to be verified against Steam/game code. Local connection supplies the same session concept during development.

## Findings from the installed game

`LevelGenerator.Create` first calls `LoadSaveData`; a numeric seed alone does not override an existing saved floor. `DungeonSaveData` contains a Unity random state, template index, origin, player/portal positions, monsters, containers, drops and used-object flags. `LoadSaveData` restores that state before constructing `MazeCreation`. Snapshot installation belongs immediately before this load, because scene/floor replacement saves the departing world first and could overwrite an earlier installation.

0.5 uses the host's world record, leaves B's player/inventory/pet records intact, and fingerprints maze cells plus both staircase positions. Replication requires the same world identity and matching layout. The local relay currently transfers the snapshot through workspace files; this must become a bounded network snapshot transfer before second-PC/Steam testing. A cryptographic checksum detects a changed payload; it does not replace peer authentication.

`FateSaveData` separates `playerSaveData`, player and pet items, stash, quest state and realm/dungeon state. `SaveData.CreateNewSaveSlotFromImport` already creates a new unique slot, but resets quest/realm/summon state and filters forbidden items. Reusing it blindly would change imported characters. A dedicated co-op import should preserve explicitly supported character fields, link the source character to its persistent co-op copy, and initialize host-owned world/quest state separately. Import is not yet implemented; A/B still use disposable test characters.

`InventoryUI` removes an item through `Character.RemoveItemFromInventory` before calling `Item.Drop`. `Item.Drop` chooses a random reachable landing location and creates a `DroppedItem`. `DroppedItem.Interact` tries the hotbar/inventory, removes the old world object, and creates a replacement drop if it cannot fit. Shared pickup therefore needs an authoritative ownership transaction, including inventory-full rejection, rather than copying the existing callback to both machines. Shared items need persistent network IDs distinct from Unity instance IDs; request IDs and committed results must prevent repeated transfers after reconnect.

`Quest.ClaimRewards` grants gold/XP/fame directly to the singleton local player, inserts the reward into that player's inventory or drops it if full, then marks the quest claimed. Shared completion and each participant's reward claim must be separate records. Each reward needs a claim ID and delivery state. A full-inventory quest reward must remain owned by its recipient rather than entering ordinary shared ground loot accidentally.

## Implement next

After verified floor joining: host monster/entity IDs and snapshots, host-confirmed combat and XP, then atomic item drop/pickup transfers and shared quest events with per-player reward claims. Implement persistent character/session IDs and co-op import before real character progression testing. Do not interpret matching layouts or copied initial monsters as synchronized combat, loot or quest progression.
