# Managed character geometry and native controls

Managed mode uses authored living dimensions, queued stance changes and upright convex queries.
Its living controller separates requested and actual velocity and consumes authored movement
dynamics. It does not implement the complete native living controller.
The remaining work is tracked in [CE-07](tasks/cryengine-removal.md).

## Shape and origin conventions

`ZonePhysicsShape.Kind` distinguishes explicit query boxes, capsules and cylinders. The legacy
`(Vector3, bool)` constructor keeps `false` as an explicit box and selects a capsule for `true`.
For curved shapes, its compatibility property `HalfExtents` contains native parameters:

| Kind | X | Y | Z | Outer bounding half sizes |
| --- | --- | --- | --- | --- |
| Box | X half size | Y half size | Z half size | `(X,Y,Z)` |
| Capsule | Radius | Unused | Center-segment half length | `(X,X,Z+X)` |
| Cylinder | Radius | Unused | Axial half length | `(X,X,Z)` |

Use `BoundingHalfExtents` for candidate selection. A capsule with radius `0.4` and segment half
length `0.32` has total height `1.44`, not `0.64`. Zero capsule segment length is a sphere;
cylinders require a positive axial half length at public admission. The isolated stance preflight
also handles the original old-cylinder/new-sphere zero-height disk query. Query positions are
primitive centers.

The original `CLivingEntity::SetParams` at `35028BF0` uses `Size.X` radially and ignores `Size.Y`.
The authored `UseCapsule=false` living body is a cylinder. It is distinct from an explicitly
requested query box. `FromLivingStance` makes this distinction during managed player admission.

Initial admission reuses `ZoneActorPhysicalizationRequest.BuildInitial`: the last source-order
stance with content ID 2 wins, authored lengths scale by entity scale, and living mass scales by its cube.
When no such stance exists, the native constructor's unscaled default dimensions are retained.
Invalid sizes, mass, offsets and gravity are refused explicitly.

For an upright living body:

```text
collider center Z = entity origin Z + HeightCollider - HeightPivot
ground reference Z = entity origin Z - HeightPivot
resting entity origin Z = physical ground Z + HeightPivot
```

The last equation is verified in the original living step at `3502DED0`. The collider bottom is
independent of the ground reference. Recentring the shape on its bottom would change authored
geometry. The owned integrator queries the ground reference but keeps the reported ground height
on the physical surface. Existing ground providers retain their zero-pivot behavior.

Native living dynamics negate the authored scalar gravity. The managed backend and owned
integrator use that per-actor signed acceleration consistently. Explicit descriptions retain the
legacy `-9.81` default; managed authored admission reads the actual model value. Jump input already
derives takeoff speed from the same authored gravity and jump height.

The captured player object 387 is model 17, actor model 8 (`elf_female`), scale 1. Stance 130 has
radius `0.4`, segment half length `0.32`, collider height `1.2`, pivot `0`, mass `80` and authored
gravity `25`. Its signed acceleration is therefore `-25`; its collider bottom is `0.48` above
the entity origin. These are its initial dimensions: wire stance 2 selects swim/content ID 3,
which has different authored dimensions. Object identity, actor-model identity, content stance
ID and wire slot must not be conflated.

## Authored stances and queued dimensions

`ZoneActorStanceCatalog` reproduces constructor `392DF300`: content IDs 1 through 9 map to slots
0 through 8, source order decides duplicates and initial slot 1 selects content ID 2. Missing
slots retain the unscaled constructor cylinder. Wire -1 supplies no new request. Slot 9 has a
getter boundary but no initialized stance storage, so it remains an explicit refusal.

The actor retains desired and accepted-current slots separately. Frame prephase retries a refused
desired change; outgoing stance and movement limits use accepted current. The backend separately
owns applied physical dimensions. Those three states can differ in the original implementation.
`SetParams(-1)` queues before preflight: a false return can still apply later, while a true
preflight can be refused during application. Removal retires queued requests for the exact
generation. Dynamics changes made before drain survive the dimension update.

Preflight probes changed collider height, radius and raw half height using the old pivot and old
primitive kind. Queue application also considers the new pivot and kind. The helper moves along
positive Z, multiplies each selected depth by 1.01, preserves the eight-query counter ordering and
accepts equality at the request's maximum shift. Fixed-direction exit and normal fallback are
separate geometry results. Candidate selection uses the native entity envelope and multipart
physics bounds, including bound-only obstruct parts, rather than render bounds or clipped triangles.
Native cross-entity/contact order and exported bounding-box padding remain unresolved.

Other-living preflight uses strict radial and segment-height overlap, excluding capsule cap radius
from its vertical shortcut. Its Z arithmetic retains `(hc + originZ) - hp`; reassociation changes
strict decisions near coordinate 8000. Actual collider placement still uses `originZ + (hc - hp)`.
The raw primitive half height and the separately clamped support half height are retained. A
request that compares equal to the clamped support height does not rebuild the raw primitive.

## Living movement ownership

`ZonePhysicsLivingMovementSettings` copies initial dimensions and dynamics, including the native
unused-float sentinel and exact constructor defaults. Authored angle degrees use the original
double-cosine conversion; explicit zero angles remain zero. Flat ground therefore fails the
strict grounded angle test when minimum-fall angle is explicitly zero. Maximum support velocity
limits a retained platform's velocity; it is not a walking-speed cap.

The living motor snapshots prior state, applies admitted early recovery, prepares actual velocity,
probes the proposed endpoint, sweeps the combined displacement, then resolves support and velocity.
It owns strict support-rise/descent, slide/climb/fall comparisons, inertia selection, air control,
gravity/resistance and landing/suspension fields for admitted terrain/static controls. Gravity
changes velocity after movement. Sweep queries extend travel by the world's 0.01 m clearance and
subtract that clearance from the selected hit distance. Generic kinematic bodies retain their
existing integrator.

Mode 0 writes a persistent requested velocity. Mode 3 also sets a one-step actual override and
persistent flag 0x2; consuming the override does not clear that flag. Frame-shaped living input
uses mode 0 and preserves its intended Z instead of copying gravity or a slope's actual velocity
back into the wish. Collision-active false is a separate original dynamics state, with its own
translation/status behavior; it is not the authored `IgnoreCollision` gameplay flag.

Static contact response has a separate angle/up-speed gate. It uses the native 1.01 impulse and
strict magnitude cap, which can halve a large inward response; a full normal projection is not a
universal actual-velocity update. Displacement sliding is separate. After non-tiny movement, the
integrity pass checks positive-Z unprojection at the resulting pose. Any admitted contact restores
the pre-sweep origin; depth above world clearance also blocks and clears actual velocity before
grounded response. A blocked body can report grounded with no real floor hit. This is kept distinct
from cached support, including the original `-1e10` missing-floor value.

The owned body remains the only transform authority. Controllers return staged pose/velocity and
copied support geometry; finite values, normals and exact body lifetime are checked before commit.
Legacy height-only support explicitly reports an unknown normal. Reentrant writes and steps are
refused, and removal/replacement during a query cannot commit a retired body's result. New bodies
wait until the next frame snapshot. Suspension and timer changes are staged alongside each
controller result and publish only after that body's validated commit. Invalid forced correction
travel is refused before immediate velocity fields or pose queues change.

Network corrections preserve the original physical boundary: X2 producer `392440E0` calls living
virtual `0xC0` / `3502AA60`. Its type-4 tuple is distinct from reset Action 4. It queues a bounded
position correction, immediately writes actual velocity XY, optionally copies original requested
velocity, then writes filtered Z to both actual and requested Z. It preserves rotation, flag 0x2
and the one-step override. The radius/speed band and near/far/forced position branches are retained.
The actor forwards the copied tuple once; it does not decompose it into generic pose and mode-3
velocity writes. A same-ground request without an owned retained support entity returns a visible
refusal. Managed pose drain keeps the existing collision traversal guard, an explicit replacement
policy rather than exact native flags-49 type-0 collision behavior. Cross-type queue interleaving
and moving-support anchors remain open.

## Query algorithms and numerical boundary

Capsule/triangle and capsule/actor cases use exact center-segment distance where applicable.
Other convex pairs use analytic support functions with GJK distance and conservative advancement.
Terrain retains its authored top triangles, holes and loaded-cell boundaries. Shape overlaps
are surface-only and shape sweeps are two-sided, as verified by the terrain-only original oracle.
Static triangle meshes retain empty interiors; explicit closed box proxies retain volume.
Bounds prune candidates and never replace the capsule or cylinder surface.

Original heightfield preparation (`35155180` for shapes, `35158080` for rays) samples each of
the four global grid vertices through its own scalar height callback. Managed physics does the
same with current loaded leaves, independently of the renderer's single coarse-quad expansion.
Unloading a cell invalidates collision immediately; compatibility render caches are unchanged.

The original shape mesh stores each full terrain surface index as a signed byte. Material
mapping (`35037930`) first replaces a negative index with the fresh terrain part's default index
0, then resolves the map. Rays retain the full integer index. This can produce different shape
and ray materials at the same location: a verified control has full index 513/material 103,
but its shape contact aliases to index 1/material 105. The managed terrain provider preserves
this distinction. The negative-byte fallback is verified from source; the recorded 54 controls
exercise positive aliases, not that negative branch.

Existing explicit box triangle SAT and actor-pair slab behavior is retained. Degenerate triangles
remain noncollidable for every shape kind. Curved queries use a 1 mm separation/contact band,
a 0.05 mm distance convergence target and a 0.5 mm advancement target. This does not bound travel
error to 1 mm at grazing contact. Numerical convergence failure is explicit. A touching shape can
move tangentially or away. Initial penetration recovery is a separate controller pass, described below;
it does not change the raw primitive sweep's initial-contact policy.

Standalone controls include capsule corner/edge rejection, independently calculated vertex sweep
distances, capsule vs cylinder caps, actor-pair geometry, affine analytic targets, coordinates near
8000, terrain holes/unload and preservation of exact box boundaries. Pivot, gravity, authored jump
height and TCP player admission/replication have separate integration controls.

## Original synchronous shape oracle

The native comparison host exposes the original `IPhysicalWorld` slot 65 (`+0x208`, implementation
`35093100`), independently of its older ray-based backend sweep/overlap approximations. The new
entry is validated against the original binary identity and vtable target before invocation.
It runs under the existing owner-thread/engine lifetime gate and copies borrowed contacts before
any subsequent native call. It is not constructed or invoked by managed scene startup.

The request is 96 bytes: primitive type at 24, writable primitive pointer at 32, sweep vector at
40, object mask at 52, contact-output pointer at 56, required/any geometry flags at 64/68, optional
intersection parameters at 72 and skip count/pointer at 80/88. The default profile uses static objects
and required/any flags `2/2` for player-collidable geometry. Explicit `target: "terrain"` selects
object mask `0x100` with the same geometry flags; dynamic bodies remain excluded.
It supplies no queue flag, callbacks, skip list or retained native pointers.

A nonzero sweep returns travel distance. Zero sweep selects overlap and returns a contact count.
The copied contact has double distance at 0, point at 8, normal at 20, unprojection direction at 32, mapped material at 56,
entity identity at 60 and part identity at 64, in a 136-byte record. The diagnostic inspects copied
contact storage as well as the scalar return; it does not infer a hit from a non-null output pointer
or silently equate a zero return with a missing contact. A valid exactly-zero-distance native
contact has not yet been observed in the executed controls. Unknown return/contact combinations
are reported as indeterminate.

The original query intersects query geometry against body geometry. Its raw contact normal has
the opposite orientation to `ZonePhysicsHit`'s blocking sweep normal. Diagnostic output retains
`nativeContactNormal` unchanged and negates it for the common sweep `normal`; overlap normals are
not compared. Box contact points may differ within the same valid contact manifold.

Both scene checks accept:

```text
--scene-shape-probes shapes.json --scene-shape-probe-output results.json
```

Each input has `name`, `shape`, three-component `center` and `sweep` arrays. Box inputs provide
`halfExtents`; capsules provide `radius` and `segmentHalfHeight`; cylinders provide `radius` and
`halfHeight`. Optional `axis` defaults to Z. The native oracle accepts arbitrary unit axes, while
managed character queries explicitly admit upright axes only. An all-zero sweep requests overlap.
Optional `target` is `static` by default or explicitly `terrain`.
Malformed, unsupported, indeterminate or failed requests remain visible in the result and fail
the diagnostic instead of becoming clear-space misses.

Current runtime evidence and exact control counts are recorded in CE-07. They cover the selected
authored controls, not every possible primitive pair or native controller state.

The inward initial-contact controls expose a separate policy gap. The original raw sweep can
skip a face already intersecting the query, then report a later contact or no contact. Managed
blocking queries currently stop an inward move immediately at initial intersection. This is not
native penetration recovery, and matching separated sweeps does not establish that behavior.

## Managed overlap recovery and ground support

The controller now recovers initial overlaps before ordinary movement. The penetration query returns
copied depth, unprojection direction, witness and authored material, with no controller clearance
included. Exact analytic/SAT cases and a bounded convex EPA solve retain the real box, capsule,
cylinder, triangle or affine analytic surface. Closed convex proxies use the whole solid volume;
triangle meshes retain empty interiors. Verified coplanar patches consolidate internal seams without
filling outer edges, holes or gaps. Terrain recovery requires an actual finite top-triangle
intersection and uses its positive authored face normal. Interior-plane depth is the shape's
support extent minus signed plane distance. Coplanar seams merge; no internal triangle sidewall
is created. A wholly submerged body is not an early terrain-surface contact. At real boundaries
the positive face-normal result is a conservative surface constraint; native finite-edge choice
and global minimum translation are not claimed. Fixed positive-Z stance exits remain a separate API.

Nearly spherical diagonal affine targets with centered or exactly aligned query offsets can
otherwise exhaust smooth-hull EPA refinement. A separate analytic certificate handles cases where
an axis support attains the global bound `target minimum radius + query inscribed radius - center distance`.
It uses no near-diagonal/near-axis admission tolerance and adds no clearance. Independent controls
prove exact depth, material, witness, separation and residual penetration, including mirrored targets
near coordinate 8000. Cases that cannot attain this bound continue through bounded EPA; a numerical
exhaustion remains an explicit failure rather than a fabricated depth or miss.

The original living step at `3502DED0` corrects an admitted static contact by
`(depth + world.clearance) * direction`. The world constructor at `350827B0` sets clearance to
`0.01` m. Its geometry query supplies `2.5 * radius` as a per-geometry solver bound, not an
aggregate frame travel budget. Native geometry can enter fallback paths at that bound; the
parameter alone does not prove a universal depth-rejection rule. The managed bounded query
filters out-of-range contacts before selecting the deepest eligible neighbor, rather than
throwing away the whole actor step. Exact native fallback remains open. Explicitly constructed
`Kinematic` bodies retain their generic support contract and have no living radius limit.

Recovery stages the whole pose before committing, preserves velocity, orientation, collider offset,
pivot and gravity, and then lets ordinary sweeps/support resolve motion. The original static early
pass also preserves velocity; its separate dynamic-body response is not implemented. A replacement
96-pass numerical work bound, finite-coordinate checks and cycle detection produce named failures
without committing the staged pose or velocity. The original early pass has no proven equivalent
96-pass bound. Certified numerical residuals at or below `0.0001` m do not trigger another correction.
Nonunique minimum translations are selected deterministically and counted in the copied recovery
report; exact native contact ordering is not claimed.

Other living bodies still block ordinary sweeps and overlaps. They are excluded from the initial
recovery pass because the original admits them only when the previous low-byte living flag `0x80`
is set. The producer of that prior stuck-contact state remains unimplemented. This exclusion must
not be described as complete actor stacking or native contact-state parity.

The original also has an idle shortcut before this pass. It tests exact zero actual and requested
velocity together with flying/gravity, cached floor/normal, suspension fields, prior contact state
and a retained ground entity. A tiny nonzero request defeats it even if float coordinates cannot
represent the resulting travel. Managed living bodies now implement the mapped static/terrain
idle state, including copied floor/normal and suspension fields. Impulse recovery selects inertia
before aging its timer, even when the subsequent idle/inactive branch skips movement. Retained
support and the complete prior stuck-contact producer remain separate controller parity work.

The original helper at `3502CA20` starts its ray at
`foot + (HeightCollider - clampedSupportHalfHeight)` and casts down
`1.5 * (HeightCollider - clampedSupportHalfHeight) + 2`. The living motor probes the proposed
endpoint, then probes again after movement. A foot below real support rises only inside the
strict authored reach; a short downward transition also depends on prior grounded state and
flags. It preserves raw height, normal and material, including slope normals outside the legacy
generic walkable threshold. The dedicated living query bypasses the legacy kinematic snap policy.
Moving support filters and complete stair/contact behavior remain open.

Explicit teleports now immediately clear cached grounded state, height and material. Invalid
teleports leave the prior pose, velocity and support unchanged. Ordinary pose corrections still
traverse collision geometry and cannot carry the actor through a wall.
For the admitted active living profile, copied `IsFlying` now complements resolved grounded state,
matching the original active-body status convention. Free fall, landing, jump, teleport and handle
retirement have explicit state controls. This physics status is distinct from gameplay flight modes.

The isolated original-body diagnostic uses a verified 72-byte type-0 position parameter and a
24-byte type-1 velocity action. Type 1 is not a position write: the former truncated 20-byte
position-labelled native action and obsolete probe have been removed. The production native pose
adapter remains a named refusal; the diagnostic does not install another production movement owner.
World step flag `8` selects and steps the living list. Flag `0` advancing world time alone is
retained as negative evidence and does not demonstrate living-body motion or recovery.

## Remaining controller work

Arbitrary character rotation, complete stair/suspension and retained support behavior, native
world speed caps, recovery contact ordering/fallback and prior stuck-contact state remain open.
The `IgnoreCollision` callback/state path, friction/elasticity and movement control driven by
animation need their own owners. Some authored stances put the collider below the pivot support
plane and can hover after a successful resize; those dimensions and cached state must be preserved.
NPC behavior, swimming, gliding, mounts, moving platforms, vehicles and rigid-body dynamics remain
unfinished. Full feature parity remains false.

The checked-in [original control fixtures](../tools/AAEmu.Zone.Diagnostics/Fixtures/README.md)
include 32 base living cases, ten followups (38 distinct living cases overall) and 54 terrain-only
shape queries, with copied results and control attribution. These fixtures require the configured
original assets to replay; standalone geometry/controller tests do not.

Five further original captures add 66 movement/stance/network-correction controls and 1,147 world
steps. In the matched-pose managed run, all 1,229 body frames agree exactly on flying, flag 0x2,
applied dimensions and wishes. All 42 dimension returns and six network returns agree; the six
network cases also agree exactly on pose and actual velocity. Maximum velocity difference is
0.0000006 m/s, and maximum position difference is 0.0011 m in four growth/kind-switch controls.
The [per-case comparison summary](../tools/AAEmu.Zone.Diagnostics/Fixtures/zone129-living-controller.managed-summary.json)
retains the remaining differences and source hashes. The full solution builds and 411 standalone
tests pass. These checks establish the exercised subset, not native creation or full gameplay parity.
