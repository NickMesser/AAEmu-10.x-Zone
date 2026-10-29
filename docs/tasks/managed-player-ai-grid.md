# Managed player AI registration and grid ownership

Priority: P0 for NPC navigation in a scene containing managed players. Status: original
producer and grid insertion recovered; managed player AI ownership remains unimplemented.
This extends [actor grids](../managed-actor-grids.md) and
[NPC navigation](../managed-npc-navigation.md). It does not require native module loading.

## Verified producer

The commented `AI.RegisterWithAI` call in original `Player.Server.OnInit` is not the
ordinary dedicated registration producer. The actual common ActorModel path is C++:

| Phase | Original source / matching PE proof |
| --- | --- |
| ServerUnit construction | `393603D0` installs primary table `39FE5598`; slot240 is `3935CA90`. |
| Initial model association | `3935D260` calls `3925B660`; after creating the actual model, its tail invokes Unit slot240. |
| Actor/model callback | `3935CA90` associates the real CActor and calls model slot1016 with its first argument true. ActorUnitModel table `39FC4168` maps this slot to `392654D0`. |
| Class selection | `392654D0` indexes original uint16 table `3A514260` by environment row and Unit identity kind. Character kind0 selects100 in every row. Dedicated client=false/server=true/multiplayer=true selects row1. |
| Registration | Model slot1280 is `39280060`, which calls `3927F610` with the actual CActor association and selected type100. |

The table rows are:

```text
row0: 100,  5, 100, 200, 100,   5, 200, 0
row1: 100,  5, 100, 200, 100, 100, 200, 0
row2: 100,100, 100, 200, 100,   5, 200, 0
```

Type200 is a no-registration result. Client-only NPC/Mate overrides exist and must not
be generalized to the dedicated Character branch. A false first selector argument
instead requests100 directly. The table does not by itself prove successful allocation:
`3927F610` requires the actual entity, script Properties, model descriptor and proxy
association. Its type100 branch reads the original player properties and movement ability.

## Actual insertion and cached position

`Entity::RegisterAI` is `32012370` (CEntity primary `320CD998`, slot472). It invokes
AI creation and then publishes entity ID, parameters, name and transform. Original
`CAISystem::CreateAIObject3105CE68` requires initialized4612 and enabled6836. For100 it:

1. Allocates the player AI object through `31040E7C`.
2. Inserts it into sorted player vector4328 using `3108C448`.
3. Inserts handle4008 into the actor grid using `311CAAD0`.

The player constructor inherits the base position108=zero and writes handle bucket4008=1.
This insertion precedes `32012370`'s Entity slot1048/`32012510`, which forwards the actual
entity position/direction through AI slot112/`3103DA78`. That setter does not refresh
the actor grid. Initial cached position is consequently constructor zero on this fresh
path, rather than the player's current body position.

Retain the already ported independent cache cadence: `31098E78` refreshes player vector4328
before active Puppet vector4264, each using its own `count/7+1` rotating cursor. With no
active Puppets, neither bucket refreshes. Player membership follows its actual creation
and retirement; Puppet enable/remove behavior is not its producer.

## Current safety boundary

The managed player factory admits a real Living body but has no corresponding type100
AI allocation or grid lease. A complete public AI index can therefore still omit a
source-required actor. Navigation now checks actual physical actors before empty-crowd
avoidance and before continuing a cached route. A body without an exact current managed
NPC/grid classification causes an explicit refusal before another physical move.

This is deliberately conservative replacement admission. It does not classify an unknown
body from its current distance: native grid positions have independent history. It also
does not fabricate a player entry, mark a missing registration non-actor, or claim an
empty bucket proves player absence. Full positive-crowd solving remains a separate gate.

## Implementation tasks

- [ ] Add a concrete scene-owned player AI lease, provisionally
  `ZoneManagedPlayerAi`. Bind the exact `ZonePlayerActor`, actual Unit allocation,
  resolved ActorModel, real player script/class parameters and physical owner.
- [ ] Resolve the shared scene entity-ID allocator before publication. Current managed
  physical admission alone does not create the source GameObject/entity/script identity;
  do not use Unit object ID or a truncated physical ID as a substitute.
- [ ] Produce a real `ZoneAiObjectState` and exact type100 registration, with a distinct
  monotonic actor-allocation order key. Bind `ZoneManagedActorGrids.BindPlayer` at the
  source insertion phase with the source-proved initial cached position.
- [ ] Publish subsequent live AI pose from the sole actual player model/body-to-Unit
  publication chain. Keep grid refresh at the existing accepted global AI frame boundary.
- [ ] On admission failure, unwind every completed registration/lease. On retirement,
  remove only that generation's grid/type-index bindings before releasing their owners.
  A reused Unit/entity ID must not inherit old cache slots, callbacks or pending work.
- [ ] Extend the physical-actor coverage check to recognize the concrete player lease;
  retain refusal for unknown live bodies and unowned positive-crowd branches.
- [ ] Test fresh zero cache versus live pose, skipped refresh, same-cell/cross-cell updates,
  disable/retirement semantics, rollback and same-ID re-entry. Add an actual managed
  player plus NPC control, including a cached route invalidated by an unclassified arrival.

Suggested API shape is `Bind(player, resolvedProfile) -> IDisposable lease`,
`Find(exactHandle)` and `PublishPose(exactPlayer)`. These are planned concrete owner
operations, not implemented callbacks or an externally supplied completeness boolean.
Resolve exact construction/profile dependencies before finalizing their public signatures.

## Evidence

`D:/aaemu-runtime/research/player-ai-census/selection-controls.json` records35 original
`392654D0` machine-code controls with zero differences: all eight kinds across three
environment profiles, false-selector behavior, and the client override branches.
The exact model slot1280 call is intercepted only to copy its arguments. This executes
selection/forwarding, not full native player allocation, scripts or perception.

The same directory retains source extracts, the offline generator and matching PE/table
proof. X2 PE SHA-256 is `50EDCD49D0537AA0F94DB55ECF22008D7FB9ED937EA02121919199B5BF8BB7ED`.
No original DLL was loaded. Current managed player entry remains valid for its documented
physical scope; this task is required before its presence can certify a complete NPC
crowd/navigation census.
