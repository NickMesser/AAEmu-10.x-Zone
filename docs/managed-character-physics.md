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
Terrain retains its authored triangles, holes, loaded-cell boundaries and solid-below overlap
policy. Static triangle meshes retain empty interiors; explicit closed box proxies retain volume.
Bounds prune candidates and never replace the capsule or cylinder surface.

Existing explicit box triangle SAT and actor-pair slab behavior is retained. Degenerate triangles
remain noncollidable for every shape kind. Curved queries use a 1 mm separation/contact band,
a 0.05 mm distance convergence target and a 0.5 mm advancement target. This does not bound travel
error to 1 mm at grazing contact. Numerical convergence failure is explicit. A touching shape can
move tangentially or away; initial penetration recovery remains separate work.

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
intersection parameters at 72 and skip count/pointer at 80/88. This profile uses static objects
and required/any flags `2/2` for player-collidable geometry, excluding terrain and dynamic bodies.
It supplies no queue flag, callbacks, skip list or retained native pointers.

A nonzero sweep returns travel distance. Zero sweep selects overlap and returns a contact count.
The copied contact has double distance at 0, point at 8, normal at 20, mapped material at 56,
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
Malformed, unsupported, indeterminate or failed requests remain visible in the result and fail
the diagnostic instead of becoming clear-space misses.

Current runtime evidence and exact control counts are recorded in CE-07. They cover the selected
authored controls, not every possible primitive pair or native controller state.

The inward initial-contact controls expose a separate policy gap. The original raw sweep can
skip a face already intersecting the query, then report a later contact or no contact. Managed
blocking queries currently stop an inward move immediately at initial intersection. This is not
native penetration recovery, and matching separated sweeps does not establish that behavior.

## Remaining controller work

Stance transitions, arbitrary character rotation, authored slope/step limits, native suspension,
penetration recovery, friction/elasticity and the `IgnoreCollision` callback/state behavior remain
unimplemented. Some special authored stances place the collider bottom below the pivot support
plane; their full landing response needs the native suspension/contact solver, not a hidden
dimension change. NPC behavior, swimming, gliding, mounts, moving platforms, vehicles and rigid-body
dynamics also need their own owners. Full feature parity remains false.
