# Managed actor-grid positions and membership

`ZoneManagedActorGrids` owns the two actor-grid buckets used by the original crowd
and path-obstacle queries. Its cached positions are independent of the live
`ZoneAiObjectState.Position`. The complete nearest-object type index remains a
separate inventory; neither object type nor today's AI position proves grid membership.

The CryAI image used for source and offline controls is
`cryaisystem_dedicate.dll`, SHA-256
`a740d58c6a3c851d84967e9513125c48b9326cbb70462ad1f57e6e60aadfe921`.

## Configuration and first insertion

Original post-zone-load `39332730` obtains the actual current Zone through
`Env264` (Seamless), virtual160/`365696A0`. Zone virtual144/152/160/168 map to
`36540070/80/90/A0`, reading Zone+430/+434/+438/+43C: the authored origin and last
cell. The call to IAISystem virtual680/`31049668` passes those four values to
`311CA8A0`, with two buckets and64m grid cells. Dimension counts are
`(lastCell-originCell+1)*1024/64`. The source cell-coordinate calculation does not
subtract the authored world origin; it clamps engine-local coordinates into the
configured dimension. `ZoneManagedActorGridConfiguration.FromLayout` uses those
actual metadata fields, rather than selecting an assumed eight-cell extent.

Fresh CPipeUser constructor `31163974` initializes its four signed16-bit handle
fields at4008..4014 to−1. Puppet constructor `31171F8C` sets bucket4008 to0;
player actor/type100 constructor `31040E7C` sets it to1. Generic non-actor allocations
do not gain grid membership merely by appearing in the type index.

The ordinary NPC's insertion precedes its first entity/proxy position publication:

1. `Entity::RegisterAI32012370` creates the fresh Puppet. Base `3103D7F8` initializes
   AI position108 to zero and enabled104 to true.
2. AI virtual432/`311594E4`/`3103CC68` establishes the entity ID and association;
   this does not write position.
3. IAIActor virtual80/`3116BC4C` installs parameters and calls `3105A7E8` with
   current actor104 (`IAIActor+232-128`), rather than a forced or newly parsed bool.
   `311CAAD0` inserts the then-current AI108 and copies that value into the grid.
4. Only afterwards `32012370` names the AI and calls Entity virtual1048/`32012510`.
   It forwards the actual entity position/direction to AI virtual112/`3103DA78`.

For that fresh admitted NPC path, initial grid position is therefore zero. A later
proxy refresh does not retroactively replace this insertion-time value. The typed
binding accepts an explicit initial cached position so other registration phases
cannot silently use the same assumption. Type100's `CreateAIObject3105CE68` path
inserts immediately after its constructor; its membership also requires the actual
source insertion point.

## Enable, retirement and cached refresh

`3105A7E8(true)` inserts a Puppet if absent and adds it to the active vector4264.
False removes its grid handle and transfers it to the disabled vector4296. Repeated
true calls do not recopy an already-present cached position. Re-enabling a removed
Puppet copies current AI108. The composing actor-event owner separately writes
enabled104, then calls the grid lease's `SetEnabled`; generic enabled-property reads
do not silently rewrite membership. Player/type100 membership is inserted through
its constructor path and retained until its actual removal; the Puppet enable
shortcut is not applied to that class.

`3103DA78` preserves the previous live AI position and performs its strict `.05f`
component movement test against the incoming setter position. That test sets dirty
byte49 and optionally notifies a listener through `3104ABA8`. It does **not** update
the actor grids. A subthreshold movement can still update a grid on the next selected
cache slice, and a large movement can remain stale until that slice.

The cache prefix of `31098E78` calls `311CAC70` for selected actors. It first checks
that the active Puppet vector is nonempty. With no active Puppets it refreshes
neither bucket, including players. Otherwise it refreshes players/vector4328 first,
then active Puppets/vector4264. Each vector retains its own cursor and processes
`count/7+1` entries with modulo wrap.

These vectors are sorted by unsigned native actor pointer through `3108C448`; their
removal preserves order through `3108C3E4`. The typed API takes an independent
`actorAllocationOrderKey`. Managed production provides its own stable actor allocation
ordering, separately from proxy allocation tokens and Unit/entity IDs. This preserves
the source algorithm for a declared order; it does not reproduce native heap addresses.

`311CAC70` copies all three live AI position components into the existing slot when
the cell is unchanged. A cell change removes the old slot with swap-last semantics
(`311CA750`) and appends to the new cell (`311CAAD0`). The moved last entry's handle
is updated. Native bucket/cell/slot query order is preserved by the managed owner.

The main AI frame `3109B198` rejects a repeated timestamp and requires initialized4612,
enabled6836 and nonzero `ai_SystemUpdate` (CVar376). After establishing current5400
and global delta5408, it performs `310986D8`, `31099968`, then `31098E78` before
later manager actor updates. `AdvanceCacheSlice` ports that cache prefix only and
must run once at the composing scene's accepted equivalent AI frame boundary. It does
not run perception, actor goals, proxy publication, physical stepping or another clock.

## Query and lifetime contract

`QueryBucket(0/1,position,radius)` follows `3108FEB4`: native cell traversal, then
strict full3D `radiusSquared > distanceSquared` over copied cached positions. It does
not test live positions, enabled flags, physical capsule bounds or rendered geometry.
Later caller-specific obstacle/crowd eligibility remains a separate owner.

Every actual type-index registration must have an explicit classification: Puppet,
player or proven non-actor. An unknown public registration makes census coverage
unowned and queries/refresh raise a named refusal. No unknown registration is silently
dropped based on type, distance or lack of a managed NPC wrapper. Root composition
uses the registry's complete internal registration snapshot for this check.

Bindings belong to exact registration/object identities. Retired type-index entries
are removed from grid state; reusing an entity ID does not reuse that binding.
Disposing a grid lease while its index registration remains live makes that entry
unclassified until its actual retirement or a new classification. The grid owner
borrows the nearest-object registrations and never disposes the index's owners itself.

Query objects have immutable copied entries and an owner revision. `IsCurrent` rejects
another owner, cached/membership changes, retirement and incomplete inventory. A copied
query remains readable after retirement but cannot authorize a current census. Selected
poses are validated before any slice update, so a nonfinite public field mutation
cannot partially refresh the earlier player slice. Finite conversion inputs outside
the admitted signed32-bit coordinate profile and dimensions beyond signed16-bit
handle capacity are explicitly refused.

## Independent checks and remaining scope

`npc-actor-grid-native-controls.json` contains11 scenarios/69 actions executed under
Unicorn from original `311CAAD0`, `311CA750`, `311CAC70`, `3108FEB4`, `3105A7E8`, sorted
vector helpers and the cache prefix of `31098E78`. Original direct no-proxy `3103DA78`
is also executed to distinguish its dirty flag from actual grid refresh. The controls
declare actor pointer order and initialize the native descriptor from source-proven
bounds; constructor allocator behavior is not claimed. The prefix stops at310990E9
before the perception loop. CRT/heap, unused perception scratch reservation, source
system getters and the independent direction setter are controlled ABI boundaries.
No engine module is loaded.

The tests compare cache coordinates, handles/slots, query order, slice order/cursors,
enable/re-enable and retirement. Cases include coordinates8000, nonzero world origins,
strict3D radius boundaries, zero radius, clamped outlying positions, swap-last migration,
counts above seven and cursor retention after removals. Additional tests cover unknown
public registrations, independent live/cached positions, stale queries, generation
reentry and atomic nonfinite refusal.

External generator and extracts are under
`D:/aaemu-runtime/research/return-approach/actor-grid-native.py`. Full perception,
unknown actor classes and native heap-address ordering remain separate parity work.
This owner supplies truthful raw census data; it does not claim those later consumers.

Production currently binds admitted NPC Puppets. Ordinary native player registration is a
ServerUnit/model producer, independently of the commented Lua `Player.Server.OnInit` call:
`3935D260` finishes Unit initialization through virtual240/`3935CA90`, then model1016/
`392654D0` selects from table`3A514260` and calls`39280060`/`3927F610`. Character Unit kind0
selects type100 in all three source environment rows, including dedicated server/MP row1.
The managed player factory does not yet own that AI allocation and its cache publication.
The navigation consumer separately
cross-checks actual physical actors and refuses an unclassified player/body before certifying
empty crowd avoidance. A missing public-index registration is not proof of no native actor.
