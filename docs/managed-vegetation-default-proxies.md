# Managed default vegetation proxies

This increment adds the first independent vegetation publication seam. It does **not** establish
populated-cell collision coverage or admit the original NPC far-return control. The production
Host, Navigation and activation coverage checks are unchanged. The remaining cell families and
vegetation branches are tracked in [the populated-cell task](tasks/managed-populated-cell-collision.md).

## Source and admitted profile

`ZoneWorldVegetationContent` reads the actual `game/worlds/{world}/vegetation.xml` through the
configured asset source. The source-order rows and complete attributes remain copied; later rows
replace earlier equal IDs, and publication order is sorted by the final ID map. This follows
`30107F70`, `30109F90` and `30108AD0`. The current bounded parser requires the known numeric
attributes and finite floats rather than inventing missing native constructor values. It preserves
integer and float attributes separately; it does not implement the group's rendering/editor behavior.

`ZoneManagedVegetationResources` borrows the scene's existing **distinct** character and ray
`ZoneManagedStaticGeometry` owners. Its accepted resource is a noncompound, directly selected
root with exactly decoded default 4096 physics, actual root material mapping, and no unowned
independent helper physics, root special slots or foliage-info chunk `0xAAFC0005`.
General triangle meshes remain surfaces, decoded primitive boxes retain their solid volume,
and sphere/cylinder/capsule proxies use the existing analytic kernels. CGF node transforms do
not replace the native direct-root identity transform.

The ported configuration/gates are:

- The serialized group is a signed 16-bit vegetation-catalog ID, separate from object.dat's model table.
  Missing/negative group references are unsupported and publish no handles.
- Actual `e_vegetation`, `e_obj_quality`, `e_vegetation_min_size`,
  `e_vegetation_create_collision_only` and, when needed, `e_view_dist_ratio_detail` inputs are
  explicit `ZoneManagedVegetationPolicy` fields. Spec 7 requires the actual detail ratio;
  native `302462D0` permits spec 0, otherwise quality 0 or spec<=unsigned quality, with detail 0
  excluding spec 7. A configured skip is reported separately from an unsupported dependency.
- This first profile accepts minimum-size 0 and finite nonnegative authored scale. A nonzero
  minimum size needs the actual CStatObj radius and remains unsupported. Raw scale 0 passes
  that admitted minimum-size gate and then clamps to 0.1; the upper clamp is 5. For every admitted
  resource, the collision-only gate's recursive HasPhysics 4096 test is true.
- Group material overrides remain unsupported. Actual root material slots and mapped physical
  surface IDs are copied from the existing material/surface owners; no basename-derived material
  or physical 0 fallback is substituted for a missing dependency.
- Explicit angle bytes X=`0x39`, Y=`0x3A`, Z=`0x3C` use the float order
  `(float(byte)*1.4117647f)*0.017453292f`, original Euler `30106900` and scale multiplication
  `30106AC0`. A group requesting terrain alignment with zero explicit X/Y angles is refused
  because that branch calls the unowned `300CD590` terrain-normal operation. Nonzero explicit
  X/Y bypass that branch, as in the original.
- Sector translation is `((engineCellX*4+sectorX)<<8,(engineCellY*4+sectorY)<<8,0)`,
  X outer/Y inner; tagged vegetation uses `(engineCellX<<10,engineCellY<<10,0)`.
  Absolute authored cell coordinates are never added again.

`30103DC0`'s default physicalizer uses the low geometry word `0x04018FFF` and separate collider
word 1 from `0x0000000104018FFF`. Both character bit 2 and ray bit `0x8000` are present. The copied
report exposes these two words separately. The source default-slot compilation checks actual used
physical materials for break-energy flag `0x400`; root flag `0x100` then adds geometry bit `0x800000`
unless the placement's raw Flags20 bit 2 suppresses that branch. These are publication/query flags;
breakage dynamics, bending and destruction are not implemented by this seam.
`300400D0` compiles per-triangle flags only for native mesh kinds; boxes decoded into twelve query
triangles still follow the primitive wrapper slot rule. A negative box/curved wrapper slot skips
that flag compilation even when its query material later maps to a break-energy surface.
The direct vegetation root reads compiled break flag `0x100` but does not read compiled
vehicle-only flag `0x80` or substitute mask64. The brush filter and `300442F0` child flag branch
must not be applied to this root by analogy; a dedicated regression preserves player/ray
publication with an actual vehicle-only physical material. Child publication remains unsupported.

## Exclusion ownership

The final `302221D0` gate calls `301597D0`. It compares the translated **authored bounding box**
against the engine's current runtime exclusion OBB table with `30164440`; it does not replace the
physical proxy with that authored box. `ZoneManagedVegetationExclusions` is a mandatory copied,
immutable snapshot of that table and the actual current engine origin. Its entries contain source
ID, orthonormal basis, half extents, local center and world position. Touching OBBs exclude the row;
no collision-tolerance epsilon changes this decision.

The engine constructor initializes the table pointer/count to 0 (`C3DEngine+24096/+24104`).
For a fresh static scene with no admitted runtime exclusion producers, the truthful initial
snapshot is `new ZoneManagedVegetationExclusions(actualEngineOrigin, [])`. This is an explicit
source state, not a default selected because no geometry was found. `3015C1E0` adds exclusion
records and also invokes other removal operations; `30158690` removes records. This increment
does not implement those later mutation/destruction operations or automatically remove proxies
after the immutable snapshot changes. A production integration must own that lifetime or restrict
admission to the proven fresh-scene/no-mutation profile. Exclusions are not a caller-supplied
coverage boolean.

## API and lifetime

Create one `ZoneManagedVegetationResources(assets, materials, layout, catalog, characters, rays,
policy, exclusions)` for the scene. `BeginCell(exactLayoutPlacement)` returns an exact-generation
`ZoneManagedVegetationCellLease`. `LoadTaggedObjects(content)` consumes vegetation rows from one
typed object input; `LoadSectors(content)` consumes the sixteen vegetation.dat sectors. Repeating
the same input kind is rejected. Every report retains source kind/sector, row order/offset,
signed group, asset path, transform, separate part/collider flags, installed counts and diagnostic.
Outcomes are `Published`, `SkippedByConfiguration`, `SkippedByExclusion` and `Unsupported`.

Immutable mesh/BVH or analytic resource data is shared per normalized CGF path. Only the admitted
resource profile enters the character/ray inventories. Each placement stages its full admitted part
set, rolls back both inventories on failure, and validates the exact cell generation after external
asset/material callbacks. A callback that retires/replaces the cell cannot cache or publish that
stale load. Replacing a cell invalidates and removes its prior generation before publishing the
new one. Cell/scene disposal removes only owned handles; foreign scene geometry survives, and
stale disposal cannot remove replacement handles. Reports and `IsCurrent` describe generation/history;
they do not certify that a caller has not separately cleared the borrowed target geometry. Any future
coverage owner must also verify live handles, complete inputs and geometry revisions. The asset source
and target geometries remain caller-owned. This is a scene-thread owner, not a concurrent loader.

Eager retention of admitted resources replaces native on-demand allocation scheduling `301008C0`.
It preserves query targets that on-demand creation can produce; an unallocated source object is
never treated as proof of physical clearance.

## Verification

The portable `ManagedVegetationTests` suite covers catalog replacement, original math controls,
cell/sector translation, direct-root transforms, true capsule contact, material IDs, curved defaults,
scale clamp, source exclusion bounds/origin, separate flags, quality/size gates, unsupported
dependencies, generation replacement, foreign-handle preservation and retirement inside an asset
callback and synchronous publication reentry. Its focused isolated run passed 13/13.

`Fixtures/vegetation-native-controls.json` records offline original instruction execution from
`cry3dengine_dedicate.dll`, SHA256
`5e22f8338d6e146939f4f22fc0531d78c7c2313547a1863cadb2cb5e3b8d3caf`:

- 16 `30102840` transforms, including `30106900` and `30106AC0`: 192/192 output float bits agree,
  including signed-zero cases. The only callbacks are actual CRT `sinf(float)`/`cosf(float)`.
- 140 `30164440` exclusion controls: 87 overlaps/53 misses, all decisions agree. Inputs include
  three-axis rotations, touching/separated faces, local centers and coordinates near 8000 m.

No engine DLL was loaded. These are transform/exclusion controls, not an original live physicalizer
comparison or complete world collision oracle. Generator and source extracts are retained under
`D:/aaemu-runtime/research/managed-vegetation-stage/` and
`D:/aaemu-runtime/research/scene-collision-next/` respectively.

A separate managed read-only probe used the configured original 10.x asset source. It read 4145
world groups and 7127 vegetation rows in authored cell 009_014. The strict profile admitted four
group 335 `jungle_tree_large_big_bright_green_fallen_e.cgf` placements: three tagged object.dat rows
(source orders 464, 791, 866) and one vegetation.dat sector 10 row (order 1212). Each real serialized
triangle witness answered both target inventories and a true capsule overlap, with material 123;
cell retirement reduced both owned inventories from 4 to 0. The report is
`D:/aaemu-runtime/research/managed-vegetation-stage/vegetation-managed-content-results.json`.
These four managed admissions are source-backed; they are not a live original collider census.

Every other group remains explicitly unsupported in this first seam. In particular, the other
default-bearing groups need root specials, foliage or compound/helper publication. Special-only
and no-physics resources are not yet certified nonblocking by this owner. XML entities, roads,
decals and all other populated-cell families remain separate work. Production coverage stays false.

Final combined validation passes 1,019 tests, 0 failures/skips, and an analyzer build with 0 errors,
64 existing warnings and no new warning diagnostics. A fresh original-content probe executes the
same tested core SHA256 `2163927F18A207C502FD7663676A3FAF670595ABC8B36DBBBA1D95B3E1E89609`.
Its four real triangle witnesses pass both ray inventories and nondegenerate capsule overlaps
(radius 0.05 m, segment half-height 0.2 m), preserve material 123 and retire both four-instance inventories
to 0. All 192 transform components are bit exact and 140 exclusion decisions match. The probe records
source input hashes and rejects an executing core that differs from the tested reference.
Final evidence is under `D:/aaemu-runtime/research/managed-vegetation-final-probe/` and
`D:/aaemu-runtime/research/collision-leaf-batch-evidence.json`. The production Host/navigation
composition and the previous accepted world-entry application are unchanged by this component batch.
