# Managed NPC regions

`ZoneManagedNpcRegions` implements the original fixed-origin region lookup used by NPC position checks. Construct it from the scene's admitted `ZoneWorldMapLayout`, use `GetRegion(localModelPosition)` for engine-local model/entity metres, and compare the result by identity with `CurrentRegion`. A region is a canonical `ZoneWorldMapZone` row. Duplicate numeric IDs or names do not merge rows. This is distinct from a database area, a Unit's received region field, terrain residency and the AI proxy's eye position.

The owner borrows immutable world content. Disposal seals its queries without disposing that content or changing another scene's region identity. It does not relocate NPCs, publish poses, switch zones or change the origin. Client streaming, editor sector edits and origin changes require their actual event owners; this owner implements the dedicated fixed-origin load profile.

## Original lookup and load

The environment field264 is `ISeamlessSystem`, verified by its concrete `SeamlessSystem` table at `3661F478`; it is not the adjacent WorldSystem at environment256. The existing native world-layout validator independently verifies both concrete services and their environment ownership.

| Original function | Actual operation |
|---|---|
| `SeamlessSystem` virtual160, `365696A0` | Returns the selected X2Zone pointer stored at16. |
| `LoadZone` `365704D0` | Stores the selected Zone at16 and its owner World at8. The non-streaming branch obtains the Zone origin through virtual144/152 and stores cell coordinates at160/164. |
| `SeamlessSystem` virtual176, `3656A740` | Computes each local cell with single-precision multiplication by1/1024 and `CVTTSS2SI`, adds the origin with32-bit integer arithmetic, and asks the full World grid for the cell. It then computes each sector as `(position - float(localCell << 10))/64`, again truncating toward zero. Z is never read. |
| X2World table`36620658`, virtual128, `36563740` | Rejects negative or out-of-grid cell coordinates; otherwise returns the X-major grid entry at `x * cellYCount + y`. |
| X2Cell table`36620408`, virtual64, `36563BA0` | Rejects negative or out-of-range sector coordinates; otherwise returns the X-major sector's Zone pointer. Invalid sectors log and return null. |
| Cell constructor`36566060` | Allocates a zeroed16-by16 sector table. |
| World XML load`365676F0` → Zone load`36565C30` | Loads physical Zone rows in source order. Each authored sector assignment writes its actual Zone pointer through X2Cell virtual72/`36563B60`. Later assignments replace earlier ones. Empty cell rows still add cell membership but fill no sectors. The separate fill-empty editor operation`36563AE0` is not called by this path. |

The1024/64 conversion constants belong to the original region query; it does not derive them from XML resolution or multiply by terrain unit size. A small negative position can consequently still address sector0 of the origin cell:−63.999996m truncates to local cell0 and sector0, while−64m produces invalid sector−1. Applying floor or rectangular containment would change this behavior. Nonfinite X/Y and out-of-range conversion inputs cannot resolve a valid sector and return null; nonfinite Z remains irrelevant to this particular query.

The lookup does not consult the selected Zone's loaded cell list or terrain. A sector belonging to another Zone can be returned from a loaded shared cell, and an authored sector in an unloaded neighboring cell can also resolve. `CurrentRegion` remains the selected Zone independently of the query result.

## Original zone129 data and controls

The original `main_world/world.xml` has164 physical Zone rows. Zone129 (`w_gweonid_forest_1`) has origin(9,14) and loads six cells: (9,14), (11,14), (11,15), (9,15), (10,15), (10,14). Only469 of their1536 final sectors belong to129. Other sectors belong to140,143,145,181,182,195 and267. In particular, loaded cell(11,14) has no final sector owned by129. Thus even the selected Zone's cell list cannot substitute for region ownership.

The test XML is a source projection retaining all164 row identities, all six selected cells' source assignments, and one unloaded neighbor(12,14). Its provenance is the original XML SHA256`83b94752055ff1750a3af8cb49fd5c57a5ac0afca8a82c419655904e6665005b`. It does not represent omitted world cells as an assertion about their real ownership.

The JSON controls were produced by executing the original x64 bytes of `3656A740`, `36563740`, `36563BA0` and `365696A0` in Unicorn with explicit world/cell/sector memory populated from the original XML. No CryEngine DLL was loaded. Only the invalid-sector logging import was replaced by a return instruction; its result is discarded before the original null return. This validates the copied query instructions, not a complete native engine startup or a relocation/position-check consumer.

`ManagedNpcRegionsTests` compares every1536 sector center and35 boundary/overflow/NaN controls with those instruction results. Additional synthetic controls cover sparse cells, unassigned sectors, later writes, duplicate Zone identities, nondefault XML physical properties, loaded-versus-unloaded membership and independent scene retirement. Tests require no original client, emulator or native library.

The original module SHA256 is recorded in the JSON. Reproduction and source extracts are in `D:/aaemu-runtime/research/npc-region-oracle.py` and `npc-region-*.c`. The NPC CheckPos consumer at3933D0E0 requires both nonnull lookup/current pointers and exact identity before using its authored idle/talk/spawn anchor. Relocation, per-NPC replication reset, group/leader and posture effects remain separate owners.
