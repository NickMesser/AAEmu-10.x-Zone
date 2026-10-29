# Managed NPC actor movement requests

`ZoneManagedNpcMovementRequests` owns the ordinary upright, dry NPC request path
from the actual proxy output to the scene's existing living body. It stores
controller requests, the walking latch and the copied CActor movement output. It
does not step physics, teleport, or write a Unit pose.

Bind after the exact body, CActor construction, AI pose and Motion leases exist.
The scene's real proxy phase supplies `ZoneManagedNpcProxyMovementOutput` after
the proxy activation gate and handler/signal work. Its fields describe the actual
`SAIActorState`, including desired speed and movement context; they are not an
arbitrary velocity interface. `PrepareFrame` runs after existing animated/stance
preparation and before the sole physical Step. The existing postphysical owner
then publishes Unit/model/spatial/AI state and samples actual CActor statistics.

## Original request chain

| Source | Owned behavior |
| --- | --- |
| `CAIProxy30518020` | Reads actual entity rotation and current normal speed, checks the concrete Unit/model movement eligibility, maps body states to actor stance slots, converts desired world direction to normalized actor-local delta, and preserves signed urgency/speed and request flags. |
| AIUnitSystem `200/39212FB0`, `224/392130B0` | The dedicated model has no client-input controller pointer144. The separate model flag1 gates AI requests; it is not a health, entity-active or AI-enabled substitute. |
| `CActor224/392D72B0` | Normal speed is constructor scalar1200=1 times the accepted stance's authored normal speed, with the actual walking multiplier when flag124 contains0x1000. |
| `CActor256/392D4FB0` | The moving proxy branch sets/clears walking from movement scalar<=.40002. The nonmoving branch preserves the old latch. Walking is independent of AllowStrafing. |
| `392EAA30/392EA280` | Caches the actual request, including its local delta and bit0x20. A fresh request replaces the previous delta rather than accumulating it across frames. |
| `392EAFC0` and the ordinary derived actor path | Use the real logical entity quaternion, actor-view quaternion and controller eye cache. Apply the native yaw dead zone, update actor rotation, then convert cached local movement into the physical request. The NPC Unit type1 branch has no player directional multiplier or unit-length clamp. |
| `392DA390` | Effective normal speed uses the actual stun/sleep/root state and complete current attribute10, with the original nonnegative multiplier. Flying/swimming modifiers remain separate. |
| `392DC900` | After animated restoration, positive authored inertia is suppressed by the real walking latch; zero and negative values survive unchanged. Acceleration remains its independently installed value. |
| `39302A20/392F0E60` | The actual derived output becomes CActor876, the independent view quaternion and controller look target376. The body receives a normal-mode0 wish, never its current physical falling Z as requested Z. |

Physical orientation uses the existing ordered `Body.RequestOrientation` path.
The composite also owns the logical entity quaternion while the command is
pending; the applied body snapshot changes only during the existing Step. This
preserves velocity, support and queued stance/correction order.

The Motion replication sample now reads the actual cached request delta when
bit0x20 is present and derives the walking flag from its owned latch. Actual
velocity still comes from postphysical CActor statistics.

## Controller slope and cached physics

`Motion.CopyMovementSpeedState` returns the accepted stance and source-owned
scalar2376. Original `392DCEF0` smooths the copied living ground normal before
the current frame's ground/air statistics and applies `39252A80` to four scalar
spring fields. The constructor normal is UnitZ and the spring period is .2.
No extra ground ray or physics step supplies this value.

`Motion.CopyAiPhysicsState` preserves two additional original caches. The
constructor zeroes both. Active `392E0C80` copies living status2+24 to AIObject16
and status2+72 to AIObject28. The first is velocity including retained ground;
the admitted static-support profile has no retained contribution. The second is
the body's gravity vector at496, not a normalized AI direction or actor velocity.
Matching dedicated `35029A30` GetStatus2 and complete original `392E0C80` controls
confirm these offsets with distinct vectors.

Original `39213180` directly compares AIObject28's squared magnitude in its
"stuck" calculation. The managed query must retain that exact cache input,
despite the surprising gravity semantics. Inactive dynamics skip the cache
producer, and later CheckPos/teleport relocation preserves the last cache until
another actual physical-stat update. Neither path substitutes current body
velocity or desired movement.

## Supported settings and limits

The matching registration instructions establish shipped defaults
`ca_GameControlledStrafing=1`, `ac_enableProceduralLeaning=.4`, and
`pl_curvingSlowdownSpeedScale=1`. X2 Console virtual80 is RegisterFloatRef; its
hidden XMM3 argument supplies the two float defaults. This is not a GetFloat
call. The defaults select identity slowdown. Custom settings that enable a
nonidentity curving slowdown refuse before physical movement until its scalar
consumer is ported.

The ordinary CActor turn gate reads the current copied Unit status fields through
`392D73D0/39258910`: stun32080, sleep32081, psychokinesis32091 and
impossible-rotate32092 inhibit generated body yaw. Root32082 is deliberately
absent from that gate. The separate `392DA390` speed consumer returns zero for
stun/sleep/root, then otherwise uses the complete current attribute10 result.
Buff list presence is not a movement refusal. These flags retain the actual
BuffMan byte-counter, restore/mutation and copied-status timing semantics.

Original restored hidden buffs6613/3590 set none of those five flags;3590 does
have the actual attribute10 modifier+300. Good buff1965 similarly sets none of
them but contributes-1000, which stops the ordinary physical wish while still
permitting body yaw. The managed frame reads current status after cached proxy
requests, so a newly applied impossible-rotate effect can suppress their turn
and removal can enable it again. Unknown/unresolved stat sources retain the
existing named refusal instead of silently treating the modifier as zero.

This does not claim every effect of those descriptors.6613's separate
`no_collide_rigid` flag is outside this ordinary static-support request producer;
its complete physical flag consumer remains unported. Psychokinesis model-latch
and body effects have separate owners and limits. No general physical-buff,
rigid-body or gliding capability follows from admitting these status inputs.

This profile still refuses pitched body
facing, the clamped head-look cone, jumping, swimming, parented/moving supports,
animated root motion, and distinct Monster/Quadruped turning. The separate
handler-to-Puppet alertness copy currently owns the zero branch; nonzero
AI-system alertness-count effects remain explicit dependencies. Native request
lean is constructor0 in this profile; an upstream nonzero lean producer is not
claimed. The full `392DC900` dynamics update contains other fields that this
inertia-only producer does not replace.

Every lease checks exact actor/body/construction/pose generations. Retirement
disposes request state and pending body commands. Expected unsupported profiles
flow through the Runtime's existing fault boundary before further physical
preparation, so a retired request cannot affect a replacement numeric Unit ID.

## Validation

Offline Unicorn controls execute complete original PE routines without loading
CryEngine modules:72 multiframe controller-slope rows;288 proxy request rows;
6 complete ordinary derived movement pipelines;12 complete actor-dynamics
inertia cases, including signed zero and negative values; and3 distinct-vector
living-status/CActor cache cases. A further34 complete original actor pipelines
cover all32 combinations of the five copied status flags plus actual attribute10
+300/-1000. These controls execute the original turn predicate and effective-speed
reader as well as the rotation/movement pipeline; no original module is loaded.
Permanent fixtures retain the copied outputs.

The composed movement tests exercise actual admission, activation, request
preparation, normal-mode living integration, pose/spatial/AI publication and
decoded movement packets. They cover the native grounded inertia delay,
falling velocity versus wish Z, native rotation-before-velocity order, walking
latch restoration, constructor/cache timing, inactive/relocation cache
preservation, exact generation retirement and named custom-setting refusal.
Original6613/3590 and1965 are restored through complete NPC wire admission before
actor construction. Frame controls preserve their actual+300/-1000 movement
modifiers, independent yaw behavior and ordinary body/model/Unit publication;
live stun/sleep/root/impossible-rotate mutations exercise the separate consumers.

This establishes the bounded actor-to-body path. Original goal execution,
pathfinding/steering and whole-resource far-return validation belong to their
concrete owners and controls.
