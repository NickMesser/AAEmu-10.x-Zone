# Installed NPC movement speeds

`ZoneManagedNpcMovementAbility` copies the original registration tables once, after
`Server.OnInit`. It retains all seven stance groups and four urgency triples per group:
desired, minimum and maximum. Later Lua table edits do not silently re-register an actor.
Missing table/fallback profiles and radius branches with unowned model inputs retain named
refusals. No zero speed table is manufactured to admit them.

The existing model-script generator preserves the original six-decimal formatting of authored
model speeds. The registration reader retains those actual Lua values, the constructor defaults
for successfully absent scalar inputs, scaled agent radius, and the native negative-look-ahead
fallback `maximumDesiredSpeed * .1 + pathRadius` (maximum starts at5).

Negative pass radius uses the actual ActorModel radius and entity scale, or the selected raw
stance's capsule dimensions. The source selects the first stance with ID2, otherwise the last
row; this lookup is distinct from the installed CActor stance-slot catalog. Negative path radius
copies that resolved pass radius, then the actual Big/ForceNavigateAroundObject model flags can
inflate it by1.1. The original five NPC resources use this supported model-backed path. The
non-Actor AimBox fallback retains a named refusal. The radius fixture compares102 executed
original-instruction controls bit for bit.

`ZoneActorMovementSpeedTable` implements `31169B4C`. Urgency thresholds are`.305`,`.7` and`1.2`;
the proxy's accepted stance selects the source stance group. Directional scaling uses the actual
previous/body turn, body/move directions, installed directional reference speeds and the copied
CActor controller angle. Desired speed remains distinct from the triple's minimum and maximum.
The actor's postphysical statistics own the controller-angle producer and its smoothing history.

The checked-in native fixture executes183 original controls: every stance/urgency selection,
directional scaling, special values and boundary cases. Table/minimum/maximum checks are bit exact;
desired directional results allow at most two float ULPs only across the explicitly substituted
CRT`atan2f` boundary. The source's reversed comparisons select Sprint for NaN urgency. This is
independent of the adaptive-urgency classifier, whose own128 executed controls preserve its
separate native behavior.

Typed ScriptAny boolean reads accept actual numbers as well as booleans: numeric1 enables the
original NPC's predictive following, zero and NaN are false, and numeric strings are not converted.
The VM's general Lua truthiness is unchanged. Nineteen executed original table-read controls cover
that conversion chain.

When the actual ActorModel resolves, its TurnSpeed (degrees converted to radians) and
FaceTargetInstantly override the Lua look-speed/facing fields as in`392ADC00`. Puppet look output
requires that model-backed registration owner. Physical CActor normal speed, walking latch and
attribute10 remain separate consumers; an AI desired-speed table is not a body velocity.

See [navigation and physical requests](managed-npc-navigation.md) for scheduling and remaining
coverage boundaries. This implementation does not establish full navigation or NPC gameplay parity.
