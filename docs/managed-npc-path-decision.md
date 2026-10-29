# Managed ordinary NPC path decisions

`ZoneNavigationPathDecision` ports the ordinary ground path-publication portion of
original `CPuppet::HandlePathDecision311757BC`. It uses the existing independent
current/original path containers, end trimming and adaptive urgency helper. The scene
still owns actual graph search, generation/revision checks, physics/water inventories,
and request/argument cancellation. A graph route alone does not establish that all
native adjustment stages are admitted.

The supported flow stages a copied candidate, preserves the previous current path's
version plus1, saves the requested end **before** adjustment, proves every original
water probe dry, trims `abs(EndDistance) > .0001f`, admits the actual ordinary point
types, updates urgency from planar path length, executes the certified empty dynamic
obstacle processor, then copies current to original. Original gets current version
plus1. A successful found result with fewer than two points remains decision3; the
original emits a diagnostic rather than converting that result to no-path. No-path
uses `3115D474`, which clears **both** current and original paths and clears the
argument after cancelling target state. The pure helper reports `ClearArgument=true`
for its concrete scene owner to perform that last owned effect.

Unknown adjustment branches refuse before publishing a partially admitted path. This
atomic unsupported-profile refusal is replacement ownership policy; the original
function can already have modified its current path before reaching a later stage.
Ordinary publication does not fabricate an immediate direct route or report success
for a missing scene query.

The copied profile must come from the real producers. `392ADC00` reads actual
Properties/AIMovementAbility and generates regular navigation capabilities134
(flying184, underwater172). The shared typed ScriptAny reader accepts numeric0/1 for
requested boolean fields; original `usePredictiveFollowing=1` is true, without any
VM-wide Lua coercion. `3115CAA4` returns planar immediately for capabilities134 with
a nonswimming proxy. Cached navigation type is full actor+3760; the constructor calls
`3115E764`, initializing it to1, and the ordinary capability branch retains that1.
Mixed capabilities can require actual current point types or a region query and
cannot be assumed planar. Native constructor/field anchors are:

| Field | Producer/use |
| --- | --- |
| Flying/underwater | Actual Properties flags, ability175/176; distinct processors in31143130. |
| Swim threshold | CAIProxy280/30515520 ->IActor216/392D7AF0: actual actor164 times entity uniform scale. |
| Body maximumZ | CAIProxy24/305159C0 reads movement-controller state+80. Actual accepted-stance AABB: `max(hc + SizeZ + (capsule ? max(SizeX,SizeY) : 0),0) - hp`. This is not the physical shape's radius or a full height span. |
| Slight backoff | Actor2816 startsfalse in31163974; COPTrace310EAF38 later copies its byte150 there. |
| Spline mode | Actor4364 starts0 through31171F8C's qword4360 clear. Positive values invoke an additional spline consumer. |
| Dynamic horizon | Actor1672 is `pathRegenIntervalDuringTrace`. Actor1768 is **Combat.Run.Maximum**, at ability+272 +group1*48 +Run2*12 +component2*4. It is independent of current movement speed/stance. |

Creation parameters start at CNavPath+100. The directional flag is+156;
`AllowDangerousDestination` is+152, and end distance+160. The forbidden slight-backoff
branch requires a planar path, the **directional** flag and actor2816. Treating that
branch as the dangerous-destination flag would select the wrong processor.

`31143130` samples the first node and each segment at most20 times. Its three-meter
steps normalize the full XYZ delta, while the decision to take another step compares
XY distance squared with9. It queries even repeated endpoints in source order.
`3113CFC0` sends an upward ray of `max(2,bodyMaxZ)`. The initial ceiling is
`waterLevel + (bodyMaxZ - swimThreshold)`; an upward hit replaces the distance with
`hit.distance-.05` and ceiling with `hit.point.Z-.05`. The lower ray starts at that
height and spans-10000. Its hit sets floor to `hit.point.Z+.05`; only a miss uses
the renderer terrain height. Both rays use object mask0x107 and flags0x2000F.
Actual water no greater than the floor proves the unchanged dry path. Water transitions,
flying/underwater insertion and truncation remain named unsupported consumers.
`31141940` visits smart-object processing only on navigation type128; this ordinary
profile requires actual ground-triangle type2 points.

Puppet virtual816 is `31170A6C`, rather than an empty notification. It invokes
`3117050C` dynamic obstacle adjustment, and can later invoke spline conversion.
`ai_AdjustPathsAroundDynamicObstacles` and `ai_CrowdControlInPathfind` both default1.
The managed path does not turn them off to get a passing scene. `31151CCC` receives
these actual inventories:

* The AI actor grid, layer0, within strict XYZ distance less than
  `(15 + abs(Combat.Run.Maximum*pathRegenInterval))²` of the actor's actual AI position.
  It excludes self as an obstacle and remembers returned physical entities so they
  are not processed a second time. Other actors require navigation interaction,
  velocity and shape processing; object types5/7/100 remain explicit gates here.
* Sleeping/rigid physical entities inside the path-prefix AABB, expanded15 on each
  axis. The prefix length is `abs(Combat.Run.Maximum*pathRegenInterval)` from current
  path fraction. `3100D4C8` changes mask0x10006 to0x50006. Static1, living8 and terrain256
  are excluded by those native class bits; authored static walls are not silently
  reclassified as dynamic obstacles.
* The actual CAISystem avoidance-sphere map at5808, initially empty in3107FD08.
  IAISystem792/3105EBF8 replaces an owner-keyed center/radius record or removes it
  when radius<=0. `ZoneNavigationAvoidanceSpheres` owns that producer/lifetime;
  nonempty sphere processing remains refused until its geometry consumer is ported.

`ZoneNavigationObstacleCensus` carries the copied query and actual candidate records,
not an arbitrary allow callback. The scene must reject incomplete or unclassified
inventory. A known single caster plus only static/terrain/living physical owners can
therefore prove the original default query has no dynamic candidates. No obstacle then
expands the original sentinel box, so subsequent navigation-node obstacle collection
is bypassed. The real empty-vector processor still calls3113EAA0 and increments the
current path version; it also clears both adjusted-object histories. Those effects
are preserved. Relevant nonself, sleeping/rigid or sphere candidates remain named
unsupported branches; a ray/sweep miss cannot certify their absence.

The query builder preserves3100D534's normalized-length return `1/(1/sqrt)` and its
small-vector cutoff. Substituting the original square root can change a float ULP.
The adaptive helper preserves the independent goal-history variant: its update can
write the goal maximum while selecting tiers from the actor's maximum. Executed
machine controls also confirm NaN's urgency tier is positive4 under the reversed
COMISS/JBE chain. A decompile-based suspicion of a NaN bug was disproved by those
controls; no behavioral change was made to the existing helper. The separate
speed-table unordered Sprint selection is unchanged.

`NavigationPathDecisionTests` compares40 complete original HandlePathDecision controls
with exact float bits, path data/version, cached navigation type, water/ray requests,
AI radius, physical bounds/mask and urgency. They include dry straight/corner/slope/long,
empty/single, positive/negative trim, dynamic off/on, no-path and ray-hit controls.
Another128 original3116847C/310C10DC math controls cover NaN and independent history.
Both checked-in fixtures are under `tests/AAEmu.Zone.Tests/Fixtures`; the independent
offline runners, matching binary SHA256 and exact source extracts remain under
`D:/aaemu-runtime/research/return-path-decision/`.

The full-flow oracle executes the original path adjustment and deep-copy instructions;
only owned CRT/diagnostic services and explicit current body/water/terrain/AI/physics
observations are intercepted. Its actual input query inventories are one self actor,
no classes2/4 and no registered spheres. It establishes this bounded profile, not
live-world conformance, general dynamic obstacle avoidance, or complete navigation.
No engine DLL is loaded. Focused checks also verify missing/wet/mismatched census
refusals, exact actor-radius boundaries, physical class exclusions, history clearing,
requested-end preservation, no-path effects and sphere lifetime.
