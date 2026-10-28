# Managed CGF physical geometry

The managed readers use authored physical proxies. They do not turn render bounds or display
meshes into collision. This format description records the dedicated 10.x loader paths that
support `ZoneCgfContent` and `ZoneCgfPhysicsGeometry`.

## Verified containers and references

The file starts with `CryTek\0\0`, a little-endian `0xFFFF0000` file type, version, and chunk-table
offset. The header occupies 20 bytes. The table starts with a 32-bit count. Version `0x744` has
16-byte entries `(type, version, offset, id)`; chunk lengths follow the next physical offset or
table/file boundary. Version `0x745` adds a fifth word containing the chunk length. Known content
chunks retain their own 16-byte `(type, version, offset, id)` headers.

The supported content profiles are:

| Chunk | Type | Version | Relevant layout, including local header |
| --- | --- | --- | --- |
| Node | `0xCCCC000B` | `0x823` | name at 16, object ID at 80, parent ID at 84, material ID at 92, 16-float TM at 100, property byte count at 216; fixed size 220 |
| Material name | `0xCCCC0014` | `0x800` | name at 24, physicalization type at 152, submaterial count at 156, up to 32 submaterial chunk IDs at 160; fixed size 424 |
| Mesh | `0xCCCC0000` | `0x800` | four physical-data chunk IDs at 108; fixed size 276 |
| Physical data | `0xCCCC0018` | `0x800` | serialized byte count at 16; serialized bytes start at 40 |
| Export flags | `0xCCCC0015` | `1` | flags at 16; fixed size 180 |
| Data stream | `0xCCCC0016` | `0x800` | stream type at 20, count at 24, element size at 28, data at 40 |
| Mesh subsets | `0xCCCC0017` | `0x800` | count at 20, 36-byte subset records at 32 |

`CLoaderCGF` at `301D3A30` loads only positive physical-data references. Slot 0 becomes native
geometry type 4096, the default blocking geometry. Slot 1 is 4097, nocollide; slot 2 is 4098,
obstruct; slot 3 retains type 4099. The scene currently admits slot 0 for living-character
collision. The decoder preserves all slots separately.

The physical-data copy is verified by `301D1FD0`. This layout is not
`SMeshPhysicalProxyHeader`: that separate 16-byte structure belongs to compiled bone proxies.

## Transforms and material identity

Node loader `301DB9A0` takes the 3x3 basis directly from the authored TM, transposed into the
engine's column-vector matrix. TM translation is in centimeters and is multiplied by `0.01`.
For `System.Numerics`, keep the raw 3x3 row order and put scaled raw words 12–14 in `M41..M43`.
Physical geometry coordinates are already meters and must not receive this conversion again.

Parent loader `301D1B70` composes the node's local TM with every ancestor. With row vectors the
order is `nodeLocal * parentLocal * grandparentLocal`. The authored brush transform and local
cell translation follow this CGF transform.

`CLoaderCGF` at `301DB9A0` classifies a mesh as a helper when its name starts with `$` or contains
`PhysicsProxy`, `$collision` or `$physics_proxy`, using case-insensitive substring comparisons.
`CStatObj::LoadCGF` at `30030D90` routes helper names containing `$occlusion` to renderer ownership
and names containing `$lod` to the visual LOD array. The matching function `301DD1E0` searches
the entire name, not only its prefix. Those nodes must not publish ordinary blocking instances
even when their meshes contain physical-data references. The reader retains their data and
exposes `IsCollisionSource` and `CollisionExclusionReason`. Physical proxy helpers remain eligible.
Helper names starting with case-insensitive `$aimpoint` do not create an independent child
static object and are also excluded from independent collision publication.

When any ordinary mesh has the literal case-insensitive name `main`, `30030D90` sets subobject
flag 2 on all other ordinary meshes. Physicalization at `300442F0` skips those ordinary meshes;
helpers bypass that flag test. Both rules apply per node. Descendants are evaluated independently
and retain their complete authored ancestor transforms. There are no ordinary `main` meshes in
the 512 inspected zone-129 resources, so this rule changes future-resource admission without
changing that zone's coverage.

The direct root and ordinary subobject physical sources are separate. Export flags bit 0 forces
direct mode. With that bit clear, more than one ordinary mesh, or a helper/nonmesh `$joint` or
`$cutdown` node, selects compound mode. An absent export-flags chunk retains the native
constructor's bit-0 default of 1. Direct mode selects the first special physical proxy helper
(`PhysicsProxy`, `$collision` or `$physics_proxy`), otherwise the first ordinary mesh. Selection
precedes visual helper admission and the `main` flag pass. `HasDirectPhysicsGeometry` therefore
reports raw selected physical data, including undecoded shapes and nondefault slots.

An ordinary mesh must exist before this root selection; native `LoadCGF` refuses helper-only
files. The reader retains their decoded data, publishes `HasOrdinaryMesh=false`, and records
that admission failure. Direct mode does not independently publish ordinary subobjects. It
publishes the selected raw root once, while independently admitted helpers retain their own
node transforms; a selected special helper can therefore supply both the raw root and its
separately transformed child. Visual helper exclusion affects the child publication, not the
earlier root registration. `DirectPhysicsSource` and `IsPhysicalDataComplete` keep these two
publication contexts separate.

Native direct-root publication uses the selected serialized physical geometry without its node
TM. `30047F40` passes untransformed source vertices and the serialized blob to the physical
loader; direct brush physicalization supplies identity local parameters. A 36-ray native oracle
check over five selected roots with nonidentity TMs confirmed this behavior. The reader retains
the authored `WorldTransform`; the scene applies identity for the selected direct root and the
authored transform for separately admitted subobjects. A nonuniform brush over native direct
physical geometry still requires its native admission gate.

Material loader `301C4750` uses a compiled material name containing `/` directly. Otherwise it
prepends the CGF file's directory. For example `old/box_a.cgf` names `learning_ob01`, resolving to
the sibling `old/learning_ob01.mtl`; the CGF basename is not the material identity.

## Serialized physical geometry

`CGeomManager` save/load at `3514B1A0`/`3514B340` has this version-1 envelope:

| Blob offset | Value |
| --- | --- |
| 0 | 32-bit serialization version, currently 1 |
| 4–67 | Legacy 64-byte physical-geometry wrapper |
| 8–19 | Three inertia values |
| 20–35 | Inertial quaternion, native `w,x,y,z` order |
| 36–47 | Inertial mass-center origin |
| 48 | Volume |
| 52 | Old reference count; the native loader resets this |
| 56 | Default local material slot (`surface_idx`) |
| 68 | Geometry type |
| 72 | Type-specific geometry payload |

Reserved/pointer words in the wrapper can contain uninitialized storage and are not interpreted.
The quaternion and origin are inertial properties, not another placement transform. Applying
the wrapper origin to a decoded box would move its center twice.

Verified supported type IDs are 0 box, 1 triangle mesh, 4 sphere, 5 cylinder, and 6 capsule.
Other types produce explicit per-part coverage diagnostics.

### Boxes

The 64-byte primitive contains nine basis floats, a 32-bit orientation flag, center `Vec3`, and
half sizes `Vec3`. Native conversion `3513AC00` generates all `±halfSize` corners. If oriented, it
multiplies by the transpose of the stored world-to-box basis, then adds the center. An
unoriented box ignores the basis. Triangulating these six authored box faces is exact; this is
not the CGF model's render AABB. Native single-box acceleration data follows the primitive and
is replaced by the managed geometry index.

### Curved primitives

`CSphereGeom::Load` at `351AFC60` reads 16 bytes: center `Vec3` and radius. The cylinder loader
at `35144D40`, also inherited by capsules, reads 32 bytes: center `Vec3`, axis `Vec3`, radius,
and half-height, followed by a retained 32-bit tessellation count. A 68-byte single-box
acceleration tree follows. Capsule containment at `3513F7A0` confirms that its half-height is
the center-segment half-length; the spherical caps extend its axial bounds by another radius.

The reader publishes immutable `ZoneCgfPhysicsPrimitive` descriptors, retaining the authored
axis, center and dimensions. A sphere's unused axis and half-height are zero. Curved primitives
have no generated vertices or triangle indices. Analytic queries belong to the physics provider;
the content reader never approximates them with a faceted mesh or their bounds.

### Triangle meshes

`CTriMesh::Load` at `351D83B0` reads:

1. Four 32-bit integers: vertex count, triangle count, maximum vertex valency, flags.
2. A vertex-map presence byte, followed by `vertexCount` unsigned 16-bit entries when present.
3. A foreign-triangle-map presence byte, followed by `triangleCount` unsigned 16-bit entries.
4. Inline `Vec3` vertices when the external-vertex flag is clear.
5. Three unsigned 16-bit indices per triangle.
6. A material-array presence byte and one native signed `char` per triangle when present.

The vertex map remaps the loaded triangle indices. Material byte `0xFF` is `-1`, not material
slot 255. Native material lookup `35037930` replaces a negative triangle slot with the part's
default slot, then resolves it through the selected CGF/brush material mapping. A positive
out-of-range mapped slot uses mapping entry zero.

If a foreign map exists and flags do not include `0x400000`, geometry refers to external source
triangles. This branch has no inline vertices, indices or material-presence byte. For each
serialized foreign triangle `F`, native `351D83B0` selects `sourceIndices[3F..3F+2]` and the
source triangle's signed material byte, then applies the optional vertex map. Flag bit 0 can
also select external vertices while indices and materials remain inline.

The verified references come from the same Mesh0800: source position stream 0 is referenced at
mesh offset 44, source index stream 5 at offset 64, and the subset chunk at offset 36. Loaders
`301E0090` and `301E0340` require DataStream0800, matching counts, 12-byte `Vec3` positions and
2-byte indices. The source positions are already meters. Each Subset0800 record contains first
index at 0, index count at 4, first vertex at 8, vertex count at 12 and material ID at 16.
Physicalizer `30047F40` assigns the low material byte over each subset's triangle range.

Only the serialized foreign faces become collision triangles; unrelated source faces are never
used as a fallback. Source buffers are resolved lazily and cached by mesh identity, while decoded
physical geometry is cached by both physical chunk and source mesh identity. Missing streams,
unsupported element profiles, out-of-bounds foreign indices and remaps remain named failures.
Subset gaps are also explicit: native material scratch storage is process-global and uncovered
bytes can retain a previously loaded CGF's values, so they cannot be reconstructed as a stable
authored default. The decoder requires authored material coverage for each selected foreign face.

The trailing native BVH is not imported. The managed backend builds its own BVH from the decoded
authored triangles.

## Positive controls and limits

The six zone-129 object/big-object tables reference 512 unique CGF resources in the inspected
10.0.2.13 client. The reader admitted every container without a file/parser failure and decoded
all their blocking proxy parts: 605 parts, including 41 referenced-source meshes and 48 curved
primitives. The triangle resources contain 149,434 authored faces; curved primitives are not
tessellated. This is content coverage, not a claim that every scene placement passes native
physicalization or that every engine collision feature has been replaced. Verified inline
default-proxy controls include:

| Resource suffix | Physical vertices | Physical triangles |
| --- | ---: | ---: |
| `01_nuia/04_gw/gw_wall_c.cgf` | 44 | 52 |
| `01_nuia/001_housing/housing_house/housing_b_roof01_1f.cgf` | 4,070 | 280 |
| `01_nuia/04_gw/ruins_stairway.cgf` | 144 | 72 |
| `01_nuia/04_gw/bridge_selemenlake_a1.cgf` | 2,118 | 1,416 |

The container reader bounds file size, chunk count, overlapping ranges, duplicate IDs, fixed
records, property strings and hierarchy depth/cycles. The physical reader bounds allocations,
remaps and triangle indices, presence flags and finite coordinates. Unsupported versions,
source element profiles, tetrahedral resources and unsupported primitive types remain visible in
coverage reports. These controls validate format decoding; native-query parity and scene-wide
collision completeness are separate acceptance criteria.
