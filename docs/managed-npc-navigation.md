# Managed NPC navigation and physical requests

`ZoneManagedNpcNavigation` binds approach/path/trace operations to the exact admitted NPC,
its installed movement parameters, the authored tiled graph, and the existing living body.
The actor retains one predictive follower across goal resets. The scene still performs one
physical step; a goal changes its shared AI movement fields and never writes a Unit pose.

The original NPC template writes numeric `usePredictiveFollowing=1`. The typed ScriptAny
boolean conversion accepts that value, and `ai_PredictivePathFollowing` defaults to1.
Consequently the ordinary `310EC518` trace is not a substitute for this NPC's predictive
`310EC118` branch. The source-backed [approach/ordinary leaves](managed-npc-approach.md)
remain available for actors whose actual configuration selects them.

## Ownership and order

Registration copies movement tables after the original entity `Server.OnInit`, before the
authored groups and character constructor. Auto-disable uses its independent native activation
word and game-object policy; an admitted multiplayer-server profile does not depend on a
fabricated camera. See [auto-disable](managed-npc-auto-disable.md) and
[return-state bindings](managed-npc-return-state.md).

The scheduled actor samples its actual proxy pose, copies body turn, clears the source movement
direction, runs the goal/output/continuation phase, and builds Puppet look and movement output.
The single proxy-activation gate then encloses the actual signal handler phase and CAIProxy
request. Signal handlers can change shared controls, so the proxy copies their live values
without running the look or delayed-stance phase twice.

`ZoneManagedNpcMovementRequests` accumulates the source controller request. The movement-frame
decorator prepares the existing body stance, consumes that controller request, and then delegates
the scene's existing physical step. Its logical entity quaternion changes at the source actor
boundary; physical status changes when the queued absolute orientation is applied. Post-step
publication reconciles body, model, Unit, spatial membership, AI pose and outgoing metadata.

The native follower pointer starts null. `31158AC4` allocates it when predictive following is
enabled, initializes path radius/look-ahead, and refreshes its2D flag. The outer trace calls this
getter before testing the global predictive CVar. Puppet output inspects the existing pointer
without allocating it. Resetting a goal preserves the follower; retiring the actor disposes it.

All recovered writes to Puppet update priority4728 store3: construction, reset and the manager's
full-update branch. At priorities2/3 the original predictive trace uses a zero prediction horizon
and clears its output samples. It still follows the path. Priorities0/1 additionally produce ten
forward samples; the native-control corpus covers both cases.

## Route publication and query limits

The path request clears both paths and enters decision0. A found route goes through the original
water/end-distance/ordinary-point and dynamic-obstacle decision stages before publishing decision3
and copying CurrentPath to OriginalPath. A certified no-route result publishes decision4. Missing
coverage, unsupported graph links or query capabilities are refusals, not evidence of no path.
The [path-decision owner](managed-npc-path-decision.md) describes its native controls and census.

The dynamic query's horizon is `abs(Combat.Run.Maximum * pathRegenIntervalDuringTrace)`, using the
installed table's exact component. It does not use current stance speed. Copied registered actor
and physical inventories supply candidate identities; known living bodies are class8 and cannot
enter mask`0x50006`. Unknown physical classes cannot be declared absent. Registered avoidance
spheres retain their own owner and are not silently discarded.

The [actor-grid owner](managed-actor-grids.md) retains independent insertion-time and rotating
cached positions. Current AI position is not a replacement for that cache. Initial Puppet
registration inserts constructor-zero position before entity transform publication; later enable
and the accepted AI-system prepass own reinsertion and cache refresh. Managed actor allocation
keys supply stable ordering independently of entity IDs and proxy keys; native heap address
ordering is an explicit comparison input, not reproduced allocator behavior.

The managed player factory does not yet own the original player AI registration contract.
An empty type index therefore does not prove an empty player grid. Crowd admission additionally
checks the backend's actual physical actor inventory and refuses an actor without a current
AI/grid classification, including an admitted player. It does not infer membership or cached
position from current body distance. A new unclassified body also invalidates a cached moving
route before the next physical request. This is a conservative replacement admission boundary;
it does not assert that native dedicated players always register, or always skip, type100.

Graph search currently uses the existing bounded A* and sampled-ground query policy. Production
explicitly configures segment/work/support limits; these are replacement limits, not recovered
native search or walkability constants. Height ownership is checked against actual installed
cells, independently of the renderer-height getter's zero fallback. Brush coverage alone cannot
certify authored vegetation, entity or road collision. The current adapter conservatively refuses
an affected cell until those inventories have physical classification. This remains a real blocker
for far-return movement in the original populated zone, even when its brush proxies all load.

Completed routes retain their graph and [physical publication](managed-collision-publications.md)
tokens: terrain, character geometry, picking geometry and water. Retirement is checked before
physical requests, independently of AI cadence. Equal-data terrain reload or add/remove of a
collision instance cannot resurrect an old admission. The runtime retires the stale actor before
the next physical step, keeping its Unit pose fixed. Per-cell invalidation/replanning remains
future work; this bounded profile refuses a retired publication.

The detailed next implementation is [populated-cell collision](tasks/managed-populated-cell-collision.md):
original8173's entire far-return route is in cell009_014, where385 default-bearing vegetation
placements and data-validated nonblocking families can replace the present coverage refusal.

## Stuck-state distinction

The actual `39213180` chain reads the current `SAIActorState` direction/speed, actual Unit movement
eligibility and attribute10. Its final vector is AIObject bytes28..36. `392E0C80` fills those bytes
from living status2+72, which this client copies from the living gravity vector. Actual velocity
has a separate cache at bytes16..24. The managed implementation preserves this offset contract;
it does not substitute a more intuitive physical-velocity test. These caches start zero, update
at the active physical-statistics boundary and survive inactive/relocation paths as the source does.

Active-goal stuck suppression first tests operation virtual72. The owned Approach, PathFind and
both trace variants have the native return0 qualifier, so their virtual88 result is never read.
Other active operation contracts require source-backed admission.

Full NPC gameplay readiness remains false. Native walkability/route selection, populated-cell
collision, nearby dynamic avoidance, wet/animated movement, perception, formation and complete
combat are separate remaining work in [the task file](tasks/cryengine-removal.md).
