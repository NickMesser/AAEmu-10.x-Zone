# Native attributes and unit points

Source: CN 10.0.2.13 `x2game-dev_dedicate.dll.aaemu-original`, image base
`0x39000000`, inspected read-only in `AAEmuNativeZone`. Addresses below are virtual
addresses. Decompiled exports, temporary probes and test/build reports stay
outside the repository.

## Formula evaluation

`0x39B1B630` selects `unit_formulas` by owner type and formula kind. Core attributes
0..5 use kinds 22..27; maximum health and mana use kinds 14 and 15. Formula
coefficients belong to the particular formula ID, variable kind and content key;
using a global coefficient per NPC grade or template would produce incorrect stats.
`0x39B19F30` resolves NPC template/kind/grade keys from the NPC descriptor. Mates
add variable kind 5, keyed by their `mate_kind_id`.

The same evaluator chooses a character's secondary level/heir pair when the
secondary level is nonzero, otherwise its primary pair. Other unit kinds use
the primary pair. Although the wire reader preserves signed bytes, native formula
inputs treat these four level bytes as unsigned. `0x39D45B60` adds the selected
character stat page to the first five attributes only when no secondary level
override is active. `0x39D452E0` returns zero for an invalid page or stat index.

The evaluator dispatch table is initialized at `0x39B1DCE0`. Recovered operators:

| Behavior | Native function |
| --- | --- |
| f32 add/subtract/multiply | `0x39B1AAD0`, `0x39B1AB40`, `0x39B1ABB0` |
| Division: denominator first; zero returns zero without evaluating numerator | `0x39B1AC20` |
| `powf` | `0x39B1ACA0` |
| Clamp with ordered comparisons | `0x39B1AF80` |
| Lazy zero/positive/negative branches | `0x39B1B040`, `0x39B1B0B0`, `0x39B1B120` |
| Min/max with ordered comparisons | `0x39B1AD20`, `0x39B1ADB0` |
| Floor, base-10 logarithm, square root, absolute value | `0x39B1B190`, `0x39B1B1F0`, `0x39B1B230`, `0x39B1B270` |

The replacement evaluates arithmetic as f32 and reproduces the recovered branch,
zero-division and comparison behavior. Its parser accepts the syntax used by all
480 configured formula rows. Parser precedence is conventional arithmetic with
right-associative power; the native parser's behavior for other ambiguous input
has not been independently recovered. It bounds expression length and nesting,
rejects unknown functions. Public expression evaluation rejects non-finite final
results; native stat evaluation passes them to the corresponding x64 conversion,
including its minimum signed integer result on NaN or overflow. Parser bounds and
unknown-function rejection remain replacement validation policies.

`0x39BF7150` truncates core formula values to i32. For health and mana, it converts
the f32 result to double, adds the exact constant at `0x3A13DED8`, and converts to
i64. That constant is **0.05000000074505806**, with bits `0x3FA99999A0000000`.
It is not the usual 0.5 rounding offset.

## Modifiers and housing

`0x39BF3C60` adds static template modifiers from `Npc` for NPCs/mates, `Slave` for
slaves, and `Housing` for houses. `0x39BF3890` applies an optional linear level
bonus: multiply the level and bonus as i32, evaluate
`floor(product * 0.01f + 0.5f)`, then add the original modifier value as f32 and
convert to i64. Type 0 is additive; type 1 contributes to the percentage sum.

`0x39BF3380` adds flat contributions before applying one combined percentage.
Percentage scaling uses double arithmetic and truncates to i64. `0x39B2E220`
then clamps the result. The loader at `0x39B2EA30` initializes supported attributes
to −2,000,000,000..2,000,000,000, then applies rows from
`unit_attribute_limits`. The configured health/mana rows raise the maximum to
1,000,000,000,000. The replacement implements these defaults for all 255 defined
attributes accepted by `0x39B27730`.

Attributes 62 (`incoming_aggro_mul`) and 90 (`aggro_mul`) take the zero default
branch of `0x39BF7150`. `0x39B27730` includes both in the integer attributes that
receive the default limits. They pass through the existing template, equipment,
buff and limit visitors. `CalculateAttributes` evaluates requested supported
attributes and their dependencies using current equipment, progression and buffs;
the vital calculation uses the same path. `ZoneAggroTable` consumes those integer
modifiers through its world-service interface. NPC ownership/session integration
is implemented; production actor and scene services remain pending.

Attribute 210 (`heal_dynamic_normalizable`) uses the owner's unit formula 53.
The branch at `0x39BF7150` truncates that formula's f32 result to i32 before the
ordinary modifier and limit passes. NPC, slave, housing and other owner-specific
readers all delegate this attribute to the same branch. `0x39B27730` confirms
that it receives the default limits. The formula reads the current modified
Spirit attribute and selected level pair; the result can itself receive static,
conditional, dynamic and formula modifiers. Formula-buff reads disable formula
contributions to the requested attribute, preserving the existing recursion rules.
Unsupported attributes remain explicit and dependency cycles reject mutations
before publishing new state.

Housing health uses `0x39D463C0`, bypassing formula kind 14. A nonnegative signed
build marker uses `housings.hp`. A negative marker with a nonzero sum of
`housing_build_steps.num_actions` uses native u32 arithmetic:
`(hp / (actions + 1)) * (marker + 1 + actions)`. Division happens before
multiplication. Zero build actions leave health unchanged.

`0x39BF6510` skips equipment stat contributions for NPCs, housing, transfers and
shipyards. Characters, mates and slaves now use `ZoneEquipmentCalculator` for
weighted item attributes, static item modifiers, enchantment/gem modifiers and
activated random attributes. Slot restrictions, broken-item exclusion and character
transformation/disabled-slot state apply before these contributions. Unsupported
buff dependencies remain
explicit unresolved sources; their partial maxima cannot govern point updates.

Appellation stamp contributions use the fixed signed 32-bit stamp field, preserving
its bits as an unsigned content ID. `0x39D1C900` copies packet member `0x994` to
state offset `0x7FBC`; the packed active title is member `0x995`, copied to
`0x7FC0`. The character-only visitor at `0x39BF6510` resolves the stamp through
`0x39C53E40`. `0x39C56B80` indexes enabled `appellation_merits` descriptors, whose
loader `0x39C8B020` attaches `AppellationMerit` unit modifiers at descriptor offset
`0x38` and stores `req_app_level` at `0x2C`. The latter supplies modifier scaling;
the stat reader does not check the character's level, requirements or lifetime.
Unknown and disabled stamps contribute nothing. Transformation does not suppress
this visitor. Active titles remain distinct and their buffs are counted through
the normal buff list. Runtime stamp selection, ownership, expiry and World relay
remain unfinished; this implements the selected stamp supplied by a snapshot and
retains it through subsequent stat invalidations and reconnect replay.

Item formulas retain separate single-precision and double-precision operations,
including both weapon rounding steps. Durability truncates and wraps at the native
byte boundaries. Static modifiers use each contributing item's level. Gem traversal
stops at the first empty slot; random attributes stop at the first empty or unresolved
group, use signed experience and do not clamp the interpolation ratio. Equipment
mutations recalculate from current state and leave current HP/MP untouched.

## Gear score

`0x39B1EE40` registers `0x39CA1E70` as the gear-score callback used by buff formula
dispatch at `0x39D432E0`. It reads current equipment and the reinforcement map,
visiting slots 0..18, 26, 27, 29, 30 and 32 in ascending order. It does not apply
the ordinary equipment modifier checks for durability, slot eligibility,
activation, excluded slots or transformation. The full score and a bare score
excluding sockets accumulate in f32 and truncate to i32 only after all slots.

`0x39CA0610` accepts detail kinds 1 and 9 and item implementations 1, 2, 22 and
24. Other implementations, absent templates and missing required metadata
contribute zero. Weapons use their holdable's gear-score multiplier; armor and
accessories use the wearable-slot record. Type-9 backpack details select wearable
slot type 30. The general `formulas` rows 30, 56 and 57 calculate weapon, armor
and accessory/backpack scores. These are distinct from `unit_formulas`.

The multiplier narrows after a double-precision multiplication by 0.01. Grade
input is `item_grades.var_holdable_dps`, proven by `0x39ADE330`, `0x39B1E510` and
`0x39B1C480`. Tempering reads `enchant_scale_ratios.scale`: `0x39C9FC00` adds 1000
in f32, then the caller multiplies by 0.001f. An unknown scale ID gives zero.
The item's detail element level is an unsigned byte. Reinforcement uses the
actual equipment slot and signed-byte reinforcement level; configuration 359
selects either the direct `gain_item_level` or general formula 69 with the base
item level (`0x39CE8F90`, `0x39CE8D50`, `0x39B1A1F0`).

Enchantment and socket scores use the referenced item's signed-short stat level
in formulas 32 and 31. They visit the enchantment ID and all nine gem IDs,
continuing past empty or unknown IDs. Random modifier groups do not count as
sockets. `0x39B1C480`, `0x39B1C5A0` and `0x39B1C670` floor each contribution to
one decimal before summation. Gear score remains available through subsequent
buff and equipment invalidations; current HP/MP retains the ordinary point-update
rules. Public score calculation does not implement client score publication.

## Buff stat contributions

`ZoneBuffContent` loads buff metadata, passive definitions, conditional modifier
relations, tags, granted-passive relations and enabled curve functions from the
same read-only database. It copies all collections before closing the connection.
Tagged-buff rows retain their own IDs, raw 32-bit fields and native NULL-to-zero
conversion. Session content policy filters the runtime tag index by row ID;
template tags remain available separately for passive-suppression exceptions.
Conditional tag modifiers use the filtered runtime index. See the World content
policy evidence in [unit-snapshots.md](unit-snapshots.md).
The synthetic SQLite fixture verifies each function relation, disabled rows,
nullable conditions and content ownership after the connection closes.

`0x39D28D70` resolves learned passive IDs through `passive_buffs.buff_id`, visiting
only definitions whose `active` flag is false for normal stat calculation. Unknown
passive IDs and missing referenced buff templates are skipped. The configured
content contains four such retired definitions. The passive visitor at `0x39BF6280`
uses stack one and source level zero. Active visitor `0x39BF6160` uses the instance's
stack and **caster ability level**, not caster level. Active `one_time` buffs skip
their own stat contributions; that flag does not disable learned passive bonuses.

Both visitors apply static `unit_modifiers` owned by the buff. Conditional
`buff_unit_modifiers` at `0x39BF43A0` add their linked modifiers once for each satisfied
buff-ID condition and tag condition independently. They use stack one and level
zero. Several matching tagged instances still satisfy a tag condition once;
one-time buffs participate in tag presence.

`0x39BF39F0` evaluates linear/manual curves only when the active instance has both
nonzero duration and tick interval. Linear functions interpolate with i32 arithmetic
between start and end over `duration / tick`; negative/zero tick indices select the
start and indices at or beyond the total select the end. Manual functions clamp the
index to their value list. The instance's stored tick index is distinct from elapsed
time. Curve results multiply by stack and join the same flat/percentage aggregates.

Dynamic functions at `0x39BF6FB0` read the source attribute and convert
`f32(attribute) * value * 0.001f` to i32. The exact constant is at `0x39F9EDE0`.
Formula functions at `0x39D432E0` read attributes with formula-buff contributions
disabled. This permits self-attribute formulas without recursively adding their
own bonus. `pc_level` and `heir_level` are zero for noncharacters. The replacement
supports these inputs and the ordinary attribute paths described below, detects dependency
cycles, and reports unsupported attribute or unavailable gear-score dependencies
as unresolved.

Native formula functions are compiled by function ID. The replacement evaluates
their content expressions with the shared f32 parser. The recovered cases support
the implemented dependencies, but floating-point expression ordering has not been
proven identical for every compiled function. For example, native formula 1 sums
the first five attributes in a different order from the content expression. This
remains a precision validation gap for large values.

## Passive grants and progression

`ZoneProgressionState` keeps current skills, primary level and passive manager
state separate from the original snapshot. Native initialization at `0x39D2ABD0`
sorts and deduplicates learned passives. Initial buff callbacks run in good, bad,
hidden order (`0x3935D260`, `0x39450870`).

Grant selection at `0x39D2BD80` requires linked passive definitions and no current
grant owner. It compares each passive's required level with the owner's ability
level. Characters use expertise experience even for inactive abilities; general
ability 0 has level 1, and ability 30 has level 0 (`0x39CB7510`, `0x39CB7700`).
Other unit kinds use primary level (`0x39D1BDE0`). Experience lookup compares the
signed 32-bit value with the next `levels.total_exp` threshold, capped at level
100 (`0x39CB72F0`). Unknown passive definitions are skipped.

Normal grants append eligible passives and sort without deduplicating against
learned IDs (`0x39D2B3A0`). The saved vector records granted IDs so removal deletes
one occurrence of each. Suppression instead saves the previous current vector,
replacing it with eligible grants and exceptions whose associated buff has the
configured exception tag (`0x39D2B2B0`, `0x39D2B9A0`). The content column is spelled
`off_passive_exection_tag_id`. An off-passive flag without grant relations has no
effect; a nonempty relation claims ownership even when no linked passive qualifies.

Removal at `0x39D2BE90` checks the removed template's grant relations rather than
matching its ID to the current owner. Suppression removal restores the saved
vector; normal removal deletes its entries from current passives. Both clear the
saved vector and owner. Remaining buff instances do not acquire ownership
automatically. The replacement preserves these native behaviors.

| Message | Body | Native serializer / handler |
| --- | --- | --- |
| BuffLearned `0x007C` | target bc, passive definition u32; 7 bytes | `0x3936C7B0` / `0x39365220` |
| SkillsReset `0x007D` | target bc, ability u8; 4 bytes | `0x3936C870` / `0x39365280` |
| LevelChanged `0x007E` | target bc, level u8; 4 bytes | `0x3936C920` / `0x39364270` |
| AddSwapPassiveBuffs `0x0087` | target bc, buff template u32; 7 bytes | `0x3936C990` / `0x39365390` |

Learning uniquely inserts a known passive and invalidates stats even for an
existing ID. Ability reset removes unknown and matching-ability skills/passives
from the current vectors, leaving saved passives unchanged (`0x39CEE8E0`,
`0x39D2AD10`). AddSwap requires an existing grant owner and appends eligible IDs
absent from the current vector; it leaves the saved vector unchanged (`0x39D2B7F0`).
World relays learning and resets through the existing per-unit Zone routing and
`AAEMU_WZ_BUFF` switch.

LevelChanged reaches `0x3925A830`, which accepts levels 0..100 for characters and
mates only. It updates primary level without invalidating stat caches or clamping
points. The replacement preserves the cached stats until another mutation
invalidates them. Progression and buff mutations calculate candidate stats before
committing state; malformed bodies and calculation failures leave the unit intact.

## Buff instance updates

Native serializers and mutation functions establish the following messages:

| Message | Body | Native evidence |
| --- | --- | --- |
| BuffCreated `0x003E` | SkillCaster union, caster persistent ID i64, target bc, instance index u32, template u32, caster level u8, ability level u16, source skill u32, stack u32, PISC quartet for charge/duration/elapsed/tick | `0x3936DC70`, `0x3938F7F0`, `0x39450740` |
| BuffRemoved `0x003F` | target bc, instance index u32; 7 bytes | `0x3936C130`, `0x39450420` |
| BuffUpdated `0x0040` | target bc, instance index/stack/charge/elapsed as four 32-bit fields, reason u8; 20 bytes | `0x3936C1D0`, `0x3944F730` |

Creation's three time values use ten-millisecond wire units and wrap as u32 when
multiplied by ten. Updates carry elapsed time directly as a 32-bit pattern. UnitState
buff snapshots already carry milliseconds and an independent tick index; they must
not undergo creation's conversion.

`0x39D28760` searches the selected category by template, additionally matching the
caster persistent ID for `per_unit_creation`. Independent buffs always allocate.
The arrays at `0x3944EDE0` hold 32 good, 20 bad and 28 hidden entries. Full categories
reject additional instances but allow refresh. Unknown creation templates are
ignored (`0x39450740`), as are absent targets and update/removal instance IDs.

| Rule | Refresh behavior |
| --- | --- |
| 1, refresh | Preserve stack and charge |
| 2, charge refresh | Replace charge; preserve stack |
| 3, charge extend | Add charge and apply signed charge cap; preserve stack |
| 4, multiple | Add stack; preserve charge |
| 5, extend | Add duration, capped by maximum remaining lifetime when configured; preserve stack and charge |
| 6, independent | Append a separate instance |
| 7, multiple decrease one | Add stack; preserve charge |

Every create applies the signed stack cap. A refresh replaces the instance index,
caster union, persistent caster ID and caster levels, while keeping the original
source skill. Rule 5 takes the greater elapsed time; other refreshes take the
smaller. Tick interval is replaced and tick index is recomputed from elapsed/tick.
New instances preserve the incoming source skill and initial elapsed time.

Updates assign stack, charge and elapsed without reapplying caps. Only a changed
stack under rule 7 with reason 3 resets elapsed and tick index to zero; other
updates preserve tick index. Lookup uses the first instance in good/bad/hidden
order, including duplicate instance IDs. Removal shifts subsequent entries and
preserves order (`0x3944EEC0`).

The registry builds candidate buff state and recalculates stats before committing
either. Malformed bodies and calculation errors leave the unit untouched. Native
buff mutations invalidate stats (`0x3925A270`) without invoking point setters, so
current HP/MP may temporarily exceed a reduced maximum. Snapshot data remains
unchanged; reconnect replay creates fresh runtime instances.

This implements buff records and supported stat contributions. Native model,
physics and skill-controller callbacks, tick advancement, expiration, periodic
effects, triggers and visibility publication remain
unfinished. The executable still does not announce simulation readiness.

## Point updates and limits

WZUnitPoints (`0x0020`, serializer `0x3936BCF0`) is exactly 19 body bytes:
`objectId bc`, `preciseHealth i64`, `preciseMana i64`. Handler `0x39364C30` ignores
unknown unit IDs. The setters at `0x3925AB00` and `0x3925AC60` set a negative input
to zero; otherwise they cap it at the computed maximum multiplied by 100. Native
ordering is preserved even for a negative computed maximum. A zero-health initial
snapshot marks the unit dead (`0x3935D260`); UnitPoints does not resurrect it.

The replacement decodes the whole body and calculates both new values before
mutation. Unknown IDs are ignored after decoding. Missing content or unresolved
modifier sources cause an explicit failure with neither point value changed.
Content errors during creation leave the registry unchanged. Native stat conversions
use their x64 indefinite values, and stat/point arithmetic wraps at its native width.
The maximum-times-100 product can wrap negative; a nonnegative input then clamps
to that negative product. Resurrection passes the product through the point setter
and therefore converts a negative product to zero. The snapshot remains distinct from current
precise points, and duplicate snapshots do not reset those points.

This is not complete unit simulation. Physics state, health-dependent model data,
buff execution, death/resurrection actor callbacks, client publication and remaining
stat-source changes still require implementation. World snapshots now carry an
explicit secondary level pair, defaulting to zero, and four signed equipment
exclusions, defaulting to -1. Normal characters therefore retain their equipment
and selected stat-page contributions; transformation and exclusions are opt-in state.

## Validation

Portable tests cover every supported owner kind, exact point bytes and World
encoding, all truncated point bodies, atomic failures, duplicate/remove behavior,
fragmented TCP dispatch, formula precision and branches, coefficient relationships,
modifier order, limits, transformed characters and housing build markers. SQLite
fixture tests hash the source before and after loading and verify that the loaded
content remains usable after closing the connection.

A separate read-only probe used the configured decrypted `game.compact` export
and evaluated 121,116 template/level scenarios without unresolved inputs or errors.
It covered every NPC at six level/heir pairs, mate templates with a nonzero mate
key, every house at two build markers, and every slave, transfer and shipyard.
All 480 formula rows parsed. The source SHA-256 remained
`6EBC25AC6D860268D5E28454E3971B1FD8E69E3DD0D98CFBFB4E93C570C3D851`.
The extended audit also loaded 30,654 buff templates and 279 passive definitions.
It evaluated each buff for a character and an NPC at three tick indices (183,924
scenarios), and each passive for both unit types (558 scenarios), without
calculation errors. Explicit partial results remain: 12 scenarios with attribute
210 dependencies and 12 with gear-score inputs. Granted-passive scenarios now
resolve through the runtime passive manager. The
same source hash remained unchanged. These are isolated buff scenarios; they do
not cover every combination of simultaneous buffs, gear or runtime effects.

Portable coverage additionally checks all seven stacking rules, all category
capacities, caster identity, signed caps, golden wire bytes, World compatibility,
every truncated buff packet prefix, conditional and curve stats, atomic failures,
initial snapshot replay and fragmented TCP create/update/remove/reconnect sequences.
Progression coverage checks all four packet layouts, ability experience thresholds,
duplicate grants, suppression exceptions, restoration after learning/reset,
grant-owner rules, category capacity, cached level changes, atomic failures and
fragmented TCP progression/reconnect sequences.

This validates content compatibility and invariants; it is not a differential
comparison of every result against a running native Zone or a client gameplay test.

The equipment audit loaded 51,010 item templates, including 19,497 weapon, armor
and accessory definitions. Across all 13 grades, 253,461 base-attribute/durability
calculations matched an independent transcription of the recovered formulas, and
251,901 eligible equipped-character scenarios completed without calculation errors
or unresolved inputs. The source hash above remained unchanged. Portable tests
cover rounding, durability, item/socket ownership, signed random experience,
activation, slot exclusions, transformation, snapshot/update/removal and atomic
failure behavior. Slave equipment eligibility and reinforcement are outside that audit.

The subsequent equipment audit covered 318 slave item templates against 1,753
resolved vehicle/slot/kind relations. It checked 556,818 combinations and calculated
121,199 valid item/vehicle/grade scenarios without errors or unresolved inputs.
One slave item has no equipment definition and therefore contributes no modifiers,
matching native eligibility. The audit also loaded 124 reinforcement definitions
and 179 selected modifiers and evaluated 23,449 reinforcement scenarios, including
signed-byte level boundaries. The content source hash remained unchanged.

Slave equipment uses the item's minimum/maximum equip levels and the vehicle's
permitted kind for the actual slot. Its stat modifier level remains the item's
separate stat level. Reinforcement thresholds use fixed offense/defense/support
slot groups, a minimum contribution of one per slot, and signed-byte accumulation.
The configured bundle mode moves slot 18 from offense to support. Selected effects
resolve the stored modifier ID directly; slot/trigger metadata does not filter an
already selected modifier. Transformation suppresses character equipment and
reinforcement contributions. Tests cover both threshold modes, item-level bonuses,
duplicate selection keys, missing references, overflow, equipment mutation and
reconnect ownership. Runtime reinforcement progression and World persistence are
separate systems still to be implemented.

The appellation audit loaded 109 enabled merits from 114 definitions, checked
6,384 stamp scenarios across all seven unit kinds and normal/transformed level
pairs, and evaluated all 1,098 active title IDs without errors or unresolved
appellation inputs. The configured export contains no `AppellationMerit` unit
modifier rows, so synthetic tests independently exercise nonzero bonuses and
required-level scaling. They also cover separate title/stamp wire fields,
unsigned stamp IDs, unknown/disabled definitions, non-character exclusions,
buff invalidation, duplicate snapshots, removal/reconnect, point-update guards
and atomic calculation failures. The source hash remained unchanged.

The subsequent gear-score audit covered all 51,010 item templates at all 13 grades,
plus tempering, element and socket variations. All 834,445 item-score cases and
12,400 reinforcement cases matched a separate transcription of the recovered
formulas, including explicit f32 operations and native `powf`. This is an
independent formula comparison, not execution against a running native Zone.
The complete stat/buff/passive audit still has no calculation errors; only its
12 attribute 210 scenarios remain partial. Gear-score dependencies now resolve.
The configured content hash remained unchanged. Portable tests additionally
cover per-item floor versus final truncation, all slot and item-detail gates,
transformation, broken equipment, bare score, both reinforcement modes, current
equipment and buff updates, atomic failures, point retention and reconnect replay.

After implementing healing normalization, the complete audit evaluated 121,116
template/level cases, 183,924 buff cases, 558 passive cases, 6,384 stamp cases and
1,098 title cases with no errors or unresolved inputs. The content hash remained
unchanged. Healing regression tests cover every owner, truncation before modifier
scaling and limits, formula-buff recursion, Spirit from current equipment/buffs,
transformation, point retention, reconnect state and atomic cycle rejection. These
audits cover the specified scenarios; they do not prove every possible combination
of simultaneous buffs or completed healing/combat execution.

## Ordinary attribute dispatch

The shared getter `0x39BF6C20` dispatches by owner kind before applying modifiers:
NPCs use `0x39D44A60`, slaves use `0x39D46070`, houses use `0x39D46470`, and the
remaining supported kinds use `0x39D45BE0`. Their fallback is `0x39BF7150`.
`ZoneBaseAttribute` now records the implemented branches explicitly, including:

- All shared direct formula branches, including health/mana regeneration,
  critical bonuses, DPS increases, casting-time formulas, heir-level
  attributes and all six normalization attributes 207..212.
- All fixed defaults, including zero, 100 and attribute 91's value of 60000.
  Attributes 140/141 start at 100 in the slave/general readers, but at zero in
  the NPC/house fallback. A defined attribute is not assumed to have a zero base
  when its native branch calls a formula or equipment helper.
- Facet-adjusted formulas for melee/ranged/spell critical and hit attributes,
  healing critical, dodge, and slave mass/steering/reverse-velocity attributes.
  `0x39D354C0` truncates its formula to i32 and reads attributes 13 and the branch's
  multiplier attribute. Both dependency results narrow to signed i32 before the
  f32 product `multiplier * facets * -0.001f`; that product truncates to i32 and is
  subtracted with wrapping arithmetic. The exact negative constant is at
  `0x39FC2A60`. The resulting attribute then receives its own modifiers and limits.
- Spell-damage critical aliases 151/152 read finished attributes 86/31. NPC
  spell/healing power 87/173 read finished attributes 35/175; NPC armor and
  resistance use formula kinds 18/19 and their formula-specific coefficients.
- General/slave mitigation branches 142/144/146 use `0x39B1BF00`, `0x39B1BF60`
  and `0x39B1BFC0`. They read current armor or resistance into the formula
  environment even when the selected formula is constant. Those equipment
  dependencies now use the defense helpers described below. NPCs and
  houses use the shared zero base instead.

Aliases and facet dependencies use the ordinary getter, including current GM
overrides, equipment, progression, buffs and limits. Formula-buff exclusion applies
to the requested attribute; native base-helper dependency reads still enable those
contributions. Every base result narrows to signed i32 before the final combiner,
including mitigation's preceding i64 conversion. All 255 defined native attributes
have base implementations. Undefined slots remain unsupported; missing required
content remains an explicit partial source or a calculation error.

Independent validation ran the original dispatchers and shared getter in isolated
memory for all 255 defined attributes, all seven unit kinds and six combinations
of formula inputs, flat/percentage modifiers and raw dependency overrides. Formula
results and modifier caches were controlled inputs; native dispatch, conversions,
aliases, facet helpers and the combiner executed unchanged. After integrating the
defensive predicates, all 10,710 cases matched complete managed results, with no
unsupported queries, partial dependencies or replacement stubs for unimplemented helpers.
Without overrides, the covered base paths are:

| Unit kind | Complete attribute paths |
| --- | ---: |
| All seven supported unit kinds | 255 each |

The expanded content probe also exposed an older coefficient initialization
difference. `0x39B1CE30` clears the coefficient arrays, then writes NaN into the
required enumerated keys before loading rows. Variable kinds 2..5 start enumeration
at key 1, leaving key 0 at zero. Character-race coefficients include key 0 in their
required set. The replacement now retains that zero default for existing formulas;
explicit rows take precedence and missing required keys still fail. An independent
execution of the original initializer verified all 240 zero-key slots across the
60 configured formula kinds and the required character zero keys.

A read-only content check used 129 synthetic empty-equipment contexts with IDs
resolved from the configured catalogs, including NPC descriptors with mate key 0.
After the defensive helper integration it completed 32,895 attribute queries,
with no unsupported queries, partial queries or calculation failures. This checks
configured content compatibility, not native equality for every content formula.
All 351 portable tests passed, including current-core dependencies, modifier order,
owner-specific constants, NPC coefficients, aliases, mitigation prerequisites,
native integer narrowing, missing-key behavior and buff-driven point limits.
The configured content source retained its recorded SHA-256. All probes and build
artifacts remain external; no live server, client or player state was used.

## Equipment defense and power

Attributes 8, 64, 87 and 173 now include the native equipment contributions from
`0x39D45590`, `0x39D45330`, `0x39D457D0` and `0x39D45980`. NPCs keep their own
defense formulas and power aliases; houses keep their shared zero bases. Slave
armor has no formula-18 contribution, while slave resistance adds formula 19.
Characters, mates, transfers and shipyards add the ordinary defense formulas.
Transformed characters suppress equipment contributions; their power branches
return the finished 35/175 dependency directly, without an intermediate f32 conversion.

`ZoneEquipmentCombatContent` loads both wearable formulas, all five inline
holdable formulas and the seven separate `item_grades` coefficients selected by
`0x39B1B8D0`/`0x39B1E240`. `0x39B2D950`/`0x39B2DAC0` populate the zero-initialized
6-by-34 matrices from `wearables.armor_bp` and `magic_resistance_bp`. These are
distinct from wearable coverage and durability ratios. Missing/zero matrix entries
skip the formula and grade lookup. Eligible wearable results use the exact f32
basis-point multiplier `0.0001f`, then truncate to i32.

The defense visitors check wearables in slots 0..14 and holdables in slots 15..18.
They read current durability directly, independently of maximum durability and
random-attribute activation flags. Character exclusion selectors suppress their
slots. Armor sums truncated i32 contributions with wrapping arithmetic. Resistance
converts the running total to f32 and truncates again after each holdable addition;
even a broken holdable's zero can round a large preceding total. Spell power accepts
holdables throughout slots 15..18. Healing additionally requires the holdable's
main or auxiliary slot mapping (`0x39C9F340`). Both sum in f32, multiply by 1000,
add the finished 35/175 dependency in f32, then truncate to i32 before receiving
their own attribute modifiers and limits.

The weapon-descriptor index (`0x39AC3010`) is independent of the item's primary
implementation. The configured database contains one non-Weapon item linked to a
holdable, so `ZoneEquipmentContent.WeaponHoldables` preserves all descriptor links.
Spell/healing visitors suppress tempering for these non-Weapon implementations;
defense visitors retain it. Regression fixtures use a nonzero formula to exercise
this distinction independently of the configured outlier's zero-valued holdable.

Reinforcement uses the exact actual-slot and signed-byte level match in
`0x39CE8F90`/`0x39CE8D50`. Configuration 359 selects general formula 69 before the
bonus is added to the signed item level. Tempering reuses the existing immutable
scale catalog: `0x39C9FC00` returns `scale + 1000f` for a known rune and zero for an
absent rune. Combat helpers apply `value * tempering * 0.001f` only when tempering
is positive. Unlike gear scoring, a missing rune therefore leaves the untempered
combat value intact. Opaque item details retain their native durability byte at
Details+5; their fixed payloads cannot reach the rune field at Details+0x3C.

An independent isolated execution of the original four unit helpers compared
2,880 results across 720 synthetic equipment states. The native item formula
environments, grade selection, matrix access, tempering, reinforcement lookup,
slot selectors, transformation and numeric conversions ran unchanged. Catalog
lookups and arithmetic expression evaluation used controlled inputs; this does
not claim native equality for every configured expression. All results matched,
including signed levels, missing templates, broken items, opaque details, invalid
slots, large dependency overrides and overflow. The configured read-only catalog
then loaded 32 holdables, 76 wearable matrix entries and 13 grades; 72,384 eligible
item/grade contexts completed without exceptions or unresolved sources.

Portable tests cover owner dispatch, grade/formula mapping, both reinforcement
modes, tempering boundaries, durability and activation rules, raw overrides,
equipment changes, buff-dependent health, atomic failure, removal and reconnect.
No live server or player state was involved, and the configured database retained
its recorded SHA-256.

## Weapon DPS, damage bounds and attack intervals

Attributes 36..38, 41..43 and 46..48 now resolve main-hand, off-hand and ranged
attack intervals and damage bounds. The shared DPS branches 96..98 call
`0x39D35E40` for all seven unit kinds. NPCs use the specialized damage/interval
branches in `0x39D44A60`; slave and housing damage/interval bases remain zero.
Characters, mates, transfers and shipyards use `0x39D451A0` and `0x39D44FF0`.

The independent default-holdable globals at `0x3D8FBCE8`, `0x3D8FBD30` and
`0x3D8FBD38`, and the default grade byte at `0x3D8FBCEC`, lie in the PE's
zero-initialized data region. Their native read sites select holdable 0 and grade 0.
The configured holdable 0 has speed 1500 ms, slot type 17, damage scale 1 and a
zero physical-DPS formula. A missing default descriptor returns zero according to
each caller's native path; a missing combat catalog remains an explicit unresolved source.

Physical DPS uses holdable formula kind 0 and `var_holdable_dps`, then applies
reinforcement, tempering, f32 multiplication by 1000 and i32 truncation. An empty
main hand uses `0x39D35A90`, evaluating the default holdable at the unit's current
level and default grade without reinforcement or tempering. Empty off-hand/ranged
slots return zero. A nonempty slot must first pass current durability, character
exclusions, item lookup and weapon-descriptor lookup. Only after those checks does
a transformed character replace its equipped DPS with the default holdable at its
secondary level. This order preserves zero DPS for broken/excluded transformed gear.

Attack intervals deliberately ignore durability and character slot exclusions.
General callers resolve the independent weapon index, falling back to the default
holdable on a failed lookup or transformation. `0x39BF6DB0` reads melee/ranged speed
modifier 54/55 and universal modifier 218 through the ordinary getter, adds their
low i32 values with wrapping, and reapplies the primary modifier's limits. General
two-handed holdables (slot type 16) then add attribute 75 with i32 wrapping. The
divisor is `max(adjustment, -999) + 1000`, also in i32. General off-hand intervals
double the holdable speed before the wrapped `speed * 1000` numerator; they divide
in i64 and return the low i32 result. NPC intervals always use the default holdable,
do not double the off hand or add two-handed modifiers, and divide in i32.

General damage reads finished DPS 96/97/98 and increase 33/34 before resolving the
weapon. Unlike interval lookup, this path additionally requires the main item
descriptor. The native f32 calculation is
`uint(speed) * (float(dps) * .001f + float(increase) * .001f) * .001f`.
`0x39C9F3B0` computes `float(damage_scale) * damage * .01f`, subtracts/adds that
spread, and the caller truncates to i32. NPC damage instead uses only increase
33/34, signed default-holdable speed and fixed multipliers `.9f`/`1.1f`; the NPC
off-hand result additionally multiplies by `.5f`. Each resulting attribute receives
its own modifiers and limits. Raw overrides bypass its calculation, while dependency
overrides feed the native f32 or low-i32 conversions at their respective call sites.

An isolated original-binary audit matched all 8,400 results across 700 synthetic
contexts and all seven owner kinds. Original dispatchers, DPS/interval/damage
helpers, grade selection, reinforcement, tempering, combined-speed wrapping and
numeric conversions executed unchanged. Catalog lookup, expression evaluation,
dependency values and primary limit bounds were controlled inputs. Cases included
absent/default/orphan descriptors, non-Weapon descriptor links, broken/opaque
details, unsigned levels, transformations, signed speed/spread extremes, raw i64
dependencies and both reinforcement modes. A configured read-only content audit
then completed all twelve attributes in 82,152 weapon/grade/slot contexts with no
exceptions or unresolved sources.

Portable regressions cover the distinct owner and slot rules, missing descriptors,
reinforcement/grade/tempering, re-clamping combined speed, two-handed bonuses,
raw overrides, equipment-dependent buff health, mutation, removal and reconnect.
These calculations do not implement weapon swings, skill execution or hit resolution.

## Armor classification, parry and block

The shared fallback implements these five attributes for every supported owner:

| Attribute | Native helper | Base calculation |
| --- | --- | --- |
| 22 `melee_parry` | `0x39D35850` | Qualified formula 5, facet penalty 81 |
| 69 `armor_type` | `0x39D35F70` | Equipment majority or transformation buff |
| 70 `armor_type_coverage` | `0x39D36130` | Full or half configured armor coverage |
| 153 `ranged_parry` | `0x39D35970` | Qualified formula 5, facet penalty 81 |
| 177 `block` | `0x39D35A00` | Qualified formula 45, facet penalty 179 |

Melee parry requires a main-hand entry in the independent weapon descriptor index
and nonzero current durability; it does not require a main item or holdable descriptor.
Block requires an off-hand weapon whose holdable has slot type 20, plus durability.
Transformation bypasses these descriptor/durability gates. Both paths still honor
the character's excluded main-hand/off-hand slot respectively. Equipment activation
flags do not affect these predicates. A failed predicate gives a zero base, which
still receives the attribute's ordinary modifiers and limits.

Ranged parry first calls `0x39CA0EB0` to select a two-handed main hand (holdable
slot type 16), or the off hand otherwise. `0x39CA41F0` requires a weapon, holdable
and main item descriptor, then evaluates physical damage with the signed template
level, current grade and tempering. Slot reinforcement does not participate.
The value must be positive and current durability nonzero. Transformation bypasses
those tests but retains the selected slot's exclusion. `0x39D299C0` then requires
passive ID 244 in the current vector, and `0x39D2A450` requires active buff ID 4270.
The configured passive 244 references buff 7544; that passive buff does not replace
the separate 4270 requirement. The generic helper accepts a null buff interface,
but ServerUnit owns a ZoneBuffMan and binds it through `0x39BF4000`; reconstructed
unit calculations always use their current buff collection.

Armor classification considers actual slots 0 and 2..7 only (`0x39C9EF30`). All
seven require nonzero current durability (`0x39CACE30`) and honor character slot
exclusions. It resolves independent armor/accessory indexes (`0x39ABF900` and
`0x39AC2FA0`), with armor taking precedence, regardless of the main item's
implementation or existence. The descriptor's nonzero slot type must occur in
`wearable_slots.slot_type_id`. Four matching pieces select the armor type from
the six native counters. Transformed characters instead test active buff IDs
16551, 16552 and 16553 in that priority order, selecting type 1, 2 or 3. These IDs
come from native globals `0x3A5A3E44`..`0x3A5A3E4C` and exist in the configured
buff catalog. `0x39450C50` searches all good, bad and hidden instances by template
ID; one-time buffs still count as present.

Coverage reads the finished attribute 69, including modifiers, limits and raw
overrides, narrowed to i32. Only types 1..5 qualify. It counts matching equipment
even during transformation and does not reject descriptor slot type zero when
that slot type exists in the content. Seven matching pieces yield full coverage;
any smaller count, including zero, yields half. Loader `0x39B2DCE0` sums coverage
for descriptor slot types 1 and 3..8 with i32 wrapping. The getter halves that
total with an unsigned right shift, then the ordinary combiner narrows it to i32.
The configured content contains 12,652 armor/accessory classifications and a
full coverage total of 90.

An isolated audit matched all 5,040 original getter results across 1,008 synthetic
contexts and all seven owners. Native dispatch, defensive predicates, passive
binary search, active buff hash/list searches, wearable iteration, static slot-set
initialization, grade selection, tempering and numeric modifier arithmetic executed
unchanged. Catalog rows, formula results, modifier caches and coverage totals were
controlled inputs; process teardown registration was suppressed. Cases included
conflicting/orphan descriptors, signed template levels, broken/opaque details,
transformations, selectors, buff combinations, raw dependency overrides and overflow.
The configured read-only content probe completed 40,036 armor and weapon contexts
with no exceptions or unresolved sources. Portable regressions also cover passive
learning/reset, buff creation/removal, equipment changes, GM overrides and reconnect.

Production combat/movement consumers remain unfinished. Attribute path coverage
does not establish complete stats for every equipment/buff combination or full
gameplay parity.
