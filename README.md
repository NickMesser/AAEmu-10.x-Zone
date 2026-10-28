# AAEmu 10.x Zone

An experimental C# zone server for [AAEmu 10.x](https://github.com/AAEmu/AAEmu-10.x). World launches it in place of the native dedicated zone (dedicated host + `x2game-dev_dedicate.dll`). It replaces all X2 game logic and still hosts the client's CryEngine `*_dedicate` engine modules for scene, physics and AI. This source is **unfinished and not ready for gameplay**. Content readers, World/Zone protocol handling and simulation components exist, but a complete playable scene and sustained real-client validation have not been established. **[TASKS.md](TASKS.md)** lists what is missing compared with the original zone, ordered by milestone.

```
client ⇄ AAEmu-10.x World ⇄ (WZ/ZW, port 1240) ⇄ AAEmu.ZoneServer ⇄ CryEngine *_dedicate modules
```

The build targets **.NET 10**. This repository does not contain the World or Login servers, a game client, a content database or proprietary engine binaries. It cannot operate a complete server by itself.

## Layout

| Path | Contents |
| --- | --- |
| `src/AAEmu.ZoneServer` | The zone server executable World launches (`AAEmu.ZoneServer.exe`) and `zone.example.json` |
| `src/AAEmu.Zone` | Zone server library: `Hosting` (startup, config, frame loop), `WorldLink` (World session), `Content` (game database and asset readers), `Scene`, `Terrain`, `Physics`, `Units`, `Actors` (players and movement), `Navigation`, `Npcs`, `Ai`, `Combat`, `WorldState`, `Scripting` (Lua), `Native/*` (CryEngine engine-module interop), `Diagnostics` |
| `src/AAEmu.Zone.Protocol` | World/Zone wire contracts and framing (WZ/ZW opcodes and messages) |
| `src/AAEmu.Commons` | Required shared upstream code, preserved with its attribution |
| `tools/AAEmu.Zone.Diagnostics` | Isolated checks and research probes (`--check-content`, `--check-startup`, `--check-framework`, `--check-scene`, `--probe-*`) |
| `tests/AAEmu.Zone.Tests` | Tests that run without World, game data or engine modules |
| `docs/` | [Task area reports](docs/tasks), the historical [first-entry roadmap](docs/FIRST-ENTRY-ROADMAP.md), unit snapshot/stat references and licensing provenance |

## Provenance and contents

Source snapshot: `bc8a9e85845e18a33f0f98d9d02757ed0bfce03b`.

- `tests/AAEmu.Zone.Tests`: seven unchanged upstream test files that run independently of World, plus loaded SQLite version and archive AES regression checks, and tests for World's launch contract. They cover protocol framing and truncation, packed integers, formula semantics, runtime configuration, navigation paths, synthetic terrain heights and archive encryption. This is not the complete upstream test suite.

The extraction uses a tracked-file allowlist. It excludes local configuration, `.codex-runtime`, build output, extracted game content and logs. Internal coordination/evidence journals (`FIRST_ENTRY_NOTES.md`, `IMPLEMENTATION_OWNERSHIP.md`) and the old Zone README were omitted; this README replaces their installation/status role. The unused Commons `Helper.cs` implementation was excluded because its third-party licensing was unclear. Source comments may still refer to upstream evidence journals and native addresses. The implementation TODO remains as [docs/FIRST-ENTRY-ROADMAP.md](docs/FIRST-ENTRY-ROADMAP.md) with internal operating instructions removed; unit snapshot/stat reference documents remain included. The later restructure into `src/`, `tools/` and `tests/` is recorded file by file in `.restructure-map.tsv`.

AAEmu was created by its [upstream contributors](https://github.com/AAEmu/AAEmu/graphs/contributors); this extraction does not claim authorship of their work. See [licensing provenance](docs/LICENSING-PROVENANCE.md).

## Build and verify

Install the .NET 10 SDK. `global.json` preserves the upstream SDK and Microsoft.Testing.Platform selection. Run these commands from this repository root; choose an absolute output directory outside the checkout:

```powershell
$zoneArtifacts = Join-Path $env:TEMP 'AAEmu-10.x-Zone-artifacts'
dotnet build AAEmu.Zone.slnx --artifacts-path $zoneArtifacts
dotnet test --project tests/AAEmu.Zone.Tests/AAEmu.Zone.Tests.csproj --artifacts-path $zoneArtifacts --results-directory (Join-Path $zoneArtifacts 'test-results') --report-html-filename (Join-Path $zoneArtifacts 'test-results/standalone.html')
```

The selected tests create synthetic inputs and need no game installation, native engine, Lua library or player database. Passing them does not establish gameplay readiness or native runtime parity.

The publication copy pins `SQLitePCLRaw.lib.e_sqlite3` to 2.1.14 while retaining the upstream managed SQLite APIs. This replaces the vulnerable 2.1.10 native dependency reported by [CVE-2025-6965](https://github.com/advisories/GHSA-2m69-gcr7-jv3q). A regression test checks the native SQLite version actually loaded by the provider.

## External runtime inputs

Copy [zone.example.json](src/AAEmu.ZoneServer/zone.example.json) outside this repository and supply your own paths. The checked-in paths are illustrative local examples, not bundled dependencies.

- A configured decrypted `game.compact` export, opened read-only. Its actual filename may be `game_decrypted.sqlite3`; the unrelated `compact.sqlite` substitute is refused.
- Authorized local game assets, accessed through configured loose directories and/or `game_pak`.
- The exact compatible Windows AMD64 engine modules required by native framework, physics and AI paths. `ZoneNativeEngine.Identities.cs` records the accepted module hashes. This source does not supply or grant rights to those modules or assets.
- A 64-bit Lua 5.1 library with the LNUM `double int64` patch (for example [LuaDist/lualnum](https://github.com/LuaDist/lualnum) compiled as a DLL with `LUA_BUILD_AS_DLL LNUM_DOUBLE LNUM_INT64`). Set `luaLibraryPath` in the zone configuration; `AAEMU_ZONE_LUA_LIBRARY` overrides it. There is no default path.
- A compatible World server for startup exchange or host experiments, with an explicitly configured endpoint. Use an isolated development World; startup joins affect that World's connection lifecycle.

For a read-only content check after building:

```powershell
dotnet (Join-Path $zoneArtifacts 'bin/AAEmu.Zone.Diagnostics/debug/AAEmu.Zone.Diagnostics.dll') --check-content --config 'C:/path/to/zone.json'
```

The diagnostics tool's `--help` lists the other checks. Native checks need the external modules and assets, and incomplete runtime dependencies may refuse admission explicitly. No native runtime checks are claimed by this extraction.

## Running under AAEmu 10.x World

World's `ZoneHostSupervisor` starts one process per zone instance with `-dedicated +world_ip … +world_port … +zone <map> +sv_map <map> +instance <id> +sv_port <port> +db_location <path> …` and the `AAEMU_ZONE_DLL`, `AAEMU_ZONE_SAVE_DIR` and `AAEMU_ZONE_LOG_NAME` environment variables. `AAEmu.ZoneServer` accepts that launch directly:

- **Base configuration.** Inputs World does not pass (engine directory, asset sources, world map, frame period) come from `zone.json`. The server takes it from `--config`, the `+zone_config` CVar, `AAEMU_ZONE_CONFIG`, or `zone.json` beside the executable, in that order.
- **What World's launch overrides.** World's endpoint, zone and content database override the configuration. The zone key is always resolved from the content database. Logs go to `AAEMU_ZONE_SAVE_DIR`/`AAEMU_ZONE_LOG_NAME` when set. ZWJoin reports `+instance` and `+sv_port`.
- **Player admission.** Set `"physicsBackend": "owned"` (as in the example). The native backend refuses players until the native `CPlayer` actor is published into ActorSystem; the owned backend admits the player with an unbound flow actor and makes this host the movement authority.
- **Spawner placements.** These are read from `game/worlds/<world>/level_design/zone/<key>/zone_server/npc_spawners.g` in the configured assets unless `spawnerFile` or `--spawner-file` names a file.
- **`AAEMU_ZONE_DLL`.** It is ignored: this process replaces that module.
- **World configuration.** Set `ZoneHost.Executable` to `AAEmu.ZoneServer.exe` and `ZoneHost.WorkingDirectory` to the client directory that `+db_location` is relative to. World's preflight still requires `ZoneHost.NativeDll` to name an existing file (TASKS.md WC-03).
- **Continent zones.** World launches only instance zones. Start continent zones from this repo: `AAEmu.ZoneServer --zones w_gweonid_forest_1,w_solzreed_1 --config zone.json` runs one host per map with `+instance 0`, prefixes each host's output with its map name and restarts a host that exits (5 s backoff doubling to 1 min). Ctrl+C retires them all.

By hand: `AAEmu.ZoneServer --config C:/path/to/zone.json [--spawner-file npc_spawners.g]`.

## Licensing and redistribution

The original [LICENSE](LICENSE), [GPL text](LICENSE.GPL) and [MIT text](LICENSE.MIT) are preserved unchanged. Upstream licensing statements conflict: its root README says LGPL version 3 or later, while `LICENSE` says the project is distributed under GPL and contributors may use GPL or MIT. This extraction does **not** resolve that inconsistency or declare a new license. Review [the preserved statements](docs/LICENSING-PROVENANCE.md) and obtain clarification from the relevant rights holders before relying on a particular license grant.

Native integration code contains recovered ABI layouts, constants and function addresses. No native binaries or extracted game assets are included. Their absence does not establish legal clearance for every source contribution or reverse-engineered contract; provenance and redistribution rights require separate review. Dependencies retain their own licenses.
