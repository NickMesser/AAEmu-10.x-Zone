# Managed NPC body publication

`ZoneManagedNpcMovementFramePhases` decorates the existing `IZoneMovementFramePhases`. Its inner phase must own the same backend as `ZoneManagedNpcPhysics`. NPC `PrepareFrame` queues actor-local requests in prephase; `StepPhysics` delegates only to the inner phase; postphase first finishes the inner phase, then copies NPC body statuses. It creates no clock sample or second physics integrator.

For each NPC staged before the step, publication requires the exact catalog NPC, registered Unit, actor shell and body lease. It commits physical position/orientation through `Unit.CommitPose`, refreshes existing spatial membership, then mirrors the committed local position into the same AI object. Retirement is rechecked around source callbacks and every publication boundary. New bodies created after staging wait until the following frame. Disposal drops the decorator's encoder references; its collaborators retain their existing owners.

## Outgoing metadata is an explicit capability

The optional constructor callback has this shape:

```csharp
Func<ZoneManagedNpcActor, ZoneManagedNpcBodySnapshot,
     ZoneMovementFrame, ZoneActorMovementState?> movementState
```

It must supply actual copied CActor delta/flags/alertness state. Physical pose and velocity come from the body status, and stance comes from the body owner's accepted actor slot. Missing or unsupported metadata suppresses encoding with a named diagnostic while real Unit/spatial/AI publication continues. Goal BodyState, movement urgency, PE velocity-mode flags and guessed zero fields are not substitutes.

Original NPC replication393405B0 calls ActorUnitModel vtable39FC4168+224→39278AC0. This is distinct from the CActor primary table.39278AC0 copies accepted stance through the actual CActor virtual+712, delta from the current CActor request, and low actor flags from independent fields including actor+124bit0x1000,+148,+416 and+418. Alertness has its own AI-proxy producer and combat-byte fallback. The complete production ownership of these fields is still pending. Optional moving-ground, hanging, push, skill and extended fall fields also require separate encoding support and are refused by this plain-body profile.

The decorator uses the existing `ZoneMovementOutgoing.Sample/Build`, delta suppression, quantization and notification queue. World pose conversion uses the scene origin. The encoder's explicit convention determines local-versus-world wire coordinates. A final generation check discards an earlier sample if a later metadata callback retires that actor, even when the old Unit temporarily remains registered.

`ManagedNpcMovementFrameTests` use the real managed backend, terrain, static wall, inner movement phases, spatial grid and outgoing codec. The principal control falls onto support, crosses a spatial cell and stops at an authored triangle wall; it checks Unit/spatial/AI/wire agreement and exactly one shared step per frame. Additional controls cover missing/unported metadata, retirement after the shared step, replacement during metadata callbacks, coordinate origins and frame guards.

This is physical body publication, not complete NPC locomotion or replication parity. The existing NPC manager's authored replication cadence/CVars, complete CActor control state, AI steering, animation-driven movement and retained moving-platform ownership remain separate. Production composition can publish actual physics while withholding outgoing NPC movement until its metadata capability is admitted.
