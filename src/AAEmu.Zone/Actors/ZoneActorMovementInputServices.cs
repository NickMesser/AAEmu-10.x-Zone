using System.Numerics;
using static AAEmu.Zone.Actors.ZoneActorMovementCorrectionMath;

using AAEmu.Zone.Content;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.Actors;

/// <summary>Initialized gameplay fields of the native 0xB0-byte movement request. Unproven names retain their offsets.</summary>
public sealed record ZoneActorMovementInputRequest
{
    public uint Flags { get; init; }
    public Vector3 NativeVector04 { get; init; }
    public Vector4 NativeVector10 => default;
    public float NativeScalar20 => 1f;
    public float NativeScalar24 => -1f;
    public int Stance { get; init; }
    public Vector3 Delta { get; init; }
    public Vector3 RotationAngles { get; init; }
    public uint NativeWord44 => 0;
    public Vector4 NativeVector48 => default;
    public float NativeScalar58 => 1f;
    public uint NativeWord5C => 0;
    public string NativeString60 => string.Empty;
    public uint NativeWord68 => 0;
    public float NativeScalar6C => -1f;
    public float NativeScalar70 => 3.1415927f;
    public float NativeScalar74 => .5f;
    public float NativeScalar78 => -1f;
    public ushort NativeWord7C => 1;
    public byte NativeByte7E => 0;
    public string NativeString80 => string.Empty;
    public int NativeWord88 => -1;
    public ulong NativeWord90 => 0;
    public ulong NativeWord98 => 0;
    public uint NativeWordA0 => 0;
    public int Alertness { get; init; }
    public float MovementScalar { get; init; }
}

public interface IZoneActorMovementInputEntity
{
    Vector3 GetPosition();
    Quaternion GetRotation();
}

public interface IZoneActorMovementInputController
{
    /// <summary>Returns retained input state; 39245A30 reads its vector after the facing callbacks.</summary>
    IZoneActorMovementInputState GetState();
    void Apply(ZoneActorMovementInputRequest request);
}

public interface IZoneActorMovementInputState
{
    /// <summary>The three live native input-state floats at +24, +28, +2C.</summary>
    Vector3 NativeVector24 { get; }
}

public interface IZoneActorMovementInputActor
{
    IZoneActorMovementInputController? GetInputController();
    int NativeGroundContact1A4 { get; }
    /// <summary>CActor virtual 100 receives projected state +D5, actor flag bit zero.</summary>
    void SetMovementFlag(bool value);
}

/// <summary>Read-only unit fields used while resolving the owner's target chain.</summary>
public sealed record ZoneActorMovementInputUnit(uint ObjectId, uint TargetId);
public sealed record ZoneActorMovementGroundObject(IZoneActorMovementInputEntity? Entity);

/// <summary>39BFF450 descriptor fields read by 392286B0; field semantics beyond those offsets are not assumed.</summary>
public sealed record ZoneActorMovementSkillDescriptor(uint Native04, uint Native08, uint Native14, uint Native20, uint Native24);

/// <summary>All arguments produced for model virtual 290, excluding its output-ID address.</summary>
public sealed record ZoneActorMovementSkillInstallRequest(uint DescriptorId, uint NativeParameter)
{
    public uint NativeArgument5 => 0; // DAT_3A602CD8
    public uint NativeArgument6 => 0;
    public float NativeArgument7 => 0;
    public float NativeArgument8 => 0;
}

/// <summary>
/// Required scene/model operations for 39245A30. Request production, attachment precedence,
/// rotation math and skill transition selection execute in the movement controller.
/// Native debug rendering/profiling is outside this gameplay input contract.
/// </summary>
public interface IZoneActorMovementInputServices
{
    IZoneActorMovementInputActor? GetPhysicalActor(ZoneUnit owner);
    IZoneActorMovementInputEntity? GetModelEntity(ZoneUnit owner);
    /// <summary>Dedicated unit manager lookup for a nonzero ID; producer implements 3925B4B0's zero-ID guard.</summary>
    ZoneActorMovementInputUnit? FindUnit(uint objectId);
    IZoneActorMovementInputEntity? GetUnitEntity(ZoneActorMovementInputUnit unit);
    ZoneActorMovementGroundObject? FindGimmick(uint objectId);
    ZoneActorMovementGroundObject? FindDoodad(uint objectId);
    void ReportMissingGround(ZoneActorMovementGroundKind kind, uint objectId);
    /// <summary>Read native model +2C for the owner-target lookup; transport identity is not substituted.</summary>
    uint GetModelUnitId(ZoneUnit owner);
    uint GetModelTargetId(ZoneUnit owner);
    Vector3 GetModelFacingDirection(ZoneUnit owner);
    /// <summary>392589F0: the target model's current local engine position, in the entity coordinate system.</summary>
    Vector3 GetUnitPosition(ZoneActorMovementInputUnit unit);
    void SetFacingControl(ZoneUnit owner, bool enabled, int priority, string reason);
    void NotifyJumpEnded(ZoneUnit owner);
    ZoneActorMovementSkillDescriptor? FindSkillController(uint type);
    void RemoveSkillController(ZoneUnit owner, uint controllerId);
    uint InstallSkillController(ZoneUnit owner, ZoneActorMovementSkillInstallRequest request);
}

public enum ZoneActorMovementGroundKind { Unit, Gimmick, Doodad }

// ---------------------------------------------------------------------------
// Turn 2 — concrete client movement input admission and the adapter-backed input
// leaves (registered IMPLEMENTATION_OWNERSHIP.md section 4, change log 2026-09-23).
// Walk/turn/stop/jump intents are validated HERE — finite values first, then
// content-derived speed/turn clamps — before any intent reaches IZonePhysicsMotion.
// ---------------------------------------------------------------------------

/// <summary>
/// Content-derived admission limits for one actor model, resolved from the factory-attached
/// <see cref="ZoneActorModel"/> (content citations):
/// <list type="bullet">
/// <item><b>MaximumSpeed</b> — <c>game_stances.max_speed</c>
/// (<c>Content/ZoneActorModelContent.cs:210/:225/:237</c>); the physicalized stance copies it
/// verbatim as <c>ZoneActorPhysicalizationStance.MaximumSpeed</c> ("speed does not scale",
/// <c>Actors/ZoneActorPhysicalizationRequest.cs:61/:127</c>) and the native stance installer
/// writes it to stance +0x1C (<c>Native/Engine/ZoneNativeModelDescriptors.cs:247</c>). The stance
/// row is the LAST row with <c>StanceId == 2</c> — the exact selection of
/// <c>BuildInitial</c> (<c>ZoneActorPhysicalizationRequest.cs:120-130</c>, "392DF300 installs
/// rows in source order then copies slot1").</item>
/// <item><b>NormalSpeed</b> — <c>game_stances.normal_speed</c>
/// (<c>ZoneActorModelContent.cs:215/:225/:242</c>, native install :251); the run/walk base for
/// request-shaped input, whose <c>MovementScalar</c> (0 / .5 / 1) is computed by the native
/// input core from the decoded delta (<c>Actors/ZoneActorMovementInput.cs:90-91</c>).</item>
/// <item><b>TurnSpeed</b> — <c>actor_models.turn_speed</c>
/// (<c>ZoneActorModelContent.cs:94/:102/:188</c>), installed verbatim on the native model at
/// +0x130 (<c>Native/Engine/ZoneNativeModelDescriptors.cs:228</c>). TODO(turn-speed-units): the
/// native consumer of model+0x130 is not proven in this tree; the content column is treated
/// as DEGREES per second (sibling angle column <c>game_lean_angle</c> is authored in degrees,
/// <c>ZoneActorModelContent.cs:40/:134</c>) and converted to radians here. The turn-3 harness
/// can pin the unit against a known actor_models row; until then the clamp documents its
/// interpretation instead of hiding it.</item>
/// <item><b>JumpSpeed</b> — derived vertical takeoff speed
/// <c>sqrt(2 · |physics_living_gravity| · game_jump_height)</c> from the same model row
/// (<c>ZoneActorModelContent.cs:39/:68/:133/:159</c>, native installs
/// <c>ZoneNativeModelDescriptors.cs</c> dynamics +0x1C/:180). Zero means "no derived jump" —
/// never a fabricated impulse.</item>
/// </list>
/// A non-positive/absent value means the limit is UNRESOLVED: callers report it through
/// <see cref="ZoneMovementDiagnostics"/> and apply no fabricated constant
/// (structured-report policy, PLAN D2 "no fabrication").
/// </summary>
public sealed record ZoneMovementInputLimits(
    float MaximumSpeed, float NormalSpeed, float TurnSpeedRadiansPerSecond, float JumpSpeed)
{
    public bool HasSpeedLimit => MaximumSpeed > 0;
    public bool HasTurnLimit => TurnSpeedRadiansPerSecond > 0;

    /// <summary>Unresolved limits: every clamp inert, every use structured-reported (never a fabricated constant).</summary>
    public static ZoneMovementInputLimits Missing { get; } = new(0f, 0f, 0f, 0f);

    public static ZoneMovementInputLimits FromActorModel(ZoneActorModel? model)
    {
        if (model is null) return Missing;
        ZoneGameStanceDefinition? stance = null;
        foreach (var row in model.Stances)
            if (row.StanceId == 2) stance = row; // last source-order row wins (ZoneActorPhysicalizationRequest.cs:122-130)
        return FromSpeeds(model.Definition, stance?.MaxSpeed ?? 0, stance?.NormalSpeed ?? 0);
    }

    /// <summary>Uses the actor's accepted stance request, including constructor defaults for missing rows.</summary>
    public static ZoneMovementInputLimits FromActorStance(ZoneActorModel model, ZoneActorStanceDefinition stance)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(stance);
        return FromSpeeds(model.Definition, stance.Physicalization.MaximumSpeed, stance.NormalSpeed);
    }

    private static ZoneMovementInputLimits FromSpeeds(ZoneActorModelDefinition definition, float maximumSpeed, float normalSpeed)
    {
        var turnSpeed = definition.TurnSpeed > 0 ? definition.TurnSpeed * (MathF.PI / 180f) : 0f; // TODO(turn-speed-units)
        var gravity = MathF.Abs(definition.PhysicsLivingGravity);
        var jumpHeight = definition.GameJumpHeight;
        var jumpSpeed = gravity > 0 && jumpHeight > 0 ? MathF.Sqrt(2f * gravity * jumpHeight) : 0f;
        return new(maximumSpeed, normalSpeed, turnSpeed, jumpSpeed);
    }
}

public enum ZoneMovementInputKind { Stop, Walk, Turn, Jump }

/// <summary>
/// One admitted walk/turn/stop/jump intent, in the engine-local world frame — the only shape
/// that crosses into <see cref="IZonePhysicsMotion"/>. <see cref="Velocity"/> is m/s,
/// content-clamped; <see cref="Yaw"/> is the (turn-clamped) absolute heading in radians;
/// <see cref="Time"/> keeps the CLIENT packet clock (<c>state.Time</c>) untouched — admission
/// latches use the scene clock argument instead (clock-domain rules of
/// <c>Actors/IZoneMovementFramePhases.cs:22-37</c>: never mix domains).
/// </summary>
public readonly record struct ZoneMovementInputIntent(
    ZoneMovementInputKind Kind,
    Vector3 Velocity,
    float Yaw,
    bool Turned,
    bool Jump,
    bool JumpEnd,
    bool IsMoving,
    sbyte Stance,
    sbyte Alertness,
    byte ActorFlags,
    Vector3 Delta,
    uint Time);

/// <summary>
/// Client movement input admission: finite-value validation, content-derived speed/turn
/// clamps, and the walk/turn/stop/jump intent classification — one instance per unit, retained
/// across admissions so the turn clamp has a previous heading and the jump edge has a latch.
///
/// Validation contract (PLAN section 4 scope item 1):
/// <list type="bullet">
/// <item><b>Finite values or rejection.</b> Every client-authored float (position Z, rotation
/// quaternion, velocity, delta, native state vector, request angles/scalar) must be finite and
/// the rotation nonzero; a violation REJECTS the input with a structured reason — client data
/// never throws out of the receive loop and never reaches physics
/// (<see cref="ZoneMovementDiagnostics.ReportRejectedInput"/>).</item>
/// <item><b>Speed clamp</b> from <see cref="ZoneMovementInputLimits.HasSpeedLimit"/>
/// (<c>game_stances.max_speed</c>): an over-limit velocity is scaled onto the limit sphere and
/// counted (<c>SpeedClamps</c>); a near-zero velocity becomes an explicit STOP intent.</item>
/// <item><b>Turn clamp</b> from <c>actor_models.turn_speed</c>: the per-admission heading
/// change is bounded by <c>TurnSpeed · Δt</c>, with Δt from the SCENE clock
/// (<c>ZoneMovementFrame.ServerMilliseconds</c>) — unsigned, non-wrap-aware subtraction exactly
/// like <c>AdmitTime</c> (<c>Actors/ZoneActorMovementController.cs:34-40</c>). Δt == 0 or
/// Δt &gt; 5 s (discontinuity/backfill) skips the clamp; the first admission has no previous
/// heading and clamps nothing. Over-limit turns are capped and counted (<c>TurnClamps</c>).</item>
/// <item><b>Jump</b> uses the same 8/16 latch the frame-phase input producer uses
/// (<c>Actors/ZoneActorMovementInput.cs:71-88</c>): flag 8 sets the latch (rising edge =
/// impulse), flag 16 ends it.</item>
/// <item><b>Clock domains stay separate</b>: <c>state.Time</c> remains client packet-ms;
/// admission latches take the caller's scene-clock argument; native physics time only advances
/// in <c>ZoneNativeTerrainPhysicsWorld.Step</c>.</item>
/// </list>
/// </summary>
public sealed class ZoneMovementInputAdmission
{
    private readonly ZoneMovementDiagnostics _diagnostics;
    private float? _previousYaw;
    private uint _previousSceneMilliseconds;
    private bool _jumpLatch;

    public ZoneMovementInputAdmission(ZoneMovementDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        _diagnostics = diagnostics;
    }

    /// <summary>Direct-branch admission from the decoded quantized state (walk/turn/stop/jump).</summary>
    public bool TryAdmitState(ZoneActorMovementState state, in ZoneMovementInputLimits limits,
        uint sceneMilliseconds, out ZoneMovementInputIntent intent, out string refusal)
    {
        intent = default;
        if (!TryValidateState(state, out refusal)) return false;

        // --- turn clamp (scene clock domain) ---
        var yaw = YawOf(state.Pose.Rotation);
        var turned = false;
        if (_previousYaw is { } previousYaw)
        {
            var wrapped = WrapPi(yaw - previousYaw);
            turned = MathF.Abs(wrapped) > 1e-4f;
            var deltaMilliseconds = unchecked(sceneMilliseconds - _previousSceneMilliseconds);
            if (limits.HasTurnLimit && deltaMilliseconds is > 0 and <= 5000)
            {
                var maximum = limits.TurnSpeedRadiansPerSecond * (deltaMilliseconds / 1000f);
                if (MathF.Abs(wrapped) > maximum)
                {
                    yaw = previousYaw + (wrapped > 0 ? maximum : -maximum);
                    _diagnostics.ReportTurnClamp();
                }
            }
        }

        // --- speed clamp (content max_speed) + stop classification ---
        var speed = state.Velocity.Length();
        var isMoving = speed > 1e-4f;
        Vector3 velocity;
        if (!isMoving) velocity = Vector3.Zero;
        else
        {
            velocity = state.Velocity;
            if (limits.HasSpeedLimit && speed > limits.MaximumSpeed)
            {
                velocity = state.Velocity * (limits.MaximumSpeed / speed);
                _diagnostics.ReportSpeedClamp();
            }
        }

        // --- jump 8/16 latch, mirror of ZoneActorMovementInput.cs:71-75 ---
        var jump = (state.ActorFlags & 8) != 0;
        var jumpEnd = (state.ActorFlags & 16) != 0;
        var rising = jump && !_jumpLatch;
        if (!_jumpLatch) _jumpLatch = jump;
        else if (jumpEnd) _jumpLatch = false;

        var kind = rising ? ZoneMovementInputKind.Jump
            : isMoving ? ZoneMovementInputKind.Walk
            : turned ? ZoneMovementInputKind.Turn
            : ZoneMovementInputKind.Stop;
        intent = new(kind, velocity, yaw, turned, rising, jumpEnd, isMoving,
            state.Stance, state.Alertness, state.ActorFlags, state.Delta, state.Time);
        _previousYaw = yaw;
        _previousSceneMilliseconds = sceneMilliseconds;
        refusal = string.Empty;
        return true;
    }

    internal static bool TryValidateState(ZoneActorMovementState state, out string refusal)
    {
        ArgumentNullException.ThrowIfNull(state);
        refusal = !float.IsFinite(state.Pose.Position.Z) ? "position Z is not finite"
            : !FiniteNonZeroRotation(state.Pose.Rotation) ? "pose rotation is not finite or is a zero quaternion"
            : !Finite(state.Velocity) || !Finite(state.Delta) || !Finite(state.NativeVector3C)
                ? "velocity/delta/native state vector contains a non-finite component" : string.Empty;
        return refusal.Length == 0;
    }

    internal static bool TryValidateRequest(ZoneActorMovementInputRequest request, Quaternion rotation, out string refusal)
    {
        ArgumentNullException.ThrowIfNull(request);
        refusal = !FiniteNonZeroRotation(rotation) ? "unit rotation is not finite or is a zero quaternion"
            : !Finite(request.Delta) || !Finite(request.NativeVector04) || !Finite(request.RotationAngles) ||
              !float.IsFinite(request.MovementScalar)
                ? "request delta/vector/angles/scalar contains a non-finite component" : string.Empty;
        return refusal.Length == 0;
    }

    /// <summary>
    /// Frame-phase admission from the native-shaped request produced by
    /// <c>ApplyPhysicalInputCore</c> (<c>Actors/ZoneActorMovementInput.cs:165-172</c>).
    /// Direction = pose rotation applied to the decoded delta (forward is the delta's own axis,
    /// the same convention outgoing World writers use, <c>AAEmu.Game/.../Npc.cs:1287-1289</c>),
    /// speed = the core's <c>MovementScalar</c> (0/.5/1, :90-91) times the content stance base,
    /// capped by <c>max_speed</c>.
    /// TODO(buffered-turn-jump): the final request flags mask the jump bits
    /// (<c>ZoneActorMovementInput.cs:167</c> clears them against :82-92) and carry the rotation
    /// only as relative angles, so turn and jump application for BUFFERED (non-direct) actors
    /// are not derivable here — the direct player path applies both
    /// (<c>ZoneMovementServices.ApplyDirectMovement</c>); NPC/NPC-wave producers extend this.
    /// </summary>
    public bool TryAdmitRequest(ZoneActorMovementInputRequest request, Quaternion unitRotation,
        in ZoneMovementInputLimits limits, uint sceneMilliseconds, out ZoneMovementInputIntent intent,
        out string refusal)
    {
        intent = default;
        if (!TryValidateRequest(request, unitRotation, out refusal)) return false;

        var yaw = YawOf(unitRotation);
        var turned = false;
        if (_previousYaw is { } previousYaw)
            turned = MathF.Abs(WrapPi(yaw - previousYaw)) > 1e-4f;

        var deltaSquared = request.Delta.X * request.Delta.X + request.Delta.Y * request.Delta.Y +
            request.Delta.Z * request.Delta.Z;
        var moving = float.IsFinite(request.MovementScalar) && request.MovementScalar > 0 &&
            deltaSquared > .01f; // native threshold, ZoneActorMovementInput.cs:90-91
        Vector3 velocity = Vector3.Zero;
        if (moving)
        {
            var baseSpeed = limits.NormalSpeed > 0 ? limits.NormalSpeed : limits.MaximumSpeed;
            if (baseSpeed <= 0)
                _diagnostics.ReportMissingLimits(0);
            else
            {
                var desired = request.MovementScalar * baseSpeed;
                if (limits.HasSpeedLimit && desired > limits.MaximumSpeed)
                {
                    desired = limits.MaximumSpeed;
                    _diagnostics.ReportSpeedClamp();
                }
                var local = request.Delta / MathF.Sqrt(deltaSquared);
                velocity = Rotate(unitRotation, local) * desired;
            }
        }

        // The request widens stance/alertness to int (ZoneActorMovementInputRequest.cs:12/:33);
        // clamp back to the wire's sbyte domain instead of truncating silently.
        var stance = unchecked((sbyte)Math.Clamp(request.Stance, sbyte.MinValue, sbyte.MaxValue));
        var alertness = unchecked((sbyte)Math.Clamp(request.Alertness, sbyte.MinValue, sbyte.MaxValue));
        intent = new(moving ? ZoneMovementInputKind.Walk : ZoneMovementInputKind.Turn,
            velocity, yaw, turned, Jump: false, JumpEnd: false, moving,
            stance, alertness, unchecked((byte)request.Flags), request.Delta, 0u);
        // Time stays 0: the request shape carries no client packet clock — scene latches use
        // sceneMilliseconds; callers must never backfill one domain into the other.
        _previousYaw = yaw;
        _previousSceneMilliseconds = sceneMilliseconds;
        refusal = string.Empty;
        return true;
    }

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool FiniteNonZeroRotation(Quaternion rotation) =>
        float.IsFinite(rotation.X) && float.IsFinite(rotation.Y) &&
        float.IsFinite(rotation.Z) && float.IsFinite(rotation.W) &&
        float.IsFinite(rotation.LengthSquared()) && rotation.LengthSquared() > 0;

    /// <summary>Heading of a yaw-bearing quaternion — the yaw term of the canonical decomposition (AAEmu.Game Utils MathUtil.cs:149, mirrored zone-side).</summary>
    internal static float YawOf(Quaternion rotation) =>
        MathF.Atan2(2f * (rotation.W * rotation.Z + rotation.X * rotation.Y),
            1f - 2f * (rotation.Y * rotation.Y + rotation.Z * rotation.Z));

    private static float WrapPi(float radians)
    {
        while (radians > MathF.PI) radians -= 2f * MathF.PI;
        while (radians < -MathF.PI) radians += 2f * MathF.PI;
        return radians;
    }
}

/// <summary>
/// Per-unit movement input state: admission latch + the physical/input leaves + the
/// model-entity mirror target. Bound to ONE player-actor generation —
/// <see cref="ZoneMovementInputBindings"/> replaces the binding when the factory resolves a
/// different generation for the same unit ID, so a stale binding can never write a retired body.
/// </summary>
public sealed class ZoneMovementInputBinding
{
    private readonly ZonePlayerActorFactory _actors;
    private readonly IZonePhysicsMotion _motion;
    private readonly ZoneMovementDiagnostics _diagnostics;

    internal ZoneMovementInputBinding(ZoneUnit owner, ZonePlayerActor actor,
        ZonePlayerActorFactory actors, IZonePhysicsMotion motion, ZoneMovementDiagnostics diagnostics,
        ZoneMovementFrameClock clock)
    {
        Owner = owner; Actor = actor; _actors = actors; _motion = motion; _diagnostics = diagnostics;
        Clock = clock;
        Admission = new(diagnostics);
        Controller = new(this);
        PhysicalActor = new(this, diagnostics);
        ModelEntity = new(owner);
        LastMovementFlag = false;
    }

    public ZoneUnit Owner { get; }
    public ZonePlayerActor Actor { get; }
    public ZoneActorHandle Handle => Actor.Handle;
    /// <summary>The shared single-sample frame clock (admission latches use its scene ms; state.Time stays client packet-ms).</summary>
    public ZoneMovementFrameClock Clock { get; }
    public IZonePhysicsMotion Motion => _motion;
    public ZoneMovementDiagnostics Diagnostics => _diagnostics;
    public ZoneMovementInputAdmission Admission { get; }
    public ZoneMovementInputControllerLeaf Controller { get; }
    public ZoneMovementInputActorLeaf PhysicalActor { get; }
    public ZoneMovementInputPoseEntity ModelEntity { get; }
    public ZoneMovementInputLimits MovementLimits => Actor.LivingStances is { } stances
        ? ZoneMovementInputLimits.FromActorStance(Actor.Model, stances.Current)
        : ZoneMovementInputLimits.FromActorModel(Actor.Model);
    /// <summary>
    /// Wire actor flag bit zero, projected by CActor virtual0x100/392D4FB0 into actor+124 bit0x1000.
    /// The original per-frame dynamics writer uses this exact flag to disable positive GameInertia.
    /// </summary>
    public bool LastMovementFlag { get; internal set; }

    /// <summary>Generation-stable liveness: an exact-generation body check before every write (scope item 6: no output for retired actors).</summary>
    public bool EnsureLive()
    {
        if (Actor.IsLive && ReferenceEquals(_actors.FindByUnitObjectId(Owner.Snapshot.ObjectId), Actor))
            return true;
        _diagnostics.ReportSkippedActor(Owner.Snapshot.ObjectId, "player actor generation retired before motion write.");
        return false;
    }

    internal void RequestStance(int slot)
    {
        if (EnsureLive()) Actor.LivingStances?.Request(slot);
    }

    internal void ApplyPendingStance()
    {
        if (!EnsureLive() || Actor.LivingStances is not { } stances || stances.AttemptedInFrame) return;
        if (!stances.TryApplyPending(out var refusal))
            _diagnostics.ReportStanceRefusal(Handle, refusal);
    }

    internal void ApplyLivingDynamics()
    {
        if (!EnsureLive()) return;
        // 392DC900: CActor virtual0x108 reads bit0x1000, produced by the selected wire
        // movement bit through virtual0x100. Only positive GameInertia is replaced with zero.
        // The separate animated-character GameInertiaAccel path is not inferred here.
        var inertia = Actor.Model.Definition.GameInertia;
        if (inertia > 0 && LastMovementFlag) inertia = 0;
        Actor.SetLivingInertia(inertia);
    }

    internal void ApplyFrameVelocity(Vector3 requestedVelocity)
    {
        if (!EnsureLive()) return;
        try
        {
            // A living frame action is mode0. Its requested Z must not be replaced with the
            // actual vertical velocity retained by the generic ordinary-motion adapter.
            if (Actor.TryApplyLivingVelocityRequest(requestedVelocity)) return;
        }
        catch (NotSupportedException error)
        {
            _diagnostics.ReportMotionRefusal("ApplyLivingVelocityRequest", Handle, error.Message);
            return;
        }
        ZoneMovementMotionCall.Velocity(_motion, Handle, requestedVelocity, _diagnostics);
    }
}

/// <summary>Unit-keyed binding cache shared by the input-services and direct-movement leaves (one leaf set per unit, replacement on generation change).</summary>
public sealed class ZoneMovementInputBindings
{
    private readonly Dictionary<ZoneUnit, ZoneMovementInputBinding> _bindings = [];
    private readonly ZonePlayerActorFactory _actors;
    private readonly IZonePhysicsMotion _motion;
    private readonly ZoneMovementDiagnostics _diagnostics;
    private readonly ZoneMovementFrameClock _clock;

    public ZoneMovementInputBindings(ZoneMovementFrameClock clock, ZonePlayerActorFactory actors,
        IZonePhysicsMotion motion, ZoneMovementDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(actors);
        ArgumentNullException.ThrowIfNull(motion);
        ArgumentNullException.ThrowIfNull(diagnostics);
        _clock = clock; _actors = actors; _motion = motion; _diagnostics = diagnostics;
    }

    /// <summary>Returns the live binding for this exact actor generation, or null when no live player body exists (managed-only / retired — structured report, never a fabricated body).</summary>
    public ZoneMovementInputBinding? GetOrLiveCreate(ZoneUnit owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var actor = _actors.FindByUnitObjectId(owner.Snapshot.ObjectId);
        if (actor is null || !actor.IsLive)
        {
            _bindings.Remove(owner);
            return null;
        }
        if (_bindings.TryGetValue(owner, out var binding) && ReferenceEquals(binding.Actor, actor))
            return binding;
        binding = new ZoneMovementInputBinding(owner, actor, _actors, _motion, _diagnostics, _clock);
        _bindings[owner] = binding; // replacement on generation change (handle equality never survives retirement)
        return binding;
    }

    /// <summary>Drops a unit's binding when its movement model retires (phases call this on skipped/retired actors).</summary>
    public void Discard(ZoneUnit owner) => _bindings.Remove(owner);
}

/// <summary>Retained native input-state floats at +24/+28/+2C (contract <c>IZoneActorMovementInputState</c>); seeded from each applied request's vector — TODO(native-input-state-abi): the native producer of +24 is the facing-callback chain (:45), which plain bodies do not run.</summary>
public sealed class ZoneMovementInputControllerLeaf(ZoneMovementInputBinding binding)
    : IZoneActorMovementInputController, IZoneActorMovementInputState
{
    private Vector3 _retained;

    public IZoneActorMovementInputState GetState() => this;
    public Vector3 NativeVector24 => _retained;

    /// <summary>
    /// Applies one frame-phase input request: admission first (finite + content clamps), then
    /// the normal living wish when its registration owns that capability, or the existing
    /// generic velocity path. Refusals are counted; rejected requests never reach physics.
    /// </summary>
    public void Apply(ZoneActorMovementInputRequest request)
    {
        if (!binding.EnsureLive()) return;
        if (!ZoneMovementInputAdmission.TryValidateRequest(request, binding.Owner.Pose.Rotation, out var invalid))
        {
            binding.Diagnostics.ReportRejectedInput(binding.Owner.Snapshot.ObjectId, invalid);
            return;
        }
        // 392EA280 retains the request's signed stance slot only under flag 8. This leaf runs
        // inside the scene prephase; a refused resize keeps the accepted stance and its limits.
        if ((request.Flags & 8) != 0)
            binding.RequestStance(Math.Clamp(request.Stance, sbyte.MinValue, sbyte.MaxValue));
        binding.ApplyPendingStance();
        if (!binding.EnsureLive()) return;
        var limits = binding.MovementLimits;
        if (!binding.Admission.TryAdmitRequest(request, binding.Owner.Pose.Rotation, limits,
                binding.Clock.Current.ServerMilliseconds, out var intent, out var refusal))
        {
            binding.Diagnostics.ReportRejectedInput(binding.Owner.Snapshot.ObjectId, refusal);
            return;
        }
        _retained = request.NativeVector04;
        binding.ApplyFrameVelocity(intent.Velocity);
    }
}

/// <summary>Physical input actor leaf: ground contact from the COPIED body status, controller from the binding (PLAN D2).</summary>
public sealed class ZoneMovementInputActorLeaf(ZoneMovementInputBinding binding, ZoneMovementDiagnostics diagnostics)
    : IZoneActorMovementInputActor
{
    public IZoneActorMovementInputController? GetInputController() =>
        binding.EnsureLive() ? binding.Controller : null;

    /// <summary>
    /// Native ground-contact word (+1A4): nonzero while grounded, 0 while airborne — the exact
    /// complement the jump-mode consumer tests (<c>ZoneActorMovementInput.cs:78-80</c>), sourced
    /// from the copied body status (<c>CopyStatus.IsGrounded</c> = complement of the native
    /// flying bit, <c>Actors/ZonePlayerActorFactory.cs:612-624</c>). A retired body
    /// (<c>Pointer == 0</c>) reports airborne with a structured skip — never a fabricated contact.
    /// </summary>
    public int NativeGroundContact1A4
    {
        get
        {
            try
            {
                return binding.Actor.TryCopyStatus(out var status) && status.IsGrounded ? 1 : 0;
            }
            catch (ObjectDisposedException)
            {
                diagnostics.ReportSkippedActor(binding.Owner.Snapshot.ObjectId,
                    "body retired (Pointer==0) during ground-contact read; reporting airborne.");
                return 0;
            }
        }
    }

    /// <summary>392D4FB0: retain the selected movement bit that controls the CActor inertia override.</summary>
    public void SetMovementFlag(bool value) => binding.LastMovementFlag = value;
}

/// <summary>Model-entity leaf over the committed unit pose (contract <c>IZoneActorMovementInputEntity</c>).</summary>
public sealed class ZoneMovementInputPoseEntity(ZoneUnit unit) : IZoneActorMovementInputEntity
{
    public Vector3 GetPosition() => ZoneMovementPoseMath.ToMeters(unit.Pose.Position);
    public Quaternion GetRotation() => unit.Pose.Rotation;
}

/// <summary>
/// Concrete <c>IZoneActorMovementInputServices</c> — the scene leaves the physical input
/// producer reads (<c>Actors/ZoneActorMovementInput.cs:39-203</c>, PLAN D2):
/// live actors/entities over the player factory, unit lookup over the registry, structured
/// missing-ground reports, model id/target/facing from the unit snapshot + committed pose, and
/// skill-controller hooks returning null (no descriptors exist, so the producer's
/// <c>definition is null</c> path at :193 skips install — nothing is fabricated).
/// One instance per scene generation, wired by Agent 5 alongside
/// <see cref="ZoneMovementServices"/> and the correction services (change-log wiring note).
/// </summary>
public sealed class ZoneMovementInputServices : IZoneActorMovementInputServices
{
    private readonly ZoneUnitRegistry _registry;
    private readonly ZoneMovementDiagnostics _diagnostics;
    private readonly ZoneMovementInputBindings _bindings;

    public ZoneMovementInputServices(ZoneUnitRegistry registry, ZoneMovementDiagnostics diagnostics,
        ZoneMovementInputBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(bindings);
        _registry = registry; _diagnostics = diagnostics; _bindings = bindings;
    }

    /// <summary>Adapter-backed physical actor; null while the generation has no live body (producer skips by name, :52-53).</summary>
    public IZoneActorMovementInputActor? GetPhysicalActor(ZoneUnit owner) =>
        _bindings.GetOrLiveCreate(owner)?.PhysicalActor;

    public IZoneActorMovementInputEntity? GetModelEntity(ZoneUnit owner) =>
        _bindings.GetOrLiveCreate(owner)?.ModelEntity ?? new ZoneMovementInputPoseEntity(owner);

    /// <summary>Dedicated registry lookup for a nonzero object ID (zero-ID guard of 3925B4B0 is the producer's, <c>ZoneActorMovementInput.cs:90</c>); target id from the unit snapshot.</summary>
    public ZoneActorMovementInputUnit? FindUnit(uint objectId)
    {
        if (objectId == 0) return null;
        return _registry.Units.TryGetValue(objectId, out var unit)
            ? new ZoneActorMovementInputUnit(unit.Snapshot.ObjectId, unit.TargetId)
            : null;
    }

    public IZoneActorMovementInputEntity? GetUnitEntity(ZoneActorMovementInputUnit unit) =>
        _registry.Units.TryGetValue(unit.ObjectId, out var resolved)
            ? new ZoneMovementInputPoseEntity(resolved)
            : null;

    /// <summary>TODO(gimmick-doodad): no gimmick/doodad ground world exists in this wave; null makes the producer emit the structured missing-ground report itself (:219-227).</summary>
    public ZoneActorMovementGroundObject? FindGimmick(uint objectId) => null;

    /// <summary>TODO(gimmick-doodad): see <see cref="FindGimmick"/>.</summary>
    public ZoneActorMovementGroundObject? FindDoodad(uint objectId) => null;

    public void ReportMissingGround(ZoneActorMovementGroundKind kind, uint objectId) =>
        _diagnostics.ReportMissingGround(kind, objectId);

    /// <summary>The unit's own object ID (the only identity the scene holds for it); the lookup is inactive while <c>Application.UseOwnerUnitTarget</c> stays false (<c>ZoneActorMovementInput.cs:14-15/:104-115</c>) — TODO(model-2c) for the native model+2C field.</summary>
    public uint GetModelUnitId(ZoneUnit owner) => owner.Snapshot.ObjectId;

    public uint GetModelTargetId(ZoneUnit owner) => owner.TargetId;

    /// <summary>TODO(model-facing): plain bodies publish no facing-direction field; Zero selects the pose/ground rotation branch (:129-138), and target-facing computes its own direction (:141-148).</summary>
    public Vector3 GetModelFacingDirection(ZoneUnit owner) => Vector3.Zero;

    public Vector3 GetUnitPosition(ZoneActorMovementInputUnit unit)
    {
        if (_registry.Units.TryGetValue(unit.ObjectId, out var resolved))
            return ZoneMovementPoseMath.ToMeters(resolved.Pose.Position);
        _diagnostics.ReportSkippedActor(unit.ObjectId, "unit entity position requested after removal; reporting origin.");
        return Vector3.Zero;
    }

    /// <summary>TODO(facing-control): no native facing controller exists for plain bodies; the latch is recorded, not fabricated.</summary>
    public void SetFacingControl(ZoneUnit owner, bool enabled, int priority, string reason) =>
        _diagnostics.ReportFacingControl(owner.Snapshot.ObjectId, enabled);

    public void NotifyJumpEnded(ZoneUnit owner) => _diagnostics.ReportJumpEnded(owner.Snapshot.ObjectId);

    /// <summary>No skill-controller descriptors exist in this tree → null; the producer's <c>definition is null</c> path skips install (<c>ZoneActorMovementInput.cs:186-199</c>).</summary>
    public ZoneActorMovementSkillDescriptor? FindSkillController(uint type) => null;

    /// <summary>Unreachable while <see cref="FindSkillController"/> returns null (the producer only removes an installed id, :176-181); named refusal instead of a stub.</summary>
    public void RemoveSkillController(ZoneUnit owner, uint controllerId) =>
        throw new NotSupportedException(
            "Skill-controller removal requires skill-controller descriptors (none exist in this tree); " +
            "FindSkillController returning null makes installs impossible, so no id can be installed to remove.");

    /// <summary>Unreachable while <see cref="FindSkillController"/> returns null (descriptorId stays 0, <c>ZoneActorMovementInput.cs:193</c>); named refusal instead of a stub.</summary>
    public uint InstallSkillController(ZoneUnit owner, ZoneActorMovementSkillInstallRequest request) =>
        throw new NotSupportedException(
            "Skill-controller installation requires skill-controller descriptors (none exist in this tree); " +
            "the producer's definition-is-null path (:193) never reaches this call.");
}
