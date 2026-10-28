# Managed NPC game-script services

These services execute recovered leaves of the original 10.x NPC scripts. They do not establish gameplay AI parity. The global gameplay admission gate remains closed while reachable navigation, perception, skill, animation and manager consumers are incomplete. Unsupported state is a named refusal, not a successful empty implementation.

The source evidence is the original `x2game-dev_dedicate.c`, `cryaisystem_dedicate.c`, `cryaction_dedicate.c` and their matching client DLLs. Function addresses below are original preferred addresses. Vtable targets and the posture constant were independently read from the binaries with PE parsing and disassembly; the DLLs were not loaded. These static-source controls and managed tests are not an executed original C++ gameplay oracle.

## Smart-object state and its filter consumer

`ZoneManagedNpcSmartObjectStates` owns one generation's actual class registration and state set. `BindEntity(binding, links)` reads `Properties.soclasses_SmartObjectClass` and `PropertiesInstance.soclasses_SmartObjectClass` from the original entity table. Original `BasicActor` supplies class `Actor`. The authored NPC class resolver produces `npc_<models.Name>`; its class is not inferred from the allocated entity name. A fresh `ZoneManagedNpcSmartObjectLinks` is the entity's ordered link owner; Unit attachment/bonding IDs are not substituted for entity links.

Native registration `3118711C`/`3118700C` tokenizes ASCII identifiers, retains source order without duplicate classes, and truncates each class expression to 1023 bytes. Tokens start with a letter or underscore and may then contain digits (`31180DAC`). A classless entity is a known absent smart object. Object construction `311868CC` starts a registered object with the case-sensitive state `Idle` and binds it to entity field `+112`.

The unit APIs and behavior transitions have different semantics:

| Caller | Original path | Managed operation |
| --- | --- | --- |
| `unit:SetSoState(state)` | `393547B0` → AI system `+1224`/`3106E228` → `311833CC` | Add one literal state. Commas and minus characters are not parsed here. |
| `unit:SetDefaultSoState(enabled)` | `393546D0` → AI system `+1216`/`3106E2E8` → `3118337C` | Clear and replace with the authored template state when enabled, otherwise literal lowercase `idle`. An empty state leaves the set unchanged. |
| Behavior alertness change | AI system expression operation `+1240`/`3105E9B8`, parser `311850AC` | Split at the first minus, remove all negative tokens first, then add positive tokens. |
| State eligibility query | `3107EFE8` → `31186A1C` → `31190614`/`31190538` | `MatchesStateFilter`: alternatives separated by `|`; require every positive token and no negative tokens. Empty alternatives are omitted. An empty filter matches an existing smart object, not an absent one. |

The AI system's `+6836` enable flag gates replacement and expression modification. Singular add/remove are independent of that flag. Its constructor `3107FD08` initializes the flag to true.

Adding/removing an actually changed `Busy` state follows only the first exact `Busy` entity link (`311833CC`/`3118372C`). Replacement does not perform linked removal. The managed link inventory captures actual actor owners and rejects an unowned live target registration; stale targets cannot redirect a callback to a reused entity ID. Cyclic links terminate through idempotent set insertion/removal. Full smart-object rule selection, helper geometry, actions and graph execution remain separate unsupported consumers.

## Requests to World

`ZoneManagedNpcGameScriptRequests` checks the exact actor, catalog NPC and registered World Unit before sending. It uses the World Unit object ID, independently of NPC IDs and engine entity handles.

`unit:RemoveBuff(type)` (`39353020`) sends `ZWRemoveBuff`, opcode `0x0B`, with instance index `0`, requested template and reason `10`, then returns Lua `true` for the known owner. The existing `BuffRemovalRequest` serializer is reused. It does not remove a local instance; the existing WZ acknowledgment path owns that mutation. The host's earlier empty return was corrected to the original Boolean.

`unit:StopCasting()` (`39351050` → server AI unit system `+112`/`393259F0`) sends up to two `ZWStopCasting` requests, opcode `0x1A`:

1. If the current skill is nonzero, send `(WorldUnitId, timeline=0, skillId)`.
2. Select the current cast timeline, or the channel timeline when the cast timeline is zero; if nonzero, send `(WorldUnitId, timeline, skillId=0)`.

Initializer `39362980` and serializer `39368F40` retain the object BC field and two uint32 fields. Sending does not clear local casting flags, timelines or controllers.

The required `ZoneNpcCastingRequestState` is a copy of independent ServerUnit fields `+32280` cast timeline, `+32284` current skill and `+32288` channel timeline. `ZoneSkillState.Casts` alone does not retain the channel field after native `SkillEnded` promotes a cast to channeling. Production must provide the actual tuple owner; an absent adapter remains a refusal, never an assumed zero channel.

## Authored posture cache and notification

`ZoneManagedNpcPostureState` owns the actor-model posture and original action-state cache. Its constructor requires actual posture content, an animation-action descriptor resolver, the scene hour and the real World sender. `Bind` requires an explicit `ZoneNpcPostureDriverProfile`:

- The resolved actual model must match `Npc.Unit.Snapshot.ModelId` and be an actor model. `Npc.Model` is cached template metadata and may differ.
- Animation driver presence is an explicit construction fact. Missing physics, a CDF or a model filename cannot prove its presence or absence.
- `NativeOverrideTypes` contains the actual resolved NPC skill vector's use-condition fields. Native `390B34D0` suppresses default posture when any entry has condition `1` (idle skill). This is the same `NpcDesc+464` vector read by `39265E50` into `AI.skills.idle`, not an assumed empty list.
- The initial posture and looted state come from the actual construction input.

`SetDefaultPosture` follows `390B34D0`: select the greatest start hour strictly below the current hour, reject invalid action `0`, then check the idle-skill override. A selected enabled action equal to `CurrentAnimationId` causes no dispatch. Disabling dispatches even when the copied model posture is already equal. The disabled-repeat server notification is therefore intentional.

ActorUnitModel primary vtable `39FC4168` has `+464=39268E70`, which compares/copies its 24-byte model posture and invokes `+1496=39263A90`. Binary `39263A90` copies action ID and activate byte and explicitly passes `r9=0` to `3945DDB0`. For supported action states `2..8`, that writes only the appropriate real action-state cache; it does not immediately tick the graph. Getter `+720=392635B0` calls `3945BB70`, which picks the first nonzero slot `3..8`, then slot `2`, then the actual binary constant `3A5281F0=2`. Constructor `3945F390` initializes the slots to invalid `0`.

The lease implements `IZoneNpcPostureModel`. Its `CurrentAnimationId` can therefore be consumed directly by the existing `ZoneNpcPostureState` manager when checking whether to queue `OnPostureChanged`. This is a current-state consumer, not a second metadata field disconnected from the manager.

`3935C760` applies the model posture and sends `ZWUnitModelPostureChanged` (`0x0F`); the new serializer writes World Unit BC, type `4`, looted, action uint32 and activate Boolean. `ApplyFromWorld` uses the same cache setter without an outgoing echo, matching `393631B0` → `3935C470`.

Action states `0` and `1` need signal/item/tool consumers in `3945DDB0` and remain named refusals. An unresolved action is also refused before publishing an unbacked posture. This fail-closed boundary is stricter than the native invalid-descriptor log and is not claimed as invalid-content parity. Graph execution and authored action effects remain gated. An explicitly absent driver retains the model posture/notification but does not change cache slots, matching the native `controller+24` guard.

## ClearCombat and event 44

The first proxy call in `393534D0` is attribute restoration, not an attention reset:

1. Entity AI primary `+48` casts to the IAIActor interface at full object `+0x900`.
2. Its `+440=311599D4` resolves the AI proxy and calls `+328`.
3. CAIProxy vtable `30725888+328=30515710` resolves CActor and calls CActor `+288=392D5400`, reading the actor's bound World Unit ID at `+96`.
4. Environment `+8` is CGame, not CSystem. CGame table `39FC6E48+912=392A9820` returns the server AI unit system. That system's table `39FDD838+344=39325C20` is `RestoreAiUnitAttr`.
5. The record is IAIActor `+1288` (full CPipeUser `+3592`). Constructor `31163974` initializes mode `3`, restore-pending byte `0`, original-flying byte `0`.
6. When restore-pending is true, native clears it and restores the saved flying state if different. `39258C90` updates the Unit, ActorUnitModel `+296=39266B70` updates CActor `+558` through `392D7C10`, then IAIActor `+480=31159BD4` rebuilds flying/navigation parameters. Updating only `Unit.IsFlying` omits required consumers.

`ZoneManagedNpcCombatScriptServices` currently admits an explicitly owned clean restore record and no threat target/entries. It preflights those bounds, runs the existing real `Npc.Aggro.Clear(10)` (including its sleep/abuser cleanup), then clears an engaged Unit and sends `ZWClearCombat` (`0x0C`). It never resets attention. Pending flying restore and nonempty threat remain named refusals before unsupported mutation.

The remaining native threat branch matters: `393534D0` can retain forward threat entries when the current AI target is not hostile, but remove reverse attacker membership through `3935FAC0`. Removing the last reverse attacker may clear combat and notify World for other units. Existing `ZoneNpcSimulation.EntryRemoved` currently removes reverse membership only; calling `Aggro.Clear` on arbitrary combat state cannot be described as the complete native notification chain. Exact AI hostility and the affected-unit clear consumers remain required.

The native combat transition itself is implemented. `392587B0`/`39258800` change the combat byte only on a real transition, then virtual `+224=39259530` sends entity event `44` with the new Boolean. The scene subscribes `ZoneUnit.CombatTransitioned` per generation and selects the corresponding script-proxy interval through `Effects.SelectScriptUpdateInterval`; binding does not initialize that selection by polling the current Unit flag.

Script `ClearCombat` invokes only the combat setter. WZ `39364E70` separately zeros attack-faction flags after the setter/event. `ClearCombatFromScript` preserves those flags, while the existing WZ `SetCombat(false)` still clears them after transition callbacks. Other flag bits are preserved.

Death3935D490 marks the Unit dead through3925B2C0 before calling the same combat setter at virtual184. `Unit.Die` preserves that event order and the attack-faction flags; repeated death without a combat transition emits no additional event44.

## Position-check boundary

`unit:NpcSetCheckPosContext` (`39354640`) writes NPC field `+156`; its consumer is `3933D0E0`. That consumer requires the real current-region query, current AI movement state, authored `AI.idlePos`/`talkPos`/`spawnPos`, relocation and return-check state. It compares squared 3D distances at context-specific thresholds, and can restore posture after talk/roaming correction. A standalone copied context integer would hide those missing effects. This API retains its named refusal until that consumer is attached; no metadata-only implementation was supplied.

## Validation and remaining admission

Focused managed tests cover actual authored class registration/filtering, first Busy-link propagation and retirement, literal versus expression state operations, request bytes/order and acknowledgment ownership, strict posture time boundaries/cache priority/notification behavior, explicit absent driver, actual-wire-model overrides, Unit transition ordering and attack-faction flags, bounded combat restoration and generation retirement. All engine modules remain absent from these tests.

The host continues to execute original Lua constructors and transitions. Named service exceptions are protected Lua errors and flow into the existing constructor/script-error reporting. A successful state update or World request is not evidence that all goals reachable from idle/talk/combat have consumers. The scene must keep exact script readiness separate from gameplay admission and preserve missing-channel, driver, skill, position, hostility, flying and graph dependencies in its diagnostics.
