# Unit movement status and buff effects

`ZoneUnit.MovementStatus.CopySnapshot()` supplies the actual Unit flags read by `AIUnitSystem::CanMove` (`39212E70`): stun, root, then sleep. It also supplies psychokinesis and impossible-rotate for `CanTurn` (`39212F30`→`39258910`), which checks stun, sleep, then those two flags. These flags are independent of entity activity, velocity, health, and the living body's grounded state. The scene separately owns model flag1 and the CActor psychokinesis latch; their combined predicate belongs to the managed NPC movement eligibility service.

The implementation covers these five status counters and their lifecycle. It does not claim all14 Unit statuses, their model/animation notifications, general buff gameplay execution, or full NPC gameplay parity.

## Descriptor ownership

The original `LoadBuffDescs` (`39AD3980`) query was recovered from the matching10.x PE. These are its zero-based query columns and the fields subsequently read by the original consumers:

| SQL column | Query index | Native descriptor field | Managed template member |
|---|---:|---|---|
|`stun`|204|byte1134, bit2|`Stun`|
|`sleep`|195|byte1133, bit8|`Sleep`|
|`root`|184|byte1132, bit0x10|`Root`|
|`psychokinesis`|132|byte1127, bit2|`Psychokinesis`|
|`psychokinesis_speed`|133|float328|`PsychokinesisSpeed`|
|`impossible_rotate`|93|byte1124, bit8|`ImpossibleRotate`|

The loader accepts boolean text beginning with `T`, `t`, or `1`; its scalar passes through the native float boundary. The configured original database contains30654 buff definitions, including1485 stun,108 sleep,833 root, and4 psychokinesis descriptors. These counts describe that input database, not hard-coded runtime admission rules.

## Counters and copied flags

The actual `ZoneBuffMan` constructor (`39450940`) installs vtable `3A00AC28`. Slot104 points to cache dispatcher `39D2CF70`; slot72 points to `3944F4F0`, whose iterator `39450A70` traverses actual Good, Bad, then Hidden buff instances. `39D2C030` changes byte counters136/137/138 for stun/sleep/root and147/148 for psychokinesis/impossible-rotate once per contributing instance. Stack, charge, elapsed time, and duration do not multiply or suppress that contribution.

Dispatcher mode1 increments for a newly allocated instance. A refresh is changed to mode2 using the original creation backup; the visitor returns before these counters. Mode3 decrements for removal. An increment at255 or decrement at0 logs an invalid-cache diagnostic and retains the old value. The managed helper preserves that byte result; it does not reproduce the native logging transport. Normal admitted lists contain at most80 instances across their three fixed capacities.

`3925A270` reads the BuffMan through ServerUnit slot128 (`39360AE0`), copies each nonzero counter into Unit32080 onward in ascending status order, and invalidates attribute290. The managed owner retains separate counters and booleans for the five covered statuses. The later two copied bytes are32091/32092, ordinals11/12. It does not synthesize the unported model callbacks for all14 statuses.

`PassiveManager` at Unit32144 is a separate owner. Initial passives, learned/reset passives, and active-buff passive grants affect the existing progression/stat pipeline; they do not enter this BuffMan iterator or contribute these counters. A passive referring to a stun descriptor therefore does not by itself make the Unit stunned.

## Mutation and restoration order

| Producer | Covered behavior |
|---|---|
|`3944F9B0` initial restoration|Copies the Good/Bad/Hidden inventory, resets and rebuilds the cache through slot104.|
|`39450740` Create|Commits the actual new/refreshed instance, invokes `3944FEB0` effects, then updates the cache and copies Unit status flags. A refresh still invokes effects, including at category capacity.|
|`3944F730` Change|Updates stack/charge/elapsed, then recopies retained counters. It does not reapply model effects or infer removal when stack becomes0.|
|`39450420` Destroy|Runs removal effects while the old instance/statuses remain visible, removes its cache contribution and array slot, then copies statuses.|
|Timing/expiry|Advances the existing timing owner; World removal remains authoritative. Passing a duration boundary does not clear these flags.|

The existing stat calculator preflights each mutation. Its ordinary cache invalidation remains in place; current HP/MP are not rewritten by these status hooks. A full category refuses a new instance, but can still refresh an existing one without an extra contribution. Missing native descriptors do not contribute in the isolated cache visitor; the wider stat/admission pipeline can still refuse unresolved content.

Initial dead state is distinct from a death notification. `3935D260` calls `3925B2C0` when initial precise health is0, then still restores effects and copies statuses. The dead setter writes only dead/model/CActor fields. Thus a zero-health admission can retain initial stun/sleep/root flags.

Explicit death `3935D490` clears Unit32080..32093 after the combat transition callback. Resurrection `3935D970` clears them before the refill helper `3925E8E0`. Neither resets the BuffMan counters or removes those instances. A later successful buff Create/Change/Destroy therefore republishes the retained counters, even while the Unit is dead. Passive-only and timing-only changes do not perform that publication.

## Model-effect boundary and deferred admission

`Unit.BuffEffect` carries an immutable `ZoneUnitBuffEffect`: Apply/Remove, the actual resulting or removed instance, its resolved descriptor, refresh/initial markers, and a monotonic sequence for psychokinesis mutations. Create/refresh publishes the event after committing the actual list but before status-cache publication. Remove publishes it while the old list and copied flags remain visible. A mere Updated packet publishes no model-effect event.

Psychokinesis is an ordered model effect, not an any-current-buff aggregate. `3944FEB0` resets the actual model controller, writes CActor548=true and CActor552 from descriptor328, and clears NPC model flag1. `39450420` resets the controller, writes false/0, and sets flag1. Removing either of two overlapping psychokinesis buffs clears the latch, even while the other instance remains. The scene's actual Motion owner consumes these events and owns its reset/latch/model-flag behavior and unsupported reset profiles.

Original chronology is `NpcManager39348440` → `39360590` → `3935D260` (model initialization, `39450870` initial effects, copied statuses), followed by NPC construction and `39346960`, which sets model flag1. `CopyInitialBuffEffects()` therefore retains the original snapshot's Good/Bad/Hidden effects for one model initialization replay before that final flag. It does not substitute the current inventory.

Managed NPC admission can be deferred while World mutations arrive. `CopyBuffModelEffectJournal()` retains the actual subsequent psychokinesis Apply/Remove/refresh sequence, separately from the initial inventory, for replay after model initialization. It never collapses overlapping events into presence. Live notifications and copied history share sequence numbers so the scene can deduplicate its subscription/replay boundary. Only psychokinesis rows are journaled. A1024-event replacement resource bound makes late replay explicitly unavailable on overflow; it does not silently drop live notifications or claim a native history limit.

If an effect callback replaces the Unit allocation, remaining work never writes into the new allocation. If a callback mutates the same Unit's buff collection reentrantly or fails before the original cache pass completes, movement status becomes explicitly unadmitted. `CopySnapshot()` then throws a named refusal rather than exposing a potentially false-clear flag. This is conservative replacement ownership policy, not a claim that arbitrary native reentrancy has been reproduced.

A genuinely empty actual inventory is source-proven clear without a descriptor catalog. Nonempty unconfigured restoration refuses both status use and initial-effect replay. Runtime retirement checks belong to the scene's exact actor/Unit generation; the status owner does not resolve a new Unit from an old numeric ID.

## Validation and limits

The external original-instruction control executes the matching PE's full `39D2C030` visitor under Unicorn with verified descriptor/tag lookups and unrelated empty inventory, intercepting only defined byte-limit imports and diagnostics. It also executes `3925A270`'s copy loop with explicit manager/status/attribute callbacks. There are105 controls with0 result disagreements:96 flag/counter/mode cases,1 invalid-descriptor case, and8 copied-status ordering cases. No native module is loaded. The portable counter matrix is retained in `tests/AAEmu.Zone.Tests/Fixtures/npc-buff-status-native-controls.json`.

The five-counter extension in `npc-buff-turn-status-native-controls.json` executes the same original leaves for417 controls:384 flag/counter/mode combinations,32 copied-status ordering cases and one invalid-descriptor case. All results match. The restored recorded buffs1965/6613/3590 are false for all five descriptor flags; they do not suppress turning merely by existing. Permanent controls also cover their actual Good/Hidden wire restoration, harmless timing copies, real impossible-rotate packet transitions, and retained counter publication after death/resurrection. An incomplete effect still refuses status access rather than substituting clear flags.

Focused tests exercise actual packet handlers for restoration, refresh, independent instances, zero-stack update, removal, full capacity, passives/grants/reset, death/resurrection ordering, initial zero health, duration expiry, effect phases, deferred journals, overflow, callback failure/reentrancy, and same-ID allocation replacement. Root integration separately validates the complete IF47 predicate and its goal-executor inversion. Other status notifications, active-buff model effects, animation, movement controllers and general buff gameplay remain individually owned or explicitly unsupported.
