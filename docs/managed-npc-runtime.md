# Managed NPC scripts and navigation

Managed startup now composes the original NPC source scripts through `ZoneManagedNpcRuntime`.
It creates no CryEngine or native Lua VM. Original assets and the content database remain external
inputs. Full NPC gameplay admission is still closed.

## Script execution and ownership

`IZoneLuaRuntime` preserves the existing reference/callback boundary. The native comparison host
uses `ZoneLuaRuntime`; the managed host uses the pinned NeoLua source compiler through
`ZoneManagedLuaRuntime`. Stable NeoLua 1.3.19 failed environment construction against .NET 10's
LINQ signatures in an independent probe, so the dependency is pinned to 1.3.20-beta.1.

The managed boundary owns references, protected calls, method receivers, retained callback values,
byte strings, and source loading. Source bytes are retained through a reversible byte-to-character
mapping, including the original invalid UTF-8 comment in `blackboard.lua`. Integer operations keep
LNUM's 64-bit precision, overflow promotion, exact division and floor modulo. Decimal literals
round once, including subnormals; hexadecimal 64-bit integers retain their signed bit pattern.
An unrepresentable double `2^63` remains a double at the managed host boundary, correcting the
native comparison wrapper's clamped export. Void invocation does not export discarded return values.

Owned coroutine workers serialize resumption and release completed/suspended workers on retirement.
The covered original NPC dependency closure contains no coroutine calls. Scene APIs with thread
affinity have not been admitted from coroutine worker callbacks. This implementation is **not full
Lua 5.1 compatibility**: original `.alb` bytecode, environment/debug APIs, implicit vararg `arg`,
the native RNG sequence, and remaining library/language edge cases still need independent controls.
Compiler CLR syntax is disabled; filesystem, process, package and native-library globals are omitted. The scene
injects a narrow `os.time` callback for the original `math.randomseed(os.time())` bootstrap.

`ZoneAssetScriptSource` reads original script bytes through the configured asset order, with a
16 MiB per-file limit and canonical game paths. An explicit `scriptRoot` instead selects the
existing loose-directory source. The original loader keeps its case-sensitive cache and reload
semantics. Its failure counter includes nested failures whose outer scripts ignore return values.

The host bootstraps original `common.lua`, `AIConfig.lua` and `npc.lua`, then validates their actual
registries and callbacks. Current zone-129 source execution loads 73 filenames, creates 20 goal
pipes, seven track patterns and 20 combat classes. Missing game APIs raise named refusals.

## Model classes and entity initialization

The native actor table is generated from database data; a mesh filename is not a Lua script name.
`ZoneNpcActorModelScripts` follows `CEntityScript::Load`/generator `320293A0` and class clone
`32094AE0`/`3202C5C0`. It preserves source-vector stance positions:

| Movement group | Source row position |
| --- | ---: |
| Relaxed | 1 |
| Combat | 0 |
| Stealth | 5 |
| Crouch | 6 |
| Prone | 7 |
| Swim | 2 |
| Fly | 8 |

The generator preserves float multiplication before native six-decimal formatting. It executes
original `npc:Create("npc_<modelName>", sourceActor)` once per cached class. Each NPC uses original
`common.new`, original server `OnInit`/`OnReset`, its AI character constructor, database
`SetParamX2` closure, and initial `spawning` behavior constructor. Incomplete stance vectors,
unresolved model classes and callback failures refuse admission.

Each attempt gets a fresh managed entity ID and proxy owner, independently of World Unit/NPC IDs.
The proxy key feeds its actual registry activation state. Prepared behavior is registered before
constructors run; every callback rechecks the exact catalog, Unit, actor and binding generation.
Failed initialization releases effects, bindings, behavior, entity references and proxy ownership.
Signals cannot keep operating on a removed actor or a replacement with the same numeric Unit ID.

Production queues each incoming generation and performs at most one original initialization per
scene frame. Pending wrappers own no Lua entity, body or behavior and cannot receive signals.
The queue preserves received activation/target requests, publishes readiness after actual
construction, and cancels callbacks from retired generations. This scheduling policy keeps a
large incoming NPC batch from invoking all Lua constructors synchronously. It is a replacement
work-count bound, not a recovered native scheduling algorithm or a per-frame time guarantee.
Cold admissions also yield when a World packet was dispatched since the previous frame; current
physics/scripts/AI continue on those frames. Continuous ingress fairness remains performance work.
See [admission ownership](managed-npc-admission-queue.md).

The production construction profile now composes actual descriptor-written CActor parameters,
installed animated dynamics, independent health, character slots, collider events, smart objects,
AI proxy poses and supported skill request/acknowledgment owners. Unsupported profiles unwind
with their precise dependency. A script-ready owner still does not open the full gameplay gate.

Initial type-5 CPuppet identity/control state is explicit partial admission. The independent
AI-object radius starts at zero, as in its constructor; model radius, collider radius and AI graph
pass radius are distinct. Complete Puppet parameter blocks, its persistent stick operation,
full animated-character state and perception remain unfinished. Ground bodies and the verified
initial collider profiles have independent owners, described below.

The descriptor and frame contracts are detailed in
[actor construction](managed-npc-actor-construction.md),
[AI proxy pose](managed-npc-ai-pose.md),
[reference/look objects](managed-npc-point-objects.md),
[casting fields](managed-unit-casting.md),
[skill execution](managed-npc-skill-patterns.md) and
[skill geometry](managed-npc-skill-geometry.md).

## Real services and frame execution

The script host binds existing goal pipes, actor indices, attention targets, local signals, NPC
aggro enablement, NPC AI time events and interaction state. `HasSignalActor` admits only exact
script owners. The original scheduled AI-time-event step can run for these owners while the
full `HasAiActor` gameplay gate remains closed.

`ZoneManagedNpcScriptEffects` owns original entity timers, default-state server update callbacks,
script update intervals and independent CActor health fields. Initial health is 0 and maximum
health is 10 from the CActor constructor; these are not substituted from World Unit statistics.
Later actor-health producers must copy their actual fields into the owner.

The two `SetScriptUpdateRate` floats are retained separately. Event44 selects the current interval
from actual Unit combat transitions, including death. Generation leases subscribe after binding
and unsubscribe before retirement; initial interval selection is not inferred by polling a flag.
`OnUpdate` uses the original strict `>` comparison
and one callback per frame. Entity timers replace matching IDs, preserve signed milliseconds and
float callback arguments, and remove the entire `deadline <= now` batch before dispatch. Timers
created by callbacks wait for the next frame.

Managed frames advance the scene clock once at frame start. Entity timers use precise accumulated
`TimeSpan` ticks converted to microseconds, rather than truncating an accumulated float. Physics
steps once, poses publish, entity script timers/updates run, AI pumps, and the existing simulation
tick follows. This is an explicit replacement phase order. Callback exceptions and nonthrowing
`System.Error` reports fault that owner; the host retires its provider generation and records the
refusal while other actors remain available.

The original dedicated `actor.SetMovementTarget` binding is an empty leaf after decoding its
three vectors and speed (`39310CA0` through actor table slot 192 to `3930BEC0`). It does not change
AI movement controls. Attachment hiding and collider mode are different: the former requires a
real slot/attachment inventory, and the latter sends entity event 43 to the collider arbiter.
Absent implementation is not evidence of an absent component. The composed service now owns
initial character lookup and the actual event43 receiver instead of replacing either with an empty call.

### Character slots, collider effects and bodies

`ZoneManagedCharacterSlots` loads the actual World model's CDF/CHR and compiled-bones0x801
inventory before publishing slot0. The dedicated `zone` command starts a server context with
client flag4 clear; the original attachment factories consequently publish only CA_BONE.
That branch is explicitly selected by the host. All 32 zone-129 character model paths load from
the original archive: 119 authored attachment declarations, 43 bone sockets, 42 supported unbound
visibility updates and one retained particle-effect refusal. `Animated LAW` is actually absent
from every initial inventory. Equipment changes, bound objects and animation remain separate.
See [character resource contracts and source evidence](managed-character-slots.md).

`ZoneAnimatedColliderController` preserves seven request layers, disabled priority, frame/reset
gates and commit-before-physical-lookup ordering. Its arbitration and defined raw parameter bytes
match 21 controls executed from the original `cryaction_dedicate.dll` code in an offline emulator.
The embedded control fixture identifies the binary hash and declared external callbacks; this
is not evidence that all physical consumers are implemented.

The real event43 receiver delivers the signed script mode at layer3. The actual living-body sink
admits uncontended modes1/4: action_move0 clears the requested wish while preserving actual motion,
inertia/acceleration and active state are applied, and geometry bit2 changes target sweep/overlap,
stance-clearance and navigation queries. Modes requiring auxiliary bodies, other collision masks,
type26=true or interleaving with queued body commands refuse before body mutation. Merely storing
the native flag blocks would not supply those effects.
An unported body-effect refusal seals that collider component while retaining its committed
selection for diagnosis. Repeating an identical request cannot report success through the native
unchanged-request shortcut after a missing body consumer; a new admitted owner is required.

`ZoneManagedNpcPhysics` creates living bodies on the scene's existing backend using the actual
`Unit.Snapshot.ModelId`, authored base scale, initial stance, cubed mass, dynamics and signed10-bit
physical material conversion. `Npc.Model` is separate cached template metadata and can differ.
Motor leases accept velocity/stance requests and copy status; they never step physics or commit
Unit poses. Tilted transforms, parent/seat ownership, extra boxes, flying/underwater profiles and
live physical scale changes retain explicit admission gates.

`IZoneManagedNpcScriptLifecycle` binds component leases after actor adoption and before OnInit.
The composed service owns character visibility, collider receiver, smart-object registration,
independent actor health and entity timers. Failure and retirement unwind all of them, including
their physical body, before releasing the script binding and proxy. Behavior smart-object changes
reach the same real state owner as Lua, with separate literal-add and expression-modify semantics.

World buff-removal and casting request serializers and a bounded posture/action-cache owner are
available independently. Casting requires the actual cast/current-skill/channel tuple; posture
requires actual animation-driver presence, resolved actions and the NPC skill-condition inventory.
Neither is inferred from missing components. Bounded ClearCombat owns the fresh AI-unit attribute
restore record and empty-threat path, preserves attention/attack-faction flags, and sends the actual
combat notification. Pending flying restoration or nonempty threat retains a refusal. Full combat,
flying-state restoration, position checks
and animation graph behavior remain separate admission dependencies. See
[game-service source contracts](managed-npc-game-services.md).

## Navigation replacement

The original navigation files are under `game/worlds/<world>/paths/%03d_%03d/`, not the zone root.
Zone 129's six cells contain 96 tile triplets. Streamed area/vertex/network readers apply the
original `(tile - 4*zoneOrigin)*256` translation, preserve local IDs, weld vertices by native XY
rules, and import paired directed links without inventing border connectivity or replacing the
separate dedicated loader's global safe root.

The combined publication contains 146,696 nodes, 231,626 link pairs, 69,539 obstacle vertices,
2,214 forbidden polygons, 2,214 designer-forbidden polygons and 155 modifiers. Two type-4 nodes
retain explicit missing-modifier diagnostics. A topology-only original entry-area route crosses
38 nodes in 134 expansions; that probe used declared clear fixture queries and is not physical
route acceptance.

`ZoneNavigationTiledGraph` supplies bounded A* over actual directed topology and signed-centimeter
pass clearance, triangle endpoint binding, forbidden areas and loaded tile coverage. Its Euclidean
cost is a replacement policy; native water, exposure, smart-object and steering costs are incomplete.
Copied results are tied to one graph publication. Tile retirement invalidates them and immediately
cancels followers' queued velocity wishes.

`ZoneManagedNavigationQueries` uses the existing backend's applied living dimensions, support,
positive overlap and full-body sweeps. Callers must certify terrain and collision coverage for the
entire requested region. Missing/unsupported coverage cannot become clear space. AI pass radius
does not become collider radius. Length, support spacing/count, rise/drop and normal thresholds
are explicit conservative replacement inputs. Line of sight uses bounded terrain/static rays.
Native X2 walkability, stair acceptance, moving supports and complete visibility remain unproved.

`ZoneNavigationPathFollower` only submits horizontal movement wishes through an exact-generation
`IZoneNavigationLocomotion` owner. It never steps physics or writes a Unit pose. Production NPC
route admission, steering and outgoing CActor metadata are still required before the host can
advertise physical NPC navigation. The host now composes the
[single-step NPC body publication decorator](managed-npc-movement.md): real Unit/spatial/AI
poses publish after the shared physics step, while missing replication metadata suppresses wire output.

## Evidence and next work

Standalone tests cover Lua arithmetic/bytes/references/callbacks/coroutines, authored initialization,
model generation, real goal/control phases, provider failure/retirement, NPC scheduled timers,
entity update/timer batches, topology, A*, publication retirement, and applied-body queries.
Five original script profiles (NPC templates 389, 515, 510, 8123 and 8173) agree on all 657 recorded
API calls and the compared parameters, callbacks and state transitions across native/managed VMs.
That oracle uses a declared generic/recording fixture and does not establish C++ NPC gameplay parity.
The separate production-host bootstrap uses actual managed catalog bindings.

The final 500-test build's real zone-129 replay reaches readiness with 80 modules and zero
CryEngine/native Lua DLLs, admits player 387 grounded at local `(1172.5996,1646.5,366.3365)`,
and retires cleanly on World EOF. The control takes about 107 seconds including the incoming
NPC burst; startup/receive throughput remains a performance acceptance task. Artifacts are
`script-entry-{zone.log,replay.log,modules.json,verification.json}` and
`captures/script-entry-output.bin` under the local runtime directory.

The NPC component batch passes558 standalone tests, including the shared-body publication and
refused-collider retry controls. Its fresh native-free entry controls keep the same grounded
player pose and clean EOF retirement. Server-timestamp-normalized movement payloads match the
earlier accepted replay byte-for-byte. The instant-input replay emitted two repeated heartbeat
rows instead of three in earlier captures; decoded position, orientation, velocity and flags are
identical. A fixed heartbeat count is not a stable acceptance criterion for this wall-clock harness.
Observed end-to-end runs of about125–142seconds leave startup/receive throughput unproven.
Artifacts are `npc-components-{entry,final-entry}-*`, `captures/npc-components-*-output.bin`,
`npc-components-final-tests.log` and `research/entry-movement-comparison.jsonl` in the runtime directory.

The final accepted build (`2b2ed94`) completes in120.625seconds with80 modules, zero forbidden
modules, player387 grounded at local `(1172.5996,1646.5,366.3365)`, one ZoneLoaded and three
movement rows for387. All three timestamp-normalized payloads match the previous accepted
replay (SHA256 `180C555000D965425C7528522AE2A5462F916907EBECCA95B4BFC1A2231EBCD8`).
The final stream contains no partial frame and no UnitLocation debug rows. World/listener exit0
and zone exit3 match the configured disconnect policy; frame/terrain/session retirement is ordered.
Artifacts use the separate historical `npc-components-accepted-entry-*` prefix and
`captures/npc-components-accepted-entry-output.bin`. The original-resource NPC control and final
558-test run cover the separate component/behavior boundaries. That build still refused
production NPC CreateProfile before class compilation; queued construction now replaces that gate.

Local evidence is under `D:/aaemu-runtime/research`: `zone129-original-script-controls.json`,
`zone129-original-script-comparison.json`, `zone129-production-script-host-bootstrap.json`,
`zone129-navigation-runtime.json`, `npc-script-proxy-services-source.md` and
`npc-oninit-gates-evidence.json`. These runtime artifacts and original assets are not repository inputs.

Local component evidence additionally includes `character-zone129-managed-validation.json`,
`npc-collider-native-controls.json` and `npc-components-original-control.json`. The last control
uses recorded WZNpcState and original resources with an explicit partial CPuppet/constructor-zero
animated dynamics profile. Templates562/5941 complete original initialization; the old control
observed state names after GO_TO_IDLE without treating a swallowed constructor failure as a
failed transition. Templates7987/8173/8177 refused the then-missing skill-pattern executor.
All component owners retire and no engine/native-Lua module loads. Unstepped control bodies do not
establish terrain, perception, combat or complete gameplay admission.

The newer `npc-components-construction-control.json` uses actual descriptor construction,
installed animated parameters, shared health, AI proxy poses, loaded skill content and independent
casting fields. All five templates complete initial original-script construction. Their GO_TO_IDLE
constructors still require AI group services (562/7987) or the position-check consumer
(5941/8173/8177). Those callback errors now retire the exact provider generation at the next
safe boundary. The control releases all nine owner families and loads zero engine/native-Lua
modules. This is initial construction evidence with unstepped bodies, not successful idle gameplay.

The construction/casting/skill batch passes663 standalone tests. The final analyzer build has
zero errors; warnings remain. Independent original-instruction controls cover42 casting cases
(34 complete handler/association comparisons plus8 helper controls),29 skill-geometry cases,
18 reference-direction/similarity cases and21 saved-attribute cases. Each oracle records its
explicit callbacks and admitted scope; these counts are not a full native gameplay comparison.

The fresh ingress-budget entry control completes in110.766seconds: player387 is grounded at
local `(1172.5996,1646.5,366.3365)`, NPC construction runs, and the post-player inventory contains
80 modules with zero forbidden engine/native-Lua modules. There is one ZoneLoaded, three movement
rows for387, no UnitLocation debug row and no partial final frame. All three movement payloads
match the previous accepted capture after normalizing only the server timestamp. World/listener
exits0 and zone exits3 with ordered retirement. The final analyzer-built application assembly is
identical to the replayed assembly; this is one stationary entry control, not a throughput benchmark
or sustained NPC gameplay test.

Runtime artifacts are `npc-native-free-final-{build,tests}.log`,
`npc-ingress-budget-entry-{zone.log,replay.log,modules.json,verification.json}`,
`captures/npc-ingress-budget-entry-output.bin`,
`research/npc-native-free-entry-movement-comparison.jsonl` and
`research/npc-native-free-batch-evidence.json`.

Next: finish Puppet/group, position-check and animated movement producers and collider command interleaving;
connect AI navigation wishes to the single scene movement authority with actual CActor outgoing
metadata; complete posture/casting/combat dependencies, perception, acquisition and leash; then
admit each supported behavior based on its actual dependencies. Keep general
entity components/FlowGraphs, dynamic bodies/vehicles and remaining Lua compatibility separately
visible in [the module removal task file](tasks/cryengine-removal.md).
