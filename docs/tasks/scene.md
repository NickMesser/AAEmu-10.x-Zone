<!-- Audit of scene: native x2game-dev_dedicate.dll (ArcheAge 10.0.2.13 r575) vs this repo at 76e439f. Paths rewritten to the src/ layout; line numbers refer to 76e439f. -->

## Scene / game framework startup / level load / entity classes / FlowGraph / world (prefix SCN)

**Coverage summary.** The repo does not load `x2game-dev_dedicate.dll`; it loads the engine DLLs (`cryaction_dedicate`, `crysystem`, `cry3dengine_dedicate`, `cryentitysystem_dedicate`, ...) and re-implements the *game layer* (CGame / IGame / X2::GameServer) in managed code (`ZoneNativeEngine.RejectNativeGameModules`, `ZoneNativeEngine.cs:408-418`). Coverage is therefore "what the engine needs from the game layer" plus a managed replacement of the server managers. Verified present: composed CSystem + GameFramework + PostInit; all 20 X2 FlowNode factories (native has exactly 13 `CG2AutoRegFlowNode[Singleton]<T>` RTTI classes expanding to 20 names = 12 singleton + 8 per-creation, matching the repo; the 21st string `X2:SkillController` is NOT a node factory but a GameObject-extension id used by `X2::FlowGraphSkillController`); World layout resolution; six-cell terrain/objects incl. the XML-entity pass and the deferred blocking pass; native level-completion order of `39332730` (static gimmicks -> time of day -> areas -> spawners+postload -> `ZWZoneLoaded` -> persistent spawners -> bounds) in `ZoneSceneCoordinator.CompleteLoadingAsync`. Biggest gaps: IGame publishes 4 of 152 slots (rest `FailFast`), the whole `InitGameFactory` (player/actor/Tornado/Rain/WindArea/ScriptControlledPhysics class registration) is absent, `ServerAreaManager::LoadAreas`, static-gimmick/doodad/ZdModel world objects, server-type-dependent join identity, and instance-zone / multi-zone handling. NOTE: `AAEmu.Zone/TODO.md` is partly stale versus code: the deferred blocking pass now has an owner (`ZoneCellDeferredBrushResources`, `ZoneSceneCellObjectLoader.cs:297-352`, `ZoneNativeStaticObjectServices.ReadDeferredGateContext :294`), the XML-entity pass is wired per cell (`ZoneSceneCellObjectLoader.cs:238-250`), and `Program.cs (pre-restructure)` already has `--host`. Engine-DLL addresses (30xxxxxx/36xxxxxx) cannot be checked in the x2game export; they are cited from repo comments only.

### Coverage table

| Native item | Native evidence | Repo status |
|---|---|---|
| X2Startup::InitImpl: cmdline, engine load, "Loading Game Data..." (DDCMS compact.sqlite3 load), "Initializing Game System..." (CreateGame+Init), "Final Initializing..." | `39A687A0` (strings 0x3A0F2E68/2DB0/2D98), `39A17A70` (`CreateGame` -> vtbl[0]), `39A37BF0` | Replaced: managed `Program.cs (pre-restructure)` + `ZoneContentDatabase` (SQLite) + `ZoneNativeEngine` startup; no DDCMS desc package (`39BCE530`) |
| CGame::Init (IGame slot 0x0) | `392AC8F0` (2104 B): creates a1[10], a1[12], a1[13], `GameBreakableEventDispatcher`; client-only managers skipped when `gEnv+376==2`; `390B1210` formula verify; `InitGameFactory` call | absent as a unit (formula semantics exist in `Content`; no manager graph) |
| InitGameFactory: GameObject ext "Player"; actor creators per actor-model row (Quadruped/Monster/CPlayer); entity classes Tornado, Rain, WindArea, ScriptControlledPhysics | `392B9010` (called at `392AC8F0`); `Scripts/Entities/Environment/Tornado.lua`, `.../Rain.lua`, `.../Physics/WindArea.lua` (strings 0x39FCD618/48/70); RTTI `CActorCreator<CPlayer\|Monster\|Quadruped>`, `CGameObjectExtensionCreator<CPlayer>`, `CTornadoCreator`, `CRainCreator`, `CWindAreaCreator`, `CScriptControlledPhysicsCreator` | absent (only identity facts about CPlayer in `ZonePlayerActorFactory.cs:57-68`, `ZoneNativeEngine.FlowActors.cs:188`) |
| IGame vtable, 152 slots (CGame primary vtable 0x39FC6E48) | slot 0x10 `392AA330` = PostInit / FlowNode registration; 0x138 `392A9030` = model lookup; 0x410 `39DB6BF0` = GetFramework; 0xE8 = spawn query; ~30 DDCMS desc-by-id thunks (0x128-0x248, 0x2a0-0x2b8); 98 slots >16 B | `ZoneNativeEngine.GameServices.cs:303-335`: 4 bound (0x10, 0x138, 0x410, 0xE8 -> null), 148 `Environment.FailFast` |
| 20 FlowNode factories (order) | strings 0x39FB8750..0x3A0BBA90; 13 `CG2AutoRegFlowNode*` RTTI | `ZoneNativeFlowNodeRegistration.cs` (RequiredNames, 20, identical order); `ZoneNativeFlowNodes.*.cs` |
| Level start server event (12/1) -> server game construction | `392B02F0` event dispatch -> `39333490`: `3935EDA0` (max_unit_for_test / max_unit_in_world CVars), `39331EF0` (29 console commands), ~14 manager singletons qword_3A607A40..A98 (NpcManager 0x120, `ServerAIUnitSystem`, `SandboxOnlineNet`, 0x4328-byte manager, ...) | partial managed equivalents (`ZoneSceneProviders.cs`, `ZoneNpcManager`); no max-unit CVars, no GM command table, no SandboxOnline |
| Level load end (13/1) -> ZoneLoaded sequence | `392B02F0` -> `39332730`: `393365A0`, `39332130`, `3932B730`, `3934B180` (`3934B0E0` LoadSpawners + sort `399CA750` + `39343660` postload), `ZWZoneLoaded` (`X2::ZWZoneLoadedPacket`), `39349DC0`, bounds `39D2D990` / gEnv+128 vtbl+680 | `ZoneSceneCoordinator.cs:152-196` matches order; leaf providers partial (see tasks) |
| Level unload (14/1) | `39333730` (teardown of all server managers) | managed ordered retirement (`ZoneSceneCoordinator.Cleanup`, `ZoneSceneActivation.Dispose`) |
| ServerAreaManager::LoadAreas ("quest_area", "district") | `3932B530` (path `%s/level_design/zone/%d/world_server/%s.xml`, string 0x39FDBED0; log "Server Area Manager : total (%d) areas"), wrapper `3932B730` | absent - `ZoneSceneLoadingServices.InitializeAreas` only reads the zones-table row (`:210-222`) |
| NpcManager::LoadSpawners | `3934AD80` (`%s/level_design/zone/%d/zone_server/npc_spawners.g`, 0x39FE2620) | present, but path supplied via CLI `--spawner-file` (`Program.cs (pre-restructure):61,118`) |
| Static gimmick request | `393365A0` (entity iterator, flag `0x100000`, zone-owner check, `ZWRequestSpawnStaticGimmickPacket`) | stub logs 0 rows (`ZoneSceneLoadingServices.cs:166-176`, TODO(static-gimmicks)) |
| ServerGimmick / ServerGimmickManager, ZdModel{Brush,Vegetation,Prefab,Crater,Dummy}, X2::CreateGimmickEntity | strings 0x39FDE770 `ServerGimmick::Init`, 0x39F8C890 `X2::CreateGimmickEntity`, 0x3A00B138/90/E8 "zone doodad load ... fail path", RTTI `ZdModel*@GameServer@X2` | absent (no WZ doodad/gimmick handlers; `WzOpcodes.cs:43-49,106-108` defined only) |
| Zone join identity by server type | `39366760` (ZWJoin builder): switch on ZoneClient+0x8C: 1 -> `cl_account_id`; 2 -> `instance_id` CVar; 3 -> none; also `sv_port`, `vpn_external_ip` | `Program.cs (pre-restructure):159` sends zeros; `ServerType` hard-coded 2 (`ZoneSceneLoadingServices.cs:36`) |
| World ZoneList registry (zone id/origin/name) | `397BDA60` (`Game/worlds/%s/world.xml`) | `ZoneWorldMapContent` (equivalent, engine-validated in `ZoneNativeEngine.WorldLayout.cs`) |

### Tasks

### SCN-01: Bind or truthfully stub the remaining 148 IGame slots
- Priority: P0 (blocks first-entry milestone: any engine call into an unbound slot kills the process, as +0xE8 already did)
- Native evidence: CGame primary vtable at 0x39FC6E48, 152 slots (found by walking the RTTI complete-object-locator of `.?AVCGame@@`, TypeDescriptor 0x3A514888); slot targets `392AC8F0` (Init), `392AC590` (0x8, "X2 Font"/"DDCMS"), `392AA3B0` (0x18), `392AA510` (0x28 "no client player"), `392AA7D0` (0x68: sets `e_water_ocean_simulate_on_zone` / `e_phys_ocean_cell`), `392AD130` (0x300 CustomizeEntitySameWithUnit), `392ADC00` (0x370, 7256 B: AI "Properties"/"PropertiesInstance"/"AIMovementAbility"/"bSpeciesHostility"/"bAffectSOM" import; strings 0x39F8B468-0x39F8B750), `392AD360` (0x3E8 CGame::Shutdown), `392AF860` (0x3F0), `392B8A70`/`392B8D70` (0x4B0/0x4B8 console cmds "quit","goto","gotoe"). 54 slots are <=16 B constant/forward thunks; ~30 are DDCMS desc-by-id lookups (`392A8FD0`..`392A9760`, e.g. 0x138 -> `39ABF360`).
- Repo state: `src/AAEmu.Zone/Native/Engine/ZoneNativeEngine.GameServices.cs:303-335` - SlotCount=152; only 0x10, 0x138, 0x410, 0xE8 (null) bound; rest `UnsupportedGameCall` -> `Environment.FailFast`.
- Work:
  - Static-scan `cryaction_dedicate`, `cryentitysystem_dedicate`, `cryaisystem_dedicate`, `cry3dengine_dedicate`, `crynetwork_dedicate` for indirect calls through the IGame pointer to build the set of slots reachable in dedicated mode.
  - Bind the constant thunks with the exact constant they return (decode the bytes of the 54 short slots) and the DDCMS desc-lookup slots against `ZoneModelContent`/content readers (return null with a named log where the table is absent, not FailFast).
  - Implement 0x68 (ocean CVars per zone), 0x370 (AI Properties import for entity scripts) and 0x3E8 (shutdown ordering) as real providers; keep FailFast only for slots whose native body says "This method is for only client".
  - Add a unit test that every slot is either bound or listed in an explicit client-only table.
- Depends on: none
- Confidence: high on slot count/targets; medium on which slots are reachable (needs engine-DLL xref scan)

### SCN-02: Port InitGameFactory (Player GOE + actor classes + 4 entity classes)
- Priority: P0 (player entry needs the "Player" extension and CPlayer actor class published; TODO.md P1 "class publication")
- Native evidence: `392B9010` (called from `CGame::Init 392AC8F0`): registers GameObject extension "Player" (`CGameObjectExtensionCreator<CPlayer>`), iterates actor-model rows (`39ABE310` vector, stride 152, row+60==1) and registers each by name with a `CActorCreator<>`: model kind ((row+64)->+196)-1 == 0 or 2 -> `Quadruped`, == 1 -> `Monster`, else `CPlayer`; then entity classes `Tornado` (`Scripts/Entities/Environment/Tornado.lua`), `Rain` (`.../Rain.lua`), `WindArea` (`Scripts/Entities/Physics/WindArea.lua`), `ScriptControlledPhysics` (no script). Error path "Actor Model(%s) is Empty" (0x39FCD6D8).
- Repo state: absent. `ZonePlayerActorFactory.cs:57-79` documents CPlayer vtable 0x39FD4E58 and its `TODO(player-lifecycle)`; `ZoneSceneProviders.cs:736-745` TODO(actor-publication); `src/AAEmu.Zone/Content/ZoneActorModelContent.cs` already reads actor-model rows.
- Work:
  - Resolve the engine IGameFramework registration entry points (framework vtbl +0 RegisterExtension, +8 actor class registration, +152 -> class registry vtbl +64) in `cryaction_dedicate` and call them from a managed `InitGameFactory` replacement inside IGame PostInit (slot 0x10), in native order relative to FlowNode registration.
  - Provide native-compatible creator objects (CPlayer / Monster / Quadruped need the real x2game class bodies - decide managed re-implementation vs. loading x2game, noting `RejectNativeGameModules`).
  - Register the 4 scripted entity classes with their script paths; verify the Lua scripts are loadable from the asset source.
  - Gate on actor-model rows exactly as native (skip rows with null model, log as native).
- Depends on: SCN-01
- Confidence: high on native behavior; low on feasibility of re-creating CPlayer without x2game code

### SCN-03: Confirm scene readiness and refresh TODO.md S1/S2
- Priority: P0
- Native evidence: deferred blocking pass `3017D480/3017CC30` (engine; cited in repo comments); native level-end (`39332730`) runs only after the engine finished the level.
- Repo state: `ZoneSceneCellObjectLoader.cs:297-352` + `ZoneCellDeferredBrushResources` (`:498`) + `ZoneNativeStaticObjectServices.ReadDeferredGateContext :294` bind the pass at fixed active position (0,0,0) / R^2=1e9; `ZoneSceneLoadingServices.LoadLevelAsync :100-137` throws if `report.Ready` is false. `TODO.md` S1 still says the pass refuses by name.
- Work:
  - Re-run `--check-scene` and confirm `ZoneSceneLoadReport.Ready == true` across six passes on six cells; update TODO.md S1/S2.
  - Replace the fixed (0,0,0) gate with the real dedicated activation position if the engine exposes it (currently an evidence-bound constant; native re-gates during streaming with budget 3.0, load-time only here).
  - Retire the "TEMPORARY retirement diagnostic" (`ZoneNativeStaticObjectServices.RetirementDiagnostic.cs`) once clean.
- Depends on: none
- Confidence: medium (code reading only, not run)

### SCN-04: Implement level-start server construction (event 12/1) and max-unit CVars
- Priority: P1
- Native evidence: `392B02F0` event dispatch (events 1, 12, 13, 14; sub-id 1 = server) -> `39333490`: `3935EDA0` reads CVars `max_unit_for_test`, `max_unit_in_world`; `39331EF0` registers 29 console commands from table 0x3A517C20; constructs `X2::GameServer::ServerAIUnitSystem`, `SandboxOnlineNet`, NpcManager (`39347390`, 0x120 B) and managers stored in qword_3A607A40..A98 (`39355610`, `39350920`, `39322800`, `39D2DB70`, `39338CA0`, `396F4830`, `397FFC10`, `39358D70` (0x4328 B), `39334EE0`, `39454A50`); then `393349D0`, `39332A70`. Unload counterpart `39333730`.
- Repo state: managers exist piecemeal (`ZoneSceneProviders.cs` NPC world, `ZoneNpcManager`, `ZoneGameScheduleController`); no `max_unit_in_world` cap (only the `ZoneMovementOutgoing.cs:25` ScMaxUnits note), no GM command table, no SandboxOnline; `housing-manager` / `plots` readiness notes.
- Work:
  - Identify the ~10 manager constructors (map to ZoneBuffMan, ZonePlotMan, HousingManager, CharacterManager, TowerDef, GameSchedule owners) and record which have managed equivalents.
  - Implement the max-unit limit semantics and expose via config.
  - Add an explicit "level start" phase to `ZoneSceneCoordinator` before `LoadLevelAsync`, matching native order.
  - Treat SandboxOnlineNet (`OnSandboxOnline*` handlers) as P2.
- Depends on: none
- Confidence: medium

### SCN-05: Implement ServerAreaManager::LoadAreas (quest_area.xml, district.xml)
- Priority: P1
- Native evidence: `3932B730` (creates `ServerAreaManager`, gated `gEnv+289!=0 && gEnv+288==0`, calls LoadAreas for "quest_area" then "district"); `3932B530` builds `%s/level_design/zone/%d/world_server/%s.xml` (0x39FDBED0), loads XML via gEnv vtbl+888, feeds the entity system (gEnv+136 vtbl+288 then +528), logs "Server Area Manager : total (%d) areas" (0x39FDBE50), error "failed to load quset area from (%s)".
- Repo state: absent; `ZoneSceneLoadingServices.InitializeAreas` (`:210-222`) only looks up a zones-table row and logs. No grep hits for quest_area / district.
- Work:
  - Reuse `ZoneNativeEntityServices.CreateCellEntityBatch` machinery (`ZoneNativeEntityServices.CellEntities.cs`) to load the two area XML files as entity batches at the `InitializeAreas` phase, using world/zone from `ZoneWorldMapLayout`.
  - Count created areas, log the native line, treat a missing XML as native does (returns false, scene continues).
  - Retire area entities with the scene.
- Depends on: SCN-03
- Confidence: high on native steps; medium on engine-side LoadEntities ABI

### SCN-06: Implement static-gimmick request pass from loaded entities
- Priority: P1
- Native evidence: `393365A0`: iterates the entity-system iterator (gEnv+136 vtbl+168), selects entities with `(entity vtbl+48 & 0x100000)`, checks zone ownership via the zone provider (gEnv+264 vtbl+176), fills `X2::ZWRequestSpawnStaticGimmickPacket` (zone key, name via entity vtbl+8, position via +312, rotation via +328, model name) and sends; plus `X2::GameServer::ServerGimmick::InitStaticEntity` (0x39FDE260) / `InitDynamicEntity` (0x39FDE718).
- Repo state: `ZoneSceneLoadingServices.RequestStaticGimmickSpawns` (`:166-176`) logs 0 rows, claiming the placement source is level entity data; leaf declared at `ZoneSceneCoordinator.cs:40`; `ZwOpcodes.RequestSpawnStaticGimmick = 0x0026` (`ZwOpcodes.cs:30`) defined but never built.
- Work:
  - Add entity enumeration over the engine entity system after the XmlEntities pass (flag 0x100000 test, owner check vs the layout's zone).
  - Encode and queue ZWRequestSpawnStaticGimmick on the registry notification queue so it flushes before `ZWZoneLoaded` (phase order already correct).
  - Handle WZGimmickCreated / Removed / ReloadStatics (0x5A / 0x5B / 0x59) per SCN-07.
- Depends on: SCN-03, SCN-07
- Confidence: medium

### SCN-07: Server gimmick/doodad world objects and ZdModel physics models
- Priority: P1 (doodad/gimmick ground and collisions, TODO(gimmick-doodad)); joint/grasp relay P2
- Native evidence: RTTI `X2::GameServer::ServerGimmick`, `ServerGimmickManager` (strings `CreateGimmick`, `HandleGimmickCreated/Grasped/Movement/Removed/SetMovement`, `CmdBreakJoint/ResetJoint`), `IZoneDoodadModel` with `ZdModelBrush/Vegetation/Prefab/Crater/Dummy` (`ZdModelPrefab::Load` 0x3A00B1C0, `ZdModelVegetation::Load` 0x3A00B168, fail-path logs at export lines 917382/917566/917663), `X2::CreateGimmickEntity` (0x39F8C890), packets `WZCreateDoodad/RemoveDoodad/DoodadChangePhase`, `WZGimmick*`, `ZWDoodadCollideUnit`, `ZWGimmick*`.
- Repo state: absent. `WzOpcodes.cs` defines `CreateDoodad 0x73`, `RemoveDoodad 0x74`, `DoodadChangePhase 0x75`, `GimmickCreated 0x5A`... but nothing in AAEmu.Zone consumes them. `ZoneActorMovementInputServices.cs:616-619` returns null for FindGimmick (TODO(gimmick-doodad)); readiness notes `doodad-unit-collisions`, `doodads`, `gimmicks`.
- Work:
  - Add message readers/handlers for the WZ doodad/gimmick packets in `ZoneSession`.
  - Create engine entities + physics for doodads by ZdModel kind (brush/vegetation/prefab/crater/dummy) through `ZoneNativeStaticObjectServices` / `ZoneNativeEntityServices`.
  - Provide the gimmick/doodad ground world to the movement input services.
  - Joint-break / grasp / movement relay (ServerGimmickManager) as P2.
- Depends on: SCN-03
- Confidence: medium

### SCN-08: Join identity per server type, server-type and environment slots
- Priority: P1
- Native evidence: `39366760` builds `X2::ZWJoinPacket`: reads CVars `sv_port` and `vpn_external_ip`; switch on ZoneClient+0x8C: case 1 -> `cl_account_id`; case 2 -> `instance_id`; case 3 -> neither; default `X2Error("zone: impossible server type")`; reply handler `393648D0` ("zone: world server refused joining(%s)" / "zone: successfully joined world server"). `39332130` / `39332730` read +0x8C (type 3 skips time of day) and gEnv+336 (0x150; nonzero skips bounds).
- Repo state: `Program.cs (pre-restructure):159` joins with Ip=0, Port=0, AccountId=0, InstanceId=0, Dev=false; `ZoneSceneLoadingServices.cs:36,92` ServerType hard-coded 2 (TODO(server-type-slot)); `EnvironmentFlag150 => false` (TODO(env-slot)); `ZoneSceneProviders.cs:1052` TODO(world-slot).
- Work:
  - Add runtime-config fields for server type, instance id, account id, external ip and sv_port; build the join accordingly and derive `ServerType` / `EnvironmentFlag150` from config rather than constants.
  - Record evidence for what each type implies (type 2 reads `instance_id`, so the repo's constant 2 paired with InstanceId=0 is suspect).
  - Remove the hard-coded dedicated constants once derived.
- Depends on: none
- Confidence: medium (meaning of types 1/2/3 inferred from the join builder only)

### SCN-09: Derive npc_spawners.g from world+zone and honor native LoadSpawners gates
- Priority: P1
- Native evidence: `3934AD80` (path `%s/level_design/zone/%d/zone_server/npc_spawners.g`, logs "loading npc spawners ...", "NpcSpawner ignored : id=%d, type=%u"), `3934B0E0` (skipped when `gEnv+336!=0` and provider vtbl+16==0; sort via `399CA750` unless `gEnv+290`; "[NPC Manager] total %ld npc spawners are loaded"), `39343660` (postload for spawner types 7607 / 12411 / 14915).
- Repo state: `Program.cs (pre-restructure):61-62,118` require `--spawner-file`; `ZoneNpcSpawnerPlacementContent.LoadFile`; `ZoneNpcDiagnostics.cs:30-32` carries the 7607/12411/14915 table (matches native); `ZoneSceneCoordinator.cs:166-170` calls Reload then InitializePostLoad.
- Work:
  - Resolve the file from `IZoneAssetSource` (`game/worlds/<world>/level_design/zone/<id>/zone_server/npc_spawners.g`); keep `--spawner-file` as override.
  - Reproduce the gEnv+290 "no sort" and gEnv+336 skip as explicit config.
  - Emit the native log lines for parity.
- Depends on: SCN-08
- Confidence: high

### SCN-10: Complete flow-node provider bindings (F1 table)
- Priority: P1 for Player nodes / ActorSensor / GameType; P2 for PostFX, ScreenFader, TornadoWander, Freeze, Difficulty, Camera FOV
- Native evidence: the 20 names in native list order (strings 0x39FB8750, 0x39FD7818, 0x39FD7830, 0x39FD7848, 0x39FD7D20, 0x39FD8680, 0x39FD8690, 0x39FD86A8, 0x39FD8BB0, 0x39FD8BC8, 0x39FD92E0..0x39FD9390, 0x39FD9958, 0x39FD9968, 0x3A0BBA90); RTTI `CG2AutoRegFlowNode<...>` x8 and `CG2AutoRegFlowNodeSingleton<...>` x5 (+7 PostFX singleton instances); "Triggered on level load if MMORPG game" (0x39FD95F0), "GameRulesName" (0x39FD95E0).
- Repo state: all 20 registered and verified (`ZoneNativeFlowNodeRegistration.cs`, `ZoneNativeFlowNodes.{GameMode,Player,PostEffects,ScreenFaders,Sensors,Tornado}.cs`). Per TODO.md F1 table, remaining: Player nodes take the absent-actor path (no concrete `IZoneNativeFlowPlayerOperations`), ActorSensor listener lifetime, real ScreenFader/UI state, Tornado extension lookup, difficulty/debug CVar state. No node outside the 20 is registered natively by x2game.
- Work:
  - Provide `IZoneNativeFlowPlayerOperations` once a real CPlayer exists (SCN-02).
  - Real ActorSystem identity for ActorSensor and GameType nodes.
  - Fader/PostFX texture/render ownership (P2; dedicated mode may never exercise it).
- Depends on: SCN-02
- Confidence: high that count/names are exact; medium on priorities

### SCN-11: Port X2::FlowGraphSkillController / FlowGraphWrapper (graph-driven skill controllers)
- Priority: P2
- Native evidence: `X2::FlowGraphWrapper::LoadFromXML/LoadFromFile/OnXMLStreamCompleted` (strings 0x39F8C5E0 / 0x39F8C6C8 / 0x39F8C630); `X2::FlowGraphSkillController` (`3924EC00` dtor, `3922A530`), extension id "X2:SkillController" (0x39FBC0A0) with `X2::FlowGraphValue<bool|int>`; `DoodadFuncPlayFlowGraph` (0x3A13AAB0).
- Repo state: absent. Only `ZoneActorMovementInput.SkillController*` fields (ordinary, non-flow skill controllers).
- Work:
  - Confirm whether the dedicated zone ever instantiates FlowGraph-based skill controllers / doodad flowgraphs (xref constructor callers of `X2::FlowGraphSkillController`).
  - If yes, expose the extension id and graph loader to the engine FlowSystem (reuse `ZoneNativeFlowNodeRuntime`).
- Depends on: SCN-02, SCN-10
- Confidence: low

### SCN-12: Instance zones (indun) and per-world layout variants
- Priority: P2
- Native evidence: `Game/worlds/%s/world.xml` ZoneList loader `397BDA60`; CVars `instance_index` / "Instance Id" (0x39FCA508/518/528), `instance_id` used in ZWJoin case 2 (`39366760`); strings "no instance zone data (%s - %d)!" (0x3A099A50), `CS_PACKET_INSTANCE_LOADED` (0x3A0259B8), "dynamic zone: isDynamic[%b] targetWorld[%d] requestedZoneKey[%d:%d]" (0x3A05D510).
- Repo state: `ZoneWorldMapContent.cs:172` IsInstance (header or `instance_` name); `ZoneNativeEngine.WorldLayout.cs:62-70` validates against the engine World; `ZoneSceneContent.cs:72` reads `indun_zones`; nothing in Program/host selects an instance world or instance id.
- Work:
  - Add config for world name (`instance_*`) and instance id; ensure `ZoneWorldMapLayout` copes with instance worlds that are not an 8x8 window (invalid-local-axis path at `ZoneWorldMapLayout.cs:31-35`).
  - Send instance_id in join (SCN-08) and verify `indun_zones`-driven policy.
- Depends on: SCN-08
- Confidence: low-medium

### SCN-13: Multi-zone per process, zone reload and seamless streaming decision
- Priority: P2
- Native evidence: native is one zone per process: single `ZoneClient` (`qword_3A67FF80`), single `ServerAreaManager` (`qword_3A606478`), single server-manager set (qword_3A607A40..A98), one level-start / level-end / unload event triple (`392B02F0`); static reload `WZGimmickReloadStatics`, "reloaded desc package" (0x39FDDC40); seamless strings ("foreignerInSeamless", "static gimmick is resurrected by seamless loading" 0x3A01A860) are World/client concepts.
- Repo state: `ZoneSceneCoordinator` claims one scene per World connection (`ZoneStartupState.ClaimScene`); engine admits one entity/physics borrower; `ZoneSceneActivation` deliberately avoids native Seamless LoadZone `365704D0` and streaming (cells fixed at load; TODO.md S1 "native CTerrain activation does not exist").
- Work:
  - Keep one-zone-per-process and document that it matches native.
  - Implement zone reload (WZGimmickReloadStatics, `content-reload` note) and clean re-load of a new scene generation after unload.
  - Decide whether Seamless streaming (neighbor cells as players cross borders) is in scope; if so own native CTerrain activation instead of the imported bootstrap.
- Depends on: SCN-03, SCN-07
- Confidence: medium

### SCN-14: Native AI bounds and navigation load at level completion
- Priority: P1
- Native evidence: `39332730` tail calls `39D2D990` (spatial bounds from zone-provider fields +144..+168) and gEnv+128 vtbl+680 (ServerAIUnitSystem bounds; "virtual+2A8" in repo); level load also loads nav data (strings `AINavModifierPrefabObject::Load() total %ld points loaded` 0x39FD10C8, `ai point prefab loaded` 0x39FD16A0).
- Repo state: spatial half real (`ZoneSceneCoordinator.cs:182-188`); AI half `TODO(ai-bounds-native)` (`ZoneSceneLoadingServices.cs:236-243`); `TODO(navigation-services)` / `TODO(path-finder)` present.
- Work:
  - Call the real engine AI bounds setter in `cryaisystem_dedicate` with the zone's four coordinates.
  - Load AI navigation and nav-modifier prefabs for the level (coordinate with the AI/navigation auditor).
- Depends on: SCN-03
- Confidence: medium

### SCN-15: Prefab object loading used by level entities
- Priority: P2
- Native evidence: `Prefab::LoadObject/LoadBrush/LoadVegetation/LoadParticleObject/LoadOrigin`, `BrushPrefabObject`, `SlotBrushPrefabObject`, `AnimationPrefabObject`, `AIPointPrefabObject`, `AINavModifierPrefabObject`, "total %d prefab node loaded" (0x39FD19E8), `EntityPrefabObject::Load`.
- Repo state: absent (no `Prefab*` hits in AAEmu.Zone).
- Work:
  - Determine whether the dedicated process loads prefab XML at level load (xref callers of `Prefabs::LoadObject`) vs. client-only; implement the server-relevant subset (collision brushes, AI point/nav modifier prefabs).
- Depends on: SCN-03, SCN-14
- Confidence: low
