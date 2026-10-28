<!-- Audit of world-state: native x2game-dev_dedicate.dll (ArcheAge 10.0.2.13 r575) vs this repo at 76e439f. Paths rewritten to the src/ layout; line numbers refer to 76e439f. -->

## World state and zone-wide features (WS)

The repo covers zone-wide state that arrives from World over WZ packets, and it does so with high fidelity. These are the time of day and world game clock, the faction and relation snapshots, Dominion and siege state (including the siege relation callback), conflict-zone state, the game-schedule controller, the world content filter and its policy, persistent (indun) spawner save and restore, and the achievement, appellation and loot content indexes. The coverage is almost entirely state ingestion plus the native read-only decision functions (relation math, siege `Contains`, spawner activation). Almost nothing is driven end to end by a live player session, which matches the TODO: the first-entry milestone is not achieved and there is no persistent host. The gaps are:

- The WZ handlers for houses, tower defense, game activity, zombie characters, backpack drops, zone-status reports and spawners-in-area. The repo defines only their opcodes, and `ZoneSession.ReceiveCore` counts them as "unimplemented".
- Most ZW senders. Only ZWZoneLoaded, UnitMovements and SaveInstanceSpawner are produced.
- Area enter/leave events (the quest-area manager).
- Player transfer between zones and zone restart/recovery. Neither has any native counterpart or repo code in the zone process.

The native server has no weather or season system. The only hits are the client `CmdEnvSetWeather` command and the Hero season UI, so those are not tasks. The oracle DLL is a combined client+server build, so many strings (achievement UI, resident commands, housing cursors) are client-only and were excluded.

### Coverage table

| Native item | Native evidence | Repo status |
|---|---|---|
| Time of day (WZ 0x54/0x55, ZW 0x1D/0x1E) | TimeOfDayManager ctor 0x300B1590; handlers 0x39332600/0x39331DC0/0x39332130; ZWTimeOfDay ctor 0x39332400 (vftable at ZWTimeOfDayPacket) | Implemented: `src/AAEmu.Zone/WorldState/ZoneTimeOfDayState.cs`, `src/AAEmu.Zone.Protocol/Messages/ZoneTimeOfDay.cs`. Wired in `ZoneSession.cs` (~line 530) and `src/AAEmu.Zone/WorldLink/ZoneStartupState.cs` (~line 107) |
| World game clock (WZ 0x85) | handler 0x393643E0; TimeKeeper::AddSample 0x3303CEC0 | Implemented: `src/AAEmu.Zone/WorldState/ZoneWorldGameClock.cs` |
| Weather / seasons | Only client `X2::CmdEnvSetWeather` (0x390B0690), `ce_set_weather`, and Hero season UI. No `GameServer` weather class | Not applicable on the zone server |
| Game schedules (WZ 0x6A-0x6C) | `X2::GameServer::GameSchedule::Start` (0x393353B0), `::End`, strings "GameSchedule(%u) %s: Activated %u spawners" | Implemented: `src/AAEmu.Zone/WorldState/ZoneGameScheduleController.cs`, dispatch in `ZoneSession.cs` ~line 381 |
| Game activity (WZ 0x6D/0x6E) | `WZGameActivityStart/EndPacket` ctors 0x3937C5D0 / 0x3937C780; registrar 0x3936F740 | Opcodes only; the packet bodies are not decoded and there is no handler |
| Tower defense (WZ 0x65-0x69, ZW 0x32) | `X2::GameServer::TowerDef::OnQueriedPlayability` (0x393591D0, "tower def is playing already"); WZTowerDef* ctors 0x393715D0, 0x3937BED0/BFB0/C090/C170/C250; spawn reason "TowerDef(%u,%u)" in sub_39348440 | Only spawner-controller content (`src/AAEmu.Zone/Content/ZoneNpcSpawnerControllerContent.cs`, `ZoneNpcSpawnerEvents.TowerDefenseControlled`). The WZ/ZW handlers are absent |
| Conflict zone (WZ 0x84) | handler 0x3932C780, strings "mismatch conflict zone group foundZG(%u) != zoneGroup(%u)" | Implemented: `ZoneConflictState.cs`, `src/AAEmu.Zone.Protocol/Messages/ConflictZoneState.cs`. Consumers (PvP rule decisions) are not located |
| Dominion/siege (WZ 0x60-0x64) | handlers 0x3932C840 (DominionDeleted), 0x3932C940 (SiegeState), 0x39695460 (Contains) | Implemented: `ZoneSiegeState.cs`, `src/AAEmu.Zone.Protocol/Messages/SiegeState.cs`. Wired through `RelationshipWorld` |
| Factions and relations (WZ 0x13-0x1C) | 0x39C94270, 0x39C942F0, 0x39C969C0, 0x39C95930, loader 0x39C063F0 / 0x39C067E0 | Implemented: `ZoneFactionState.cs`, `ZoneUnitRelationships.cs`, `ZoneRelation.cs`, `src/AAEmu.Zone/Content/ZoneFactionContent.cs`, `Messages/Faction*.cs` |
| Faction events: AttackFaction 0x89, FactionImmigrateNpcSpawnerCondition 0x8A | `WZAttackFactionPacket`, `WZFactionImmigrateNpcSpawnerConditionPacket` (RTTI) | Opcodes only |
| Indun / persistent instance spawners | `CanSaveInstanceSpawner` (0x3933ED40, strings "indun-persistent spawner should not have npc group"); ZWSaveInstanceSpawner ctor 0x39347F90; "failed to spawn persist instance npc" in sub_39349800; spawn reason "IndunAction" | Implemented: `src/AAEmu.Zone.Protocol/Messages/SaveInstanceSpawner.cs`, `ZoneNpcSpawnerRegistry.cs` lines 206-240 and 519-583. The reload path from the join `PersistentSpawners` is restored through `ApplyPersistentSpawners` |
| World content filter / policy | 0x39C5B580 (join filter buffer) | Implemented: `src/AAEmu.Zone.Protocol/Messages/WorldContentFilter.cs`, `ZoneWorldContentPolicy.cs`. Applied to achievement and loot indexes and stats |
| Quest areas / area enter-leave (ZW 0x23/0x24) | `ServerAreaManager::LoadAreas` (0x3932B530, "failed to load quset area from (%s)", file key `quest_area`); area enter event 0x3932B1B0 and leave event 0x3932B370, which send ZWEnterArea/ZWLeaveArea | Absent. `ZwOpcodes.EnterArea/LeaveArea` are defined but nothing builds or sends them |
| Spawners in area (WZ 0x42) | `WZActivateNpcSpawnersInAreaPacket` ctor 0x39379D70 | Opcode only. `ZoneNpcAreaSpawner.cs` is a different feature (area-type spawner placement) |
| Housing (WZ 0x06, 0x4D, 0x4E, ZW 0x07) | `Housing::SetBuildDone` (0x3933AB50), `HousingManager::Create` (0x3933AD20, "no house unit %u"), `Housing::GetHousingUnit`; WZHouseState ctor 0x39389B80, WZHouseBuildDone 0x3937A700, ZWRemoveHouse 0x3933AE70 | Absent. Only `HousingStatTemplate` content and `IsInHousingArea` (`ZoneSceneProviders.cs` ~line 325) |
| Residents / hero / dominion economy | Strings are client-only (`c_resident_*`, "ZoneGroup %u ... Resident not available") | Not a zone-server concern |
| Achievements | Loader 0x39C56B80 | Content index only: `src/AAEmu.Zone/Content/ZoneAchievementContent.cs`, `src/AAEmu.Zone/Units/ZoneAchievementIndex.cs`. No runtime tracking. No evidence the zone server tracks achievements; the World server likely owns that |
| Appellations | Loader 0x39C8B020 | Content only: `src/AAEmu.Zone/Content/ZoneAppellationContent.cs` (stamp stat application in `ZoneStatContent`) |
| Progression (level, skills, passives) | WZ 0x7C-0x7E, 0x87 | Implemented: `ZoneProgressionState.cs`, dispatch in `ZoneSession.cs` |
| Loot | Content loaders | Content only: `src/AAEmu.Zone/Content/ZoneLootContent.cs`, `ZoneLootIndex.cs`. No drop-roll runtime. The zone only reports `BackpackDropped`; the roll is likely World-owned |
| Zombie character (WZ 0x82), DropBackpack (0x7F), ZWBackpackDropped (0x36) | ctors 0x3937DB80, 0x3937D870, 0x3932B930 | Absent |
| RequestZoneStatus / ReportZoneStatus (WZ 0x77) | ctors 0x39371640 / 0x39365090 | Absent |
| Heartbeat (ZW 0x3C) | ZWHeartbeatPacket ctor 0x39332510 | Message defined (`src/AAEmu.Zone.Protocol/Messages/ZoneHeartbeat.cs`). The scheduling loop is not verified here |
| Zone restart / transfer / recovery | No zone-server native logic found. The strings "recovery" and "transfer" in the zone binary are client UI or the unrelated `Transfer` unit kind | Repo has `ZoneNativeProcessOwnership` and `ZoneSceneCoordinator` retirement. No cross-zone transfer code, which is consistent with World owning transfers |

### Tasks

### WS-01: Implement ZWEnterArea/ZWLeaveArea and ServerAreaManager (quest areas)
- Priority: P1
- Native evidence: `ServerAreaManager::LoadAreas` sub_3932B530 (string "failed to load quset area from (%s)", `quest_area` file); enter event 0x3932B1B0 (logs "area enter event received (event, player eid, area id, area eid)") and leave event 0x3932B370; ZWEnterAreaPacket / ZWLeaveAreaPacket (RTTI, vftables inside those functions). The ZWLeaveArea size constant is 36.
- Repo state: absent. `ZwOpcodes.EnterArea` / `LeaveArea` (0x23 / 0x24) are defined but never constructed or sent. No `Messages/EnterArea*.cs`.
- Work:
  - Decompile 0x3932B530 and the enter/leave callbacks. Recover the area-file format, the trigger shapes, and the body layout of the ZW packets (the event id, the player eid, the area id, the area eid, and the position floats at +256/+264).
  - Add `Messages/ZoneAreaEvent.cs` encode and a loader for the quest-area data that targets the zone's world path.
  - Run area containment testing on the player movement frame (the ZoneUnit spatial index) and queue the enter/leave frames through `Registry.QueueNotification`.
- Depends on: player actor and movement frame loop (outside this area).
- Confidence: medium (packet layout still to be read).

### WS-02: Handle the remaining WZ world-state packets instead of counting them unimplemented
- Priority: P1
- Native evidence: registrar sub_3936F740 binds every WZ packet to a handler. The packet ctors are WZRequestZoneStatus 0x39371640, WZZombieCharacter 0x3937DB80, WZDropBackpack 0x3937D870, WZActivateNpcSpawnersInArea 0x39379D70, WZGameActivityStart/End 0x3937C5D0 / 0x3937C780, WZVegetationCutdown 0x3937D050, WZCheckMoleMiner 0x3937D9C0, WZNpcBanned 0x3937CC10, WZFactionImmigrateNpcSpawnerCondition and WZAttackFaction (RTTI).
- Repo state: opcodes in `WzOpcodes.cs` lines 53-143 (e.g. `RequestZoneStatus` 0x77, `ZombieCharacter` 0x82, `DropBackpack` 0x7F, `ActivateNpcSpawnersInArea` 0x42, `GameActivityStart` 0x6D, `AttackFaction` 0x89, `FactionImmigrateNpcSpawnerCondition` 0x8A). `ZoneSession.ReceiveCore` falls into the default branch ("not implemented by this wave; refused by name and counted").
- Work:
  - For each opcode, decompile the handler through the registrar (sub_3936F740) and decode the body into `Messages/`.
  - Implement `RequestZoneStatus` (needed so World can query the zone, with the ZWReportZoneStatus reply built at 0x39365090), `ActivateNpcSpawnersInArea` (wire to `ZoneNpcSpawnerRegistry`), `FactionImmigrateNpcSpawnerCondition` / `AttackFaction` (spawner/faction condition changes) and `GameActivityStart/End`.
  - Defer the cosmetic ones (VegetationCutdown, Gimmick*) to the owning area.
  - Add tests in `AAEmu.Zone.Tests`.
- Depends on: WS-04 for the ZW send path.
- Confidence: medium.

### WS-03: Implement tower-defense WZ/ZW handling
- Priority: P2
- Native evidence: `TowerDef::OnQueriedPlayability` sub_393591D0 ("TowerDef(%u,%u): The tower def is playing already!", ZWTowerDefReportPlayability reply); WZTowerDefReload / QueryPlayability / Start / End / WaveStart ctors 0x393715D0 / 0x3937BFB0 / 0x3937C090 / 0x3937C170 / 0x3937C250; spawner activation "TowerDef %u spawner %u type %u event %d active %d" in sub_39349800; spawn reason "TowerDef(%u,%u)" in sub_39348440.
- Repo state: content only (`src/AAEmu.Zone/Content/ZoneNpcSpawnerControllerContent.cs` `ZoneNpcSpawnerTower`, `ZoneNpcSpawnerEvents.TowerDefenseControlled` and `MarkTowerDefenseControlled`). No WZ decoding of the five opcodes, no ZW report.
- Work:
  - Decode the five WZ TowerDef packets and the ZW 0x32 report in `Messages/`.
  - Implement a tower-def controller that mirrors `ZoneGameScheduleController` (start/wave/end drive spawner phases, with a playing-already guard).
  - Reply to the playability query with the phase count.
- Depends on: WS-02 pattern, spawner registry.
- Confidence: medium.

### WS-04: Emit the missing ZW world-state frames (ReportZoneStatus, Heartbeat loop, BackpackDropped, RemoveHouse)
- Status: **Partial** — heartbeat loop done via ZW-01; ReportZoneStatus, BackpackDropped, RemoveHouse remain
- Priority: P1
- Native evidence: ZWHeartbeat ctor 0x39332510, ZWReportZoneStatus 0x39365090, ZWBackpackDropped 0x3932B930, ZWRemoveHouse 0x3933AE70, ZWZoneLoaded 0x39332730 (already sent).
- Repo state: only `ZwOpcodes.ZoneLoaded` and `UnitMovements` are sent from `AAEmu.Zone`. `src/AAEmu.Zone.Protocol/Messages/ZoneHeartbeat.cs` exists but no scheduler references it (verify before building).
- Work:
  - Confirm whether the heartbeat is produced; if not, add a periodic sender in the session loop.
  - Add `Messages/` encoders for ReportZoneStatus and BackpackDropped.
  - Hook the backpack drop to `WZDropBackpack` (WS-02).
- Depends on: persistent host (TODO F-series).
- Confidence: medium.

### WS-05: Implement housing state (WZHouseState, HouseBuildProgress/Done, ZWRemoveHouse, housing-area test)
- Priority: P2
- Native evidence: `HousingManager::Create` 0x3933AD20 ("no house unit %u"), `Housing::SetBuildDone` 0x3933AB50 ("unit model is not prefab"), `Housing::GetHousingUnit`; WZHouseState ctor 0x39389B80, WZHouseBuildDone 0x3937A700, ZWRemoveHouse 0x3933AE70; `ZoneUnitKind.Housing` stats from `housings` / `housing_build_steps`.
- Repo state: absent for house creation, build state and removal. `HousingStatTemplate` content exists in `ZoneStatContent.cs`. `IsInHousingArea` is implemented in `ZoneSceneProviders.cs` ~line 325. `ZoneSiegeState.Participant` already handles Housing unit faction.
- Work:
  - Decode `HouseState`, `HouseBuildProgress` and `HouseBuildDone` (WZ 0x06 / 0x4E / 0x4D) and the ZW RemoveHouse request.
  - Create a Housing `ZoneUnit` from the prefab model (zd model prefab) through `ZoneUnitRegistry`.
  - Track build steps and HP, and apply the siege relation for housing.
- Depends on: unit registry and model descriptors.
- Confidence: medium.

### WS-06: Wire world-state consumers into gameplay (conflict state to PvP rules, siege and relation to combat, schedule to spawners)
- Priority: P1
- Native evidence: conflict handler 0x3932C780, siege relation callback 0x39695460 and 0x3932C940, `GameSchedule::Start/End` 0x393353B0, combat relationship dump `s_combat_relationship_dump`.
- Repo state: state holders are implemented (`ZoneConflictState`, `ZoneSiegeState.GetRelation`, `ZoneUnitRelationships`, `ZoneGameScheduleController`). `ZoneSession.ReceiveCore` throws NotSupportedException when no `RelationshipWorld` is configured. No evidence in this audit that `ConflictState` is consulted by any combat or attack check beyond holding the byte.
- Work:
  - Grep the native for readers of the conflict global at 3A517B70 (state=7 default) and reproduce each consumer (attack permission, drop rules).
  - Make sure the session always constructs a `RelationshipWorld` for a real join, so the throw above cannot trigger on a conflict or siege packet.
  - Add integration tests that feed WZ packets through `ZoneSession` and assert the relation results.
- Depends on: player actors.
- Confidence: medium-low.

### WS-07: Confirm and (if native) implement loot, achievement and appellation runtime ownership
- Priority: P2
- Native evidence: loaders 0x39C56B80 (achievement and appellation indexes), 0x39C8B020 (merits). The strings visible in the zone binary for tracing and UI (`ClientAchievementManager`, `GetAchievementInfo`) are client-only. No `X2::GameServer::*Achievement*` or `*Loot*` RTTI exists; the zone-side RTTI list (`CharacterManager`, `NpcManager`, `ZoneBuffMan`, `Housing`, `ServerAreaManager`, ...) has none.
- Repo state: content indexes only (`src/AAEmu.Zone/Content/ZoneAchievementContent.cs`, `ZoneAppellationContent.cs`, `ZoneLootContent.cs`, `src/AAEmu.Zone/Units/ZoneAchievementIndex.cs`, `ZoneLootIndex.cs`), filtered by `ZoneWorldContentPolicy`. `ZoneSession.Achievements` property is exposed but no runtime tracker.
- Work:
  - Verify by xref whether any GameServer code (e.g. NPC death) calls the loot or achievement index; the evidence suggests World owns both.
  - If unreferenced, mark the indexes as parity-only and avoid building runtime trackers in the zone.
  - If referenced, add the specific hook (e.g. char-record counters on kill).
- Confidence: low (negative finding, not exhaustively proven).

### WS-08: Document and verify absent zone-side recovery / transfer / weather behavior
- Priority: P2
- Native evidence: no zone-server handler for weather or seasons (only client `CmdEnvSetWeather`, 0x390B0690); no zone transfer logic. Transfers and restarts appear to be orchestrated by World through ZWJoin / retirement, and the spawner persistence is the only zone-owned state (WS: SaveInstanceSpawner).
- Repo state: no code, correct by absence. Join-time restoration of persistent spawners is `ZoneNpcSpawnerRegistry.ApplyPersistentSpawners` (line 230), driven by the scene coordinator ordering ("39332730 sends ZoneLoaded before persistent restoration").
- Work:
  - Add a short note to `TODO.md` that weather, seasons and cross-zone transfers are not zone-process responsibilities.
  - Test the reconnect path: a persisted instance spawner restore after a second join in the same process.
- Confidence: medium.

### WS-09: Match native time-of-day reporting (no ZW 0x001D/0x001E on the continent host)
- Status: **Done** — `ZoneServerType` (world `main_world` = type 3, per 0x393654B0 comparing against off_3A5192C0 = "main_world"; instance worlds = 2) skips the initial detailed report and both per-tick time reports; `ZoneEngineCVars` reads `game/config/dedicated.cfg` from the asset sources with `+name value` CVars overriding, and `e_time_of_day_engine_update = 0` freezes `ZoneTimeOfDayState.Advance`. Live replay check not run. Open: the type-3 string at 0x3A5192C0 was not recovered (IDA offline), so instance id 0 stands in for the native map-descriptor compare.
- Priority: P1
- Native evidence: 0x393654B0 sets ZoneClient+140 (dedicated: 3 if the map descriptor string equals the global at 0x3A5192C0, else 2). 0x39332130 (initial ZW 0x001E) and the 0x393324E0 time phase (0x393322F0 detailed, 0x39332400 hourly ZW 0x001D) are skipped for type 3. The 6200 s native baseline for zone 129 contains zero 0x001D/0x001E. `e_time_of_day_engine_update` is not referenced by x2game (engine-side CVar); dedicated.cfg sets it to 0.
- Work: none remaining beyond recovering the 0x3A5192C0 string.
- Confidence: medium.
