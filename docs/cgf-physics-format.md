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

When any ordinary mesh has the literal case-insensitive name `main`, `30030D90` sets subobject
flag 2 on all other ordinary meshes. Physicalization at `300442F0` skips those ordinary meshes;
helpers bypass that flag test. Both rules apply per node. Descendants are evaluated independently
and retain their complete authored ancestor transforms. There are no ordinary `main` meshes in
the 512 inspected zone-129 resources, so this rule changes future-resource admission without
changing that zone's coverage.

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

Verified type IDs are 0 box, 1 triangle mesh, 4 sphere, 5 cylinder, and 6 capsule. The current
reader supports exact boxes and inline triangle meshes. Other types produce explicit per-part
coverage diagnostics.

### Boxes

The 64-byte primitive contains nine basis floats, a 32-bit orientation flag, center `Vec3`, and
half sizes `Vec3`. Native conversion `3513AC00` generates all `±halfSize` corners. If oriented, it
multiplies by the transpose of the stored world-to-box basis, then adds the center. An
unoriented box ignores the basis. Triangulating these six authored box faces is exact; this is
not the CGF model's render AABB. Native single-box acceleration data follows the primitive and
is replaced by the managed geometry index.

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
triangles. Flag bit 0 similarly selects external vertices. These cases require their exact
reference reconstruction and are currently refused; display geometry is never used as a
fallback. The trailing native BVH is not imported. The managed backend builds its own BVH from
the decoded authored triangles.

## Positive controls and limits

The six zone-129 object/big-object tables reference 512 unique CGF resources in the inspected
10.0.2.13 client. The reader admitted every container without a file/parser failure. Verified
inline default-proxy controls include:

| Resource suffix | Physical vertices | Physical triangles |
| --- | ---: | ---: |
| `01_nuia/04_gw/gw_wall_c.cgf` | 44 | 52 |
| `01_nuia/001_housing/housing_house/housing_b_roof01_1f.cgf` | 4,070 | 280 |
| `01_nuia/04_gw/ruins_stairway.cgf` | 144 | 72 |
| `01_nuia/04_gw/bridge_selemenlake_a1.cgf` | 2,118 | 1,416 |

The container reader bounds file size, chunk count, overlapping ranges, duplicate IDs, fixed
records, property strings and hierarchy depth/cycles. The physical reader bounds allocations,
remaps and triangle indices, presence flags and finite coordinates. Unsupported versions,
external buffers, tetrahedral resources and unsupported primitive types remain visible in
coverage reports. These controls validate format decoding; native-query parity and scene-wide
collision completeness are separate acceptance criteria.
