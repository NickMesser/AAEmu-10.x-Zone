# Managed NPC model effects

`ZoneManagedNpcActorMotion` owns the exact generation's model flag word and the independent
CActor psychokinesis latch. `CopyMovementEligibilityState` returns those actual fields for
`IF_CAN_MOVE47`; it does not derive permission from entity activity, AI enablement, health,
physical velocity or current buff presence. Stun, sleep and root belong to the separate Unit
status owner. The branch executor owns NOT inversion.

## Native state and effect order

CActor constructor392E7D80 zeroes statistics+412/+416, which are CActor548/552. The model's
getter184/39266C00 reads548; AIUnitSystem39213050 names that getter
`IsPsychokinesisTarget`. This field is distinct from framehold at539.

The matching ActorUnitModel vtable39FC4168 maps176 to39266BE0,184 to39266C00 and168 to39280090.
Setter176 writes the latch and its float speed. The original generated buff loader's
`psychokinesis_speed` field is descriptor328; apply instruction39450052 loads that scalar into
XMM2 before invoking176. Removal39450521 explicitly zeroes XMM2. The decompiler omitted those
float arguments from its callers.

| Boundary | Cached movement | CActor548 | CActor552 | NPC model flag1 |
|---|---|---|---|---|
| Constructor | separate constructor state | false |0 |0 |
| Initial restored psychokinesis buff | reset | true | authored speed | clear |
| NPC39346960 completion | no reset | preserved | preserved | set |
| Create or refresh3944FEB0 | reset | true | authored speed | clear |
| Update3944F730 | no effect call | preserved | preserved | preserved |
| Remove39450420 | reset | false |0 | set |

The flag mutation is conditional on actual Unit kind1. Set/clear64/72 invoke model1480,
which is ActorUnitModel nullsub392631F0. They do not perform entity activation or an AI event.

These are ordered writes, not reference counting. Removing either of two psychokinesis buffs
clears the latch and sets flag1 even while the other instance remains. A subsequent refresh
sets the latch again. Death, expiry, stack values and an aggregate `any(buff)` check do not
invent additional model effect calls.

## Deferred admission and retirement

The original outer sequence39348440 calls39360590/3935D260, including39450870's initial restored
Good/Bad/Hidden effects, before constructing39345000 and running39346960. Its final model flag1
write therefore follows restored effects. The managed lease replays that immutable initial
inventory before `CompleteNpcInitialization`.

Managed admission can be deferred while World packets continue. The actual Unit owner retains
ordered post-enter psychokinesis apply/remove records with sequence numbers. After the final
initialization flag1 write, the lease replays that journal and then consumes live events. This
preserves Create/Remove/Create before admission and prevents a late initialization write from
incorrectly enabling model flag1. Snapshot input, mutable current buffs and mutation history
remain distinct. Reentrant event delivery is buffered and deduplicated by its actual sequence.

The journal and pending callback work have an explicit replacement limit1024. An unavailable,
overflowed or incomplete history refuses model use; no events are collapsed into current
presence. A live subscribed owner still receives events after the retained journal fills.
Retirement unsubscribes the exact Unit allocation. A replacement with the same numeric identity
gets a new latch, counters and subscription.

## Movement reset profile

Model168/39280090 submits actor request-clear mask0x1B1F8000 through CActor648 and resets the
NetActorMovementController through its8/3924A210/3924A080 method. The actual derived actor648
methods forward to ActorMovementController392EAA30/392EA280. This is cached request/reset work;
the function does not issue a living velocity action, teleport or physics Step.

The supported profile has constructor-empty actor and network request caches. Actual model
controller IDs, nonempty or queued living wishes, current copied actor movement output and
current copied controller look target require their unported reset consumers. The check reads
the current pose-owner fields; its previous published sample can be stale. This conservative
profile gate is replacement admission policy, not an additional native CanMove condition.
An actual pose-cache owner and finite authored speed are required for an effect.

Unsupported effects commit the real World buff state, poison only the affected model generation
and queue one copied fault. Its statistics and eligibility queries refuse until the scene consumes
`DrainFaults` at the next safe boundary. Runtime retirement precedes subsequent body preparation;
healthy generations continue their normal phases.

Physical velocity and pose remain independent, including real falling velocity with empty
requests. The ported dry statistics consumers392E0C80/392E0B50/392E0A70 do not read548/552.
The separate AI movement-request generator39219740 does: request byte64&1 selects movement
type4 and, when548 is set, takes speed552/1000. That producer and future psychokinesis locomotion
remain unported. Owning the latch and CanMove predicate does not admit that movement path.

## Controls

Focused integration controls use actual registry Create/Update/Remove packets, two overlapping
descriptors, refresh, current pose-cache mutations, actual physical falling, safe-boundary retirement,
healthy actor continuation and same-ID reentry. The motion-only fixture explicitly uses a clean
Unit profile instead of borrowing unresolved buffs from a captured player.

The complete original CanMove leaf has67 offline PE controls with declared lookup/model callbacks;
those controls validate the predicate, not its producers. The model-effect audit retains exact
original function extracts, vtable targets and caller instructions outside the repository at
`D:/aaemu-runtime/research/if47-model-effects/npc-model-effects-evidence.json`. No original engine
module is loaded by that source audit.
