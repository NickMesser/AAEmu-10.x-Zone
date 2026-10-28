# Managed NPC posture

`ZoneManagedNpcPostureState` now has a production construction path for the fresh dedicated ActorUnitModel. It owns the model's posture and action-state cache, supplies the existing `IZoneNpcPostureModel.CurrentAnimationId` consumer, and emits real `ZWUnitModelPostureChanged` requests. It does not advance an animation graph.

`BindDedicatedConstruction(actor, construction, models, npcSkills)` requires the exact current construction lease and the actual `Unit.Snapshot.ModelId`. Cached NPC template model metadata cannot stand in for that model. The lease expires with either actor or construction. The actual loaded NPC skill-child vector supplies the condition-1 override test; an empty selected skill list is not evidence of an empty child inventory.

## Original construction and lifetime

The evidence is from the original 10.x `x2game-dev_dedicate` binary and its matching decompile.

| Address | Observed behavior |
| --- | --- |
| `39289EB0` | UnitModel construction allocates the object stored at model `+536` using `394606E0`. |
| `394606E0` | `X2::AnimationStateMachine::Queue` construction sets driver `+24` null and action-cache entries `+108..+140` to invalid zero. |
| `39268000` / `3945F390` | Model link/reset clears the actor/driver and the same cache slots. This is a reset, separate from allocation. |
| `39277D00` | Actual ActorUnitModel creation calls `3945F1E0` only inside the `gEnv+288` client branch. |
| `3945F1E0` | Driver link stores the actual CActor graph-state getter `vtable+320` at controller `+24`, resolves graph input IDs, and resets the cache. |
| `39263A90` | Actor posture dispatch passes action ID and activation to `3945DDB0`; machine code explicitly clears argument 4. |

The production factory therefore admits the verified fresh dedicated null-driver profile. This is not inferred from missing assets, a headless process, or an absent living body. It does not admit later client graph linkage, model replacement, or an externally supplied graph driver as though those lifecycles had been implemented.

## Descriptor data and cache consumer

`ZoneAnimationActionContent` reads real `anim_actions.id` and `action_state_id`. Native loader `39C04BB0` reads these columns into the descriptor resolved by `39BFE4A0`. The original local database contains 434 rows: 5 signal, 14 mount, 338 idle, 19 bind, 46 fall, 1 loot, 4 sit, 4 lay, and 3 quest actions.

`3945DDB0` tests driver presence before descriptor resolution. An absent driver skips that action effect, including unknown IDs. With an explicitly supplied driver-present test profile, states 2 through 8 write their corresponding cache slot: the action ID when enabled and invalid zero when disabled. Because the actual posture caller passes argument 4 as zero, this path does not request an immediate graph tick. States 0 and 1 still refuse in the managed driver-present profile: their real signal, mount, and equipment consumers are not provided by storing cache values.

`3945BB70` supplies the current action. It scans slots 3 through 8 in ascending order, then slot 2, then returns the original initialized constant 2. The posture manager uses this value for its enabled-equal suppression; disabled requests still notify World repeatedly. Strict time selection and the actual condition-1 override test retain their original ordering.

World posture updates use the same cache setter without an outgoing echo. Outgoing default-posture requests have the actual object ID, union discriminator 4, looted flag, uint32 action ID, and activation flag. Production notification queuing is scoped to the exact Unit and posture lease. Retiring that lease discards only its own unsent requests, preserving other owners' frames.

## Validation and limits

The embedded `npc-posture-native-controls.json` fixture records 25 independent controls using copied original reset, cache setter, and getter instructions. It covers the genuine empty-queue reset branch, null-driver calls, real action IDs for states 2 through 8, cache priority, deactivation, and default action 2. The descriptor resolver alone supplies copied ID/state rows from the original database; signal/mount/tool, graph tick, and nonempty queue/free branches are outside this probe. No CryEngine or Lua DLL was loaded. The external reproducer and complete result remain at `D:/aaemu-runtime/research/npc-posture-leaf-controls.py` and `npc-posture-native-controls.json`.

The focused tests compare all 25 captured cache outcomes and also cover the concrete SQLite reader, actual wire-model selection, condition-1 child inventory, construction retirement, cross-owner refusal, real posture payloads, and scoped notification teardown. These checks establish the supported posture/cache path, not complete NPC animation or gameplay parity.
