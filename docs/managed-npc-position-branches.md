# NPC position branches and scheduled signal dispatch

Managed actor services implement native branch 45 `IF_POSITION_DIST_LESS` and
branch 46 `IF_POSITION_DIST_GREATER`. The goal executor continues to mask and apply
the `NOT` bit `0x100` exactly once; actor services return the uninverted predicate.

## Actual operands and comparisons

`3115AAA4` reads the goal position at native GoalParameters `+24` and distance
threshold at `+56`. Its actor position helper `3103CA08` first asks virtual `+448`
for the actor's entity. If present, it copies Entity virtual `+312` world origin;
otherwise it reads AIObject virtual `+120` position. The actual CPuppet primary
table `311EAC50` maps `+448` to entity resolver `3103CC78`, which uses its real
entity ID and the entity-system owner.

The admitted managed NPC owns its entity, so services read the existing scene
entity/Unit position callback and revalidate the exact actor generation after
that callback. The AI proxy view position, model bounding center, reference point
and requested movement are separate values. In particular a stance's view-height
offset must not create a false distance from an entity standing on its idle point.
The source entity-absent fallback is exercised in the oracle; a retired managed
actor does not use it to revive a stale entity generation.

Distance is calculated in float operation order:

```text
x = goal.X - entity.X
y = goal.Y - entity.Y
z = goal.Z - entity.Z
distance = sqrt((y*y + x*x) + z*z)
branch45 = distance <= threshold
branch46 = distance > threshold
```

This is full 3D distance, including Z. Neither a squared comparison nor an XY-only
test preserves the source rules for negative thresholds, float overflow and
unordered values. Both predicates return false for NaN, despite misleading
negated comparisons in the decompiled C. Infinity and signed-zero comparisons
follow the original ordered PE comparisons. No tolerance or geometric clearance
is added to these branch predicates.

## Proxy phase follows goal execution

Signal goals submit to `ZoneNpcAiSignals`; submission does not synchronously call
the behavior handler. Original CPuppet `31172DEC` executes `311614B4` goals/output
before its later proxy virtual `+64` call. CAIProxy table `30725888` maps that call
to `30518020`. With its proxy-update/handler guards admitted, this function first
calls virtual `+240`, which maps to signal dispatcher `30518D30`, then generates
the proxy movement request.

The managed actor's scheduled `Update` now runs its existing goal/output phase,
rechecks the exact live generation and signal handler, then invokes the existing
`Npc.AiSignals.Update`. An unscheduled or disabled actor does not receive a blanket
scene-frame drain. A goal-phase failure or callback retirement cannot dispatch
through an old actor afterward. Existing runtime `OwnedActor.Update` owns faulty
callback reporting and retirement; the new call creates no second failure owner.

Existing queue behavior remains unchanged: pending constructors run first,
ordinary signals drain in reverse order, kind 3 is deferred as kind 30, and forced
signals use their existing gated path. Auxiliary updates follow that drain.
`30518920` decrements auxiliary delay using ScriptSystem virtual `+88` system-frame
seconds. The current managed owner supplies its configured frame-period sample;
an accumulated AI scheduling interval must not replace it. Complete variable-frame,
paused-time and native GameObject proxy-update scheduling remain separate fidelity
work beyond the admitted fixed-period actor profile.

## Authored idle continuation

NPC 8173's relevant behavior is `AIBehaviour.idle` with
`entity.AI.param.idle_ai="hold_position"`. Its source
`game/scripts/ai/goalpipes/pipe_manager_x2.lua` builds this pipe:

1. `NOT + IF_CAN_MOVE` skips returning when movement is disallowed.
2. Branch 45 skips returning when entity-to-idle distance is at most 3m.
3. Otherwise a queued `ReturnToIdlePos` signal requests the return state.
4. A two-second timeout is followed by queued `UpdateIdleEnd`.

The existing timeout remains active at equality and completes only after elapsed
time exceeds its duration. The scheduled proxy drain delivers `UpdateIdleEnd` to
`AIBehaviour.idle.UpdateIdleEnd`, which selects `idle_hold_position`. That pipe
runs body position, urgency, look direction and the actual idle skill goal before
queuing `UpdateIdle`, which constructs the hold pipe again. A skill request is
not proof of a successful skill acknowledgment or completed gameplay effect.

This differs from `AIBehaviour.hold_position`, whose corresponding names are
`holdPosition_HoldPosition`, `UpdateHoldPositionEnd` and `holdPosition_Base`.

On the farther-than-3m branch, `almighty_npc.idle` maps `ReturnToIdlePos` to
`return_state`. Its constructor first needs the actual auto-disable getter/setter,
combat/aggro state and optional restoration services; its authored pipe then uses
`locate(refpoint)`, `run` and `approach`. Formation branches similarly require their
real formation and movement owners. These paths are not completed by adding the
distance predicates or dispatching their signals.

## Validation

`npc-position-distance-native-controls.json` is embedded in the tests. Its 52
controls execute original `3115AAA4`, `3103CA08`, entity resolution `3103CC78`, the
entity-ID getter and AI-position fallback under Unicorn without loading engine
DLLs. Entity-system lookup and Entity world-origin copy are explicit ABI callbacks;
the CRT `sqrtf` boundary executes scalar `SQRTSS`. The fixture records the original
image SHA256, raw input float bits and predicate results, including decorated
`NOT` values. Tests cover all 52 results, a distinct entity/view origin, vertical
distance, real executor inversion and position-callback retirement.

Four scheduled-proxy tests verify actual signal-goal queueing, delivery after the
same actor's finished goal phase, once-only scheduled delivery, disabled/absent
handlers, unsupported goals and callback retirement. The predicate/proxy tests and
the six existing actor-scene tests pass in isolation. Original instruction script
and detailed output are retained at
`D:/aaemu-runtime/research/npc-position-distance-native.py` and
`npc-position-distance-native-controls.json`.

The independent original-resource integration output
`D:/aaemu-runtime/research/npc-hold-position-production-order-control.json` records
all five original constructor/idle/position-check profiles without a fatal
retirement, then 150 additional frames for NPC 8173. Its actual pipe changes from
`hold_position` to `idle_hold_position` at frame 95, emits a real World StartSkill
request for Unit 968/skill 40917 at frame 99, and returns to `hold_position` at
frame 161. No skill acknowledgment is injected, so this demonstrates scheduled
loop/request progress rather than a successful skill effect. The existing
protected `Server.OnUpdate` Lua comparison error remains reported with
`RetireActor=false`. All 17 owner counts reach zero after retirement; no engine
modules are loaded.

Branch 47 status ownership is documented separately in
`managed-npc-movement-eligibility.md`. Remaining approach/path execution, full
native proxy scheduling, perception, formations and skill effects retain their
own explicit admission boundaries.
