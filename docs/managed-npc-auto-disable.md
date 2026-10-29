# Managed NPC auto-disable and proxy activation

The unchanged original `scripts/ai/behaviors/x2/common/return_state.lua` calls
`AI.GetAutoDisable(entity.id)` and `AI.AutoDisable(entity.id, status)`. These are
global AI bindings over an engine entity handle. There is no authored
`actor:SetAutoDisable` alias. `ZoneNpcAutoDisable` now uses the exact actor's existing
`ZoneActorActivationState`, and the scheduled proxy phase consumes that state through
the concrete GameObject activation path. This does not open the global gameplay-ready
gate or implement unrelated GameObject extensions.

The matching original `cryaction_dedicate.dll` registrar `30545220` binds literal
`GetAutoDisable` to `30534F70` and `AutoDisable` to `30535170`. The PE registration
instructions and vtable targets are retained externally in
`D:/aaemu-runtime/research/return-auto-disable/binding-registration-evidence.json`.
The source is the original 10.x CryAction decompile, with the X2 producer in
`x2game-dev_dedicate.c`; no generic CryEngine version was substituted.

| Operation | Original behavior |
| --- | --- |
| Getter | Exactly one argument; ScriptAny handle tag3 narrows to unsigned32 entity ID. Missing entity, AI, or proxy returns boolean false. A live proxy returns the negation of its word86 being nonzero. Wrong argument count returns no values. |
| Setter | Exactly two arguments; handle arg1 and numeric tag4 arg2. The number narrows to float and then `CVTTSS2SI`. Integer zero passes signed word `-32768` to proxy408; any nonzero integer passes0. Invalid second types leave state unchanged. It returns no values. |
| Proxy constructor `30516370` | Word86 starts at1024. The existing distance-cadence allocation seed belongs to the same proxy allocation. |
| Proxy408 `30514D60` | Request0 cannot cancel a positive countdown. An accepted request stores the signed word and immediately invokes proxy80. |
| Proxy80 `30514DA0` | Positive words decrement once; negative words persist. Global `ai_UpdateAllAlways` or a nonzero word selects policy1; otherwise policy2. |
| Proxy376 `305198D0` | Reports nonzero word, including the negative persistent value. It does not consult the global override. |

Consequently `AutoDisable(id,1)` during startup grace leaves the countdown intact.
`AutoDisable(id,.999)` truncates to zero and disables automatic deactivation, as does
`AutoDisable(id,0)`. NaN, infinity and integer-conversion overflow produce the native
indefinite integer, a nonzero enable request. A global override can keep updates active
while `GetAutoDisable` still reports true because its raw word is0.

The actual NPC property producer runs after original `Server.OnInit`, before AI
registration and the character constructor. `39265E50` writes the **boolean**
`entity.PropertiesInstance.bAutoDisable = !NpcTemplate.ActivateAiAlways`.
`LoadNpcDescs39B080B0` reads SQL column4 `activate_ai_always` into bit4 of descriptor
byte656: its local descriptor starts at stack+0x60 and the flag byte is stack+0x2F0,
offset656. The existing managed 101-column template reader already loads this field.
`392ADC00` requests a boolean for the registration read, with a false output default.
The original CScriptTable typed reader also accepts numeric Lua values: numeric1 is
true,0 is false, and the original unordered comparison makes NaN false; strings fail.
`3927F610` ultimately passes0 or
`-32768` to proxy408. The helper preserves this ordering in
`ZoneManagedNpcSceneScriptServices.InstallAuthoredChildren`, before group parameters,
and checks the exact binding after every script-table operation. Missing original
`PropertiesInstance` refuses this construction profile.

Proxy+8 is an **IGameObject pointer**, rather than an IEntity pointer. Its vtable240
target `305E0C80` changes the GameObject AI activation policy and returns actual
`IEntity.IsActive`. A repeated identical policy does not reevaluate or reactivate
the entity. `305DEEC0` rejects hidden/no-AI entities first, accepts nonhidden policy1,
and checks probable visibility/distance for policy2. `305E0AD0` bypasses extension-slot
evaluation when that AI result is true; `305DFB00` then activates the entity when its
actual active bit differs. Recording the policy integer alone would miss this effect.

`ZoneManagedNpcGameObjectActivationProfile` defaults to `Unowned`. Nonhidden policy1
is independently admitted. Policy2 changes require the explicitly supplied
`MultiplayerWorldServer` profile, including the original default visibility-timeout0.
This follows the real `306F0470` StartGameContext producer: context flag2 sets
gEnv289/server, and absence of single-player flag0x40 sets gEnv290/multiplayer.
It does not infer these fields from being headless or from an earlier native geometry
oracle. `305DF1D0` returns visible for that server+multiplayer state after its disabled
timeout branch. `305DEFD0` registers `g_VisibilityTimeout` with default0. Custom timeout
or non-multiplayer visibility is outside this profile.

Actual CActor initialization `392E16B0` sets GameObject prephysics mode2 through
IGameObject168/`305E0D20`. In this admitted AI-true branch it remains enabled; the
policy callback does not perform an additional physics step. Hidden policy changes
still require the actual CActor/AnimatedCharacter extension update-slot inventory,
forced-update alternatives and their effects. Those branches explicitly refuse;
an absent renderer is insufficient evidence of an empty inventory. An unchanged
policy safely returns the already owned activity without evaluating those branches.
Custom physics suspension and general extension-slot scheduling are also unported.

`ZoneManagedNpcActor.UpdateProxyActivationPolicy()` runs once in the scheduled actor
update, after goal execution and before the proxy signal handler, matching `30518020`.
Its actual entity-active result gates that handler and subsequent proxy work. The NPC
manager's existing eight-frame proximity/activation consumer reads the same proxy
word through `ZoneNpcActivationState.UpdateActivation`; turning auto-disable off
therefore keeps an out-of-sight NPC active without creating another scheduler.
Entity activity, AI enablement and script-update eligibility remain distinct owners.
Retired bindings and actor objects cannot write a replacement generation. As in the
original global binding, a newly resolved entity handle always addresses its current
entity; the binding does not turn an engine handle into a historical generation token.

`ManagedNpcAutoDisableTests` has21 focused controls, including the complete checked-in
[`npc-auto-disable-native-controls.json`](../tests/AAEmu.Zone.Tests/Fixtures/npc-auto-disable-native-controls.json)
oracle comparison. That fixture retains133 executed original machine controls:
98 word setter/update cases,27 full getter/setter binding cases, and8 concrete
GameObject activation-policy cases. All compared fields agree. The independent
Unicorn runner executes the original PE instructions with explicit FunctionHandler,
lookup, global/CVar and entity callbacks; a deliberately nonempty uninterpreted
extension vector proves that the admitted AI-true path bypasses it. It does not
claim hidden extension behavior or a live-engine lifecycle oracle. The runner and
matching PE SHA256 are retained in
`D:/aaemu-runtime/research/return-auto-disable/native-controls.py` and its JSON output.
No native engine or Lua DLL is loaded by these controls or by the managed tests.

Typed boolean conversion is independently backed by19 executed original
CScriptTable35512DC0 ->CScriptSystem3550D910/3550D4E0 controls in
[`npc-script-any-boolean-native-controls.json`](../tests/AAEmu.Zone.Tests/Fixtures/npc-script-any-boolean-native-controls.json).
RTTI proves table vtable3557AB58 and slot48. The original Lua-number branch at3550D764
explicitly accepts requested tag2 and preserves that tag. This host API conversion
must not be confused with ordinary Lua truthiness: number0 is true in Lua conditions,
and a string such as `"1"` is not accepted by this typed boolean reader.
`ZoneLuaScriptAny.TryBoolean` implements the typed host read without changing the VM.

The focused suite additionally verifies original post-OnInit producer ordering,
startup grace, persistent disable/restore, exact arity/types/handle narrowing,
the global-override/getter distinction, changed versus unchanged entity activity,
scheduled signal gating, real World proximity activation, explicit unsupported
profiles, and retirement/reuse. Run it with isolated artifacts and
`--treenode-filter '/*/*/ManagedNpcAutoDisableTests/*'`. Full authored return navigation,
the combat restoration skill/buff branch and broader AI readiness require their own
owned consumers and acceptance evidence.
