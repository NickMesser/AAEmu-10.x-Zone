# CryEngine module removal

Target: run the zone process with no CryEngine dedicated modules installed or loaded, then
replace the gameplay and collision capabilities they supplied. A terrain-only player scene is
the first milestone; it is not full feature parity.

Use [`zone.example.json`](../../src/AAEmu.ZoneServer/zone.example.json) with
`"engineBackend": "managed"`. This selects `managed-terrain` physics and managed AI by default.
Neither `engineDirectory` nor `luaLibraryPath` is required. Assets and the read-only content
database remain required. Mixed native providers are rejected before startup.

The native comparison configuration is
[`zone.native.example.json`](../../src/AAEmu.ZoneServer/zone.native.example.json). Existing
`physicsBackend=owned` and its legacy `managed` alias still use native terrain and static
queries. They do not remove CryEngine. Existing configurations retain their original semantics.

## Current replacement boundary (2026-10-28)

| Module | Managed replacement in this change | Remaining capability gaps |
| --- | --- | --- |
| `crysystem.dll` | Configured assets, content, clocks, scene owner, startup/frame/shutdown composition | Finish diagnostics, platform checks and capability policies across every scene type |
| `cry3dengine_dedicate.dll` | World/cell selection, authored heightmaps/surfaces/seams, decoded placements, default/picking CGF proxies including referenced source streams and curved primitives | Vegetation, visibility/area behavior, water volumes and procedural objects |
| `cryphysics_dedicate.dll` | Character integration, terrain/static triangle and analytic primitive queries, shared-mesh BVHs, affine instances, combined movement sweeps, reachable ground/ceiling support, actor bounds and material IDs | Exact character capsules, complete stance/step behavior, friction, rigid bodies, swimming, vehicles, ragdolls and exact tangent conformance |
| `cryentitysystem_dedicate.dll` | Player/NPC identity generations, registry/spatial membership and managed player lifecycle | General entity proxies, components, areas, attachments and entity-script state |
| `cryaction_dedicate.dll` | Player movement controller/frame pipeline and managed goal-pipe execution adapter | Authored behavior construction, game-object actions, signal bindings and FlowGraphs |
| `cryaisystem_dedicate.dll` | Provider ownership, active/target state, signals, generation retirement and deterministic update scheduling | Perception, authored behavior admission, navigation, locomotion, leash and combat decisions |
| `cryscriptsystem.dll` | Managed mode has no Lua bootstrap or script DLL dependency | Replacement interpreter or translated behavior layer and the game script API |
| `cryanimation.dll` | Authored model/stance dimensions and movement parameters are consumed as data | Server-relevant skeleton collision, animation events and motion driven by animation |
| `cryrendernull.dll` | Managed host is headless and never constructs a renderer | No renderer is required; retain checks that no rendering service enters the server path |
| `tbb*`, `xlcommon.dll`, `xldiag.dll`, engine CRT dependencies | Engine startup is bypassed entirely | Remove native comparison deployment dependencies after replacement coverage is sufficient |

New production owners are `ZoneManagedSceneHost`, `ZoneManagedSceneActivation`,
`ZoneManagedTerrainGeometry`, `ZoneManagedPhysicsBackend`, `ZoneManagedAiProvider` and
`ZoneManagedAiActor`. They reuse existing content readers, unit/NPC registries, World protocol,
scene retirement, owned integration and movement publication. Native ABI adapters remain for
comparison; managed scene startup never constructs them.

## Implemented first milestone

- [x] **CE-01 — Independent host composition.** Native-free configuration, World join once,
  ordered loading, `ZWZoneLoaded`, authoritative player admission, frame pipeline and retirement.
  No engine/Lua directory is required and the native engine-input builder is not called.
- [x] **CE-02 — Authored managed terrain.** Load the selected cells at zone-local coordinates,
  apply XML surface slots and version-24 overrides, preserve seam behavior and holes, and unload
  current samples on retirement. Failed cells retain named diagnostics and prevent terrain readiness.
- [x] **CE-03 — Managed collision foundation.** Rays intersect authored two-meter triangles and
  report normals/materials. Sweeps/overlaps and registered actor bounds support basic character
  collision. Missing cells, unloaded cells and hole slot 31 provide no floor. Capsules currently
  use conservative upright bounds; terrain collision does not imply object collision.
- [x] **CE-04 — Managed AI ownership and execution seam.** Fresh behavior state per generation,
  exclusion across managed/native providers, deterministic wrapped-clock scheduling, target
  retirement, callback-safe cancellation and disposal, and an adapter to existing goal pipes.
  The production host currently admits idle managed NPC control state. Its gameplay AI gate stays
  closed until authored behavior and supporting services are attached; unsupported signals fail.
- [x] **CE-05 — Evidence without engine modules.** Synthetic host integration checks join,
  readiness, player landing/movement, clean World EOF and player-before-terrain retirement while
  inspecting loaded modules. A separate real-content zone-129 replay also reaches readiness,
  admits captured player 387 with `physicalId=0`, emits movement/location reports and retires.

The first native-free milestone passed 172 standalone tests, including 31 new configuration,
physics, AI and scene/host cases. Static collision and referenced/curved proxy work pass all 247 tests.
The solution builds successfully; existing analyzer warnings remain.

Real-content replay evidence is kept outside the repository: `managed-entry-verified-zone.log`,
`managed-entry-verified-modules.json` and `captures/managed-entry-output-verified.bin` under the local runtime
artifact directory. The ready process inventory contained 76 modules and **zero CryEngine
modules**. The captured player stayed at World `(10388.599609375, 15982.5, 366.3350830078125)`;
zone origin `(9,14)` maps this to local `(1172.599609375, 1646.5, 366.3350830078125)`.
The final build's physics status trace confirms `grounded=True` at that position.
This input contains stationary movement samples. A sustained real-client walk/jump/re-entry
session remains an acceptance check; replay alone does not prove that interaction.

## Remaining implementation tasks

### CE-06 — Authored static mesh collision (P0, in progress)

Implemented: bounded CGF 0x744/0x745 containers and authored physical readers for boxes,
inline and referenced triangle meshes, spheres, cylinders and capsules. Referenced proxies use
the precise Mesh0800 position/index streams, serialized foreign-triangle selection, vertex remap
and source subset materials. A display mesh is never substituted for missing collision data.
Curved proxies retain analytic surfaces; they are not tessellated. Character query capsules still
use conservative bounds and remain CE-07 work.

Static instances share immutable resources and mesh BVHs, with scene buckets pruning candidates.
Analytic rays and convex-support overlap/sweep queries compose with existing triangle queries.
Curved overlap/sweep contact uses a declared 1 mm separation tolerance; grazing time-of-impact
error is not bounded to 1 mm of travel. Numerical convergence failure is explicit. General
triangle meshes preserve empty interiors; authored boxes retain their solid convex volume.

Native root selection, export flags, helper/LOD/occlusion/aimpoint admission, the `main` mesh rule,
default vs picking profiles and signed material mappings are preserved. Direct-root physical
geometry uses identity instead of the render node TM. Independently admitted helpers retain
their node transforms. Native direct bodies reject nonuniform brush scales when either Y or Z
differs from X by more than 0.01; compound bodies use the separate multipart path. Malformed
placements and unsupported data keep named diagnostics.

Validation: all 512 zone-129 object/big-object CGFs decode completely: 605 default physical parts,
including 41 referenced-source meshes and 48 analytic curved primitives. Their triangle resources
contain 149,434 authored faces. These are source counts; material variants and placements are
reported separately. The six-cell scene contains 6,317 character proxies (6,037 triangle instances
and 280 analytic instances) and 6,579 picking proxies across 5,456 brushes. No brush resource has
an unsupported decoded proxy. Another 229 brushes have picking-only collision, and 704 have no
admitted character or picking proxy, including the one native-rejected nonuniform direct placement.
Ray-only coverage is reported separately, and one failed placement does not mark later valid
placements of the same resource unsupported. The 119,728 other placements still require their
own owners; `FullSceneCollisionReady` remains false. All 247 standalone tests pass.

The production movement pipeline also rejects repeated client pose corrections through a wall,
publishing the blocked authoritative pose back to World. Explicit scene teleports bypass that
ordinary correction path. A final six-cell host replay admits player 387, confirms it grounded
at local `(1172.5996,1646.5,366.33502)`, then retires player and terrain owners on clean World EOF.
The host uses no engine/Lua paths; module-inventory tests and the real-scene inventory confirm
no CryEngine modules. Runtime artifacts include `static-entry-final-zone.log`,
`static-entry-verified-modules.json` and `captures/static-entry-output-final.bin`.

The referenced/curved build also replays the real entry, confirms player 387 grounded at that
same local position, and retires on clean World EOF. Its inventory again contains 76 modules
and zero CryEngine/Lua modules. The measured zone exit code is 3, matching its configured
main-world disconnect policy. Evidence: `curved-entry-zone.log`, `curved-entry-replay.log`,
`curved-entry-modules.json` and `captures/curved-entry-output.bin`.

Independent rays through authored wall, roof, stair and bridge placements agree with the original
physics engine on all 21 hit/miss decisions and all 17 hit material IDs; the maximum distance
difference is 0.0000398 m. The comparison exposed and corrected the native adapter's omitted
64-meter deferred-sector offsets. Results are external runtime artifacts named
`static-native-probe-corrected-results.json`, `static-managed-probe-final-results.json` and
`static-parity-summary.json`. Backface normals retain authored winding; sweep normals oppose motion.
The maximum copied-position difference is 0.0001 m and maximum normal-vector difference is
0.0000276. These measurements cover the selected controls, not every decoded resource.

The expanded 108-ray oracle adds referenced meshes with nonidentity foreign-triangle maps,
source-subset materials, direct-root transforms, uniform/nonuniform admission and curved
entry/inside-exit/near-tangent controls. All 103 controls outside the five nominal exact-tangent
diagnostics agree on hit/miss and all 74 hit material IDs. Maximum distance difference is
0.000502 m, copied-position difference 0.000633 m and normal-vector difference 0.00442.
The five exact-tangent diagnostics remain in the output: two have different native/managed
hit decisions and one shared hit differs by 0.002448 m in travel. Those are unresolved native
float-transform/intersection conformance cases, not successful parity checks. Nearby scene
proxies also intercept some rays; independent source evidence identifies those controls.
Artifacts: `curved-native-probe-results.json`, `curved-managed-probe-results.json`,
`curved-parity-summary.json` and `research/static-final-scene-probe-manifest.json`.

Use `--check-managed-scene --config zone.json --collision-report coverage.json` to inspect actual
coverage. Both scene checks accept `--scene-probes rays.json --scene-probe-output hits.json`.
Each input ray has `name`, three-component `origin` and three-component `displacement` arrays in
zone-local meters. The native comparison uses `--check-scene` and the native configuration.

Remaining: vegetation and entity collision owners, visibility-imported geometry, water/procedural
owners and complete scene-instance admission. Exact nominal tangencies remain sensitive to native
float transforms and intersection rounding; keep those controls visible rather than declaring
bit-exact conformance. The current catalog's `BrushCoverageComplete` reports decoded/admitted brush
coverage only. See [CGF format evidence](../cgf-physics-format.md).

`ZoneManagedSceneActivation.Report.Cells[*].Objects` retains the remaining vegetation/entity
descriptions. Extend the existing resource cache and instance index with their verified physical
owners and the still-unsupported proxy formats. Retain exact material IDs and transforms, with
missing or unsupported resources visible in the per-resource coverage report.

Acceptance: rays, sweeps and overlaps hit a known zone-129 wall, roof, stair and bridge at the same
positions/materials as recorded native queries. A terrain miss under a bridge must still hit its
mesh. Walking into a building must stop. Unloaded instances must immediately stop colliding.
Do not use render bounds as if they were authored collision geometry.

### CE-07 — Character collision and movement fidelity (P0)

Replace conservative capsule bounds with true capsule queries. Complete stance transitions,
authored gravity and slope/step limits, penetration recovery, fall/landing state and collision
surface response. Preserve the added static ceiling/sliding/support behavior and ensure ordinary
client pose corrections cannot bypass the physics scene. Explicit scene teleports retain their
separate authoritative path. Preserve the single movement authority and World coordinate boundary.
Extend movement for swimming, gliding, ladders, mounts and moving platforms as their owners land.

Acceptance: recorded stance sizes and jump arcs, slope ascent/descent, stair climbing, a low
ceiling and a capsule passing a corner behave correctly; re-entry cannot reuse a retired body.
Run meaningful terrain and static-mesh cases rather than comparing an implementation to itself.

Native sweep/overlap acceptance needs a direct primitive-query oracle: the current native backend
adapter uses ray approximations and is not an exact shape reference. The original synchronous
`IPhysicalWorld` slot 65 (`+0x208`, target `35093100`) and its writable 96-byte request/contact ABI
have been recovered and PE-checked, but have not been executed as a shape parity check. The local
research profile is `research/native-primitive-world-query-profile.md`. Implement a narrow isolated
diagnostic, verify contact copying/material mapping and initial overlap behavior, then compare
the same authored surfaces with real box and capsule requests.

### CE-08 — Authored NPC behavior admission (P0)

Attach the existing NPC signal/actor/goal-pipe runtimes to managed provider actors. Resolve
authored AI file and initial behavior per NPC, construct the behavior stack, route signals and
provide the required managed script/entity API. Define a supported behavior set and report each
unsupported script/action. Remove the `HasAiActor` gameplay gate only for admitted behaviors
whose dependencies are present. Do not flip it globally: several reachable leaves still refuse.

Acceptance: an authored idle/talk NPC executes its initialization and a named signal; a hostile
NPC runs its actual authored state transitions; missing behavior remains a named admission
refusal; removal, failure and re-entry release every actor/listener/target generation.

### CE-09 — Managed perception, navigation and locomotion (P0)

Wire authored navigation areas/shapes and path data into an owned query service. Add supported
path search, forbidden-area/capability checks, territory/leash predicates, line-of-sight against
terrain and static collision, perception/alertness, target selection and movement requests.
AI requests movement through the scene authority, never a second physics writer.

Acceptance: NPCs route around an authored obstacle, cannot path through a wall, lose line of sight,
return to their spawn territory and clear dead/retired targets. Exercise disconnected navigation
regions, unsupported traversal types and missing cell data with explicit outcomes.

### CE-10 — Entity components, areas and FlowGraphs (P1)

Replace the native entity-system services with generation-owned components and managed event
dispatch. Instantiate relevant authored entities and areas from retained placements. Port the
server FlowGraph node behavior into managed graph execution, including timers, references,
activation order and teardown. Start with zone 129's ServerOnly elevator and related area events.

Acceptance: elevator starts from authored graph input, moves its collision surface, carries a
player and retires cleanly; `ZWEnterArea`/`ZWLeaveArea` follow actual crossings; graph callbacks
cannot retain a removed entity or a previous scene generation.

### CE-11 — Script execution without engine libraries (P1)

Select and implement a compatible managed script execution/translation layer. Existing authored
scripts depend on Lua 5.1 plus integer behavior and game bindings; replacing the DLL requires
validating those semantics, script source resolution, reference lifetimes and error behavior.
Keep this independent from the native comparison host's `ZoneLuaRuntime`.

Acceptance: execute representative authored AI/entity scripts with the required number/string/
table/metatable/coroutine semantics, install their used game bindings and reproduce signal/state
transitions. No replacement may silently skip a failed script and claim behavior readiness.

### CE-12 — Dynamic bodies, vehicles and animation inputs (P1)

Add the server-relevant physical proxy and skeleton data readers, attachments/bonds, moving
doodads/gimmicks, water/buoyancy, rigid bodies and vehicle/boat controllers. Separate authored
geometry loading from the dynamic solver. Resolve motion driven by animation and collision events
that gameplay consumes; headless operation does not make those inputs optional.

Acceptance: reference ship/slave/transfer cases, moving platforms, attachment removal and water
entry/exit work without a native body; the backend capability report tracks each supported class.

### CE-13 — Deployment and World launch cleanup (P1)

Make managed mode the documented deployment path, retain an explicit native comparison example,
and fail clearly on a native-only diagnostic command with managed configuration. In the separate
World repository, remove the `ZoneHost.NativeDll` existence preflight when launching this managed
executable. That World change is external to this repository.

Acceptance: run from a clean directory containing the published .NET application and configured
assets/content only, with engine directories absent and no engine paths on `PATH`; World launches
and restarts both a continent and an instance zone. Module inventory stays engine-free.

### CE-14 — Replacement parity suite and final native deletion (P1)

Grow recorded geometry/AI/graph/movement cases alongside the replacements. Compare against
recorded original results or the explicit native comparison host, retaining resource coverage and
known differences. Include walk, jump, logout/re-entry, multiple actors, NPC combat, objects and
long-running tick/retirement checks. Only remove `Native/*`, native diagnostic modes and native
deployment inputs once the corresponding capabilities have independent replacement evidence.

Acceptance: all required server scenarios run with no engine DLLs installed; capability reports
have no unowned prerequisite for the accepted gameplay scope; removing comparison code does not
change World wire behavior or invalidate recorded parity checks.
