using AAEmu.Zone.Simulation.Contracts;

namespace AAEmu.Zone.Simulation;

// ---------------------------------------------------------------------------
// Turn 2 — concrete prephase/postphase coordinator Agent 5 calls (registered
// IMPLEMENTATION_OWNERSHIP.md section 4, change log 2026-09-23; PLAN E2 +
// section 5 frame safety). It implements IZoneMovementFramePhases, whose doc
// (Simulation/Contracts/IZoneMovementFramePhases.cs) fixes the ordering:
//
//   packet dispatch (incl. ReceiveMovement) → flush
//   tick: PrePhase(frame) → backend.Step(elapsed) → PostPhase(frame)
//         → simulation.Update(frame) → flush
//
// All three calls run on the scene owner thread inside ZoneSceneCoordinator
// Dispatch (single-threaded, non-reentrant — ZoneSceneCoordinator.cs:237-244),
// so the residual risks are REENTRY and mid-callback ownership loss, not data
// races (PLAN section 5). This class refuses both by name:
//   - _inFrame: PrePhase while a frame is open = the coordinator skipped
//     PostPhase → named refusal (recursion-guard precedent ZoneSimulationTick.cs:66)
//   - _stepped: a second StepPhysics inside one frame = named double-step refusal
//     (belt: the native world's own Enter() busy guard, ZoneNativeTerrainPhysicsWorld.cs:234-235,
//     and Step's <=0 refusal, ZoneNativeTerrainPhysicsWorld.Step.cs:53-59)
//   - per-actor rechecks before EVERY status copy / pose write / encode:
//     IsMovementCurrent + controller.IsRetired + actor.IsLive (+ Pointer==0 purge
//     surfaces as the adapter/factory's named failures) — retired actors produce
//     no output (scope item 6).
// Per-actor failures (CorrectionOwnershipLostException is absorbed inside the
// controller methods, ZoneActorMovementCorrection.cs:121/:158; named service
// refusals such as the open TODO(pose-write) seam, and ObjectDisposed from a
// body retired mid-callback) are caught PER ACTOR and the batch continues
// (PLAN section 5); reentry guard violations (InvalidOperationException from the
// controller's own _receiving/_sampling/_phasing gates) are PROGRAMMER errors
// and propagate.
// ---------------------------------------------------------------------------
public sealed class ZoneMovementFramePhases : IZoneMovementFramePhases
{
    private readonly ZoneUnitRegistry _registry;
    private readonly IZonePhysicsBackend _backend;
    private readonly ZonePlayerActorFactory _actors;
    private readonly IZoneActorMovementInputServices _input;
    private readonly IZoneActorMovementCorrectionServices _correction;
    private readonly ZoneMovementOutgoing _outgoing;
    private readonly ZoneMovementFrameClock _clock;
    private readonly ZoneMovementDiagnostics _diagnostics;
    private readonly ZoneMovementInputBindings _bindings;
    private readonly List<StagedActor> _staged = [];
    private bool _inFrame;
    private bool _stepped;

    private readonly record struct StagedActor(ZoneUnit Unit, ZoneActorMovementController Controller);

    /// <summary>
    /// One instance per scene generation (Agent 5 wiring note). Every collaborator is
    /// scene-scoped: the registry, the ONE backend, the ONE player-actor factory, the concrete
    /// movement services, the encoder, the shared single-sample clock and the diagnostics sink.
    /// </summary>
    public ZoneMovementFramePhases(ZoneUnitRegistry registry, IZonePhysicsBackend backend,
        ZonePlayerActorFactory actors, IZoneActorMovementInputServices input,
        IZoneActorMovementCorrectionServices correction, ZoneMovementOutgoing outgoing,
        ZoneMovementFrameClock clock, ZoneMovementDiagnostics diagnostics,
        ZoneMovementInputBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(actors);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(correction);
        ArgumentNullException.ThrowIfNull(outgoing);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(bindings);
        _registry = registry; _backend = backend; _actors = actors; _input = input;
        _correction = correction; _outgoing = outgoing; _clock = clock;
        _diagnostics = diagnostics; _bindings = bindings;
    }

    /// <summary>True while a frame is open (PrePhase ran, PostPhase has not) — diagnostics surface for the harness.</summary>
    public bool IsFrameOpen => _inFrame;

    /// <summary>
    /// Stages this frame's movement actors: eligibility gate, mode-2/3 sampling and input
    /// production THROUGH the controller's prephase hook (which is where "input → physical
    /// action" is applied, before the integration). Staged = movement-current controller with
    /// admitted input; non-controller units (NPC wave, managed-only units) are not staged.
    /// Returns the staged actor count. Refuses a reentered or unfinished frame BY NAME.
    /// </summary>
    public int PrePhase(in ZoneMovementFrame frame)
    {
        if (_inFrame)
            throw new InvalidOperationException(
                "ZoneMovementFramePhases.PrePhase reentered an open frame: the coordinator must run " +
                "PrePhase → StepPhysics → PostPhase exactly once per frame and finish PostPhase even when " +
                "an actor fails (unfinished-frame refusal, recursion-guard precedent ZoneSimulationTick.cs:66).");
        _inFrame = true;
        _stepped = false;
        _staged.Clear();
        try
        {
            // ONE clock sample per frame, validated and published before any leaf reads a clock
            // (pump discipline, Contracts/IZoneAiFramePump.cs:8-12; SetFrame refuses non-positive
            // periods and non-finite game clocks by name).
            _clock.SetFrame(frame);
            var snapshot = ZoneMovementEligibilitySnapshotBuilder.PlainBody;
            foreach (var unit in _registry.Units.Values.ToArray())
            {
                if (unit.MovementModel is not ZoneActorMovementController controller) continue;
                // Removal during a pending frame: RemovePrepared retires the movement model
                // (ZoneUnitRegistry.cs:204-211) and StopMovement retires everything (:257-268);
                // a retired or scene-stopped actor is never staged, never stepped for.
                if (!_registry.IsMovementCurrent(unit) || controller.IsRetired) continue;
                try
                {
                    if (controller.TryRunPrePhase(frame, snapshot, _input, out _))
                        _staged.Add(new(unit, controller));
                }
                catch (NotSupportedException error)
                {
                    // A named seam refusal in one actor's input production must not stop the staging pass.
                    _diagnostics.ReportPhaseRefusal(unit.Snapshot.ObjectId, error.Message);
                }
            }
            return _staged.Count;
        }
        catch
        {
            _inFrame = false; // the frame never became stageable — leave no half-open frame behind
            _staged.Clear();
            throw;
        }
    }

    /// <summary>
    /// Exactly ONE backend step per frame: refuses step-outside-frame, double-step and
    /// non-positive elapsed BY NAME (matching the world's own "a zero-time drain is not a
    /// simulation step", ZoneNativeTerrainPhysicsWorld.Step.cs:53-59), then delegates to the
    /// backend — whose adapter maps 1:1 onto the native world's nonzero Step and admission
    /// profile (ZoneNativePhysicsBackendAdapter.Step → ZoneNativeTerrainPhysicsWorld.Step).
    /// </summary>
    public void StepPhysics(TimeSpan elapsed)
    {
        if (!_inFrame)
            throw new InvalidOperationException(
                "ZoneMovementFramePhases.StepPhysics ran outside an open frame: the ordering contract is " +
                "PrePhase → StepPhysics → PostPhase exactly once per frame (IZoneMovementFramePhases).");
        if (_stepped)
            throw new InvalidOperationException(
                "ZoneMovementFramePhases.StepPhysics attempted a SECOND physics step in one frame: one scene " +
                "period per frame per scene generation (frame-safety section; the native Enter() busy guard " +
                "ZoneNativeTerrainPhysicsWorld.cs:234-235 is the belt behind this refusal).");
        if (elapsed <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(elapsed),
                "A zero-time drain is not a simulation step; StepPhysics requires a strictly positive scene period " +
                "(ZoneNativeTerrainPhysicsWorld.Step.cs:53-59; framePeriod validated at ZoneSceneActivation.cs:74-75).");
        _stepped = true;
        _backend.Step(elapsed);
    }

    /// <summary>
    /// Publishes post-step state per staged actor — the PLAN section 4 order: correction hook →
    /// status copy → CommitPose → spatial refresh → entity mirror → outgoing sample — then
    /// drains the encoder into ZWUnitMovements frames on the existing notification route.
    /// Re-checks movement-currency and actor liveness before EVERY write (scope item 6); named
    /// refusals and mid-callback retirement are caught per actor and the batch continues (PLAN
    /// §5); guard violations from the controller's reentry gates propagate (programmer errors).
    /// Returns the number of entries queued this frame (0 = nothing changed on the wire —
    /// delta suppression + heartbeat of ZoneMovementOutgoing).
    /// </summary>
    public int PostPhase(in ZoneMovementFrame frame)
    {
        if (!_inFrame)
            throw new InvalidOperationException(
                "ZoneMovementFramePhases.PostPhase ran without PrePhase: the frame was never opened " +
                "(IZoneMovementFramePhases ordering contract).");
        if (!_stepped)
            throw new InvalidOperationException(
                "ZoneMovementFramePhases.PostPhase ran before StepPhysics: post-step publication must observe " +
                "the integration (IZoneMovementFramePhases ordering contract).");
        try
        {
            var snapshot = ZoneMovementEligibilitySnapshotBuilder.PlainBody;
            foreach (var staged in _staged)
            {
                var (unit, controller) = staged;
                // --- scope item 6: recheck BEFORE every status copy / pose write / encode ---
                if (!_registry.IsMovementCurrent(unit) || controller.IsRetired)
                {
                    SkipRemoved(unit);
                    continue;
                }
                var actor = _actors.FindByUnitObjectId(unit.Snapshot.ObjectId);
                if (actor is null || !actor.IsLive)
                {
                    _diagnostics.ReportSkippedActor(unit.Snapshot.ObjectId,
                        "postphase skipped: no live player actor generation (Pointer==0 / retired).");
                    SkipRemoved(unit);
                    continue;
                }

                ZoneActorMovementState? admitted;
                try
                {
                    controller.TryRunPostPhase(frame, snapshot, _correction, out admitted);
                }
                catch (NotSupportedException error)
                {
                    // Named seam refusals (TODO(pose-write), unsupported hanging) — report and
                    // continue THIS actor's publication: the authoritative pose still exists.
                    _diagnostics.ReportPhaseRefusal(unit.Snapshot.ObjectId, error.Message);
                    admitted = controller.LastAdmitted;
                }
                catch (ObjectDisposedException)
                {
                    _diagnostics.ReportSkippedActor(unit.Snapshot.ObjectId,
                        "postphase skipped after correction: physical body retired (Pointer==0) mid-callback.");
                    SkipRemoved(unit);
                    continue;
                }
                if (!_registry.IsMovementCurrent(unit) || controller.IsRetired) { SkipRemoved(unit); continue; }

                // --- authoritative pose + spatial update (PLAN E3; CommitPose CLEARED seam) ---
                ZonePhysicsActorStatus status;
                try
                {
                    if (!actor.TryCopyStatus(out status))
                    {
                        _diagnostics.ReportSkippedActor(unit.Snapshot.ObjectId,
                            "postphase skipped: body status unavailable (managed-only or not live).");
                        continue;
                    }
                    if (!_registry.IsMovementCurrent(unit) || controller.IsRetired) { SkipRemoved(unit); continue; }
                    unit.CommitPose(new ZoneUnitPose(
                        ZoneMovementPoseMath.ToUnitWorldPosition(status.Position), status.Orientation));
                    // Spatial refresh reads the model position AFTER the commit — with Agent 5's
                    // modelPosition delegate reading ZoneUnit.Pose, membership tracks the body
                    // (ZoneUnitRegistry.UpdateSpatialMembership :279-284, index Update :94).
                    _registry.UpdateSpatialMembership(unit);
                    if (!_registry.IsMovementCurrent(unit)) { SkipRemoved(unit); continue; }
                    actor.SyncEntityPose(); // body → entity mirror (ZonePlayerActorFactory.cs:625-634)
                }
                catch (ObjectDisposedException)
                {
                    _diagnostics.ReportSkippedActor(unit.Snapshot.ObjectId,
                        "postphase skipped: native body/entity retired (Pointer==0) during pose publication.");
                    SkipRemoved(unit);
                    continue;
                }

                // --- outgoing encode: authoritative post-step pose, not re-emitted input ---
                if (admitted is not null)
                    _outgoing.Sample(unit, status, admitted, frame);
                else
                    _diagnostics.ReportSkippedActor(unit.Snapshot.ObjectId,
                        "encoder sample skipped: no admitted input state (stance/actorFlags source absent).");
            }

            return _outgoing.Build(frame);
        }
        finally
        {
            _inFrame = false;
            _stepped = false;
            _staged.Clear();
        }
    }

    /// <summary>A removal observed mid-frame drops that unit's bindings and suppression state so no stale entry can survive into a later frame (scope item 6: no output for retired actors).</summary>
    private void SkipRemoved(ZoneUnit unit)
    {
        _bindings.Discard(unit);
        _outgoing.Discard(unit);
    }
}
