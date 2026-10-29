# Managed COPApproach and ordinary ground trace

The executable `approach` goal now follows the recovered CryAI operation rather than
turning the target into a fixed-speed wish or a teleport. `ZoneApproachGoal` owns its
`ZoneApproachPathFindGoal` and trace leases, waits for the actor's real path decision,
and emits signals through the existing scheduled proxy. The enclosing scene supplies
the authored graph, collision queries, exact actor identity, physical status, movement
abilities, movement-speed table, target objects and body authority.

The actual default predictive configuration uses the separate source-backed
[predictive follower and trace](managed-predictive-navigation.md). The ordinary leaf
described below is admitted only for configurations that select it.

## Original boundaries

The original image is `cryaisystem_dedicate.dll`, SHA-256
`a740d58c6a3c851d84967e9513125c48b9326cbb70462ad1f57e6e60aadfe921`.
Addresses below refer to that original image unless stated otherwise.

- `310F6574` constructs Approach from distance/duration, accuracy and flags.
  Bits1/2 select last-result target/look; `0x400` permits partial paths;
  `0x4000` makes the first scalar an absolute duration; `0x8000` stops on
  animation start. Optional vector X variation draws `random()%10 / 9.0f` only
  when greater than `.01f`. The native constructor does **not** copy the parsed
  text into the optional no-path signal or enable the serialized percent flag.
- `310E06A8` constructs the independent operation state; `310E4F88` executes it;
  `310DA9B8` resets its child leases, both paths, loose-attention version and
  stuck monitor. Actor path-request cancellation
  is part of `3115D474`, rather than an incidental collection clear.
- `310D5B98` returns the configured end distance, initial percentage distance,
  or `-(actualMovementSpeed * duration)`. Speed is `31169B4C` with the current
  urgency and its strafe argument false. `MaxSpeed`, capsule radius and AI pass
  radius cannot replace that callback.
- `310DBA08` is the pathfinder operation. The explicit-target branch reads the
  target's entity-preferred origin, direction and object type at submission time.
  Its request to actor v688 carries `type!=1`, building ID `-1`, actual tolerance,
  end distance and the exact target. Decision0 waits,3 adopts a path,4 fails.
- `311757BC` publishes path decisions, applies route adjustments, trims the end
  through `31141B30`, updates adaptive urgency and copies CurrentPath to OriginalPath.
  The scene search/publication owner must preserve that boundary and graph revisions.
- `310DBFC8` / `310F1878` / `310EE988` implement COPTrace. Its ordinary ground leaf
  is `310EC518`; flight, path-follower alternatives, smart objects, animation/exact
  positioning and regeneration are independent branches.

Approach's first request execution returns running even if the scene finishes the
search synchronously. On a later found decision it marks the path found and re-enters
the target-selection phase before executing trace. Trace completion supplies the
terminal result. Missing/disabled targets fail through reset.

Approach initially compares the AI/view position; ordinary trace uses the actual
entity origin. Its source-backed pathfinder-disabled branch uses a strict
`endDistance > distance` comparison, and derives direction from the entity origin.
That leaf is available for actual matching actor profiles. Production cannot select
it to bypass an actor that requires pathfinding.

## Path and speed leaves

`ZoneNavigationPathSteering` ports `3113F5F0`, `311409D0`, `311410D0` and `3113E5D0`:

- segment-bisector advancement and monotonic first-segment projection;
- short-segment removal and preservation of the final point;
- radius-constrained look-ahead, including the original eight refinements;
- actual-progress/actual-velocity stuck adaptation of look-ahead;
- copied direction, remaining distance, path distance and curvature outputs.

These functions mutate the owned path's progress, never an actor pose. Debug drawing
collections are cleared without inventing a renderer. Existing path sampling keeps
the authored height even when distance is measured horizontally.

`ZoneGroundTraceMath` ports ordinary ground steering, corner and end braking,
optional actual living-ground slope response, acceleration/deceleration and zero-urgency
deceleration. `ZoneGroundTraceGoal` writes only the shared actor controls. Its first
trace interval is the original `.1f`; later intervals use the actual scene frame clock.
At the end, nonnegative accuracy waits until actual forward velocity is at most `.01f`.
The trace-owned positional type2 AI object follows the ordinary path look-ahead sample,
which is separate from the radius-constrained steering point. Its scene lease is released
on reset/retirement. `OnMove` handlers can veto an update before movement effects.

The caller must supply actual values for look-ahead, path radius, speed look-ahead,
corner/slope slowdown, acceleration, deceleration, adaptive look-ahead, current navigation
type and physical status. There are no replacement default speeds or physics steps.

`310E13D8` also performs turn-in-place before ordinary speed calculation. The bounded
implementation admits its verified stateless no-turn cases and explicitly refuses a
reachable multi-frame turning/entity-rotation branch. `NeedsTurnInPlace` uses actual
prior desired speed, maneuver threshold, body direction, urgency and obstruction state.
Equality enters the turn branch. A previous active turn cannot be silently reset by
calling the stateless helper.

## Scene contracts and lifetime

`IZoneApproachGoalServices` supplies target/path/trace/loose-attention owners and the
current-generation guard. `IZoneApproachPathFindServices.RequestPath` receives a copied
`ZoneApproachPathSubmission`; it owns pending search cancellation, authored topology,
physical segment admission, route adjustment and publication. `IZoneGroundTraceGoalServices`
supplies the real marker, OnMove event, physical frame, speed calculation, navigation
classification and actor avoidance. `ZoneGoalOperationFactory` accepts an optional
Approach service as its final constructor argument and refuses an absent owner.

Callbacks are followed by exact-current/operation-epoch checks. A callback that retires
the actor or resets the current operation prevents later signal, target, trace or
control effects. Child leases are released once. Reset clears CurrentPath through the
actual cancellation owner and clears CurrentPath, OriginalPath and target2840 through
`3115D474`. Its IAIActor-relative offsets96/304 are absolute actor2400/2608; a new path
request also clears both paths before publication.

The movement owner can queue `ZoneManagedNpcBodyLease.RequestOrientation` through
`ZoneManagedPhysicsBackend.SetLivingOrientation`. This consumes the absolute upright
orientation already produced by the CActor/animated-character owner. Native derived
movement `392F4340`/`39305870` produces its relative rotation and hands the request to
animated `30661100`; computing that source request remains the movement owner's work.
The physical boundary neither substitutes a pose correction nor computes another AI yaw.
It copies the finite upright unit quaternion, preserves its sign and rejects unowned
tilt/scaled-quaternion/general-body profiles. It uses the same command log as wishes,
stance and correction requests. At drain, only the existing owned body's orientation
changes: position, actual/requested velocity, support caches and pending requests survive.
The scene remains the only physics step and Unit/model pose publisher.

The native stuck monitor is **not** an actual-velocity timer. `3115C414` first filters
active goals through `31158DA4`/virtual72, then tests virtual88 only on admitted types.
Approach, PathFind and Trace all return false at virtual72. Server AIUnitSystem v240 resolves to x2game
`39213180`: after actual Unit/CanMove/model/animated/movement-owner gates, it compares
the animated actual velocity against the movement state's requested direction/speed
and Unit attribute10. `ZoneApproachStuckMath` ports only that final comparison.
The caller supplies its real owner gates; the helper does not turn absent models into
clear movement. The source timer is capped at3 seconds and queues `OnPathStuck` only
when strictly greater than1.5 seconds, then resets.

## Independent controls and remaining work

Fixtures embedded in the tests retain the original-instruction observations:

- `npc-path-steering-native-controls.json`:120 advancement, look-ahead, curvature and
  update controls, including corners, reversing paths, slopes, 2D/3D and coordinates8000.
- `npc-ground-trace-native-controls.json`:22 full `310EC518` scalar controls, with
  declared physical-status and speed-triplet callbacks. Marker lifecycle, certified
  no-turn behavior and post-trace avoidance are explicit external boundaries.
- `npc-approach-native-controls.json`:18 scenarios/30 frames executing `310E4F88` and
  the explicit `310DBA08` branch. Scene path decisions and COPTrace results are controlled
  ABIs. This verifies orchestration, request arguments and signals, not native search.
- `npc-turn-admission-native-controls.json`:13 `310E13D8` entry controls. Execution stops
  at the active-turn boundary before its camera/entity effects, preserving the refusal.

All controls run the original PE instructions under Unicorn without loading engine
modules. CRT square root uses scalar SQRTSS; copied status, allocation, diagnostics and
request/signal boundaries are listed in each artifact. Local scripts/source extracts
are under `D:/aaemu-runtime/research/return-approach`; builds and tests are isolated under
`D:/aaemu-runtime/return-physics-artifacts`. Floating comparisons have declared small
single-precision tolerances; they are not a claim of global bit-for-bit engine parity.

Actual scene avoidance `3116E80C`, the active ordinary turn state,
flight, vehicles, smart-object/exact positioning, path regeneration, water/actor route
adjustments and full original search cost/order remain separate owners or named refusals.
The existing tiled A* search retains its documented replacement cost policy. This slice
does not promote it to original search parity or admit arbitrary routes across missing
geometry. A successful far-return production probe requires the scene composition to
admit all reached branches and publish movement through its single physics authority.
