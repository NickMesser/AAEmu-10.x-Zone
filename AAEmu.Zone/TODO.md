# First playable world TODO

Roadmap inherited from the source snapshot. Historical native-check results below are upstream observations, not validation of this publication copy.

**Next milestone:** one player enters Zone **129**, stays connected, stands on
terrain, walks, jumps, logs out and enters again. **Not achieved.** There is no
persistent gameplay launch mode yet. Full Zone feature parity remains the later goal.

Work in this order: **framework startup → native scene and level services →
player and movement → persistent host → one client check**. Finish these connections
before expanding feature coverage. Use the existing evidence in
FIRST_ENTRY_NOTES.md (internal upstream evidence omitted from this publication); another broad test pass is not the next step.

## Current position

- Engine ownership, model descriptors, game-service publication and the complete
  20-name registration owner are implemented in source. A **composed** native framework
  startup and shutdown now returns ready under an isolated check — **PASSED** (2026-09-15, exit 0):
  PostInit, provider publication, the expected World layout and the owned shutdown order
  all completed. Scene, actor, movement and client work remain open.
- **The scene path loads, answers a ground query and retires cleanly (2026-09-15, exit 0):**
  `--check-scene` composed the same engine, loaded **6/6 terrain cells**, completed five of the six
  real object passes on **all six** cells, completed the node-read schedule on all six, reached
  **`GroundQuery`** with real values — 305.42047 / material 115 at cell (0,0), matching the recorded
  terrain reference to five decimals — and finished **ordered retirement with every step clean**
  (all ten steps `ok`: `visibilityCells=0 importedBatches=0 vegetation=0 groups=absent brushes=0
  materials=0 models=0`, lease released). The check prints
  `PASSED (six-cell load, real ground query and ordered retirement) with 6 refused pass(es) - NOT ready:
  CellObjects:DeferredBlockingWork.` The deferred-object pass **still prevents readiness** —
  `ZoneSceneLoadReport.Ready` stays false. S1's acceptance criterion is met; admitting that pass is
  the remaining S1 item, recorded below. Player actors, physical frames and the persistent host
  remain **incomplete**.
- **All 20 game FlowNode behaviors are written.** This counts node behavior only. The
  last two, **Game:PlayerStaging** and **Game:PlayerLink**, are implemented in the
  working tree (`Simulation/ZoneNativeFlowNodes.Player.cs`, uncommitted as of
  2026-09-15) and are exercised by the composed build; only their real
  player-operation bindings remain open. Several service providers still lack full
  runtime binding.
- Component checks passed for imported terrain, object resources, model lookup and
  an unchanged XML batch. They do not establish a complete, active scene.
- Player components and movement algorithms exist; player actors, the physical frame
  loop, World movement publication and the persistent host remain **incomplete** (unwired).

## F1. Complete framework startup — immediate next checkpoint

Owner: engine integrator. Starts in
[ZoneNativeEngine.GameServices](Simulation/ZoneNativeEngine.GameServices.cs) and
[ZoneNativeFlowNodeRegistration](Simulation/ZoneNativeFlowNodeRegistration.cs).

**Startup/shutdown achieved (2026-09-15).** The composed native framework returns ready,
resolves the configured World and shuts down in its owned order; the recorded PostInit
access violation did not recur. The provider/behavior bindings in the table below remain
open, so F1's service work is not finished. See
the composed startup result (internal upstream evidence omitted from this publication).

- [x] Let GameFramework create and own the sole CSystem, preserving native shutdown order.
- [x] Locate the earlier PostInit crash: AI model lookup reached a missing `gEnv+8`
  game service. Implement retained model/actor/stance records, IGame publication,
  model lookup, and the real FlowSystem registration owner.
- [x] Preserve all 20 exact factory names and their order: **12 singleton, 8 per creation**.
- [x] Implement the shared native factory/node adapter, callback admission,
  reference/clone handling and native port/default storage. Source review passed;
  the complete family set and providers still need composition.
- [x] Finish the two remaining behaviors: **Game:PlayerStaging** and **Game:PlayerLink**.
  Both are written in `Simulation/ZoneNativeFlowNodes.Player.cs` and registered by the
  composed factory set. What remains is not the behavior but the real player operations
  they call: no concrete `IZoneNativeFlowPlayerOperations`/player-class owner exists yet,
  so with no client actor both nodes correctly take their absent-actor path.
  Reuse the saved port/event traces and the
  node checkpoint (internal upstream evidence omitted from this publication).
- [ ] Bind every service in the table below to the real engine/game owner. Call each
  provider's read-only admission during factory construction; missing implementation
  must not become a successful empty provider or fixed dedicated-mode result.
  Construction and admission are now exercised; the remaining entries in the table
  still lack their full runtime binding.
- [x] Compose all 20 factory owners through the engine-aware `CreateWithFramework`
  builder. Preserve singleton/clone lifetimes, hidden smart-pointer returns, graph
  callbacks, partial-construction cleanup, and defaults' destruction before DLL unload.
- [x] Run one isolated framework startup and shutdown: verify IGame registration,
  PostInit, providers and native World lookup. Record the first failing phase.
  The old options-only startup overload intentionally rejects incomplete composition.
  Result: exit 0 after correcting the provider-3 CSystem mirror offset (`+0x138`, not
  `+0x130`); the first failing phase before that fix was
  `CompleteGameFrameworkInitialization (PostInit)`.

| Node family | Behavior source | Concrete remaining work |
| --- | --- | --- |
| Seven PostFX nodes + Camera:OverrideFOV | 8 written; direct engine bindings present | Add their singleton factories; exercise only as required by startup |
| DifficultyLevel, Multiplayer:MP, Multiplayer:GameType | 3 written | Real difficulty/debug CVar state, client-actor/entity lookup and diagnostics |
| ActorSensor, HitInfo, ExplosionInfo | 3 written | Real ActorSystem/player identity and listener lifetime for ActorSensor |
| CrysisFX:ScreenFader, X2UI:ScreenFader | 2 written | Real master/group fader, shared UI fade state, texture/render update ownership |
| Crysis:TornadoWander | 1 written | Real extension/target lookup and completion lifetime through extension retirement |
| Crysis:FreezeEntity | 1 written; native dedicated handler is empty | Singleton factory binding |
| Game:PlayerStaging, Game:PlayerLink | Both written (working tree, uncommitted as of 2026-09-15); absent-actor path taken | Bind the real player staging/link/visibility operations |

The target's required ServerOnly elevator graph uses **Start,
Movement:MoveEntityTo, Time:Delay and Logic:Any**. All four are existing CryAction
built-ins. Preserve that authored graph; the separate X2 factory inventory above
is not a request to replace those built-ins.

**Done when:** the composed native framework returns ready, resolves the expected
World, and shuts down in its owned order. A build or managed admission check alone
does not close F1.

## S1. Activate the real scene and terrain

Owner: scene integrator. Depends on F1. Start in
[ZoneNativeEngine](Simulation/ZoneNativeEngine.cs),
[ZoneDedicatedTerrainCellResources](Simulation/ZoneDedicatedTerrainCellResources.cs)
and [ZoneTerrainCellWindow](Simulation/ZoneTerrainCellWindow.cs).

**S1 acceptance criterion met (2026-09-15, working tree, exit 0).** `--check-scene --config` now
completes the whole sequence: six-cell terrain load, five connected object passes on all six cells,
the node-read schedule, a **real ground query** (engine cell (0,0) at (512,512): height **305.42047**,
material **115**, local surface 0 - matching the recorded terrain reference to five decimals; plus cell
(2,0) at (2560,512): 121.803444 / 103 / 513), and **ordered retirement with every step clean** -
`visibilityCells=0 importedBatches=0 vegetation=0 groups=absent brushes=0 materials=0 models=0` and the
lease released. The check prints
`PASSED (six-cell load, real ground query and ordered retirement) with 6 refused pass(es) - NOT ready:
CellObjects:DeferredBlockingWork.` See
the S1 scene activation checkpoint (internal upstream evidence omitted from this publication) and
the retirement witness result (internal upstream evidence omitted from this publication).

**Not ready, and that is a separate remaining item.** `ZoneSceneLoadReport.Ready` is still false and the
deferred blocking pass still refuses by name: no managed producer creates or retires the deferred
models, materials and brushes from `statobjs.dat`/`materials.dat`/`brush.dat`, and the pass's active
World name/origin/position services are not bound. Exit 0 confirms the load, query and retirement; it
does **not** confirm scene readiness, and the success line names the refusal so it cannot be misread.

- [x] Prove dedicated load inputs: mode **0**, initial position **(+0.0, +0.0, +0.0)**.
  Resolve the existing native World registry and all six target cells.
- [ ] Own normal Seamless activation and its real CTerrain. Replace the imported
  terrain bootstrap in this production mode; avoid loading cells/objects twice.
  **Partially:** `ZoneSceneActivation` is the exclusive production scene owner on the composed
  framework engine and replaces the imported bootstrap; it never calls native Seamless
  LoadZone365704D0, because that would run the native cell producers a second time over the
  managed composition. A native CTerrain activation therefore does not exist yet.
- [ ] Connect passes in order: **main objects → sector vegetation → visibility →
  XML entities → big objects → deferred blocking work**.
  **Five of six connected**, and all five now complete on **all six cells**. The deferred blocking pass
  (3017D480/3017CC30) still refuses with `ZoneCellPassUnavailableException`; the load does not abort on
  the refusal.
- [x] Connect native roads, terrain/mesh resources and active-node scheduling, using
  an explicit frame period. Own deferred worker completion, cancellation and failures.
  Queued roads, cover/node metadata and the explicit frame period are connected, and the bounded
  one-node-per-cell-per-period read schedule ran to `state=Complete` on all six cells through the real
  stream adapter. Compiled road meshes and the native scheduler's active-node list still have no owner.
- [x] Retire scene entities/bodies and detach ground references first; then retire the
  five captured terrain replicas while CTerrain remains alive; finally retire the engine.
  All ten retirement steps complete cleanly with nothing left alive and the lease released.
- [x] Publish scene readiness only after required passes finish.
  `ZoneSceneLoadReport.Ready` is false while any pass refuses or any cell is unloaded.

**Done when:** one six-cell load supports a ground query and unloads its actual
roads/water/objects safely. Reuse component results; no separate asset corpus.
**This is met.** The admission of `DeferredBlockingWork` and the scene-ready flag remain open.


## S2. Supply complete XML and level services

Owner: entity/level integrator. Coordinate ownership with S1. Start in
[ZoneNativeEntityServices](Simulation/ZoneNativeEntityServices.cs) and
[ZoneSceneCoordinator](Simulation/ZoneSceneCoordinator.cs).

- [x] Preserve authored classes, Lua timing, parent/proxy completion, graph/area
  events, full entity IDs and owned retirement. One unchanged batch loaded and
  retired **55 entities**, but produced **no physics bodies**.
- [ ] Integrate that batch path into the scene, preserving native skips and real bodies.
- [ ] Supply `IZoneSceneLoadingServices`: level/time/areas, required scripts, static
  gimmicks, spawners, NPC postload and bounds.
- [ ] Admit required script/proxy/body variants with actual lifetimes. Current
  static/rigid support does not cover skeleton/articulated/area ownership or all Lua providers.

**Done when:** the unchanged target batch completes within the real scene and its
owned resources retire. Include this in S1's check instead of a second corpus.

## P1 / M1 / H1. Connect the player and persistent host

These owners may implement in parallel once shared scene/actor contracts agree.

| ID / owner | Next implementation | Depends on | Done when |
| --- | --- | --- | --- |
| P1 — Player | GameObject/ActorSystem/AnimatedCharacter, geometry, snapshot admission, rollback and retirement | Framework; scene for placement | One player is created/removed and its physical ID can be reused |
| M1 — Movement | Actor prephase → native physics → postphase, clock/status/actions, authoritative pose/spatial updates and `ZWUnitMovements` | P1 + scene collision | Walk/jump change the physical pose and publish a World update |
| H1 — Host | Gameplay CLI/config and production factories on ZoneSceneRunner; synchronize, load, send `ZWZoneLoaded`, adopt scene and keep receive/tick alive | Scene + P1 + M1 | One persistent development World session and clean disconnect/shutdown |

Start in [ZoneUnitRegistry](Simulation/ZoneUnitRegistry.cs),
[ZoneActorMovementController](Simulation/ZoneActorMovementController.cs),
[ZoneSimulationTick](Simulation/ZoneSimulationTick.cs),
[Program.cs](Program.cs), [ZoneSceneRunner](Simulation/ZoneSceneRunner.cs) and
[ZoneSession](Simulation/ZoneSession.cs).

## E1. Perform one client entry check

- [ ] Record commit, actual command/configuration, endpoints, Zone and external logs.
- [ ] Enter one player; stay connected about a minute and confirm ground placement.
- [ ] Walk, turn, stop and jump; confirm collision and authoritative movement.
- [ ] Observe one nearby NPC/static object if present.
- [ ] Log out and re-enter; confirm old-player cleanup and new admission.
- [ ] Stop the development host and record shutdown.

Record pass/fail and the first relevant event. Turn each failure into one
reproducible next action; do not expand the check into a full parity campaign.

## Runtime preparation

Supply your own external content database, compatible native modules, game assets and isolated World endpoint as described in the repository README. Keep configuration, build output and runtime logs outside the checkout. Internal launch details and local operational instructions are omitted from this publication.

## Park until first entry works

Other-zone navigation/flight/volume graphs; wider NPC AI; combat/skills; housing;
vehicles/ships; swimming/flying/ragdolls; world activities; full transfer/recovery;
performance tuning. Resume for a concrete entry blocker or after E1.
The wider upstream parity backlog is outside this first-entry roadmap.
