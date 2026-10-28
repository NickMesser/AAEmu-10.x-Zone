# NPC SaveUnitAttr state

`ZoneManagedNpcCombatScriptServices.SaveUnitAttr(actor, int changeOption)` supplies the original saved-attribute producer needed by `AI.SetParamX2`. It writes the actor's owned `ZoneNpcAiUnitAttributeRestoreState`; it does not arm restoration, change the Unit's flying/combat state, update a body/navigation profile, or send a World frame.

The original server registration binds `unit.SaveUnitAttr(changeOption)` to `39351510`. That function resolves the bound World Unit ID, obtains its model/entity AI actor, and calls IAIActor virtual +424. The CPipeUser implementation is CryAI `31159914`, with the following state effects:

The registration wrapper `399EE7B0 -> 395DA0B0` requires a numeric first argument, narrows it through native float to signed32-bit integer, and invokes the bound method only on successful conversion. Missing/nonnumeric arguments return no Lua values without saving; omission must not be converted into option0. The original almighty character supplies `entity.AI.param.canChangeAiUnitAttr` explicitly.

| Native IAIActor offset | Full CPipeUser offset | Field | Save behavior |
| --- | --- | --- | --- |
| +1288 | +3592 | Mode | Nonzero option copied as all32 bits. Option0 chooses2 if currently flying, otherwise3. |
| +1292 | +3596 | RestorePending | Preserved. Saving does not set or clear it. |
| +1293 | +3597 | OriginalFlying | Copies the actual current World Unit flying byte. |

The constructor `31163974` initializes mode3 and both bytes false. A later fly-skill producer may arm pending restoration; that is separate from SaveUnitAttr. ClearCombat still explicitly refuses pending flying restoration because its complete Unit, CActor and AI navigation consumers are not implemented by this profile.

The binary confirms the flying query's identity and storage: `31159974` loads the proxy's copied World Unit ID into EDX, and the two AIUnitSystem +16 calls retain/reload that ID. Server AIUnitSystem vtable `39FDD838` resolves +16 to `39212970`, which resolves the actual Unit and reads byte +32777 (`ZoneUnit.IsFlying`). This is not the actor-health field, a physical airborne status, or the saved OriginalFlying byte.

Admission39D1C900 copies the snapshot's packed bit9 through byte9859 into the actual flying byte. Inverse39D1C2B0 and serializer393932E0 rebuild that bit from the current byte. `ZoneUnit.IsFlying` now retains both initial and acknowledged World state, and `Flags` derives bit9 from it; a later flying change cannot leave the original snapshot bit stale. These are separate from the physical body's airborne status and the casting bit1.

The managed method requires the exact current Unit/NPC/actor allocation and its bound attribute lease. An unbound, retired, or foreign allocation refuses before mutation. This is the managed lifetime admission boundary; the original native binding silently returns when optional model/proxy/AI interfaces are absent.

`D:/aaemu-runtime/research/native-save-unit-attr.py` executes the original CryAI `31159914` and server `39212970` under Unicorn without loading either DLL. Explicit callbacks provide the proxy, AI service lookup, bound World ID and Unit lookup; the original instructions choose the mode, read Unit flying, and write the record. The21 controls cover zero/nonzero/signed32-bit options, both flying values, both pending values, and an invalid proxy Unit ID. They report zero disagreements with the implemented field rule. Full binary hashes and copied outputs are in `native-save-unit-attr-controls.json`.

Focused `ManagedNpcCombatScriptServicesTests` cover these saved fields, absence of unrelated mutation/notifications, retained pending-flight refusal, and exact allocation retirement. Iteration artifacts are isolated under `D:/aaemu-runtime/npc-combat-attrs-artifacts`.
