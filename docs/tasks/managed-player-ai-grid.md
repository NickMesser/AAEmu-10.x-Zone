# Managed player AI registration and grid ownership

Priority: P0 for NPC navigation in a scene containing managed players. Status: a concrete
fresh-construction owner and controls are frozen in an external stage; production player
AI/grid ownership and moving-player pose remain unimplemented.
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

## Actual model, script and parameter prerequisites

The automatic Player script path requires actual character descriptors. CGame table
`39FC6E48` slot752 is `392ABBE0`, which scans the character rows through `39AEF8D0`.
Loader `39B03750` resolves SQL `characters.model_id` into each row's model pointer at+40.
Character Unit kind alone does not prove that predicate. `3202C5C0` generates the actual
ActorModel table, then runs original `Player.lua`/`CreatePlayer` for matching models.
The shared model-class owner must preserve that ordering before NPC or player classes
are derived from the source table. `392B9010` separately selects the C++ creator from
ActorModel.MovementId:1/3 Quadruped,2 Monster,otherwise CPlayer. The staged profile refuses
the non-CPlayer branches.

The stage executes original `CreatePlayer`, `common.new` and `Player.Server.OnInit`.
That actual constructor sets `thrusterAISoundRadius=38` and calls the original empty
`Player.OnInit`; its commented registration is not replaced with a fabricated Lua call.
The original-resource control uses captured Character387/model17 and its resolved
ActorModel, not a generic Character-only script table.

`3927F610` reads actual `Properties.groupid`, `commrange`, `Perception.sightrange` and
`AIMovementAbility`. It overwrites script `species` from actual Unit32300 (`39258C00`,
the managed Unit faction field). Negative player pass radius uses scaled ActorModel.Radius
and the first stance-ID2 capsule override; if ID2 is absent, it retains model radius.
Do not reuse the NPC helper's last-stance fallback. Negative path radius copies the resolved
pass radius, and avoidance radius adds that path radius. The player branch does not use the
NPC1.1 path-radius inflation branch.

The enclosing common tail also matters for both object types. Original instructions
`3927FEDC..3927FF0D` write AIParams177/178 from ModelDesc144 bits2/8, and180 from Unit kind.
Loader `39ADC040` maps those two bits to `Big`/`ForceNavigateAroundObject`. These writes
occur after either the Player or Puppet-specific branch; constructor defaults or the
inner `392ADC00` parser alone cannot establish false values. Root owns the corresponding
NPC common-tail flag correction; the player stage supplies independent executed evidence
and does not change the existing radius implementation.

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

All checkboxes below describe production work and intentionally remain open. External
staging and its controls do not admit a player into the production navigation census.

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

The frozen stage's concrete API is `ZoneManagedPlayerAi.BindFresh(exactPlayer)`,
`Find(exactHandle)`, `FindEntity(entityId)`, `ObserveUnownedPlayerInput(exactPlayer)` and
`ValidatePhysicalPublication()`. It returns an owned lease with actual script, AI object,
type-index registration, player-grid lease, separately reserved identities and copied
parameters. This is a bounded staging API, not a replacement for the still-required live
player controller/pose producer.

## Remaining integration and lifetime hooks

1. Create one scene-owned `ZoneManagedSceneActorIdentities` before any NPC or player
   admission. Share it with `ZoneManagedNpcRuntime`'s entity reservations,
   `ZoneManagedNpcActorScene.Prepare`'s AI allocation-order reservations,
   `ZoneManagedNpcProfileFactory`'s proxy reservations and the player owner. The three
   domains advance independently, including failed attempts; numeric aliases or separate
   counters starting at1 cannot stand in for one shared scene authority.
2. Share the actual `ZoneNpcActorModelScripts` owner and real `ZoneCharacterContent`.
   Apply the source automatic Player transformation at first model-class construction,
   before deriving NPC/player classes. Do not generate a second conflicting model global
   or repeatedly wrap `MakeSpawnable` through separate caches.
3. Compose one actual registration lifecycle for NPCs and players. Native ctor
   `3107FD08` starts initialized4612=false/enabled6836=true; `3104A3A8` initializes,
   and `31097660` changes enabled. The stage's `InitializeRegistration` validates its
   live bootstrapped managed owners but does not claim complete native AI initialization,
   perception or navigation. Enabled changes must not become a player-only shadow flag.
4. Bind at `ZoneSceneFrameCoordinator.RegisterAdmittedPlayer` after the actual
   AddActor/Teleport/BindPhysicsAuthority sequence and before any input or Step. Preserve
   the real Unit/model/body association and independent managed entity/script/proxy identity;
   the native-only `ZonePlayerActor.EntityId` property is not a substitute. Original
   `3935CA90` ignores the AI creation result, so a failed AI profile must not automatically
   be misrepresented as failed physical/Unit admission.
5. Dispose the exact typed player lease at actor retirement before its Unit/entity owners
   disappear. Remove grid and type-index membership before releasing script/proxy state;
   AI retirement must not remove the caller-owned physical body. Scene reservations and
   actual CActor construction history outlive individual AI leases, preventing old or
   reposed actors from being rebound as fresh. Same-ID re-entry needs a new actor generation.
6. Port the real live CActor/controller producers before extending pose support:
   CActor movement output876, independent view quaternion848, controller look target376
   and cached view position228. Accepted stance ViewOffset participates in BodyInfo.
   The stage covers only fresh, upright, unparented construction and explicitly retires
   before unowned input, even a zero physical wish. `ObserveUnownedPlayerInput` is mandatory
   at that boundary; body equality cannot prove an unchanged view/request cache.
   `ValidatePhysicalPublication` runs after actual body/Unit publication and before the
   global AI grid prefix. Changed pose/body/model/scale/stance/faction remains a refusal,
   not a current-position cache or a new constructor profile.
7. Extend the physical census only with the exact current player/body/registry/grid lease.
   Keep the existing unrepresented-living-body refusal for failed or unowned profiles.
   Add a real player plus authored NPC control and source-backed moving/view/stance cases;
   the staged cache control's controlled Puppet fixture is not that gameplay control.

## Frozen external stage

Manifest and exact file hashes:
`D:/aaemu-runtime/research/managed-player-ai-stage/manifest.json`.
The adjacent `README.md` describes the source boundaries and proposed hooks. The three
staged source files are `ZoneManagedSceneActorIdentities.cs`,
`ZoneManagedPlayerActorScripts.cs` and `ZoneManagedPlayerAi.cs`; none is in repository
compile inputs. Isolated outputs are under `D:/aaemu-runtime/player-ai-stage-artifacts`.
The stage build reports zero warnings/errors, with no main/parity artifact writes.

`managed-controls.json` records **34 passing managed controls** using the unchanged
captured player387/model17 and actual database/script dependencies. They cover constructor
execution, installed parameters, fresh identities, constructor-zero insertion followed by
real initial view pose, skipped/rotating cache updates, disable versus membership, failed
registration rollback, changed-pose refusal, same-ID re-entry, stale disposal and terminal
construction history. All Unit/type-index/grid owners retire to0. The process measured
72 modules and zero forbidden CryEngine/X2/native-Lua modules. User-owned native PID4192
was untouched; every staged process/session exited and was collected.

The stage does not execute full player perception, moving-player state, complete CPlayer
gameplay or a production NPC/player crowd path. No implementation checkbox is closed by it.

## Evidence

`D:/aaemu-runtime/research/player-ai-census/selection-controls.json` records35 original
`392654D0` machine-code controls with zero differences: all eight kinds across three
environment profiles, false-selector behavior, and the client override branches.
The exact model slot1280 call is intercepted only to copy its arguments. This executes
selection/forwarding, not full native player allocation, scripts or perception.

The same directory retains source extracts, the offline generator and matching PE/table
proof. X2 PE SHA-256 is `50EDCD49D0537AA0F94DB55ECF22008D7FB9ED937EA02121919199B5BF8BB7ED`.

The frozen stage adds **62 offline original-instruction controls**, all with zero differences:

- `native-model-controls.json`:20 actual `392ABBE0`/`39AEF8D0` character-model lookup
  controls using17 original character descriptors, plus32 common-tail controls across
  all eight Unit kinds and ModelDesc flags0/2/8/10.
- `native-registration-controls.json`:10 base/player constructor, creation-gate/order
  and player-event controls. CPipeUser constructor services, allocation and vector/tree/grid
  storage callbacks are intercepted explicitly. These controls prove those bounded fields
  and calls, not full native actor construction or live BodyInfo.

These are additional to the unchanged35 selector controls. The generators, matching PE
hashes and source extracts remain beside the stage manifest. No original module or native
Lua DLL was loaded.

Current managed player entry remains valid for its documented
physical scope; this task is required before its presence can certify a complete NPC
crowd/navigation census.
