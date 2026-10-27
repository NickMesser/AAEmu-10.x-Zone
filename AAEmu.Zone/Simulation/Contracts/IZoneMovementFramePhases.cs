namespace AAEmu.Zone.Simulation.Contracts;

/// <summary>
/// One scene frame's single clock sample, carried into the movement/physics phases. The frame
/// coordinator samples every clock EXACTLY ONCE per frame before calling
/// <see cref="IZoneMovementFramePhases.PrePhase"/> (pump discipline: the pump never samples its
/// own time source, <c>Simulation/Contracts/IZoneAiFramePump.cs:8-12</c>).
///
/// Field domains — never mix them:
/// <list type="bullet">
/// <item><see cref="Frame"/> — the scene's <c>IZoneFrameSource</c> counter; it advances with
/// unchecked wrapping (<c>Simulation/IZoneFrameSource.cs:16-22</c>), so consumers compare it
/// unsigned and never assume monotonic growth across wrap.</item>
/// <item><see cref="Elapsed"/> — the configured frame period as a <see cref="TimeSpan"/>.
/// It stays a <see cref="TimeSpan"/> in every consumer; scene creation validates
/// <c>0 &lt; period</c> (and ≤ 10 s) at
/// <c>Simulation/ZoneSceneActivation.cs:74-75</c>. A non-positive value must never be
/// constructed by a coordinator.</item>
/// <item><see cref="ServerMilliseconds"/> — the single sampled scene clock, the value of
/// <c>IZoneUnitUpdateServices.TimeMilliseconds</c> (<c>Simulation/ZoneUnitUpdateScheduler.cs:9</c>)
/// surfaced through <c>IZoneActorMovementServices.ServerMilliseconds</c>
/// (<c>Simulation/ZoneActorMovementController.cs:14</c>). It feeds unsigned, non-wrap-aware
/// history admission (<c>ZoneActorMovementHistory.AdmitTime</c>,
/// <c>Simulation/ZoneActorMovementController.cs:34-40</c>), receive-path suppression windows
/// (<c>Simulation/ZoneActorMovementController.cs:118-138</c>), and the per-entry <c>Time</c>
/// (u32 ms) of every outgoing ZWUnitMovements record this frame. It is NOT the client's
/// packet <c>state.Time</c>, which remains the client clock it arrived on.</item>
/// <item><see cref="GameSeconds"/> — the single sampled game clock,
/// <c>IZoneActorMovementServices.GameSeconds</c>
/// (<c>Simulation/ZoneActorMovementController.cs:13</c>), used for elapsed-time gating and
/// sampling in the movement controller (<c>Simulation/ZoneActorMovementController.cs:118-138</c>).
/// Native physics time is a THIRD domain: it advances only inside
/// <c>ZoneNativeTerrainPhysicsWorld.Step</c> and is never written into this record.</item>
/// </list>
///
/// Coordinate domain: every value in this record is clock state; it carries no positions.
/// Positions crossing these phases keep their domains as documented on
/// <see cref="IZoneMovementFramePhases"/> below. Registered in
/// AAEmu.Zone/IMPLEMENTATION_OWNERSHIP.md section 4 (change log 2026-09-23) under the
/// contract-folder extension allowance; Agent 5 owns the scene-frame wiring that constructs
/// it (Agent 4 precedent, <c>Simulation/Contracts/IZoneAiFramePump.cs:14-16</c>).
/// </summary>
public readonly record struct ZoneMovementFrame(
    uint Frame,
    TimeSpan Elapsed,
    uint ServerMilliseconds,
    float GameSeconds);

/// <summary>
/// The exact phase API Agent 5's frame coordinator calls once per scene frame, in this order,
/// all on the scene owner thread inside <c>ZoneSceneCoordinator.Dispatch</c>
/// (<c>Simulation/ZoneSceneCoordinator.cs:237-244</c>, which rejects reentry at :240):
///
/// <code>
/// packet dispatch (incl. ReceiveMovement) → flush notifications
/// tick:  PrePhase(frame) → backend.Step(elapsed) → PostPhase(frame) → simulation.Update(frame) → flush
/// </code>
///
/// Ordering rationale, all cited:
/// <list type="bullet">
/// <item>Packet dispatch precedes the tick and each side flushes its own notifications —
/// the explicit event-loop policy (<c>Simulation/ZoneSession.cs:226-235</c>) — so this frame's
/// received movement input arrives BEFORE <see cref="PrePhase"/> stages eligibility and BEFORE
/// the step integrates it; the tick entry itself is
/// <c>Simulation/ZoneSession.cs:246-247</c>.</item>
/// <item><see cref="PrePhase"/> runs before <see cref="StepPhysics"/>: eligibility gates,
/// history sampling and input production for this frame land before the integration.</item>
/// <item><see cref="PostPhase"/> runs before <c>simulation.Update</c> because the manager
/// reports read what postphase publishes — <c>UpdateUnitLocationReports</c> is declared at
/// <c>Simulation/ZoneSimulationTick.cs:26</c> and invoked at :123. Physics and actor phases are
/// host phases outside those tick services (<c>Simulation/ZoneSimulationTick.cs:5-7</c>).</item>
/// <item>The final flush happens after <see cref="PostPhase"/> so the ZWUnitMovements batch
/// queued during postphase goes on the wire in the same iteration
/// (<c>Simulation/ZoneSession.cs:251</c>).</item>
/// <item>Exactly ONE <see cref="StepPhysics"/> per frame per scene generation: the coordinator
/// has a single call site, implementations rethrow on reentry inside a frame (recursion-guard
/// precedent <c>Simulation/ZoneSimulationTick.cs:66</c>), and the native world's own busy guard
/// rejects nested native calls (<c>Simulation/ZoneNativeTerrainPhysicsWorld.cs:234-235</c>).</item>
/// </list>
///
/// Coordinate domains crossing these phases (each documented at its own surface):
/// body status is the engine-local world frame for physics (copied values only,
/// <c>Simulation/ZoneNativeTerrainPhysicsWorld.cs:8</c>); the entity mirror is updated through
/// <c>PublishPose</c> (<c>Simulation/ZonePlayerActorFactory.cs:625-634</c>); the model pose
/// lives in the WZUnitState world frame (<c>Simulation/ZonePlayerActorFactory.cs:556-557</c>);
/// correction origins convert local↔world with the fixed 2^-54 step
/// (<c>Simulation/ZoneActorMovementCorrectionMath.cs:14-22</c>); outgoing ZW entries are
/// emitted in the same local/world convention World's relay decodes — "Create is zone-local →
/// ZW is local → world for SC + mirrors"
/// (<c>AAEmu.WorldServer/AAEmu.World/Core/Relay/MovementRelay.cs:341-343</c>) — and the
/// encoder takes that policy as a constructor flag supplied by Agent 5.
///
/// Registered in AAEmu.Zone/IMPLEMENTATION_OWNERSHIP.md section 4 (change log 2026-09-23);
/// flagged to Agent 5 for scene-frame wiring. The concrete coordinator
/// (<c>Simulation/ZoneMovementFramePhases.cs</c>) lands in a later Agent 3 turn.
/// </summary>
public interface IZoneMovementFramePhases
{
    /// <summary>Stages this frame's movement actors (eligibility, sampling, input production). Returns the staged actor count.</summary>
    int PrePhase(in ZoneMovementFrame frame);

    /// <summary>
    /// Advances the physics backend by exactly one scene period. Implementations reject
    /// <c>elapsed &lt;= 0</c> by name — a zero-time drain is not a simulation step — matching
    /// <c>ZoneNativeTerrainPhysicsWorld.Step</c>
    /// (<c>Simulation/ZoneNativeTerrainPhysicsWorld.Step.cs</c>), whose zero-time counterpart
    /// is the engine loading drain (<c>Simulation/ZoneNativeEngine.PhysicsCompletion.cs:46</c>).
    /// </summary>
    void StepPhysics(TimeSpan elapsed);

    /// <summary>Publishes post-step state (correction, pose commit, spatial membership, entity mirror, outgoing encode). Returns the emitted entry count.</summary>
    int PostPhase(in ZoneMovementFrame frame);
}
