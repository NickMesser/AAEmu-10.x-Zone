<!-- Audit of movement: native x2game-dev_dedicate.dll (ArcheAge 10.0.2.13 r575) vs this repo at 76e439f. Paths rewritten to the src/ layout; line numbers refer to 76e439f. -->

## Movement, units, player actors, physics, navigation (MOV)

Coverage summary. The repo treats x2game-dev_dedicate.dll as a hosted oracle, so "coverage" means managed owners for native phases. The managed side is deep in several areas. It has a unit registry with a 64-cell spatial index (39332730), the actor movement receive/admission/correction/sampling controller (392578D0, 39270A40, 39253D00, 39232570, 392515E0), the prephase/step/postphase coordinator, the ZWUnitMovements encoder, a native living-body physics world wrapper (3502A300 is the living action handler), and the legacy AI path object plus graph pools in cryaisystem. The first-entry blockers are all in the player-creation and pose-write seam. `ZonePlayerActorFactory.ReportDependencies()` (ZonePlayerActorFactory.cs:223-229) reports 4 of 5 dependencies unresolved: the CPlayer class owner, GameObject/ActorSystem/AnimatedCharacter publication, the player operations ABI, and the living teleport seam. The `TODO(pose-write)` seam means the native body cannot be moved by the host, and `ZoneOwnedPlayerPhysics` is the managed fallback. Several native movement subsystems have no owner at all: MoveHackChecker verification, fall damage, swim/glide/ladder/mount states, ship/vehicle controllers, NPC path finding, and the legacy navigation runtime (quadtrees and A*). Interest management is World-side, so the zone only needs to publish accurate poses.

Caveat: the repo has no tests for movement. The test project has only NavigationPath, Trimming, TerrainHeightField, Frame, PackedInteger, Config, ArchiveAes, ContentFormula and Sqlite tests.

### Coverage table

| Native item | Native evidence | Repo status |
| --- | --- | --- |
| Server unit registry, spatial grid, deferred removals | 39332730 (cell size 64), 3935FE10, 3935D260; RTTI `X2::GameServer::ServerUnit` 0x3A5191D8 | Present: src/AAEmu.Zone/Units/ZoneUnitRegistry.cs (632 lines), ZoneUnitSpatialIndex.cs, ZoneUnitUpdateScheduler.cs |
| Zone tick incl. UpdateMovementBuffers | 39333110, 3936A010 | Present: ZoneSimulationTick.cs (services interface; production binding in ZoneSceneProviders.cs) |
| Actor movement receive/history/sampling/correction | 392578D0, 39270A40, 39253D00, 39232570, 392515E0 | Present: ZoneActorMovementController.cs, ZoneActorMovement*.cs; permissive TODOs (model-guard, model-488-90C, interpolation config, model-flags) |
| Prephase / physics step / postphase | RTTI `X2::NetActorMovementController` 0x3A512308, `IUnitModelMovementController` | Present: ZoneMovementFramePhases.cs, ZoneNativeTerrainPhysicsWorld.Step.cs; the pose-write step is refused by name |
| ZWUnitMovements batching to World | `X2::ZWUnitMovementsPacket` vftable 3937F430; `ZWUnitMovementsBuffer` 3936E720 (3 buffers, +184/+336216/+672248) | Present: ZoneMovementOutgoing.cs (437 lines, encoder), Protocol/Messages/UnitMovement.cs. Gaps: only the low-5 ActorFlags bits are emitted; no FallVel/Gc/Climb/Push optional blobs |
| Living body physics (CLivingEntity) | cryphysics 3502A300 (action_move), 35028BF0 SetParams, 35029A30 GetStatus | Partial: ZoneNativeTerrainPhysicsWorld.Living.cs, ZonePoseWriteProbe.cs, ZoneNativePhysicsBackendAdapter.cs. Position write is not wired (body+0x47=3 means the action is deferred) |
| Managed owned-physics fallback | n/a | ZoneOwnedPlayerPhysics.cs (gravity plus terrain raycast), ZoneOwnedPhysicsBackend.cs; not the native authority |
| CPlayer / GameObject / ActorSystem / AnimatedCharacter creation | CPlayer ctor 39301CA0, factory 3929E580 (0xB78 bytes), dtor 39301EC0, RTTI `CPlayer` 0x3A5154D8, `CActor` 0x3A515248, `AnimatedCharacter` string 0x39FD2A20 | Blocked: ZonePlayerActorFactory.cs:48-52,223-229 (4 of 5 dependencies unresolved) |
| Player flow nodes (staging/link) operations | actor +340/+217/+218/+258, 392FDC10; listener add/remove 39302320/393008C0 | Nodes written (ZoneNativeFlowNodes.Player.cs); operations throw Blocked (ZonePlayerActorFactory.cs:580-619) |
| Movement hack verification (MoveHackChecker) | RTTI `X2::MoveHackChecker::MovementVerifier` 0x3A511F38 (vftable setters at 3921B940, 3921DB40, 3921FB90); "over velocity detected" log in 3921D1C0; cvars `movement_verify_*` registered in 392B1240; "Dump movements for Unit" 39218F00 | Absent |
| Fall damage / freefall signal | "apply fall damage" in 392D7600; "fall height" 392DD770; cvars `pl_fallDamage_SpeedSafe/Fatal/Bias`, `pl_fall_start_velocity/height` (392B1240) | Absent (only attribute-name strings in Protocol/Units/NativeAttributeNames.cs) |
| Swim / water / drown | "actor swim bottom level" 392D7490; cvars `swim_up_speed_mul`, `swim_jump_speed`, `swim_jump_end_depth`; WaterVolumePrefabObject 0x3A514E70; swim dead anim strings | Absent; ZoneNativeStaticObjectServices.Water.cs loads water cells only |
| Glider | "[glide] glide start" 392E36B0, `GlideBuffFinder` 0x3A513D60, StartGlider/CleanupGlider | Absent |
| Ladder / climb doodad | `ServerUnit::LeaveClimbDoodad` 3935D5A0, `ActorUnitModel::GetClimbInfo`, cvar `movement_verify_move_speed_max_climbing_vel` | Absent (`TODO(gimmick-doodad)` in ZoneActorMovementInputServices.cs:616) |
| Mount / slave / passenger | `Unit::ProcessPassenger` 392615C0, `Unit::AttachTo`, Mount/UnMountHorseController 0x3A5127D0/0x3A512800, SlaveSpawn/Despawn/EscapeController | Mostly absent; ZoneUnit.MovementBlockedUntil (+A59C, EscapeSlave) only |
| Warp / impulse / teleport | `Unit::Warp` 39258B00, `Unit::Impulse` 3925C7D0 | Absent; physics Teleport refuses (ZonePlayerActorFactory.cs:772-778) |
| Ship / vehicle movement controllers | `ServerShipSimulationController::SendMovement` 39236B60, `ServerVehicleSimulationController::SendMovement` 392345A0, `_IsValidShipMovement` 39228AE0, `VehicleUnitModel::GetUnitMovement` 3926C5A0 | Wire records only (UnitVehicleMovement, UnitShipMovement); no controllers |
| Ragdoll / FallAndPlay | `CreateRagdollConstraint` 3922B7F0, OnFallAndPlay 392D5800/392DE780 | Absent (`TODO(model-flags)` ragdoll flag defaults to false) |
| Psychokinesis / movement sync guard | "sync movement rejected" 39270A40, "movement queue over flowed" 392578D0/3933A820 | Guard permissive (ZoneMovementSceneServices.cs:214,261); queue overflow semantics partly modelled |
| AI movement speeds / AIMovementAbility | AIMovementSpeeds table parse 392ADC00, `sprintSpeed`, `usePathfinder` | Partial: ZoneActorMovementInputServices.cs turn speed (`TODO(turn-speed-units)`); AIMovementSpeeds table not seen |
| NPC path request | `MoveSkillController::RequestPathFinding` 39227ED0, OnPathFound, IAIPathFindListener | Absent |
| Legacy nav startup (netmission0.bai, areas, vertices, graph records) | cryaisystem 3107BE38, 31103988, 311A8A00 | Content readers present (Content/ZoneNavigationLegacy*.cs); stops at "requires remaining graph reader" |
| Nav graph pools, links, candidate index, region ids, shape cache | 3110B148.., 31104EE8.., 310FB7E8, 3109366C, 3117C328 | Present: Simulation/ZoneNavigation*.cs |
| Nav modifiers (AINavModifierPrefabObject, "AINavigationModifier") | RTTI 0x3A514ED0; 31049028, 310515F8 | Present: ZoneNavigationModifierQuery/Runtime.cs; prefab-object side unverified |
| CNavPath point data and trimming | 3113CA60..3113F1D0, 31141B30 | Present: ZoneNavigationPath*.cs, covered by tests |
| Volume / flight / road / surface nav, quadtree build, path search | 311A8A00 (volume), 3107BE38 continuation | Absent (src/AAEmu.Zone/Content/ZoneNavigationLegacyStartup.cs:140-151) |

### Tasks

### MOV-01: Publish CPlayer through the RTTI class registry and bind the player-class owner
- Priority: P0
- Native evidence: CPlayer ctor 39301CA0, factory 3929E580 (operator_new 0xB78), destroy 3929E520, RTTI locator 0x39FC1888, vtable 0x39FD4E58, class-name op 39302950 returning "CPlayer" at 0x39FC0ED0, RTTI string `CPlayer` 0x3A5154D8.
- Repo state: ZonePlayerActorFactory.cs:48,223-225 `ClassPublicationDependency` is unresolved. `ZoneNativeFlowActorRegistry.RegisterPlayerClass` (ZoneNativeFlowActorRegistry.cs:78-95) has no producer.
- Work:
  - Resolve the RTTI-keyed class registry entry for CPlayer and call the factory with a valid entity and GameObject.
  - Capture the +1F0 class-name token and register an `IZoneNativeFlowPlayerClassOwner`.
  - Implement destroy (3929E520) on retirement and rollback.
  - Flip the dependency to resolved only after a successful create, name read and destroy.
- Depends on: MOV-02
- Confidence: medium

### MOV-02: Compose GameObject / ActorSystem / AnimatedCharacter for the player entity
- Priority: P0
- Native evidence: `AnimatedCharacter` string 0x39FD2A20; "ActorUnitModel::OnModelChanged - AnimatedCharacter is NULL" 0x39FC2290; `ActorUnitModel::Init` 0x39FC23A8; RTTI `CActor` 0x3A515248. Repo cites 3DEngine+6A0 for the surface lookup.
- Repo state: ZonePlayerActorFactory.cs:49,226 `ActorPublicationDependency` unresolved. ZoneNativeEntityServices.cs:7-9 says plain Default-class entities never publish to ActorSystem. FIRST_ENTRY_NOTES composition item is still unchecked.
- Work:
  - Locate native functions that create GameObject, register it with ActorSystem, and attach AnimatedCharacter from the model descriptor.
  - Extend ZoneNativeEntityServices to publish the entity; `CaptureFlowActorIdentity` must then return non-null (ZoneNativeEngine.FlowActors.cs:113).
  - Wire the engine surface resolver (3DEngine+6A0) into the factory; it is a required gate.
  - Add rollback and retirement in the reverse of the creation order.
- Depends on: none
- Confidence: medium

### MOV-03: Bind real player operations (sensor listeners, staging, link, draw-hidden)
- Priority: P1
- Native evidence: listener add/remove 39302320/393008C0; CPlayer dtor 39301EC0 (listener ids 1-12, vector at +0x9F0, sub-object +0x9B8); staging at actor +340/+217/+218/+258 and 392FDC10.
- Repo state: ZonePlayerActorFactory.cs:50,227,580-619 (each operation throws `Blocked`). The flow nodes exist in ZoneNativeFlowNodes.Player.cs and Sensors.cs.
- Work:
  - Implement a concrete `IZoneNativeFlowPlayerOperations` on top of a real CPlayer.
  - Respect the 12-id listener set and the +0x9F0 vector lifetime.
  - Exercise via a composed Game:PlayerStaging / PlayerLink check.
- Depends on: MOV-01
- Confidence: medium

### MOV-04: Close the pose-write seam: move a living body by the native action handler (or take ownership of the frame pump)
- Status: **Partial** — the selected backend's status is bound to the player generation and read by input, correction, pose publication and entity mirroring. Owned physics no longer steps while movement readers sample the separate native body. `ZoneOwnedMovementMotion` preserves integrated height/vertical velocity across ordinary client corrections; jump impulses, gravity and landing remain authoritative. Explicit teleports still use the backend directly. Native living-body action/teleport remains unresolved.
- Priority: P0
- Native evidence: cryphysics 3502A300 (inline path only when body+0x47 > 6, otherwise deferred); queued producers 35023200/35023650; SetParams 35028BF0, GetStatus 35029A30, vtable +0x48/+0x50.
- Repo state: `TODO(pose-write)` in IZonePhysicsMotion.cs:38,77, ZoneNativePhysicsBackendAdapter.cs:142, ZoneMovementSceneServices.cs:65,175,313, ZoneActorMovementCorrection.cs:540. ZonePoseWriteProbe.cs is an evidence mode only. ZoneOwnedPlayerPhysics.cs is a managed substitute with its own authority.
- Work:
  - Find what makes body+0x47 exceed 6 (the bound player body reads 3) and either drive that native frame pump or flush the queue from the host.
  - Implement `ParametersCall`/action calls for position, velocity and teleport (the living-teleport dependency, ZonePlayerActorFactory.cs:52,229,772-778).
  - Verify with a read-back after a step that the position changed, then remove the refusals.
  - Decide whether the owned fallback is kept only as a test double.
- Depends on: MOV-02
- Confidence: medium

### MOV-05: Wire real physics step with player living-body parameters (dimensions, gravity, dynamics) and ground contact
- Status: **Partial** — owned movement now uses the selected backend's status; its native ground adapter converts endpoint pairs to displacements, so ground rays remain vertical. Horizontal collision sweeps cover velocity times the actual frame duration. Native actor composition, full living dynamics and authored per-actor gravity remain open.
- Validation (2026-10-28): real six-cell zone-129 replay admits unit 387 and records `grounded=True` at local `(1172.5996, 1646.5, 366.33508)` on repeated owned steps. The native terrain query at `(512,512)` returns that same horizontal column after the endpoint/displacement fix. Player resources retire before terrain on World EOF; the process exits with the expected code 3.
- Priority: P0
- Native evidence: cryphysics living entity param types; CLivingEntity action string "CLivingEntity:Action(action_move)"; "velRequested" at +0x1C0 in the state export (vtable +0x78); cvars in x2game.cvar*.txt (`pl_*`).
- Repo state: ZoneNativeTerrainPhysicsWorld.Living.cs:114-128 binds only the simulation/flag/dimensions/dynamics buffers. Step is in ZoneNativeTerrainPhysicsWorld.Step.cs. Terrain collision is in the scene check (real ground query verified). Deferred static objects, skeleton and articulated bodies are not admitted (TODO.md S2).
- Work:
  - Populate the living-dynamics parameters from the actor model and cvars (gravity, stand/crouch dims, step height, slope).
  - Run a step over the six-cell terrain and statics, and confirm the player rests on the terrain at its spawn.
  - Add managed tests that assert height and ground material after N steps.
- Depends on: MOV-04, S1 DeferredBlockingWork (TODO.md)
- Confidence: medium

### MOV-06: Bind the production movement services (replace permissive leaves) and run prephase/step/postphase in the host loop
- Status: **Partial** — the production prephase/step/postphase loop publishes the selected backend's live pose. Admission binds the movement model immediately, so idle actors publish physics before their first movement packet. Spatial queries read the committed pose instead of the entry snapshot. Native model/eligibility flags and interpolation CVars remain open.
- Priority: P0
- Native evidence: 39270A40 (model+140 guard), 392515E0 (model+488 / actor+90C), 39232570 (physical attachment), 39253D00 (time admission), 3936A010 (UpdateMovementBuffers), 39333110 (tick).
- Repo state: ZoneMovementFramePhases.cs and ZoneMovementSceneServices.cs exist. `TODO(model-guard)`, `TODO(model-488-90C)`, `TODO(movement-interpolation-config)`, `TODO(model-flags)`, `TODO(updated-latch)`, `TODO(buffered-turn-jump)`, `TODO(actor-flag-100)`, `TODO(facing-control)`, `TODO(native-input-state)`. No persistent host calls the coordinator (H1).
- Work:
  - Resolve each TODO against the native model/actor field it names, or record why the permissive default is correct.
  - Wire `IZoneMovementFramePhases` into the scene coordinator tick ahead of `ZoneSimulationTick`.
  - Add the interpolation mode and buffer from configuration (the native source cvars).
  - Add a headless test that feeds a client movement packet and expects a changed authoritative pose.
- Depends on: MOV-04, H1
- Confidence: medium

### MOV-07: Complete ZWUnitMovements publication (flags, optional blobs, zone-owned authoritative state)
- Status: **Partial** — backend poses convert from engine zone-local meters to continent coordinates at publication, matching World's default wire policy. A captured-entry regression verifies the outgoing packet and live spatial position together. Fall, ground, climb and push optional blobs remain open.
- Priority: P0
- Native evidence: `ZWUnitMovementsPacket` vftable 3937F430; buffer 3936E720 (3 x 336 KiB buffers); update 3936A010; "movement queue over flowed" 392578D0/3933A820.
- Repo state: ZoneMovementOutgoing.cs (437 lines) encodes default-style actor movement only, with the low-5 ActorFlags bits. ZoneMovementOutgoing.cs:63 has `TODO(world-seed-check)` on the coordinate frame policy. The UnitMovement record has Vehicle/Ship/Push/Ground variants (Protocol/Messages/UnitMovement.cs:51-67).
- Work:
  - Confirm byte-for-byte parity of the buffer layout and flush cadence with the native buffer (3936A010) against the captured packets.
  - Emit FallVel, ground-contact (Gc), climb and push blobs when flags demand them.
  - Verify the world-vs-local coordinate policy at World.
  - Emit NPC movement through the same path.
- Depends on: MOV-06
- Confidence: medium

### MOV-08: Implement server-side movement verification (MoveHackChecker::MovementVerifier)
- Priority: P1
- Native evidence: RTTI `X2::MoveHackChecker::MovementVerifier` 0x3A511F38; log 3921D1C0 ("over velocity detected ... backpack, freefall, SumOverVel, ForceSync"); constructors/vftable setters 3921B940, 3921DB40, 3921FB90; dump 39218F00; cvars registered in 392B1240: `movement_verify_enable`, `_speed_*`, `_onground_*`, `_airstanding_*`, `_gravity_*`, `_detailed_warp_*`, `_min_z_pos`, `_max_packet_interval`, `_ignore_msec_after_skill_controller`, `movement_boost_mul`, `movement_levitation_hack_buff_start_time`.
- Repo state: absent.
- Work:
  - Decompile the verifier classes and tabulate each cvar with its default.
  - Implement the sampling windows and ForceSync (correction) output through the existing correction services (ZoneActorMovementCorrection.cs).
  - Report to World the same hack reports; add tests with synthetic speed/air-stand/gravity traces.
- Depends on: MOV-06
- Confidence: medium (the verifier class is not yet decompiled; the cvar list is solid)

### MOV-09: Implement fall damage, freefall and landing
- Priority: P1
- Native evidence: "apply fall damage, %.2f, %.2f" in 392D7600; "fall height: %.2f" 392DD770; "fall speed" near it; freefall flag handler 392E1D50/392F0610/392FFFF0; cvars `pl_fallDamage_SpeedSafe/Fatal/Bias`, `pl_fall_start_velocity`, `pl_fall_start_height`, `pl_fall_debug`; "landfall" string; ZoneBuffLandingRemoval.cs hints at landing hooks.
- Repo state: absent (no fall-damage computation). ZoneBuffLandingRemoval.cs only removes buffs.
- Work:
  - Port the speed-to-damage curve (safe/fatal/bias) and apply through the existing unit-points path (Protocol/Messages/UnitPoints.cs).
  - Read the fall velocity from the authoritative physics step (MOV-05) rather than from the client.
  - Fire the landing buff removal after damage.
- Depends on: MOV-05
- Confidence: medium

### MOV-10: Swim, water-volume and drowning movement
- Priority: P1
- Native evidence: "actor swim bottom level" 392D7490; cvars `swim_up_speed_mul`, `swim_down_speed_mul`, `swim_side_speed_mul`, `swim_back_speed_mul`, `swim_buoy_speed`, `swim_jump_speed`, `swim_jump_end_depth`, `swim_jump_permission_range`; `WaterVolumePrefabObject` RTTI 0x3A514E70; "no water volume in prefab" 0x39F8BE80; swim death strings.
- Repo state: absent. Water cell data is loaded (ZoneNativeStaticObjectServices.Water.cs) but not used for unit movement.
- Work:
  - Implement the water-level query for a position and the swim state transition (stance) in the movement postphase.
  - Apply the speed multipliers and the buoyancy.
  - Include the drown and underwater-breath hook if the zone drives it (confirm the owner, as it may be World).
- Depends on: MOV-05
- Confidence: low-medium

### MOV-11: Glider, ladder/climb, rope, and mount/passenger movement states
- Priority: P2
- Native evidence: glider 392E36B0 ("[glide] glide start"), `GlideBuffFinder` 0x3A513D60, `StartGlider`; `ServerUnit::LeaveClimbDoodad` 3935D5A0; `Unit::ProcessPassenger` 392615C0; `Unit::AttachTo`; `ActorUnitModel::GetClimbInfo`; Mount/UnMountHorseController 0x3A5127D0/0x3A512800; `NetActorRopeSkillController` 0x3A512868.
- Repo state: absent. `TODO(physical-attachment)` at ZoneActorMovementController.cs:311 and `TODO(gimmick-doodad)` at ZoneActorMovementInputServices.cs:616.
- Work:
  - Implement the linked-sampling path for a unit attached to a gimmick, doodad or slave.
  - Add the climb-doodad enter/leave and glider state, each behind its buff or flag.
  - Mount/slave: model the passenger offsets and the movement block (+A59C) release.
- Depends on: MOV-06
- Confidence: low-medium

### MOV-12: Ship and vehicle movement controllers (server simulation)
- Priority: P2
- Native evidence: `ServerShipSimulationController::SendMovement` 39236B60, `ServerVehicleSimulationController::SendMovement` 392345A0, `X2::_IsValidShipMovement` 39228AE0 ("invalid ship movement"), `VehicleUnitModel::GetUnitMovement` 3926C5A0, `ShipUnitModel::GetUnitMovement`, `VehicleMovementController::UpdateWheeledVehicle`; RTTI `ServerShipMovementController` 0x3A5121D0, `IShipMovementController`.
- Repo state: only the wire records (UnitVehicleMovement, UnitShipMovement in UnitMovement.cs:65-67); no controllers or physics.
- Work: Port the validity checks, the send cadence and the wheeled/ship dynamics; this is parked per TODO.md until first entry works.
- Depends on: MOV-06
- Confidence: medium

### MOV-13: Unit warp, impulse, teleport and forced-sync corrections
- Priority: P1
- Native evidence: `Unit::Warp` 39258B00, `Unit::Impulse` 3925C7D0, "ForceSync(%d, %d, %d, %d)" in 3921D1C0, `ResetMovementController` string.
- Repo state: absent; the physics Teleport refuses (ZonePlayerActorFactory.cs:772-778; IZonePhysicsBackend.cs:77). Correction exists in ZoneActorMovementCorrection.cs.
- Work:
  - Implement Warp to set the pose, the body and the movement history anchor atomically (reset ZoneActorMovementHistory).
  - Implement Impulse for knockback and push (the Push blob in MOV-07).
  - Needed for zone transfer, GM teleport and the world-entry placement of the first player.
- Depends on: MOV-04
- Confidence: medium

### MOV-14: Player admission, placement and re-entry end to end (P1 acceptance)
- Priority: P0
- Native evidence: CPlayer 39301CA0 / 3929E580; unit admission 3935D260; physical ID reservation (living entity ushort ids); registry Create (ZoneUnitRegistry Character branch).
- Repo state: ZonePlayerActorFactory implements Create/Retire/Admit with rollback and fresh physical ID retries (MaxPhysicalIdAttempts=8) but runs only managed-only or blocked. No test creates and removes a player in a native scene.
- Work:
  - Run Create, then ground placement, then Retire, then Create again in the composed scene check; assert that physical IDs are not reused and the reservation count grows by one per entry.
  - Decide on the 16-bit physical-ID exhaustion policy for long sessions (the IDs are never released, Living.cs:59-63).
  - Make the placement use the real terrain ground query (305.42 at cell 0,0 as recorded).
- Depends on: MOV-01, MOV-02, MOV-04, MOV-05
- Confidence: medium

### MOV-15: NPC path finding and movement controller
- Priority: P1
- Native evidence: `MoveSkillController::RequestPathFinding` 39227ED0, `OnPathFound` 0x39FBEC78, `IAIPathFindListener` RTTI 0x3A511FD0, strings `usePathfinder`, `pathFindPrediction`; AI dll path functions 3113CA60..3113F1D0.
- Repo state: path object only (ZoneNavigationPath*.cs). No path search, no request and no listener. `FindPathType` is only used for spawn (ZoneNpcInitialSpawnBehavior.cs:42,118).
- Work:
  - Implement the request/response between MoveSkillController and the AI path system; deliver the path to the NPC movement goal (ZoneLocateGoal etc.).
  - Feed NPC steering into the same physics step and outgoing encoder.
  - Gate on the nav runtime (MOV-16).
- Depends on: MOV-16, MOV-06
- Confidence: medium

### MOV-16: Finish the legacy navigation runtime (volume/flight/road/surface readers, quadtree build, A* search)
- Priority: P2
- Native evidence: cryaisystem 3107BE38 (load sequence), 311A8A00 (volume reader), 31103988 (network runtime construction), 311A2864 (vertices), 31058598 (FindPathType virtual); `AINavigationModifier` 0x39FD1838; netmission0.bai.
- Repo state: src/AAEmu.Zone/Content/ZoneNavigationLegacyStartup.cs:140-151 stops with `RequiresRemainingGraphReaders`. Pools, links, region ids, shape cache and modifier query/runtime exist in Simulation/ZoneNavigation*.cs but are not composed into a published graph. Quadtree construction is not implemented (file mentions it in a comment only).
- Work:
  - Implement the volume, flight, road and surface loaders and quadtree construction in order.
  - Compose the runtime graph from the existing pools and publish it to the AI provider.
  - Implement the path search and the smart-object traversal on `ZoneNavigationPath`.
  - Test against the zone 129 network file.
- Depends on: none (the other-zone graphs are parked in TODO.md)
- Confidence: medium

### MOV-17: Nav-modifier prefab objects and dynamic regions from XML entities
- Priority: P2
- Native evidence: `AINavModifierPrefabObject` RTTI 0x3A514ED0; "AINavModifierPrefabObject::UpdateMatrix(int flags) faield to create navigation shape" 0x39FD0900; region id pool 31048EB8..3113AA6C.
- Repo state: runtime/query exist (ZoneNavigationModifierRuntime.cs, ZoneNavigationShape.cs); no XML-entity producer creates them (S2 entity batch has no such class).
- Work: Add the entity class producer and UpdateMatrix hook so authored modifiers register and retire with the cell.
- Depends on: MOV-16, S2 entity services
- Confidence: low

### MOV-18: Movement test coverage and a headless movement simulator
- Status: **Partial** — `ZoneMovementAuthorityTests` drives captured player admission through owned physics, physical input, prephase/step/postphase, live spatial membership and ZW movement decoding. It checks idle publication, retirement, and a complete jump/fall/landing despite repeated stale client poses. Terrain is synthetic in these tests; real-client walk/jump/re-entry and other movement modes still require validation.
- Priority: P1
- Native evidence: n/a (parity harness); the controller and phases cite 39253D00/392578D0.
- Repo state: AAEmu.Zone.Tests has no test for ZoneActorMovementController, ZoneMovementFramePhases, ZoneMovementOutgoing, ZoneUnitSpatialIndex or ZoneUnitRegistry.
- Work:
  - Add tests with fake services: time admission, history sampling, correction, quantization round trip against World's `UnitMoveType.Read`, and spatial index moves.
  - Add a golden-vector test of ZWUnitMovements bytes.
- Depends on: none
- Confidence: high

### MOV-19: Sight / visibility ownership decision (interest management)
- Priority: P2
- Native evidence: no zone-side visibility class found in RTTI/strings (only `ZoneUnitSpatialIndex` cell grid, 39332730, with Character/Npc categories); client `ClientUnit`; Zone sends `ZWUnitMovements` and World does the relay (per repo MovementRelay notes).
- Repo state: ZoneUnitSpatialIndex.cs implements range queries for AI and aggro. No sight set is needed for World relay.
- Work: Confirm with the World team that no zone-side interest set is required, and record it; add a range-query parity test against 39332730 behavior.
- Depends on: none
- Confidence: medium
