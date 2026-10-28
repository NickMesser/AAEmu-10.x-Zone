using System.Numerics;
using System.Runtime.InteropServices;
using AAEmu.Zone.Actors;
using static AAEmu.Zone.Native.Engine.ZoneNativePhysicsModule;

namespace AAEmu.Zone.Native.Physics;

/// <summary>Copied original living fields for an isolated diagnostic; no native pointers escape.</summary>
public readonly record struct ZoneNativeLivingDiagnosticState(ZoneNativeLivingStatus Status,
    Vector3 RawPosition, Vector3 RawVelocity, Vector3 RequestedVelocity, Vector3 Gravity,
    float Radius, float AxialHalfHeight, float HeightCollider, float HeightPivot,
    uint EntityFlags, uint LivingFlags, uint PartColliderFlags, float GroundHeight,
    Vector3 GroundNormal, uint CommandState, float ContactClearance,
    byte SimulationClass, bool InLivingStepList, float StepPeriod, float StepElapsed)
{
    public ZoneNativeLivingMovementDiagnostic Movement { get; init; }
    public bool VelocityBypassPending { get; init; }
}

/// <summary>Copied movement parameters and support/suspension state, independent of actor speed.</summary>
public readonly record struct ZoneNativeLivingMovementDiagnostic(float MinSlideAngle,
    float MaxClimbAngle, float MaxJumpAngle, float MinFallAngle, float MinSlideCosine,
    float MaxClimbCosine, float MaxJumpCosine, float MinFallCosine, float MaximumSupportVelocity,
    float SuspensionOffset, float SuspensionVelocity, float SuspensionAcceleration,
    float SuspensionTime, bool HasRetainedSupport, Vector3 SupportVelocity,
    float GeometryRadius, float GeometryAxialHalfHeight, Vector3 PrimaryPartOffset,
    float Inertia, float InertiaAcceleration, float AirControl, float AirResistance);

public sealed unsafe partial class ZoneNativeTerrainPhysicsWorld
{
    /// <summary>
    /// Diagnostic-only original living-class simulation.3508BD10 flag8 initializes world+688
    /// and dispatches350801B0 over that living list; flags0 only drains/advances the clock.
    /// This retains the existing simulation admission checks and does not change production Step.
    /// </summary>
    public void StepDiagnosticLiving(TimeSpan elapsed)
    {
        var seconds = (float)elapsed.TotalSeconds;
        if (elapsed <= TimeSpan.Zero || !float.IsFinite(seconds) || seconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(elapsed), "A native living diagnostic step requires a positive finite period.");
        using var operation = Enter();
        RequireSimulationStepState();
        if (Marshal.ReadIntPtr(Marshal.ReadIntPtr(_world) + 0x100) != _module.Handle + 0x8bd10)
            throw new InvalidDataException("The native living diagnostic step target changed.");
        try { Virtual<TimeStepCall>(_world, 0x100)(_world, seconds, 8); }
        catch (Exception error) { NativeInvocationFailed(error); throw; }
        CompleteCall();
    }

    /// <summary>
    /// Diagnostic-only type0 position parameters, verified against3506EF20/35028BF0. This uses
    /// the same72-byte pose layout as creation, restricted to unit-scale bodies. Native performs its own ground detachment and
    /// velocity reset; callers must verify read-back before treating a queued request as applied.
    /// </summary>
    public int SetDiagnosticLivingPose(LivingEntity body, Vector3 position, Quaternion orientation)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!Finite(position) || !Finite(new(orientation.X, orientation.Y, orientation.Z)) ||
            !float.IsFinite(orientation.W) || !float.IsFinite(orientation.LengthSquared()) || orientation.LengthSquared() <= 0)
            throw new ArgumentOutOfRangeException(nameof(position), "A native living diagnostic pose requires a finite position and nonzero rotation.");
        using var operation = Enter();
        ReadLivingStatusCore(body);
        if (DiagnosticSingle(body.Pointer + 196) != 1)
            throw new NotSupportedException("The diagnostic living pose profile supports unit-scale bodies only.");
        if (Marshal.ReadIntPtr(Marshal.ReadIntPtr(body.Pointer) + 0x38) != _module.Handle + 0x28bf0)
            throw new InvalidDataException("The native living diagnostic position-parameter target changed.");
        var pose = new LivingPose
        {
            Type = 0, Position = position, Orientation = Quaternion.Normalize(orientation),
            Scale = 1, SimulationClass = int.MinValue, RecalculateBounds = 1, ThreadSafe = int.MinValue
        };
        int result;
        try { result = Virtual<LivingParametersCall>(body.Pointer, 0x38)(body.Pointer, &pose, 0); }
        catch (Exception error) { NativeInvocationFailed(error); throw; }
        CompleteCall();
        return result;
    }

    /// <summary>Diagnostic type4 dynamics bActive(+92), the original living collision-active bit.</summary>
    public int SetDiagnosticLivingCollisionActive(LivingEntity body, ZoneActorPhysicalizationRequest request, bool active)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(request);
        using var operation = Enter();
        ReadLivingStatusCore(body);
        var parameters = new byte[request.Dynamics.Length];
        request.Dynamics.CopyDefinedTo(parameters);
        Word(parameters, 92, active ? 1u : 0u);
        var result = SetLivingParameters(body, parameters, 0);
        CompleteCall();
        return result;
    }

    /// <summary>
    /// Diagnostic-only original64-byte type1 dimensions request.35028BF0 owns validation,
    /// refused requests and any permitted entity-origin shift. The request producer retains its
    /// authored +Z unprojection direction, max(heightPivot,1) distance, and zero postpone flag.
    /// A nonzero return alone does not prove immediate application; copy state before/after.
    /// </summary>
    public int SetDiagnosticLivingDimensions(LivingEntity body, ZoneActorPhysicalizationRequest request, int threadSafe = -1)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(request);
        if (threadSafe is not (-1 or 0))
            throw new ArgumentOutOfRangeException(nameof(threadSafe), "Dimensions diagnostics support the original actor queued(-1) or explicit immediate(0) profiles.");
        if (request.EntityScale != 1 || request.Dimensions.Length != 64 || request.Dimensions.ReadUInt32(0) != 1)
            throw new ArgumentException("The native living dimensions diagnostic requires the verified unit-scale64-byte type1 request.", nameof(request));
        using var operation = Enter();
        ReadLivingStatusCore(body);
        if (DiagnosticSingle(body.Pointer + 196) != 1 ||
            Marshal.ReadIntPtr(Marshal.ReadIntPtr(body.Pointer) + 0x38) != _module.Handle + 0x28bf0)
            throw new InvalidDataException("The native living dimension-parameter profile changed.");
        var parameters = new byte[request.Dimensions.Length];
        request.Dimensions.CopyDefinedTo(parameters);
        var result = SetLivingParameters(body, parameters, threadSafe);
        CompleteCall();
        return result;
    }

    /// <summary>
    /// Original3502A300 action type1: mode0 submits ordinary requested velocity; mode3 adds the
    /// native one-step inertia bypass. This is never a position or recovery write.
    /// Native35038670 validates dir and dt;35023650 owns inline/queued execution. Step owns any
    /// queued command. The returned action result alone does not prove that a queued request ran.
    /// </summary>
    public int SubmitDiagnosticLivingVelocity(LivingEntity body, Vector3 velocity, float requestedTime = 0, int mode = 3)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!Finite(velocity) || !float.IsFinite(velocity.LengthSquared()) || velocity.LengthSquared() > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(velocity), "Native living diagnostic velocity must be finite and at most1000m/s.");
        if (!float.IsFinite(requestedTime) || requestedTime < 0 || requestedTime > 2)
            throw new ArgumentOutOfRangeException(nameof(requestedTime), "Native living action dt must be finite and in[0,2].");
        if (mode is not (0 or 3))
            throw new ArgumentOutOfRangeException(nameof(mode), "The diagnostic supports verified requested-velocity modes0 and3 only.");
        using var operation = Enter();
        ReadLivingStatusCore(body); // Validates the exact owned full body before accessing its ABI.
        if (Marshal.ReadIntPtr(Marshal.ReadIntPtr(body.Pointer) + 0x50) != _module.Handle + 0x2a300 ||
            Marshal.ReadInt32(_module.Handle + 0x37c5f4) != 24)
            throw new InvalidDataException("The original living velocity action does not match the recovered24-byte profile.");
        var action = new DiagnosticLivingVelocityAction
        {
            Type = 1, Direction = velocity, JumpMode = mode, RequestedTime = requestedTime
        };
        int result;
        try { result = Virtual<DiagnosticLivingActionCall>(body.Pointer, 0x50)(body.Pointer, &action, 0); }
        catch (Exception error) { NativeInvocationFailed(error); throw; }
        CompleteCall();
        return result;
    }

    /// <summary>
    /// Original network-state tuple, CLiving virtual0xC0/3502AA60. This is distinct from
    /// Action4 (reset). The original owns its internal type0 pose queue and immediate velocity
    /// writes; callers must capture both the return/immediate state and the next world step.
    /// </summary>
    public int SubmitDiagnosticLivingCorrection(LivingEntity body, ZoneActorMovementCorrectionAction correction)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!Finite(correction.Position) || !Finite(correction.Velocity) ||
            !float.IsFinite(correction.Velocity.LengthSquared()) ||
            correction.OriginalVelocity is { } original && (!Finite(original) || !float.IsFinite(original.LengthSquared())))
            throw new ArgumentOutOfRangeException(nameof(correction), "A native network-state tuple requires finite copied vectors.");
        using var operation = Enter();
        ReadLivingStatusCore(body);
        if (Marshal.ReadIntPtr(Marshal.ReadIntPtr(body.Pointer) + 0xC0) != _module.Handle + 0x2aa60)
            throw new InvalidDataException("The original living network-state tuple target changed.");
        var fields = new byte[84];
        Word(fields, 0, 4);
        WriteVector(4, correction.Position);
        WriteVector(16, correction.Velocity);
        if (correction.OriginalVelocity is { } wish) WriteVector(28, wish);
        else Word(fields, 28, ZoneActorPhysicalizationRequest.UnusedFloatBits);
        fields[68] = correction.SameGround ? (byte)1 : (byte)0;
        fields[69] = correction.GroundTransition ? (byte)1 : (byte)0;
        fields[70] = correction.Native46 ? (byte)1 : (byte)0;
        Word(fields, 72, 0);
        Word(fields, 76, correction.GroundPart);
        Word(fields, 80, correction.GroundPartId);
        int result;
        try
        {
            fixed (byte* pointer = fields)
                result = Virtual<DiagnosticLivingCorrectionCall>(body.Pointer, 0xC0)(body.Pointer, pointer, 0);
        }
        catch (Exception error) { NativeInvocationFailed(error); throw; }
        CompleteCall();
        return result;

        void WriteVector(int offset, Vector3 value)
        {
            Word(fields, offset, BitConverter.SingleToUInt32Bits(value.X));
            Word(fields, offset + 4, BitConverter.SingleToUInt32Bits(value.Y));
            Word(fields, offset + 8, BitConverter.SingleToUInt32Bits(value.Z));
        }
    }

    /// <summary>
    /// Reads the original body fields used by35028BF0/35029A30/3502DED0. The native status pose
    /// and raw simulation pose remain separate because GetStatus can include interpolation.
    /// Field+244 is the world's float contact clearance, initialized to0.01 by350827B0.
    /// </summary>
    public ZoneNativeLivingDiagnosticState CaptureLivingDiagnosticState(LivingEntity body)
    {
        ArgumentNullException.ThrowIfNull(body);
        using var operation = Enter();
        var status = ReadLivingStatusCore(body);
        var parts = Marshal.ReadIntPtr(body.Pointer + 280);
        var geometry = Marshal.ReadIntPtr(body.Pointer + 656);
        if (parts == 0 || geometry == 0 || Marshal.ReadInt32(body.Pointer + 288) < 1)
            throw new InvalidDataException("The native living diagnostic requires its original primary collider part.");
        return new(status, DiagnosticVector(body.Pointer + 408), DiagnosticVector(body.Pointer + 448),
            DiagnosticVector(body.Pointer + 460), DiagnosticVector(body.Pointer + 472),
            DiagnosticSingle(body.Pointer + 544), DiagnosticSingle(body.Pointer + 552),
            DiagnosticSingle(body.Pointer + 532), DiagnosticSingle(body.Pointer + 540),
            unchecked((uint)Marshal.ReadInt32(body.Pointer + 92)),
            unchecked((uint)Marshal.ReadInt32(body.Pointer + 952)),
            unchecked((uint)Marshal.ReadInt32(parts + 84)), DiagnosticSingle(body.Pointer + 572),
            DiagnosticVector(body.Pointer + 484), unchecked((uint)Marshal.ReadInt32(body.Pointer + 64)),
            DiagnosticSingle(_world + 244), Marshal.ReadByte(body.Pointer + 71), InLivingList(body.Pointer),
            DiagnosticSingle(body.Pointer + 924), DiagnosticSingle(body.Pointer + 928))
        {
            VelocityBypassPending = Marshal.ReadByte(body.Pointer + 1120) != 0,
            Movement = new(DiagnosticSingle(body.Pointer + 628), DiagnosticSingle(body.Pointer + 632),
                DiagnosticSingle(body.Pointer + 636), DiagnosticSingle(body.Pointer + 640),
                DiagnosticSingle(body.Pointer + 612), DiagnosticSingle(body.Pointer + 616),
                DiagnosticSingle(body.Pointer + 620), DiagnosticSingle(body.Pointer + 624),
                DiagnosticSingle(body.Pointer + 644), DiagnosticSingle(body.Pointer + 556),
                DiagnosticSingle(body.Pointer + 560), DiagnosticSingle(body.Pointer + 564),
                DiagnosticSingle(body.Pointer + 568), Marshal.ReadIntPtr(body.Pointer + 960) != 0,
                DiagnosticVector(body.Pointer + 1004), DiagnosticSingle(geometry + 72),
                DiagnosticSingle(geometry + 76), DiagnosticVector(parts),
                DiagnosticSingle(body.Pointer + 516), DiagnosticSingle(body.Pointer + 520),
                DiagnosticSingle(body.Pointer + 524), DiagnosticSingle(body.Pointer + 528))
        };
    }

    private bool InLivingList(nint pointer)
    {
        var entry = Marshal.ReadIntPtr(_world + 688);
        var seen = new HashSet<nint>();
        while (entry != 0)
        {
            if (entry == pointer) return true;
            if (!seen.Add(entry) || seen.Count > ushort.MaxValue)
                throw new InvalidDataException("The original living simulation list is cyclic or exceeds the diagnostic bound.");
            entry = Marshal.ReadIntPtr(entry + 104);
        }
        return false;
    }

    private static float DiagnosticSingle(nint address) => BitConverter.Int32BitsToSingle(Marshal.ReadInt32(address));
    private static Vector3 DiagnosticVector(nint address) =>
        new(DiagnosticSingle(address), DiagnosticSingle(address + 4), DiagnosticSingle(address + 8));

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct DiagnosticLivingVelocityAction
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(4)] public Vector3 Direction;
        [FieldOffset(16)] public int JumpMode;
        [FieldOffset(20)] public float RequestedTime;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int DiagnosticLivingActionCall(nint entity, DiagnosticLivingVelocityAction* action, int threadSafe);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int DiagnosticLivingCorrectionCall(nint entity, byte* tuple, int reserved);
}
