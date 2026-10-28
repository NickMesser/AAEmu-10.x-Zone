# Managed NPC position correction and replication

This is a bounded replacement of original3933D0E0,3933B510 and393405B0. Production composes
concrete group, authored-sector region, body, model/Unit/spatial, AI pose, dedicated posture,
CActor motion and movement-buffer owners. It does not enable the full NPC gameplay gate.

## Position checks

The original Position step still runs when `(frameByte ^ UpdatePhase)&127 ==0`, including dead
NPC owners. `HasPartialUpdateStep` admits that step only for an actual bound generation. Other
unfinished manager leaves retain their independent gates. The Lua setter stores the exact signed
context; an unknown value is diagnosed when consumed. Constructor39345000 zeroes the eight bytes
at+152, including the context word at+156. The matching PE instruction39345158 writes that QWORD,
so a fresh generation owns NONE0 before any Lua setter. Its first same-region check skips anchors;
the independent outside-region branch can still recover to the actual current `AI.spawnPos`.

Positions come from the applied model transform, not the AI eye position. Both region queries must
return the current canonical Zone object. The destination uses the current entity's actual AI table:

| Context | Anchor | Strict3D distance | Grouped nonleader | Posture after correction |
|---|---|---|---|---|
| NONE0 | none inside region | none | no gate | none |
| ROAMING1 | idlePos |15m | skip | none |
| HOLD2 | idlePos |7m | skip | default(false) |
| TALK3 | talkPos |7m | skip | default(false) |
| FOLLOW_PATH4/FOLLOW_UNIT5 | original empty branch | none | no gate | none |
| ALERT6 | idlePos |50m when grouped nonleader; otherwise15m | no skip | none |
| FLYTRAP7 | idlePos |3m | no gate | none |

Distance uses scalar `(dx*dx+dy*dy)+dz*dz`, strictly greater than the squared threshold. Missing
or wrong-type inside anchors do nothing. Outside the current region, only actual Puppet alertness2
suppresses recovery to current `AI.spawnPos`; NONE does not suppress recovery. An absent spawn
anchor refuses explicitly instead of copying native uninitialized stack or a wire spawn snapshot.
The currently admitted fresh Puppet profile owns initial alertness0; later full proxy-update copies
remain a separate producer and are not inferred from a behavior's desired alertness.

Correction preflights actual owners, coherent published/body transforms and existing spatial
capacity. It runs physical action4(reset1), action10 and translation, publishes the actual resulting
model/Unit/spatial/AI pose, resets the real per-NPC replication cache, then restores posture for
HOLD/TALK. It never Steps physics. A committed physical operation is not rolled back on a later
publication failure; the failing generation retires through the existing runtime boundary.

## Replication

`ZoneNpcMovementReplicationState` owns the native cache and counters. Constructor counters use
the high32 bits of `100*random`; cache starts with zero position/velocity, identity rotation,
stance-1, invalid controller0 and cached allow-strafingfalse. Reset changes only lastSync and
the two counters. The live settings are:

| CVar | Native default | Meaning |
|---|---:|---|
| npc_movement_skip |1 | add min80ms of truncated f32(characterCount*.2) |
| npc_move_sync_period |100 | unsigned millisecond period |
| npc_move_skip_disabledAI |2000 | inactive NPC call counter |
| npc_move_skip_standing |400 | active unchanged observation counter |
| npc_enable_fastest_sync |0 | reset all three cadence fields on each call |
| npc_sync_together |1 | separate global manager movement gate |

The load source is CharacterManager tree size: manager slot0, virtual32/39DAF020, field24.
It is not NPC count or frame duration. Inactive and standing comparisons use the old signed
counter before increment. Period subtraction/addition retains unsigned wrap. Position and velocity
use component tolerance.01; exact stopping forces a change. Quaternion comparison accepts q/-q
within.00001. Controller ID/phase changes add10 to the change count.

The guarded byte at SAIActorState+964 is **AllowStrafing**, reached through3934B1F0 and the actual
IAIActor interface. It is read only when the cached NPC template's model is Actor and its
ActorModel.DBSlopeAlignment flag is set. It is not a CActor field or measured ground tilt.
Wire flag8 follows that cached value; flag4 is added for change count>=10. KeepSyncing comes
from actual CActor virtual448/392D5730 (ragdoll/special-state condition), not the skill controller.

An inactive send zeros wire velocity/delta while retaining actual actor velocity in the cache.
After committing a send, actual model controllerId152 resets to invalid0. A full1000-row buffer
drops the following row **after** those effects. Generation retirement removes its unsent rows.
The existing tick's movement-buffer phase drains the NPC buffer into ZWUnitMovements.

Three frame clock domains remain explicit: World-aligned ServerMilliseconds for the existing
player stack; local CTimer0 for NPC cadence; CTimer1/CSystem6744 for the model's wire timestamp.
The managed dedicated host has no pause/time-scale producer, so its two local domains share one
monotonic sample before World's TimeKeeper offset. Tests can and do distinguish their values.

## Encoding and evidence

Both movement paths use the original actor encoders. Quaternion rotation is an axis-angle byte
vector (393753C0), not Euler angles. Velocity divides by60 before clamp/floor(value*32767+.5),
with negative limit-32767; delta uses clamp/floor(value*127+.5). Position encodes actual fixed XY
and native clamped22-bit height. Optional fall velocity has its original unsigned quantizer.
Unowned ground/hanging/push/max-pushed fields refuse before encoding.

Committed fixtures contain45 original sampler/cache observations and68 complete actor writer
observations, executed from the original PE instructions offline. Sampler ABI callbacks provide
explicit cvar, clock, model/pose and AI-state inputs; they do not prove those producers. The full
writer captures typed serializer output, with scalar math imports controlled and recorded.
Neither oracle loads an engine DLL. Separate region, physical relocation, posture and motion
tests verify their concrete owners. This does not establish perception, wet/dynamic movement,
navigation locomotion, graph animation, controllers, combat or general feature parity.
