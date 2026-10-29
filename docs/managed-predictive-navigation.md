# Managed native predictive path following

`ZonePredictivePathFollower` ports the original `CPathFollower`; it does not use the
repository's replacement fixed-speed `ZoneNavigationPathFollower`. `ZonePredictiveTraceGoal`
ports the predictive leaf of COPTrace and writes copied actor/proxy output. The scene
owns route publication, actual physical status, speed/ability inputs, navigation and
avoidance callbacks, body requests and the single physics step.

The actual native CVar is `ai_PredictivePathFollowing`, registered with default1 at
CryAI source56265..56276 and stored in AIEnv552. `31158AC4` creates the actor's follower
when its copied `usePredictiveFollowing` field1602 is true. `310EE988` selects predictive
`310EC118` when the follower exists and that CVar is nonzero. Lua numeric1 is true.
Authored8173 therefore requires this branch under the original default; selecting the
ordinary trace or changing the CVar cannot establish its parity.

## Source and ownership

The source image is `cryaisystem_dedicate.dll`, SHA-256
`a740d58c6a3c851d84967e9513125c48b9326cbb70462ad1f57e6e60aadfe921`.

- `3114D028` constructs copied follower parameters.
- `3114D2F8` rebuilds its trajectory when `CurrentPath.Version` changes. It retains
  source navigation types, computes the original full3D samples around a corner,
  generates curvature-dependent lateral offsets and appends the terminal extension.
- `3114C298` finds the closest effective segment, advances the initial target and
  projects actual velocity onto the target direction for its retained-speed state.
- `3114B678` advances the target toward configured look-ahead, with the original
  maximum advance `3 * frameSeconds * maximumSpeed`.
- `3114C748` computes movement velocity, applies original acceleration/deceleration
  and speed bounds, and tests the terminal plane. `3114BB58` computes remaining distance.
- `3114C0D8` samples effective nodes from the current target/index; the copied sampler
  preserves signed-distance extrapolation and reports the actual distance at the end.
- `3114DD78` performs the main update and optional forward prediction. Its ten samples
  span one second; each `.1f` output interval contains two `.05f` steps using the
  original order of position integration and next-velocity calculation.
- `310EC118` refreshes parameters from actual movement abilities and the source speed
  triplet, then publishes direction, desired speed, distance and prediction data.

The follower belongs to the actor at native3984 and borrows its stable CurrentPath.
Finishing or resetting a trace does not dispose that actor-lifetime owner. A new route
is detected by the path version; no invented nearest waypoint or fallback link is added.
Retiring the actor disposes the follower and rejects further updates. Copied predictions
do not step physics, change a Unit/entity pose or imply that a retired route remains valid.

Parameters preserve normal speed, path radius, look-ahead, maximum acceleration and
deceleration, minimum/maximum speed, end distance, stop-at-end and use2D. These are actual
AI inputs. Physical capsule radius and authored stance maximum speed cannot substitute
for them. The optional source curvature-slowdown byte3120BF04 defaults to zero; the
standalone follower also ports its true branch without claiming a recovered runtime writer.

Predictive trace clamps normal speed with the original scalar arithmetic. It sets
stop-at-end to `!ContinueMovingAtEnd && !ClosedLoop`, and the additional follower end
distance to1 only when continuation has an actual type1 path target and no exact-position
request. Other Approach end-distance trimming belongs to the route publication owner.
The predictive leaf does not run ordinary `310E13D8` turn-in-place or write its movement
context1/3. Its trace dummy is initialized from the entity position; it does not perform
ordinary trace's subsequent look-ahead marker updates.

## Actual prediction eligibility

Native Puppet4728 is initialized to3 by constructor `31171F8C` (instruction3117221A),
reset to3 by `3116D3CC` (3116D502), and reset to3 before a manager full update in
`3109B198` (3109B89E). These are the only writes/address references to that field in
the exact CryAI image; the other three references read it. An external owner must not
replace that actual per-generation state with assumed zero.

`310EC118` uses one-second prediction only when a non-Puppet or unsigned Puppet4728<2
qualifies. At priority2/3 it sets the prediction horizon to zero, clears the shared
vector to zero entries and passes no prediction pointer. Movement still runs normally.
The managed trace passes that actual eligibility to the follower and publishes an empty
prediction output for the zero-horizon branch. It does not invent future samples or retain
another actor's old output.

`IZonePredictiveTraceGoalServices` extends the existing trace scene services with the
actual actor follower, a copied predictive physical/ability frame, the current path target
type, profile admission and prediction publication. Current-generation checks follow
external callbacks. OnMove veto, repeated same-frame clock values, terminal cleanup and
retirement preserve the trace's source ordering. Scene consumers assign prediction times
`(index+1)*.1f` and retain body status as applied physical state, separately from queued
movement/orientation requests.

## Independent checks and limits

The fixtures execute the original PE instructions under Unicorn without loading engine
modules:

- `npc-predictive-native-controls.json`:26 scenarios/42 frames execute full trajectory
  rebuild, signed samples, closest projection, target advance, velocity, endpoint and all
  predicted steps. They cover corners, reversing routes, repeated points, 2D/3D slopes,
  coordinates8000, braking, continuation, zero acceleration/deceleration and curvature.
- `npc-predictive-trace-native-controls.json`:15 scenarios/28 frames execute original
  `310EC118` and the original follower together, checking actual copied controls and every
  prediction sample/time. Cases include priority3/2/1 and a0→3 transition, continuation
  target types, closed loops, normal-speed clamps and tiny velocities.

Only allocation/CRT and declared scene ABI boundaries are supplied. Source path math
is executed rather than replaced by fixture routes or another steering algorithm. Marker
lifecycle and post-trace navigation/avoidance are separately declared boundaries, so
these observations do not claim full native scene behavior or original path-search costs.
Fixtures retain the original zero-look-ahead NaNs as named observations; the managed
profile explicitly refuses zero look-ahead before publishing movement. A finite scalar
failure or the explicit100000-iteration target-advance guard raises a named error rather
than returning fabricated clear space. The native loop has no equivalent fixed work cap.

Scripts and extracts are under `D:/aaemu-runtime/research/return-approach`, with isolated
builds under `D:/aaemu-runtime/return-physics-artifacts`. Ordinary trace remains available
for configurations that actually select it. Full far-return production proof additionally
requires the scene's real route adjustments, activity/census owners, navigation/avoidance
and CActor/animated/body publication path; no successful source-math fixture bypasses them.
