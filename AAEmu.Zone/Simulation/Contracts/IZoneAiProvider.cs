namespace AAEmu.Zone.Simulation.Contracts;

/// <summary>
/// The one replaceable AI backend of a scene generation: NPC create/remove, signal delivery,
/// activation, target inputs and update scheduling.
///
/// Invariants:
/// <list type="bullet">
/// <item>One AI backend per NPC: <see cref="CreateNpc"/> binds a live NPC generation to exactly
/// this provider instance; a second bind for a live generation is rejected, and only
/// <see cref="RemoveNpc"/> ends the binding. Re-entry rebinds the next generation handle through
/// a fresh <see cref="CreateNpc"/> call — never a resurrected one.</item>
/// <item>One scene owner: exactly one provider instance serves a scene generation, chosen once
/// at scene creation through <see cref="ZoneSceneBackendSelection"/>.</item>
/// <item>Provider exclusivity across implementations: the production implementation today is the
/// temporary NATIVE AI provider (the embedded engine's AI, constructed and retired only inside
/// its adapter type). The managed provider is unfinished and preserved for later replacement —
/// TODO(managed-ai): complete it and admit it through <c>ZoneSceneBackendSelection.Ai</c>. It
/// must not run for native-driven NPCs: a generation created by one provider receives every
/// signal, activation, target and scheduling call only from that same instance and retires
/// through <see cref="RemoveNpc"/> on it; scene creation never routes a native-driven NPC to the
/// managed implementation, and the unfinished managed implementation never picks up native-driven
/// NPC state on its own.</item>
/// </list>
///
/// Shapes are reused, not redefined: signals are <c>ZoneAiSignal</c>
/// (Simulation/ZoneAiSignal.cs), the record <c>IZoneNpcAiRuntime.DispatchScriptSignal</c>
/// already consumes (Simulation/IZoneNpcAiRuntime.cs); scheduling follows the per-generation
/// discipline of <c>ZoneUnitUpdateScheduler</c> (Simulation/ZoneUnitUpdateScheduler.cs), where
/// removal retires pending work instead of leaving it queued against a dead generation.
/// </summary>
public interface IZoneAiProvider
{
    /// <summary>
    /// Creates and binds one AI generation for a live NPC actor handle
    /// (<see cref="ZoneActorKind.Npc"/> only). Rejects players, unknown or retired handles, and
    /// any generation that already has a binding — one AI backend per NPC.
    /// </summary>
    void CreateNpc(ZoneActorHandle actor);

    /// <summary>
    /// Ends the generation's binding: retires its scheduled updates, pending signals and native
    /// or managed roots. Idempotent for cleanup paths; afterwards every other call for that
    /// handle is rejected. A native adapter must leave no callback or listener rooted — the
    /// retirement proof <c>IZoneNativeFlowPlayerActorOwner.VerifyFlowListenersRetired</c>
    /// demands (Simulation/ZoneNativeFlowActorRegistry.cs).
    /// </summary>
    void RemoveNpc(ZoneActorHandle actor);

    /// <summary>
    /// Delivers one script/AI signal to the bound generation, replacing the native dispatch path
    /// of <c>IZoneNpcAiRuntime.DispatchScriptSignal</c> (Simulation/IZoneNpcAiRuntime.cs).
    /// Signals for unbound or retired generations are rejected, not buffered.
    /// </summary>
    void SendSignal(ZoneActorHandle actor, ZoneAiSignal signal);

    /// <summary>
    /// Activation input for the bound generation: <c>true</c> is the active state (native event 7),
    /// <c>false</c> the inactive state (native event 6), the mapping of
    /// <c>IZoneNpcActivationAi.SendActivationEvent</c> (Simulation/ZoneNpcActivationState.cs).
    /// Inactive pauses update scheduling but keeps the binding; repeated identical states do nothing.
    /// </summary>
    void SetActive(ZoneActorHandle actor, bool active);

    /// <summary>
    /// Target input for the bound generation; <c>null</c> clears the target. The write precedes
    /// subsequent signal execution, preserving the discipline of <c>IZoneNpcSignalRuntime</c>
    /// (Simulation/IZoneNpcSignalRuntime.cs) where execution observes the already-updated target.
    /// The target may be any live actor handle, including players.
    /// </summary>
    void SetTarget(ZoneActorHandle actor, ZoneActorHandle? target);

    /// <summary>
    /// Update scheduling for the bound generation: <paramref name="period"/> is the requested
    /// cadence, <c>null</c> cancels it. Registration and removal follow
    /// <c>ZoneUnitUpdateScheduler</c> (Simulation/ZoneUnitUpdateScheduler.cs): a cancelled or
    /// retired generation's remaining batch is retired with it, never left pending. The provider
    /// never samples its own frame clock; the scene owner pumps time through one authority.
    /// </summary>
    void SetUpdateSchedule(ZoneActorHandle actor, TimeSpan? period);
}
