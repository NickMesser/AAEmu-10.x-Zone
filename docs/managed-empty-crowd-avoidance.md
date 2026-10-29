# Managed empty-crowd avoidance

`ZoneEmptyCrowdAvoidance` implements the empty actor-census branch of original
`CPuppet::UpdatePuppetInternal` helper `3116E80C`. Its output is the source movement
direction and desired speed, together with persistent crowd refresh, solver and timer
state. It neither checks static collision nor writes an entity/body pose.

The source image is `cryaisystem_dedicate.dll`, SHA-256
`a740d58c6a3c851d84967e9513125c48b9326cbb70462ad1f57e6e60aadfe921`.
The implementation executes this branch without allocating synthetic obstacles or
using a replacement steering rule.

## Inputs and ownership

Each exact actor generation owns one instance and disposes it on retirement. The
actual entity position, actor view direction, control direction/speed, frame clock,
frame seconds, alertness and update priority are inputs. View direction is the
source `actor.v128` value; body direction cannot substitute for it.

The radius is actual actor1620, named `AvoidanceRadius`. `392ADC00` registers it as
the typed `movementAbility.avoidanceRadius` plus the resolved `pathRadius`. It is
independent of the scaled agent pass radius at actor1492 and of physical dimensions.
The source entry gate returns immediately when this avoidance radius exceeds15.
The additional alertness gate reads raw actor1593/1594. Unknown values are refused
when the predicate depends on them. Alertness below2 makes those fields irrelevant.

On refresh the caller supplies the complete union of the two actual grid-query
inputs, with object identity and each grid entry's cached position. Wrappers
`3105ECA8`/`3105ED18` query the two buckets through `3108FEB4`, with radius30 and a
strict full3D squared-distance comparison against900. The helper ignores exact self
and entries outside that strict radius. Another entry inside requires the populated
avoidance owner and raises a named refusal before committing state. Reusing an
entity ID does not make an old object the new actor's self.

This empty-census admission is conservative: it does not infer the source's later
candidate hostility, activity, target-pointer, interaction or proxy-ignore filters.
The scene must own inventory coverage; an empty caller-provided list alone cannot
certify that an unknown actor inventory is empty. Scene composition may require a
fresh empty census each frame; the source helper itself consumes it only at refresh.

## Source state and update order

Constructor `31171F8C` initializes last-census time, was-moving, steering side and
obstruction timer to zero. `310458DC` initializes solver origin to zero, forward
to(1,0,0), side to(0,1,0) and radius to1. These values are copied independently.

Movement requires direction length strictly greater than `.01f` and desired speed
strictly greater than `.01f`. A stationary update changes only was-moving to false.
A subsequent moving update refreshes immediately. Otherwise the refresh condition
is elapsed seconds strictly greater than `.5f`. Binary `3116E94A..3116E968` subtracts
the full64-bit microsecond clocks, converts the signed64-bit difference to float
and multiplies by `.000001f`; the decompiler's apparent DWORD read is incorrect.

For a type5 ground Puppet, source priority0/1 forces a full solver rebuild. Other
priorities rebuild on a full update or the transition from stationary to moving.
`3104595C` records the actual entity origin, flattens and normalizes view direction
using the source minimum-normal-float term, builds its perpendicular side and stores
twice the avoidance radius. Actual entity bounds are read for the per-candidate
height tests, but no empty-loop result depends on them; the helper does not invent
an entity bounding box.

`3104496C` with zero constraints returns the original direction and sets steering
side to zero. This application is outside the rebuild gate: partial updates retain
the existing basis but still apply the solver and decay the obstruction timer by
actual frame seconds. The timer is then clamped to0..3 in source order. A type other
than5 or a3D actor has no eligible candidates to process and skips this ground
solver/timer branch. An entry-gated update leaves all persistent state unchanged.

`ZoneEmptyCrowdAvoidanceResult.SolverRebuilt` identifies rebuilding the basis,
independently of applying the existing empty solver. Result/state records copy all
values. Refused refreshes, nonfinite admitted inputs and disposed owners publish
no new state or movement. The helper never steps physics or advances the scene clock.

## Independent verification and remaining scope

`npc-empty-crowd-native-controls.json` records31 scenarios/40 frames executed from
the original `3116E80C`, `3104595C` and `3104496C` instructions under Unicorn without
loading engine modules. Controlled boundaries provide real-shape-independent empty
grid vectors, entity/view getters, source clock, debug CVar0 and CRT operations.
Cases cover exact refresh/movement thresholds, clocks beyond32bits and signed64-bit
wrap, all entry gates, priority-forced rebuilds, partial updates, stops/restarts,
timer decay/clamps, nonnormalized/zero view vectors and coordinates8000. Two distinct
controlled entity bounds confirm their irrelevance for the empty loop. Seeded timer
controls are copied native prior state, separately identified from constructor state.

Focused tests compare every captured scalar, basis vector and refresh/rebuild event.
Additional controls verify exact self, strict3D radius admission, atomic refusals,
source cached-refresh timing, callback-free retirement and reentry with a reused ID.
The external generator/extracts are under
`D:/aaemu-runtime/research/return-approach/empty-crowd-native.py` and `3116E80C.c`.

Populated crowd constraints, later candidate eligibility and nonempty solver outputs
remain unimplemented. No successful empty-census control claims dynamic-obstacle,
static-clearance, arbitrary actor-grid or complete native update parity.
