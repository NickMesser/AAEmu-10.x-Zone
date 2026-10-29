# Managed static water queries

`ZoneManagedWaterQueries` owns the loaded scene's resolved ocean level and copied
serialized lake/river physical contours. It performs no engine-module loading,
physics stepping, character pose writes or water-cache publication. An absent or
unsupported water owner does not establish that a character is dry.

## Query and lifetime contract

```csharp
using var water = new ZoneManagedWaterQueries(resolvedOceanLevel, loadedCellReports,
    ZoneManagedWaterQueryProfile.StaticServerWithoutOceanVortex);
float level = water.GetWaterLevel(actualQueryPoint, actualAreaQueryRange);
float ceiling = water.MaximumPotentialWaterLevelAt(actualQueryPoint);
```

The caller supplies the actual query point and range. Actor `392E3FF0` passes its
entity position plus the scaled swim-threshold Z, with the **full entity-world
AABB diagonal** as range. Area spawner `39348AF0` instead passes its sampled
candidate and the resolved NPC brief **radius**. Original instructions
`39349120..3934913D` confirm that both callers pass server flags 2, argument 3 zero
and a null output pointer to engine virtual `+568` (`0x238`). Neither caller's
range should be replaced with a guessed physical-body radius or model bound.

Queries require a live cell at the point and a complete admitted loaded inventory.
Missing/invalid object data or an unsupported processor produces a named refusal.
Explicitly absent authored files are an empty inventory. `RetireCell` removes that
cell's physical areas and point coverage and invalidates global inventory completeness.
A contour can cross into a neighboring cell; its loss cannot establish a new dry ceiling
there. Navigation also validates exact ordered source-cell references and the initialized
terrain's resolved ocean input. `Dispose` invalidates all queries. This
is an initial-load/retirement owner, not a dynamic area creation or cell reentry
service. Reentry constructs a fresh owner from its actual load reports.

`ZoneTerrainWorldInitialization.ResolveOceanLevel` supplies the ocean input:
authored positive/negative zero becomes the native no-ocean value `-1e6`.
The final query converts any result at or below `-1e6` to `-1e10`.

## Admitted static geometry

The serialized factory `30223480`, lake/river contour methods `300F9130` and
`300F9A80`, and area setup `300F5E90` establish the following behavior:

- Lake/type 2 and river/type 3 volume IDs deduplicate physical areas. Empty lakes
  register their ID; empty rivers do not. Later duplicate rows do not create
  another area. Load-report order determines the managed construction order.
- Lake physical vertices project onto the first render vertex's height. River
  vertices use the plane established from the local bounds and render setup,
  then project/translate in the original order. Raw plane D and render AABBs do
  not substitute for this physical contour.
- Physical depth comes from serialized offset `0x6B`, flow from `0x6F`;
  offset `0x37` is fog density. Native contour-count and orientation rules apply.
- The admitted authored plane normal is `(0,0,1)`. Lakes support the small
  floating-point PCA tilt that the original area constructor can produce from a
  flat contour. Rivers require an exactly horizontal physical frame, a simple
  contour, and an upward nondegenerate canonical two-bank triangle strip.
  Folded/overlapping strips require their actual mesh processor and refuse.

Area constructor `35047C80` computes a perimeter-weighted center, open-edge
covariance, principal axes using bounded eigensolver `35122FA0`, a local contour
and a world AABB. The world AABB precedes planar upper-interval padding. Queries
retain the strict world-AABB and polygon admission of `3504E000`/`3504C770`,
including its rounded edge-search boundaries. Lake intervals extend from
`min(-depth,0)` to the native upper interval; river positive-range queries also
respect the actual planar triangle-surface distance.

The buoyancy plane normal follows area status `3504EE60`, matrix-to-quaternion
`35023EB0` and rotation `30049C60`. `3013FCB0` projects the query point using its
original single-precision operation order, takes the maximum with the ocean,
and consumes at most four eligible medium 0 records in newest-first order. Its
plane-origin `X+Y==0` exclusion is preserved, including consumption of one of the
four records. Native asynchronous streaming/contact order is not claimed.

**Zero-range river queries remain unadmitted.** If a river's actual strict world
AABB could contribute, the public query refuses before returning a value. Native
river mesh function `351C8600` uses its generated spatial grid and `3515AB20`
float ray/triangle predicates; those exact mesh-boundary rules remain to be
ported. Ocean/lake zero-range queries and zero-range points outside all river
world AABBs remain admitted. Positive range is the current actor/spawner profile.

## Conservative dry proof

`MaximumPotentialWaterLevel` and `MaximumPotentialWaterLevelAt` are upper bounds,
not actual sampled water levels. The latter selects a conservative superset using
only actual native world AABBs; it ignores polygon/mesh/range/four-record rejection.
Each area's bound maximizes its linear plane projection over that finite AABB,
then rounds outward to cover every float subtraction, product and sum. This also
covers the small PCA tilt and cancellation at scene-local coordinates near 8 km.

For a known nonnegative scaled swim threshold `T`, the actor query point is
`origin+(0,0,T)`. If `origin.Z-ceiling>T`, the native water distance exceeds `2*T`
for every nonnegative query range, proving the dry branch without inventing an
entity-world AABB diagonal. The ceiling must not be written into an actual native
water cache. This proof also covers the unadmitted zero-range river query branch.

## Required environment profile

The enum is an explicit caller assertion of the supported environment, not an
automatic discovery of engine globals. Dynamic/global areas, transformed or
warped water, moving processors, nonzero authored quality settings, wave/vortex
processors and runtime water edits require their real owners.

For server flags 2, `3013FCB0` invokes the ocean processor only when vortex predicate
`30138570` is true: engine `+800` radius is greater than 1 and `+788/+792` X/Y are
both nonzero. The fresh engine constructor has X/Y 0, Z 100 and radius 0. Environment
loader `301598E0` can replace these fields from `OceanAnimation/VortexPosition`
and `VortexRadius` (radius clamped 0..300; missing attributes default 0). Its X
component has an original integer conversion before storage. The production
composition must establish actual initial/environment producer state before
selecting this profile. The six zone129 cell LevelData XML files have no
`OceanAnimation` element; that inventory alone does not prove the absence of a
separate environment import or later producer.

## Verification and remaining work

The real configured zone129 asset source contains 127 water rows across 12 cell
object files: 19 lake rows and 108 river rows, yielding 19 deduplicated physical
areas (13 lakes, 6 rivers). All 19 admit; no source asset/cache is modified.

The external evidence directory `D:/aaemu-runtime/research/managed-water` contains
the read-only inventory, source extracts, `native-area-probe.py`,
`native-area-controls.json` and `native-kernel-comparison.json`. The oracle executes
original PE instructions under Unicorn; it does not load engine DLLs. Executed
code includes original area construction/PCA, river mesh creation, status and
quaternion rotation, buoyancy parameters, area selection and complete flag 2
`GetWaterLevel`. CRT memory allocation/copy/fill, worker classification, physical
service lookup and area-grid publication are explicit ABI callbacks; original
medium 0 list insertion and geometry predicates still execute. Each main query
control uses one isolated real area and ocean 50. Factory projection is separately
source-derived and tested; the oracle receives the copied projected contour and
does not execute the complete serialized factory/streaming chain.

The 21 constructor controls comprise 19 real areas plus concave/8 km controls. All
nonzero frame, plane-normal and bound fields match bit-for-bit; 14 frames differ
only in signed-zero bits. Of 1,323 complete query samples, 1,233 are admitted and
all 1,233 result bits match. The 90 conservatively refused zero-range river samples
include the two known native mesh-boundary differences. Every native sampled
level, including refused samples, is at or below the reported conservative bound.
The 13 focused tests cover the distinct caller range behavior, contour and strict
boundaries, native rounded lake vertex, river refusal/strip admission, projection,
ID deduplication, coverage/retirement, four-record/origin filtering, invalid inputs and
the 8 km certificate.

Original image SHA256 values:

- `cryphysics_dedicate.dll`: `b558cd33b93177d3163ecf483368e3c7a0612507257ad73bc87a73ee872e2e20`
- `cry3dengine_dedicate.dll`: `5e22f8338d6e146939f4f22fc0531d78c7c2313547a1863cadb2cb5e3b8d3caf`

The exact river mesh-grid/closest-triangle branch, nonhorizontal/warped/flow
refinement, ocean vortex/waves, dynamic water ownership, streaming reentry and
cross-area ordering remain explicit implementation work. Water level ownership
does not by itself implement buoyancy forces, swimming, flow locomotion or async
bottom-ray lifetime.
