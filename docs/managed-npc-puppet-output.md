# Managed NPC Puppet output

`ZoneManagedNpcPuppetOutput` owns the ordinary type5 NPC portion of the original
`CPuppet::Update` (`31172DEC`) which produces the `SOBJECTSTATE` consumed by
`CAIProxy::Update` (`30518020`). It uses the actual registered actor, model-backed
movement ability, sampled proxy pose, live Unit stats and owned navigation state.
It does not run a second physics step, move a Unit, drain signals, choose a behavior,
or assert that the rest of the Puppet/perception update is implemented.

The scene composes these phases in order:

1. Refresh the actual proxy position and sample body turn using the global AI frame delta.
2. Call `BeforeGoals(actor)`. This publishes the actor clock interval and clears movement
   direction. It preserves desired speed, urgency and the independent look/body history.
3. Execute `Entry.Phase.Update`, including the actual goal pipes, path-output refresh
   and continuation direction.
4. Call `BuildBeforeProxy(actor)` to repair ordinary NPC urgency, update delayed stance,
   calculate strafing, and produce look/body targets.
5. Apply the actual `ai_UpdateProxy` and GameObject activation gates. Only an admitted
   proxy phase drains its queued Lua signals.
6. Call `CopyStateForProxy(actor)` after the handler phase and pass it to the real managed
   proxy request consumer. This recopies live control fields changed by signals without
   repeating look smoothing or delayed stance.

The concrete owner constructor takes `ZoneManagedNpcActorScene`, `ZoneManagedNpcAiPose`,
`ZoneManagedNpcMovementAbility`, `ZoneManagedNpcActorMotion`, `ZoneStatCalculator`, an
optional lookup for the actual `ZoneManagedNpcNavigationLease`, and actual engine CVars.
`Bind` returns a generation-owned disposable lease. `CopyStuckMovementInput` reads current
movement direction and desired speed during goal execution, rather than controller delta
or a previous proxy request. `CopyTimePassed` exposes the separate actor interval to crowd
handling. Retirement invalidates every subsequent query/publication through the old lease.

## Native producers and calculations

`31007EAC` constructs the base actor's state with `31009BBC`, whose `31009ABC` tail sets
desired speed to **1**, path distance to **-1**, both urgencies to **.4**, and look/body/move
vectors to zero. These values belong to the NPC construction helper; an arbitrary new CLR
`ZoneActorControlState` still has CLR defaults. The before-goal clear in `311738DA` touches
movement direction, not desired speed.

`31172DEC` samples global AI delta through CAISystem v88. The verified primary table is
`311DDA00`, v88 is `31097678`, and the getter returns float5408. `CAISystem::Update`
(`3109B198`) writes that field from its global elapsed argument. Body-turn rate and
`3116BD2C` smoothing use this global delta, independently of skipped actor updates.
The current host's fixed global pump supplies that profile.

Actor2836 instead uses the current frame's microsecond clock minus actor4112, capped above
at .5 seconds. A nonpositive prior clock produces zero. `31173855` executes signed64
`CVTSI2SS rdx`; the decompiler's truncated `int` expression is incorrect. The source does
not clamp a backward clock's negative interval to zero. `BeforeGoals` updates this clock
before the movement clear, matching the admitted ordinary-update branch.

Registration `392ADC00` uses the resolved actual ActorModel's `turn_speed` for both idle
and combat look speed, converting degrees to radians, and its `face_target_instantly`
flag for strafing. It reads the Lua look fields only when that ActorModel lookup fails.
The registered snapshot remains independent of later Lua mutations. A missing catalog
does not authorize an output profile. Source AIParams constructor `3100891C` supplies -1
for the fallback turn-speed fields. The distinct crowd radius at actor1620 is typed
`avoidanceRadius` plus resolved `pathRadius`, not scaled agent `passRadius`.

Authored negative radius sentinels use the real `392AEA32..392AEC3D` registration branch.
Negative `passRadius` first uses `actualScale * ActorModel.Radius`; `392ADBC0` selects the
**first** source-order stance with content ID2, otherwise the **last** row, or null for an
empty vector. If that row has `UseCapsule`, `actualScale * max(SizeX,SizeY)` replaces the
model radius. This differs from the physical stance catalog's last-duplicate-wins lookup.
Nonnegative pass radius scales through the actual registered entity. Negative `pathRadius`
then copies the resolved pass radius. `392655C0` produces UnitModel697 from the actual
model's `Big` flag; only that flag together with `ForceNavigateAroundObject` enables the
subsequent single-precision path-radius multiplication by1.1. The non-Actor AimBox fallback
remains a separate unadmitted profile. Wrong-typed Lua scalar values retain typed defaults
and release their returned owned table/function references.

`3116774C` selects the Puppet's actual copied alertness speed, returns zero for instant
strafing, and applies live Unit attribute72 as `speed * (intAttribute * .01 + 1)`. The managed
owner uses the existing complete stat calculator and refuses unresolved sources.

`3116B94C` calls AIUnitSystem v192, which is **CanTurn** (`39212F30`), rather than a renderer
or animation strafe capability. It requires clear Unit bytes32080/32081/32091/32092 and a
null actual model v320 input pointer. The dedicated NPC model constructor supplies that
null client-input branch. The actual Unit status owner supplies stun, sleep, psychokinesis
and impossible-rotate from the corresponding BuffMan counters and copied-flag lifecycle.
It retains refresh, removal, death/reset and later publication history independently of
current buff presence or collection identity. A timing-only collection replacement cannot
invalidate this proof. An incomplete/reentrant effect mutation still causes a named refusal.

The original recorded hostile profiles restore harmless buffs:7987 has good1965;
8173/8177 have hidden6613 and3590. Their actual descriptors set none of these four flags.
Rejecting any nonempty BuffMan list was therefore an admission bug, even when that list
was still the exact original snapshot. Their supported turning decision now uses the
actual flags. This does not admit unowned model effects or other Puppet profiles.

`3100675C` invokes AIUnitSystem v232, `IsCarryingHeavyBackpack` (`39213110`). Its
`39260030` consumer returns false directly for Unit kind1/NPC. The ordinary NPC urgency
branch therefore restores goal urgency only when it exceeds current urgency plus .0001.
Character and Slave inventory branches are separate, unadmitted profiles.

`3116CCBC` supplies the look/body candidates. It can select the actual loose-attention
target, clamp nearby attention height, or sample the active path. Ordinary sampling uses
`CNavPath::GetPosition(2.5)`; an actually allocated predictive follower uses its current
target and registered look-ahead through `3114C0D8`. The output inspects
`ExistingPredictiveFollower` without allocating one. A CLR object existing eagerly would
not prove the original actor3984 pointer existed.

For moving planar targets, XY is projected forty meters along the path/body blend.
The **look** candidate preserves AI view Z while the **body** candidate uses entity-origin
Z. Turn smoothing retains the original direction-quaternion construction, its double
intermediates, source slerp branch and scalar float ordering. A non-strafing update leaves
the previous body target intact. Look-enable follows the actual `LookAt` goal's
`UseBodyDirection` producer at actor276; speed or target presence does not invent that flag.

Debug CVars `ai_ForceStance` and `ai_ForceAllowStrafing` apply after look calculation and
only inside the proxy-enabled branch. A nondefault `ai_ForceLookAimTarget` refuses until
the actual named-entity producer is owned. Source default `"none"` remains literal and
case-sensitive. Disabling `ai_UpdateProxy` still permits the earlier Puppet calculation,
while suppressing proxy-only overrides, handler drain and request production.

## Executed evidence and limits

`tests/AAEmu.Zone.Tests/Fixtures/npc-puppet-look-native-controls.json` contains:

- **42** original `3116CCBC` controls executing its original strafe, path and smoothing
  helpers: fresh idle, retained body history, moving/short paths, attention types and
  height clamps, loose targets, turn rates, combat speed, attribute72, first/retained
  strafing, instant-strafe flag and a special actor-target suppression control.
- **15** original `31173842..311738BF` interval controls, including clocks beyond32 bits,
  the half-second boundary, a backward clock, and negative/zero prior clocks. Their
  interval bits and published64-bit clocks match exactly.
- One fully executed original `31009BBC` constructor, including `31009ABC` and nested
  reset helpers, whose defined control defaults are compared bit-for-bit.

`npc-movement-radius-native-controls.json` adds **102** exact-bit comparisons against the
original radius block and its actual `392ADBC0` helper. They cover explicit/sentinel radii,
three scales, empty/no-ID2/duplicate-ID2 source vectors, capsule and noncapsule rows,
signed zero, and every combination of the two inflation flags. A composed negative-sentinel
test verifies actual constructor admission and the later path-look-ahead/avoidance values.

The oracle uses offline copied PE instructions under Unicorn. It never loads an engine
module. Its typed proxy/Unit/clock inputs are explicit controls, not live scene discovery.
CRT allocation/copy services are emulated; square-root instructions execute directly;
trigonometric imports use scalar libm rounded to float. The 42 look comparisons allow
30 micrometers to account for this stated math-library boundary. This is evidence for
the admitted calculations, not full original actor-update or locomotion conformance.

External scripts and exact extracts are in
`D:/aaemu-runtime/research/return-puppet-output/`. Permanent owner controls cover retained
speed, actual LookAt enable lifecycle, post-handler scalar recopy, elapsed-clock separation,
debug ordering, stale/reentrant retirement and independent replacement history.

Formation-target overrides, positive smart-object/animation actor-target phases, full
perception/aim producers, swimming/flight, unknown target identities and the unowned Unit
turn-status effects remain explicit dependencies. The current goal/extension inventory
does not create formation or special actor-target owners; the admitted path uses their
verified constructor-null/zero branches. A future producer must install its real consumer
before those branches can be admitted. The global NPC gameplay-ready gate stays closed.
