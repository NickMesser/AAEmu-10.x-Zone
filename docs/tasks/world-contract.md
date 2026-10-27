<!-- Audit of world-contract: native x2game-dev_dedicate.dll (ArcheAge 10.0.2.13 r575) vs this repo at 76e439f. Paths rewritten to the src/ layout; line numbers refer to 76e439f. -->

## World ↔ Zone contract

Paths: `W` = `C:\Users\messe\AppData\Local\Temp\aaemu-10x-world\repo\AAEmu.WorldServer\AAEmu.World\`. Game-side files are written as `AAEmu.Game/...`. `Z` = `C:\Users\messe\Documents\GitHub\AAEmu-10.x-Zone\` (`AAEmu.Zone/`, `AAEmu.Zone.Protocol/`), snapshot `76e439f`.

**Summary.** The wire layer already matches. Framing is identical, and all WZ opcode values are identical (compared constant by constant). ZW values agree wherever both sides define one. The ZWJoin, JoinResponse, FactionList, RelationList, SpawnerList, ToD and WorldGameTime layouts agree, and so do ZWSpawnNpc (first 41 bytes), ZWRemoveNpc and ZWUnitMovements framing. The replacement's startup exchange (`ZoneWorldClient.SynchronizeAsync`) should complete against World's join sequence unchanged. The gaps are in the launch contract and in gameplay breadth.

1. **The replacement cannot be launched by World as written.** World passes native `+cvar` arguments and the env vars `AAEMU_ZONE_DLL`, `AAEMU_ZONE_SAVE_DIR` and `AAEMU_ZONE_LOG_NAME`. The replacement's `ParseArguments` throws `Unknown option` on the first `-dedicated` or `+zone`. It needs `--config runtime.json --host --spawner-file ...`, and World supplies none of that. World also pre-checks that `ZoneHost.NativeDll` exists.
2. **The replacement always joins with `iid=0`.** World keys dungeon copies by `(zoneKey, world.Id)` and waits for exactly that copy, so an instance copy never becomes ready and the dungeon fails after `ReadyTimeoutSeconds`.
3. **World only launches instance zones** (dungeons and the warm pool). Continent (static) zones are started by an external "Zone Manager" that is not in either repo.
4. **"Ready" means World received ZW `ZoneLoaded` (0x001F) on that connection.** Nothing else counts. World does not watch the child after 300 ms, so a crashed host costs the full 120 s.
5. **Many WZ opcodes World sends are only counted and ignored by the replacement.** The most important is `ActivateNpcSpawnersInArea` (0x0042), World's NPC-population trigger. Doodad, gimmick and house packets are also ignored, and several ZW combat opcodes World relays have no sender in the replacement.

Tasks: 16 (P0: 6, P1: 6, P2: 4).

---

## 1. Launch contract

| Item | World behaviour (file:line) | Replacement state (file:line) |
|---|---|---|
| Executable | `ZoneHost.Executable`, passed through `Path.GetFullPath`. Default `""` and `Enabled=false`. `Core/ZoneHost/ZoneHostCommandLine.cs:34,76`; `Models/WorldAppConfiguration.cs:75-101`; `Config.json:30-45`. Expects `AAEmu.ZoneHost.exe`, a console PE that calls AllocConsole (`ZoneHostWin32.cs:9-13`). | The output is a managed `AAEmu.Zone.dll` (README says run it with `dotnet`). An apphost exe would also work. |
| Preflight | `TryStart` fails if `Executable`, `NativeDll` or `WorkingDirectory` is missing (`ZoneHostSupervisor.cs:219-235`). `Build` throws on empty values (`ZoneHostCommandLine.cs:34-39`). | There is no native zone DLL. A placeholder file or a World change is needed. |
| Working dir | `ZoneHost.WorkingDirectory` goes to `CreateProcessW lpCurrentDirectory` (`ZoneHostCommandLine.cs:77`; `ZoneHostWin32.cs:70-83`). | Paths resolve from the config-file directory or are absolute (`src/AAEmu.Zone/Hosting/ZoneRuntimeConfiguration.cs:53-60`), so the cwd is not important. |
| Args | Built in the order below, each value quoted (`ZoneHostCommandLine.cs:41-66`; quoting at `ZoneHostWin32.cs:19-30`). | `Program.cs (pre-restructure):674-690` `ParseArguments` accepts only `--config --content-db --zone --world-host --world-port --locale --spawner-file --terrain-file --world-file --world-name --research-*` and the mode flags, and throws on anything else. |
| `-dedicated` | Emitted when `Dedicated` (default true). | Rejected. |
| `+world_ip`, `+world_port`, `+world_serveraddr`, `+world_serverport` | `WorldIp`/`WorldPort` (defaults `127.0.0.1` / `1240`), each value twice under both names. | Only json `worldHost`/`worldPort`. `--config` forbids overriding them (`Program.cs (pre-restructure):70-75`). |
| `+zone` and `+sv_map` | Same value for both: the World template name, e.g. `instance_howling_abyss`, not a numeric key (`ZoneHostSupervisor.cs:212,252`). | json `zoneName` and `zoneKey` are both required and `ResolveZone` must agree (`ZoneRuntimeConfiguration.cs:128-134`). |
| `+instance` | `world.Id`. It is 0 only for continent hosts from Zone Manager. | No instance concept. `Program.cs (pre-restructure):159-160` joins with `ZoneJoin(1203,1203,zone.Key,0,0,0,0,false)`, so `iid=0`. |
| `+sv_port` | `SvPortBase (65000) + counter % 1000` (`ZoneHostSupervisor.cs:476-490`). World never connects to it. | Not needed, but must be accepted. |
| `+db_location` | Default `game/db/game_decrypted.sqlite3`, relative to the working dir. | json `contentDatabasePath`. |
| Render cvars | `+e_render 0 +r_Driver Null` when `DisableRendering` (default true). | Must be ignored. |
| AI cvars | `+npc_move_skip_standing`, `+npc_move_skip_disabledAI`, `+npc_movement_skip`, `+ai_systemupdate` are always emitted. Then `ExtraArguments` is split on whitespace and quotes, so later duplicates override (`ZoneHostCommandLine.cs:60-66`; test `ZoneHostCommandLineTests.cs` `Build_CustomAiSchedulingAndExtraArguments_AppearInOverrideOrder`). | Must be ignored. |
| Env | World's own environment plus three overlays (`ZoneHostCommandLine.cs:68-73`; `ZoneHostWin32.cs:32-58`). `AAEMU_ZONE_DLL` = `NativeDll`. `AAEMU_ZONE_SAVE_DIR` = `<RuntimeLogRoot or %TEMP%\AAEmuZoneHost>\Logs\<zone>-<worldId>`. `AAEMU_ZONE_LOG_NAME` = `ArcheAge-<zone>-<worldId>-<ts>.log` (`ZoneHostSupervisor.cs:246-251`). | Not read. The only env used is `AAEMU_ZONE_LUA_LIBRARY` and a retirement diagnostic. Logs go to the json `logDirectory` and `Console.WriteLine`. |
| Process flags | `DETACHED_PROCESS \| CREATE_UNICODE_ENVIRONMENT`, `bInheritHandles=false`, no stdio (`ZoneHostWin32.cs:15-17,62-90`). | A detached process has no console, so a log file is the only diagnostic. |
| Ready | `DungeonLoaderTask.WaitForZoneHost` polls every 250 ms for up to `ZoneHost.ReadyTimeoutSeconds` (default 120, env override `AAEMU_ZONE_HOST_READY_SECONDS`) for `IsZoneInstanceLoaded(zoneKey, world.Id)`. `zoneKey = world.Template.ZoneKeys[0]` (`AAEmu.Game/Models/Game/Indun/DungeonLoaderTask.cs:111-145`; `Program.cs (pre-restructure):99-100`). That is `ZoneSession.GetByZoneInstance`, which needs `State >= ZoneLoaded` (`Program.cs (pre-restructure):70-73`; `Core/Zone/ZoneConnectionRegistry.cs:184-191`). State becomes ZoneLoaded only on ZW 0x001F (`Core/Network/ZoneProtocolHandler.cs:134-136`). | `ZoneSceneCoordinator.cs:181` sends 0x001F after level load, ToD, areas, authored spawners and NPC post-load. ReadyTimeout must cover the replacement's native-engine and scene load time. |
| Ready, manual host | If the supervisor did not start a process (`spawnedHost=false`), any single ZoneLoaded host for the zone key is accepted (`requireExactCopy=false`, `DungeonLoaderTask.cs:139-145`). System instances (Mall) never spawn or wait (`DungeonLoaderTask.cs:26-30,44`). | n/a |
| Early-exit check | After `CreateProcess`, `WaitForExit(300)`. If the child is already gone, `TryStart` returns false and the dungeon is destroyed, with players getting `NoServerInstanceResource` (`ZoneHostSupervisor.cs:254-258`; `DungeonLoaderTask.cs:31-35,92-105`). | A crash after 300 ms is not noticed until the full ReadyTimeout. |
| Stop | Dungeon destroy/dispose calls `StopInstanceZoneHost(World.Id)`, which goes to `StopForWorld` and `Process.Kill(entireProcessTree:true)`, then releases the port (`AAEmu.Game/Models/Game/Indun/Dungeon.cs:577,617`; `ZoneHostSupervisor.cs:389-403,547-558`; `Program.cs (pre-restructure):83-90`). There is no graceful stop. World does not stop hosts on its own shutdown (`Program.cs (pre-restructure):1554-1580` only nulls delegates). | The host exits 0 on Ctrl+C or World EOF (`Program.cs (pre-restructure):124-146`), so a hard kill is acceptable. |
| Warm pool | Optional (`ZoneHost.WarmPool.Enabled`, `Zones[].WorldTemplateName/Size`, `DefaultSize=2`, `IdleUnloadSeconds=1800`). Env: `AAEMU_WARM_INSTANCES`, `AAEMU_ZONEHOST_WARM_{ENABLED,DEFAULT_SIZE,IDLE_SECONDS}` (`ZoneHostWarmPool.cs:7-52`). After `OnMainWorldReady`, `StartWarmPool` fills idle hosts, each with its own `WorldInstance` (`ZoneHostSupervisor.cs:78-147,315-356`; `Program.cs (pre-restructure):242-274`). `TryClaimWarm` binds an idle slot to the party or character id. A 30 s cull stops idle slots. A cold spawn refills on `StopForWorld`. Pre-spawn waits for ZoneLoaded (`Program.cs (pre-restructure):255-266`). Tests: `AAEmu.UnitTests/World/Core/ZoneHost/ZoneHostWarmPoolTests.cs`. | The replacement must load and connect with no players, then idle. The per-copy memory and CPU cost is unmeasured. |
| Static (continent) zones | World never starts them. `TryStart` is reachable only from `DungeonLoaderTask` and the warm pool (`Program.cs (pre-restructure):79-90,242-274`). The config comment says continent hosts keep the Zone Manager default (`WorldAppConfiguration.cs`, `SvPortBase` doc). A static host connects on its own (`+world_ip/+world_port`, default 127.0.0.1:1240) and registers with `iid=0`. Without a ZoneLoaded host the player's enter is refused with a once-per-zone warning (`PlayerEnterService.cs:25-33,182-205`). | `--host` takes World host and port and joins once (`Program.cs (pre-restructure):155-162`). Nothing launches it per zone. |
| Disconnect or crash | `OnDisconnect` removes NPC mirrors for the connection, calls `TowerDefScheduler.OnZoneDisconnected`, removes the registry entry, resets NpcStateSent and the buff registry for `(zone, instance)`, and logs an error. It then calls `WorldIntegration.NotifyZoneLost`, which runs `ReturnInWorldClientsToCharacterSelect(zoneId, reason, instanceId)`. Clients in that copy go to character select and other copies are untouched (`ZoneProtocolHandler.cs:34-57`; `AAEmu.Game/WorldIntegration.cs:861-877,1420-1450`). There is no auto-restart, no process watchdog and no dungeon teardown, and the supervisor keeps the dead `TrackedHost` until `Stop`. A host that rejoins the same `(zone, iid)` replaces the registry entry (`ZoneConnectionRegistry.cs:158-178`). | The replacement process exits when the session ends. |

---

## 2. Network handshake

### Framing (byte for byte)

| Aspect | World | Replacement | Match |
|---|---|---|---|
| Transport | Raw TCP. World listens on `ZoneNetwork` `*:1240` (`Config.json:2-5`; `Core/Network/ZoneNetwork.cs:17-30`). | `TcpClient{NoDelay}.ConnectAsync(worldHost, worldPort)` with a 30 s connect deadline (`Program.cs (pre-restructure):155-158`). | yes |
| Header | `[u16 LE payloadLen][u16 LE opcode][body]`, where `payloadLen = 2 + body` (`Core/Packets/ZonePacket.cs:5-22,28-36`; `AAEmu.Commons/Network/LengthPrefixedFrames.cs:25-64`). | `ZoneFrame.cs:5-24`: `HeaderSize=4`, length u16 LE = `Body+2`. | yes |
| Endianness | `PacketStream` is little-endian by default. | `BinaryPrimitives.*LittleEndian`. | yes |
| Max size | u16, so body ≤ 65533. | `MaxBodySize = 65533` (`ZoneFrame.cs:12`). | yes |
| Encryption, compression, checksum | None. | None. | yes |
| Short frame (`payloadLen < 2`) | Dropped and the reader advances 2 bytes (`LengthPrefixedFrames.cs:46-50`; `ZoneProtocolHandler.cs:79-116`). | Throws `InvalidDataException` (`ZoneFrame.cs:35-36`; `ZoneFrameDecoder.cs:73-75`). | Compatible, since a well-behaved zone never sends length < 2. |
| Strings and ids | `u16 len` + UTF-8; 3-byte packed BC ids. | `ZoneReader/ZoneWriter` string and `ReadBc/WriteBc` use the same forms. | yes |
| Heartbeat or timeout | None. `AAEmu.Commons/Network/Core` has no idle timeout or keepalive. The ZW heartbeat (0x3C) is read and logged at Debug only (`ZoneProtocolHandler.cs:131-133`; `Packets/Zw/ZWHeartbeatPacket.cs`). World neither replies nor runs a liveness timer. | `src/AAEmu.Zone.Protocol/Messages/ZoneHeartbeat.cs` exists but nothing in `AAEmu.Zone` sends it. `ZoneSceneProviders.cs:967 UpdateHeartbeat` only advances ToD and script clocks. | World does not require it. |

### Join sequence

| # | Dir | Packet | World file:line | Replacement file:line |
|---|---|---|---|---|
| 1 | Z→W | `ZWJoin` 0x0000: `u32 pFrom, u32 pTo, i32 id (zone key), u32 ip, u16 port, u64 accountId, u32 iid, u8 dev` (31 bytes). | Read at `Packets/Zw/ZWJoinPacket.cs:12-30`. World uses only `Id` and `InstanceId`. Accepted only if `State < Joined`; a repeated opcode 0 is ignored (`ZoneProtocolHandler.cs:122-126`). | `src/AAEmu.Zone.Protocol/Messages/ZoneJoin.cs:4-23`, protocol 1203/1203. `Program.cs (pre-restructure):159` sends ip=0, port=0, account=0, iid=0, dev=false. |
| 2 | W→Z | `WZJoinResponse` 0x0000: `u16 reason=0, u16 fsetLen + fset[31], u8 bf=0, u16 type=0, u8 pvp=0, u8 duel=0, u16 type2=0, u32 peaceMin=0, u32 filterSize=0` (no filter body). | `Packets/Wz/WZJoinResponsePacket.cs:41-93`. fset[0]='1', plus bit 60 (PhysicsDamage) and bit 177 (ServerSideShipSim). Kill switches `AAEMU_WZ_FSET_PHYSICS_DAMAGE=0`, `AAEMU_WZ_FSET_SERVER_SIM=0`. | `src/AAEmu.Zone.Protocol/Messages/ZoneJoinResponse.cs:52-89` reads the same layout. `Reason != 0` throws (`ZoneStartupState.cs:84-85`). An empty filter is accepted (`WorldContentFilter.Read`). |
| 3 | W→Z | `WZFactionList` 0x0013, chunks of ≤20: `[u32 total][u8 count][FactionDesc…]`. An empty list is one packet with total 0. | `Packets/Wz/WZFactionListPacket.cs:15-71`; wire at `AAEmu.Game/Models/Game/Faction/SystemFaction.cs:108-125`. `AAEMU_WZ_REAL_FACTIONS=0` sends empty lists (`ZoneProtocolHandler.cs:205-215`). | `src/AAEmu.Zone.Protocol/Messages/FactionSnapshot.cs` has identical field order and widths (strings max 128). Complete when entries equal total (`src/AAEmu.Zone/WorldLink/ChunkedSnapshot.cs:14-31`). |
| 4 | W→Z | `WZFactionRelationList` 0x0014, chunks of ≤200: `[u32 total][u8 count][{u32 id, u32 id2 (id<=id2), u8 state, u8 nState, u64 updateTime, u64 changeTime, i64 updaterId, str, i64 confirmerId, str}…]`. | `Packets/Wz/WZFactionRelationListPacket.cs:15-97`. | `src/AAEmu.Zone.Protocol/Messages/FactionRelationSnapshot.cs`, same layout. |
| 5 | W→Z | `WZSpawnerList` 0x0005: `[u8 last][u8 count≤100][{u32 id, u32 type, u8 state}…]`. The final chunk has last=1, and an empty last=1 chunk is sent for seamless worlds. The saved-spawner source is `ZoneNpcSpawnerCatalog.GetPersistentSpawners(zoneKey, instanceId)`. | `ZoneProtocolHandler.cs:216-218`; `Packets/Wz/WZSpawnerListPacket.cs`. | `src/AAEmu.Zone.Protocol/Messages/SpawnerSnapshot.cs`, same layout. The startup gate is complete when JoinResponse, relations and the last spawner chunk have arrived and factions are full (`ZoneStartupState.cs:30-31`). |
| 6 | W→Z | `WZTimeOfDay` 0x0054 (`f32`) and `WZDetailedTimeOfDay` 0x0055 (`f32 time, speed, start, end`), sent only if a seed hour exists. Shared-day zones (main world) seed `TimeManager.GetTime`. An instance seeds the authored `time_of_day.xml` start hour, and if none exists it logs an error and sends nothing. | `ZoneProtocolHandler.cs:219-256`. | `src/AAEmu.Zone.Protocol/Messages/ZoneTimeOfDay.cs`. Handled in `ZoneStartupState.Receive` and, after startup, in `ZoneSession.ReceiveCore` (ToD, DetailedToD, WorldGameTime all call `Startup.Receive`). |
| 7 | W→Z | `WZWorldGameTime` 0x0085 (`u32` UTC seconds of day), always sent last. Then World sets `State=Joined`. | `ZoneProtocolHandler.cs:258-259`. | `ZoneStartupState.cs:115-121`. |
| 8 | Z→W | `ZWZoneLoaded` 0x001F, empty body. | `State=ZoneLoaded`, re-index by `(zoneId, iid)`, `PlayerEnterService.OnZoneLoaded` (`ZoneProtocolHandler.cs:134-159`). | `ZoneSceneCoordinator.cs:181`. |
| 9 | W→Z | After ZoneLoaded, World flushes doodads, housing, gimmicks, dominion, combat relations and conflict zone state via `NotifyZoneReadyFor*`. `GameScheduleRelay.OnZoneLoaded` sends schedule start/continue/end. `TowerDefScheduler.OnZoneLoaded` runs. `WZActivateNpcSpawnersInArea` (0x0042) is sent only if `NpcSpawnerActivate.PrewarmOnZoneLoaded`; otherwise it goes out when the first player enters (radius 1024). | `ZoneProtocolHandler.cs:142-158,272-336`; `PlayerEnterService.cs:132-153`. | Doodad, house, gimmick and 0x0042 fall into the default branch (`ZoneSession.cs:535`) and are counted and ignored. |

**Ordering caveat.** The replacement's `SynchronizeAsync` throws `NotSupportedException` for any other WZ opcode received before the gate closes (`ZoneStartupState.cs:122-123`). World sends nothing else between join and the last SpawnerList chunk on that connection. Buff relays use `JoinedZones()` (`PlayerEnterService.cs:226-232`) and could in principle interleave.

---

## 3. Packet usage matrix

### WZ opcodes World SENDS

All WZ opcode values in `W/Core/Packets/Wz/WzOpcodes.cs` equal `Z/AAEmu.Zone.Protocol/WzOpcodes.cs`; no mismatches. "Sent by World" means a `ZonePacket` subclass with that opcode exists in `Core/Packets/Wz`.

| Group | World sends (opcode) | Replacement handling | Notes |
|---|---|---|---|
| Join and bring-online | JoinResponse 0000, FactionList 0013, RelationList 0014, SpawnerList 0005, ToD 0054, DetailedToD 0055, WorldGameTime 0085 | all (`ZoneStartupState.cs:73-125`) | layouts compared and equal |
| Unit lifecycle | UnitState 0007, UnitRemoved 0008, UnitMovement 0009, UnitPoints 0020, UnitDeath 0021, UnitResurrection 0022, UnitEquipmentChanged 001E, 001F, UnitFactionChanged 0019, UnitExpeditionChanged 001A | handled (`ZoneSession.cs:387-455`) | The UnitState body is `WriteWzBody` = `UnitStateWireSerializer` + buffs + `UnitStateActionSerializer` (`AAEmu.Game/Core/Packets/G2C/SCUnitStatePacket.cs:36-41`). The replacement reads it with `UnitStateSnapshot.Read`. No byte-level golden comparison exists (see WC-11). |
| NPC | NpcState 0002 (reply to ZWSpawnNpc), NpcSpawnFailed 0003, NpcStartDespawn 0004, NpcSpawnerEvent 0070, NpcControl 006F, NpcBanned 0072 | NpcState, NpcSpawnFailed, NpcSpawnerEvent handled | **Not handled:** NpcStartDespawn, NpcControl, NpcBanned. |
| Spawner activation | **ActivateNpcSpawnersInArea 0042** | **no handler** | WC-08. |
| Aggro and interact | UpdateAggro 0044, AggroReset 0045, FakeDeath 0046, AggroCopy 0047, InteractNpc 0048, InteractNpcEnd 0049 | handled | |
| Combat state | CombatEngaged 0025, CombatCleared 0026, ForceAttackSet 0024, TargetChanged 002A, UnitDuelState 0029, CvF 0027, FvF 0028 | handled | |
| Buffs and progression | BuffCreated 003E, BuffRemoved 003F, BuffUpdated 0040, BuffLearned 007C, SkillsReset 007D, AddSwapPassiveBuffs 0087, LevelChanged 007E | handled | World gates buff bodies with `AAEMU_WZ_BUFF`. |
| Skill, plot, damage | SkillStarted 002B, SkillFired 002C, SkillEnded 002D, SkillStopped 002E, CastingStopped 002F, UnitDamaged 0030, UnitHealed 0031, PlotEvent 003A, PlotEnded 003B, PlotCastingStopped 003C, PlotChannelingStopped 003D, SkillCooldownReset 0039, SkillCooldownReduce 0086, ChangeChargeSkillCooldown 0088, CreateSkillController 0011, SkillControllerState 0010, OnUnitBlinked 0023, BlinkUnit 0032, KnockBackUnit 0033, ImpulseUnit 0034, UnitInvisible 0035, AttackFaction 0089, ZombieCharacter 0082 | **none handled** | Some are gated by `AAEMU_WZ_SKILL_FIRED`, `AAEMU_WZ_UNIT_DAMAGED`, `AAEMU_WZ_UNIT_HEALED`, `AAEMU_WZ_UNIT_POINTS`. |
| Faction | FactionCreated 0015, SponsorChanged 0016, Dismissed 0017, SetRelationState 0018, ExpeditionWarState 001B, FactionIndependence 001C | handled | |
| Slaves and vehicles | UnitAttached 000A, UnitDetached 000B, UnitHung 000C, UnitUnhung 000D, UnitBond 000E, UnitUnbond 000F, SlaveMasterChanged 004A, ShipControlChange 004B, EscapeSlave 004C, UnitFlyingStateChanged 0036, RequestUnitFlyingState 0037 | **none handled** | JoinResponse sets fset bit 177 (server-side ship sim), which implies the zone should simulate ships. |
| World objects | HouseState 0006, HouseBuildDone 004D, HouseBuildProgress 004E, CreateDoodad 0073, RemoveDoodad 0074, DoodadChangePhase 0075, VegetationCutdown 0076, GimmickCreated 005A, GimmickRemoved 005B, GimmickMovement 005C, GimmickReloadStatics 0059, GimmickGrasped 005D, GimmickSetMovement 005E, PhysicalExplosion 005F, DropBackpack 007F | **none handled** | |
| Siege, conflict, tower defence | DominionData 0060, DominionDeleted 0061, SiegeState 0062, SiegeMember 0063, SiegeSecondHalf 0064, ConflictZoneState 0084, GameSchedule 006A-006C, TowerDef 0065-0069, GameActivityStart/End 006D/006E, FactionImmigrateNpcSpawnerCondition 008A | Dominion*, Siege*, ConflictZoneState, GameSchedule handled | TowerDef, GameActivity and 008A not handled. |
| Ops and quest | GmCommand 004F, RunCommand 0001, RequestCombatUnits 0071, RequestZoneStatus 0077, PhysicsCPU 0078, RayCasting 007A, AiDebug 007B, CheckMoleMiner 0080, CheckMoleTrader 0081, Sandbox* 0056-0058, AttackOnQuest 0050, FollowUnitOnQuest 0051, FollowPathOnQuest 0052, RunCommandSetOnQuest 0053 | only GmCommand | |

The replacement's default branch (`src/AAEmu.Zone/WorldLink/ZoneSession.cs:535-545`) counts the opcode in `UnimplementedOpcodes` and keeps serving. It does not close the connection.

### ZW opcodes World HANDLES

| ZW op | World value and handling (file:line) | Replacement value and sender |
|---|---|---|
| Join 0000 | `ZoneProtocolHandler.cs:122-126,184-270` | same value; `ZoneJoin.ToFrame` |
| UnitMovements 0008 | `MovementRelay.RelayZoneMoveToClient` (`MovementRelay.cs:582-836`): `i32 count; entries {Bc, u8 type, UnitMoveType body}`. A malformed entry drops the rest of the batch. A local→world shift is applied only if `ZoneCoordBoundary.UseLocalOnZoneWire` or the NPC is `ZoneSimUsesLocalCoordinates` (`MovementRelay.cs:642-644`). The SC split is 400 units. | same value; `ZoneMovementOutgoing.cs:268`, designed against World's parse, max 1000 entries per frame |
| Heartbeat 003C | log only (`ZoneProtocolHandler.cs:131-133`) | same value; **no sender** |
| ZoneLoaded 001F | state transition and flushes (`ZoneProtocolHandler.cs:134-159`) | same value; `ZoneSceneCoordinator.cs:181` |
| SpawnNpc 0002 | `NpcSpawnRelay.OnSpawn` with `ZwSpawnNpcParser` (`Relay/ZwSpawnNpcParser.cs:36-102`): first 41 bytes `u32 spawnerId, u32 spawnerType, u8 member, u8 part, u16 table, u32 template, u32 groupType, u32 groupId, u8 groupMember, f32 x, y, z, zRot, scale`, total length 41..512. World replies with `WZNpcState` 0002. Positions are zone-local. | same prefix in `NpcSpawnRequest.WriteBody` (`src/AAEmu.Zone.Protocol/Messages/NpcSpawnMessages.cs`), then creator, name, master, faction, reason and flags. A long name (up to 1024) can exceed World's 512 cap (WC-10). |
| RemoveNpc 0003 | `ZwRemoveNpcParser`: exactly 7 bytes, `Bc + i32 delay` | same layout (`NpcNotifications.cs:6-15`) |
| TimeOfDay 001D, Detailed 001E | `ZoneSimRelay.HandleTimeOfDay` (`ZoneSimRelay.cs:813-834`), which calls `OnZoneTimeOfDay(zone, iid, time, speed, start, end, detailed)` | same layout; `ToWorldFrame` |
| CreateBuff 000A, RemoveBuff 000B, MakeAggroTargetHostile 000D | raw hex in `CombatRelay.cs:25-41,65-112` (not in World's `ZwOpcodes.cs`) | same values; `Messages/Buff*Request.cs`, `NpcNotifications.cs` |
| SkillControllerState 0009, ClearCombat 000C, StartSkill 0018, StopSkill 0019, StopCasting 001A, ResponseCombatUnits 002D, KillNpc 0031 | raw hex only; relayed to SC (`CombatRelay.cs:101-112`) | **absent from the replacement enum and no sender** |
| AiAggro 0034, AggroRemove 0041 | in World's enum; `CombatRelay` | **absent from the replacement enum** (it ends at 0x3F) |
| SaveInstanceSpawner 0005 | **World has no handler**; it falls to the default log (`ZoneProtocolHandler.cs:178-179`) | constant and message exist (`src/AAEmu.Zone.Protocol/Messages/SaveInstanceSpawner.cs`). Saved indun spawner state would never persist. |
| NpcSaid 0006, ChangeTarget 0017, NpcInteractionEnded 0014, NpcInteractionStatusChanged 002C, ErrorMsgToPlayer 0013, UnitModelPostureChanged 000F | `ZoneSimRelay.cs:38-77` | message classes for the Npc* ones in `Messages/Npc*.cs`; ErrorMsgToPlayer and UnitModelPostureChanged have no sender |
| NpcAbusers 003D/003E/003F | `ZoneSimRelay.cs:133-190` | `NpcNotifications.cs` |
| Areas, gimmicks, house, mole, hack, backpack, raycast, tower def, fall, collision | EnterArea 0023, LeaveArea 0024, Gimmick* 0025-002A, RayCastingResult 0030, TowerDefReportPlayability 0032, BackpackDropped 0036, ReportMole* 0037/0038, ReportMoveHack 0039, ReportDoodadSurfaceHack 003A, DoodadCollideUnit 003B, UnitFell 0010, UnitCollision 0011/0012, RemoveHouse 0007 (`ZoneSimRelay.cs:38-77`). UnitLocation 0035 has no World handler. | constants exist, no senders. `RequestStaticGimmickSpawns` is a native provider (`ZoneSceneCoordinator.cs:40,127`). |

Body layouts: no mismatches found in those both sides serialize (join, response, faction, relation, spawner, ToD, WorldGameTime, SpawnNpc prefix, RemoveNpc, UnitMovements framing). Not verified byte for byte: WZUnitState, WZNpcState, WZBuff* and ZW Create/RemoveBuff.

---

## 4. Player entry path

1. The game server calls `WorldIntegration.TryEnterZone(bcId, body)`, which maps to `PlayerEnterService.EnterZone` (`Program.cs (pre-restructure):68`; `Core/Relay/PlayerEnterService.cs:24-49`).
2. **Zone selection.** `ForCharacter(bcId)` resolves the zone key from `ActiveChar.Transform.ZoneId`, otherwise from position and world template. A boat parent uses `ZoneAnnouncedTo`. The instance id is `ParentWorld.Id`. The lookup tries `(zoneKey, instanceId)` with ZoneLoaded, then the unique ZoneLoaded host for the key (`PlayerEnterService.cs:182-205,260-270,350-420`). With no host the enter is refused. `AAEMU_ZONE_PRIMARY_FALLBACK=1` falls back to any loaded zone.
3. **What World sends, in order** (`PlayerEnterService.cs:89-115,148-182`). The body is rebuilt from the live Character via `WorldIntegration.BuildWzUnitStateBody(character, out buffs)`.
   - `WZUnitRemoved(bcId)` first, if the zone already tracks the unit, plus `ZoneBuffRegistry.ClearUnit`.
   - `WZUnitState` 0x0007.
   - `WZUnitFactionChanged(objId, 0, factionId, false)`, only if the faction id is non-zero.
   - `WZActivateNpcSpawnersInArea(local xyz of the player, r=1024)`, when `NpcSpawnerActivate.Enabled`.
   - `WZUnitExpeditionChanged`, only if the character has an expedition.
   - Buff snapshot bookkeeping (`MarkSnapshot`); ended buffs are retired via `RelayBuffRemovedToZone`.
4. **Handoff between zones.** `HandoffOnZoneChange` sends `WZUnitRemoved` to the old host and repeats the enter sequence on the new host. It fails closed if the new zone is not ZoneLoaded (`PlayerEnterService.cs:62-101`). Leaving sends `WZUnitRemoved` (`PlayerEnterService.cs:51-60`).
5. World gets no ack for an enter, so an admission failure in the zone is invisible to World. The replacement counts refusals (`ZoneSession.cs:430-455`) and logs them to its console only.
6. **Client to zone movement.** World relays the client's move as `WZUnitMovement` 0x0009: `Bc + u8 moveType + move body` (`MovementRelay.RelayClientMoveToZone`, `MovementRelay.cs:548-580`; `Packets/Wz/WZUnitMovementPacket.cs`). With `UseLocalOnZoneWire=false` (the default) the coordinates are not shifted, so the body is in continent (world) space.
7. **Zone to client movement.** The zone sends `ZWUnitMovements` 0x0008. `RelayZoneMoveToClient` converts and fans out to SC (`MovementRelay.cs:582-836`).
8. **Leave.** `OnPlayerLeave` leads to `LeaveZone` and `WZUnitRemoved`.

**What the replacement must do for spawn and move**
- Handle `WZUnitState` for a Character. `ZoneSession.cs:414-455` decodes it, admits it through `Registry.Create` and `Frame.RegisterAdmittedPlayer`, and counts refusals instead of dying.
- That admission depends on the native character-spawn path (`ZonePlayerActorFactory.cs`, `ZoneNativeEntityServices.cs`). The comments at `Program.cs (pre-restructure):541-545` and `ZoneSession.cs:430-455` describe an IGame+0xE8 callback that had killed the process, now contained as a per-player refusal. This is a replacement-internal risk for first entry, not a contract issue.
- Handle `WZUnitMovement` for the unit (`ZoneSession.cs:387-413`).
- Emit `ZWUnitMovements` in the frame World expects (WC-06).
- Handle `WZUnitFactionChanged`, equipment and buffs (all handled).
- Handle `WZActivateNpcSpawnersInArea` or populate NPCs independently (WC-08).

---

## 5. World-side assumptions that bind it to the native zone

| Assumption | Evidence | Impact on replacement |
|---|---|---|
| `ZoneHost.NativeDll` must exist | `ZoneHostSupervisor.cs:225-229`; `ZoneHostCommandLine.cs:38-39` | WC-03: relax in World or supply a placeholder file. |
| Native cvar syntax (`+name value`) and `-dedicated` | `ZoneHostCommandLine.cs:41-66` | WC-01. |
| Zone identity is the World template name (`+zone`, `+sv_map`), and the native derives the key from content | `ZoneHostSupervisor.cs:212,252` | WC-02: the replacement needs template name to key resolution. |
| Instance copy identity is `ZWJoin.iid == world.Id` | `ZoneProtocolHandler.cs:195-197`; `Core/Zone/ZoneInstanceKey.cs`; `DungeonLoaderTask.cs:139-145` | WC-02. |
| Env overlays `AAEMU_ZONE_DLL`, `AAEMU_ZONE_SAVE_DIR`, `AAEMU_ZONE_LOG_NAME` | `ZoneHostCommandLine.cs:68-73` | The only per-launch channel besides cvars (WC-12). |
| World's own env is inherited by the child | `ZoneHostWin32.cs:32-58` | `AAEMU_ZONE_GAME_DATA_ROOT` (set from `ZoneGameDataRoot`, `Program.cs (pre-restructure):60-64`) and every `AAEMU_WZ_*` / `AAEMU_ZW_*` switch reach the child. |
| `ZoneGameDataRoot` is an extracted game-data tree | `Core/Relay/ZoneGameDataRootResolver.cs`; used by `ZoneNpcSpawnerCatalog`, `ZoneSpawnerPlacementCatalog`, `ZoneDoodadPlacementCatalog`, `ZoneTimeOfDayCatalog` | World reads `worlds/<name>/zone_server/npc_spawners.g`, `cells/*/doodad.g` and `time_of_day.xml` itself. There is no log parsing. The replacement's assets and spawner file should point at the same data (WC-15). |
| Zone-local coordinates on some zone-wire messages | `ZoneCoordBoundary.cs:24-60,94-113`; `Program.cs (pre-restructure):1547-1551` | WC-06. |
| `WZActivateNpcSpawnersInArea` is the NPC population trigger | `ZoneProtocolHandler.cs:152-158,321-336`; `PlayerEnterService.cs:132-153` | WC-08. |
| fset bits 60 and 177 enable physics damage and server-side ship sim | `WZJoinResponsePacket.cs:23-37` | The replacement parses features (`HasFeature`) but World cannot tell whether it honours them. |
| No log parsing and no stdout capture | `ZoneHostWin32.cs:62-90` | none |
| Windows-only launcher (`DETACHED_PROCESS`, P/Invoke `CreateProcessW`) | `ZoneHostWin32.cs` | Fine on Windows only. |
| Process lifetime: a 300 ms early-exit check and `Kill(entireProcessTree)` | `ZoneHostSupervisor.cs:254-258,391-392` | WC-07, WC-16. |

---

## Tasks

### WC-01: Accept the native `-dedicated +cvar value` command line in the replacement host
- Status: **Done** — `ZoneServerLaunch` parses `-dedicated +cvar value` (last value wins, unconsumed CVars logged); tests built from `ZoneHostCommandLine.Build` output
- Priority: P0
- Evidence: W `Core/ZoneHost/ZoneHostCommandLine.cs:41-66`; Z `Program.cs (pre-restructure):674-690` (`Unknown option` on any other token), `Program.cs (pre-restructure):12-48` (modes)
- Repo state: Absent. `-dedicated`, `+zone`, `+sv_map`, `+instance`, `+sv_port`, `+world_ip`, `+db_location` and the rest abort startup with `Zone failed: Unknown option ...` (exit 1).
- Work:
  - Zone repo: add a World-launch mode selected by `-dedicated` or `+zone` that implies `--host` and tolerates unknown `+cvar`s (log and ignore `e_render`, `r_Driver`, `npc_*`, `ai_systemupdate`, `sv_port`).
  - Zone repo: parse `+world_ip`/`+world_port` (or the `world_serveraddr`/`world_serverport` duplicates), `+zone`, `+sv_map`, `+instance` and `+db_location`, letting them override the json. Today `--config` forbids overriding them (`Program.cs (pre-restructure):70-75`).
  - Keep later-wins semantics, because `ExtraArguments` can repeat a cvar (test `Build_CustomAiSchedulingAndExtraArguments_AppearInOverrideOrder`).
  - Keep `--config` for engine, asset and log paths. Add a parser test fed from `ZoneHostCommandLine.Build` output.
- Confidence: high

### WC-02: Join with the `+instance` id and resolve the zone key from the `+zone` template name
- Status: **Partial** — ZWJoin carries `+instance`/`+sv_port`; template-name → content zone mapping still unverified against World
- Priority: P0
- Evidence: W `ZoneProtocolHandler.cs:195-201`, `Core/Zone/ZoneConnectionRegistry.cs:158-191`, `DungeonLoaderTask.cs:113,139-145`, `ZoneHostSupervisor.cs:212,252`; Z `Program.cs (pre-restructure):159-160`, `src/AAEmu.Zone/Hosting/ZoneRuntimeConfiguration.cs:128-134`
- Repo state: Partial. The ZWJoin wire is right, but `iid` is hard-wired to 0 and the zone comes from a json `zoneName`/`zoneKey` pair.
- Work:
  - Zone repo: set `ZoneJoin.InstanceId` from `+instance`. It must equal `world.Id`, or the dungeon load times out. Two copies must register as distinct `(zoneKey, iid)`.
  - Zone repo: resolve `+zone <World template name>` to the content zone name and key. Check that `world.Template.ZoneKeys[0]` in World equals what `ZoneContentDatabase.ResolveZone` returns for that template name. An explicit name-to-key table in the replacement config may be needed.
  - ZWJoin `ip`, `port` and `accountId` are unused by World and can stay 0, with `dev=false`.
  - Add a test that two joins with iid 7 and 8 co-exist under the registry rules (`ZoneConnectionRegistryTests`).
- Confidence: high for iid, medium for the name-to-key mapping

### WC-03: Make World's launch preflight accept a non-native host
- Status: **Partial** — zone side ships `AAEmu.ZoneServer.exe`; World still requires `ZoneHost.NativeDll` to exist (World change or placeholder)
- Priority: P0
- Evidence: W `ZoneHostSupervisor.cs:219-235`, `ZoneHostCommandLine.cs:34-39,68-73`, `Models/WorldAppConfiguration.cs:75-101`, `Config.Local.json.example`; Z (no native zone DLL, deliverable is `AAEmu.Zone.dll`)
- Repo state: World hard-requires `Executable`, `NativeDll` and `WorkingDirectory` to exist and always sets `AAEMU_ZONE_DLL`.
- Work:
  - Zone repo: publish an apphost `AAEmu.ZoneHost.exe` (or thin launcher) that wraps `AAEmu.Zone`. This satisfies `Executable`.
  - World-side change (preferred): add `ZoneHost.Kind` (`native` or `managed`) so `NativeDll` is optional and `AAEMU_ZONE_DLL` is skipped when managed.
  - No World change: point `NativeDll` at any existing placeholder file. The replacement ignores `AAEMU_ZONE_DLL`.
  - Document the `Config.Local.json` values (`Executable`, `WorkingDirectory`, `NativeDll`, `RuntimeLogRoot`, `ReadyTimeoutSeconds`) for a replacement run.
- Confidence: high

### WC-04: Derive the runtime configuration from launch arguments and env instead of a json plus spawner-file per zone
- Status: **Partial** — base config from `--config`/`+zone_config`/`AAEMU_ZONE_CONFIG`/`zone.json`, launch overlay via `WithLaunch`, `npc_spawners.g` from assets; world map still fixed by `worldMapAsset`
- Priority: P0
- Evidence: W `ZoneHostCommandLine.cs:45-53,68-73`; Z `Program.cs (pre-restructure):54-63` (`--host` requires `--config` and `--spawner-file`), `src/AAEmu.Zone/Hosting/ZoneRuntimeConfiguration.cs:43-100` (required fields, unmapped members disallowed), `src/AAEmu.ZoneServer/zone.example.json`
- Repo state: Absent. World passes no config path. The json carries one `zoneName`/`zoneKey` and a fixed `worldHost`/`worldPort`, and `--host` demands `--spawner-file` for that one zone.
- Work:
  - Zone repo: allow a base runtime json (found through an env var World inherits, e.g. a new `AAEMU_ZONE_RUNTIME_CONFIG`, or next to the executable) whose zone-specific fields (`zoneName`, `zoneKey`, `worldHost`, `worldPort`, `logDirectory`) are overlaid from `+zone`, `+world_ip/+world_port` and `AAEMU_ZONE_SAVE_DIR`.
  - Zone repo: derive the spawner file from the asset source (`worlds/<world>/zone_server/npc_spawners.g`, the same path World resolves via `ZoneGameDataRootResolver.TryResolveNpcSpawnersFile`) instead of requiring `--spawner-file`.
  - Zone repo: resolve world map asset and layout from the zone name rather than a fixed `worldMapAsset`/`worldName`.
  - World-side alternative: `ZoneHost.ExtraArguments` can carry `--config path`, but it is the same for every zone.
- Confidence: high

### WC-05: Provide a launcher for static (continent) zones
- Status: **Done** — zone repo owns it: `AAEmu.ZoneServer --zones a,b` starts one host per map with `+instance 0`, restarts with backoff
- Priority: P0
- Evidence: W `ZoneHostSupervisor.cs:202-212` (`TryStart` is instance-only), `Models/WorldAppConfiguration.cs` (`SvPortBase` doc: continent hosts keep the Zone Manager default), `Program.cs (pre-restructure):79-90,242-274`, `PlayerEnterService.cs:25-33,182-205`; Z `Program.cs (pre-restructure):155-162`
- Repo state: Absent on both sides. World never launches static zones, and the Zone Manager that does is in neither repo.
- Work:
  - Decide the owner. Either add a World-side `ZoneHost.StaticZones: [{ WorldTemplateName, ZoneKey }]` list started at `OnMainWorldReady` with `+instance 0` (World change), or ship a launcher in the zone repo that starts one `AAEmu.Zone` per continent zone key against World's zone port.
  - Each static host must join with `iid=0` and a unique zone key. Two hosts with the same `(zoneKey, 0)` replace each other in the registry (`ZoneConnectionRegistry.cs:158-178`).
  - Add a restart or watch policy. Today a dropped static zone sends its clients to character select and nothing restarts it.
  - First player entry needs at least the starter-zone host running.
- Confidence: medium (depends on how the user's Zone Manager works today)

### WC-06: Align coordinate frames and the movement flag with World's default (continent on the wire)
- Priority: P0
- Evidence: W `AAEmu.Game/Core/Managers/World/ZoneCoordBoundary.cs:24-60,94-113`, `MovementRelay.cs:548-580,642-644`, `AAEmu.Game/WorldIntegration.cs:882-934`, `ZoneProtocolHandler.cs:321-336`, `ZwSpawnNpcParser.cs:55-60`; Z `src/AAEmu.Zone/Hosting/ZoneSceneHost.cs:299` (`localOnWire: true`), `src/AAEmu.Zone/Actors/ZoneMovementOutgoing.cs:20-88,117`, `src/AAEmu.Zone/Actors/IZoneMovementFramePhases.cs:80-95`
- Repo state: Partial and ambiguous. The replacement's own doc says the model pose is in the WZUnitState world frame, yet it passes `localOnWire: true` and says output is "in the same local/world convention World's relay decodes".
- Work:
  - Zone repo: confirm in a captured session which frame Characters' ZW moves use. With World's default (`AAEMU_WZ_UNIT_POS_LOCAL` unset), World does not shift ZW moves for Characters, so they must be continent coordinates. NPCs get shifted only if World marked them `ZoneSimUsesLocalCoordinates`, which happens only when the local wire policy is on.
  - Keep ZWSpawnNpc positions and the circle in `ActivateNpcSpawnersInArea` zone-local. World converts continent to local for the circle and treats spawn coordinates as local.
  - Make `localOnWire` follow the inherited `AAEMU_WZ_UNIT_POS_LOCAL` / `AAEMU_WZ_UNIT_POS_WORLD` env instead of a constant.
  - Add a golden test: encode a pose with `ZoneMovementOutgoing`, parse it with World's `MoveType.Read`, and check the frame.
- Confidence: medium

### WC-07: Detect early host death and size ReadyTimeout for the replacement
- Priority: P1
- Evidence: W `ZoneHostSupervisor.cs:254-258`, `DungeonLoaderTask.cs:44-47,111-132`, `Program.cs (pre-restructure):99-100`, `Models/WorldAppConfiguration.cs:100`; Z `ZoneSceneCoordinator.cs:127-183`
- Repo state: World only checks `WaitForExit(300)`. A crash in the following seconds (bad args, native engine init, missing asset) leaves players waiting the full `ReadyTimeoutSeconds` (120).
- Work:
  - World-side change: in `WaitForZoneHost`, abort as soon as the tracked process exits (the supervisor already holds the handle and `HasExited`). Include the host log directory (`AAEMU_ZONE_SAVE_DIR`) in the error.
  - Measure the replacement's real load time with the native engine and cells, then raise `ZoneHost.ReadyTimeoutSeconds` or `AAEMU_ZONE_HOST_READY_SECONDS`.
  - Verify the warm pool works: `WaitForZoneHostReady` and pre-spawn while the replacement idles with no players.
- Confidence: high for the World observation, low for the load-time figure

### WC-08: Handle `WZActivateNpcSpawnersInArea` (or prove NPCs spawn without it)
- Priority: P1
- Evidence: W `Core/Packets/Wz/WZActivateNpcSpawnersInAreaPacket.cs`, `ZoneProtocolHandler.cs:152-158,272-336`, `PlayerEnterService.cs:132-153`, `Config.json:22-29`; Z `Protocol/WzOpcodes.cs:34` (constant only), `ZoneSession.cs:535` (default branch)
- Repo state: Absent. World sends it on first player entry (and optionally on ZoneLoaded). The replacement counts it as unimplemented.
- Work:
  - Zone repo: add the message reader (activate flag, center xyz in zone-local coordinates, radius; see the packet class) and apply it to `ZoneNpcSpawnerRegistry`.
  - If the replacement's spawners are always active, document that and add a deliberate no-op case.
  - Verify the ZWSpawnNpc then WZNpcState reply loop end to end, since that is how World mirrors NPCs to clients.
- Confidence: medium (the replacement may activate spawners on its own)

### WC-09: Cover the World→Zone opcodes that are currently ignored
- Priority: P1
- Evidence: W `Core/Packets/Wz/*.cs` (list in §3), `ZoneProtocolHandler.cs:142-158` (post-ZoneLoaded flushes); Z `src/AAEmu.Zone/WorldLink/ZoneSession.cs:535-545`
- Repo state: Partial. About 60 World opcodes land in the default counter, including CreateDoodad, RemoveDoodad, DoodadChangePhase, HouseState, HouseBuild*, Gimmick*, NpcStartDespawn, UnitAttached/Detached/Hung/Bond, UnitFlyingState, skill/plot/damage/heal, Impulse/Knockback, TowerDef, GameActivity, quest and sandbox opcodes.
- Work:
  - Zone repo: rank by player impact. Doodads, gimmicks, houses and NpcStartDespawn come first because World flushes them right after ZoneLoaded. Skill, damage and heal come next, then ships (fset bit 177 promises a server-side ship sim).
  - Zone repo: add message readers to `AAEmu.Zone.Protocol/Messages` (World's writers are in `Core/Packets/Wz`) and dispatch them in `ZoneSession.ReceiveCore`.
  - For anything deliberately ignored, add an explicit no-op case so the counter shows only unknown packets.
  - World-side option: clear the fset bits with the existing env kill switches (`AAEMU_WZ_FSET_PHYSICS_DAMAGE=0`, `AAEMU_WZ_FSET_SERVER_SIM=0`) until the replacement honours them.
- Confidence: high

### WC-10: Fill the ZW opcode gaps and sender coverage
- Priority: P1
- Evidence: W `Core/Relay/CombatRelay.cs:25-41,65-112`, `ZoneSimRelay.cs:38-77`, `Core/Packets/Zw/ZwOpcodes.cs`; Z `src/AAEmu.Zone.Protocol/ZwOpcodes.cs` (ends at 0x3F)
- Repo state: Partial. The replacement enum lacks 0x09, 0x0C, 0x18, 0x19, 0x1A, 0x2D, 0x31 (World has them only as raw hex in `CombatRelay`), AiAggro 0x34 and AggroRemove 0x41. It has no sender for skill start/stop/cast, KillNpc, ClearCombat, SkillControllerState, area enter/leave, gimmick, backpack, hack/mole reports or heartbeat. Its `SaveInstanceSpawner` 0x05 has no World handler.
- Work:
  - Zone repo: add the missing constants with World's values. Build senders as the corresponding simulation lands (combat, areas, gimmicks).
  - World-side: add a ZW 0x05 handler that persists indun spawner state (which feeds `WZSpawnerList` on the next join), or accept that saved spawner state is lost.
  - Check ZWSpawnNpc body length against `ZwSpawnNpcParser` (`MinBodyLength=41`, soft cap 512). A name near the 1024 limit would exceed it.
- Confidence: medium

### WC-11: Golden-test WZUnitState, WZNpcState and buff bodies against World's serializer
- Priority: P1
- Evidence: W `AAEmu.Game/Core/Packets/G2C/SCUnitStatePacket.cs:36-41`, `UnitState/UnitStateWireSerializer.cs:29-39`, `WorldIntegration.cs:882-896,1284-1326`, `AAEmu.UnitTests/WorldServer/ZonePacketWireTests.cs`, `AAEmu.UnitTests/Game/**/UnitStateBuffSnapshotTests.cs`; Z `src/AAEmu.Zone.Protocol/Messages/UnitStateSnapshot.cs`, `src/AAEmu.Zone.Protocol/Messages/NpcSpawnMessages.cs` (`NpcStateSnapshot.Read`)
- Repo state: Unverified. The reader is detailed and strict (`EnsureFullyConsumed`), but the only proof is the replacement's own tests.
- Work:
  - Zone repo: capture bytes from World's serializer (fixtures dumped from `BuildWzUnitStateBody` for a Character, an NPC and a hull) and feed them to `UnitStateSnapshot.Read` and `NpcStateSnapshot.Read`.
  - Pay particular attention to the flag-conditioned tails (first hitter, high abilities, GM modes), the Housing heading and the Character-only block.
  - World-side (optional): export the fixtures from `AAEmu.UnitTests` so both repos share them.
- Confidence: medium

### WC-12: Log to a file under `AAEMU_ZONE_SAVE_DIR` / `AAEMU_ZONE_LOG_NAME` and cope with no console
- Priority: P1
- Evidence: W `ZoneHostCommandLine.cs:68-73`, `ZoneHostSupervisor.cs:246-258`, `ZoneHostWin32.cs:9-17,62-90` (DETACHED_PROCESS, no stdio); Z `Program.cs (pre-restructure):77-78` (`ZoneConsoleLog(configuration.LogDirectory)`), all other diagnostics are `Console.WriteLine`
- Repo state: Partial. The log directory comes from the json and may differ from the one World reports.
- Work:
  - Zone repo: when launched by World, prefer `AAEMU_ZONE_SAVE_DIR` and `AAEMU_ZONE_LOG_NAME` for the log file and mirror `Console` output to it. A detached process has no console to read.
  - Make sure `Console.Out` writes do not throw with no console.
  - Print the zone key and instance id on the first log line.
- Confidence: medium

### WC-13: Tolerate interleaved WZ frames during startup
- Priority: P2
- Evidence: W `PlayerEnterService.cs:226-232` (`JoinedZones`: buff relays target `State >= Joined`), `ZoneProtocolHandler.cs:259`; Z `src/AAEmu.Zone/WorldLink/ZoneStartupState.cs:122-123`
- Repo state: Exists but brittle. Any WZ opcode other than the seven startup ones throws `NotSupportedException` and aborts the join.
- Work:
  - Zone repo: queue unrecognised frames received before the gate closes and replay them after `ZoneLoaded`, instead of throwing.
  - World-side alternative: send relays that need not reach a merely Joined host only to ZoneLoaded connections.
- Confidence: medium

### WC-14: Send the ZW heartbeat
- Priority: P2
- Evidence: W `ZoneProtocolHandler.cs:131-133`, `Packets/Zw/ZWHeartbeatPacket.cs`; Z `src/AAEmu.Zone.Protocol/Messages/ZoneHeartbeat.cs`, `src/AAEmu.Zone/Scene/ZoneSceneProviders.cs:967`
- Repo state: Message exists, no sender. World neither requires nor answers it, and there is no timeout in `AAEmu.Commons/Network/Core`.
- Work:
  - Zone repo: send `ZoneHeartbeat` periodically (the native `0x39332510` path) so World logs show liveness.
  - Do not rely on `reqGameTime`. World never answers a heartbeat with `WZWorldGameTime`, so the replacement's game clock only resyncs from the join-time packet.
  - World-side (optional): add a liveness timer if heartbeats are sent.
- Confidence: high

### WC-15: Point the replacement at the same game data as `ZoneGameDataRoot`
- Priority: P2
- Evidence: W `Core/Relay/ZoneGameDataRootResolver.cs`, `Program.cs (pre-restructure):60-64` (config to env `AAEMU_ZONE_GAME_DATA_ROOT`), `Core/Relay/ZoneNpcSpawnerCatalog.cs:175,270-278`, `Core/Relay/ZoneTimeOfDayCatalog.cs:86-131`; Z `src/AAEmu.Zone/Hosting/ZoneConfiguredAssets.cs`, `src/AAEmu.Zone/Hosting/ZoneRuntimeConfiguration.cs` (`assetSources`)
- Repo state: Partial. Both sides read `worlds/<name>/...` files (World: `npc_spawners.g`, `doodad.g`, `time_of_day.xml`; replacement: assets, spawner file, world map). Nothing checks that they are the same tree.
- Work:
  - Zone repo: honour `AAEMU_ZONE_GAME_DATA_ROOT` (already in the child's env) as a directory asset source when `assetSources` omits it.
  - Document which files the two servers must agree on (persistent spawner ids sent in `WZSpawnerList`, the ToD seed).
- Confidence: medium

### WC-16: Define shutdown and orphan behaviour
- Priority: P2
- Evidence: W `ZoneHostSupervisor.cs:389-403,547-558`, `Program.cs (pre-restructure):1554-1580,1676-1677`, `Dungeon.cs:577,617`; Z `Program.cs (pre-restructure):124-146`, `src/AAEmu.Zone/WorldLink/ZoneSession.cs` (`ReceiveAsync` ends on EOF)
- Repo state: Partial. World kills the tree without notice and leaves detached hosts running when it exits itself. The replacement exits on World EOF, so orphans clear when the socket closes.
- Work:
  - Zone repo: treat connect failure or EOF as a clean exit with a distinct code, and do not retry in a tight loop.
  - Verify that `KillTree` leaves no stale native engine state (temp files, locks) that breaks the next launch with the same log dir name.
  - World-side (optional): stop all tracked hosts on World shutdown.
- Confidence: medium
