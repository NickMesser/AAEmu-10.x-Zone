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

Production currently refuses the incomplete actor profile before model-class compilation,
per-entity cloning or `OnInit`. An initial real-entry control repeatedly reached the same
missing attachment service, causing enough receive backlog to time out the replay sender before
player entry. A subsequent control proved model-class construction but exposed its cold compilation
cost before an already-known refusal. The earliest admission check avoids both costs. Fully configured
test/service compositions exercise the complete initialization and teardown path; the production
gate stays closed until its required character/component owners exist.

Initial type-5 CPuppet identity/control state is explicit partial admission. The independent
AI-object radius starts at zero, as in its constructor; model radius, collider radius and AI graph
pass radius are distinct. Complete Puppet parameter blocks, its persistent stick operation,
physical/animated-character components and perception remain unfinished.

## Real services and frame execution

The script host binds existing goal pipes, actor indices, attention targets, local signals, NPC
aggro enablement, NPC AI time events and interaction state. `HasSignalActor` admits only exact
script owners. The original scheduled AI-time-event step can run for these owners while the
full `HasAiActor` gameplay gate remains closed.

`ZoneManagedNpcScriptEffects` owns original entity timers, default-state server update callbacks,
script update intervals and independent CActor health fields. Initial health is 0 and maximum
health is 10 from the CActor constructor; these are not substituted from World Unit statistics.
Later actor-health producers must copy their actual fields into the owner.

The two `SetScriptUpdateRate` floats are retained separately. Event 44 selects the current interval;
its producer is not inferred from a combat flag. `OnUpdate` uses the original strict `>` comparison
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
Absent implementation is not evidence of an absent component. Both remain named refusals.

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
physical admission and the consumer connecting these wishes to NPC movement publication are
still required before the host can advertise physical NPC navigation.

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

Local evidence is under `D:/aaemu-runtime/research`: `zone129-original-script-controls.json`,
`zone129-original-script-comparison.json`, `zone129-production-script-host-bootstrap.json`,
`zone129-navigation-runtime.json`, `npc-script-proxy-services-source.md` and
`npc-oninit-gates-evidence.json`. These runtime artifacts and original assets are not repository inputs.

Next: own character slots/attachments and entity event receivers/collider layers; finish original
NPC constructor parameters and physical bodies; connect AI navigation wishes to the single scene
movement authority; port smart-object state, posture, perception, acquisition, leash, and combat
services; then admit each supported behavior based on its actual dependencies. Keep general
entity components/FlowGraphs, dynamic bodies/vehicles and remaining Lua compatibility separately
visible in [the module removal task file](tasks/cryengine-removal.md).
