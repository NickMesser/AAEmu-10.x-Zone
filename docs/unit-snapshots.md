# Native unit snapshot evidence

Source: CN 10.0.2.13 `x2game-dev_dedicate.dll.aaemu-original`, image base
`0x39000000`, analyzed read-only in the existing `AAEmuNativeZone` Ghidra project.
Addresses below are native virtual addresses. Raw exports and build/test results
remain outside the repository.

## Wire ownership

WZUnitState (`0x0007`) contains the complete shared UnitState body, followed by
good/bad/hidden buff lists and a conditional action state. The registered handler
`0x3936B560` calls `0x3936B3C0` only after decoding. That function ignores duplicate
object IDs; a second snapshot does not refresh an existing unit.

| Block | Native serializer | Encoding |
| --- | --- | --- |
| WZ envelope | `0x39394CC0` | Shared body, three buff lists, i32 action; nonzero action appends u32 time, u32 duration, two f32 velocity limits |
| Shared body | `0x393932E0` | Header, identity, placement, equipment, appearance, relations, gameplay state, character-only tail |
| Identity | `0x39387CA0` | u8 discriminator, exact type-dependent persistent IDs, bc IDs, timelines, templates and owner fields; housing build count is u16 |
| Packed integer group | `0x3937E2D0` | Up to four u32 values; two bits per value encode byte width minus one, little endian; empty groups emit nothing |
| Position | `0x39B11410` | u32 X, u32 Y, 22-bit Z, two XY sign bits; XY shift into signed 64-bit fixed point, Z uses native f32 arithmetic |
| Equipment | `0x3938AC90` | u64 valid flags over 34 slots; body-image slots 19..25 are template-only except for slaves; NPC compact/full distinction; character trailing u64 flags |
| Full/compact items | `0x39387A80`, `0x39387C20` | Zero template omits all following item fields. Full items include detail union and every timestamp/header field |
| Item detail union | `0x393876A0` | Equipment has structured fields, one PISC quartet and fourteen further PISC values; other known types are fixed-size opaque data |
| Appearance | `0x393881E0`, `0x39388120` | Cumulative extension gate, hair/body/face, separate 6-value and 3-value PISC groups, u16-length modifiers capped at 128 bytes |
| Attachment/bonding | `0x39388F90`, `0x39CE59A0` | Signed point; -1 omits the tail; bonding otherwise includes bc, space i32, spot i32, kind u32 |
| Posture | `0x39387550`, `0x3937E490`, `0x39387480` | Type and looted flag; only house/actor/farmfield/turret append fields |
| Expertise | `0x39CB8150` | 29 records of u32 experience/i8 order, then byte count and active IDs |
| Character visuals | `0x3937E700` | Six STP bytes, packed flags byte, cosplay visual byte; this is distinct from the incremental visual-option packet |
| Character stat pages | `0x39390F20` | i32 count; five i32 stats and two u32 application counters per page; then selected page and extension fields |
| Reinforcement maps | `0x39390BA0`, `0x393916B0` | Slot i32/level u8/experience i32 and slot u8/level i8/effect u32 respectively; first duplicate map key wins |
| Buff lists | `0x3938FE70`, `0x3938FA00` | Capacities 32/20/28; full caster union, persistent source/levels, two PISC quartets per buff |
| Buff caster | `0x3938E850` | Unit/doodad/item/mount/gimmick variants; item and mount have distinct conditional tails |
| Action detail | `0x3937E8B0` | Duration and horizontal/vertical limits after the envelope's action/time |

Native coordinate constants at `0x3A107B1C`, `0x3A107B4C`, `0x3A107B50`,
`0x3A107B54`, `0x3A107B58` are respectively `1/4194304`, `4194304`, `4096`,
`-100`, and `4196`. The decoder retains quantized inputs and implements the signed
fixed-point shift, including overflow, rather than using rounded display coordinates.

The World equipment writer now excludes zero-template wrappers from valid flags,
matching the native template-ID test. Its compact item path also omits the zero
template tail. No real content IDs are introduced by this correction.

## State and current limits

`ZoneUnitRegistry` inserts only fully decoded bodies. Missing removes are no-ops,
matching lookup behavior in `0x39364A80`; removing a record permits object-ID reuse.
This implements snapshot ownership, not the whole native unit constructor.
`0x39360590` and `0x3935D260` additionally initialize derived stats, buff processing,
model/physics state and spatial placement. Those systems remain unfinished.

`ZoneSession` binds startup and runtime processing to one caller-owned stream.
Its receive loop runs once and closes permanently on EOF, cancellation or failure.
It handles UnitState, UnitRemoved, UnitPoints, equipment and activation changes,
BuffCreated/Updated/Removed, BuffLearned, SkillsReset, LevelChanged,
AddSwapPassiveBuffs, death/resurrection/combat/force-attack/target/duel changes,
faction lifecycle messages `0x15..0x1C` and the supported clock
snapshots; other opcodes fail explicitly. It never sends ZoneLoaded. When stat
content is supplied, creation resolves templates and calculates supported vital stats.
UnitPoints requires complete supported modifier inputs and clamps precise HP/MP
to computed maxima multiplied by 100. See [stat evidence](unit-stats.md).

The replacement applies explicit validation policies beyond native unchecked
memory access: bounded frame and collection allocations, rejection of unsupported
identity/item/caster discriminators, invalid active expertise IDs, oversized face
data, and buff counts beyond capacity. Native buff reads clamp the count; the
replacement rejects such malformed input rather than accepting a shortened body.
The registry's default 100,000-unit capacity is a replacement resource limit, not
a recovered native maximum. Reserved identity kind 7 is decoded structurally;
its gameplay meaning has not been established.

## NPC spawn handshake

`0x3938E0E0` is shared by ZWSpawnNpc and WZNpcSpawnFailed (`0x3936D8C0`).
Its complete body is spawner ID/type u32, member/part u8, table u16, NPC template
u32, group type/id u32 and member u8, position xyz f32, yaw f32, scale f32,
creator UnitIdentity, name string (1024-byte limit), master name string (128-byte
limit), faction u32, spawn reason union, despawn-on-creator-death bool,
use-summoner-aggro-target bool, lifetime f32 and faction-permission bool. Optional
serialization groups are always present on this binary path. Position is local
to the Zone. World allocates the client object ID after validating the request.

The familiar 78-byte body is only the empty Character-creator, empty-name,
default-reason case. World previously required exactly that size and ignored its
tail. It now uses the complete shared decoder. Non-finite position, rotation,
scale or timing values and oversized/truncated/trailing bodies are rejected as an
explicit replacement input policy. Nonpositive finite scale retains World's
existing mirror fallback of one; the decoded request retains its raw value.

`0x3938DE50` reads the spawn reason discriminator as an unsigned byte. Reason 1
adds u32/u16 through `0x39387F80`; 2, 4 and 9 add u32; 3 and 6 add CastAction.
Other values have no additional payload. CastAction (`0x3938DAF0`) adds:

| Kind | Payload |
| --- | --- |
| 0 | skill u32, timeline u16 |
| 1 | plot u32, timeline u16, event u32, skill u32 |
| 2 | buff template u32, target bc, buff index u32, two bools |
| 3 | primary ID u32, buff template u32, target bc |
| 4 | primary ID u32, target bc |
| Other | Empty |

WZNpcState (`0x393700B0`) contains spawner ID u32, member/part u8, table u16,
creator identity, despawn/aggro bools, lifetime f32, spawn reason, the shared
UnitState body (`0x393932E0`), all three buff lists (`0x3938FE70`), spawning effect
time f32 and group (`0x3938E040`). It has **no movement action tail**. The shared
reader consumes exactly the unit/buff section and leaves effect/group decoding to
the NPC envelope. World now carries the original request's creator, reason,
lifetime and flags into this acknowledgement instead of replacing them with
empty defaults. The existing default-reason and World-authored summon producers
remain available.

WZNpcSpawnerEvent wraps `0x3938DF30`: spawner ID u32, event u8, spawn reason,
creator identity, context type i32, despawn/aggro bools and lifetime f32. It uses
the same union codecs. Unknown reason/action discriminators retain their native
empty-payload behavior; unsupported creator identities are rejected.

World's per-connection registry records UnitState, NpcSpawn or NpcState explicitly.
Schedule checks, replay, spawner updates and removal use that type instead of
guessing from raw bytes. Malformed spawn requests do not allocate an ID. NPC
removal cannot consume a player entry, and delayed removal captures the exact
registration object. Replacement of that registration, including reuse of the
same body byte array, makes the old removal a no-op. Disconnect cleanup retires
both NPC message types while leaving player ID release to the player lifecycle.

The native runtime path remains to be implemented: `0x39362D40` first checks level
loading, then `0x39348440` resolves a nonzero spawner and NPC descriptor, resolves
the creator's owner to its current unit object ID, creates the ServerUnit and NPC,
and initializes the actor, AI and aggro table through `0x39346960`. The creator
result is an object ID, not a faction ID. NPC identity object ID and outer unit
object ID are distinct fields. The native constructor `0x39345000` sends a delayed
ZWRemoveNpc immediately when lifetime exceeds 0.001 seconds. Managed owner creation
and lifetime notification are implemented; production spawner readiness, actors
and AI remain pending in the replacement.
The spawn request's names and faction/permission fields are preserved by the
decoder and replay body; applying those overrides to World mirrors remains
pending. No NPC simulation readiness is inferred from these wire contracts.

Tests cover independent golden bodies, every creator and reason/action branch,
World packet equivalence, nonempty NPC buff lists, effect/group alignment,
all truncated prefixes, invalid tail values, typed ownership and delayed removal
after registration replacement. These fixtures do not execute native NPC AI.

## Combat lifecycle and placement

The registration function `0x3936F740` binds these messages to the listed handlers:

| Message | Body | Serializer / handler |
| --- | --- | --- |
| UnitDeath `0x21` | object bc | `0x3936BD80` / `0x393633F0` |
| UnitResurrection `0x22` | object bc, fixed X i64, fixed Y i64, Z f32, yaw f32 | `0x3936BDD0`, `0x39B113A0` / `0x39369A60` |
| ForceAttackSet `0x24` | object bc, enabled bool | `0x39366F90` / `0x39364CC0` |
| CombatEngaged `0x25` | object bc | `0x3936C310` / `0x393637E0` |
| CombatCleared `0x26` | object bc | `0x3936C360` / `0x39364E70` |
| UnitDuelState `0x29` | object bc, duel object bc, unsigned team u8 | `0x39367550` / `0x39364EB0` |
| TargetChanged `0x2A` | object bc, target bc, force-by-World bool | `0x3936BEC0` / `0x3936E170` |

Missing units are ignored after full decode. Death invokes `0x3935D490`, setting
the dead flag through `0x3925B2C0`, clearing combat through `0x39258800`, clearing
snapshot flags 1/10 and invalidating stats. It does not set HP/MP or remove buffs.
It clears the selected target for non-characters only. Repeated notifications
still perform these operations. Zero HP from UnitPoints alone does not mark a
unit dead; positive HP alone does not resurrect it.

Resurrection invokes `0x3935D970`; its helper `0x3925E8E0` recalculates and refills
HP/MP to their maxima multiplied by 100, clears the target and flags 1/10, and then
`0x3925B2C0` clears the dead flag. It preserves equipment, buffs, force-attack,
attack-faction flags and the combat flag. It accepts already-living units too.
The replacement calculates all required stats and placement before publishing a
mutation; missing/incomplete stats or calculation failures leave state untouched.
This is an explicit replacement validation policy.

`0x392587B0` and `0x39258800` set/clear combat at ServerUnit+`0x8005`;
`0x39364CC0` writes force-attack at `0x8007`. Snapshot flags 0/3 initialize these
fields through `0x393932E0` and `0x39D1C900`. CombatCleared additionally zeroes the
eight attack-faction booleans at `0x80BC`; the death path does not do that extra
clear. TargetChanged stores the target ID even if it is absent locally.

The character-only bc after expertise is the duel object ID, not a faction ID:
snapshot member `0x9A6` maps to ServerUnit+`0x7FE8`, and `0x9A7` maps to duel team
at `0x7FEC`. UnitDuelState writes exactly those fields. World now writes its current
duel object ID there and includes combat/force-attack flags in all WZ snapshots.
The packed `0x9A8` field is the separate expedition-war opponent at `0x7FF0`.

Resurrection positions use signed fixed X/Y at a scale of `2^44` and full f32 Z,
preserving the received fixed values separately from the initial quantized
snapshot. `0x39369A60` converts position relative to the Zone origin through
`0x39B10E00` and forms quaternion `(0,0,sin(yaw/2),cos(yaw/2))` before actor
placement. The replacement stores current world placement; terrain/collision and
actor placement remain pending. Non-finite resurrection Z/yaw are rejected.

Snapshot heading bytes encode an axis-angle vector. `0x3938AFC0` calls
`0x3937F4A0`, dividing each signed component by 127. Squared lengths below
`0.00006200012285262346f` or above `0.9999380111694336f` produce identity. Otherwise
the normalized axis is multiplied by `sin(length*2*pi/2)` and W is the cosine,
with the native float/double conversion boundaries. Housing carries full yaw.
The shared encoder follows `0x393753C0`: arithmetic-clamp quaternion W to [-1,1],
derive the axis and angle, normalize the axis (fallback +X below the native
epsilon), scale by angle/(2*pi), then apply `floor(component*127 + .5)`.
World now uses this encoder for UnitState. Its earlier Euler-byte writer produced
incorrect orientations when roll, pitch and yaw were combined. Movement packets
retain their distinct rotation encoding.

These state transitions do not implement corpse/respawn scheduling, physical
actors or script callbacks. With NPC ownership enabled, forced clearing of an NPC
target removes the previous target from its threat table (`0x393210B0`, reason 6).
Death detaches threat sharing before clearing that NPC's table. The replacement
still cannot announce ZoneLoaded or serve gameplay.

## NPC threat storage and selection

`ZoneAggroTable` implements the table component independently of actor services;
`ZoneNpcSimulation` connects it to NPC owners and the session. `0x3931F040` creates 64-byte entries, rejects the
owner and group members, and applies target eligibility through `0x39261710`.
It evicts the last entry at a capacity of 100, registers reverse NPC membership,
emits hostility, then notifies observers and group members. The first entry has
flags `0x87`: initializer `0x39F6BE90` combines the top-target bit `0x80` and the
three component-leader bits. The uninitialized binary storage is not that default.

`0x39321340` scales each unsigned damage/healing/direct amount by the target's
outgoing modifier using f32, truncates to i64 and retains the low 32 bits before
addition. Ranking scales each stored component by the incoming multiplier before
summing; `0x39322950` instead sums first, scales, and decays after 1,000 ms measured
from creation. A zero addition updates an existing timestamp without marking it
dirty. Newly created entries below the score threshold are removed immediately.

`0x3931EB70` first considers the current target's challengers in vector order.
A newer unsigned taunt timestamp wins; without a current taunt, a challenger must
exceed the distance-dependent switching threshold. It promotes only the first
qualifying challenger, then sorts the remaining entries by weighted threat.
Healing and direct leaders use a 1.1 threshold; damage uses the candidate's
distance ratio. Positive component ranks exclude flagged leaders. Native sorting
uses stable insertion for up to 32 entries and median partitioning above that
size, so equal-key order must not be replaced by an arbitrary library sort.

`0x393210B0` and `0x393200D0` block further additions after the last removal;
`0x3931F790` releases that block after updating selection. `0x393204B0` saves a
sleeping target's original components once and replaces them with `(0,0,1)`.
Waking adds the saved components to the current amounts without restoring flags.
`0x39320E60` replaces selected components with raw i32 bits. `0x39321A10` copies
through the add path, reapplies outgoing modifiers, and restores creation metadata
for only the first overlapping old target. Empty-source copy is a no-op and
self-copy empties the destination. Group mirroring at `0x3931FA10` copies entry
fields without rescaling and requires the caller's group reentry guard.

Actor eligibility (including stealth), production group ownership, terrain/path
cancellation, leash/return processing, abuser detection/removal, regional delivery,
diagnostic packets and AI callbacks remain production integration work. These are
required services; the production implementation does not supply permissive defaults.

## NPC ownership and aggro routing

The NPC owner map uses the identity ID inside the unit snapshot. The unit registry
uses the outer object ID. `WZAggroReset` handler `0x3936E200` directly indexes the
owner map by identity ID, while `WZAggroCopy` handler `0x39364EF0` resolves both
outer object IDs through `0x39340D90`. The copy handler rejects identical IDs before
calling the table operation. Tests deliberately use different values for both IDs.

Creation preserves the complete `WZNpcState` metadata and requires known templates
and nonzero spawners. Duplicate outer or identity IDs preserve the existing owner
as an explicit replacement ownership policy. Creator lookup (`0x39360CE0`) uses
character persistent ID, NPC identity ID or housing timeline; other creator kinds
have no owner lookup. Constructor `0x39345000` queues `ZWRemoveNpc` when lifetime
exceeds **0.001 seconds**, before owner initialization. Its delay is f32 seconds
times 1000, truncated to i64 and stored as the low signed 32 bits.

`WZUpdateAggro` (`0x3936DDC0`, handler `0x39363890`) consumes three unit object IDs,
u32 amount, hostility and the complete cast union. Skill target and unit in charge
must exist; source may be absent. Hostile `0x39322500` and friendly `0x39321830`
both put the amount in the **direct** component. Hostile updates route through the
target NPC and eligible linked NPCs; friendly updates traverse a copy of the
target unit's ordered reverse NPC set. The cast action supplies diagnostic context,
not an additional effect execution in these handlers.

Routing `0x39321740` attributes slave-source threat to the unit in charge and adds
a zero entry for the slave; mate-source threat goes to the mate with a zero entry
for the unit in charge. Other sources use the unit in charge. Slave/mate charge
owners resolve to characters before additional-target processing. Scene services
must implement `0x39321540` range/attachment exclusions and `0x39321C80` spatial,
AI, template, faction and group link rules; an eligible linked NPC receives one
direct point. Group mirroring has a reentry guard and target-change callbacks
notify current sharing members in native order.

`ZWMakeAggroTargetHostile` (`0x393698C0`) contains NPC identity ID and target object
ID. Abuser registration/clear (`0x3936D500`, `0x3936D6B0`) instead use the NPC's
outer object ID. `ZWRemoveNpc` (`0x3936CA50`) contains NPC identity ID and i32 delay.
The session drains notifications after each complete incoming packet in enqueue
order. Current stat invalidation refreshes lazily queried aggro attributes 62/90;
the level-change handler retains attributes already cached, like other stats.

Fake death `0x3936F060` copies reverse memberships and removes the target from those
tables without changing HP or real death state. NPC death callback `0x393452D0`
disables sharing before clearing threat, notifies AI, then detaches its spawner.
Removal `0x39345E90` detaches the spawner and destroys threat ownership before
releasing the unit (`0x393441B0`, `0x3935FB70`). Other NPC tables retain a removed
unit's ID until their target-refresh pass sees it missing. Session termination
attempts cleanup for every owned NPC even when one actor cleanup fails, discards
pending notifications, and forbids reuse of that simulation.

`IZoneNpcWorld` requires creation validation, actor initialization and cleanup,
spatial/group rules and AI callbacks. Only the test suite supplies a synthetic
scene at present. `ZoneNpcSimulation.Update` implements native `0x393482D0`'s
staggered frame schedule, described below. It requires the remaining actor services
and does not establish gameplay readiness.

### NPC return and abuser maintenance

NPC ownership requires `ZoneNpcContent` as well as stat content. Creation rejects a
missing definition in either catalog before actor initialization. `0x39346960` passes
the template's `return_distance` and `absolute_return_distance` to `0x3931EF20`;
the table stores the normal radius squared, absolute radius and absolute radius
squared separately. The configured read-only game-content source supplies those values.

`ProcessReturn` implements `0x393211E0` and helpers `0x39320C90`, `0x39320D20`,
`0x39320440` and `0x39320DF0`:

- Kinds 3, 4, 5 and 8 first check twice the absolute radius against the actor script's
  current `spawnPos`. A squared radius must exceed f32 0.1; distance must be strictly
  greater than the radius squared. Crossing it reads `spawnPos` again, relocates the
  owner, clears threat with reason 2 and returns. This check also runs on empty tables.
- Ordinary checks require a nonempty table and either no native navigation service
  at AI-system virtual `0x388`, or a nonzero result from that service's virtual `0x10`.
  This is a required environment query, not a default permissive setting.
- The ordinary distance test uses the **absolute** radius squared against the stored
  encounter origin. It does not use `return_distance`. The housing helper follows
  when `return_when_enter_housing_area` is set, then the territory helper queries
  the owner's spawner territory. All three run even after the first empties the table.
- `0x393200D0` records a reason only if the table is nonempty and the signed reason
  is below 18. Implemented reasons are territory 1, distance 2, faction change 3,
  housing 4, death 15, destruction 16 and all-zero reset 17. Later empty clears retain
  the first reason. Clearing removes reverse memberships and abusers; target ID and
  target-change callbacks remain deferred until selection.

`RefreshAbusers` implements `0x3931E570`. Category 0 retains an ID only while the
table is nonempty, the unit and its threat entry exist, and native `0x3931D890`'s
navigation/forbidden-area/path query still classifies the actor as an abuser.
Category 2 requires a nonempty table and an existing unit below terrain height minus
one. Height equality recovers the unit; NaN comparisons do not recover it. Category
1 is untouched by this pass. Both visited categories traverse unsigned IDs in order.
Native erasure advances twice, so the immediate successor is skipped until a later
pass. The implementation preserves that behavior, verified by running the original
tree insertion, erase and cleanup instructions.

Removed IDs are combined into a sorted set, even when an ID remains in another
category. `0x3931DA70` sends `ZWUnregisterNpcAbusers` (`0x3E`) in batches of 100.
Serializer `0x3936D590` writes NPC **unit object ID** as bc, count as u8, then count
bc target IDs. Outgoing count clamps to 100. The replacement reader rejects an
oversized count before allocating/reading, and rejects truncated or trailing data.
This notification is distinct from the single-object-ID clear packet (`0x3F`).

The external native comparison executes 600 return scenarios and 85 abuser scenarios,
including an empty table, missing actors, every category, overlapping categories,
terrain recovery boundaries, and more than 100 removals. Return checks use synthetic
geometry/navigation services and intercept the clear/relocate boundary; abuser tests
execute the original ordered-set lifecycle and capture outbound packet fields. All
685 managed comparisons match. Repository tests additionally exercise actual managed
clear/selection/group ownership, packet encoding, batching and NPC session cleanup.

`RefreshAggro` explicitly performs target refresh, return checks, abuser cleanup and
selection for living NPCs. It does not supply the native actor activation gate or
frame schedule. Actor script positions, navigation/pathfinding, territory/housing
geometry, movement replication, World abuser consumers and executable runtime wiring
remain integration work; this component does not announce `ZoneLoaded`.

### NPC frame scheduling and timers

`ZoneNpcUpdateSchedule` preserves the order in `0x393482D0` and `0x3933B490`.
Native reads the low byte of the engine frame number and XORs it with byte one of
the NPC allocation address. A managed allocation has no equivalent stable address;
the replacement explicitly assigns consecutive byte phases to new owners. Each
phase uses the native masks and order. No native frames-per-second value is inferred.
The actor/entity gate runs first. Death is sampled once before callbacks, so death
during a callback affects the next frame's gate. Movement enablement is read last.

| Cadence | Ordered work | Death gate |
| --- | --- | --- |
| Every frame, aggro enabled | Target selection after the maintenance steps due below | Living only |
| Every 8 frames, aggro enabled | Target refresh before selection | Living only |
| Every 64 frames, aggro enabled | Return checks, then abuser cleanup, before selection | Living only |
| Every 4 frames | AI time event (`0x3933BE40`), target acquisition (`0x39345770`) | Living only |
| Every 8 frames | Proximity (`0x3933BEA0`), activation (`0x3933C020`), cooldowns (`0x39343250`) | Also dead |
| Every 16 frames | Ignored-target expiry (`0x3933FB20`) | Living only |
| Every 64 frames | Creator check (`0x3933C3B0`), talks (`0x39343CF0`), follow target (`0x3933C270`), crowd buff (`0x39347060`) | Living only |
| Every 128 frames | Position check (`0x3933D0E0`) | Also dead |
| After the above, when enabled | Movement replication (`0x393405B0`) | Also dead |

AI timer activation (`0x3933B3B0`) stores enablement, the unsigned millisecond
interval and the current time. Intervals below 50 are accepted; native only logs
a warning. An enabled timer fires once when unsigned elapsed milliseconds reach
the interval, without catch-up. It writes the originally sampled timestamp after
the callback, including when that callback reconfigures the timer. Zero interval
fires on every eligible timer update. Unsigned subtraction preserves counter wrap.

Ignored-target registration (`0x39342FE0`) stores the low u32 of current milliseconds
plus f32 seconds times 1000, truncated through signed i64. Invalid/overflowing float
conversion uses the native indefinite i64 value, whose low u32 is zero. Updating an
existing ID replaces its deadline. A new ID evicts the smallest unsigned deadline
only when the map already exceeds 20 entries; ties select the lowest object ID.
The resulting capacity is **21**. Expiry removes every deadline at or below current
unsigned time. This is an absolute comparison, so wrapped deadlines can expire early.
Acquisition runs before expiry on a frame when both are due and must inspect the
current `Timers.IgnoredTargets` map.

Creator checks use the resolved creator **object ID**, not its persistent or NPC
identity ID. With `DespawnOnCreatorDeath` enabled and a nonzero creator ID, a missing
or dead creator emits `ZWRemoveNpc` for the NPC identity with zero delay. Native
repeats the request on each qualifying frame until World removes the NPC. It does
not destroy ownership locally or clear the request flag after sending.

`IZoneNpcWorld` requires implementations of the remaining actor callbacks listed
in the table. Only synthetic test scenes implement them currently. Scheduling these
callbacks does not supply AI acquisition, script execution, cooldown/interaction
state, proximity queries, terrain geometry or movement serialization. The executable
therefore remains a startup/content validator.

`ZoneSession.ReceiveAsync` accepts an optional `IZoneFrameSource`. It maintains one
packet read and one frame read, handles a completed packet before a simultaneously
ready tick, then flushes notifications after each. At most one packet precedes a
ready tick, preventing traffic from starving simulation. A partial packet remains
owned by its original read while updates continue. Clock end, EOF, cancellation,
packet errors and actor exceptions cancel and observe both pending reads before
releasing actors. Sources must honor cancellation; the caller owns and disposes the
stream and source. Actor cleanup failures retain an initiating receive/update error
in an aggregate exception. `PeriodicZoneFrameSource` increments an unsigned frame
counter with an explicit caller-selected period and coalesces missed ticks.

NPC traversal snapshots current ownership; callbacks may remove owners safely and
new owners begin on the next update. Removed/replaced owners receive no further
callbacks in that traversal. This is an explicit managed lifetime policy.

The isolated oracle executes the original scheduler with all 128 effective phases
and 128 frame residues for living/enabled, dead/enabled and living/disabled aggro:
**49,152** schedules match. It also checks native missing-entity/AI gates. Native
timer setup/update produces **26** matching timestamp/event samples; original map
registration, eviction and expiry produce **3,044** matching operations, including
capacity, duplicate IDs, equal deadlines and wraparound. Clock/actor callbacks are
synthetic; native tree and scheduler code execute unchanged. Repository tests cover
real TCP idle/fragmented reads, notification ordering, repeated creator removal,
callback death/removal, clock termination/failure and complete actor cleanup.

## NPC content catalog

`ZoneNpcContent` loads the 101 scalar columns read by native `LoadNpcDescs`
(`0x39B080B0`), including behavior flags, return/link distances, AI references,
appearance, interaction, equipment and skill references. Nullable integers/floats
become zero and integers preserve `sqlite3_column_int`'s low 32 bits. Database
levels remain i32 values; this load step does not clamp them to a snapshot byte.
Native boolean loaders test only the first text character for `T`, `t` or `1`;
they do not trim whitespace or treat every nonzero numeric value as true. Floating
columns convert the SQLite double to f32. Names retain source text, before the
separate native localization pass at `0x39AF2BA0`.

Group-member loading (`0x39AFE6F0`) skips rows whose NPC template is missing.
Group assembly (`0x39AFE9E0`) attaches remaining rows to existing definitions in
source row order. The configured tables have `INTEGER PRIMARY KEY` IDs; explicit
`ORDER BY id` retains their native full-table traversal order. Member indexes
therefore refer to ordered formation slots, including duplicate NPC templates.
Offsets, formation tension, leader and movement-leader flags remain distinct.
Missing leaders and empty groups are allowed; actor initialization `0x39346960`
uses the first movement leader if present.

Aggro-link rows (`0x39AFC590`) attach independently to matching NPC templates
(`0x39B080B0`) and link definitions (`0x39AFC750`). A link can retain a row for an
absent NPC, while an NPC can retain a row referring to an absent link. Expansion
at `0x39321C80` skips absent link definitions, then appends each existing link's
NPC IDs in order without deduplicating either the source rows or resulting IDs.
AI files (`0x39AFC0A0`) and parameter text (`0x39AFC9F0`) resolve independently.
Unresolved AI IDs remain visible as source references; the catalog does not
invent a replacement script or execute a parameter string.

The configured content audit loaded 19,522 NPCs, 382 groups, 1,568 formation
members, 180 aggro links, 973 link rows, 15 AI files and 3,459 parameter sets.
An independent SQLite comparison checked all **1,971,722 scalar field values**,
every formation/link relationship and order, expanded link lists, and AI text
with no mismatches. The source database SHA-256 remained unchanged. The data
contains two duplicate NPC/link pairs, one group without a movement leader and
three NPCs referencing a missing AI-file definition; these are preserved.

This catalog does not yet resolve every referenced model, interaction, posture,
skill or other NPC child table. Production actor creation, group state, AI script
execution, pathing and executable runtime wiring remain required for feature parity.

## Verification

Tests consume World-generated complete snapshots for all seven supported unit
classes and check every truncated prefix. Nonempty native-layout fixtures cover
character stat/reinforcement collections and every buff caster variant. Further
coverage includes full equipment details and opaque item variants, appearance,
optional flag branches, attachments, bonding, posture and zero/nonzero actions.
Golden packed-integer bytes independently check widths and group boundaries.
TCP tests cover coalesced startup plus fragmented units, duplicate/create/remove
ordering, EOF, cancellation, malformed/unsupported messages and one-shot receive
loops. These are synthetic fixtures, not claims of completed client gameplay
validation. The current suite also covers content-derived vital stats and precise
point updates; see the accompanying stat evidence for those checks.
Combat tests additionally cover exact World/golden packet bodies, every truncated
prefix, all owner kinds, repeated death/resurrection, current equipment/buff/level
recalculation, target and flag distinctions, failed-mutation atomicity, removal,
reconnect and fragmented TCP dispatch. Rotation cases independently fix the native
axis-angle bytes, negative-half rounding, threshold behavior and tilted World
snapshot orientation. These checks do not execute a native physical actor.

Aggro tests cover arithmetic/truncation, clock wrap and decay, capacity and reverse
membership callbacks, blocking/unblocking, taunts, component ranks, sleep, reset,
copy and group mirroring. Golden orders for 3, 32, 33, 41, 42 and 99 trailing
entries were captured by executing original `0x39325100` x64 machine code in an
isolated emulator. Weighted/taunted selection flags and component caches were
also checked against original `0x3931EB70`, with only region-observer lookup and
delivery stubbed. This is native instruction validation of those functions, not
a running Zone, physical actor or end-to-end gameplay test.

NPC integration tests cover both ID domains, creator resolution, rejection before
ownership, lifetime threshold and notification ordering, source/charge routing,
reverse memberships, group propagation, forced targets, fake/real death, removal,
stat invalidation and failed actor cleanup. Wire fixtures check all cast branches,
World encoders, native golden bytes, every truncated prefix and trailing garbage.
TCP replay covers fragmented/coalesced creation and aggro packets, notification
order, EOF, cancellation, malformed frames and fresh ownership on reconnect.

## Faction, expedition, family and war state

`0x39D1C900`, called with `ServerUnit+8` by `0x3935D260`, maps the packed
three-value group to current faction (`0x8E2` -> unit `0x7E2C`), expedition
(`0x8E3` -> `0x7E30`) and family (`0x8E4` -> `0x7E34`). The getters are
`0x39258C00`, `0x39258C20` and `0x39258C50`; the latter supplies the family
lookup and invitation in `0x396AA220` / `0x39D36B30`. These are not plot IDs,
mana-regeneration flags or three copies of the faction ID. World writes its
current faction, expedition and character family separately. Housing resolves
the expedition through its owner's existing World membership service.

Packet member `0x9A8` is the packed expedition-war opponent at unit `0x7FF0`.
The character-only byte `0x9A9` is the war side at `0x7FF4`; World retains the
name `Camp` for this byte. `0x392587E0` writes these two values. Duel state stays
at `0x7FE8/0x7FEC`. NPC WZ snapshots now carry actual packed state and optional
flag blocks, independently of the zeroed client NPC mirror. Choosing WZ is an
explicit serializer context, including when no placement override is supplied.

| Message | Complete body | Serializer / handler |
| --- | --- | --- |
| FactionCreated `0x15` | full FactionDesc | `0x3936A950` / `0x39363270` |
| FactionSponsorChanged `0x16` | full FactionDesc | `0x3936A970` / `0x39363290` |
| FactionDismissed `0x17` | faction u32 | `0x39366CF0` / `0x393632B0` |
| FactionSetRelationState `0x18` | two faction u32, state/next u8, update/change u64 | `0x39366D20` / `0x393632D0` |
| UnitFactionChanged `0x19` | unit bc, old/new faction u32, temporary bool | `0x3936B8C0` / `0x3936E0E0` |
| UnitExpeditionChanged `0x1A` | unit bc, old/new expedition u32 | `0x3936B970` / `0x39364BC0` |
| ExpeditionWarState `0x1B` | two expedition u32, started bool | `0x39366E40` / `0x39363310` |
| FactionIndependence `0x1C` | old/new faction u32, name string, created u64 | `0x39366EB0` / `0x39363340` |

`ZoneFactionState` copies startup descriptors and relations for one connection.
`0x39C97E90` replaces a created faction; `0x39C97BC0` only replaces an existing
sponsor descriptor. Both owner handlers (`0x3932D480/0x3932D4F0`) set an existing
character owner's expedition and clear war state; the sponsor handler does this
even if its descriptor ID was absent. `0x39C9DD00` rejects dismissal when the
mother ID is zero. Successful dismissal resets matching unit expeditions and
war state through `0x3935DB90`, retaining faction IDs and pair relations.

`0x3932D5A0` ignores relation state zero and otherwise resets updater/confirmer
IDs and names. `0x39C97890` accepts only pairs whose IDs are both at least 100
(`0x3A5A2D90`), stored in canonical ID order. This dynamic relation map does not
replace the separate static system-faction relations or the full unit-specific
relationship evaluator. Faction-help (`0x3932D2B0` / `0x39C94270`) compares each
ID after exactly one nonzero mother hop and excludes zero; it does not require
the mother to exist in the map or recursively walk ancestors.

The two unit change handlers compare the supplied old ID before mutation.
`0x3925FA70` writes faction/temporary state before updating an available AI
actor; `0x3936E0E0` then clears an owned NPC's threat. Expedition changes reset
war state even if the new expedition equals the old one. `0x3932D930` applies
war side 1 to the first expedition's existing units, then side 2 to the second;
equal IDs therefore end with side 2. Stopping resets both sides. Later units
retain their snapshot state because this handler has no persistent war map.

Independence (`0x3932D660`) copies the old descriptor, sets the new ID/name/time,
clears its mother, enables diplomacy-target, and points the old descriptor at
the new faction. Existing members keep their expedition and war state. Their
current faction changes only outside battlefield Zone keys, without clearing
NPC threat. `0x39BFB630` compares `battle_fields.zone_key`, loaded at descriptor
`0x58` by `0x39B9F0C0`; the Zone-key getter is also used by join sender
`0x39366760`. `ZoneContentDatabase.IsBattlefield` supplies that classification.
Unknown classification rejects a known faction's independence before mutation.

Wire tests compare World encoders, native golden bodies, all truncated prefixes
and trailing bytes. Lifecycle tests cover stale IDs, independent snapshot fields,
owner updates, root protection, shared threat, relation replacement, war ordering
and battlefield independence. Fragmented TCP replay verifies ordered dispatch,
malformed-body atomicity, unchanged startup snapshots and fresh reconnect state.
An isolated x64 emulator also executed the original `0x39C94270` over 112
synthetic faction lookups, `0x3932D930` over 16 ordered war-dispatch cases, and
`0x392587E0` over four war-field writes with surrounding memory checks. All
matched the recovered rules. This exercises native instructions with synthetic
memory and observed dispatch, not a running actor or end-to-end gameplay session.
Full relationship overrides, diplomacy scheduling and production actor/AI services
remain unfinished.

## Base faction relation evaluation

`ZoneFactionState.GetRelation` implements `0x39C969C0`, called by the Zone's
base-relation callback `0x3932D210`. An original ID of zero returns no relation
and no attack permission. Equal nonzero original IDs return friendly immediately,
even when their descriptors are absent. Otherwise `0x39C942F0` follows each
nonzero mother ID until a root or missing descriptor, retaining the missing
ancestor's ID. This differs from the single-hop faction-help helper above.

`0x39C95930` compares those resolved IDs in this order: equal roots are friendly;
either built-in hostile faction (ID 3) makes the pair hostile; otherwise either
neutral faction (ID 2) makes it neutral; otherwise either friendly faction (ID 1)
makes it friendly. The constants are at `0x3A5A2D84/88/8C`. Remaining pairs use
the current canonical unsigned-ID relation map (`0x39C95880`), with neutral for
a missing pair. Only hostile state permits attack at this layer; the reason byte
is zero. Snapshot state zero and unknown state bytes are preserved. Scheduled
next-state/time fields do not change the result until a World update arrives.

The manager constructor `0x39331BC0` initializes empty faction and relation maps
through `0x396F4830` and `0x3932FB20`; this evaluator does not seed or fall back to
the static game-content tables. Reconnect builds new maps from World's complete
snapshots. Sponsor, create, dismiss, independence and relation messages affect
the next query without cached ancestry. Cyclic ancestry makes the native walk
nonterminating; the replacement explicitly throws `InvalidDataException` when
the walk exceeds the descriptor count. Invalid/equal-ID shortcuts still run first.

Tests cover built-in precedence, full versus single-hop ancestry, missing roots,
all 256 relation-state bytes, unsigned high IDs, update ordering, reconnects and
long/cyclic chains. An isolated x64 emulator executed original `0x39C942F0` and
`0x39C969C0` instructions over synthetic maps and configured faction rows
projected into World snapshot maps. All 154 root queries and 17,260 ordered
relation queries matched the replacement. This verifies the base evaluator;
unit-specific overrides, safe-zone rules and force-attack permissions are a
separate unfinished layer.

## Static faction content

`ZoneContentDatabase.LoadFactionContent` reads the configured decrypted
`game.compact` export through the existing read-only, query-only connection.
Native scalar loader `0x39C063F0` consumes 14 `system_factions` columns: ID,
aggro-link, expedition-creation description, guard-help, icon ID, integration,
diplomacy-target, mother ID, name, owner ID/name/type, political system and
show-create-expedition. The five flags occupy bits 0..4 at native descriptor
`0x40`. IDs retain unsigned low 32 bits; owner type and political system retain
signed low 32 bits. Booleans use the first text character (`T`, `t` or `1`),
including SQLite numeric-to-text conversion; NULL yields zero, false or empty
text. The replacement shares these conversion helpers with NPC content loading.

`0x39C067E0` loads `system_faction_relations` as a separate ordered row catalog.
Each row retains its ID, original first/second faction IDs and full signed
32-bit state. The loader does not canonicalize pairs, deduplicate pair rows,
validate referenced factions or truncate state to the dynamic wire byte.
Catalog constructors copy their inputs and reject duplicate row identities.
These definitions do not populate or override a connection's `ZoneFactionState`.

The catalog retains source name/description text and icon IDs; native localization
(`0x39BFF180` / `0x39AC1350`) and icon-object resolution remain separate work.
Guard targeting uses the unit evaluator below and still requires production scene services.
Tests cover every scalar field, NULLs, boolean prefixes, integer widths, row
ordering, reverse/duplicate pairs, unresolved IDs and read-only file integrity.
A direct comparison against the configured database matched all 1,736 scalar
values across 124 factions and 11,532 values across 2,883 relation rows. The
content-check executable loaded both catalogs and the source SHA-256 remained
`6EBC25AC6D860268D5E28454E3971B1FD8E69E3DD0D98CFBFB4E93C570C3D851`.

## Character and faction combat overrides

`WZCvFCombatRelationship` (`0x27`, body `0x3936AC10`, entry `0x39389790`)
contains a u8 count and entries of signed character i64, state u8 and reason u8.
The internal 24-byte record has an additional faction field which is absent
from the wire. `WZFvFCombatRelationship` (`0x28`, body `0x3936DD40`, entry
`0x3938FFC0`) contains a u8 count and entries of first/second faction u32,
state u8 and reason u8. Both allow 255 entries and preserve all state/reason bits.
Registration `0x3936F740` connects handlers `0x39363810/0x39363830` through
`0x39380E80/0x39380F50`. Their functors allocate 255 internal records and parse
the full body before dispatch. World writers now use the shared wire contracts.

The handlers call `0x3932C2D0/0x3932C510`, which apply entries in order to the
relationship context. `0x39CC2020/0x39CC20A0` remove a matching key for state zero;
nonzero entries use insertion with no replacement. The native comparators
(`0x39CC12E0/0x39CC1430`) discard duplicate insertions, preserving the first
state and reason until removal. Empty packets retain all entries. Character
keys compare as signed i64, and faction keys compare as ordered unsigned pairs.
The faction update handler does not canonicalize its input or filter IDs below 100.

`0x39CC15A0` requires a nonzero character ID and nonzero opposing faction ID,
then looks up only the character key. `0x39CC1710/0x39CC1670` require two nonzero
faction IDs. The caller `0x39CC17B0` canonicalizes that query, so a reversed stored
pair remains a distinct row which does not satisfy the canonical lookup.
Both lookups retain state, permit attack exactly for state 1 and return reason
zero even when a different reason is stored. Override precedence is source
character, reverse character, then faction pair; a nonzero state stops this
search. Equal faction IDs do not bypass explicit combat overrides.

`ZoneCombatRelations` implements these maps and lookup stages. `ZoneSession`
owns a fresh instance for each connection and dispatches both messages after
complete decoding. Neither missing actors nor actor removal invalidates these
character/faction records. Tests cover wire bounds, all nonzero state bytes,
zero-ID guards, insertion/removal order, unsigned pairs, lookup precedence,
stored reasons, malformed-batch atomicity and fragmented TCP reconnects.
An isolated x64 emulator ran the original serializers in both directions and
executed the native handlers, balanced-tree mutations and lookups over 105
update batches. Only heap allocation/free and serializer primitives were
supplied externally. All stored rows, counts and 9,345 queries matched the
replacement. Production World gameplay must still produce the updates. The
ServerUnit evaluator below combines these maps with the remaining unit rules;
combat and NPC targeting still require actual scene services.

## ServerUnit relationship evaluation

`ZoneUnitRelationships` implements the Zone wrapper `0x3932C4B0` and shared
evaluator `0x39CC17B0`, specialized to the native ServerUnit interface. Constructor
`0x393603D0` places this interface at unit+`0x8670`, vtable `0x39FE54D8`, and the
Zone buff manager at unit+`0x86A0`. The evaluator keeps the relation byte,
attack-permission bit and reason byte independent.

The accessors establish the identity and state rules:

- `0x3935D010` accepts a unit with a nonzero character, faction or expedition ID.
  Death does not invalidate its relationship identity.
- `0x3935D130` uses the character's persistent ID, a slave/mate owner ID, or an
  NPC owner ID only when its signed value exceeds 999. Other kinds have no owner.
- `0x3935E4C0/0x3935E570` use character duel state directly and resolve a mate's
  current owner character for duel ID/team. Vehicles do not inherit duels.
  Owner traversal uses the first matching current character; missing owners
  produce duel ID zero and team `0xFFFFFFFF`.
- `0x3935D1E0` reads character GM mode index 6. Other kinds return false.
- `0x3935E5F0` reads the character's Retribution counter and resolves current
  owners for mates/slaves. `0x39D2C030` changes this counter for exact buff ID
  2167 (`0x3A5A38DC`), independent of the generic `buffs.crime` column. The
  configured source confirms this is an active, non-one-time buff, has no passive
  definition, and has `crime=false`. Current active buff presence supplies this
  state after creation/removal; a missing template is not counted.
- `0x3935F9A0` finds the NPC owner by `Identity.NpcObjectId`, then
  `0x3931DBF0` searches all threat entries for the target object ID. The entry
  need not be the selected target, eligible or absent from the abuser list.
- `0x3935D200` tests both NPC definition flags `AcceptAggroLink` and
  `AggroLinkSpecialGuard`, proven in loader `0x39B080B0`.
- `0x3935C690` reads the current scene's Zone key. Unit World/region IDs are
  not substitutes. `0x39CC08C0` selects expedition over base faction unless the
  temporary-faction flag is set; `0x39CC0770` always uses the base faction.
  Join handler `0x393648D0` selects the latter from `WZJoinResponse.Battlefield`.

Precedence matches the shared evaluator: validity and GM checks; source/reverse
character and faction-pair overrides; conflict override; base faction relation;
faction-186 battlefield participants; reciprocal expedition war; opposing duel;
siege override; then kind-specific attack restrictions. Faction 186 uses owner
equality and reason 13, but does not bypass later restrictions. War uses reason
11 and requires nonzero owners/expeditions and reciprocal war-opponent IDs.
Group 49 (`arche_mall`, constant `0x3A5A30AC`), battlefields and war-chaos groups
exclude this override. Integration zones discard it; opposing duels can then
apply if the previous relation was not hostile. Duel IDs zero/`0xFFFFFFFF`,
same owners and same teams do not qualify. Accepted war/duel reasons bypass the
later owned-unit PVP block.

Conflict callback `0x3932C350` requires owners, a valid current key, a nonzero
common group, current conflict state 6 and a war-chaos definition for the
current conflict group. It compares full roots of the units' **base** factions,
returning reason 12. Loader `0x39BA56B0` maps `war_chaos` to descriptor+`0x158`
bit 3. Zone loader `0x39C88B10` maps `integration` to descriptor+`0x28` bit 2.
The helper does not additionally require the conflict group to equal the units'
group. The incoming conflict-state handler separately validates the group.

An NPC source with a threat entry gains attack permission before guard protection.
Other protected NPC sources return reason 9. Against NPC targets, a friendly
base-faction relation to the area's faction blocks attack with reason 1 before
guard protection (reason 10). NPC-to-NPC queries keep the prior result. For two
owned non-NPC units, the configured PVP block has reason 2; friendly/neutral units
with the same owner have reason 3. Hostility alone does not grant attack permission:
the later player branch requires force attack or the retained reason 8. For
friendly/neutral relations, a different owner's Retribution also permits attack.
Final force-attack decisions clear the reason byte while preserving relation state.

Guard protection additionally requires a **festival** definition: loader
`0x39BB6890` directs `LoadFestivalZoneDescs` (`0x39B51EF0`, `festival_zones`)
to the map read by `0x39B3D6D0`. Eligibility requires membership in the active-date
festival set built by `0x39B4DDE0` and passes runtime group exclusions in
`0x39C53B50`. Conflict-zone loading `0x39B43990` reads this set and writes a
separate open-conflict set; it does not add conflict groups to the festival set.
A siege-zone row is not a substitute.

The ServerUnit vtable makes slots `0x10`, `0x58`, `0x60` and `0xA0` constant zero.
The Zone peace-faction callback `0x3932C2C0` also returns the zero faction constant.
Consequently the generic invulnerability, same-team, attackable-housing and
peace/low-level branches are inactive in this server configuration. This is a
verified specialization, not an assumed policy for an unknown callback.

`ZoneSession.Relationships` is created only with explicit NPC/buff content and a
`relationshipWorldFactory` returning `IZoneRelationshipWorld`. The factory receives
the accepted join response after startup. `ZoneRelationshipScene` supplies area
data, join/group PVP configuration, battlefield/war-chaos classification, conflict
updates and festival activation. Its `ZoneSiegeState` evaluates siege participation
and geometry; the required `IZoneRelationshipRuntime` supplies guard exclusions.
Production runtime configuration and executable scene wiring remain unfinished. Tests
exercise live duel and buff changes, owner disappearance, NPC identity/aggro,
native rule precedence and connection isolation. Native machine-code comparison
executes the real wrapper, evaluator, ServerUnit accessors, base/override maps and
war/duel/conflict helpers. Scene/content lookups and the final siege result are
supplied explicitly; NPC ownership is empty in that probe and tested separately
through the real replacement ownership path. All 6,708 native comparisons matched.
The additional siege comparisons below exercise the previously supplied callback.
Neither comparison establishes complete production runtime or siege gameplay.

## Relationship scene content and conflict updates

`ZoneSceneContent` retains all ten scalar zone fields and all eighteen consumed
festival fields, including raw calendar values. It also loads battlefield keys,
`conflict_zones.war_chaos` and `indun_zones.pvp`. These last two catalogs are policy
projections; their remaining gameplay definitions are unfinished. Group keys use
the low unsigned 16 bits and repeated group records overwrite prior fields,
matching the native loaders. Duplicate zone keys are rejected as malformed content.
The configured read-only database supplied 319 areas, 33 conflict policies,
50 PVP policies and 20 festivals.

Configuration builder `0x3932C580` resolves the scene's zone group. A matching
nonzero join group uses the join PVP flag; otherwise lookup `0x39B3DDA0` uses
`indun_zones.pvp`, loaded into descriptor bit 6 at offset `0x88` by `0x39B9F7F0`.
A missing policy does not block owned-unit combat. Battlefield membership comes
from the zone key, independently of the join's effective-faction selector.

`WZConflictZoneState` opcode `0x84` has exactly a little-endian `u16` group and
`u8` state, serialized by `0x39368670` and registered by `0x39384A10`. Handler
`0x393643A0` calls `0x3932C780`, which accepts an update only when the current
scene group is nonzero and matches the packet group. All 256 state bytes are
accepted. The initial global at `0x3A517B70` contains group 0 and state 7;
join conflict fields are copied separately and do not initialize this state.
The replacement uses a shared World/Zone wire contract and fresh connection
state. Complete decoding precedes mutation. Wrong-group updates leave state
unchanged; malformed packets close the session without applying their prefix.

Festival loader `0x39B51EF0` applies the configured signed minute offset to each
date before `0x39B4DDE0` converts the stored dates again and selects active groups.
The native loader samples current time once. The replacement therefore requires
an explicit content-load timestamp and offset and retains the resulting set.
Runtime exclusion lookup precedes active-festival membership, as in `0x39C53B50`.
The World join filter now supplies these exclusions through `ZoneWorldContentPolicy`;
see the content-filter evidence below.

Calendar conversion `0x39B110A0` returns zero when year, month or day is zero.
Other values clamp year to at least 1900, month to 1–12, day to 1–31, hour to
0–23 and minute to 0–59. `XlMakeFileTime` calls MSVCR100 `_mktime64` with
`tm_isdst=0`, normalizing dates in local standard time. The values are Unix
seconds despite the helper name. Offset conversion `0x39B0F1E0` uses
`_localtime64_s`, including daylight-saving rules, before the later conversion
back to seconds. Period helper `0x39B0F3B0` returns before/during/after as 2/1/0;
endpoints are inclusive and zero endpoints are unbounded.

`ZoneCalendarDate` uses the Windows system UCRT and explicitly preserves the
original MSVCR100 upper timestamp limit `0x793406FFF`. Failed time conversion
retains signed -1 as unsigned all-ones. An invalid shifted timestamp produces
the native failed calendar fields without invoking the CRT invalid-parameter
handler. Finite conversions on other platforms throw explicitly; portable
native-compatible local-time rules are not implemented.

Validation compared the original calendar, period, conflict handler and complete
PVP configuration builder against the replacement: 3,622 calendar cases,
225 period queries, 80 conflict updates and 192 configurations matched. All
422 catalog records and 18 festival activation sets also matched. The probe
supplied content lookups, allocation and the final configuration sink; the native
selection branches executed unchanged. Calendar probes used the original
MSVCR100 helpers. The source database hash remained unchanged. The full Zone
suite passed 269 tests, including fragmented conflict packets, rejected malformed
bodies and fresh scenes on reconnect. These checks do not establish gameplay readiness.

## Dominion and siege state

`ZoneSiegeState` implements the connection-owned Dominion/siege state used by
`0x3932C290` / `0x3932C8E0`. The receive loop supports all five lifecycle messages:

| Message | Opcode | Body serializer | Handler |
| --- | --- | --- | --- |
| DominionData | `0x60` | `0x39362370` / `0x39CC4310` | `0x39363EE0` |
| DominionDeleted | `0x61` | Empty | `0x39363F00` |
| SiegeState | `0x62` | `0x39367F70` | `0x39363F20` |
| SiegeMember | `0x63` | `0x39367FD0` | `0x39363F50` |
| SiegeSecondHalf | `0x64` | `0x39365F30` | `0x39363F80` |

DominionData carries group `u16`, owner faction/house/tax `u32`, full world
position, five 64-bit money amounts, four 64-bit timestamps, point `u32`, object
`bc`, area rules, full siege status, non-PVP start `u64` and duration `u16`.
The native end time adds that duration with unsigned wraparound. Area rules
(`0x39BDC750`) retain two type fields, gate/wall bytes and four signed radii.
Siege status (`0x39CC2830` / `0x39CC4250`) has four duration values for periods
1 through 4, started/fixed times, an extra scalar, a signed period byte, defender,
ordered offense vector and second-half boolean. The replacement preserves all
fields; it does not reuse the older client Dominion model's different layout.

Each faction (`0x39CC2DC0`) contains faction `u32`, object `bc`, full world
position, member limit/count bytes and signed 64-bit member IDs, building count
with `(bc, full position)` entries, then team and score `u32`. Fixed native
storage holds 50 members and 100 buildings. The replacement rejects limits or
counts that could overrun those native arrays, as a malformed-input policy;
the original serializer itself does not enforce those bounds. Counts greater
than the current member limit remain valid when they fit storage. Offense count
is `u32`, bounded against the complete frame before allocation. Every message
must consume its full body before any state changes.

The full snapshot replaces the current group and state without an additional
group-match test (`0x3932CFD0`). SiegeState replaces rules and participants but
retains the previous owner faction when the incoming owner is zero
(`0x3932C940`). Deletion retains the current group and resets ownership, timers,
geometry and rosters; the initial/default defender member limit is 50
(`0x39CC3F30`, `0x39CC3E60`). Each new scene starts independently.

Membership changes (`0x3932C860`) select the defender first, then the first
matching offense faction. Addition (`0x39CC24B0`) appends below capacity and
accepts duplicate and zero IDs. Removal (`0x39CC26F0`) deletes the first matching
ID and moves the last member into the vacated slot. Unknown factions and missing
members leave state unchanged. Second-half updates immediately change the flag.

The relationship override requires nonzero Dominion ownership, period 3 and
both units inside the siege area (`0x39CC3BF0`). Area helper `0x39695460` combines
the main circle, offense headquarters, offense buildings and the defender
headquarters in the second half. Horizontal distance (`0x39B10ED0`) uses wrapped
signed fixed-point subtraction, single-precision scale `2^-44`, squared signed
radius and a strict `<` boundary. Height does not participate. Headquarters
circles require period 3; the main and offense-building predicates themselves
do not. The outer relationship callback still requires period 3.

Participant lookup `0x39CC2F20` uses a character's own persistent ID, a slave or
mate's owner ID, and a housing unit's current base faction. NPCs and other kinds
do not qualify through owners. Defender lookup precedes ordered offense lookup.
Both units in the same record are friendly. Distinct offense rosters are neutral
in the first half and hostile in the second half. The housing branch retains a
false attacker flag even when matching an offense faction, so it does not enter
that first-half neutral branch. Other distinct participants are hostile
(`0x39CC3110`). The returned reason byte is zero; later ServerUnit force-attack
and other restrictions still apply.

Validation executed the original serializers in both directions for 33 complete
Dominion cases, including full-capacity rosters/buildings and signed period
values. The replacement matched all 4,000 relationship queries, 24,000 geometry
queries and 1,000 ordered native lifecycle updates. The probe supplied the
actor/world position accessor, allocation, CRT memory operations, C++ iterator
bookkeeping and an empty unrelated persistent map. It executed the native
participant lookup, circle predicates, wrappers and state mutations. The full
test suite passed 277 tests, including a fixed native golden packet, World
encoder equality, all truncations/trailing bytes, malformed nested counts,
fragmented TCP updates, deletion and reconnect isolation.

Remaining work includes World gameplay production/replay of these snapshots,
authoritative siege schedules, economics, actors, skills/effects, client presence
and live validation. The runtime festival filter configuration is described below.

## World content filters and festival exclusions

Accepted join handler `0x393648D0` initializes the raw filter buffer through
`0x39C5B580`, then rebuilds filtered content through `0x39C56B80`. A nonzero
rejection reason returns before filter initialization. A malformed accepted
filter returns failure before Zone scene startup. `ZoneStartupState` follows
that ordering and publishes neither the join nor a partial filter on failure.

The buffer has this layout, independently of the enclosing join's signed
32-bit byte size:

| Field | Native width |
| --- | --- |
| Category count | `u16` |
| Category name length and bytes | `s8`, then that many bytes |
| Filter count for this category | `u16` |
| Each filter name length and bytes | `s8`, then that many bytes |

An entirely empty buffer succeeds and clears the configuration. Zero counts
and empty names are valid. Names are length-based binary `std::string` values:
invalid UTF-8 and embedded NUL bytes are retained. Repeated categories replace
their entire filter set while keeping the first key spelling. Filter duplicates
keep the first spelling. Case-insensitive map/set comparison is native
`0x39C53490`. Trailing bytes inside the filter buffer are ignored by the native
parser; the shared contract retains them for exact re-encoding. The enclosing
join body must still be fully consumed.

Bounds lambda `0x39C536A0` checks each requested read against the buffer size.
Its signed count handling does not safely reject negative name lengths. The
replacement rejects length bytes 128–255 explicitly, validates count bounds
before allocation, and constructs the entire filter before changing connection
state. This is a malformed-input safety policy, not an alternate wire format.

The other caller, `0x394BBB70`, is registered by `0x39562930` as
`PacketFunctor<ClientPlayer,ServerToClientPacketType,SCWorldContentPacket>`.
It does not establish a WZ runtime update. Zone's implemented filter lifecycle
is the accepted join and a fresh configuration on reconnect.

The name comparator calls MSVCR100 `tolower` for each **unsigned byte**.
`xlcommon.dll!XlInitStringUtil` (`0x3301FB80`) sets `LC_CTYPE` from its locale
table at `0x331EEA70` / `0x331EEAD0`. Original CRT probes confirmed:

| Native locale selections | Byte folding |
| --- | --- |
| Initial C, `ko`, `zh_cn`, `zh_tw`, `ja`, `th` | ASCII A–Z only |
| `en_us`, `en_sg`, `de`, `fr`, `ind` | Windows-1252 uppercase bytes |
| `ru` | Windows-1251 uppercase bytes |

In this binary `zh_tw` selects `chinese-simplified`, and `ind` selects Hindi
with code page 1252. `WorldContentNameComparer` reproduces these tables without
changing the managed process locale. Filter dictionaries and sets use the same
explicit comparer, including equality and hashing. Callers pass the configured
comparer to startup and the relationship scene; omitted configuration uses C.
The CLI exposes this selection as `--locale`. Unknown explicit selections fail.

Loader `0x39C69C40` reads `id, content_type, content_id, name` from
`world_contents`. Exact, case-sensitive type matching uses
`xlcommon!XlCompareString` at `0x3301E1B0`. The 18 kinds are Craft=0, Zone=1,
TowerDef=2, GameSchedule=3, Transfer=4, NpcSpawner=5, QuestContext=6,
DoodadAlmighty=7, Character=8, LootPack=9, AppellationMerit=10, ScheduleItem=11,
ArchePass=12, Instance=13, Appellation=14, Achievement=15, TaggedBuff=16 and
FestivalZone=17. Unknown types fail loading. IDs retain SQLite's low 32 bits;
FestivalZone's effective content ID uses only the low 16 bits. Names and types
use native C-string termination; the immutable catalog preserves raw UTF-8 bytes
before the first NUL. It does not apply the packet-name length limit to content.

The configured read-only database contains 7,405 descriptors. The single festival
descriptor is row 4923, name `test_fes`, kind FestivalZone, content ID 93. Relational
checks confirmed `festival_zones.zone_group_id=93` and `zone_groups.id=93`
(`w_hanuimaru`). The other 7,404 rows cover all 17 remaining kinds.

Rebuild `0x39C549D0` clears exclusion sets before processing the descriptors.
Festival handler `0x39C5B320` accepts only kind 17 with a nonzero `u16` group,
then looks for its name in category `festival_zone`. Matches enter the group
set at configuration offset `0x460`. Guard helper `0x39C53B50` rejects an
excluded group before testing active-festival membership through `0x39B3E2B0`.
`ZoneWorldContentPolicy` supplies this behavior to `ZoneRelationshipScene`.
Content IDs are resolved from descriptors, never parsed from filter names.

Validation executed the original parser for 567 accepted/truncated buffers,
plus 24 locale-specific filter/policy cases and the fixed golden buffer
`01000141020001620143`. The original comparator matched all 196,608 single-byte
pairs across the three distinct case tables. The managed comparison also
checked comparison, equality and hash consistency for 786,432 pairs across
all 12 locale selections. All 3,248 festival queries, 7,405 configured catalog
records and 128 synthetic width/type/name records matched. The native loader
executed its real type branches and the original xlcommon comparison function;
the probe supplied SQLite row reads, storage allocation and string interning.
The parser and festival probe supplied allocation, CRT memory and case helpers,
iterator bookkeeping, logging and active-festival membership. All parsing,
map/set mutation and exclusion branches executed from the original binary.

The configured `TEST_FES` filter selected exactly group 93. The source database
SHA-256 remained `6EBC25AC6D860268D5E28454E3971B1FD8E69E3DD0D98CFBFB4E93C570C3D851`.
All 287 tests passed. The suite covers native golden bytes, duplicate replacement, signed lengths,
truncation, raw names, locale changes, read-only loading, World encoder equality,
rejection ordering and fragmented TCP reconnect with fresh scene policies.

The subsequent category and stat integration is described below. Authoritative
gameplay, executable scene wiring and live-client validation remain pending.

## All content exclusions and filtered stat inputs

Registration `0x39C5BA90` binds all 18 category names to separate handlers.
These handlers compare the descriptor kind before matching a name. They skip
the zero content-ID sentinel except for LootPack, which can exclude zero.
Only the Zone handler resolves a descriptor before inserting its result;
all other handlers retain matching content IDs even without a referenced row.

| Kind | Filter category | Handler | Exclusion set offset |
| --- | --- | --- | --- |
| Craft | `craft` | `0x39C58AD0` | `0x20` |
| Zone | `zone` | `0x39C58D30` | `0x60` |
| TowerDef | `tower_def` | `0x39C58F80` | `0x80` |
| GameSchedule | `game_schedule` | `0x39C591E0` | `0xA0` |
| Transfer | `transfer` | `0x39C59440` | `0xC0` |
| NpcSpawner | `npc_spawner` | `0x39C596A0` | `0xE0` |
| QuestContext | `quest` | `0x39C59B60` | `0x120` |
| DoodadAlmighty | `doodad` | `0x39C59900` | `0x100` |
| Character | `character` | `0x39C59DC0` | `0x160` |
| LootPack | `loot_pack` | `0x39C5A020` | `0x180` |
| AppellationMerit | `appellation_merit` | `0x39C5A280` | `0x1A0` |
| ScheduleItem | `schedule_item` | `0x39C5A4E0` | `0x200` |
| ArchePass | `arche_pass` | `0x39C5A740` | `0x240` |
| Instance | `instance` | `0x39C5A9A0` | `0x280` |
| Appellation | `appellation` | `0x39C5AC00` | `0x2C0` |
| Achievement | `achievement` | `0x39C5AE60` | `0x300` |
| TaggedBuff | `tagged_buff` | `0x39C5B0C0` | `0x400` |
| FestivalZone | `festival_zone` | `0x39C5B320` | `0x460` |

Zone handler `0x39C58D30` resolves `zones.id` through `0x39C763F0`, then inserts
descriptor offset `0x24`, the signed Zone key. Missing descriptors do nothing;
a resolved zero or negative key is still inserted. `ZoneWorldContent` therefore
loads the 319 ID-to-key mappings independently of scene lookup by key, and
`ZoneWorldContentPolicy.ExcludedZoneKeys` keeps this domain separate from
`ExcludedIds(kind)`. Festival groups remain `u16`; other kinds use full 32-bit IDs.

Rebuild `0x39C56B80` exposes enabled, non-excluded appellation merits in the map
at `0x1C0`. The source accessor is `0x39C75890`; the enabled flag is bit zero of
descriptor offset `0x78`. Selected-stamp lookup therefore loses the merit's
stat contributions when that merit is excluded. The source catalog still retains
all enabled merits for subsequent fresh connections.

Tagged-buff descriptor loader `0x39AD2160` reads `id, buff_id, tag_id` with
`sqlite3_column_int`. Its accessor `0x39ABE290` returns parallel row-ID and
eight-byte descriptor arrays. Rebuild excludes by **tagged-buff row ID**, then
creates buff-to-tag and tag-to-buff sets at `0x420` and `0x440`. Duplicate
relations collapse after filtering; one remaining row preserves the relation.
Zero IDs and references to missing buff templates remain in these runtime sets.
`ZoneBuffContent.TaggedBuffs` retains the raw rows. `HasRuntimeTag`, `RuntimeTags`
and `RuntimeBuffs` read the filtered relation view in both directions.

Lookup `0x39C54240` tests a buff/tag pair in `0x420`; `0x39D28A40` uses it for
tag visitors. `0x39C541D0` supplies the tags used by `0x39D2C030` to maintain
active tag counters. Conditional stat modifiers consume active tag presence.
The separate loader `0x39B42380` attaches unfiltered template tags only to
existing buff definitions, at descriptor offset `0x3E0`. Passive suppression
helper `0x39D2B2B0` reads this template set directly. Applying runtime exclusions
to that set would incorrectly remove passive exceptions. The replacement keeps
`HasTag` for template checks and uses `HasRuntimeTag` for active conditions.

`ZoneSession.JoinAsync` accepts the configured `worldContent` catalog, builds
`ContentPolicy` after join validation, and applies filtered stat content before
creating the registry or relationship evaluator. Filtered sessions without the
catalog fail before invoking the scene factory. Views share immutable definitions
and preserve their unfiltered tag and merit sources; rebuilding one view cannot
change another session. The startup validator also builds these supported views.

Native validation executed the actual registration, category dispatch, Zone
lookup, configuration rebuild, tag-set construction and tag queries for 52
synthetic cases and one configured-data case. All 954 exclusion sets, 52,931
active tag relations in both index directions, 1,908 tag queries and enabled
merit indexes matched.
The configured inputs contained 7,405 World descriptors, 319 Zone mappings,
52,542 tagged-buff rows and 114 merits (109 enabled). The `jury` filter resolved
to tagged rows 55823 and 64349, both tag 3640, with buff IDs 3621 and 25918.
The `Open_2` Zone filter resolved catalog IDs to their actual Zone keys.
The read-only source database hash remained unchanged.

All 294 tests passed. Added coverage verifies category isolation, native ID
domains, duplicate tag relations, passive exceptions, selected-merit stats,
filter application before unit creation, missing-catalog rejection and fresh
content views across fragmented TCP reconnects.

The probe supplied empty unrelated crafting/quest/schedule/instance/loot/achievement
catalogs and normal allocation/CRT helpers. It did not emulate the dependencies
for those systems, and this validation does not establish their filter parity.
Their filtered indexes and gameplay consumers, plus native runtime hot reload,
remain pending. Native `0x39C549D0` does not clear the tagged-buff exclusion set;
no WZ hot-reload packet has been established. Replacement sessions build a fresh
policy for each connection rather than exposing a same-process reload operation.

## Achievement dependency indexes

`ZoneAchievementContent` loads the inputs needed by the achievement section of
native rebuild `0x39C56B80`. These index definitions deliberately describe
dependencies; they do not yet load or evaluate achievement unit requirements,
localized presentation, completion counts or rewards.

| Source | Native loader | Descriptor stride | Index fields |
| --- | --- | --- | --- |
| `achievements` | `0x39C62FF0` | `0x68` | ID `+0`, subcategory `+4`, parent `+0x30`, season-off flag `+0x60` bit 3 |
| `achievement_sub_categories` | `0x39C63960` | `0x20` | ID `+0`, reward achievement `+0x18` |
| `achievement_objectives` | `0x39CE1530` | `0x38` | ID `+0`, owner achievement `+4`, record `+8` |
| `pre_completed_achievements` | `0x39C62E40` | `0x0C` | ID `+0`, completed achievement `+4`, dependent achievement `+8` |
| `char_records` | `0x39CE1330` | `0x10` | ID `+0`, kind `+4`, value1 `+8`, value2 `+0x0C` |

All numeric inputs use `sqlite3_column_int` with NULL-to-zero conversion and
32-bit truncation. The season flag uses the first text byte: `T`, `t` or `1`.
The source enum identifies record kind 9 as `complete_achievement`; its `value1`
is compared with unsigned achievement IDs. Negative stored values therefore keep
their complete 32-bit ID pattern.

The rebuild first constructs the available-achievement map at `+0x320` after
applying achievement exclusions. `season_off` does not remove an achievement
from this map. It then builds six maps of ordered vectors:

| Offset | Key | Values and eligibility |
| --- | --- | --- |
| `0x340` | Parent achievement ID | Available children with a nonzero parent; the parent need not exist or remain available |
| `0x360` | Achievement ID | Objective IDs whose owner and record exist; kind-9 records additionally require their target achievement to remain available |
| `0x380` | Achievement ID | Completed-achievement prerequisites, only when both endpoints remain available |
| `0x3A0` | Completed achievement ID | Dependent achievements from the same eligible prerequisite rows |
| `0x3C0` | Subcategory ID | Available achievements whose subcategory exists, excluding season-off entries and the subcategory's own reward achievement |
| `0x3E0` | Record ID | Owner achievement IDs from the eligible objectives |

Vectors preserve descriptor iteration order, duplicate values and self-edges.
They are not sets and are not sorted by achievement priority. Missing lookups
return empty lists. Queries `0x39C54080`, `0x39C540F0` and `0x39C54160` expose
the child, prerequisite and subcategory vectors respectively. Their sentinel
`0x3D8FB2F8` is zero; assembly at `0x39C574B3` confirms the parent field is the
key passed to the `0x340` map.

`ZoneWorldContentPolicy.ApplyTo(ZoneAchievementContent)` creates the immutable
index. `ZoneSession.JoinAsync(achievementContent: ...)` builds it after accepted
join filtering and before scene construction. The source rows remain available
for a subsequent connection with a different filter. The executable also loads
these rows and rebuilds the indexes during startup validation.

Validation executed the original rebuild, descriptor accessors, subcategory and
character-record lookups, vector construction and three public query functions.
Across 59 synthetic and three configured-data cases, all 372 dependency indexes,
108,945 ordered edges and 2,145 public query results matched. The managed probe
also checked 264,372 lookups, including absent keys. The configured source had
5,122 achievements, 44 subcategories, 12,750 objectives, 1,533 prerequisite rows
and 9,639 character records. With no exclusions, 12,748 objectives were eligible;
the three configured achievement filter names reduced the available achievement
count to 5,080 and eligible objectives to 12,658. The source database SHA-256
remained unchanged.

The actual executable also completed `--check-startup` against a fragmented
synthetic World peer using the configured database and all three achievement
filter names. It reported 5,080 available achievements, exited successfully and
sent no premature `ZoneLoaded`. This check did not connect to the running World
or modify player state.

All 299 tests passed, including native-width SQLite inputs, duplicate and cyclic
edges, missing descriptors, season-off and reward rules, completion-record
dependencies, immutable results and fragmented TCP reconnects. Unrelated native
crafting, quest, schedule, instance and loot catalogs were empty in the emulator.
This verifies achievement dependency indexing, not those systems or authoritative
achievement completion and reward delivery.

## Loot catalogs and filtered NPC packs

`ZoneLootContent` follows the native loader sequence inside `0x39BB6890`:

1. `0x39BB48C0` creates declared packs from `loot_packs`, then joins `loots` to
   `items` by item ID and adds item entries to packs/groups.
2. `0x39B432C0` applies `loot_groups` settings only to existing groups.
3. `0x39B43500` applies `loot_actability_groups` ranges only to existing groups.
4. `0x39B97850` builds NPC-to-pack vectors from `loot_pack_dropping_npcs`.

The native loot join has no extra ordering clause. Missing item definitions
contribute no row, while an item row with a missing pack declaration creates an
implicit pack through `0x39BB4740`. Declared packs survive even when empty.
Pack and group IDs use signed 32-bit map ordering; NPC and item IDs retain their
unsigned 32-bit patterns. Grade is narrowed to a byte and Zone group to `u16`.
All integer reads use `sqlite3_column_int`, including NULL-to-zero and truncation.
Boolean fields recognize the initial `T`, `t` or `1` byte.

Pack metadata contains the `war_drop` flag and `extra_gain_act_group_id`.
Assembly at `0x39BB47D1` and `0x39BB47D8` copies uninitialized stack fields for
packs created without a declaration; the called map constructor `0x39BB1940`
does not initialize them. The replacement exposes `Metadata == null` for these
packs instead of inventing deterministic values. The configured data has 35 loot
rows across three such packs: 5716, 9773 and 15384. Their item/group contents and
NPC mappings are retained. Rolling that depends on the missing metadata has not
been implemented.

Group creation `0x39BAF490` establishes the following fields relative to the
group value (the native map node's value starts at `+0x20`):

| Offset | Field | Initial behavior |
| --- | --- | --- |
| `0x00` | Item vector | Ordered 24-byte entries; duplicate items remain separate |
| `0x20` | Zone group (`u16`) | Zero until an existing-group setting replaces it |
| `0x24` | Group drop rate (`i32`) | 10,000,000 |
| `0x28` | Total item weight (`i32`) | 10,000,000 for group zero; zero otherwise |
| `0x2C` | Has quest items | False |
| `0x30`, `0x34` | Minimum/maximum actability dice | Zero |
| `0x38` | Grade distribution (`i32`) | Zero |

Only positive group numbers accumulate item rates and set the quest flag when
`items.loot_quest_id > 0`. Weight addition wraps at 32 bits. Group zero keeps
its fixed denominator even when group metadata changes its drop rate; negative
groups do not accumulate weights. Later settings replace earlier metadata without
creating a group or changing its item vector, denominator or quest flag.

Each item entry contains item ID `+0`, grade `+4`, minimum amount `+8`, maximum
amount `+0xC`, drop rate `+0x10`, always-drop `+0x14` and World-message flag `+0x15`.
`0x39B53180` appends the complete entry. The replacement preserves raw rates,
amounts and flags; it does not normalize them into percentages or clamp ranges.

Filtered rebuild `0x39C55750` walks the original NPC vectors at ItemManager
`+0xC0`, skips pack IDs in the signed exclusion set at configuration `+0x180`,
resolves remaining packs from ItemManager `+0xA0`, and appends pointers to the
configuration map at `+0x1E0`. It preserves duplicate mappings and their order.
NPCs with no resolvable, non-excluded packs have no entry. The source SQL ignores
`default_pack`; neither that flag nor NPC template existence gates this index.
Filtering does not remove definitions from the shared pack catalog.

`ZoneWorldContentPolicy.ApplyTo(ZoneLootContent)` creates this immutable index.
`ZoneSession.JoinAsync(lootContent: ...)` builds `Loot` from the accepted join
filter, and each reconnect uses the original source catalog. The executable
loads and filters loot during startup validation.

Validation executed the original four loaders, pack/group constructors, item
append function, category dispatch and NPC filter rebuild. SQLite calls were
supplied by a read-only connection using the original SQL queries; allocation,
memory and logging helpers were supplied by the isolated emulator. Across
synthetic and configured inputs, all 8,737 packs, 13,448 complete groups and
32,980 complete item entries matched. Only the two uninitialized fields of
implicit packs were excluded from value comparisons and represented as missing
metadata. All 47 filtered NPC maps, 70,336 ordered pack links and 26,659 NPC
lookups matched with zero mismatches.

The configured catalog contains 8,731 packs, 13,441 groups and 32,971 items.
The 13,835 source NPC mapping rows cover 4,407 NPC IDs; resolving existing packs
leaves 11,825 mappings across 3,583 NPC IDs. Applying all four configured loot
filter names leaves 11,446 mappings. The actual executable also passed startup
against a fragmented synthetic World peer with these filters, without emitting
`ZoneLoaded`. The source database SHA-256 remained unchanged.

All 305 tests passed. New tests cover native default/overflow behavior, missing
and empty packs, settings replacement, inner-join omission, raw scalar widths,
duplicate NPC mappings, zero/negative pack IDs, immutable views and reconnect.
Loot generation, dice/grade selection, quest eligibility, corpse ownership,
client loot state and persistent item delivery remain pending.

## Incremental GM modes and command parameters

`WZGmCommand` is opcode `0x004F` in `0x3936F740`. Body serializer
`0x3936C5F0` writes the three-byte unit ID through `0x3937F0F0`, an unsigned
command byte, and a length-prefixed parameter string with a `0x3FF` byte capacity.
The in-memory command slot is four bytes, but only one byte travels on this link.
World now uses the shared complete body writer. The reader retains binary bytes
and requires full consumption before dispatch.

`0x393639D0` bounds the parameter C string with `strnlen(..., 0x400)`, calls
`Data::Load`, and resolves the unit only after successful parsing. Missing units
and syntax errors do not mutate state. Its only active selectors are 2, 3, 30,
49, 142, 143 and 144. Other selectors are ignored; they do not represent generic
spawn/health commands on this link. The replacement implements selector 30 and
explicitly rejects the other active selectors until their gameplay services exist.

Mode handler `0x390B2FB0` requires children 0 and 1 of the parsed root. It resolves
the first value through `0x39BFB140` and writes the second value's low signed byte
to unit offset `0x7E40 + mode`. It does not check unit kind, invalidate stats,
change HP/MP, call the actor service, or emit an acknowledgement. The name table
at `0x3A5A2B30` contains, in index order: `invincible`, `cooldown`, `combatlog`,
`almighty`, `debug_plot`, `test_siege`, `live`, `zone_permission`, `test_integration`.
Name matching uses `XlCompareStringI`. Native `0x3935D1E0` uses mode 6 only for
characters; the managed relationship evaluator now reads the current mode view.

The parameter grammar comes from `xlcommon.dll` at image base `0x33000000`:

- `Data::Load` at `0x330061D0` skips a UTF-8 BOM and invokes the line parser.
- `0x33006000` tracks indentation; `0x33005F60` parses lists. Whitespace nests
  values, while commas restore the enclosing list's parent. Parentheses permit
  multiline lists. `live,1` and `live\r\n1\r\n` supply two root children.
- `0x33004C90` handles quoted strings, `#` comments, top-level `#?` metadata and
  indented block values. Quotes permit tabs and preserve backslashes except for
  escaped quotes/backslashes. Newlines inside quotes normalize to LF.
- `0x33004860` consumes lookahead and rejects tabs outside quoted values.
  `0x33006100` tests remaining bytes after newline lookahead, so it skips a final
  one-byte line without a terminator. Consequently `live\n1` lacks its second
  argument, while `live\n1\n` supplies it.
- `Data::IntValue`/`Int64Value` at `0x33003230`/`0x33003250` use decimal-prefix
  conversion through `XlAtoI32`/`XlAtoI64` (`0x33020EA0`/`0x33020EB0`). Arithmetic
  wraps; Boolean words and `0x` prefixes are not special. The signed-character
  digit lookup at `0x3316E2F0` reads zero bytes for all negative character indexes,
  causing high-bit bytes to contribute zero digits. This behavior is retained.

There is one deliberate initialization difference: the original loader leaves
unused indentation slots uninitialized. Block parsing can read a slot before the
line parser assigns it. A controlled native probe changed the same input's child
count by priming the previous call with a different indentation. The replacement
initializes unused slots to zero per command; previously assigned slots retain
their native role in block termination. No command depends on prior stack contents.

Independent validation executed the original parser on 8,520 synthetic inputs.
Complete value trees, numeric projections and parse acceptance matched for 8,490
inputs; 30 block-syntax differences were identified and reported separately for
the uninitialized indentation behavior. Re-executing the original parser in
isolated memory with unused indentation slots explicitly zeroed matched all 8,520
inputs, including complete value trees and both numeric widths, with no differences.
This isolates the initialization policy from the parser's grammar and conversions.
A second oracle executed the original
mode handler in isolated emulated memory, using original `Data` results and native
lookup/conversion helpers. All 4,954 mode-command cases matched, including every
low byte for all nine modes, case variants, malformed/missing arguments, wrapping
values and binary numeric values. Bytes outside the mode array were unchanged.

All 313 tests passed. Wire tests cover complete bodies, every truncated prefix,
trailing bytes, binary values and capacity. Lifecycle tests cover all unit kinds,
unchanged initial snapshots, death, duplicate creation, removal, immediate
relationship changes and fragmented TCP reconnects. World tests verify that direct
mode changes and console commands relay once and persist into subsequent snapshots.
World also ignores a missing command target instead of changing the caller's mode.
Other mode consumers, GM skill/NPC commands and production simulation
readiness remain unfinished. No live servers, clients or player state were used.

## GM attribute overrides and native numeric limits

`WZGmCommand` selectors 2 and 3 now set and clear current attribute overrides.
The command body and shared `Data` grammar are unchanged. Native handlers
`0x390B2E50` and `0x390B2F10` require two and one top-level values respectively;
extra values are ignored. Selector 2 reads `Data::Int64Value`. The case-insensitive
lookup at `0x39B25840` accepts 255 names across 290 slots, ending with
`extra_gain_by_mine_act_group` at 289. Unnamed slots and numeric IDs are not command
names. Unknown names, malformed parameters and absent units have no effect.

`0x39BF3480` stores the raw signed 64-bit value, enables the slot and increments
its cache version. `0x39BF34B0` disables the slot and increments its version even
if already disabled. Neither handler calls the HP/MP setters. In the replacement,
each accepted command publishes an immutable override set and recalculated stat
snapshot together. This also invalidates cached NPC attribute queries. An explicit
zero remains enabled; clearing it restores normal calculation. Initial World
snapshots do not contain these overrides. Duplicate snapshots retain current
state, while removal and a new connection begin with no overrides.

The shared getter at `0x39BF6C20` tests the override before any formula, modifier,
cache, template or limit access and returns its raw value. This order also applies
to formula-buff dependency queries and attributes whose ordinary calculation is
not yet implemented. The replacement follows this order and carries overrides
through equipment/activation, buff creation/update/removal, learned-passive and
ability reset, level-dependent invalidation, death and resurrection paths.
Other attributes still follow their ordinary dependencies and limits.

Extreme overrides make native numeric behavior observable through derived stats:

- `0x39B1B630` promotes the f32 formula result to double. The core and healing
  branches of `0x39BF7150` truncate to signed i32; HP/MP add the exact rounding
  constant `0.05000000074505806` and truncate to signed i64. Invalid conversions
  produce the corresponding minimum signed integer, including NaN and overflow.
- `0x39BF6C20` passes only the signed low 32 bits of the base getter's result to
  `0x39BF3380`. Direct overrides return before this narrowing. Flat accumulators
  wrap at 64 bits; percentage accumulators and stack products wrap at 32 bits.
  Combined percentage conversion uses the native signed i64 indefinite value.
- HP/MP setters `0x3925AB00` and `0x3925AC60` first map negative input points to
  zero; otherwise they take the smaller of the input and the wrapped product
  `maximum * 100`. That second branch can produce negative points.
  Resurrection `0x3925E8E0` passes each wrapped product back through its setter,
  so a negative product becomes zero. For example, a raw maximum of `long.MaxValue`
  clamps input 1 to -100 but refills to zero on resurrection.

Independent probes executed the original name lookup for all 255 names in both
lowercase and uppercase, and the original set/clear handlers for 1,054 cases.
Each active override was read through the original getter with both cache flags;
unrelated unit bytes were verified unchanged. Every managed result matched.
Another probe exercised 4,980 original base-conversion/getter/combiner cases with
controlled formula results, neutral secondary modifiers and identity limits, plus
246 original point-setter/resurrection cases. All managed results matched after
the probe's formula-return fixture was corrected to promote f32 to double.

All 322 tests passed. New regressions cover raw values, dependencies and cycles,
all seven unit kinds, retained overrides through invalidation, atomic failure,
point arithmetic, NPC query cache invalidation and fragmented TCP reconnects.
Older failure-atomicity tests now inject invalid formula inputs or modifier types;
overflow is native arithmetic rather than an expected exception. Probe artifacts
remain outside the repository. No live server, client or player state was used.
GM skill/NPC commands, unimplemented ordinary attributes and production gameplay
services remain incomplete; these results do not establish full Zone parity.
