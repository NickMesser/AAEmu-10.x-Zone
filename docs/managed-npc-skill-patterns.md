# Managed NPC skill patterns and requests

`ZoneManagedNpcSkillPatterns` owns a generation's pattern, authored skill-list input,
selection, common `useskill` goal, World request and ordered acknowledgment state. It
uses the real Unit's casting fields and cooldown maps. Sending `ZWStartSkill` does
not start a local cast, spend mana, deal damage, apply plot effects or count as success.

This is a bounded execution profile, not complete NPC combat parity. Controllers,
phase skill lists, base autoattacks, special targeting, skill costs/modifiers and
locomotion still have explicit admission boundaries. `GameplayReady` remains false.

## Original 10.x path

The evidence is the original dedicated modules and their decompiles, rather than a
generic CryEngine API or another version of ArcheAge:

| Function | Recovered behavior |
| --- | --- |
| CryAction `30535D70` | `AI.SetSkillUsePattern`: entity handle, AI actor interface, signed integer; no Lua return values. |
| CryAI `31043C84`, getter `31043C7C` | IAIActor+376 writes full CPuppet+3376; +368 reads it. Constructor default is0. |
| X2 `39265E50`, called by `3927F610` | Populate `entity.AI.skills` from the actual NPC descriptor's `np_skills` links before AI character construction. |
| X2 `39354860` | `unit:NpcSetSkillList`: clear vector/map, iterate table, append numeric values or numeric keys whose values contain `{delay, strafeBoolean}`, then publish list readiness. |
| CryAI `310EA0B8` | Construct `COPUseSkill`: low flag bits1/2/4/8 and optional `OnRequestSkillInfo_<tag>` suffix. |
| CryAI `310E6B68`, `310E4838`, `310DF568` | Request authored skill information, pick, validate again, request a skill, await acknowledgment and optional delay. |
| X2 `393289C0`, `39327430`, `39328290` | Ordered candidates, multiply-high random selection, optional closest out-of-range selection, alternative threat targets, then optional base skill. |
| X2 `39457B60`, `3925ED90`, `3925C3D0` | Source/target validation and actual model aim-shape range checks. |
| X2 `39326730`, `39362920`, `3936D1F0` | Resolve the request target, send actual `ZWStartSkill`24; separate buff-cancel/autoattack shortcuts exist. |
| CryAI `310D9878`, `310D9958`, `310D9A38` | Ordered Start/Fire/End callbacks match skill type and timeline; a failed result is retained. |
| X2 `39456D70`, `39457170`, `39BF7A80`, `39BF7980`, `39BFA200` | Successful acknowledgments start global/skill cooldowns with resolved modifiers. |

`SUP_DEFAULT=0`, `SUP_BIG_MONSTER=1`, `SUP_ARCHER=2`, `SUP_FLYTRAP=3`.
The setter accepts other signed integers too. In the recovered dedicated picker,
only value3 changes the fallback test: auto-stick out-of-range selection is skipped
for Flytrap. Values0/1/2 do not acquire invented weighting policies. Other original
systems also read the pattern, including the Flytrap exception in nearby NPC help
selection; that help/perception consumer is separate unfinished work.

The goal flags are `Parameters.Integer`; the tag is `Parameters.Text`. Native factory
`184410` passes GoalParameters+40/+56 to `310EA0B8`. The constructor consumes only
the four low bits. This is distinct from `flyuseskill` and `bindedUseskill`, which
remain unsupported operations.

## Authored data and initialization

`ZoneContentDatabase.LoadNpcSkillContent()` loads source-order `np_skills`, skill
target/range/source scalars and enabled `Skill` unit requirements. Unknown skill
references stay in the lists and the picker skips their unresolved descriptors.
The six original role mappings are:

| Condition | Lua list |
| --- | --- |
| 0 | combat |
| 1 | idle |
| 3 | alert |
| 4 | dead |
| 7 | onAlert |
| 10 | onCombat |

Other conditions are not silently assigned to these lists. `Parameter1/2` are
retained; they are not substituted for the Lua keyed skill metadata. In particular,
NPC7987's condition5 skill14700 is not inserted into `idle` or `combat`.

Call `InstallAuthoredSkillLists` after the original server `OnInit` completes
(`BasicActor.ResetCommon` creates `entity.AI`) and before the character constructor.
It installs the actual child lists, not a selected behavior or a hard-coded skill.
The original behavior then chooses a list in `OnRequestSkillInfo` and calls the
bound `NpcSetSkillList` service. Each `useskill` execution consumes that response.

The read-only real-content probe loaded38,043 skill descriptors and17,010 NPC
skill rows. Templates8173 (`almighty_npc`) and8177 (`big_monster_roaming`) both
have idle skill40917. It has target type0, fixed range0..100, no cost/controller/
enabled unit requirements, cooldown8000ms, custom GCD7000ms, and ignores GCD
when checking eligibility. Their passive173 resolves through `passive_buffs` to
buff3590; it is not buff173. NPC7987 (`flytrap`) uses combat skill14133, target4,
range0..25, full-circle targeting and no obstacle check.

External local evidence: `D:/aaemu-runtime/research/npc-skill-pattern-content-evidence.json`.
The metadata probe completed with52 loaded modules and no Cry/X2/Lua51 modules.
It is a content/parser control, not evidence that World has executed a skill.

## Owned execution and boundaries

Bind the skill lease only after script adoption/behavior registration and the
actual living-body generation exist. The lease borrows that body; it does not
dispose the physical owner. Selection, script input, execution and callbacks
check the actual Unit allocation, script binding, catalog generation and body.
Retired allocations cannot issue requests or consume a successor's acknowledgments.

The common source profile uses actual life state, all four independent casting
identifiers, mana, progression passives and cooldown maps. Unknown/effectful buff
descriptors, status-tag rules, skill modifiers, GM overrides, enabled skill
requirements, costs and special controller/toggle/autoattack paths refuse.
The clean-buff inventory is conservative: exclusion is an unsupported-profile
diagnostic, not an invented native skill failure code. Native fresh restriction
state is the admitted profile; general restriction producers are not complete.

General unit targeting additionally requires the real authored model aim-shape
service. Entity-center distance and the living capsule are not substitutes for
native `39CE6AA0`/`39CE6C20`. The picker supports types0 and4 with exact-current
targets; other union/target types refuse. In-range candidates retain duplicates
and native traversal order. The random index is `(count * uintRandom) >> 32`.
Flytrap suppresses out-of-range fallback; other patterns can select the nearest
too-far candidate. Executing that movement-dependent choice still requires the
unported `COPStick` owner. Optional base-skill execution also refuses.

The final eligibility check uses the real request target. For non-position skill
requests, native `390B31B0` copies `Unit.TargetId`, even when descriptor target0
ultimately means the caster. The wire therefore retains that value, frequently0,
instead of substituting the selected AI target or the caster's numeric ID.
Choosing AI attention does not write `Unit.TargetId`.

The common goal waits for the raw GCD before requesting skill information, and
again before sending a skill that does not ignore GCD. Existing cast/channel
identifiers cause one original-order `StopCasting` request sequence followed by
waiting for World to clear the fields. The independent autoattack casting byte
does not substitute for those identifiers.

The goal follows the original two-second skill-info/start timeout, bounded
60-second active cast/channel wait and authored post-use delay. The original
zero-cast/zero-channel Started branch has no end timeout, and nonchannel Fired
waits for cast identifiers rather than the autoattack byte. Active cast/channel
range monitoring uses the actual attention target and original minimum and
`maximum+7m` guard; leaving it requests StopCasting without clearing local fields.
Start/Fire failures do
not start cooldowns. A successful non-channeling Fire can finish before End;
Start+Fire+End received between AI frames retain their ordering. Registry
callbacks are synchronous and carry the actual Unit allocation and full result.

Cooldown writes are limited to the no-skill-modifier profile. Successful Started
with timing1 starts the default1000ms or positive custom GCD, using actual
attack-speed attributes74/218 and the existing stat calculator. Successful Fire
starts the actual descriptor's cooldown when timing is nonzero and no toggle buff
exists. Charges/equipped-weapon durations refuse. An unowned effect discovered
after a request is queued as an actor fault; it cannot interrupt World's field
producer halfway through Fire. Scene integration must drain these faults before
further AI execution and retire the faulted generation.

World remains authoritative for effects, plots, damage, buffs and mana. Full
phase controllers, historical skill-use/unstick policy, target acquisition,
perception, auxiliary goals, general skill requirements/modifiers and skill
controller execution remain unfinished. A passing common cast does not admit
all reachable combat scripts.

## Verification

`ManagedNpcSkillPatternTests` exercises real Unit/registry acknowledgment and
cooldown owners, request bytes, failure and unrelated-timeline handling, multiple
callbacks between AI frames, duplicate/keyed Lua inputs and delay consumption,
source child roles, signed pattern state, timeout boundaries, generation
retirement, unsupported profile refusals and asynchronous effect faults.

Scene senders tag queued requests with the actual Unit allocation and
`GetRequestScope(actor)` lease token. Disposing that lease discards only its own
unsent requests; it leaves other notification scopes intact. The same scope owns
the common goal's `StopCasting` requests.

The fixtures use controlled NPC state and real managed body ownership. They do
not claim a native combat oracle, an actual connected World cast, or gameplay
parity merely because a Lua constructor now completes.
