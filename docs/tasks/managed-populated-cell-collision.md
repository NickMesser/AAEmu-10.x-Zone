# Managed collision coverage for populated cells

Priority: P0 for the original zone 129 NPC far-return control. Status: target-cell vegetation,
dependency-backed object/entity certificates and live production coverage are implemented.
Whole-world query admission and the original far-return acceptance remain open.
This task extends [managed NPC navigation](../managed-npc-navigation.md) and CE-06/CE-08 in
[CryEngine removal](cryengine-removal.md).

## Outcome and current blocker

Make the unchanged original 8173 `ReturnToIdlePos` / `return_state` / Approach path move its
actual living body back from the existing 4 m displacement inside authored cell 009_014. Keep the
original scripts, goal parameters, 7 m position-check threshold, single physical step and sole Unit
pose publisher. A forced timeout, direct teleport in place of Approach, rewritten Lua or an
empty synthetic scene does not satisfy this task.

`ZoneManagedNpcNavigationQueries.CheckCollisionCoverage` now uses the concrete
[cell coverage owner](../managed-cell-collision-coverage.md). It classifies the initial cell for
both character and ray categories, retaining exact inputs, dependency proofs and live handles.
Native physical queries are global across loaded cells: a complete local inventory cannot
exclude an unknown contributor from another cell. Those remaining contributors still refuse
global clearance. Admit them through their real owners or prove selected-class/current physical
influence exclusion; do not replace the global query with an authored-cell filter.
A decoded model, render mesh, authored bounding box, missing native body or successful ray miss
does not establish physical clearance.

The [vegetation owner](../managed-vegetation-default-proxies.md) now reads all 4,145 world groups
and accounts for all 7,127 target-cell vegetation rows under the actual configured CVars:
`e_vegetation=1`, quality 0, minimum size 1, collision-only 1, foliage 0. It publishes 385 placements with
396 character and 396 ray proxies; the other 6,742 rows take source configuration skips. Every
published proxy has a real source-triangle ray and nondegenerate-capsule witness. The target's
74 XML rows and 107 roads/decals also have runtime dependency-backed certificates. Both query
indices retire completely. The original 4 m far-return acceptance remains open because other
loaded cells retain unowned possible world-query contributors.

The smallest useful first profile is engine cell (0, 0), authored `main_world/cells/009_014`.
Recorded 8173 starts at local `(775.96875,940.6719,230.23447)`; its existing 4 m far-return stimulus
stays inside this cell. Other populated cells retain explicit unsupported coverage until their
physical families are admitted, and currently prevent this global physical query from being
certified. Local publication is a completed milestone; it is not whole-scene collision or full
NPC gameplay parity.

## Original-content census

These are reader-backed counts from the original 10.x client, not inferred collider counts.

| Cell 009_014 input | Authored rows |
| --- | ---: |
| `object.dat` brush | 675 |
| `object.dat` decal | 97 |
| `object.dat` road | 10 |
| `object.dat` vegetation | 869 |
| `vegetation.dat` vegetation | 6,258 |
| `entities.xml` entity | 74 |
| Water, GeomEntity, AnimObject, boid flock | 0 |

The 7,127 vegetation placements reference 26 world group IDs:

| Resource classification | Placements | Meaning |
| --- | ---: | --- |
| Some node references default 4096 physics | 385 | Requires actual native publication and proxy geometry |
| Special slots only, no independently published child physics | 6,721 | Root masks exclude player/ray; certify exact publication first |
| No physical chunk references | 21 | Native `HasPhysics` cannot produce a body from these resources |

The 385 count is a resource-reference census. It is **not** the final number of native parts,
instances or admitted bodies: quality gates, root selection, compound publication and helper
publication still apply. All 18 default-bearing resources below decode authored triangle proxies
with the current CGF reader; this does not replace native publication validation. All 18 groups
have `bAlignToTerrain=0`.

Paths in this table are relative to `game/objects/env/`.

| Group ID | Placements | Compound | Original resource |
| ---: | ---: | :---: | --- |
| 19 | 9 | No | `05_nature/08_cry_source/trees/river_tree/river_tree_b.cgf` |
| 22 | 1 | No | `05_nature/08_cry_source/trees/river_tree/river_tree_c_broken.cgf` |
| 32 | 97 | No | `05_nature/08_cry_source/trees/hill_tree/hill_tree_large_bright_green.cgf` |
| 33 | 60 | No | `05_nature/08_cry_source/trees/hill_tree/hill_tree_small_bright_green.cgf` |
| 34 | 26 | No | `05_nature/08_cry_source/trees/hill_tree/hill_tree_small_grey_green.cgf` |
| 35 | 32 | No | `05_nature/08_cry_source/trees/hill_tree/hill_tree_small_yellow_green.cgf` |
| 71 | 11 | No | `05_nature/08_cry_source/trees/jungle_tree_thin/jungle_tree_thin_a.cgf` |
| 279 | 9 | No | `05_nature/03_temp/gw_tree_c01.cgf` |
| 291 | 2 | Yes | `05_nature/03_temp/gw_tree_c03.cgf` |
| 331 | 5 | No | `05_nature/08_cry_source/trees/jungle_tree_large/jungle_tree_large_big_bright_green_fallen_a.cgf` |
| 335 | 4 | No | `05_nature/08_cry_source/trees/jungle_tree_large/jungle_tree_large_big_bright_green_fallen_e.cgf` |
| 341 | 20 | No | `05_nature/08_cry_source/trees/jungle_tree_large/jungle_tree_large_big_grey_green_360_deg.cgf` |
| 360 | 14 | No | `05_nature/08_cry_source/trees/river_tree/river_tree_c.cgf` |
| 385 | 18 | Yes | `05_nature/03_temp/gw_tree_c04.cgf` |
| 386 | 21 | No | `05_nature/03_temp/gw_tree_c05.cgf` |
| 456 | 44 | Yes | `01_nuia/04_gw/gw2_tree_a01.cgf` |
| 1466 | 1 | No | `05_nature/08_cry_source/trees/thin_leave_tree/thin_leave_tree_big_grey_green_240_deg.cgf` |
| 2972 | 11 | Yes | `01_nuia/04_gw/gw2_tree_a02.cgf` |

Group 2972 has the authored render material override `objects/env/01_nuia/04_gw/gw2_tree_a01`.
The other 17 groups have empty overrides. Original 30121740 loads physical CGFs with a null
material override;30147630 stores the group material separately for rendering. Query materials
therefore resolve from the actual intrinsic StatObj/node material graph. A native poisoned
override-pointer control verifies that physicalization does not read this render-only pointer.

## Native vegetation contract to implement

### Catalog, load gates and transforms

- [x] Add a managed world vegetation catalog, for example
  `Content/ZoneWorldVegetationContent.cs`. Reuse the asset source and read original
  `game/worlds/main_world/vegetation.xml`. The current world has 4,145 unique group IDs.
  Preserve source IDs, model/material paths, quality/config fields and flags. Do not interpret
  a group ID as an index in the object.dat model table.
- [x] Port the relevant `30107F70` XML reads and `30108AD0` group publication semantics.
  The latter publishes through engine virtual 1176 into the ID-indexed StatInstGroup catalog.
  Preserve replacement/source-order behavior rather than relying on dictionary iteration.
- [x] Preserve `302221D0` signed group-index validation, native quality/model gates, configured
  vegetation/minimum-size/collision-only policy and scale clamp `[0.1,5]`. Recover and declare
  the supported configuration explicitly. An on-demand or skipped allocation is not evidence
  that physical geometry cannot affect a query.
- [x] Reuse `ZoneCellVegetationContent` and `ZoneCellObjectContent`, which already retain the
  complete 64-byte records. `302253C0` supplies sector translation
  `((engineCellX*4+sectorX)<<8,(engineCellY*4+sectorY)<<8,0)`, with X outer/Y inner.
  Tagged vegetation in object.dat uses engine-cell translation `(X<<10,Y<<10,0)`.
  World absolute cell coordinates must not be added a second time.
- [x] Port exact transform `30102840` and Euler helper `30106900`. Raw row bytes `0x39`,
  `0x3A`, `0x3C` become native X, Y, Z angles respectively, using the original float order
  `float(byte)*1.4117647*0.017453292`. Apply authored uniform scale and translated position.
  General terrain alignment calls `300CD590` when the group enables it and explicit X/Y angles
  are absent; implement that branch or refuse it. The initial 18 groups do not need this branch.

`Native/StaticObjects/ZoneNativeStaticObjectServices.Vegetation.cs` contains reusable source
mapping and asset lookup knowledge. It is a comparison adapter, not a managed runtime dependency.
Managed loading must make no Native calls or engine-module loads.

### Physical resource publication and masks

`CVegetation` table `30289F88`, virtual 400, resolves to physicalizer `30103DC0`.
`CStatObj` table `3027EE78`, virtual 480, resolves to `30025950`: `HasPhysics` checks actual
root geometry references and, when requested, child statobjs. Render bounds do not enter this
test. The physicalizer calls `30102840` and creates a static entity from the resulting transform.

The following values are native **part flags**, not entity-type masks. Keep player bit`0x2`
and ray bit`0x8000` separate from the world entity-selection mask used by navigation.

| Publication path | Native part flags | Player/ray consequence |
| --- | --- | --- |
| Vegetation default 4096 loop | `0x04018FFF` before source-specific additions | Contains both player and ray |
| Root 4098 when both special resources are present | `0x00104000` | Contains neither |
| Root 4097 in that two-resource branch | `0x00200000`, or `0x00202000` with foliage | Contains neither |
| One available root special resource | `0x00300000`, optionally OR`0x4000` and/or`0x2000` | Contains neither |
| Independent child 4097 in `300442F0` | `0x00008000` | Ray only |

- [x] Preserve the distinction between the low 32-bit flags and adjacent collider flags in the
  native geometry parameters. For example, the default initialization is the 64-bit word
  `0x0000000104018FFF`; do not treat its upper word as another collision category.
- [ ] Port the physicalizer's actual root/default list and independent child call. Its final
  `CStatObj` virtual 592 resolves to `300442F0`. This admits ordinary/helper children using
  native type/flag rules and their composed transforms. `IsCompound` alone does not describe
  all child publication: a direct resource can still publish helper physics.
- [ ] Preserve child default masks and their real producers. In `300442F0` the normal branch
  computes `(~(-hasColltypePlayer & 2) & 0xFFB) | 0x18004`, yielding `0x18FFF` or`0x18FFD`;
  a selected `colltype_player` child uses`0x2`. Child model flag`468 & 0x400` removes player,
  flag`472 & 0x100` adds`0x800000`, and the native signed-flag/CVar branch can select`0x40`.
  These must be recovered/admitted or explicitly refused, not replaced by one universal mask.
- [x] Do not apply brush picking policy to vegetation root special slots. Conversely, do not
  declare all vegetation 4097/4098 nonblocking: independent child 4097 is a ray target.
  The 6,721 target-cell special-only placements have no independently published child physics;
  other cells contain such children, including noncompound resources.
- [x] Reuse `ZoneCgfContent`, `ZoneCgfPhysicsGeometry`, `ZoneTerrainMaterialResolver` and shared
  `ZoneManagedTriangleMesh`/primitive instances. Extract reusable resource/material construction
  from `ZoneManagedSceneCollision` if needed, while keeping brush and vegetation publication
  policies separate. Use serialized proxies, never render triangles or proxy-shaped AABBs.
- [x] Preserve local/default material-slot semantics, group material overrides, helper gates,
  native root-versus-child transforms and physical entity union bounds. Decode or refuse any
  required foliage/breakability/physicalization branch whose flags remain unowned.

`301008C0` controls on-demand physical creation. It is not a no-collision test. A managed owner
may eagerly retain the supported immutable collision resources, but that replacement scheduling
policy must be explicit and must not drop geometry that the native on-demand path can create.

### Cell and resource lifetime

- [x] Add a scene-owned vegetation instance lease, for example
  `Scene/ZoneManagedSceneVegetation.cs`. Share immutable resource/material variants, retain
  distinct player/ray handles and tie every placement to the exact loaded cell generation.
- [x] Stage a placement's complete admitted part set before publication. On partial failure,
  remove every handle already added for that placement and retain its exact named diagnostic.
- [x] Unloading or replacing a cell invalidates coverage before exposing the replacement,
  removes all its player/ray instances and prevents stale leases from removing new handles.
  Shared mesh/BVH resources may survive only while their owning scene/cache remains valid.
- [x] Report authored rows, classified nonblocking rows, admitted parts and failed placements
  separately. Preserve group/model/source-file/sector/row provenance. Resource-level failures
  must not be counted repeatedly as unrelated successful coverage.

## Nonblocking certificates for the initial cell

These certificates establish physical eligibility for specified query masks. They do not claim
complete rendering, audio, area events, particle simulation or general entity-script execution.
Each certificate must inspect original content and its dependencies at runtime; a class-name
allowlist or a checked-in success count is insufficient.

The 74 XML entities are 33 ParticleEffect,15 RandomSoundVolume,13 Light,6 AmbientVolume,
5 AreaShape and 2 ReverbVolume. All have empty Geometry/Archetype and no FlowGraph.

- [x] Add a bounded entity-collision classifier, for example
  `Scene/ZoneManagedCellEntityCollision.cs`, backed by `ZoneCellEntityContent` and the original
  `game/entities/*.ent` class descriptions. Native registry `32094D70`/`32094E80` resolves these
  descriptions; load manager `32097000` owns class/quality/parent/proxy admission. Validate
  class/script identity and all supported row children. Unexpected geometry, archetypes,
  custom creators, FlowGraphs, physical callbacks or child spawning remain named refusals.
- [x] Certify the exact original sound profiles from
  `Scripts/Entities/Sound/{AmbientVolume,RandomSoundVolume,ReverbVolume}.lua`.
  Their server initialization owns NetPresent/sound/area flags and contains no geometry load or
  physicalization. Preserve the actual resource dependency checks rather than inventing silent
  script execution success.
- [x] Certify target Light rows through original `Scripts/Entities/Lights/Light.lua` and
  binding `3204B2D0` -> entity virtual 848/`3200F430`. This creates a light/render slot, not a
  solid physical proxy. Reject unsupported FlowGraphs: two Light rows elsewhere in zone 129
  have them even though none of the target 13 does.
- [x] Certify the scriptless/invisible `game/entities/areashape.ent` ordinary polygon profile.
  `CAreaProxy` constructor `3208C1E0` creates a CAreaManager record; `3208CD80` loads the
  Points/Height branch into shape-type 0. This is not `CPhysicalProxy::PhysicalizeArea`.
  Do not use that conclusion for its gravity/Bezier or other unported branches.

### Particle effects require their effect graph

`ParticleEffect.lua` calls emitter operations, not entity model physicalization. The binding
chain is `32036BC0` -> entity virtual 832/`3200F380` -> `32089340`, which owns an emitter/render
slot. An emitter can nevertheless create physical descendants, so class identity alone cannot
certify a ParticleEffect row.

The target 33 rows reference nine original effects across three libraries. Their 28 nested Params
contain no `ePhysicsType`, legacy `PhysicsType`, collision or Geometry fields. Native parameter
constructor `30086910` initializes byte`+976` to 0; metadata `3007B0B0` maps `ePhysicsType` there.
Matching PE enum table `302D5AF0` maps 0=`ParticlePhysics_None`,1=`SimpleCollision`,
2=`SimplePhysics`,3=`RigidBody`. The version20 content reader validates the complete selected
default/no-force graph, and the target entity-row certificates consume that proof. Additional
parameter and physical-descendant families elsewhere in the scene remain refused.

| Original particle library | Referenced effect names after library prefix |
| --- | --- |
| `game/libs/particles/x2_sounds.xml` | `particles.stubble_poison_fog`, `particles.vendor_elf_bottle`, `particles.vender_elf_misc`, `particles.vendor_elf_repair`, `particles.vendor_elf_food` |
| `game/libs/particles/x2_effects.xml` | `mon_effects.nmon_poison_fog_launch`, `fire_smoke.torchlight_a` |
| `game/libs/particles/x2_effects_p.xml` | `env.elf_light_r`, `env.elf_light_y` |

- [x] Implement the supported parameter-default/descendant traversal profile. Resolve the full
  effect name, preserve library version/default semantics and inspect every nested effect.
  Refuse unresolved names, unowned inheritance/default sources or possible physical descendants.
  Do not classify effects from the absence of a top-level geometry field alone.
- [x] Test a nested rigid-body effect and a missing/default-inheriting effect as refusals,
  alongside the original nine nonphysical candidates. Keep an explicit reason per entity row.

### Roads require the normal load/compile lifecycle

`30222AA0` creates `CRoadRenderNode` through constructor `302391A0`, which initializes physical
entity`+160` and geometry`+168` to null, then queues its authored vertices. Actual compiler
`302398F0` projects/clips terrain into a render mesh. It removes an existing physical body but
does not create a new one. Table`3029B6B8` physicalize/dephysicalize slots 400/520 point to
`30020AD0` no-ops.

A separate special-geometry creator `30238680` exists. The matching PE audit found no direct
call/jump or vtable reference to it, and it is not invoked by the authored factory/compiler.
This supports a bounded normal-load road profile; it does not establish that every possible
road state or external editor/game call is nonphysical.

- [x] Classify the target 10 roads only under the verified normal authored load/compile lifecycle.
  Preserve source parameters/material dependencies and refuse any admitted owner that can
  install a physical road body. Add an offline factory/compiler call-path control before marking
  this certificate complete.
- [x] Keep render compilation readiness separate. The existing
  `ZoneNativeStaticObjectServices.Roads.cs` comparison owner intentionally stops in the queued
  phase; that implementation is not evidence of completed road compilation or collision parity.

## Coverage integration and suggested file ownership

| Lane | Files/owners | Required result |
| --- | --- | --- |
| Content | New world vegetation catalog; existing cell/CGF readers | Bounded authored IDs, flags, transforms and resource dependencies |
| Static collision | New vegetation owner; reusable resource builder from `ZoneManagedSceneCollision` | Native-admitted parts, mask-specific indices and exact cell retirement |
| Entity/object classification | New classifier and particle/road certificates | Per-row physical eligibility with dependency-backed refusals |
| Scene/report integration | `ZoneManagedSceneActivation`, new current-cell coverage records | One authoritative inventory and coverage generation per loaded cell |
| Navigation | `ZoneManagedNpcNavigationQueries` | Consult real coverage for every touched cell and requested mask |
| Verification | New focused content/collision/coverage tests and original-resource probe | Source controls, real collision hits and unchanged 8173 far return |

- [x] Define a concrete copied coverage result containing engine cell/generation, query mask,
  classified/admitted/unsupported counts and original dependency diagnostics. Do not expose a
  caller-provided permissive boolean as the authority.
- [x] Require exactly one current cell inventory and complete required input reads. Missing,
  malformed, failed or retired input is not equivalent to an authored absent file.
- [x] Replace the populated-cell guard only for complete relevant coverage. A clear capsule
  sweep and a clear navigation ray may require different admitted part sets.
- [x] Preserve terrain support, holes, world-to-engine mapping, brush completeness and existing
  navigation work/support limits. Eager vegetation publication is not permission to change path
  search, support sampling or controller motion policy.
- [x] Keep general `FullSceneCollisionReady` and gameplay readiness separate. One supported cell
  must not clear failures in another cell or enable wet/dynamic/animated profiles globally.

## Tests and acceptance

- [x] Offline native controls record actual `AddGeometry` IDs, flags and transforms for direct
  default vegetation, root-special-only vegetation and compound/helper child cases. Read or
  emulate matching PE instructions without loading original DLLs. Distinguish child 4097 ray
  eligibility from the root special masks and pin source-specific child default masks.
- [x] Exact transform controls distinguish all three packed angle bytes, scale clamp endpoints,
  both placement coordinate systems and nonzero engine-cell translations. Refuse unported
  terrain alignment rather than borrowing a render orientation or bounding box.
- [x] Material controls cover group 2972's override, local/default triangle slots and unresolved
  material graphs. Positive original tree rays/capsule contacts must report actual physical
  normals/materials; a root-only special foliage proxy must remain absent from those masks.
- [x] Failure/lifetime controls cover missing group/model, unsupported physical chunk,
  independent child proxy, partial placement rollback, unload/reload, stale generation and
  removal of both character/ray instances. Reused resources must not duplicate vertex arrays.
- [x] Certificate controls use actual class descriptions/scripts and nested particle parameters.
  Add explicit Geometry, FlowGraph, physical descendant, class mismatch and unsupported area
  variants; each must prevent coverage instead of being dropped.
- [x] An original 009_014 regression accounts for all 7,127 vegetation,74 entities and 10 roads,
  independently of its 675 brushes/97 decals. Assert final admitted/nonblocking/failure totals
  derived by the owners; do not hardcode 385 as an exact final collider count.
- [ ] Unchanged original 8173 enters original `return_state` after the same 4 m displacement inside
  the 7 m correction threshold, obtains a genuinely covered route, produces ordinary/predictive
  goal output according to its installed settings and moves through the existing living body.
- [ ] Observe return-to-idle completion and destructor restoration through the actual original
  callbacks. Verify body/model/Unit/spatial/AI/outgoing coherence, sole backend Step and sole
  Unit pose writer. Do not use timeout or `NpcTeleportTo` as a replacement for movement.
- [x] Retirement/re-entry cancels old route/controller work, removes cell geometry and prevents
  pending callbacks or commands from publishing a retired actor or cell generation.
- [x] Run focused tests, the required solution checks and the original-resource control. Inspect
  loaded modules in the real managed host: zero CryEngine/native-Lua modules. Record scope,
  unresolved families and original-content provenance with the result.

## Current integrated boundary

The local cell proof is complete for `0x2|0x8000`. The global query still refuses before a
false no-path or route publication: other loaded cells contain independent vegetation helpers,
compound special/foliage branches, terrain-aligned resources, unowned decal/material profiles,
particle descendants, physical/animated entities and non-Entity Object records. All of them
remain visible in the copied source inventory and diagnostics.

The root/default publication plan is implemented for the supported resource profiles. The two
remaining child-publication tasks above cover generic independent helpers, compound selected
special aggregation, active compound foliage localization and mutation/joint propagation.
The configured `302221D0` minimum-size gate uses the actual loaded CStatObj radius: raw
Mesh0800 header bounds, native ordinary-node union/transform and half-diagonal float order.
The gate multiplies the raw placement scale before the accepted scale is clamped. Those bounds
are a loader gate input, never replacement collision geometry.

Bodyless cell/brush retirement and exact owned-handle loss invalidate cached navigation before
another physical step. Water must match exact source-cell order and resolved ocean bits; loss
of any source cell invalidates global water completeness rather than producing a drier ceiling.
General scene readiness and NPC gameplay readiness remain false.

## Other-cell work that remains open

The selected six cells contain 118,696 vegetation placements (8,479 tagged plus 110,217 sector
rows),119 referenced vegetation group IDs/109 CGFs,6,389 brushes,827 decals,123 roads,
127 water rows and 782 XML entities. All 109 vegetation resources decode, but physical publication
and eligibility remain separate. The 4,976 placements referencing some default slot are another
resource census, not a collision coverage result.

| Family outside the first profile | Count | Required next owner/proof |
| --- | ---: | --- |
| AnimObject | 22 | Authored `.cga`/`.chr` physics, animation and transform lifecycle; rows have `bPhysicalize=1` |
| GeomEntity | 3 | Class flag 2/nonempty Geometry automatic physicalization; `32097000` -> `32011FC0` selects static/rigid behavior |
| BasicEntity airport elevator | 1 | Actual `bPhysicalize=1`, `bRigidBody=1`, `bGimmick=1`; never classify as cosmetic |
| Boid flock entities | 31 | 14 Chickens,12 Birds,5 Fish; `CreateFlock` `320BA9F0` can create child entities/proxies; no dedicated skip has been established |
| Water rows | 127 | Class 5/bit `0x20` upper bounds now exclude the navigation mask `0x107`; actual water queries/19 areas remain a separate owner |
| Other vegetation | 111,569 placements | Actual default/special/helper publication, material/foliage flags and any terrain alignment |
| SoundEventSpot | 34 | Implemented exact ordinary sound profile and dependency certificate; arbitrary script execution remains separate |
| Light FlowGraphs | 2 | Resolve actual effects before any no-solid certificate |
| FogVolume | 1 | Implemented exact type 7 render profile, full-null physical accessor and dependency certificate |
| Non-Entity Object children | 27 | 11 OccluderArea, 7 Portal, 6 VisArea and 3 VoxelObject; retain every row and recover each physical/proxy producer |

BasicEntity/AnimObject use original `BasicEntity.lua` and
`EntityCommon.PhysicalizeRigid`; animated shapes need their actual physical/frame consumers.
GeomEntity can reuse authored CGF proxies only after its entity/class/material policy is owned.
Do not substitute render bounds for any of these proxies. Keep general XML scripting, FlowGraph,
child spawning, moving bodies and area effects as explicit remaining capabilities.

The next source-backed increments are:

1. Complete eligible independent vegetation helpers in `300442F0`, selected-special aggregation,
   active compound foliage localization and the real `300CD590` alignment producer. Preserve
   the actual per-part flags, material slots, transforms and mutation exclusions.
2. Own the three actual GeomEntity registrations through `32097000`/`32011FC0`, class flags,
   geometry resources and selected static/rigid behavior. AnimObject, elevator, voxel and flock
   physical descendants require their separate animation/dynamic/mutation lifetimes.
3. Recover the additional particle parameter/resource families. Current refusals include explicit
   Geometry descendants; ordinary visual parameters cannot justify dropping those descendants.
4. Classify all27 non-Entity Object children, other decal projection/material profiles, parented
   lights, area variants and FlowGraphs. Retain exact row/dependency proofs and global scope.
5. Integrate the [fresh-player AI owner](managed-player-ai-grid.md) with shared scene identities
   and the actual player controller/publication boundary, then implement positive crowd solving.
   The external player stage alone does not make a live managed player a covered crowd actor.

## Evidence and source locations

The integrated batch passes 1,159 tests with 0 failures/skips. The final normal solution build
has 0 errors and no new analyzer diagnostics. Build4 emits 34 existing warnings incrementally;
the last test-project analyzer pass and final production pass together match the prior diagnostic
set. Exact compiled core SHA-256:
`0CB8DA14D82D81A9F099AF0968A11CB2B7734D9075F77038A9232DBCBEA2763A`.

Accepted evidence under `D:/aaemu-runtime/`:

- `cell-coverage-final-{build4,tests4}.log`: normal solution build and full suite.
- `research/cell-coverage-final-scene.json`: actual 6-cell publication; target mask 32770,
  no target diagnostics; character/ray inventories 10,562/10,824 retire to 0/0. Other cells
  retain 11,545 unsupported vegetation rows and explicit entity/object refusals.
- `research/managed-vegetation-extension-final-probe/final-evidence-summary.json`: exact-core
  configured target publication, 396 real triangle/ray/capsule witnesses and original input hashes.
- `research/collision-final-census/collision-final-results.json`: all 809 XML children accounted
  for (782 entities plus 27 other Object rows), 557 certified/252 refused; 123/123 roads,
  723/827 decals, 127/127 water class bounds. The 104 decal refusals remain explicit.
- `research/cell-coverage-npc-return-{near,far}-control.json`: five original constructors and
  corrections, 150-frame near hold/idle controls, real idle skill request and 22 owner categories
  retiring to 0. The unchanged 4 m far return retires 8173 on the global cell(2,0) helper-physics
  refusal; it is not an accepted route or completion.
- `cell-coverage-ready-entry-verification.json` and
  `research/cell-coverage-ready-entry-wire-verification.json`: 116.25 s real TCP replay, 81 modules,
  zero engine/native-Lua modules, player 387 grounded at the accepted pose, one ZoneLoaded and
  five complete movement rows (two player/three NPC). Both player payloads match the previous
  accepted capture after normalizing only Time. No actor refusals; replay exit 0/zone exit 3.
- `research/cell-coverage-batch-evidence.json`: combined source-commit/artifact-hash record.

These probes execute the same core as the final test suite and server. No staged reader assembly
is substituted. Earlier `parity-artifacts` and four-proxy component evidence remain unchanged.

Original client: `ArcheAge Returns 10.0.2.13 - 8yx - r575 - 2026-06-18`.
Required inputs are `game/worlds/main_world/{world.xml,vegetation.xml}`,
`cells/009_014/client/{object.dat,big_object.dat,vegetation.dat,entities.xml}`, the listed CGFs
and actual material graphs, `game/entities/*.ent`, original class scripts and the three particle
libraries. Use the configured asset source rather than requiring the local extraction path.

Audit artifacts remain outside the repository at
`D:/aaemu-runtime/research/scene-collision-next/`:

- `inventory.json`: complete original row/class/group/CGF census and retained entity XML.
- `cell-summary.json`: per-cell resource/publication census.
- `target-cell-default-vegetation.json`: the 18 group/model records and material override.
- `target-cell-particles.json`: the nine original effect trees'28 Params.
- Address-named `.c` extracts: source functions cited above.

Matching source files are `cry3dengine_dedicate.c` and `cryentitysystem_dedicate.c` under the
local decompilation output. The audit read matching PE vtables/enum data offline and loaded no
original modules. These artifacts support the task; they do not constitute completed managed
collision acceptance or an executed end-to-end original populated-cell return.
