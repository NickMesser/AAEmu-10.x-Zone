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
| `cryphysics_dedicate.dll` | True upright capsule/cylinder queries, authored dimensions/dynamics, queued stances, requested/actual velocity ownership, mapped slope/air/landing response, no-platform network corrections, terrain/static recovery, shared-mesh BVHs, affine instances and material IDs | Complete stair/suspension and retained support behavior, native recovery fallback/contact state, world speed caps, friction, rigid bodies, swimming, vehicles, ragdolls and exact tangent conformance |
| `cryentitysystem_dedicate.dll` | Player/NPC generations, registry/spatial membership, model-class generation, original entity-script initialization, owned entity timers and script updates | Character slots/attachments, collider-event receivers, general components, areas and complete entity state |
| `cryaction_dedicate.dll` | Player movement/frame pipeline, real managed goal/control phases, authored behavior construction and signal bindings | Complete NPC service admission, game-object actions and FlowGraphs |
| `cryaisystem_dedicate.dll` | Provider/actor/proxy ownership, signals, scheduled NPC AI timers, tiled authored graph import, bounded A*, physical query adapter and wish-only path follower | Full Puppet parameters, perception, physical NPC route admission, steering, leash and combat decisions |
| `cryscriptsystem.dll` | Managed source VM, LNUM compatibility transforms, original 73-file NPC bootstrap and explicit game bindings; no native Lua DLL | Full Lua 5.1/bytecode/library compatibility and remaining actor/game APIs |
| `cryanimation.dll` | Authored model/stance dimensions and movement parameters are consumed as data | Server-relevant skeleton collision, animation events and motion driven by animation |
| `cryrendernull.dll` | Managed host is headless and never constructs a renderer | No renderer is required; retain checks that no rendering service enters the server path |
| `tbb*`, `xlcommon.dll`, `xldiag.dll`, engine CRT dependencies | Engine startup is bypassed entirely | Remove native comparison deployment dependencies after replacement coverage is sufficient |

New production owners are `ZoneManagedSceneHost`, `ZoneManagedSceneActivation`,
`ZoneManagedTerrainGeometry`, `ZoneManagedPhysicsBackend`, `ZoneManagedAiProvider`,
`ZoneManagedNpcRuntime`, `ZoneManagedNpcActorScene` and `ZoneNavigationTiledGraph`.
They reuse existing content readers, unit/NPC registries, World protocol,
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
  collision. Missing cells, unloaded cells and hole slot 31 provide no floor. The initial milestone
  used conservative capsule bounds; CE-07 now replaces them with upright capsule geometry.
- [x] **CE-04 — Managed AI ownership and execution seam.** Fresh behavior state per generation,
  exclusion across managed/native providers, deterministic wrapped-clock scheduling, target
  retirement, callback-safe cancellation and disposal, and an adapter to existing goal pipes.
  The production host loads the original source and admits authored construction only when a
  supported actor profile is supplied; known incomplete profiles refuse before expensive work.
  Signal-ready actors can consume their scheduled AI timers independently of the still-closed full
  gameplay gate. Remaining service dependencies retain named admission refusals.
- [x] **CE-05 — Evidence without engine modules.** Synthetic host integration checks join,
  readiness, player landing/movement, clean World EOF and player-before-terrain retirement while
  inspecting loaded modules. A separate real-content zone-129 replay also reaches readiness,
  admits captured player 387 with `physicalId=0`, emits movement/location reports and retires.

The first native-free milestone passed 172 standalone tests, including 31 new configuration,
physics, AI and scene/host cases. The static/reference/curved milestone passed 247 tests; the
character-shape implementation passed 272 standalone tests. The overlap/support and corrected
terrain implementation passed 329; the current stance/movement slice passes **411 standalone tests**.
The NPC scripting/navigation slice passes **500 standalone tests**. The solution builds
successfully; analyzer warnings remain.

The final source-VM/tiled-navigation zone-129 replay also admits player 387, grounded at local
`(1172.5996,1646.5,366.3365)`, emits three movement reports and retires on clean
World EOF. Its readiness inventory contains 80 modules and zero CryEngine/native Lua DLLs.
The replay listener exits 0 and the zone exits 3 under its configured disconnect policy.
Evidence: `script-entry-{zone.log,replay.log,modules.json,verification.json}` and
`captures/script-entry-output.bin` under the runtime artifact directory. Cold burst processing
in this control takes about 107 seconds end to end; profile startup/receive throughput separately
before claiming production performance parity.

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
Curved proxies retain analytic surfaces; they are not tessellated. True character capsules and
the authored initial living dimensions are implemented under CE-07.

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

The native player bit also has child-only `colltype_player` selection and `no_player_collide`
property rules that need generic admission support. Independent inspection found no such names
or properties in the current 512-resource catalog, so the player-mask shape controls below are
not affected. Direct-root registration and object-quality-dependent child flags must remain
distinct when those rules are ported.

`ZoneManagedSceneActivation.Report.Cells[*].Objects` retains the remaining vegetation/entity
descriptions. Extend the existing resource cache and instance index with their verified physical
owners and the still-unsupported proxy formats. Retain exact material IDs and transforms, with
missing or unsupported resources visible in the per-resource coverage report.

Acceptance: rays, sweeps and overlaps hit a known zone-129 wall, roof, stair and bridge at the same
positions/materials as recorded native queries. A terrain miss under a bridge must still hit its
mesh. Walking into a building must stop. Unloaded instances must immediately stop colliding.
Do not use render bounds as if they were authored collision geometry.

### CE-07 — Character collision and movement fidelity (P0, in progress)

Implemented: true upright capsules and cylinders against authored terrain, triangle proxies,
analytic targets and registered actor bodies. Bounding boxes only select candidates. Existing
explicit query boxes retain their SAT/slab behavior. Independent controls cover rounded corners,
triangle vertices, caps, actor pairs, affine targets, degeneracy and coordinates near 8000.

Managed player admission now uses the recovered initial stance selection and native scaling:
radius/segment dimensions, cylinder vs capsule, collider-center offset, ground pivot, cubically
scaled living mass and signed authored gravity. Ground height remains on the physical surface;
the entity origin settles at ground plus pivot. A TCP admission test verifies authored dimensions
and nonzero pivot through movement replication. A separate jump test combines authored takeoff
speed and gravity to reach the authored jump height within the integration step's error.

The captured player object 387 resolves to model 17 / actor model 8 / stance 130, scale 1. Its
capsule radius is 0.4, center-segment half length 0.32, collider height 1.2, pivot 0 and living mass
80. Authored scalar gravity 25 becomes acceleration -25. The previous feet-plus-half-size center
and constant -9.81 acceleration did not represent this authored body.

The original synchronous shape API is now executed through an isolated diagnostic: `IPhysicalWorld`
slot 65 (`+0x208`, target `35093100`), a writable 96-byte request and immediately copied contacts.
It selects static player-collidable parts with required/any flags 2/2. Raw return, raw normal and
copied distance/material/entity/part remain visible. Common sweep normals explicitly reverse the
original query/body contact orientation. Unknown protocol outcomes fail the diagnostic.
The old native backend adapter's ray approximations remain unsuitable as an exact shape oracle.

Use `--scene-shape-probes shapes.json --scene-shape-probe-output results.json` with either scene
check. The input distinguishes raw primitive centers from actor origins, and zero sweep requests
overlap. See [shape conventions, source evidence and numerical limits](../managed-character-physics.md).

Validation includes 13 new capsule/convex cases, seven authored-living cases, four pivot/gravity/jump
cases, a native ABI layout guard and the TCP host regression. All 272 standalone tests pass and
the full solution builds. The original 108-ray scene output remains unchanged. The real-content
replay admits player 387 with capsule center offset 1.2, pivot 0 and gravity -25, confirms it
grounded, then retires in order on World EOF with the expected exit code 3. Its ready-process
inventory contains 76 modules and zero CryEngine/Lua modules. Artifacts: `capsule-entry-zone.log`,
`capsule-entry-replay.log`, `capsule-entry-modules.json` and `captures/capsule-entry-output.bin`.

The exact native shape suite executes 132 box/capsule/cylinder requests. All 96 separated,
clear/intersecting-overlap and outward-motion controls match hit/miss; all 24 positive separated
sweeps match materials. Maximum distance difference is 0.000560 m and common-normal vector
difference is 0.004053. Three of 12 nominal exact-touch overlaps differ within the declared contact
band. The 24 inward initial-contact/penetration controls expose seven hit/miss differences and
shared-hit travel differences up to 0.508648 m: the original raw query can skip an initially
intersecting face and select a later surface, while managed blocking queries currently stop at
zero. Overall hit decisions match in 122/132 cases; this is not full sweep/controller conformance.
The complete outputs retain every residual. Artifacts: `native-character-shape-results.json`,
`managed-character-shape-results.json`, `character-shape-parity-summary.json` and
`research/native-oracle-character-shape-probes.json`.
The [132-request corpus and original native results](../../tools/AAEmu.Zone.Diagnostics/Fixtures/README.md)
are also checked into the repository, including the unresolved controls.

Implemented next: copied penetration depth/direction/witness/material against true query shapes,
static triangles, whole closed convex proxies, affine analytic targets and terrain top surfaces.
The controller stages recovery before ordinary movement, adds the original 0.01 m clearance,
uses the verified per-query 2.5-radius bound for authored living bodies and preserves velocity
during early static correction. Deterministic ambiguous choices are reported. Finite/cycle checks
and a replacement 96-pass numerical bound fail before committing staged pose/velocity; that work
bound is not claimed as a native constant. Ground support can also repair an initially buried foot
pivot within the original `HeightCollider - segmentHalfHeight` limit, without recentring its collider
or adopting distant overhead terrain. Explicit teleports immediately invalidate cached support.

Independent controls cover all nine box/capsule/cylinder actor pairs, triangle planes/corners,
contained solid volumes vs empty mesh interiors, nonuniform/sheared/mirrored affine targets,
terrain curved footprints and coordinates near 8000. Controller controls cover wall recovery,
outward/sliding motion, ordinary pose corrections, zero-motion contact clearance, ties, atomic
failure, generation retirement, unload, explicit generic box profiles and authored buried-pivot
support, airborne state and the strict authored support boundary near Z=8000. Exact support-bound
certificates also resolve the reviewed near-spherical diagonal-target EPA exhaustion cases without
changing iteration limits or tolerances; uncertified cases retain bounded EPA and explicit failure.
That slice passed 329 standalone tests. The 108-ray output and all previous result fields of the 132 raw
shape controls remain
unchanged, including their known residuals.

The real-content recovery replay again admits player 387 grounded at
`(1172.5996,1646.5,366.33502)`, with authored center 1.2/pivot 0/gravity -25, and retires before
terrain on World EOF. Listener exit is 0 and zone exit 3 follows its configured disconnect policy.
The ready process has 76 modules and zero CryEngine/Lua modules. Artifacts:
`recovery-final-entry-zone.log`, `recovery-final-entry-replay.log`,
`recovery-final-entry-modules.json` and `captures/recovery-final-entry-output.bin`.
The module snapshot was taken after the grounded player status was observed. Final corpus
verification is recorded in `recovery-final-verification-summary.json`.

The original-body diagnostic now verifies position with the original 72-byte type-0 parameters,
submits a complete 24-byte type-1 velocity action, steps the actual living list with world flag 8
and copies pose, velocities, dimensions, support and flags. Flag 0 only advancing world time is
not recovery evidence. The former position-labelled 20-byte velocity action was invalid and has
been removed; the native production pose adapter remains a named refusal. Diagnostic bindings
remain isolated from managed startup and production movement ownership.

The checked-in [living and terrain fixtures](../../tools/AAEmu.Zone.Diagnostics/Fixtures/README.md)
retain 32 base living cases, ten followups (38 distinct living cases overall), 54 terrain-only
primitive queries and their original copied results/control attribution. Actual living-list
membership and elapsed body time establish that those bodies were stepped. Idle, moving,
gravity-only and tiny-request controls distinguish the original early idle shortcut from raw
query behavior. The later stance/movement slice implements the mapped static/terrain idle gate
and movement state. Retained support and the full prior stuck-contact producer remain open.

The terrain oracle corrected two earlier assumptions: deeply submerged shapes do not overlap the
terrain, and upward shape sweeps can hit its two-sided top triangles. Physics rays and shape queries
now sample all four global vertices through their individual current-leaf scalar heights;
renderer/GetHeight coarse-quad behavior is unchanged. Native shape materials pass through a signed
triangle byte (negative values fall back to fresh terrain part index 0), while rays retain full
integer map indices. Controls independently verify this positive alias; the negative branch has
source and standalone-test evidence but was not executed in the recorded native window.

All 54 terrain hit/miss decisions now agree, and all 12 shared positive sweep material IDs agree.
The six downward sweeps have maximum travel difference 0.000628 m and normal-vector difference
0.000000131. The six upward crossings retain adjacent-face selection differences: maximum travel
difference 0.004911 m and normal-vector difference 0.060844. No tolerance was widened to hide those
residuals. External comparison artifacts are `managed-terrain-shape-corrected-results.json` and
`terrain-corrected-parity-summary.json`.

Implemented next: the authored stance catalog maps content IDs 1–9 to actor slots 0–8 and retains
the initial slot-1/content-2 distinction. Desired, accepted-current and applied-body states are
separate. Frame prephase retries refused requests and publishes accepted stance/speed. The physical
queue preserves both original acceptance divergences: false preflight can apply later, and true
preflight can be refused at drain. It retains raw primitive versus clamped support height, old
pivot/kind preflight, the strict other-living shortcut and float operation order near coordinate
8000. Positive-Z geometry exits are independent of normal fallback. Entity admission uses multipart
physics bounds rather than render bounds; export padding and native contact ordering remain open.

The living controller now consumes authored slide/climb/jump/fall angles and exact native defaults,
inertia/acceleration selection, air control/resistance, gravity and landing coefficients. It keeps
wish and actual collision velocity separate, probes the proposed endpoint, sweeps with the original
clearance extension and resolves support before slope/air response. Mode 0 requests, mode 3's
one-step override and its persistent flag are distinct. The mapped static/terrain idle shortcut,
suspension fields and pre-idle impulse timer aging are owned. Actor input supplies normal-mode
wishes and the recovered GameInertia flag update; animated movement-control modes remain separate.
Static contacts now use the original actual-velocity gate and capped impulse, independently of
displacement sliding. The non-tiny post-movement integrity pass restores an intersecting pose and
can block/clear actual motion. Blocked grounding and a real support hit remain separate states;
the copied missing-floor value stays -1e10. Controller state commits with its validated owned body.

Validation: the full solution builds with 38 existing warnings and no errors; all 411 standalone
tests pass. Five new original captures retain 66 movement/stance/correction cases and 1,147 world
steps. From the same copied frame-zero poses, all 1,229 managed body frames match flying state,
flag 0x2, applied dimensions and requested velocity exactly. All 42 dimension returns and six
network-correction returns match. All six correction cases match pose, actual velocity, wish,
flags and immediate-versus-queued timing exactly. Maximum actual-velocity difference elsewhere
is 0.0000006 m/s. Four growth/kind-switch controls retain up to 0.0011 m position difference;
the restrictive-fall controls retain up to 0.000544 m. These are measured residuals, not widened
passing tolerances. The checked-in [comparison summary](../../tools/AAEmu.Zone.Diagnostics/Fixtures/zone129-living-controller.managed-summary.json)
retains every case and source hash. Creation admission, native impulse-timer numerical values,
moving-support state and complete gameplay behavior are not established by this corpus.

The 108 ray results and all prior fields of the 132 static-shape controls remain unchanged.
All 54 terrain hit decisions and 12 positive sweep materials still agree; the existing adjacent
terrain-face travel/normal residuals remain. External verification is recorded in
`living-final-geometry-verification.json` and `research/living-controller-final-comparison.json`.

The current stationary entry replay admits player 387 at local
`(1172.5996,1646.5,366.3365)`, reports grounded, publishes accepted slot 1 and retires players before
terrain. Its live inventory has 76 modules and zero CryEngine/Lua modules. Listener exit is 0 and
zone exit 3 follows the configured World-disconnect policy. This run uses the existing
`parity-entry-input.bin` derivative, whose three player movement samples request slot 1; it is
distinct from an untouched original-client capture. Artifacts are `living-final-entry-zone.log`,
`living-final-entry-replay.log`, `living-final-entry-modules.json`,
`living-final-entry-verification.json` and `captures/living-final-entry-output.bin`.

Network correction is now a copied tuple through the verified physical virtual 0xC0, not reset
Action 4 or generic pose/mode-3 writes. Velocity/request fields update immediately and position
queues; rotation and movement mode survive. Same-ground requests require a retained support owner
and otherwise visibly refuse. The managed drain retains its traversed-correction guard, so exact
native collision policy and cross-type queue order are not claimed.

Terrain early recovery now uses positive authored face-normal depth, gated by an actual finite
triangle intersection. Coplanar seams merge without internal sidewalls. At a real boundary it is
a conservative surface constraint; native edge selection remains open. Fixed upward stance depth
is separate. The 2.5-radius parameter is a native per-geometry solver bound with fallback paths,
not proof of universal rejection. Managed out-of-range contacts are filtered before deepest
selection so they cannot abort a whole actor step or hide an eligible neighbor.

Remaining: arbitrary character rotation, full stairs/suspension and retained supports, native world
speed caps, exact recovery/contact order and fallback, prior stuck-contact state, animated movement
control and friction/elasticity. The authored `IgnoreCollision` callback/state path has no managed
owner. Some stances put the collider below the pivot plane and can hover with preserved cached
state; retain the actual dimensions. Swimming, gliding, ladders, mounts and moving platforms need
their own owners. Full feature parity remains false.

Remaining acceptance: complete stance/jump behavior across gameplay movement states, stairs,
moving support and native recovery/contact-state conformance, plus
a sustained real-client walk/jump/logout/re-entry session. Native-free replay and isolated geometry
controls alone do not establish complete living-controller parity.

### CE-08 — Authored NPC behavior admission (P0, in progress)

Implemented: the original 73-file NPC source bootstrap, actual AI character/behavior lookup,
database parameter closures, source-ordered database model-class generation, cached original
`npc:Create`, original `common.new`, server initialization, character and spawning constructors.
The composition supports real actor/goal/proxy owners and unwinds refused generations.
Production refuses its incomplete actor profile before model-class compilation or per-entity
cloning/initialization. Repeated known-failing attachment calls caused receive backlog
in the first real-entry control; unsupported profiles must not block player admission.
Entity IDs and proxy keys are allocated independently of World/NPC IDs. Signals and original
scheduled AI time events use exact script ownership; the full gameplay gate remains closed.

Original-script controls for five NPC profiles match all **657/657 recorded API calls**, parameters,
callback results and tested state transitions in the native and managed VMs. Their declared
generic/recording fixture does not establish C++ NPC gameplay parity. Separate production bootstrap
uses real managed goal/track/combat catalogs. See [runtime evidence and contracts](../managed-npc-runtime.md).

Entity timers, independent constructor health fields, default-state script updates and both update
intervals now have real owners. Timer callbacks use the original due-batch and argument rules;
faults retire their exact provider generation. Native dedicated `SetMovementTarget` is verified empty
after argument decoding and is implemented accordingly.

- [ ] Own actual character-slot/attachment inventories. Implement present attachment effects;
  only a real absent lookup may take `HideAttachment`'s native empty branch.
- [ ] Dispatch entity event 43 through owned receivers. Implement animated collider request layers
  and arbitration before physics writes; absent physics alone cannot justify ignoring the event.
- [ ] Complete Puppet parameters, persistent stick, indexed reference/look objects, smart-object
  state, actor-health update producers, script-state switching and event-44 interval selection.
- [ ] Attach posture, casting, buff, combat and position-check services used by each admitted
  behavior. Unsupported operations must remain per-owner refusals.

Remove the `HasAiActor` gameplay gate only for admitted behaviors whose dependencies are present.
Do not flip it globally: several reachable leaves still refuse.

Acceptance: an authored idle/talk NPC executes its initialization and a named signal; a hostile
NPC runs its actual authored state transitions; missing behavior remains a named admission
refusal; removal, failure and re-entry release every actor/listener/target generation.

### CE-09 — Managed perception, navigation and locomotion (P0, in progress)

Implemented: all 96 zone-129 streamed `paths/%03d_%03d/{areas,verts,net}mission0.bai` triplets,
native tile translation, XY vertex welding, directed paired adjacency, modifiers and forbidden
areas. The combined graph has 146,696 nodes, 231,626 paired links and 69,539 vertices. Missing
modifiers/traversal types and unavailable tiles keep explicit outcomes and diagnostics.

Bounded A* uses actual graph clearance and forbidden/coverage checks. Copied paths belong to
one publication; retirement invalidates them and synchronously clears followers' queued wishes.
The physical adapter queries applied living bodies, terrain/static support and full-body sweeps
with caller-certified coverage. The follower requests horizontal velocity through the existing
movement owner and never steps physics or writes a pose. Its search cost, support/clearance and
following policies remain explicit replacements, not recovered X2 walkability/steering parity.

- [ ] Register actual NPC physical bodies and connect the generation-owned motor to the scene's
  single movement publication authority. Production physical route admission remains gated.
- [ ] Supply truthful full-region terrain/brush/entity/vegetation coverage to route and visibility
  queries. Missing owners cannot be represented as clear space.
- [ ] Recover/compare X2 walkability, native route costs, steering, stairs and moving support.
- [ ] Own perception/alertness, target acquisition, territory/leash, group routing and return policy.

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

### CE-11 — Script execution without engine libraries (P1, in progress)

Implemented: a VM-neutral host boundary and managed source execution through pinned NeoLua,
with protected calls, exact method receivers, byte strings, references/callback ownership,
LNUM integer promotion/division/modulo/hex semantics, correctly rounded fractional literals,
and owned coroutine retirement. Original source loading uses the configured asset order.
Nested script failures and nonthrowing script error reports cannot claim initialization readiness.
The native comparison host retains its independent `ZoneLuaRuntime`.

- [ ] Implement original Lua 5.1 `.alb` bytecode or require proven source equivalents for every
  deployment script; current managed mode executes source only.
- [ ] Expand independent language/library controls: environments/debug APIs, implicit vararg
  `arg`, native RNG sequence, numeric loops and other still-uncovered edge cases.
- [ ] Resolve coroutine interaction with scene-thread-only services; the covered original NPC
  dependency closure contains no coroutine use.
- [ ] Finish the actual entity/game bindings listed in CE-08 and CE-10. A managed compiler alone
  does not establish gameplay parity.

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
The recorded character-shape corpus is now in the diagnostic project's `Fixtures` directory;
its known differences prevent it from being treated as a passing full-parity gate.

Acceptance: all required server scenarios run with no engine DLLs installed; capability reports
have no unowned prerequisite for the accepted gameplay scope; removing comparison code does not
change World wire behavior or invalidate recorded parity checks.
