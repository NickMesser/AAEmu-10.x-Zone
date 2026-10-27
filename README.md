# AAEmu 10.x Zone

An experimental C# replacement for the native ArcheAge Zone simulation, extracted from the upstream AAEmu development checkout. This source is **unfinished and not ready for gameplay**. Content readers, World/Zone protocol handling and simulation components exist, but a complete playable scene and sustained real-client validation have not been established.

The `10.x` project name describes the intended game-server family. The build currently targets **.NET 10**. This repository does not contain the World or Login servers, a game client, a content database or proprietary engine binaries. It cannot operate a complete server by itself.

## Provenance and contents

Source snapshot: `bc8a9e85845e18a33f0f98d9d02757ed0bfce03b`.

- `AAEmu.Zone`: experimental executable, content readers, scripting and simulation.
- `AAEmu.Zone.Protocol`: World/Zone wire contracts and framing.
- `AAEmu.Commons`: required shared upstream code, preserved with its attribution.
- `AAEmu.Zone.Tests`: seven unchanged upstream test files that run independently of World, plus loaded SQLite version and archive AES regression checks. They cover protocol framing and truncation, packed integers, formula semantics, runtime configuration, navigation paths, synthetic terrain heights and archive encryption. This is not the complete upstream test suite.

The extraction uses a tracked-file allowlist. It excludes local configuration, `.codex-runtime`, build output, extracted game content and logs. Internal coordination/evidence journals (`FIRST_ENTRY_NOTES.md`, `IMPLEMENTATION_OWNERSHIP.md`) and the old Zone README were omitted; this README replaces their installation/status role. The unused Commons `Helper.cs` implementation was excluded because its third-party licensing was unclear. Source comments may still refer to upstream evidence journals and native addresses. The implementation TODO remains with internal operating instructions removed; unit snapshot/stat reference documents remain included.

AAEmu was created by its [upstream contributors](https://github.com/AAEmu/AAEmu/graphs/contributors); this extraction does not claim authorship of their work. See [licensing provenance](docs/LICENSING-PROVENANCE.md).

## Build and verify

Install the .NET 10 SDK. `global.json` preserves the upstream SDK and Microsoft.Testing.Platform selection. Run these commands from this repository root; choose an absolute output directory outside the checkout:

```powershell
$zoneArtifacts = Join-Path $env:TEMP 'AAEmu-10.x-Zone-artifacts'
dotnet build AAEmu.Zone/AAEmu.Zone.csproj --artifacts-path $zoneArtifacts
dotnet test --project AAEmu.Zone.Tests/AAEmu.Zone.Tests.csproj --artifacts-path $zoneArtifacts --results-directory (Join-Path $zoneArtifacts 'test-results') --report-html-filename (Join-Path $zoneArtifacts 'test-results/standalone.html')
```

The selected tests create synthetic inputs and need no game installation, native engine, Lua library or player database. Passing them does not establish gameplay readiness or native runtime parity.

The publication copy pins `SQLitePCLRaw.lib.e_sqlite3` to 2.1.14 while retaining the upstream managed SQLite APIs. This replaces the vulnerable 2.1.10 native dependency reported by [CVE-2025-6965](https://github.com/advisories/GHSA-2m69-gcr7-jv3q). A regression test checks the native SQLite version actually loaded by the provider.

## External runtime inputs

Copy [Runtime.example.json](AAEmu.Zone/Runtime.example.json) outside this repository and supply your own paths. The checked-in paths are illustrative local examples, not bundled dependencies.

- A configured decrypted `game.compact` export, opened read-only. Its actual filename may be `game_decrypted.sqlite3`; the unrelated `compact.sqlite` substitute is refused.
- Authorized local game assets, accessed through configured loose directories and/or `game_pak`.
- The exact compatible Windows AMD64 engine modules required by native framework, physics and AI paths. `ZoneNativeEngine.Identities.cs` records the accepted module hashes. This source does not supply or grant rights to those modules or assets.
- A 64-bit Lua 5.1 library with the `double+int64` patch when using scripting/native host paths. Set `AAEMU_ZONE_LUA_LIBRARY` to its absolute path. The historical fallback is a local development path, not an installation mechanism.
- A compatible World server for startup exchange or host experiments, with an explicitly configured endpoint. Use an isolated development World; startup joins affect that World's connection lifecycle.

For a read-only content check after building:

```powershell
dotnet (Join-Path $zoneArtifacts 'bin/AAEmu.Zone/debug/AAEmu.Zone.dll') --check-content --config 'C:/path/to/external-runtime.json'
```

The executable's `--help` lists other check/host options. Native checks need the external modules and assets, and incomplete runtime dependencies may refuse admission explicitly. No native runtime checks are claimed by this extraction.

## Licensing and redistribution

The original [LICENSE](LICENSE), [GPL text](LICENSE.GPL) and [MIT text](LICENSE.MIT) are preserved unchanged. Upstream licensing statements conflict: its root README says LGPL version 3 or later, while `LICENSE` says the project is distributed under GPL and contributors may use GPL or MIT. This extraction does **not** resolve that inconsistency or declare a new license. Review [the preserved statements](docs/LICENSING-PROVENANCE.md) and obtain clarification from the relevant rights holders before relying on a particular license grant.

Native integration code contains recovered ABI layouts, constants and function addresses. No native binaries or extracted game assets are included. Their absence does not establish legal clearance for every source contribution or reverse-engineered contract; provenance and redistribution rights require separate review. Dependencies retain their own licenses.
