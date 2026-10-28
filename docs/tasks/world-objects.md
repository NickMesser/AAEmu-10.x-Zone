<!-- Audit of world-objects: native x2game-dev_dedicate.dll (ArcheAge 10.0.2.13 r575) vs this repo at 76e439f. Paths rewritten to the src/ layout; line numbers refer to 76e439f. -->

## World objects: doodads, gimmicks, housing, slaves/vehicles/ships, attachments

Coverage is near zero for runtime objects. The repo has only (a) wire opcode constants for the WZ/ZW packets (`src/AAEmu.Zone.Protocol/WzOpcodes.cs`, `ZwOpcodes.cs`), (b) a movement layer that carries GroundGimmickId/GroundDoodadId/HangingObjectId but whose lookups are stubs returning null (`ZoneActorMovementInputServices.cs:617,620`, `ZoneActorMovementCorrection.cs:443-452`), (c) model-kind enums including Vehicle/Ship (`src/AAEmu.Zone/Content/ZoneModelContent.cs:7`) and ship-leaf stubs (`ZoneSceneProviders.cs:790-794`), and (d) a static-gimmick request step that logs "0 rows" (`ZoneSceneLoadingServices.cs:166-175`). There is no server-side gimmick/doodad/housing/slave/ship/transfer object model, no WZ handlers for them, and no ZW senders. For the first-entry milestone (zone 129) the minimum is: static level doodads/gimmick entities as collidable cell objects (cell-object pass still "DeferredBlockingWork" per TODO.md), the ServerOnly elevator FlowGraph (Start, Movement:MoveEntityTo, Time:Delay, Logic:Any; CryAction built-ins, TODO.md L96), and tolerant handling of ground-object ids. Housing, vehicles/ships, mounts and transfers are explicitly parked in TODO.md ("Park until first entry works").

Notes: native RTTI has no server-side Doodad/Slave/Transfer manager classes in the Zone DLL (GameClient:: names are shared compiled-in code, plus `IZoneDoodadModel@GameServer` 0x3A527F30). Server-side owners are `ServerGimmick`, `ServerGimmickManager`, `Housing`, `HousingManager`, `ServerUnit`, and `Server{Ship,Vehicle}SimulationController`. Doodads on the zone side are driven by World packets (WZCreateDoodad/RemoveDoodad/ChangePhase); Doodad* DB tables are loaded (DoodadFuncZoneBuff/Overlap/React).

| Native item | Native evidence | Repo status |
|---|---|---|
| ServerGimmick (static/dynamic entity, joints, grasp, break) | RTTI 0x3A518B58; sub_39335AB0 (InitStaticEntity: "no physics on static gimmick", "guid is 0"); sub_393370E0 (UpdateGraspedPosition) | absent |
| ServerGimmickManager Create/Created/Removed/Movement/Grasped/SetMovement | sub_393392C0 (CreateGimmick), sub_39339400 (HandleGimmickCreated), sub_39339AD0 (HandleGimmickMovement), sub_39338540 (HandleGimmickGrasped); log "[Server Gimmick] created gimmick. id(%u), type(%u), guid(%llu), hasControl(%s)" | absent (opcodes only: WzOpcodes.cs:43-45,106-108) |
| ZWRequestSpawnStaticGimmick at level load | sub_393365A0 | stub: ZoneSceneLoadingServices.cs:166-175; ZoneSceneCoordinator.cs:40,171 |
| ZW gimmick packets (JointBroken, ResetJoints, RemoveParts, Collided, Movement) | RTTI ZWGimmick*Packet 0x3A518A00-0x3A518AE0 | absent (no senders) |
| Doodad create/remove/phase from World | RTTI WZCreateDoodad/RemoveDoodad/DoodadChangePhase Packet (0x3A51A6D0, 0x3A51A700) | opcodes only WzOpcodes.cs:47-49 |
| ZWReportDoodadSurfaceHack / ZWDoodadCollideUnit | RTTI ZW...Packet; vftable refs at .c lines 917190, 920210 | opcodes only ZwOpcodes.cs:46,48 |
| IZoneDoodadModel, doodad models | RTTI 0x3A527F30 (server), GameClient::*DoodadModel 0x3A579760-0x3A5798B8 | absent; static prefab/vegetation resources partially in src/AAEmu.Zone/Scene/ZoneCellObjectResources.cs, ZoneCellVegetationResources.cs |
| Doodad funcs/phases | strings 0x3A13A780-0x3A13A9A0; LoadDoodadFunc*Descs loaders (0x3A139760, 0x3A139830, 0x3A168940) | absent (only WorldContentKind.DoodadAlmighty, ZoneWorldContent.cs:12) |
| ClimbDoodadInfo / LeaveClimbDoodad / GetClimbInfo | RTTI 0x3A528A38; sub_3935D5A0; sub_3926A9A0 ("GetClimbInfoFromDoodad failed!") | partial: Climb/HangingObjectId in ZoneActorMovementState.cs:26-30; no climb doodad lookup |
| Ground attachments (gimmick/doodad as ground) | movement ground ids | stubs returning null: ZoneActorMovementInputServices.cs:617,620; ZoneActorMovementCorrection.cs:443-452 |
| WZUnitAttached/Hung/BondToDoodad/Unbond | sub_39362F90 (OnUnitAttached), sub_39363040 (OnUnitHung), sub_39363100 (OnUnitBondToDoodad); RTTI 0x3A519508-0x3A519600 | opcodes only WzOpcodes.cs:14-19 |
| Housing / HousingManager (state, build done/progress, remove) | RTTI 0x3A518B90, 0x3A518C70; sub_3933AD20 (Create "no house unit %u"), sub_3933AB50 (SetBuildDone "unit model is not prefab"), sub_39339DA0; WZHouseState/BuildDone/BuildProgress, ZWRemoveHouse | opcodes only WzOpcodes.cs:10,38-39 |
| Slave: master change, escape, spawn | sub_39364F60 (OnSlaveMasterChanged), sub_3936F280 (OnEscapeSlave), sub_3935D680 (HandleEscapeSlave); SlaveSpawn/Despawn/EscapeController RTTI 0x3A512480-0x3A5124E0 | opcodes WzOpcodes.cs:37,98; ZoneUnit.cs:21 comment only |
| Ship physics & control | ServerShipSimulationController RTTI 0x3A512208: sub_39237520 UpdatePhysics, sub_39236B60 SendMovement, sub_39250050 ("Ship Simulation Stucked"), sub_39228AE0 _IsValidShipMovement; ServerShipMovementController 0x3A5121D0; WZShipControlChange | stubs: ZoneSceneProviders.cs:790-794 (native 39261A50/39258EB0); ZoneActorMovementState.cs:39 throws NotSupported for ship helm; opcode WzOpcodes.cs:97 |
| Vehicle sim / VehicleUnitModel slaves | RTTI ServerVehicleSimulationController 0x3A512240; sub_39292790 (InitSlaves) | ZoneModelKind.Vehicle only (ZoneModelContent.cs:7) |
| Mounts | RTTI MountHorseController 0x3A5127D0 / UnMountHorseController 0x3A512800 | absent |
| Transfers, shipyards, portals | strings "Transfer(%u, %u)", "Shipyard(%u, %u)", "Portal_"; ServerTransferRestrictedItemDesc | absent (Shipyards id set in ZoneStatContent.cs:26 for stats only) |
| ServerOnly elevator FlowGraph | TODO.md L96 | FlowNodes written per TODO; no MoveEntityTo/elevator owner found in .cs (grep no hits) |

### OBJ-01: Bind real static gimmick/doodad entities as ground objects for movement
- Priority: P0
- Native evidence: sub_39335AB0 (ServerGimmick::InitStaticEntity), sub_393392C0 (CreateGimmick), sub_393365A0 (static spawn request).
- Repo state: FindGimmick/FindDoodad return null (ZoneActorMovementInputServices.cs:617,620); missing-ground reports (ZoneActorMovementCorrection.cs:443-452); RequestStaticGimmickSpawns logs 0 rows (ZoneSceneLoadingServices.cs:166-175).
- Work:
  - Create ServerGimmick/doodad entity records (id, type, guid, pose, physics proxy) and a registry by object id.
  - Implement FindGimmick/FindDoodad returning ZoneActorMovementGroundObject.
  - Decide static-gimmick placement source (level entity data; DB has none per notes) and feed RequestStaticGimmickSpawns.
- Depends on: scene cell-object admission (S1 DeferredBlockingWork)
- Confidence: medium

### OBJ-02: Run the ServerOnly elevator/platform graph for zone 129
- Priority: P0
- Native evidence: TODO.md L96 (Start, Movement:MoveEntityTo, Time:Delay, Logic:Any); sub_39339AD0 (HandleGimmickMovement); "ServerGimmickManager::HandleGimmickSetMovement" string 0x39FDECC0.
- Repo state: FlowNode behaviours exist per TODO; no MoveEntityTo entity-mover owner found in .cs; no gimmick entities to move.
- Work:
  - Verify CryAction MoveEntityTo reaches a level/gimmick entity target.
  - Emit ZWGimmickMovement for moved gimmicks; keep ground-pose sampling (ZoneActorMovementLinkedSampling) consistent.
  - Add a check that the graph starts and an entity transform changes.
- Depends on: OBJ-01
- Confidence: low (engine-side MoveEntityTo ownership not traced)

### OBJ-03: Implement WZ/ZW gimmick packet handlers and senders
- Priority: P0
- Native evidence: RTTI WZGimmickCreated/Removed/Movement/SetMovement/Grasped/ReloadStatics, ZWGimmick{JointBroken,ResetJoints,RemoveParts,Collided,Movement}, ZWRequestSpawnStaticGimmick (0x3A518A00-0x3A518B18, 0x3A51A2B0-0x3A51A318); sub_39339400, sub_39339AD0, sub_39338540.
- Repo state: opcode constants only (WzOpcodes.cs:43-45,106-108); echo path mentioned at ZoneSceneLoadingServices.cs:161 but no message classes.
- Work:
  - Define Messages/ for each packet from native serializers.
  - Dispatch into the gimmick registry (hasControl semantics from the Created log).
  - Roundtrip tests.
- Depends on: OBJ-01
- Confidence: medium

### OBJ-04: Handle World doodad packets and unit attach/bond packets
- Status: **Partial** — doodad create/remove/phase handlers with phase state are done (`ZoneDoodadRegistry`); unit attach/detach/hung/bond packets and the ZW surface/collide reports remain open.
- Priority: P1
- Native evidence: WZCreateDoodad/RemoveDoodad/DoodadChangePhase RTTI (0x3A51A6D0/0x3A51A700); sub_39362F90, sub_39363040, sub_39363100 ("can't find unit/child"); ZWReportDoodadSurfaceHack, ZWDoodadCollideUnit.
- Repo state: opcodes only (WzOpcodes.cs:14-19,47-49; ZwOpcodes.cs:46,48); no handlers.
- Work:
  - Parse attach/detach/hung/unhung/bond/unbond and apply parent-child links on ZoneUnit.
  - Doodad create/remove/phase handlers with phase state.
  - ZW surface-hack and collide reports from ground checks.
- Depends on: OBJ-01
- Confidence: medium

### OBJ-05: Load doodad function content and zone doodad effects
- Priority: P2
- Native evidence: LoadDoodadFuncZoneBuff/Overlap/ReactDescs (0x3A139760, 0x3A139830, 0x3A168940); DoodadFuncZoneBuff/Overlap/React 0x3A13A780-0x3A13A9A0.
- Repo state: absent (ZoneWorldContent.cs:12 DoodadAlmighty kind only).
- Work:
  - Content readers for doodad func tables; zone buff/overlap/react effects.
  - Growth timers and interaction use are World-authoritative; implement only zone-side effects.
- Depends on: OBJ-04
- Confidence: low

### OBJ-06: Climb doodads and hanging units
- Priority: P1
- Native evidence: ClimbDoodadInfo RTTI 0x3A528A38; sub_3935D5A0 (LeaveClimbDoodad); sub_3926A9A0 (GetClimbInfo).
- Repo state: ZoneActorMovementState.cs:26-30 fields only.
- Work:
  - Resolve ClimbDoodadInfo from doodad model, feed climb movement mode, implement LeaveClimbDoodad.
- Depends on: OBJ-04
- Confidence: medium

### OBJ-07: Housing state and build lifecycle
- Priority: P2
- Native evidence: RTTI 0x3A518B90/0x3A518C70; sub_3933AD20, sub_3933AB50, sub_39339DA0; WZHouseState/BuildDone/BuildProgress, ZWRemoveHouse.
- Repo state: opcodes only (WzOpcodes.cs:10,38-39).
- Work:
  - Housing entity (prefab model) and manager keyed by HouseId; handle state/progress/done/remove; register prefab collision.
- Depends on: OBJ-04
- Confidence: medium

### OBJ-08: Slave lifecycle (spawn, master change, escape)
- Priority: P2
- Native evidence: sub_39364F60, sub_3936F280, sub_3935D680; Slave{Spawn,Despawn,Escape}Controller RTTI 0x3A512480-0x3A5124E0; sub_39292790 (InitSlaves).
- Repo state: opcodes only (WzOpcodes.cs:37,98); ZoneUnit.cs:21 comment.
- Work:
  - Slave unit kind with master/passenger links, child slaves, escape handling.
- Depends on: OBJ-04
- Confidence: medium

### OBJ-09: Ship/vehicle simulation and control
- Priority: P2
- Native evidence: sub_39237520, sub_39236B60, sub_39250050, sub_39228AE0; RTTI 0x3A5121D0, 0x3A512208, 0x3A512240; WZShipControlChange.
- Repo state: leaf stubs ZoneSceneProviders.cs:790-794; helm unsupported ZoneActorMovementState.cs:39.
- Work:
  - Ship physics (mass, submerge depth, water), helm movement validation, ShipControlChange, periodic SendMovement.
- Depends on: OBJ-08, physics/water
- Confidence: medium

### OBJ-10: Mounts, transfers, shipyards, portals
- Priority: P2
- Native evidence: RTTI 0x3A5127D0/0x3A512800; strings "Transfer(%u, %u)", "Shipyard(%u, %u)", "Portal_"; LoadServerTransferRestrictedItemDescs (0x3A14F8E0).
- Repo state: absent.
- Work:
  - Mount via unit attach; transfer path entities; shipyard build states; portal triggers; transfer-restricted items table.
- Depends on: OBJ-04, OBJ-08
- Confidence: low
