# FATE Co-Op development workspace

Current milestone: Steam lobbies and a direct game-to-game link carrying replicated players and pets, host-provided Grove dungeon layouts, and one host-controlled shared test monster. The combat fixture uses fixed 10-damage hits; full combat, XP, loot and progression remain future milestones. Pickups and unrelated enemies are disabled during testing.

See [the assessment](docs/feasibility.md) and [the current prototype guide](docs/prototype.md). The original Steam installation and save are protected. The mod is deployed only into the disposable `test-game` and `test-game-b` copies.

- `src/`: the BepInEx co-op prototype and offline sandbox patcher.
- `docs/`: findings and prototype acceptance criteria.
- `research/`: local-only ILSpy output from the installed game, excluded from version control.
- `tools/`: local inspection tools and repeatable inspection script.

Local tools include ILSpyCmd 9.1.0.7988, portable .NET SDK 8.0.425, Mono.Cecil 0.11.6, and BepInEx 6.0.0-be.788 with bundled Harmony. Builds use local references and the offline NuGet configuration.

Do not distribute game assemblies, decompiled game sources, or personal saves. The offline patcher redirects game-owned persistent paths to `test-saves/instance-a`, and namespaces PlayerPrefs calls. Steam stays enabled for invites and networking. The launcher verifies patched assembly hashes before running.
