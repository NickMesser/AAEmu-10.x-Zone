<!-- Audit of ops: native x2game-dev_dedicate.dll (ArcheAge 10.0.2.13 r575) vs this repo at 76e439f. Paths rewritten to the src/ layout; line numbers refer to 76e439f. -->

## Ops: server CVars, GM/console commands, host process, logging, engineering

The native dedicated zone (`x2game-dev_dedicate.dll`) registers one big game-CVar table in `sub_392B1240` (0x392B1240, ~330 CVars, most of them client leftovers: `cl_*`, `aim_*`, `v_*`, `pl_zeroG*`, `bot_*`) and a console-command table in `sub_392B8D70` (`quit`, `goto`, `gotoe`, `dialog_task_count`, `test_ars`, `test_login_deny`, `game_provider`, `start_packet_profile`, `dump_auth_feature_set`, `dump_auth_characters`, `dump_hack_checker_history`, `run_sleeper`). The CVars that change server behavior are few: the `movement_verify_*` / `movement_hack_*` anti-cheat family, `max_unit_in_world`, `sv_input_timeout`, `world_serveraddr/port`, `instance_id/index`, `gm_startup`, `combat_sync_framehold`, `data_mining_*`, the `g_unit_collide_*` family, `swim_*`, `pl_fall*` and `cd_*_update_distance`. The repo has no CVar layer at all. It only owns two game-mode CVars for FlowGraph nodes (`ZoneNativeFlowGameModeCVars.cs`), and it hardcodes everything else. Of the zone-side GM commands, the repo handles 3 selectors (2, 3, 30) and throws `NotSupportedException` for 4 more (49, 142, 143, 144), which would tear down the session if a GM sends one. The repo already has a `--host` persistent mode (`Program.cs (pre-restructure)` `RunHostAsync` -> `ZoneSceneHost.RunAsync` -> `ZoneSceneRunner`), so TODO.md's "no persistent gameplay launch mode" is stale. The host is still unproven and lacks crash handling, SIGTERM/ProcessExit handling, a structured log and any CI. Test coverage is about 50 test methods in 9 files, concentrated on frames, navigation paths, terrain height and archive AES.

Notes on evidence quality: many `npc_*` / `net_*` / `um_*` names (e.g. `npc_move_sync_period`, `net_actor_sync_period`, `npc_disable_creation`, `npc_kill`) exist in `x2game-dev_dedicate.strings.txt` (0x39FDC7D8..0x39FDD3C8) but have NO code xref in the Hex-Rays export. Their registration is probably in a table the decompiler did not resolve. Treat them as "present, unverified". The `.cvar.txt` / `.cvar2.txt` dumps are from the CLIENT `x2game.dll` (the `sv_input_timeout` hit in `cvar2` is at line 1855), so they do not show dedicate-only CVars.

### Coverage: server-relevant CVars (registered in sub_392B1240, 0x392B1240)

| Native item | Native evidence | Sim effect | Repo status |
| --- | --- | --- | --- |
| `movement_verify_enable` (default 0), `_target_max` 100, `_sample_max` 200, `_speed_sample_min` 50, `_max_packet_interval` 1000, `_ignore_msec_after_skill_controller` 2000, `_move_speed_enable` 1, ~30 more tolerance/rate/warp CVars, `movement_hack_report_debug`, `movement_verify_dump_log`, `movement_levitation_hack_buff_start_time` 5000, `movement_boost_mul` | Registration lines ~584933-585190 in the .c; reporter text `Speed hack detected%s ... Err(%.1fpct=%d/%d)` at line 478374; `dump_hack_checker_history` handler near line 477670 | Movement validation / anti-cheat on `ZWUnitMovements` | absent (no match for `movement_verify`, `HackChecker` anywhere in `*.cs`) |
| `max_unit_in_world` (default 100000), `max_unit_for_test` | line 583566 | Unit-id/capacity cap | absent |
| `sv_input_timeout` ("timeout in ms to stop interpolating client inputs") | line 583431 | Input interpolation cutoff | absent |
| `world_serveraddr`, `world_serverport`, `auth_serveraddr/port/svc`, `instance_id`, `instance_index`, `editor_serveraddr/port`, `vpn_external_ip`, `sv_port`, `sv_map`, `cl_account_id` | line 583521 (world_*); join packet builder `sub_393652F0` area lines 721986-722040 | Endpoint + instance identity put in `ZWJoinPacket` | partial: `world-host/world-port/zone` exist in `ZoneRuntimeConfiguration.cs` and `ZoneJoin`; `instance_id`/`instance_index` absent (Program.cs (pre-restructure) passes `0,0,0,0,false`) |
| `gm_startup`, `test_world_queue`, `test_world_congestion` | lines 583921, ~583900 | GM / queue test switches | absent |
| `combat_sync_framehold`, `ragdoll_hit*`, `queued_skill_debug`, `projectile_debug` | lines 583846+ | Combat sync timing / debug | absent |
| `g_unit_collide_process_frequency` (6), `g_unit_collide_{front,rear,side}_bound_rate`, `_bottom_box_*`, `_report_interval`, `sv_unit_collide_dameage_debug` | lines 585197-585310 | Ship/vehicle collision damage | absent |
| `swim_*_speed_mul`, `swim_buoy_speed`, `swim_jump_*`, `pl_fall_start_height/velocity`, `pl_fallDamage_Speed{Safe,Fatal,Bias}`, `g_walkMultiplier`, `g_godMode`, `fly_stance_enable` | table lines 581544-585478 | Player locomotion / fall damage constants | partial at best: movement algorithms exist (`ZoneActorMovementInputMath.cs`, `ZoneMovementFramePhases.cs`); no named CVar mapping; unverified whether constants match |
| `cd_cattle_update_distance`, `cd_furniture_update_distance`, `max_interaction_doodad_distance`, `dynamic_action_bar_distance`, `log_doodad_interaction` | same table | Doodad/housing update and interaction ranges | absent |
| `data_mining_report_interval`, `data_mining_perf_interval`, `data_mining_file_open`, `use_data_mining_manager` | lines 585312+ | Periodic perf/telemetry reports | absent |
| `auto_disconnect_timer`, `g_enableIdleCheck` | table | Idle disconnect | absent |
| `ai_systemupdate`, `ai_ignoreplayer`, `ai_DebugDraw`, `ai_DrawSpawner`, `sys_sleep_background` | strings 0x39FBD238 / 0x3A00E950 / 0x3A00E990; referenced as lookups in client cinema code (line 956965) | AI update toggles | absent (only the native AI provider gate in `ZoneNativeAiProvider.cs`) |
| `npc_move_sync_period`, `npc_move_net_delay`, `npc_move_skip_standing`, `npc_movement_skip`, `npc_sync_together`, `npc_enable_fastest_sync`, `net_actor_sync_period`, `net_actor_force_sync_period`, `npc_check_target_under_terrain`, `npc_area_spawner_collision_ckeck`, `npc_aggro_cancel_timeout`, `npc_disable_creation`, `npc_spawn_on_terrain` | strings 0x39FDC7D8..0x39FDD3C8 (help text at 0x39FDCA00: "-1 : use previous frame time, 0 : no delay, 1 ~ : msec constant"); NO .c xrefs | NPC movement sync cadence and spawn behavior (probable) | absent. `ZoneNpcMovementGate.cs`, `ZoneNpcSpawnCollision.cs`, `ZoneNpcUpdateSchedule.cs` hardcode their own values; unverified against these CVars |
| `sys_max_fps` | strings 0x3A117CC8 is only a table entry; the client `cvar2` has it at 0x39F1E818 | None found for the dedicate | not recovered; repo uses `framePeriodMilliseconds` in the runtime config, with a comment that it is "not a recovered native frame rate" |

### Coverage: GM and console commands

| Native item | Native evidence | Repo status |
| --- | --- | --- |
| `WZGmCommandPacket` dispatch `sub_393639D0` (0x393639D0): parse `Data` text (max 0x400), look up unit by id, switch on selector | `X2::GameServer::OnGmCommand` log "syntax error" / "no unit" at lines 720060-720067 | `ZoneUnitRegistry.ApplyGmCommand` (src/AAEmu.Zone/Units/ZoneUnitRegistry.cs:358) mirrors the parse-before-lookup order |
| selector 2 SetAttribute (`sub_390B2E50`), 3 ClearAttribute (`sub_390B2F10`), 0x1E Mode (`sub_390B2FB0`, strings `X2::HandleGmSetAttribute/HandleGmMode`) | lines 161259-161320 | implemented (ZoneUnitRegistry.cs:361-375, `GmCommand.cs` mode names `invincible,cooldown,combatlog,almighty,debug_plot,test_siege,live,zone_permission,test_integration`) |
| selector 0x31 (49) `HandleGmSkill` `sub_39454180`: fires skill id on unit when `a1+24==1` | line 919841 | throws `NotSupportedException` (ZoneUnitRegistry.cs:377-378) |
| selector 0x8E (142) `HandleGmSearchNpc` `sub_39454400` (1-2 args) | line 919930+ | throws `NotSupportedException` |
| selector 0x8F (143) `sub_39454230` (calls `sub_393200D0(... ,14)` on the unit's component at +104) | line 919875 | throws `NotSupportedException` |
| selector 0x90 (144) `HandleGmMoveNpc` `sub_39454260` (x,y,z world coords minus cell origin `<<10`, then `sub_3933B510`) | line 919889 | throws `NotSupportedException` |
| Console: `npc_abuser`, `npc_follow_path`, `npc_follow_unit`, `npc_run_ai_command_set`, `npc_spawner_event` handlers `CmdNpcAbuser` 0x393413C0, `CmdNpcFollowPath` 0x39342C90, `CmdNpcFollowUnit` 0x39342B30, `CmdNpcRunAiCommandSet` 0x393416A0, `CmdNpcSpawnerEvent` 0x39349B90; `NpcSpawner::SpawnForDev` 0x3933EDF0 | lines 690887-698080, 688986 | absent (no console at all) |
| Console table `sub_392B8D70`: `quit goto gotoe dialog_task_count test_ars test_login_deny game_provider start_packet_profile dump_auth_feature_set dump_auth_characters dump_hack_checker_history run_sleeper` | lines 586662-586814; `run_sleeper` handler `sub_392AC660` line ~577510 (Stop / Random / Impulse lag simulation) | absent; `ZoneNativeEngineConsole.cs` only allocates CONIN$/CONOUT$ for the native engine |
| `doodad_dump <id>`, `net_dump_movement` ("dump statistics of outgoing movement packets"), `move_dump` | line 918618 (`doodad_dump`); strings 0x39FDD1E0, 0x39FBA200 | absent |
| Other GM paths (`SC_PACKET_GM_*` dumps, `gm_target/gm_unit/gm_watch`) | client-side strings only (0x3A0142C0.., 0x3A021958..) | N/A for the zone (World/client side) |

### Coverage: process, host, logging

| Native item | Native evidence | Repo status |
| --- | --- | --- |
| Loads `cryaction_dedicate.dll` | line 2130958 | `ZoneNativeEngine.cs` + `ZoneNativeProcessOwnership.cs` own native modules |
| World connect and join: "zone: client connected to world", `ZWJoinPacket` with `sv_port`, `vpn_external_ip`, `cl_account_id`, `instance_id` | `sub_393652F0`..`sub_393648D0` lines 721986-722040, 720712 | `ZoneWorldClient.SynchronizeAsync` + `ZoneJoin`; no `vpn_external_ip`/instance support |
| Join refusal: `X2Error("zone: world server refused joining(%s)")` with reason table `off_3A51AB48` | 0x393648D0 (line 720700) | partially; verify `ZoneWorldClient.cs` surfaces named reasons (not checked in depth) |
| World disconnect handler `sub_3936D7E0`: logs "zone: client disconnected (reason=%d)", clears state, and for modes 2/3 issues console command `quit` (graceful process exit) | line 726433 | the host runs "until cancel or World EOF" (Program.cs (pre-restructure):200-203); exit code 0 on EOF. Reason code is not logged |
| Overflow guard "dropped movement unit %u - exceeded 1000" in per-frame movement queue (`sub_3936D860`, 336-byte slots) | line 726455 | check `ZoneMovementOutgoing.cs` for a cap (not verified) |
| Net thread wait CVars (`net_thread_min/max/random_wait_time`) validated at connect ("bad combination of net thread wait times") in `sub_393654B0` | line ~721270 | absent (managed async receive; no equivalent) |
| Logging: `X2Log(level,...)` levels 1/2/3 and `X2LogPrefixed(3,"X2::GameServer::...")`, `X2Error`; engine log at `zone-host.log` name | throughout .c | `ZoneConsoleLog.cs` tee to `zone-<utc>-<pid>-<guid>.log` (no levels, no rotation, no categories); engine log name from `CreateComposedEngineInputs(... "zone-host.log")` |
| Perf/monitoring: `data_mining_perf_interval`, `start_packet_profile` / `dump_packet_profile`, `net_input_dump` -> `netinput.log` (0x39FD4748), `g_profile`, `lua_loading_profiler` | strings/lines cited above | absent; no metrics or frame-time reporting |
| Process safety: native crash policy | no dump writer found in the dedicate (host stub is external) | repo: Ctrl+C only (`Console.CancelKeyPress`, Program.cs (pre-restructure):47-49); no `ProcessExit`/`PosixSignalRegistration`, no `AppDomain.UnhandledException`, no minidump |
| Threading | n/a (native is the engine's thread model) | `ZoneSimulationThread.cs` one owner thread + `BlockingCollection`; `ZoneSceneRunner` runs frames with `framePeriod` |

### Coverage: repo engineering

| Item | Evidence | Status |
| --- | --- | --- |
| CI | `.github/` does not exist | absent |
| Warnings policy | `Directory.Build.props` sets only TargetFramework, VersionPrefix, ImplicitUsings; `Nullable` is per-project; no `TreatWarningsAsErrors`, `AnalysisLevel`, `EnforceCodeStyleInBuild` | absent |
| Tests present (9 files, ~51 test methods; 1,070 lines) | ArchiveAes 1, ContentFormula 3, NativeSqliteVersion 1, NavigationPath 15, NavigationPathTrimming 11, PackedInteger 2, RuntimeConfiguration 2, TerrainHeightField 7, ZoneFrame 9 | thin |
| Subsystems with ZERO tests | Protocol messages other than framing (GmCommand, UnitDuelState, ConflictZoneState...), ZoneUnitRegistry, ZoneSession (receive dispatch), ZoneWorldClient (sync), NPC spawner/manager/AI/aggro/signals (`ZoneNpc*`, `ZoneAggro*`, `ZoneGoal*`), buffs/stats/equipment/reinforcement/gear score, loot/achievement/faction/combat relations, movement controller and math (`ZoneActorMovement*`), `ZoneSceneHost`/`Runner`/`Coordinator`/`SimulationThread`/`SimulationTick`, `ZoneConsoleLog`, `Program` CLI parsing, scripting (Lua) | absent |
| Docs | README.md, TODO.md, docs/LICENSING-PROVENANCE.md, AAEmu.Zone/docs/{unit-snapshots,unit-stats}.md; no ops/runbook, no CLI reference beyond `--help`, `FIRST_ENTRY_NOTES.md` omitted | thin |

### Tasks

### OPS-01: Refresh H1 status and prove `--host` end-to-end against an isolated World
- Status: **Partial** — protocol-level fake World and sync tests; live --host run against World still open
- Priority: P0
- Native evidence: join sequence `sub_393652F0`..`sub_393648D0` ("zone: client connected to world", "zone: successfully joined world server", refusal at 0x393648D0); disconnect `sub_3936D7E0` ("zone: client disconnected (reason=%d)").
- Repo state: `Program.cs (pre-restructure)` already has `--host` (lines 57-62 help, 192-232 `RunHostAsync`) -> `ZoneSceneHost.RunAsync` (src/AAEmu.Zone/Hosting/ZoneSceneHost.cs:89) -> `ZoneSceneRunner.RunAsync`. TODO.md H1 still says "no persistent gameplay launch mode". It is untested: no test, and no recorded successful run. The TODO text is stale.
- Work:
  - Correct TODO.md H1 to reflect the existing `--host` path and list what is actually missing (player actor, movement publication).
  - Run `--host` against an isolated World stub or test World; record which phase first fails (load -> readiness -> adopt -> receive).
  - Add a protocol-level fake World (in-process TCP) test that drives sync + `ZWZoneLoaded` + clean disconnect so the host can be regression-tested without the game.
  - Log the disconnect reason code and the join-refusal reason name on exit paths.
- Depends on: S1 deferred-pass admission (scene `Ready`), P1/M1 for anything beyond load.
- Confidence: high

### OPS-02: Write the E1 client-entry runbook plus evidence-capture script
- Priority: P0
- Native evidence: native zone entry log lines ("zone: client connected to world", "zone: successfully joined world server"); `ZWZoneLoaded` flow in `sub_393648D0`.
- Repo state: TODO.md E1 is a checklist only (lines 193-203); README says internal launch details are omitted. No script or doc to record commit, command, config, endpoints and logs.
- Work:
  - Add `docs/E1-entry-check.md` (or section in README) with: config template, command line, World endpoint, log locations (`ZoneConsoleLog` path + engine `zone-host.log`), pass/fail table per E1 bullet.
  - Add a small script that snapshots `git rev-parse HEAD`, `Runtime.json`, the zone log path and exit code into one folder outside the checkout.
  - Capture first-failing-event conventions: reuse the `FrameworkPhases` list (Program.cs (pre-restructure)) for the host path.
- Depends on: OPS-01
- Confidence: medium

### OPS-03: Graceful shutdown, signal, and crash handling for the host
- Status: **Partial** — SIGTERM/SIGHUP/close retire like Ctrl+C, unhandled exceptions logged, exit codes 0/1/130 documented; crash dumps and log rotation open
- Priority: P1
- Native evidence: World disconnect triggers console command `quit` for modes 2/3 (`sub_3936D7E0` at 0x3936D7E0, calls vtable +296 with "quit"); `bot_restart_after_crash` CVar (0x39FC7BC8) shows crash-restart is an external supervisor concern.
- Repo state: only `Console.CancelKeyPress` (Program.cs (pre-restructure):47-49) mapped to exit 130; no `AppDomain.UnhandledException`, `ProcessExit`, `PosixSignalRegistration` (SIGTERM/SIGHUP/console close event); `ZoneNativeEngineConsole.cs` allocates a hidden console that is not tied to CTRL_CLOSE_EVENT.
- Work:
  - Hook `AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException` to write a final line to the run log with exception, phase and engine state before exit.
  - Register `PosixSignalRegistration` for SIGTERM/SIGHUP/SIGQUIT and the Windows console-close event to cancel `shutdown` and wait for ordered retirement (native engine teardown order is a recorded requirement).
  - Define exit-code table (0 EOF clean, 1 error, 130 cancel, a distinct code for World refusal/EOF) and document it in `--help`.
  - Optional: write a minidump on unhandled native fault (access violation) since the native engine can crash (PostInit AV is in TODO history).
- Depends on: OPS-01
- Confidence: medium

### OPS-04: Add a ServerCVar configuration layer with the sim-affecting native CVars
- Priority: P1
- Native evidence: `sub_392B1240` (0x392B1240) registration; `movement_verify_*` (lines ~584933-585190, defaults e.g. enable=0, target_max=100, sample_max=200, speed_sample_min=50, max_packet_interval=1000, ignore_msec_after_skill_controller=2000, levitation_hack_buff_start_time=5000), `max_unit_in_world`=100000 (line 583566), `sv_input_timeout` (line 583431), `combat_sync_framehold` (line 583846), `gm_startup` (line 583921), `g_unit_collide_process_frequency`=6 (line 585197).
- Repo state: absent. `Runtime.example.json` has only paths, zone, world endpoint, locale, `framePeriodMilliseconds`, backends, `terrainTexturePoolSize`, `scriptRoot`. `ZoneRuntimeConfiguration.cs` has no `cvars` object. Constants are hardcoded across `Simulation/`.
- Work:
  - Add optional `cvars` map to the runtime JSON, validated against a registry (name, type, native default, source address); unknown names are an error.
  - Seed the registry from the table above (defaults extracted from the dedicate; re-verify each default against `sub_392B1240` before trusting).
  - Plumb the values to consumers as they come online (movement verifier, unit cap, input timeout, collision constants) rather than as hidden constants.
  - Log the effective values at startup, next to the existing "Content verified" lines.
- Depends on: none
- Confidence: medium (defaults partly parsed from decompile arg order)

### OPS-05: Implement movement verification / hack checker (anti-cheat) or explicitly record it as out of scope
- Priority: P2
- Native evidence: ~33 `movement_verify_*` CVars (lines 584933-585190); reporter format string "Speed hack detected%s. %s %s Err(%.1fpct=%d/%d) ErrST(%s) CP(%.1f) Skip(%.1fpct,%d) ..." at line 478374; console `dump_hack_checker_history [unitId] [dumpType]` (blink / SkillController history) line 477670/586796; default `movement_verify_enable` = 0, so native ships it off.
- Repo state: absent. No file mentions `movement_verify` or hack checker.
- Work:
  - Decide whether to port: it is off by default natively, so it does not block the first-entry milestone.
  - If ported, hang it off `ZoneUnitRegistry` movement updates and the `dump_hack_checker_history` command; record the five checks (gravity, onground, speed, airstanding, move-speed sampling) from the CVar names.
  - If not, document it in TODO under "Park" with the addresses.
- Depends on: M1, OPS-04
- Confidence: medium

### OPS-06: Implement the four unsupported zone-side GM selectors (49, 142, 143, 144) without crashing the session
- Status: **Partial** — no selector throws; native arity/kind checks ported, 49/142/143/144 effects log by name pending skill, NPC search reply, NPC mode and NPC teleport subsystems
- Priority: P1
- Native evidence: dispatcher `sub_393639D0` switch cases 0x31, 0x8E, 0x8F, 0x90 -> `sub_39454180` (HandleGmSkill: fire skill id on unit via vtable +80 of the IGame sub-object at gEnv+8 offset 912), `sub_39454400` (HandleGmSearchNpc, 1-2 args), `sub_39454230` (calls `sub_393200D0(unit+104, 14)`), `sub_39454260` (HandleGmMoveNpc: 3 floats minus cell origin `<<10`, then `sub_3933B510`).
- Repo state: `ZoneUnitRegistry.cs:376-378` throws `NotSupportedException` for these four after the id lookup succeeds. `ZoneSession.ReceiveCore` (ZoneSession.cs:326-328) does not catch it, so one GM packet could fault the receive loop.
- Work:
  - Immediately: replace the throw with a logged "unhandled GM selector N" no-op (native ignores unknown selectors silently) and add a unit test.
  - Implement 144 (move NPC) and 143 first (small); 49 (GM skill) depends on skill fire path; 142 (search NPC) feeds a client reply, check what it does after the loop at line 919930+.
  - Reuse `GmCommandData` parser; add tests for each argument-count branch (native logs "unexpected" via `X2LogPrefixed(3, ...)` on bad arity).
- Depends on: NPC manager (`ZoneNpcManager.cs`) for 142/144
- Confidence: high for the throw risk; medium for semantics of 143

### OPS-07: Provide a minimal server console (stdin) with the native command set
- Priority: P2
- Native evidence: command table `sub_392B8D70` (quit, goto, gotoe, dialog_task_count, test_ars, test_login_deny, game_provider, start_packet_profile, dump_auth_feature_set, dump_auth_characters, dump_hack_checker_history, run_sleeper), `doodad_dump` (line 918618), NPC commands `CmdNpc*` (0x393413C0, 0x39342C90, 0x39342B30, 0x393416A0, 0x39349B90), `SpawnForDev` 0x3933EDF0, `NpcManager::LoadSpawners` 0x3934AD80.
- Repo state: absent. `ZoneNativeEngineConsole.cs` is only a Win32 handle setup for the native engine.
- Work:
  - Add a read-only stdin command loop on a non-simulation thread posting to the simulation owner (`ZoneSimulationThread.Post`) with `quit`, `status`, `dump_units`, `npc_spawner_event`.
  - Port `quit` first (it is the native shutdown path on World loss).
  - Keep it disabled unless `--console` is given.
- Depends on: OPS-01
- Confidence: medium

### OPS-08: Structured log levels, channels, and rotation
- Priority: P2
- Native evidence: `X2Log(level, ...)` with levels 1/2/3, `X2LogPrefixed(3, "X2::GameServer::<Class>::<Fn>", msg)` pattern (e.g. `X2::GameServer::OnGmCommand`), `X2Error`; per-feature logs `netinput.log` (0x39FD4748), movement dump file ("Movement dumps are saved to file '%s'", 0x39FB9F88).
- Repo state: `ZoneConsoleLog.cs` (41 lines) tees stdout/stderr into one `zone-<utc>-<pid>-<guid>.log`; no levels, categories, size cap, retention; host `trace` is `Console.WriteLine` with a timestamp (Program.cs (pre-restructure), `RunHostAsync`); engine log name `zone-host.log`.
- Work:
  - Introduce `ILogger`-style wrapper with level + category and keep the tee file sink.
  - Add retention (delete logs older than N days / total size cap) in `LogDirectory`.
  - Write a one-line "run header" (commit, config path, args, OS, .NET version, native module hashes from `ZoneNativeEngine.Identities.cs`).
- Depends on: none
- Confidence: high

### OPS-09: Frame-time and tick metrics
- Priority: P2
- Native evidence: `data_mining_perf_interval` / `data_mining_report_interval` CVars (line 585312), `start_packet_profile` and `dump_packet_profile`, `g_profile`, "An interval of AI time event is too short..." (0x39F91030). The native frame rate is NOT recovered from the dedicate (`sys_max_fps` is client only).
- Repo state: `ZoneSceneRunner` uses `framePeriod`; `ZoneSimulationTick.cs` (148 lines) has no timing instrumentation; no overrun warning.
- Work:
  - Measure per-frame duration and overrun count in `ZoneSceneRunner`; emit a periodic summary (p50/p99, overruns, queue depth of `ZoneSimulationThread`).
  - Record outbound packet counts per opcode (packet-profile equivalent).
  - Investigate the native main loop (host process exports; `archeage.exe.c` / the stub host) to recover the real frame period before fixing a default.
- Depends on: OPS-01
- Confidence: medium

### OPS-10: Remove the hardcoded Lua library path and external-environment assumptions
- Status: **Done** — `luaLibraryPath` config key (relative to the config file), `AAEMU_ZONE_LUA_LIBRARY` override, no default (`ZoneSceneHost.ResolveLuaLibrary`); stock LNUM `"double int64"` accepted; README names LuaDist/lualnum.
- Priority: P1
- Native evidence: n/a (repo-only finding).
- Repo state: `ZoneSceneHost.cs` line ~68: `DefaultLuaLibrary = @"C:\AAEmuRuntime\luatools\lua-5.1.1\x64\lua51.dll"` with env override `AAEMU_ZONE_LUA_LIBRARY`; error message cites `FIRST_ENTRY_NOTES:711` (file not in the publication). `Runtime.example.json` has no `luaLibrary` key.
- Work:
  - Add `luaLibraryPath` to the runtime JSON; keep the env override; no default pointing to a developer drive.
  - Reword the error text to reference README instead of a missing note.
  - Add `RuntimeConfigurationTests` cases for the new key.
- Depends on: none
- Confidence: high

### OPS-11: Add `instance_id` / `instance_index` / external IP to the join and config
- Priority: P2
- Native evidence: join builder reads `sv_port`, `vpn_external_ip` and, depending on mode (`case 1` account via `cl_account_id`, `case 2` via `instance_id`) lines 722000-722040; `instance_id`/`instance_index` CVars at `sub_392B1240`.
- Repo state: `Program.cs (pre-restructure)` builds `new ZoneJoin(ZoneJoin.NativeProtocolVersion, ..., zone.Key, 0, 0, 0, 0, false)`; no config for instances.
- Work:
  - Add `instanceId`, `instanceIndex`, `externalIp`, `listenPort` options to config and thread them into `ZoneJoin`.
  - Test the encoded join packet byte-for-byte against the native layout (`ZWJoinPacket` vtable build in `sub_393648D0` neighborhood).
- Depends on: OPS-04
- Confidence: low (field order only partly read)

### OPS-12: CI pipeline (build + selected tests + warnings gate)
- Priority: P1
- Native evidence: n/a (repo-only).
- Repo state: no `.github/`; README documents `dotnet build` and `dotnet test --project ... ` commands (TUnit / Microsoft.Testing.Platform, `global.json`).
- Work:
  - Add `.github/workflows/ci.yml` (windows-latest; .NET 10 SDK; restore, build `AAEmu.Zone`, run tests with the README flags and `--artifacts-path`).
  - Add NuGet vulnerability audit step (the README mentions a pinned vulnerable native SQLite).
  - Add a secrets/paths lint: fail if checked-in files contain `C:\AAEmuRuntime` or other local paths (currently present in `ZoneSceneHost.cs` and `Runtime.example.json`).
- Depends on: none
- Confidence: high

### OPS-13: Warnings and analyzers policy
- Priority: P2
- Native evidence: n/a.
- Repo state: `Directory.Build.props` lacks `Nullable`, `TreatWarningsAsErrors`, `AnalysisLevel`, `Deterministic`, `EnforceCodeStyleInBuild`; `Nullable` set per csproj; `AllowUnsafeBlocks` is presumably set in AAEmu.Zone.csproj (unsafe native code), unreviewed.
- Work:
  - Move `Nullable=enable` into `Directory.Build.props`; enable `TreatWarningsAsErrors` for new warnings via a baseline (`WarningsNotAsErrors` list) so the 67k-line code base can be adopted incrementally.
  - Enable `AnalysisLevel=latest-recommended` and `EnforceCodeStyleInBuild`; add `.editorconfig`.
  - Add source-link / deterministic build properties for reproducible binaries.
- Depends on: OPS-12
- Confidence: medium

### OPS-14: Close test gaps for the highest-risk subsystems
- Priority: P1
- Native evidence: wire handlers the tests would pin, e.g. GM dispatcher 0x393639D0, join 0x393648D0, movement queue guard 0x3936D860 ("dropped movement unit %u - exceeded %d", cap 1000).
- Repo state: zero tests for ZoneUnitRegistry/ZoneSession dispatch, protocol messages beyond `ZoneFrame`, `ZoneWorldClient` sync, NPC spawners/manager/aggro/goals, buffs/stats/equipment, loot/achievements/factions, actor movement math, `ZoneSceneHost/Runner/SimulationThread/Tick`, `ZoneConsoleLog`, `Program.ParseArguments`. Existing: ~51 test methods in 9 files.
- Work:
  - Protocol round-trip tests for every message in `AAEmu.Zone.Protocol/Messages` (encode -> decode -> compare), starting with `GmCommand`, `GmCommandData`, `ZoneJoin`.
  - `ZoneUnitRegistry.ApplyGmCommand` tests: selectors 2/3/30, unknown selector, unit missing, oversized params (>1023), parse failure.
  - Fake-World in-process test of `ZoneWorldClient.SynchronizeAsync` including a refusal case.
  - `ZoneSimulationThread` tests (cancellation, callback exceptions, no leaked tasks) and a `ZoneSceneRunner` frame-period test with a fake clock.
  - CLI parsing tests for `--host` named-error paths (Program.cs (pre-restructure):57-65).
- Depends on: none (OPS-12 to run them automatically)
- Confidence: high

### OPS-15: Operations documentation
- Priority: P2
- Native evidence: n/a.
- Repo state: README (build, runtime inputs), TODO.md (roadmap, stale H1), docs/LICENSING-PROVENANCE.md; internal notes (FIRST_ENTRY_NOTES.md) are referenced in code comments but not shipped; `--help` is the only CLI reference.
- Work:
  - Add `docs/ops.md`: CLI modes and exit codes, runtime JSON schema (all keys incl. `framePeriodMilliseconds`, `terrainTexturePoolSize`, `physicsBackend`, `aiBackend`, `aiDependencyGated`, `scriptRoot`), log locations, env vars (`AAEMU_ZONE_LUA_LIBRARY`).
  - Add a CVar reference generated from the OPS-04 registry (name, default, native address, repo consumer).
  - Replace code-comment references to unshipped notes with in-repo docs.
- Depends on: OPS-04, OPS-10
- Confidence: high
