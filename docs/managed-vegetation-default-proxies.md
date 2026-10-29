# Managed vegetation proxy publication

The vegetation owner now classifies all 26 authored groups and 7,127 placements in target cell
009_014 under the actual dedicated configuration. It publishes 385 placements containing 396
player proxies and 396 ray proxies; 6,742 placements skip by configuration, with zero unsupported
rows in that cell. This is vegetation evidence. Whole-cell and cross-cell collision admission also
requires the independent family, input-completion and live-handle owners described in
[the populated-cell task](tasks/managed-populated-cell-collision.md).

## Source configuration and load gates

`ZoneWorldVegetationContent` reads `game/worlds/{world}/vegetation.xml` through the configured asset
source. It retains the source-order rows and complete attributes; later equal IDs replace earlier
rows, and publication uses the final ID map. The serialized signed 16-bit vegetation group ID is
separate from object.dat's model table. Missing/negative references remain unsupported.

`ZoneManagedVegetationPolicy.FromCVars(actualCVars)` consumes the actual engine configuration,
including dedicated.cfg and command-line overrides. Offline instruction execution of the original
CVar registrations `3000A900`/`30011A40` establishes these engine defaults:

| CVar | Engine default | Actual configured value used by the content probe |
| --- | ---: | ---: |
| e_vegetation | 1 | 1 |
| e_obj_quality | 0 | 0 |
| e_vegetation_min_size | 0 | 1 |
| e_vegetation_create_collision_only | 0 | 1 |
| e_view_dist_ratio_detail | 30 | 30 |
| e_phys_foliage | 2 | 0 |
| e_vegetation_bending | 2 | 2 |
| e_foliage_stiffness | 3.2 | 3.2 |

The minimum size, collision-only value and detail ratio are floats. In particular, a nonzero
collision-only float selects recursive default-4096 admission; it is not an integer CVar.
`302462D0` permits spec 0, otherwise quality 0 or spec <= unsigned quality. Detail ratio 0 excludes
spec 7. A configured skip is distinct from an unowned resource or publication branch.

`302221D0` compares `minimumSize <= statObjRadius * rawSerializedScale` before clamping scale to
0.1..5 and before the collision-only gate. The radius is the actual CStatObj+260 producer:
Mesh0800 header bounds at +124..144 pass through `301D3A30`/`30030500`, and `30024D70` computes
half their diagonal with the original float operation order. `3002F720` returns that scalar. A direct
resource uses the selected ordinary mesh's raw bounds; compound `30030D90` unions the ordinary
node bounds after the original unpadded `300212E0` transforms. These render bounds serve only
the load-size gate. Physical queries use the decoded physical proxies.

The raw-scale order matters: a small raw scale can fail minimum size even though the later 0.1
clamp would have enlarged it. NaN fails the original ordered size comparison. A surviving
nonfinite transform or a radius overflow remains outside this finite managed profile.

## Physical parts, materials and foliage

`ZoneManagedVegetationResources` borrows the scene's existing distinct character and ray
`ZoneManagedStaticGeometry` owners. Immutable mesh/BVH and analytic resources are shared by
normalized CGF path. General triangle meshes remain surfaces, primitive boxes retain solid volume,
and sphere/cylinder/capsule proxies use the existing analytic kernels.

The initial static publication plan follows `30103DC0`, `30025950` and `300442F0`:

- A direct default 4096 root uses geometry word `0x04018FFF` and separate collider word 1. Player
  bit 2 and ray bit `0x8000` are present. Its physical local transform is identity; the node's render
  transform is not substituted.
- Root special 4097/4098 parts retain their distinct original flags and entity bounds. The compiled
  foliage count and signed `e_phys_foliage >= 2` gate decide the special-part plan. These special
  masks do not contribute player/ray proxies in the admitted direct-root profile.
- The selected-default compound prefix publishes each selected ordinary child's default geometry
  once with the original raw identity transform, then excludes it from the final child pass. This
  includes the source prefix's children whose later flag-2 gate would otherwise differ. All four
  target-cell compounds have empty compiled foliage and no selected special-child data; their
  default children are supported. Group 2972 contributes two proxies per placement.
- Foliage chunk `0xAAFC0005` is decoded and validated. Its copied spine/vertex/skin/bone arrays and
  original `3003EA10` 64-bone compilation result drive the initial publication flags. An empty
  32-byte chunk is an empty source state. A present chunk is not treated as either automatically
  unsupported or automatically empty. Dynamic bending, wind, articulated foliage and destruction
  remain separate unported behavior.
- The complete physical entity union includes every actually published part, including parts whose
  flags exclude the current player/ray query. Each query-included instance carries that union as
  its admission bounds. A query mask does not justify shrinking the source entity bounds.
- Actual intrinsic node material slots map through the existing material/surface owners. Native
  `30147630` retains a group render-material override separately, while `30121740` loads the model
  without that override and the direct/default physicalizer never reads group+104. It therefore
  does not replace the physical material map here. An original instruction control poisons that
  pointer and verifies the same publication.

`300400D0` checks used physical materials for break-energy flag `0x400`; compiled root flag
`0x100` adds geometry bit `0x800000` unless placement Flags20 bit 2 suppresses it. Only native mesh
kinds use per-triangle flag compilation. A box decoded into twelve query triangles still follows
the primitive wrapper slot rule; a negative wrapper slot skips that compilation. Query material
resolution remains independent of this flag-compilation rule.

The direct root reads compiled break flag `0x100` but ignores compiled vehicle-only flag `0x80`.
It does not substitute brush/child mask 64. The regression with a vehicle-only intrinsic material
preserves player/ray publication. This distinction must survive later child/brush extensions.

A complete plan with neither player bit 2 nor ray bit `0x8000` returns `ClassifiedNonBlocking`.
It does not invent a terrain-aligned transform or a material to explain an absent contribution.
At the actual collision-only configuration, the target's nondefault groups skip before publication.

## Placement and exclusion ownership

Explicit angle bytes X=`0x39`, Y=`0x3A`, Z=`0x3C` follow the original float order
`(float(byte)*1.4117647f)*0.017453292f`, Euler routine `30106900` and scale routine `30106AC0`.
A contributing resource requesting terrain alignment with zero explicit X/Y angles still refuses
because `300CD590` terrain-normal ownership is absent. Nonzero explicit X/Y bypass that branch,
as in the original. Sector translation is
`((engineCellX*4+sectorX)<<8,(engineCellY*4+sectorY)<<8,0)`, X outer/Y inner. Tagged rows use
`(engineCellX<<10,engineCellY<<10,0)`; absolute authored cell coordinates are not added again.

The final `302221D0` gate calls `301597D0`, comparing the translated authored bounding box with
the runtime exclusion OBB table through `30164440`. That box is an exclusion input, never a
replacement physical proxy. The mandatory immutable snapshot retains each source ID, basis,
half extents, local center, world position and the actual engine origin. Touching excludes the row;
no collision epsilon changes this source decision.

`ZoneManagedVegetationExclusions.FreshStaticScene(layout)` is restricted to dedicated,
nonstreaming static LoadZone with no admitted exclusion mutation. Original CrySystem `365704D0`
sets Seamless+160/+164 from the zone origin before cell loading; virtual 32, `3653EF00`, returns
`float(int32(originX << 10)), float(int32(originY << 10)), 0`. The factory preserves that signed
shift/conversion order, including nonzero origins. The engine constructor's exclusion pointer/count
starts at zero. Later `3015C1E0` insertion/destruction, `30158690` removal and streaming-origin
mutation need their own owner; this snapshot does not silently support them.

## API, completeness and lifetime

Construct one `ZoneManagedVegetationResources(assets, materials, layout, catalog, characters, rays,
policy, exclusions)` per scene. `BeginCell(exactLayoutPlacement)` returns an exact-generation
`ZoneManagedVegetationCellLease`. `LoadTaggedObjects(content)` consumes one typed object input;
`LoadSectors(content)` consumes the sixteen vegetation.dat sectors. A repeated attempted input is
rejected. Source-row order/offset, group, asset, model radius, part flags/transforms/bounds, foliage
counts, installed handle counts and diagnostics are copied in the placement reports.

The read-only internal input snapshots retain the exact content object identities and completion
state, plus a copied placement-lease list. A failed attempt remains incomplete and cannot be
retried as if it had never happened. Normal completion does not turn `Unsupported` reports into
coverage. The outcome set is `Published`, `SkippedByConfiguration`, `SkippedByExclusion`,
`ClassifiedNonBlocking` and `Unsupported`.

`PublicationRevision` is a monotonic managed lifetime token, not a native counter. It advances
before cell bind/retirement, placement publication/retirement, and input attempt/completion,
including bodyless classified placements. Checked exhaustion refuses before lifetime or handle
mutation. Repeated disposal is idempotent. Each placement stages its admitted part set and rolls
back both inventories on failure. Asset/material callbacks are followed by exact-generation checks;
synchronous same-owner publication reentry is refused. Cell replacement retires its predecessor,
foreign handles survive, and stale disposal cannot remove a replacement's handles.

Reports and `IsCurrent` describe lifetime/history. They do not certify that another borrower has
not cleared the target geometry. A coverage owner must also match complete source inputs,
placement dispositions, live handles and geometry revisions. The asset source and target
geometries remain caller-owned. This is a scene-thread owner. Eager immutable retention replaces
native on-demand allocation scheduling `301008C0`; an unallocated native object is not evidence
of clearance.

## Verification and remaining branches

The isolated focused build has zero warnings/errors and all 23 `ManagedVegetationTests` pass.
They cover root/special/default plans, compound raw transforms and duplicate suppression,
foliage compilation, intrinsic materials, wrapper flags, actual CVar defaults, minimum-size/raw
scale ordering, exclusion origin, cell/sector coordinates, incomplete inputs, lifetime revisions,
callback reentry/retirement and atomic revision-exhaustion refusal.

`Fixtures/vegetation-native-controls.json` contains offline original PE instruction controls from
cry3dengine_dedicate.dll, SHA256
`5e22f8338d6e146939f4f22fc0531d78c7c2313547a1863cadb2cb5e3b8d3caf`:

| Original leaf | Controls |
| --- | ---: |
| 30102840 placement transforms, including 30106900/30106AC0 | 16; 192/192 output float bits agree |
| 30164440 exclusion SAT | 140; 87 overlap/53 miss decisions agree |
| 30103DC0 with original 30025950/300442F0 selected-child processing | 70 publication controls |
| 3003EA10 foliage compilation | 12 |
| 3000A900/30011A40 CVar registrations | 8 |
| CrySystem 3653EF00 origin | 5 |
| 30024D70 radius | 5; output float bits agree |
| 300212E0 unpadded render-bound transform | 40; output float bits agree |
| 302221D0 factory size/clamp/collision-only gates | 9 |

The publication harness declares the model/world ABI callbacks and copies the original
AddGeometry outputs; allocation/CRT callbacks are explicit in the fixture. It does not run an
original live physical world. No engine DLL is loaded. Generators are retained under
`D:/aaemu-runtime/research/managed-vegetation-stage/`, and recovered publication extracts under
`D:/aaemu-runtime/research/scene-collision-next/`.

The separate configured-original-content probe reads 4,145 world groups and all 7,127 target rows.
Under the actual policy above, it reports 26/26 supported groups, 385 published placements,
6,742 configured skips, 396 instances per query inventory and zero unsupported rows. Every
published serialized triangle supplies an independent ray/contact witness, with physical materials
copied from the authored graph; retirement returns both owned inventories to zero. This is managed
query verification against real content, not a live original AddGeometry/material/collision census.
The report is
`D:/aaemu-runtime/research/managed-vegetation-stage/vegetation-managed-content-results.json`.

Remaining general branches refuse precisely: eligible independent-helper physics, active compound
foliage localization, selected compound special aggregation, compound break-flag propagation,
joint/cutdown mutation, other node-property processors, missing/incomplete material or physical
resources, and required terrain alignment. Dynamic foliage/breakage and mutable exclusion or
streaming-origin behavior remain unported even for initially supported static resources. These
branches do not occur in the admitted target-cell plan under its actual configuration. Other cells
and other cell families still require their own complete-input and query-specific proofs.
