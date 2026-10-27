# Zone server task list

This repository becomes the **zone server for AAEmu 10.x**. World runs it in place of the native dedicated zone (`dedicatedserver.exe` host + `x2game-dev_dedicate.dll`). This file lists what is still missing, ordered by what blocks that goal first.

```
 ArcheAge 10.0.2.13 client
          │  game protocol (client ⇄ World only; the client never talks to a zone)
          ▼
 AAEmu-10.x World  (github.com/AAEmu/AAEmu-10.x, AAEmu.WorldServer)
          │  ZoneHostSupervisor launches the zone process:
          │    -dedicated +world_ip +world_port +zone +sv_map +instance +sv_port +db_location …
          │    env AAEMU_ZONE_DLL / AAEMU_ZONE_SAVE_DIR / AAEMU_ZONE_LOG_NAME
          │  WZ/ZW frames on the zone port (default 1240)
          ▼
 AAEmu.ZoneServer  (this repo, src/AAEmu.ZoneServer → src/AAEmu.Zone)
    replaces:  the dedicated host + x2game-dev_dedicate.dll (all X2 game logic; ZoneNativeEngine
               refuses to load any x2game module)
    still hosts: CryEngine *_dedicate engine modules from the client's Bin64 (system, physics,
               3D engine/terrain, entity system, CryAction, CryAISystem) for scene, physics and AI
```

## How this list was made

On 2026-10-27, 13 parallel audits compared three things:

- **The original zone.** The Hex-Rays export of the client's `Bin64/x2game-dev_dedicate.dll` (ArcheAge 10.0.2.13 r575, 89,574 functions) and its string/RTTI/CVar dumps. Every task cites native function addresses, strings or RTTI names.
- **This repository** at `76e439f` (before the restructure in this change).
- **The World server.** [AAEmu/AAEmu-10.x](https://github.com/AAEmu/AAEmu-10.x) at `7853366`, for the launch and WZ/ZW contract.

Each area report in [`docs/tasks/`](docs/tasks) has a coverage table, the evidence and the full task bodies. Repo paths in those reports are rewritten to the `src/` layout. Their line numbers refer to `76e439f`. Priorities were assigned by the auditors and reconciled below. Treat "confidence: low/medium" items as hypotheses until checked.

Nothing in this list is validated against a running client. The recorded check results in [docs/FIRST-ENTRY-ROADMAP.md](docs/FIRST-ENTRY-ROADMAP.md) are historical observations.

## Coverage snapshot

| Area | What exists | Biggest gaps | Report |
| --- | --- | --- | --- |
| World ↔ Zone contract | Framing, WZ opcode values, join and startup sync (factions, relations, spawners, time) match World byte-for-byte | Static (continent) zones have no launcher. Instance and template mapping are unverified. Coordinate frame on the wire is ambiguous | [world-contract](docs/tasks/world-contract.md) |
| WZ inbound | 133/133 opcodes defined; 56 decoded and dispatched | 77 fall to the "unimplemented" counter (skills, damage, doodads, gimmicks, slaves, attach/bond, reply packets). Some handlers throw and can end the session | [wz-inbound](docs/tasks/wz-inbound.md) |
| ZW outbound | 45/58 opcodes defined and matching; ~20 serializers; ~17 emitted | `ZWHeartbeat` and `ZWUnitLocation` never sent. No skill, area, gimmick or report packets. No replies to World requests | [zw-outbound](docs/tasks/zw-outbound.md) |
| Framework / scene / level | Composed engine start and stop; 20 FlowNode factories verified exact; six-cell load with ground query; deferred pass now coded | 148/152 IGame slots `FailFast`. `InitGameFactory` (Player, CPlayer, Monster, Quadruped, Tornado, Rain, WindArea, ScriptControlledPhysics) absent. Area loading and static gimmick spawns absent | [scene](docs/tasks/scene.md) |
| Players / movement / physics / navigation | Unit registry, spatial index, movement receive, correction and sampling, prephase/step/postphase coordinator, ZWUnitMovements encoder, living-body wrapper | Player creation has 4 of 5 native dependencies unresolved. The pose-write seam is open. No fall, swim, glide, ladder, mount or hack-checker. NPC pathfinding unfinished | [movement](docs/tasks/movement.md) |
| World objects | Opcode constants only | Doodads, gimmicks (incl. the zone 129 ServerOnly elevator), attach/hang/bond, housing, slaves, ships, transfers | [world-objects](docs/tasks/world-objects.md) |
| NPC AI | Aggro table, spawners and groups, signals, timers, talks, posture, part of the goal-pipe engine | Native AI provider gated (CAISystem instance unrecovered); managed AI provider unwritten; ~25/43 goal ops throw; no locomotion, leash or skill loop | [npc-ai](docs/tasks/npc-ai.md) |
| Lua API | Lua 5.1 host; `Script` 3/11; `AI` 14/170; 488 AI constants | Nothing beyond `Script` is installed in production. `ScriptBindUnit` 0/64. Most native→Lua signals never sent. No entity-script host | [lua-api](docs/tasks/lua-api.md) |
| Combat (zone side) | Stats and formulas ~85%; buff stacking and timing; gear score | Skill-state mirror, buff side effects and CC flags, cooldowns, skill controllers and NPC skill use ≈0%. World computes effects; the zone mirrors them | [combat](docs/tasks/combat.md) |
| Content DB | 96 of 986 tables the native zone loads (~10%), several partially | `constants`, `options`, `return_points`, `sub_zones`, `characters`. Full skills, buffs, effects and plots; doodad funcs; quest/district area XMLs | [content-db](docs/tasks/content-db.md) |
| World state | Time of day, clock, factions and relations, dominion and siege, schedules, spawner persistence, content filter | Area enter/leave (`ZWEnterArea`/`ZWLeaveArea`), tower defense, housing state; state not yet consulted by combat/PvP | [world-state](docs/tasks/world-state.md) |
| Ops | Persistent host path (`ZoneSceneHost`) exists | No CVar layer; GM selectors 49/142–144 throw; no crash or signal handling, CI or structured logs; tests cover ~10 subsystems out of ~20 | [ops](docs/tasks/ops.md) |
| Cross-cutting RTTI | 23 `X2::GameServer::*` + ~134 shared server classes classified | `ServerUnit`/`ServerCharacter`/`CharacterManager` layer, `ServerAreaManager`, skill controllers, `ZoneBuffMan`, doodad runtime. The 311 `GameClient` and ~120 `ScriptBind*Addon` classes are client-only and out of scope | [rtti-inventory](docs/tasks/rtti-inventory.md) |

Totals: **183 tasks**: 30 P0, 89 P1, 64 P2.

## Done in this change (restructure branch)

- **Repository restructured as a zone server.** The layout is now `src/` (Commons, Protocol, the `AAEmu.Zone` library and the `AAEmu.ZoneServer` executable), `tools/AAEmu.Zone.Diagnostics` (the former `--check-*`/`--probe-*` modes) and `tests/`, with `AAEmu.Zone.slnx`.
  - The 250-file `Simulation` folder is split into domain folders and namespaces: `Hosting`, `WorldLink`, `Scene`, `Terrain`, `Physics`, `Units`, `Actors`, `Navigation`, `Npcs`, `Ai`, `Combat`, `WorldState`, `Diagnostics` and `Native/{Engine,Entities,FlowNodes,StaticObjects,Physics,Ai}`.
  - Source `File.cs:N` citations were shifted to stay exact. The full old→new map is in `.restructure-map.tsv`.
- **WC-01 (done).** `AAEmu.ZoneServer` accepts World's `-dedicated +cvar value` launch. Repeated CVars keep the last value, and unconsumed CVars are logged. A test is built from `ZoneHostCommandLine.Build`'s argument list.
- **WC-04 (mostly done).** The base config is found from `--config`, `+zone_config`, `AAEMU_ZONE_CONFIG` or `zone.json` beside the executable.
  - `+world_ip/+world_port`, `+zone`, `+db_location` and `AAEMU_ZONE_SAVE_DIR`/`AAEMU_ZONE_LOG_NAME` overlay it (`ZoneRuntimeConfiguration.WithLaunch`). The zone key always comes from the content DB.
  - `npc_spawners.g` now defaults to `game/worlds/<world>/level_design/zone/<key>/zone_server/npc_spawners.g` from the assets (World's resolver layout). `spawnerFile`/`--spawner-file` override it.
  - *Open:* deriving the world map from the zone instead of the fixed `worldMapAsset`.
- **WC-02 / ZW-02 (partial).** ZWJoin now carries `+instance` as `InstanceId` and `+sv_port` as `Port`. *Open:* confirm that World's template name equals the content zone name, and implement the native server-type switch (account id / external IP).
- **WC-03 (partial).** The zone side ships an executable World's `ZoneHost.Executable` can point at. *Open:* make World's `NativeDll` preflight optional (World change), or point it at a placeholder.

## Milestones

### M0 — World can launch and keep a zone host

Goal: World starts `AAEmu.ZoneServer` for an instance and for the starter continent zone. The host reaches `ZWZoneLoaded` and stays connected until it is told to stop.

1. **Launch contract:** finish WC-02, WC-03 and WC-04 (see above); WC-05 (a launcher or World-side list for static continent zones; nothing starts them today); WC-07 (World detects early host death).
2. **Keep the session alive:** ZW-01 (send `ZWHeartbeat` every second); WZ-01 (no WZ packet may fault the session, mirroring native handlers that always `return 1`); OPS-06 (GM selectors 49/142–144); ZW-03 (native disconnect policy: types 2/3 `quit`).
3. **Scene readiness:** SCN-03 (re-run the scene check and confirm `ZoneSceneLoadReport.Ready`); SCN-01 (bind or truthfully stub the 148 unbound IGame slots so no engine call `FailFast`s); DB-01 (`constants`, `options`, `return_points`, `sub_zones`, `characters`).
4. **Prove it:** OPS-01 (run the host against an isolated World; add an in-process fake-World test for sync → `ZWZoneLoaded` → disconnect); OPS-03 (graceful shutdown on World's `Kill`, crash logging).

### M1 — First player entry on zone 129

Goal: one player enters, stands on terrain, walks, turns, jumps, logs out and re-enters (the E1 check).

- **Player actor:** RT-01 (`ServerUnit`/`ServerCharacter`/`CharacterManager`); SCN-02 (port `InitGameFactory`: "Player" extension and CPlayer actor class); MOV-01 and MOV-02 (CPlayer publication; GameObject/ActorSystem/AnimatedCharacter); MOV-14 (admit, place, retire and re-admit end to end).
- **Physics and movement:** MOV-04 (close the pose-write seam); MOV-05 (living-body parameters and ground contact); MOV-06 (bind the production movement services in the host loop); MOV-07 (complete `ZWUnitMovements`); WC-06 (continent vs zone-local coordinates on the wire, per World's `AAEMU_WZ_UNIT_POS_*`); WZ-02 (native movement gate and first-entry WZ order).
- **Standing on things:** OBJ-01 (gimmick and doodad ground objects); OBJ-02 (the zone 129 ServerOnly elevator graph); OBJ-03 (gimmick WZ/ZW packets).
- **Vitals:** CMB-01 (the stat calculator completes for a real equipped character).
- **Entry check:** OPS-02 (E1 runbook and evidence capture).
- **NPCs present in zone 129:** NPCs are not required to walk, but if spawning is on, LUA-01 to LUA-04 are P0. Without them every AI script load fails.

### M2 — Basic gameplay in a zone

- **NPC life:** AI-01 to AI-08, LUA-02 to LUA-07 and LUA-10, CMB-11 and CMB-12, MOV-15, WZ-06.
- **Combat mirror:** CMB-02 to CMB-10, CMB-13 to CMB-16, WZ-03 to WZ-05, ZW-06, ZW-08, RT-03.
- **World requests that expect a reply:** ZW-07 and WZ-11 (`RequestZoneStatus`/`ReportZoneStatus`, `RequestCombatUnits`, `RayCasting`, mole checks, `RunCommand`), RT-11.
- **Areas:** WS-01, ZW-05 and RT-02 (`ServerAreaManager`, `quest_area`/`district` XML, `ZWEnterArea`/`ZWLeaveArea`), SCN-04 to SCN-07, DB-09 and DB-10.
- **World objects:** OBJ-04, OBJ-06, WZ-07, DB-06, RT-04.
- **Content:** DB-02 to DB-08 and the remaining P1 DB tasks.
- **Ops:** OPS-04 (CVar layer), OPS-10 (no hard-coded Lua path), OPS-12 (CI), OPS-14 (tests), MOV-08 to MOV-10, MOV-13, MOV-18.

### M3 — Parity

Housing (OBJ-07, WS-05, RT-06), slaves, ships and vehicles (OBJ-08, OBJ-09, MOV-12, RT-07, WZ-08), mounts, transfers and portals (OBJ-10, MOV-11), gimmick physics (RT-05, ZW-09, WZ-09), tower defense and instances (WS-03, RT-10, SCN-12, SCN-13), hack checker (OPS-05, RT-08, ZW-10), plots and stealth (CMB-17 to CMB-22), and the remaining P2 items in the index below.

## Same work found by several auditors

Do each of these once. The listed IDs are the same work seen from different angles.

| Work | Task IDs |
| --- | --- |
| Join identity: instance id, sv_port, server type, external IP | WC-02, ZW-02, SCN-08, OPS-11 |
| Persistent host status and stale roadmap | OPS-01, SCN-03 (roadmap now marked historical) |
| `ScriptBindUnit` Lua table | LUA-02, AI-04, CMB-12 |
| NPC skill selection and use | AI-07, CMB-11 |
| Area manager and `ZWEnterArea`/`ZWLeaveArea` | WS-01, ZW-05, RT-02, SCN-05 |
| Heartbeat and missing ZW world-state frames | ZW-01, WS-04 |
| Unhandled WZ requests needing ZW replies | WZ-11, ZW-07, WS-02, RT-11 |
| Skill controllers and `ZoneBuffMan` | CMB-15, CMB-07, RT-03 |
| Doodad runtime and packets | OBJ-04, WZ-07, DB-06, RT-04 |
| Gimmicks | OBJ-01–03, WZ-09, ZW-09, RT-05 |
| Movement verification / hack checker | MOV-08, OPS-05, RT-08, ZW-10 |
| Player class layer and creation | RT-01, MOV-01, MOV-02, SCN-02 |
| Housing | OBJ-07, WS-05, RT-06 |
| Slaves, ships, vehicles | OBJ-08, OBJ-09, MOV-12, RT-07, WZ-08 |
| Tower defense | WS-03, AI-09, RT-10 |
| Skill and buff content columns | DB-03, CMB-04, CMB-08, RT-09 |

## Open questions to settle early

- **Static zones.** Who launches continent zones: a World-side list, or a launcher in this repo (WC-05)? Today an external Zone Manager that is in neither repo does it.
- **Coordinate frame.** Which frame do characters' ZW moves use with World's default settings (WC-06)? Settle it with a captured session.
- **Template names.** Does World's `+zone` template name always equal the content `zones.name` (WC-02)? Otherwise config needs a name-to-key table.
- **Player and actor classes.** Re-implement CPlayer/Monster/Quadruped in managed code, or keep a native creator path (SCN-02)? The engine currently refuses every x2game module by design.
- **AI backend.** Recover the CAISystem instance for the native AI provider (AI-01), or write the managed provider (AI-02)?

## Task index

Generated from the area reports. The links point to the full task bodies.

### P0 - blocks World launching the zone or first player entry

| ID | Task | Area |
| --- | --- | --- |
| WC-01 | Accept the native `-dedicated +cvar value` command line in the replacement host | [world-contract](docs/tasks/world-contract.md#wc-01-accept-the-native--dedicated-cvar-value-command-line-in-the-replacement-host) |
| WC-02 | Join with the `+instance` id and resolve the zone key from the `+zone` template name | [world-contract](docs/tasks/world-contract.md#wc-02-join-with-the-instance-id-and-resolve-the-zone-key-from-the-zone-template-name) |
| WC-03 | Make World's launch preflight accept a non-native host | [world-contract](docs/tasks/world-contract.md#wc-03-make-worlds-launch-preflight-accept-a-non-native-host) |
| WC-04 | Derive the runtime configuration from launch arguments and env instead of a json plus spawner-file per zone | [world-contract](docs/tasks/world-contract.md#wc-04-derive-the-runtime-configuration-from-launch-arguments-and-env-instead-of-a-json-plus-spawner-file-per-zone) |
| WC-05 | Provide a launcher for static (continent) zones | [world-contract](docs/tasks/world-contract.md#wc-05-provide-a-launcher-for-static-continent-zones) |
| WC-06 | Align coordinate frames and the movement flag with World's default (continent on the wire) | [world-contract](docs/tasks/world-contract.md#wc-06-align-coordinate-frames-and-the-movement-flag-with-worlds-default-continent-on-the-wire) |
| WZ-01 | Make WZ dispatch non-fatal and native-ordered for the first-entry path — **Partial** | [wz-inbound](docs/tasks/wz-inbound.md#wz-01-make-wz-dispatch-non-fatal-and-native-ordered-for-the-first-entry-path--partial-per-packet-containment-per-opcode-fault-counters-pre-load-gate-for-npcschedulespawnerevent-tests-unit-id-too-big-check-not-ported-max-id-sub_39258650-unrecovered) |
| WZ-02 | Verify the end-to-end first-entry WZ sequence against native handlers | [wz-inbound](docs/tasks/wz-inbound.md#wz-02-verify-the-end-to-end-first-entry-wz-sequence-against-native-handlers) |
| ZW-01 | Emit ZWHeartbeat once per second from the scene tick | [zw-outbound](docs/tasks/zw-outbound.md#zw-01-emit-zwheartbeat-once-per-second-from-the-scene-tick) |
| SCN-01 | Bind or truthfully stub the remaining 148 IGame slots | [scene](docs/tasks/scene.md#scn-01-bind-or-truthfully-stub-the-remaining-148-igame-slots) |
| SCN-02 | Port InitGameFactory (Player GOE + actor classes + 4 entity classes) | [scene](docs/tasks/scene.md#scn-02-port-initgamefactory-player-goe--actor-classes--4-entity-classes) |
| SCN-03 | Confirm scene readiness and refresh TODO.md S1/S2 | [scene](docs/tasks/scene.md#scn-03-confirm-scene-readiness-and-refresh-todomd-s1s2) |
| MOV-01 | Publish CPlayer through the RTTI class registry and bind the player-class owner | [movement](docs/tasks/movement.md#mov-01-publish-cplayer-through-the-rtti-class-registry-and-bind-the-player-class-owner) |
| MOV-02 | Compose GameObject / ActorSystem / AnimatedCharacter for the player entity | [movement](docs/tasks/movement.md#mov-02-compose-gameobject--actorsystem--animatedcharacter-for-the-player-entity) |
| MOV-04 | Close the pose-write seam: move a living body by the native action handler (or take ownership of the frame pump) | [movement](docs/tasks/movement.md#mov-04-close-the-pose-write-seam-move-a-living-body-by-the-native-action-handler-or-take-ownership-of-the-frame-pump) |
| MOV-05 | Wire real physics step with player living-body parameters (dimensions, gravity, dynamics) and ground contact | [movement](docs/tasks/movement.md#mov-05-wire-real-physics-step-with-player-living-body-parameters-dimensions-gravity-dynamics-and-ground-contact) |
| MOV-06 | Bind the production movement services (replace permissive leaves) and run prephase/step/postphase in the host loop | [movement](docs/tasks/movement.md#mov-06-bind-the-production-movement-services-replace-permissive-leaves-and-run-prephasesteppostphase-in-the-host-loop) |
| MOV-07 | Complete ZWUnitMovements publication (flags, optional blobs, zone-owned authoritative state) | [movement](docs/tasks/movement.md#mov-07-complete-zwunitmovements-publication-flags-optional-blobs-zone-owned-authoritative-state) |
| MOV-14 | Player admission, placement and re-entry end to end (P1 acceptance) | [movement](docs/tasks/movement.md#mov-14-player-admission-placement-and-re-entry-end-to-end-p1-acceptance) |
| OBJ-01 | Bind real static gimmick/doodad entities as ground objects for movement | [world-objects](docs/tasks/world-objects.md#obj-01-bind-real-static-gimmickdoodad-entities-as-ground-objects-for-movement) |
| OBJ-02 | Run the ServerOnly elevator/platform graph for zone 129 | [world-objects](docs/tasks/world-objects.md#obj-02-run-the-serveronly-elevatorplatform-graph-for-zone-129) |
| OBJ-03 | Implement WZ/ZW gimmick packet handlers and senders | [world-objects](docs/tasks/world-objects.md#obj-03-implement-wzzw-gimmick-packet-handlers-and-senders) |
| LUA-01 | Wire the existing AI Lua bindings into the scene bootstrap | [lua-api](docs/tasks/lua-api.md#lua-01-wire-the-existing-ai-lua-bindings-into-the-scene-bootstrap) |
| LUA-02 | Implement the `ScriptBindUnit` Lua table (64 methods plus RACE_/GENDER_ globals) | [lua-api](docs/tasks/lua-api.md#lua-02-implement-the-scriptbindunit-lua-table-64-methods-plus-race_gender_-globals) |
| LUA-04 | Dispatch the missing native-to-AI signals from the simulation | [lua-api](docs/tasks/lua-api.md#lua-04-dispatch-the-missing-native-to-ai-signals-from-the-simulation) |
| CMB-01 | Verify the stat/vital path completes for a real first-entry character | [combat](docs/tasks/combat.md#cmb-01-verify-the-statvital-path-completes-for-a-real-first-entry-character) |
| DB-01 | Add the missing global/config tables the first-entry path reads (constants, options, tags, return_points, sub_zones, characters) | [content-db](docs/tasks/content-db.md#db-01-add-the-missing-globalconfig-tables-the-first-entry-path-reads-constants-options-tags-return_points-sub_zones-characters) |
| OPS-01 | Refresh H1 status and prove `--host` end-to-end against an isolated World | [ops](docs/tasks/ops.md#ops-01-refresh-h1-status-and-prove---host-end-to-end-against-an-isolated-world) |
| OPS-02 | Write the E1 client-entry runbook plus evidence-capture script | [ops](docs/tasks/ops.md#ops-02-write-the-e1-client-entry-runbook-plus-evidence-capture-script) |
| RT-01 | Add ServerUnit / ServerCharacter / ServerUnitOwner class layer and CharacterManager | [rtti-inventory](docs/tasks/rtti-inventory.md#rt-01-add-serverunit--servercharacter--serverunitowner-class-layer-and-charactermanager) |

### P1 - basic gameplay in a zone

| ID | Task | Area |
| --- | --- | --- |
| WC-07 | Detect early host death and size ReadyTimeout for the replacement | [world-contract](docs/tasks/world-contract.md#wc-07-detect-early-host-death-and-size-readytimeout-for-the-replacement) |
| WC-08 | Handle `WZActivateNpcSpawnersInArea` (or prove NPCs spawn without it) | [world-contract](docs/tasks/world-contract.md#wc-08-handle-wzactivatenpcspawnersinarea-or-prove-npcs-spawn-without-it) |
| WC-09 | Cover the World→Zone opcodes that are currently ignored | [world-contract](docs/tasks/world-contract.md#wc-09-cover-the-worldzone-opcodes-that-are-currently-ignored) |
| WC-10 | Fill the ZW opcode gaps and sender coverage | [world-contract](docs/tasks/world-contract.md#wc-10-fill-the-zw-opcode-gaps-and-sender-coverage) |
| WC-11 | Golden-test WZUnitState, WZNpcState and buff bodies against World's serializer | [world-contract](docs/tasks/world-contract.md#wc-11-golden-test-wzunitstate-wznpcstate-and-buff-bodies-against-worlds-serializer) |
| WC-12 | Log to a file under `AAEMU_ZONE_SAVE_DIR` / `AAEMU_ZONE_LOG_NAME` and cope with no console | [world-contract](docs/tasks/world-contract.md#wc-12-log-to-a-file-under-aaemu_zone_save_dir--aaemu_zone_log_name-and-cope-with-no-console) |
| WZ-03 | Implement the unit attach/hang/bond/posture/skill-controller family | [wz-inbound](docs/tasks/wz-inbound.md#wz-03-implement-the-unit-attachhangbondpostureskill-controller-family) |
| WZ-04 | Skill / plot / combat-feedback packets | [wz-inbound](docs/tasks/wz-inbound.md#wz-04-skill--plot--combat-feedback-packets) |
| WZ-05 | Forced movement / physics unit packets | [wz-inbound](docs/tasks/wz-inbound.md#wz-05-forced-movement--physics-unit-packets) |
| WZ-06 | NPC / spawner control packets | [wz-inbound](docs/tasks/wz-inbound.md#wz-06-npc--spawner-control-packets) |
| WZ-07 | Doodad, house and vegetation packets | [wz-inbound](docs/tasks/wz-inbound.md#wz-07-doodad-house-and-vegetation-packets) |
| WZ-08 | Slave / vehicle / ship packets | [wz-inbound](docs/tasks/wz-inbound.md#wz-08-slave--vehicle--ship-packets) |
| WZ-11 | GM, command and diagnostics packets | [wz-inbound](docs/tasks/wz-inbound.md#wz-11-gm-command-and-diagnostics-packets) |
| ZW-02 | Send native-correct ZWJoin fields (sv_port, external IP, account/instance by server type) | [zw-outbound](docs/tasks/zw-outbound.md#zw-02-send-native-correct-zwjoin-fields-sv_port-external-ip-accountinstance-by-server-type) |
| ZW-03 | Match native disconnect/lifecycle policy — **Done** | [zw-outbound](docs/tasks/zw-outbound.md#zw-03-match-native-disconnectlifecycle-policy--done-disconnect--join-refusal-named-via-zoneworldlifecycle-world-eof-exits-with-code-3-for-server-type-2-refusal-strings-of-off_3a51ab48-still-positional) |
| ZW-04 | Implement ZWUnitLocation emission (replace the no-send UpdateUnitLocationReports) | [zw-outbound](docs/tasks/zw-outbound.md#zw-04-implement-zwunitlocation-emission-replace-the-no-send-updateunitlocationreports) |
| ZW-05 | Add ZWEnterArea / ZWLeaveArea | [zw-outbound](docs/tasks/zw-outbound.md#zw-05-add-zwenterarea--zwleavearea) |
| ZW-06 | Add skill ZW packets (StartSkill, StopSkill, StopCasting, SkillControllerState, LongDistSkillControllerCreated) | [zw-outbound](docs/tasks/zw-outbound.md#zw-06-add-skill-zw-packets-startskill-stopskill-stopcasting-skillcontrollerstate-longdistskillcontrollercreated) |
| ZW-07 | Handle World requests that expect a ZW reply (ZoneStatus, CombatUnits, RayCasting, Mole checks, RunCommand) | [zw-outbound](docs/tasks/zw-outbound.md#zw-07-handle-world-requests-that-expect-a-zw-reply-zonestatus-combatunits-raycasting-mole-checks-runcommand) |
| ZW-08 | Add unit-state and aggro ZW notifications (UnitFell, UnitModelPostureChanged, ChangeUnitFlyState, UnitCollision/Result, ErrorMsgToPlayer, AggroTargetChanged, KillNpc, ClearCombat, AggroRemove) | [zw-outbound](docs/tasks/zw-outbound.md#zw-08-add-unit-state-and-aggro-zw-notifications-unitfell-unitmodelposturechanged-changeunitflystate-unitcollisionresult-errormsgtoplayer-aggrotargetchanged-killnpc-clearcombat-aggroremove) |
| SCN-04 | Implement level-start server construction (event 12/1) and max-unit CVars | [scene](docs/tasks/scene.md#scn-04-implement-level-start-server-construction-event-121-and-max-unit-cvars) |
| SCN-05 | Implement ServerAreaManager::LoadAreas (quest_area.xml, district.xml) | [scene](docs/tasks/scene.md#scn-05-implement-serverareamanagerloadareas-quest_areaxml-districtxml) |
| SCN-06 | Implement static-gimmick request pass from loaded entities | [scene](docs/tasks/scene.md#scn-06-implement-static-gimmick-request-pass-from-loaded-entities) |
| SCN-07 | Server gimmick/doodad world objects and ZdModel physics models | [scene](docs/tasks/scene.md#scn-07-server-gimmickdoodad-world-objects-and-zdmodel-physics-models) |
| SCN-08 | Join identity per server type, server-type and environment slots | [scene](docs/tasks/scene.md#scn-08-join-identity-per-server-type-server-type-and-environment-slots) |
| SCN-09 | Derive npc_spawners.g from world+zone and honor native LoadSpawners gates | [scene](docs/tasks/scene.md#scn-09-derive-npc_spawnersg-from-worldzone-and-honor-native-loadspawners-gates) |
| SCN-10 | Complete flow-node provider bindings (F1 table) | [scene](docs/tasks/scene.md#scn-10-complete-flow-node-provider-bindings-f1-table) |
| SCN-14 | Native AI bounds and navigation load at level completion | [scene](docs/tasks/scene.md#scn-14-native-ai-bounds-and-navigation-load-at-level-completion) |
| MOV-03 | Bind real player operations (sensor listeners, staging, link, draw-hidden) | [movement](docs/tasks/movement.md#mov-03-bind-real-player-operations-sensor-listeners-staging-link-draw-hidden) |
| MOV-08 | Implement server-side movement verification (MoveHackChecker::MovementVerifier) | [movement](docs/tasks/movement.md#mov-08-implement-server-side-movement-verification-movehackcheckermovementverifier) |
| MOV-09 | Implement fall damage, freefall and landing | [movement](docs/tasks/movement.md#mov-09-implement-fall-damage-freefall-and-landing) |
| MOV-10 | Swim, water-volume and drowning movement | [movement](docs/tasks/movement.md#mov-10-swim-water-volume-and-drowning-movement) |
| MOV-13 | Unit warp, impulse, teleport and forced-sync corrections | [movement](docs/tasks/movement.md#mov-13-unit-warp-impulse-teleport-and-forced-sync-corrections) |
| MOV-15 | NPC path finding and movement controller | [movement](docs/tasks/movement.md#mov-15-npc-path-finding-and-movement-controller) |
| MOV-18 | Movement test coverage and a headless movement simulator | [movement](docs/tasks/movement.md#mov-18-movement-test-coverage-and-a-headless-movement-simulator) |
| OBJ-04 | Handle World doodad packets and unit attach/bond packets | [world-objects](docs/tasks/world-objects.md#obj-04-handle-world-doodad-packets-and-unit-attachbond-packets) |
| OBJ-06 | Climb doodads and hanging units | [world-objects](docs/tasks/world-objects.md#obj-06-climb-doodads-and-hanging-units) |
| AI-01 | Recover the CAISystem instance and unblock native AI construction | [npc-ai](docs/tasks/npc-ai.md#ai-01-recover-the-caisystem-instance-and-unblock-native-ai-construction) |
| AI-02 | Decide and build the managed AI provider (TODO(managed-ai)) | [npc-ai](docs/tasks/npc-ai.md#ai-02-decide-and-build-the-managed-ai-provider-todomanaged-ai) |
| AI-03 | Implement the ~25 unimplemented goal-pipe operations | [npc-ai](docs/tasks/npc-ai.md#ai-03-implement-the-25-unimplemented-goal-pipe-operations) |
| AI-04 | Add the ScriptBindUnit NPC Lua API | [npc-ai](docs/tasks/npc-ai.md#ai-04-add-the-scriptbindunit-npc-lua-api) |
| AI-05 | NPC locomotion, anchor/leash return, path-following and path-finder | [npc-ai](docs/tasks/npc-ai.md#ai-05-npc-locomotion-anchorleash-return-path-following-and-path-finder) |
| AI-06 | Provide a concrete live IZoneNpcWorld / actor runtime for the scene | [npc-ai](docs/tasks/npc-ai.md#ai-06-provide-a-concrete-live-izonenpcworld--actor-runtime-for-the-scene) |
| AI-07 | NPC skill selection and combat loop | [npc-ai](docs/tasks/npc-ai.md#ai-07-npc-skill-selection-and-combat-loop) |
| AI-08 | Aggro gaps: acquisition, linked/shared aggro, sleep, cancel timers, retarget | [npc-ai](docs/tasks/npc-ai.md#ai-08-aggro-gaps-acquisition-linkedshared-aggro-sleep-cancel-timers-retarget) |
| LUA-03 | Implement the remaining ~156 `AI.*` functions (prioritised) | [lua-api](docs/tasks/lua-api.md#lua-03-implement-the-remaining-156-ai-functions-prioritised) |
| LUA-05 | Complete CAIHandler: character scripts, aiconfig, OnLoadAI/OnSaveAI, StartupBehaviour | [lua-api](docs/tasks/lua-api.md#lua-05-complete-caihandler-character-scripts-aiconfig-onloadaionsaveai-startupbehaviour) |
| LUA-06 | Provide the entity-script host (entity tables and lifecycle callbacks) | [lua-api](docs/tasks/lua-api.md#lua-06-provide-the-entity-script-host-entity-tables-and-lifecycle-callbacks) |
| LUA-07 | Implement remaining `Script.*` functions (timers, reload variants, dumps) | [lua-api](docs/tasks/lua-api.md#lua-07-implement-remaining-script-functions-timers-reload-variants-dumps) |
| LUA-10 | Command-set / quest `RunCommandSet` path | [lua-api](docs/tasks/lua-api.md#lua-10-command-set--quest-runcommandset-path) |
| CMB-02 | Add WZ message codecs and opcodes for the skill/damage/knockback/cooldown/plot packets | [combat](docs/tasks/combat.md#cmb-02-add-wz-message-codecs-and-opcodes-for-the-skilldamageknockbackcooldownplot-packets) |
| CMB-03 | Add outbound ZW opcodes/messages the zone originates for combat | [combat](docs/tasks/combat.md#cmb-03-add-outbound-zw-opcodesmessages-the-zone-originates-for-combat) |
| CMB-04 | Load full SkillDesc content (the `skills` table and companions) | [combat](docs/tasks/combat.md#cmb-04-load-full-skilldesc-content-the-skills-table-and-companions) |
| CMB-05 | Implement the zone skill-state mirror (StartSkill/FireSkill/EndSkill/StopSkill/StopCasting) | [combat](docs/tasks/combat.md#cmb-05-implement-the-zone-skill-state-mirror-startskillfireskillendskillstopskillstopcasting) |
| CMB-06 | Wire WZUnitDamaged/WZUnitHealed into NPC aggro and AI notification | [combat](docs/tasks/combat.md#cmb-06-wire-wzunitdamagedwzunithealed-into-npc-aggro-and-ai-notification) |
| CMB-07 | Implement ZoneBuffMan create/destroy side effects on the unit | [combat](docs/tasks/combat.md#cmb-07-implement-zonebuffman-createdestroy-side-effects-on-the-unit) |
| CMB-08 | Load the full BuffDesc (buffs table and companion tables) | [combat](docs/tasks/combat.md#cmb-08-load-the-full-buffdesc-buffs-table-and-companion-tables) |
| CMB-09 | Implement unit crowd-control/status state consumers (stun, sleep, root, silence, walk-only, cannot-jump, blank-minded, glide) | [combat](docs/tasks/combat.md#cmb-09-implement-unit-crowd-controlstatus-state-consumers-stun-sleep-root-silence-walk-only-cannot-jump-blank-minded-glide) |
| CMB-10 | Mirror the per-unit skill cooldown manager | [combat](docs/tasks/combat.md#cmb-10-mirror-the-per-unit-skill-cooldown-manager) |
| CMB-11 | Implement the NPC AI skill pipeline (pick, can-use, use, stop) | [combat](docs/tasks/combat.md#cmb-11-implement-the-npc-ai-skill-pipeline-pick-can-use-use-stop) |
| CMB-12 | Implement the Lua ScriptBindUnit combat API used by AI scripts | [combat](docs/tasks/combat.md#cmb-12-implement-the-lua-scriptbindunit-combat-api-used-by-ai-scripts) |
| CMB-13 | Skill target/range/obstacle validation helpers | [combat](docs/tasks/combat.md#cmb-13-skill-targetrangeobstacle-validation-helpers) |
| CMB-14 | Complete death/resurrection side effects on the unit | [combat](docs/tasks/combat.md#cmb-14-complete-deathresurrection-side-effects-on-the-unit) |
| CMB-15 | Implement skill controllers (dash/leap/rope/impulse/floating/move/rotate/anchor/crawl/wandering/flowgraph) | [combat](docs/tasks/combat.md#cmb-15-implement-skill-controllers-dashleapropeimpulsefloatingmoverotateanchorcrawlwanderingflowgraph) |
| CMB-16 | Handle knockback/impulse/blink/invisible packets | [combat](docs/tasks/combat.md#cmb-16-handle-knockbackimpulseblinkinvisible-packets) |
| DB-02 | Introduce a unified DDCMS loader orchestrator mirroring sub_39BCA9A0 | [content-db](docs/tasks/content-db.md#db-02-introduce-a-unified-ddcms-loader-orchestrator-mirroring-sub_39bca9a0) |
| DB-03 | Complete skills/buffs/effects data model (full skills+buffs columns, effects dispatch, skill_effects, buff_triggers, tick effects, 58 *_effects payload tables) | [content-db](docs/tasks/content-db.md#db-03-complete-skillsbuffseffects-data-model-full-skillsbuffs-columns-effects-dispatch-skill_effects-buff_triggers-tick-effects-58-_effects-payload-tables) |
| DB-05 | Finish the NPC data set (interactions, initial buffs, nicknames, mount skills, doodad bindings, strafe params, grade configs, spawn-effect links, merchants) | [content-db](docs/tasks/content-db.md#db-05-finish-the-npc-data-set-interactions-initial-buffs-nicknames-mount-skills-doodad-bindings-strafe-params-grade-configs-spawn-effect-links-merchants) |
| DB-06 | Add the doodad data model (doodad_almighties full columns, groups, funcs, phase funcs, 135 doodad_func_* tables, modifiers, bundles, place skins, gimmicks) | [content-db](docs/tasks/content-db.md#db-06-add-the-doodad-data-model-doodad_almighties-full-columns-groups-funcs-phase-funcs-135-doodad_func_-tables-modifiers-bundles-place-skins-gimmicks) |
| DB-07 | Items, loot, crafting and economy tables beyond the stat subset | [content-db](docs/tasks/content-db.md#db-07-items-loot-crafting-and-economy-tables-beyond-the-stat-subset) |
| DB-08 | World/zone metadata completion (world_groups, world_divisions, zone climates, map data, zone_group tables, resurrection times) | [content-db](docs/tasks/content-db.md#db-08-worldzone-metadata-completion-world_groups-world_divisions-zone-climates-map-data-zone_group-tables-resurrection-times) |
| DB-09 | Load ServerAreaManager XMLs (quest_area, district) and return_point.g | [content-db](docs/tasks/content-db.md#db-09-load-serverareamanager-xmls-quest_area-district-and-return_pointg) |
| DB-10 | Derive world file paths from zone id instead of CLI arguments | [content-db](docs/tasks/content-db.md#db-10-derive-world-file-paths-from-zone-id-instead-of-cli-arguments) |
| WS-01 | Implement ZWEnterArea/ZWLeaveArea and ServerAreaManager (quest areas) | [world-state](docs/tasks/world-state.md#ws-01-implement-zwenterareazwleavearea-and-serverareamanager-quest-areas) |
| WS-02 | Handle the remaining WZ world-state packets instead of counting them unimplemented | [world-state](docs/tasks/world-state.md#ws-02-handle-the-remaining-wz-world-state-packets-instead-of-counting-them-unimplemented) |
| WS-04 | Emit the missing ZW world-state frames (ReportZoneStatus, Heartbeat loop, BackpackDropped, RemoveHouse) | [world-state](docs/tasks/world-state.md#ws-04-emit-the-missing-zw-world-state-frames-reportzonestatus-heartbeat-loop-backpackdropped-removehouse) |
| WS-06 | Wire world-state consumers into gameplay (conflict state to PvP rules, siege and relation to combat, schedule to spawners) | [world-state](docs/tasks/world-state.md#ws-06-wire-world-state-consumers-into-gameplay-conflict-state-to-pvp-rules-siege-and-relation-to-combat-schedule-to-spawners) |
| OPS-03 | Graceful shutdown, signal, and crash handling for the host | [ops](docs/tasks/ops.md#ops-03-graceful-shutdown-signal-and-crash-handling-for-the-host) |
| OPS-04 | Add a ServerCVar configuration layer with the sim-affecting native CVars | [ops](docs/tasks/ops.md#ops-04-add-a-servercvar-configuration-layer-with-the-sim-affecting-native-cvars) |
| OPS-06 | Implement the four unsupported zone-side GM selectors (49, 142, 143, 144) without crashing the session — **Partial** | [ops](docs/tasks/ops.md#ops-06-implement-the-four-unsupported-zone-side-gm-selectors-49-142-143-144-without-crashing-the-session--partial-no-selector-throws-native-aritykind-checks-ported-49142143144-effects-log-by-name-pending-skill-npc-search-reply-npc-mode-and-npc-teleport-subsystems) |
| OPS-10 | Remove the hardcoded Lua library path and external-environment assumptions | [ops](docs/tasks/ops.md#ops-10-remove-the-hardcoded-lua-library-path-and-external-environment-assumptions) |
| OPS-12 | CI pipeline (build + selected tests + warnings gate) | [ops](docs/tasks/ops.md#ops-12-ci-pipeline-build--selected-tests--warnings-gate) |
| OPS-14 | Close test gaps for the highest-risk subsystems | [ops](docs/tasks/ops.md#ops-14-close-test-gaps-for-the-highest-risk-subsystems) |
| RT-02 | Implement ServerAreaManager (area enter/leave) and EnvironmentManager | [rtti-inventory](docs/tasks/rtti-inventory.md#rt-02-implement-serverareamanager-area-enterleave-and-environmentmanager) |
| RT-03 | Port the skill-controller family and ZoneBuffMan | [rtti-inventory](docs/tasks/rtti-inventory.md#rt-03-port-the-skill-controller-family-and-zonebuffman) |
| RT-04 | Implement doodad runtime (Doodad, phase funcs, climb doodads) | [rtti-inventory](docs/tasks/rtti-inventory.md#rt-04-implement-doodad-runtime-doodad-phase-funcs-climb-doodads) |
| RT-09 | Add missing content readers for zone-relevant DescStorage types | [rtti-inventory](docs/tasks/rtti-inventory.md#rt-09-add-missing-content-readers-for-zone-relevant-descstorage-types) |
| RT-11 | Hook raycast/collision services to World packets | [rtti-inventory](docs/tasks/rtti-inventory.md#rt-11-hook-raycastcollision-services-to-world-packets) |
| RT-14 | Team/party/raid accessors for zone-side aggro sharing and friendly checks | [rtti-inventory](docs/tasks/rtti-inventory.md#rt-14-teampartyraid-accessors-for-zone-side-aggro-sharing-and-friendly-checks) |

### P2 - parity and later

| ID | Task | Area |
| --- | --- | --- |
| WC-13 | Tolerate interleaved WZ frames during startup | [world-contract](docs/tasks/world-contract.md#wc-13-tolerate-interleaved-wz-frames-during-startup) |
| WC-14 | Send the ZW heartbeat | [world-contract](docs/tasks/world-contract.md#wc-14-send-the-zw-heartbeat) |
| WC-15 | Point the replacement at the same game data as `ZoneGameDataRoot` | [world-contract](docs/tasks/world-contract.md#wc-15-point-the-replacement-at-the-same-game-data-as-zonegamedataroot) |
| WC-16 | Define shutdown and orphan behaviour | [world-contract](docs/tasks/world-contract.md#wc-16-define-shutdown-and-orphan-behaviour) |
| WZ-09 | Gimmick packets | [wz-inbound](docs/tasks/wz-inbound.md#wz-09-gimmick-packets) |
| WZ-10 | Siege/conflict extras and TowerDef | [wz-inbound](docs/tasks/wz-inbound.md#wz-10-siegeconflict-extras-and-towerdef) |
| WZ-12 | Complete SetFaction/body-fidelity checks of already-dispatched handlers | [wz-inbound](docs/tasks/wz-inbound.md#wz-12-complete-setfactionbody-fidelity-checks-of-already-dispatched-handlers) |
| WZ-13 | Add a generated WZ conformance table test | [wz-inbound](docs/tasks/wz-inbound.md#wz-13-add-a-generated-wz-conformance-table-test) |
| ZW-09 | Add gimmick ZW packets (RequestSpawnStaticGimmick, Movement, JointBroken, ResetJoints, RemoveParts, Collided) | [zw-outbound](docs/tasks/zw-outbound.md#zw-09-add-gimmick-zw-packets-requestspawnstaticgimmick-movement-jointbroken-resetjoints-removeparts-collided) |
| ZW-10 | Add anti-cheat and misc report packets (ReportMoveHack, ReportDoodadSurfaceHack, DoodadCollideUnit, BackpackDropped, RemoveHouse, TowerDefReportPlayability, DumpNpc, AiAggro, AiDebug, Sandbox*) | [zw-outbound](docs/tasks/zw-outbound.md#zw-10-add-anti-cheat-and-misc-report-packets-reportmovehack-reportdoodadsurfacehack-doodadcollideunit-backpackdropped-removehouse-towerdefreportplayability-dumpnpc-aiaggro-aidebug-sandbox) |
| ZW-11 | Reconcile ZwOpcodes.cs with the full native list and add a coverage test | [zw-outbound](docs/tasks/zw-outbound.md#zw-11-reconcile-zwopcodescs-with-the-full-native-list-and-add-a-coverage-test) |
| ZW-12 | Record that CT*/TC*/StreamClient are out of scope for the zone | [zw-outbound](docs/tasks/zw-outbound.md#zw-12-record-that-cttcstreamclient-are-out-of-scope-for-the-zone) |
| SCN-11 | Port X2::FlowGraphSkillController / FlowGraphWrapper (graph-driven skill controllers) | [scene](docs/tasks/scene.md#scn-11-port-x2flowgraphskillcontroller--flowgraphwrapper-graph-driven-skill-controllers) |
| SCN-12 | Instance zones (indun) and per-world layout variants | [scene](docs/tasks/scene.md#scn-12-instance-zones-indun-and-per-world-layout-variants) |
| SCN-13 | Multi-zone per process, zone reload and seamless streaming decision | [scene](docs/tasks/scene.md#scn-13-multi-zone-per-process-zone-reload-and-seamless-streaming-decision) |
| SCN-15 | Prefab object loading used by level entities | [scene](docs/tasks/scene.md#scn-15-prefab-object-loading-used-by-level-entities) |
| MOV-11 | Glider, ladder/climb, rope, and mount/passenger movement states | [movement](docs/tasks/movement.md#mov-11-glider-ladderclimb-rope-and-mountpassenger-movement-states) |
| MOV-12 | Ship and vehicle movement controllers (server simulation) | [movement](docs/tasks/movement.md#mov-12-ship-and-vehicle-movement-controllers-server-simulation) |
| MOV-16 | Finish the legacy navigation runtime (volume/flight/road/surface readers, quadtree build, A* search) | [movement](docs/tasks/movement.md#mov-16-finish-the-legacy-navigation-runtime-volumeflightroadsurface-readers-quadtree-build-a-search) |
| MOV-17 | Nav-modifier prefab objects and dynamic regions from XML entities | [movement](docs/tasks/movement.md#mov-17-nav-modifier-prefab-objects-and-dynamic-regions-from-xml-entities) |
| MOV-19 | Sight / visibility ownership decision (interest management) | [movement](docs/tasks/movement.md#mov-19-sight--visibility-ownership-decision-interest-management) |
| OBJ-05 | Load doodad function content and zone doodad effects | [world-objects](docs/tasks/world-objects.md#obj-05-load-doodad-function-content-and-zone-doodad-effects) |
| OBJ-07 | Housing state and build lifecycle | [world-objects](docs/tasks/world-objects.md#obj-07-housing-state-and-build-lifecycle) |
| OBJ-08 | Slave lifecycle (spawn, master change, escape) | [world-objects](docs/tasks/world-objects.md#obj-08-slave-lifecycle-spawn-master-change-escape) |
| OBJ-09 | Ship/vehicle simulation and control | [world-objects](docs/tasks/world-objects.md#obj-09-shipvehicle-simulation-and-control) |
| OBJ-10 | Mounts, transfers, shipyards, portals | [world-objects](docs/tasks/world-objects.md#obj-10-mounts-transfers-shipyards-portals) |
| AI-09 | Spawner leftovers: persistent/instance-saved spawners, population cap, TowerDef | [npc-ai](docs/tasks/npc-ai.md#ai-09-spawner-leftovers-persistentinstance-saved-spawners-population-cap-towerdef) |
| AI-10 | Track patterns, strafe params, roaming areas and ai_commands | [npc-ai](docs/tasks/npc-ai.md#ai-10-track-patterns-strafe-params-roaming-areas-and-ai_commands) |
| AI-11 | Client-driven NPC control and npc_ai_client_params | [npc-ai](docs/tasks/npc-ai.md#ai-11-client-driven-npc-control-and-npc_ai_client_params) |
| AI-12 | AI debug/diagnostics parity | [npc-ai](docs/tasks/npc-ai.md#ai-12-ai-debugdiagnostics-parity) |
| LUA-08 | Implement server-meaningful `System.*` and `Physics.*` | [lua-api](docs/tasks/lua-api.md#lua-08-implement-server-meaningful-system-and-physics) |
| LUA-09 | Provide `Particle.*` and `Sound.*` stub tables | [lua-api](docs/tasks/lua-api.md#lua-09-provide-particle-and-sound-stub-tables) |
| CMB-17 | Combat state lifecycle (engage timeout, ZWClearCombat, force-attack, duel) | [combat](docs/tasks/combat.md#cmb-17-combat-state-lifecycle-engage-timeout-zwclearcombat-force-attack-duel) |
| CMB-18 | Implement ZonePlotMan (plot events mirror) | [combat](docs/tasks/combat.md#cmb-18-implement-zoneplotman-plot-events-mirror) |
| CMB-19 | Fall damage and collision notifications to World | [combat](docs/tasks/combat.md#cmb-19-fall-damage-and-collision-notifications-to-world) |
| CMB-20 | Stealth/detection and invisibility semantics | [combat](docs/tasks/combat.md#cmb-20-stealthdetection-and-invisibility-semantics) |
| CMB-21 | Quest-driven and special combat packets | [combat](docs/tasks/combat.md#cmb-21-quest-driven-and-special-combat-packets) |
| CMB-22 | Initial/system buffs applied by the zone (npc/slave initial buffs, safe-area, zone buffs) | [combat](docs/tasks/combat.md#cmb-22-initialsystem-buffs-applied-by-the-zone-npcslave-initial-buffs-safe-area-zone-buffs) |
| DB-04 | Add plots (skill plot scripting) tables | [content-db](docs/tasks/content-db.md#db-04-add-plots-skill-plot-scripting-tables) |
| DB-11 | Slaves, vehicles, transfers and butlers data | [content-db](docs/tasks/content-db.md#db-11-slaves-vehicles-transfers-and-butlers-data) |
| DB-12 | ServerGimmick / ZdModelPrefab / ZdModelVegetation resources and Prefabs/*.xml | [content-db](docs/tasks/content-db.md#db-12-servergimmick--zdmodelprefab--zdmodelvegetation-resources-and-prefabsxml) |
| DB-13 | Quest tables and quest-related zone hooks | [content-db](docs/tasks/content-db.md#db-13-quest-tables-and-quest-related-zone-hooks) |
| DB-14 | Housing, instance/event/siege and game-rule tables | [content-db](docs/tasks/content-db.md#db-14-housing-instanceeventsiege-and-game-rule-tables) |
| DB-15 | Table-schema drift guard (compare repo SELECT lists with native SQL strings) | [content-db](docs/tasks/content-db.md#db-15-table-schema-drift-guard-compare-repo-select-lists-with-native-sql-strings) |
| DB-16 | Decide handling of client-only tables loaded by the shared loader | [content-db](docs/tasks/content-db.md#db-16-decide-handling-of-client-only-tables-loaded-by-the-shared-loader) |
| WS-03 | Implement tower-defense WZ/ZW handling | [world-state](docs/tasks/world-state.md#ws-03-implement-tower-defense-wzzw-handling) |
| WS-05 | Implement housing state (WZHouseState, HouseBuildProgress/Done, ZWRemoveHouse, housing-area test) | [world-state](docs/tasks/world-state.md#ws-05-implement-housing-state-wzhousestate-housebuildprogressdone-zwremovehouse-housing-area-test) |
| WS-07 | Confirm and (if native) implement loot, achievement and appellation runtime ownership | [world-state](docs/tasks/world-state.md#ws-07-confirm-and-if-native-implement-loot-achievement-and-appellation-runtime-ownership) |
| WS-08 | Document and verify absent zone-side recovery / transfer / weather behavior | [world-state](docs/tasks/world-state.md#ws-08-document-and-verify-absent-zone-side-recovery--transfer--weather-behavior) |
| OPS-05 | Implement movement verification / hack checker (anti-cheat) or explicitly record it as out of scope | [ops](docs/tasks/ops.md#ops-05-implement-movement-verification--hack-checker-anti-cheat-or-explicitly-record-it-as-out-of-scope) |
| OPS-07 | Provide a minimal server console (stdin) with the native command set | [ops](docs/tasks/ops.md#ops-07-provide-a-minimal-server-console-stdin-with-the-native-command-set) |
| OPS-08 | Structured log levels, channels, and rotation | [ops](docs/tasks/ops.md#ops-08-structured-log-levels-channels-and-rotation) |
| OPS-09 | Frame-time and tick metrics | [ops](docs/tasks/ops.md#ops-09-frame-time-and-tick-metrics) |
| OPS-11 | Add `instance_id` / `instance_index` / external IP to the join and config | [ops](docs/tasks/ops.md#ops-11-add-instance_id--instance_index--external-ip-to-the-join-and-config) |
| OPS-13 | Warnings and analyzers policy | [ops](docs/tasks/ops.md#ops-13-warnings-and-analyzers-policy) |
| OPS-15 | Operations documentation | [ops](docs/tasks/ops.md#ops-15-operations-documentation) |
| RT-05 | Implement gimmick (breakable/jointed physics prop) subsystem | [rtti-inventory](docs/tasks/rtti-inventory.md#rt-05-implement-gimmick-breakablejointed-physics-prop-subsystem) |
| RT-06 | Implement housing runtime (Housing, HousingManager) | [rtti-inventory](docs/tasks/rtti-inventory.md#rt-06-implement-housing-runtime-housing-housingmanager) |
| RT-07 | Implement ship/vehicle/slave simulation controllers | [rtti-inventory](docs/tasks/rtti-inventory.md#rt-07-implement-shipvehicleslave-simulation-controllers) |
| RT-08 | Implement MoveHackChecker and mole-miner/trader reports | [rtti-inventory](docs/tasks/rtti-inventory.md#rt-08-implement-movehackchecker-and-mole-minertrader-reports) |
| RT-10 | Implement instance/indun, battlefield and tower-defense zone logic | [rtti-inventory](docs/tasks/rtti-inventory.md#rt-10-implement-instanceindun-battlefield-and-tower-defense-zone-logic) |
| RT-12 | Determine need for TgwWorker / StreamClient / AuthClient / CryptoSystem | [rtti-inventory](docs/tasks/rtti-inventory.md#rt-12-determine-need-for-tgwworker--streamclient--authclient--cryptosystem) |
| RT-13 | Ranking snapshots and ReloadDesc handling | [rtti-inventory](docs/tasks/rtti-inventory.md#rt-13-ranking-snapshots-and-reloaddesc-handling) |
| RT-15 | Butler and Mate handling on the zone | [rtti-inventory](docs/tasks/rtti-inventory.md#rt-15-butler-and-mate-handling-on-the-zone) |
