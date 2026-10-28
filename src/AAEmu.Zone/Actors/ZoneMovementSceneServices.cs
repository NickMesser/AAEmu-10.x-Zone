using System.Numerics;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.Actors;

// ---------------------------------------------------------------------------
// Turn 2 — concrete movement scene services (registered in
// IMPLEMENTATION_OWNERSHIP.md section 4, change log 2026-09-23). This file holds
// the pieces shared by every movement-phase leaf: the single-sample frame clock,
// structured phase diagnostics, pose fixed-point math, and the concrete
// IZoneActorMovementServices (clocks, gates, direct movement application).
// The input-services and correction-services concretes live beside their own
// interfaces (ZoneActorMovementInputServices.cs / ZoneActorMovementCorrection.cs)
// per the turn-2 scope.
// ---------------------------------------------------------------------------

/// <summary>
/// The ONE per-frame clock sample shared by every movement leaf. The frame coordinator
/// (Agent 5's caller of <c>IZoneMovementFramePhases.PrePhase</c>) samples the scene clocks
/// exactly once per frame and pushes the record here before staging actors — pump discipline:
/// the pump never samples its own time source (<c>Ai/IZoneAiFramePump.cs:8-12</c>).
///
/// Clock-domain rules, restated from <c>Actors/IZoneMovementFramePhases.cs:12-37</c>
/// and binding on every reader of this record:
/// <list type="bullet">
/// <item><c>ServerMilliseconds</c> is the scene clock: history admission
/// (<c>ZoneActorMovementHistory.AdmitTime</c>, <c>Actors/ZoneActorMovementController.cs:34-40</c>),
/// receive-path suppression windows (:116-138), admission latches and the outgoing
/// ZWUnitMovements per-entry <c>Time</c>. It is NOT the client packet clock
/// (<c>ZoneActorMovementState.Time</c> stays client packet-ms and is never overwritten here).</item>
/// <item><c>GameSeconds</c> feeds elapsed-time gating in the controller
/// (<c>IZoneActorMovementServices.GameSeconds</c>, <c>Actors/ZoneActorMovementController.cs:13</c>).</item>
/// <item><c>Elapsed</c> stays a <see cref="TimeSpan"/>; native physics time is a third domain
/// that only advances inside <c>ZoneNativeTerrainPhysicsWorld.Step</c>.</item>
/// <item>An unset clock reads zeros (no frame yet) — consumers treat
/// <see cref="HasFrame"/> == false as "not staged this frame".</item>
/// </list>
/// Validation lives in <see cref="SetFrame"/>: the coordinator must supply a positive period
/// (scene creation validates <c>0 &lt; period</c>, <c>Scene/ZoneSceneActivation.cs:81-82</c>)
/// and a finite game clock, and failures are refused BY NAME, never silently normalized.
/// </summary>
public sealed class ZoneMovementFrameClock
{
    public ZoneMovementFrame Current { get; private set; }
    /// <summary>False until the coordinator pushed this frame's sample; leaves must not fabricate clocks.</summary>
    public bool HasFrame { get; private set; }

    public void SetFrame(in ZoneMovementFrame frame)
    {
        if (frame.Elapsed <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(frame),
                "A movement frame requires a strictly positive configured period " +
                "(Scene/ZoneSceneActivation.cs:81-82; IZoneMovementFramePhases doc surface).");
        if (!float.IsFinite(frame.GameSeconds))
            throw new ArgumentOutOfRangeException(nameof(frame),
                "A movement frame requires a finite sampled game clock (IZoneMovementFramePhases.cs:31-37).");
        Current = frame;
        HasFrame = true;
    }
}

/// <summary>
/// Structured, non-silent counters for the movement/physics phases. Named refusals that a
/// phase elects to survive (the open <c>TODO(pose-write)</c> ABI seam, retired actors skipped
/// mid-frame, missing content) are recorded HERE instead of being swallowed — "never a silent
/// skip" is the refusal policy of <c>Native/Physics/ZoneNativeTerrainPhysicsWorld.Step.cs:144-153</c>
/// carried over to the managed phases. Tests read these counters; the harness reports them.
/// </summary>
public sealed class ZoneMovementDiagnostics
{
    private readonly object _gate = new();

    public long MotionApplicationRefusals { get; private set; }
    public long RejectedInputs { get; private set; }
    public long MissingGroundReports { get; private set; }
    public long MissingLimitReports { get; private set; }
    public long SpeedClamps { get; private set; }
    public long TurnClamps { get; private set; }
    public long SkippedActors { get; private set; }
    public long PhaseRefusals { get; private set; }
    public long FacingControlRequests { get; private set; }
    public long JumpEndedNotifications { get; private set; }
    public long StanceChangeRefusals { get; private set; }

    public string? LastRefusalMessage { get; private set; }
    public string? LastRejectionReason { get; private set; }
    public string? LastSkipReason { get; private set; }

    internal void ReportMotionRefusal(string operation, ZoneActorHandle handle, string message)
    {
        lock (_gate)
        {
            MotionApplicationRefusals++;
            LastRefusalMessage = $"{operation}({handle}): {message}";
        }
    }

    internal void ReportRejectedInput(uint objectId, string reason)
    {
        lock (_gate) { RejectedInputs++; LastRejectionReason = $"unit {objectId}: {reason}"; }
    }

    internal void ReportMissingGround(ZoneActorMovementGroundKind kind, uint objectId)
    {
        lock (_gate) { MissingGroundReports++; LastSkipReason = $"missing {kind} ground {objectId}"; }
    }

    internal void ReportMissingLimits(uint objectId)
    {
        lock (_gate)
        {
            MissingLimitReports++;
            LastRejectionReason = $"unit {objectId}: content-derived movement limits absent (game_stances/actor_models).";
        }
    }

    internal void ReportSpeedClamp() { lock (_gate) SpeedClamps++; }
    internal void ReportTurnClamp() { lock (_gate) TurnClamps++; }

    internal void ReportSkippedActor(uint objectId, string reason)
    {
        lock (_gate) { SkippedActors++; LastSkipReason = $"unit {objectId}: {reason}"; }
    }

    internal void ReportPhaseRefusal(uint objectId, string reason)
    {
        lock (_gate) { PhaseRefusals++; LastRefusalMessage = $"unit {objectId}: {reason}"; }
    }

    internal void ReportStanceRefusal(ZoneActorHandle actor, string reason)
    {
        lock (_gate)
        {
            StanceChangeRefusals++;
            LastRefusalMessage = $"stance({actor}): {reason}";
        }
    }

    internal void ReportFacingControl(uint objectId, bool enabled)
    {
        lock (_gate) { FacingControlRequests++; LastSkipReason = $"unit {objectId}: facing control {(enabled ? "enable" : "disable")} recorded (plain body)."; }
    }

    internal void ReportJumpEnded(uint objectId)
    {
        lock (_gate) { JumpEndedNotifications++; }
    }
}

/// <summary>
/// Pose fixed-point math shared by every movement leaf. The model pose domain is the
/// <c>UnitWorldPosition</c> fixed-point frame: <c>FixedX/FixedY = meters × 2^44</c>
/// (<c>UnitPosition.X = FixedX / 17592186044416</c>,
/// <c>AAEmu.Zone.Protocol/Units/UnitIdentity.cs:77-83</c>; the same 2^44 scale appears in the
/// sampler's blend arithmetic, <c>Actors/ZoneActorMovementSampling.cs:95-105</c>), and the
/// correction origin is the region-granularity step <c>Origin(x, y) = x &lt;&lt; 54</c> with
/// local conversion at scale 2^-44 (<c>Actors/ZoneActorMovementCorrectionMath.cs:14-22</c>;
/// origin units are 1024 m regions — <c>ZoneUnitSpatialIndex.cs:9</c>).
/// Body-status poses use zone-local meters; committing a World pose adds the scene origin.
/// The unshifted helpers remain useful when the caller already has World-meter coordinates.
/// </summary>
public static class ZoneMovementPoseMath
{
    /// <summary>2^44 fixed-point scale of <c>UnitWorldPosition</c> (UnitIdentity.cs:77-83).</summary>
    public const double FixedScale = 17592186044416.0;

    /// <summary>Body-status/engine floats → committed model pose (mirror of CorrectionMath.World's truncating conversion, CorrectionMath.cs:18-20/:23).</summary>
    public static UnitWorldPosition ToUnitWorldPosition(Vector3 position) =>
        new(NativeTruncate(position.X * FixedScale), NativeTruncate(position.Y * FixedScale), position.Z);

    /// <summary>Committed model pose → engine-frame meters (UnitIdentity.cs:77-83).</summary>
    public static Vector3 ToMeters(UnitWorldPosition position) =>
        new((float)(position.FixedX / FixedScale), (float)(position.FixedY / FixedScale), position.Z);

    /// <summary>World fixed-point pose to the engine's zone-local meters; each origin cell is 1024 meters.</summary>
    public static Vector3 ToLocalMeters(UnitWorldPosition position, ZoneActorMovementWorldOrigin origin) =>
        new((float)(unchecked(position.FixedX - ((long)origin.X << 54)) / FixedScale),
            (float)(unchecked(position.FixedY - ((long)origin.Y << 54)) / FixedScale), position.Z);

    /// <summary>Engine zone-local meters to the World pose, preserving fixed-point precision at the boundary.</summary>
    public static UnitWorldPosition ToWorldPosition(Vector3 localPosition, ZoneActorMovementWorldOrigin origin)
    {
        var local = ToUnitWorldPosition(localPosition);
        return new(unchecked(local.FixedX + ((long)origin.X << 54)),
            unchecked(local.FixedY + ((long)origin.Y << 54)), local.Z);
    }

    /// <summary>CVTTSD2SI semantics of the native conversion: overflow/NaN → long.MinValue (CorrectionMath.cs:23, same rule as ZoneActorMovementInterpolation.NativeInt64, Sampling.cs:126-129).</summary>
    private static long NativeTruncate(double value) =>
        value >= 9223372036854775808.0 || value < -9223372036854775808.0 || double.IsNaN(value)
            ? long.MinValue : (long)value;
}

/// <summary>
/// Refusal-survivable motion application: every movement-authority write to
/// <see cref="IZonePhysicsMotion"/> is wrapped HERE so the open <c>TODO(pose-write)</c> ABI
/// seam (<c>Physics/IZonePhysicsMotion.cs</c>) refuses by name into
/// <see cref="ZoneMovementDiagnostics"/> instead of tearing down the packet receive loop
/// (direct movement applies inside <c>ReceiveCore</c>, <c>Actors/ZoneActorMovementController.cs:124-131</c>).
/// Only <see cref="NotSupportedException"/> is absorbed — handle/registration errors
/// (<see cref="InvalidOperationException"/>) and validation errors still propagate, because they
/// mean the wiring is wrong, not that the seam is open. The adapter's own members keep
/// refusing by name when called directly (turn-3 ABI probe flips them on proof).
/// </summary>
public static class ZoneMovementMotionCall
{
    public static void Velocity(IZonePhysicsMotion motion, ZoneActorHandle handle, Vector3 velocity,
        ZoneMovementDiagnostics diagnostics)
    {
        try { motion.ApplyVelocity(handle, velocity); }
        catch (NotSupportedException error) { diagnostics.ReportMotionRefusal("ApplyVelocity", handle, error.Message); }
    }

    public static void Impulse(IZonePhysicsMotion motion, ZoneActorHandle handle, Vector3 impulse,
        ZoneMovementDiagnostics diagnostics)
    {
        try { motion.ApplyImpulse(handle, impulse); }
        catch (NotSupportedException error) { diagnostics.ReportMotionRefusal("ApplyImpulse", handle, error.Message); }
    }

    public static void Pose(IZonePhysicsMotion motion, ZoneActorHandle handle, Vector3 position,
        Quaternion orientation, ZoneMovementDiagnostics diagnostics)
    {
        try { motion.ApplyPose(handle, position, orientation); }
        catch (NotSupportedException error) { diagnostics.ReportMotionRefusal("ApplyPose", handle, error.Message); }
    }
}

/// <summary>
/// Concrete <c>IZoneActorMovementServices</c> (<c>Actors/ZoneActorMovementController.cs:7-19</c>)
/// for the player path (PLAN D1):
/// <list type="bullet">
/// <item>Clocks read the injected single-sample frame (<see cref="ZoneMovementFrameClock"/>) —
/// never a wall clock of their own (pump discipline, IZoneAiFramePump.cs:8-12).</item>
/// <item><c>AllowsSynchronization</c> = true with <c>TODO(model-guard)</c>: the model+140
/// psychokinesis guard has no native owner yet (contract doc, :9-10); a fabricated false would
/// silently disable all input, a fabricated true is the permissive leaf — documented permissive
/// until the model owner lands (eligibility note precedent,
/// <c>ZoneActorMovementEligibility.cs:5-7</c>).</item>
/// <item><c>UsesDirectPhysicalMovement</c> = true for Character bodies: this IS the player path
/// and <c>ReceiveCore</c> takes the direct branch for them (:11-12 doc, :124-131); the
/// buffered history path serves the remaining kinds (PLAN D1).</item>
/// <item><c>InterpolationMode</c>/<c>BufferMilliseconds</c> default to the identity profile
/// (mode 0, buffer 0) with <c>TODO(movement-interpolation-config)</c>: no configured source for
/// them exists in this tree; Agent 5 may inject the real config through the constructor.
/// Mode 0 keeps history admission inert (<c>ZoneActorMovementController.cs:150</c>) and the
/// correction consumer's mode-0 branch is positional (:207).</item>
/// <item><see cref="ApplyDirectMovement"/> = the direct-branch physical write: admitted,
/// finite-checked, content-clamped walk/turn/stop/jump intent → <see cref="IZonePhysicsMotion"/>
/// through <see cref="ZoneMovementMotionCall"/> (refusal-tolerant while TODO(pose-write) is open).
/// The quantized input state arrives exactly as decoded (<c>ZoneActorMovementState.FromMovement</c>);
/// jump flags 8/16 bookkeeping inside the frame phase stays with <c>TryApplyPhysicalInput</c>
/// (<c>ZoneActorMovementInput.cs:71-88</c>) — the direct path adds only the physical impulse
/// edge, mirrored from that latch (:74-75).</item>
/// </list>
/// One instance per scene generation; constructed by Agent 5 and handed to the controllers
/// through <c>ZoneActorMovementController</c>'s services parameter (wiring note recorded in
/// IMPLEMENTATION_OWNERSHIP.md, change log 2026-09-23).
/// </summary>
public sealed class ZoneMovementServices : IZoneActorMovementServices
{
    private readonly ZoneMovementFrameClock _clock;
    private readonly IZonePhysicsMotion _motion;
    private readonly ZoneMovementInputBindings _bindings;
    private readonly ZoneMovementDiagnostics _diagnostics;

    public ZoneMovementServices(ZoneMovementFrameClock clock,
        IZonePhysicsMotion motion, ZoneMovementInputBindings bindings, ZoneMovementDiagnostics diagnostics,
        int interpolationMode = 0, int bufferMilliseconds = 0)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(motion);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentOutOfRangeException.ThrowIfNegative(interpolationMode);
        ArgumentOutOfRangeException.ThrowIfNegative(bufferMilliseconds);
        _clock = clock; _motion = motion; _bindings = bindings; _diagnostics = diagnostics;
        InterpolationMode = interpolationMode;
        BufferMilliseconds = bufferMilliseconds;
    }

    /// <summary>TODO(model-guard): model+140 psychokinesis guard has no native owner (contract :9-10); permissive leaf until it does.</summary>
    public bool AllowsSynchronization(ZoneUnit owner) => true;

    /// <summary>TODO(model-488-90C): native "model+488 exists and actor+90C is false" (:11-12) has no owner; Characters take the direct branch (PLAN D1).</summary>
    public bool UsesDirectPhysicalMovement(ZoneUnit owner) =>
        owner.Snapshot.Identity.Kind == Protocol.Units.ZoneUnitKind.Character;

    public float GameSeconds => _clock.Current.GameSeconds;
    public uint ServerMilliseconds => _clock.Current.ServerMilliseconds;

    /// <summary>TODO(movement-interpolation-config): identity profile until scene config supplies the native mode.</summary>
    public int InterpolationMode { get; }
    /// <summary>TODO(movement-interpolation-config): zero buffer until scene config supplies the native value.</summary>
    public int BufferMilliseconds { get; }

    /// <summary>
    /// Direct-branch physical application (<c>ZoneActorMovementController.cs:124-131</c>).
    /// Rejections (non-finite client state) and clamp/refusal outcomes are structured diagnostics;
    /// this method never throws for client-authored data and never invents motion for an actor
    /// whose body is gone.
    /// </summary>
    public void ApplyDirectMovement(ZoneUnit owner, ZoneActorMovementState state, float elapsedSeconds)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(state);
        var binding = _bindings.GetOrLiveCreate(owner);
        if (binding is null)
        {
            _diagnostics.ReportSkippedActor(owner.Snapshot.ObjectId,
                "direct movement has no live player body to apply to (managed-only or retired actor).");
            return;
        }

        var limits = binding.MovementLimits;
        if (!limits.HasSpeedLimit && binding.Actor.Model.Stances.Count == 0)
            _diagnostics.ReportMissingLimits(owner.Snapshot.ObjectId);

        // Clock domain: admission latches use THIS frame's sampled scene clock; state.Time stays
        // the client packet clock (IZoneMovementFramePhases doc surface). elapsedSeconds is the
        // controller's GameSeconds delta and is deliberately not mixed into the ms latches.
        _ = elapsedSeconds;
        if (!binding.Admission.TryAdmitState(state, limits, _clock.Current.ServerMilliseconds,
                out var intent, out var refusal))
        {
            _diagnostics.ReportRejectedInput(owner.Snapshot.ObjectId, refusal);
            return;
        }

        // Receiving only stages the native slot request. Its one physical resize attempt is
        // owned by the upcoming prephase, after valid input and before the single scene step.
        binding.RequestStance(intent.Stance);

        // walk / stop → desired world velocity (engine-local frame, IZonePhysicsMotion doc).
        ZoneMovementMotionCall.Velocity(_motion, binding.Handle, intent.Velocity, _diagnostics);

        // turn → authoritative orientation at the live body position (pose write refuses by name
        // while TODO(pose-write) is open; caught + counted, receive loop survives).
        if (intent.Turned)
        {
            try
            {
                if (binding.Actor.TryCopyStatus(out var status))
                    ZoneMovementMotionCall.Pose(_motion, binding.Handle, status.Position,
                        UnitHeadingFromYaw(intent.Yaw), _diagnostics);
            }
            catch (ObjectDisposedException)
            {
                // Pointer==0 retirement observed between liveness checks (adapter purge semantics,
                // ZoneNativePhysicsBackendAdapter.Resolve): skip this write by name.
                _diagnostics.ReportSkippedActor(owner.Snapshot.ObjectId, "body retired (Pointer==0) during direct turn write.");
            }
        }

        // jump rising edge → vertical impulse derived from content
        // (game_jump_height × physics_living_gravity, ZoneActorModelContent.cs:39/:68).
        if (intent.Jump)
        {
            if (limits.JumpSpeed > 0)
                ZoneMovementMotionCall.Impulse(_motion, binding.Handle, new Vector3(0, 0, limits.JumpSpeed), _diagnostics);
            else
                _diagnostics.ReportMissingLimits(owner.Snapshot.ObjectId);
        }
    }

    /// <summary>Yaw-only unit quaternion — the exact helper the protocol uses for housings (<c>AAEmu.Zone.Protocol/Units/UnitHeading.cs:23</c>).</summary>
    private static Quaternion UnitHeadingFromYaw(float yaw) => Protocol.Units.UnitHeading.FromYaw(yaw);
}
