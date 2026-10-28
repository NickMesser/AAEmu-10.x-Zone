# Managed NPC CActor movement statistics

`ZoneManagedNpcActorMotion` owns the initial CActor/model fields and the dry, upright,
static-support frame path used by `ActorUnitModel39278AC0`. It borrows the actual actor,
construction, living body, character slots and complete water inventory. It never steps
physics, relocates a body or publishes a Unit/AI pose.

Bind after actual construction/physicalization. Call `CompleteNpcInitialization(actor)`
after the original NPC character constructor succeeds: `39346960` then sets model flag1
through `392A47B0`. Before that boundary the model's real constructor flag is0.

Call `Update(actor, body.CopyState(), frame)` once after the scene's physical step.
`CopyReplicationState(actor)` combines the resulting CActor metadata with a **fresh** body
pose/accepted stance. A later CheckPos relocation changes that pose without rerunning
the CActor velocity/timer producer. `ResetControllerAfterReplication(actor)` implements
`392A48D0`, clearing only the model's copied controller ID after the consumer commits.
Disposing this owner retires its own generation state, not its borrowed scene resources.

| Owned field | Original producer |
| --- | --- |
| Initial mode/air timer | `392E7D80`'s final writes are mode3 and air timer1.0, overriding its early zero stores. Ground timer starts0. |
| Actual actor velocity228 | `392E0C80` copies living status2+36. An exactly zero physical gravity vector clears actor228 even if the physical body moves. Wishes are separate. |
| Ground classification | `392E0C80` uses flying, the copied blocking-contact timer, the strict cache sentinel `>-1e10`, and an entity-origin/cache-height comparison `<.1` only when the previous air timer is exactly0. Physical `IsGrounded` alone does not produce the actor flags. |
| Blocked timer372 | Positive copied contact sets `.3`; otherwise subtract elapsed and clamp at0. The managed source is its existing committed motor `PriorBlocked` state; its collision response remains the documented replacement policy. |
| Mode/landing transition416 | `392E0B50` changes air/ground mode, resets prior nonzero mode timers and sets landing only for air-to-ground. |
| Ground timer148 | `392E0A70` adds this frame's elapsed after transition resets. Replica flag4 means this timer is positive. Landing transition independently contributes flag16. |
| Air timer/fall speed | `392E08F0`/`392D7A10` add elapsed and derive falling speed from actual velocity/gravity. The bounded profile refuses fast falls before the unported fall-state/effect branch. |
| Landing consumer | Model+816=`39270C60` visits actual child models then `3945B0D0`. Dedicated `39277D00` skips client-only `3945F1E0`, leaving fresh Queue+24 null (`394606E0`); that actual absent-driver branch has no landing mutation. Dynamic model children/animation drivers remain separate owners. |
| Request delta | `392EAEB0`/`39255BE0` construct a real empty movement request. Replica delta is its flag32/+44 value, initially zero; neither physical velocity nor desired Puppet steering substitutes for it. The full proxy movement-request producer remains unported. |
| Model replication gate/controller ID | `39289EB0` initializes model flags0, controller pointer144 null and controller ID152 to native invalid0. NPC initialization later sets flag1. |
| KeepSyncing | CActor virtual448=`392D5730`: `(ragdoll401 || timer2216>0) && !special32`. The admitted constructor profile has all three false/zero. This is a CActor getter, not a skill-controller getter. |
| GuardedAllowStrafing | The guard uses the NPC's **cached template model**, independently of World's current model. When that actor descriptor's DB `SlopeAlignment` bit is set, `393405B0` reads actual `SAIActorState.AllowStrafing`; otherwise the sample is null and the consumer preserves its old cache. |
| Timestamp | `39278AC0` calls CSystem virtual1192. The managed frame's independently sampled `UnpausedMilliseconds` supplies this value; World-aligned `ServerMilliseconds` is a separate clock. Missing provenance refuses sampling. |

The guarded AI byte's original chain is important: `3934B1F0` gets Entity+456's AI
object, CPuppet+32=`3100994C` returns its IAIActor subobject at full+232, and that
interface's +16=`31110B4C` returns state at full+240. State+964 is full+1204
`AllowStrafing`, matching `ZoneActorControlState.AllowStrafing`. CActor+964 is part of
a quaternion and is unrelated. The legacy DB column name does not rename this AI field.

`CopyPuppetAlertness` owns the independently proven constructor value0 at CPuppet+4160
(`31171F8C`), or null for actual non-Puppet absence. It does not copy
`ZoneNpcBehavior.Alertness`. Original `311680BC` transfers CAIProxy+96/CAIHandler+176
only after the full proxy update inside `31172DEC`; that later consumer is still unported.
Non-Puppet replica fallback metadata is also explicitly refused.

## Character and water proof

`ZoneCharacterInitialSwimThreshold` reads the admitted character's original CHR
CompiledBones0801 default B2W matrices. `392D8F00` selects semantic IDs
46,45,5,4,3,2 in that order. CryAnimation's actual semantic map names them
`Bone_Swim`, `camera`, `Bip01 Spine3`, `Bip01 Spine2`, `Bip01 Spine1`, `Bip01 Spine`.
The original lowercase-CRC/source-order map takes the last joint for each identity.
The absolute default-pose Z is in meters and receives entity scale once.

The initial, unanimated slot profile owns this default pose. It does not claim an evaluated
animation pose. A missing selected joint refuses the unowned animated-character AABB
fallback; skeleton animation/reset/equipment mutations require updating this profile's
producer before it can remain current.

The query point follows native float order: `entityZ + scaledThreshold`. The concrete
water owner's `MaximumPotentialWaterLevelAt(queryPoint)` is a certified upper bound for
any eligible area and nonnegative true entity-bounds diagonal. If
`queryZ - ceiling > threshold * 2`, the native swimming classifier must reject swimming.
This is a conservative dry-profile admission proof. The ceiling is never reported as a
sampled CActor water cache, and physical collider bounds never replace entity bounds.
Missing/retired/unsupported water inventory and an unproved wet location refuse sampling
before committing CActor state.

A read-only census of all32 zone129 NPC model paths found a selected semantic joint in
every original skeleton. Examples: elf male camera Z1.4633634m; elf female camera
Z1.41414m. Local evidence: `D:/aaemu-runtime/research/npc-motion-swim-threshold-census.json`
and `npc-motion-skeleton-semantics.json`. These observations are content coverage,
not an executed animation or full NPC gameplay-parity claim.

## Validation and remaining profile limits

`ManagedNpcActorMotionTests` uses real body creation/stepping and complete character/water
fixtures to check short fall/landing, one-frame transition flags, timers, zero-gravity actor
velocity, fresh relocation pose with preserved actor statistics, distinct template/wire
models, clocks, atomic refusals and generation/callback retirement. It also verifies native
semantic priority, duplicate CRC source order and raw authored pose units.

Swimming/current animated water state, jump requests/events, fast and repeated hard-fall
effects, ragdoll/ladder/glide, dynamic/moving supports, retained ground transforms, additional
model children and full Puppet proxy update/request/alertness copying remain separate work.
The raw living-query/native-controller residuals documented elsewhere still apply.
