# Normal authored road and decal collision certificates

`ZoneCellNonBrushCollisionContent` classifies exact parsed object.dat/big_object.dat rows under the original normal authored factory/compile lifecycle. Its first supported profile covers the 10 roads and 97 decals in original `main_world/cells/009_014`. It returns dependency-backed source certificates for player part flag `0x2` and ray part flag `0x8000`. These are part flags, distinct from navigation's physical-entity selection mask.

The owner creates neither renderer nodes nor physical geometry. Successful content decoding, render bounds, a missing managed renderer and a query miss are not used as the no-physical proof. The proof comes from the specific native factories, setters, physical accessors and compiler paths below. Render compilation, textures, decal projection and cell collision coverage remain separate capabilities.

## Exact native lifetime

Addresses refer to the matching original 10.x `cry3dengine_dedicate.dll`, read offline only. Its SHA-256 is `5e22f8338d6e146939f4f22fc0531d78c7c2313547a1863cadb2cb5e3b8d3caf`.

| Operation | Native path and consequence |
| --- | --- |
| Road record load | `30222AA0` consumes the 63-byte header plus all counted Vec3s. Its non-skipped branch allocates `CRoadRenderNode` through `302391A0`, copies flags/material/priority/parameters, translates bounds and vertices by `(engineX<<10,engineY<<10,0)`, calls the actual vertex setter and registers the render node. It does not create a physical entity. |
| Fresh road state | Constructor `302391A0` installs table `3029B6B8`, sets the physical entity at node `+160` and physical geometry at `+168` to null, and has no IEntity association. |
| Road vertices and task | Table slot `+624` is `30239660`. It preserves the four source float parameters, calculates render bounds and calls `302389D0`, which adds the actual road to the renderer compilation queue. Queue admission is not compilation readiness. |
| Packed road material word | Low 24 bits select the actual material table slot. The high byte goes through table `+632` -> `30237F80`, which writes sort priority at node `+152`. The target's one high-byte value 1 is not a physics flag. |
| Ordinary road compiler | `302398F0` samples/clips terrain into render data. It can unregister geometry/remove/destroy a preexisting physical entity, then zeros node `+160`. It does not call the special physical creator. Fresh normal roads retain no physical body. This certificate does not assert that the managed runtime has compiled that render mesh. |
| Road physical accessors | Table `3029B6B8` slot `+376` -> `3023C020` reads node `+160`. Slot `+384` -> `3023C030` can externally install a physical entity. That mutation is outside the admitted normal lifetime. |
| Normal decal load | `30222F10` consumes exactly 111 bytes, allocates through `3005D0F0`, translates bounds/position, resolves the low-24-bit material index and calls table `+624` -> `3005CA30` (`SetProperties`). This stores render/decal parameters and a StreamMaterial reference. There is no model table reference or normal physical creation. |
| Decal physical accessors | Table `302805E8` slot `+376` -> `3005C2B0` returns a full null pointer; slot `+384` -> `3005C2C0` is a no-op. The copied parameters at decal `+160/+168` are direction values, not the road's physical entity/geometry fields. |
| Normal physicalization callbacks | Both tables map slots `+400` and `+520` to no-op `30020AD0`. Slot `+408` -> `30020B40` returns only boolean AL=false; it is not the pointer-valued GetPhysics accessor. |
| Retirement | Road `3023C150` -> `30239280` -> `302381F0` removes the queued/render resources and releases material/vertex ownership. `30238020` cleans an installed physical body if present. Decal `3005E590` -> `3005E3A0` unregisters and destroys its render/decal resources and releases StreamMaterial. No retirement path adds a new collision body. |

Quality/configuration can skip allocation: road factory tests the skip argument, `302462D0` quality and engine CVar `+65*4`; decal tests the skip argument, quality and CVar `+66*4`/`+72*4`. A skipped row is not assumed harmless merely because it was skipped. Both the skipped and admitted normal paths create no relevant physical contribution, so the certificate does not depend on guessing a live quality/CVar value.

`30238680` is a different special-geometry creator. An explicit offline positive control executes it and observes triangle-geometry creation/registration, `CreatePhysicalEntity`, AddGeometry and physical parameter writes. The matching PE audit finds no direct call/jump or data/vtable pointer reference to that address; neither the authored factory nor compiler invokes it. This negative reference audit alone is not a universal absence proof. External special creation, external road `SetPhysics`, editor/game mutation or another physical producer invalidates the bounded normal-lifetime certificate and needs a separate owned profile.

## Actual parsed inputs and dependencies

Call `Classify(content,row,engineCellX,engineCellY)` with the exact `ZoneCellObjectRecord` instance in its `ZoneCellObjectContent`. Equivalent bytes from another content object do not establish membership. The engine indices must belong to the native eight-by-eight loaded window; authored world absolute cell coordinates are not added again.

The immutable result preserves the source content/record identity, file kind, original order/offset/octree node, raw payload SHA-256 and engine-cell indices. Road metadata includes the four authored float parameters, sort priority and copied load vertices. Decal metadata includes the authored projection type, translated position, direction/orientation, radius and sort priority. `TranslatedAuthoredBounds*` retain the translated source bounds; they are not compiled render bounds or collision geometry. Native configuration/material branches can change effective rendering/decal projection, so the copied authored projection type is not presented as the final renderer mode.

The initial road profile requires an even count of at least four finite vertices and finite parameters. Decals admit the target's authored projection types 0 and 2 with finite vectors and positive radius. Unknown families or unowned shapes remain explicit. Parsed brush, vegetation and water rows return `RequiresAnotherOwner`, never a successful no-physical certificate. The existing content reader rejects unknown tags, invalid table references and truncated variable records before this owner is called.

Selected material indices and the original `ZoneCellObjectResource` remain attached to successful/dependency-refused results. Path normalization and actual text material loading reuse `ZoneTerrainMaterialResolver`. A bounded snapshot source retains asset path, complete byte length and SHA-256. The first profile admits an ordinary single material with only `Textures`/`Texture`/`TexMod` and `PublicParams` structure. Multimaterial/submaterial/dual/external directives, ambiguous attributes, unsupported XML and built-in/error fallbacks are refused. All eight actual selected material definitions meet the admitted profile. Shader/texture compilation is not needed to prove that these normal node factories have no physical producer and is not claimed as ready.

Missing definitions yield `MissingDependency`; malformed or failed ordinary asset reads yield `InvalidData` with the actual dependency identity. Unowned semantics yield `Unsupported`. No partial or fallback dependency can yield `NoPhysicalContribution`. Guarded asset-source capability failures still propagate. The existing resolver's fallback semantics are retained as internal behavior, with a failed certificate recorded rather than a successful fallback claim.

The owner borrows the asset source, closes opened streams and freezes definitions/results per owner. Disposal prevents new classifications and clears caches without disposing the source. Immutable results survive only as statements about their frozen input; the scene coverage owner must attach the actual cell generation and invalidate current coverage before unload/replacement. No dynamically mutable road/decal rendering owner is admitted by these source certificates.

Replacement resource limits are 4 MiB per material, 100,000 XML nodes, XML depth 64, 512 material snapshots/requests and 65,536 row certificates. DTDs/external XML resolution and processing instructions are disabled/refused. These are replacement safety bounds, not recovered native allocation limits.

## Original cell evidence

The unchanged original `game/worlds/main_world/cells/009_014/client/object.dat` is 1,017,435 bytes, SHA-256 `237003254a7cecf2accd2ea704eec4790b587f45cc0018ecaf277208c281e10e`. Its parsed rows are:

| Family | Rows | This owner |
| --- | ---: | --- |
| Brush | 675 | Requires concrete brush owner |
| Tagged vegetation | 869 | Requires concrete vegetation owner |
| Decal | 97 | Normal authored no-physical certificate |
| Road | 10 | Normal authored no-physical certificate |
| Water/unknown other family | 0 | No absence assumption is generalized to other files/cells |

The ten roads contain 598 authored vertices and all reference material table index 38, `game/materials/aa_terrain/roads/tr_road_mix_ground_105`. Its 940-byte `.mtl` SHA-256 is `11fce5838e53e1a1a64f5d4d214f727082cad330423c8eb3f66284d8569c1e47`.

| Original row order | Source offset | Vertices | High-byte priority |
| ---: | ---: | ---: | ---: |
| 660 | 161448 | 66 | 0 |
| 1110 | 234355 | 66 | 0 |
| 1195 | 248274 | 66 | 0 |
| 1197 | 249512 | 28 | 0 |
| 1294 | 266433 | 66 | 0 |
| 1309 | 269391 | 66 | 0 |
| 1474 | 290720 | 48 | 0 |
| 1533 | 300072 | 66 | 0 |
| 1553 | 304692 | 60 | 0 |
| 1567 | 307582 | 66 | 1 |

The 97 decals reference seven ordinary materials: `aa_terrain/decals/{tr_decal_dirt_500a,tr_decal_dirt_500b,tr_decals_straw_100a,water_pool_on_concrete_coloredit01}` and `decals/terrain/{dirt_on_road_16_gray1,orcbloody_vill_dirt_01,w_pavingstone_decal_brown}`, beneath `game/materials/`. Their exact bytes and hashes are retained in the portable fixture and dependency report.

## Controls and limits of the evidence

The offline Unicorn harness executes copied original instructions, with declared allocator/memory, quality, load-profiler, render-registration and material-request dependencies. It loads no original engine DLL. It covers all 107 original factory rows and repeats the ten roads at engine cell `(7,3)`, producing 117 comparisons of cursor consumption, translated points, parameters, priority, constructor state and physicalization no-op behavior. The managed tests compare copied factory values bit for bit.

Additional controls execute seven skip/quality/CVar factory gates, verifying null output, full cursor consumption and no registered node. They execute the original compiler's under-four count gate and complete four-point degenerate-strip compiler paths with/without a seeded prior body. The seeded path unregisters geometry, removes it and destroys the entity; the null path creates none. They do not execute a full nondegenerate terrain-clipped renderer mesh. The distinct special creator positive control proves the physical-call recorder detects creation, while actual external SetPhysics controls show that a road accepts that mutation and a decal ignores it. The general normal-compiler no-create conclusion also relies on the audited complete native function/call path, rather than treating a degenerate render path as comprehensive renderer parity.

The portable fixture compresses the unchanged original object.dat for transport only, preserving exact record order and offsets, and includes all eight selected material definitions plus copied instruction outputs. Tests also cover unsupported projection/road shape, unknown/truncated record dependencies, malformed/missing materials, graph directives, byte/depth bounds, exact input membership, failure provenance, immutable snapshots and retirement during an asset-source callback.

Source extracts, the reproducible offline harness and full reports are under `D:/aaemu-runtime/research/managed-road-collision` on the development machine. The later scene coverage owner must combine these 107 certificates with the actual brush, vegetation and 74 XML entity owners before navigation can admit the populated cell. This change does not remove the populated-cell guard or establish original far-return acceptance by itself. See [the populated-cell collision task](tasks/managed-populated-cell-collision.md).
