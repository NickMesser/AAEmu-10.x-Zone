# Managed character geometry and native controls

Managed mode uses authored living dimensions and upright convex queries. This replaces the
previous capsule bounding boxes; it does not implement the complete native living controller.
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
cylinders require a positive axial half length. Query positions are primitive centers.

The original `CLivingEntity::SetParams` at `35028BF0` uses `Size.X` radially and ignores `Size.Y`.
The authored `UseCapsule=false` living body is a cylinder. It is distinct from an explicitly
requested query box. `FromLivingStance` makes this distinction during managed player admission.

Initial admission reuses `ZoneActorPhysicalizationRequest.BuildInitial`: the last source-order
stance with ID 2 wins, authored lengths scale by entity scale, and living mass scales by its cube.
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
the entity origin. Object identity and actor-model identity must not be conflated.

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
filling outer edges, holes or gaps. Terrain recovery requires an actual top-triangle intersection
and selects an upward unprojection; a wholly submerged body is not an early terrain-surface contact.

Nearly spherical diagonal affine targets with centered or exactly aligned query offsets can
otherwise exhaust smooth-hull EPA refinement. A separate analytic certificate handles cases where
an axis support attains the global bound `target minimum radius + query inscribed radius - center distance`.
It uses no near-diagonal/near-axis admission tolerance and adds no clearance. Independent controls
prove exact depth, material, witness, separation and residual penetration, including mirrored targets
near coordinate 8000. Cases that cannot attain this bound continue through bounded EPA; a numerical
exhaustion remains an explicit failure rather than a fabricated depth or miss.

The original living step at `3502DED0` corrects an admitted static contact by
`(depth + world.clearance) * direction`. The world constructor at `350827B0` sets clearance to
`0.01` m. Its geometry query limits individual living unprojections to `2.5 * radius`; this is
not an aggregate frame travel budget. Managed authored bodies have an explicit `Living` profile
and use that bound. Explicitly constructed `Kinematic` bodies retain their generic support contract
and do not acquire a fabricated native living radius limit.

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
represent the resulting travel. Managed recovery currently runs proactively; a speed epsilon or
an idle velocity test alone would not reproduce that state-dependent predicate. Porting the full
idle/contact-cache behavior remains part of controller parity.

The owned integrator also receives the actor identity for support queries. For authored living
bodies, a bounded fallback repairs a foot pivot slightly below a real support surface even when
the collider is already clear. The original helper at `3502CA20` starts its ray at
`foot + (HeightCollider - segmentHalfHeight)` and casts down
`1.5 * (HeightCollider - segmentHalfHeight) + 2`. The final living clamp raises a foot only when
it lies strictly less than `HeightCollider - segmentHalfHeight` below the floor. The managed
fallback applies that bound, preserves the raw surface height/material and does not adopt distant
terrain above an interior floor. Existing crossing/touch support remains in place; the complete
native suspension, authored walkable-angle and stair solver are still separate work.

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

Stance transitions, arbitrary character rotation, authored slope/step limits, native suspension,
complete recovery contact ordering/state, friction/elasticity and the `IgnoreCollision`
callback/state behavior remain unimplemented. Some special authored stances place the collider
bottom below the pivot support plane; the original diagnostic also observes hovering rather than
a simple pivot landing for those dimensions. Their full response needs the suspension/contact
solver, not a hidden dimension change. NPC behavior, swimming, gliding, mounts, moving platforms,
vehicles and rigid-body dynamics also need their own owners. Full feature parity remains false.

The checked-in [original control fixtures](../tools/AAEmu.Zone.Diagnostics/Fixtures/README.md)
include 32 base living cases, ten followups (38 distinct living cases overall) and 54 terrain-only
shape queries, with copied results and control attribution. These fixtures require the configured
original assets to replay; standalone geometry/controller tests do not.
