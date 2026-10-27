using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using static AAEmu.Zone.Simulation.ZoneNativePhysicsModule;

namespace AAEmu.Zone.Simulation;

/// <summary>
/// Acquires an independent lease preventing retirement of the exact native foreign-data owner.
/// For kind 2 this must be a real IEntity. A GC root or an opaque token is not an IEntity lease.
/// </summary>
public interface IZoneNativePhysicsForeignData
{
    IZoneNativePhysicsForeignDataLease AcquirePhysicsLease();
}

/// <summary>
/// Ownership of each acquired lease transfers to the physical world. Pointer and Kind are captured
/// once; the native owner must remain valid until Dispose, including after deferred body destruction.
/// The provider and its other clients must not dispose the acquired lease independently.
/// A standalone world retains it through native world Release. An engine borrower instead
/// synchronously detaches its body and purges stale events before releasing the lease.
/// </summary>
public interface IZoneNativePhysicsForeignDataLease : IDisposable
{
    nint Pointer { get; }
    short Kind { get; }
}

/// <summary>
/// Capability implemented only by the real engine's IEntity service. Engine identifies the exact
/// native owner, and the independently retained IEntity has an exclusive plain-body association:
/// no native entity proxy, skeleton or other client may separately own or destroy this body.
/// Dispose retires that provider association after the physics adapter has synchronously destroyed
/// the body and purged its stale logged events, under the already-held engine gate while
/// EntitySystem is still alive; cleanup must not reenter a public engine operation. It must not
/// destroy the physical body again or escrow the whole engine until native world Release.
/// </summary>
internal interface IZoneNativeEnginePhysicsForeignDataLease : IZoneNativePhysicsForeignDataLease
{
    ZoneNativeEngine Engine { get; }
    uint EntityId { get; }
}

/// <summary>Copied fields from native position status 1 and living status 2; no native pointers escape.</summary>
public readonly record struct ZoneNativeLivingStatus(Vector3 Position, Quaternion Orientation,
    Vector3 Velocity, Vector3 UnconstrainedVelocity, bool IsFlying, float FlyingTime);

public sealed unsafe partial class ZoneNativeTerrainPhysicsWorld
{
    /// <summary>
    /// The exact guard message, exposed as a constant so the admission loop can recognize the
    /// reserved-identity refusal by identity instead of a fragile literal copy. The guard itself
    /// and the "reserved until world teardown" policy (:59-63) are unchanged by this exposure.
    /// </summary>
    public const string ReservedIdentityMessage = "This native ushort identity remains reserved until the owning world is released.";

    // Retired IDs live through the native world generation. Standalone foreign leases also wait
    // for world Release; borrowed leases retire after synchronous destruction and event purging.
    private readonly List<LivingEntity> _livingEntities = [];

    /// <summary>
    /// Creates the native no-skeleton PE_LIVING body, with the actor's already-scaled dimensions and
    /// dynamics. This does not physicalize character slots, install skeleton geometry, emit entity
    /// events or implement the remaining actor lifecycle. Entity identity truncates to ushort as in
    /// 32077490. Every claimed ushort ID remains reserved until world teardown, including after
    /// body disposal and borrowed adapter recreation; reuse after deferred native destruction has
    /// not been admitted. Null foreignData is the explicit unbound low-level body path, not an actor
    /// binding. An engine borrower accepts bound data only from that engine's internal IEntity
    /// service with exclusive plain-body association and independently retained native lifetime.
    /// </summary>
    public LivingEntity CreateLivingEntity(ZoneActorPhysicalizationRequest request, Vector3 position,
        Quaternion orientation, uint entityId, IZoneNativePhysicsForeignData? foreignData = null,
        bool nativeEntityFlag30 = false)
    {
        using var operation = Enter();
        ArgumentNullException.ThrowIfNull(request);
        if (!Finite(position) || !Finite(new(orientation.X, orientation.Y, orientation.Z)) ||
            !float.IsFinite(orientation.W) || !float.IsFinite(orientation.LengthSquared()) ||
            orientation.LengthSquared() <= 0 || !float.IsFinite(request.EntityScale) || request.EntityScale <= 0)
            throw new ArgumentException("Native living pose and scale must be finite with a nonzero rotation and positive scale.");
        var physicalId = unchecked((ushort)entityId);
        if (_engineLease is null ? _livingEntities.Any(entity => entity.PhysicalEntityId == physicalId) :
            _engineLease.ReservedLivingIds.Contains(physicalId))
            throw new InvalidOperationException(ReservedIdentityMessage);
        var dimensions = new byte[request.Dimensions.Length];
        var dynamics = new byte[request.Dynamics.Length];
        request.Dimensions.CopyDefinedTo(dimensions);
        request.Dynamics.CopyDefinedTo(dynamics);
        _engineLease?.ReservedLivingIds.Add(physicalId);
        var body = new LivingEntity(this, physicalId);
        _livingEntities.Add(body); // All partial construction ownership is rooted before native work.
        try
        {
            if (foreignData is not null)
            {
                body.Lease = foreignData.AcquirePhysicsLease() ?? throw new InvalidOperationException("The native foreign-data provider returned no lease.");
                _engineLease?.ValidateForeignLease(body.Lease, entityId);
                body.ForeignPointer = body.Lease.Pointer;
                body.ForeignKind = body.Lease.Kind;
                if (body.ForeignPointer == 0)
                    throw new InvalidOperationException("A supplied native foreign-data lease must identify a live native owner.");
                if (_engineLease is not null && body.ForeignKind != 2)
                    throw new InvalidOperationException("The engine IEntity capability must expose native foreign-data kind 2.");
            }
            var pose = new LivingPose
            {
                Type = 0, Position = position, Orientation = orientation, Scale = request.EntityScale,
                SimulationClass = int.MinValue, RecalculateBounds = 1, ThreadSafe = int.MinValue
            };
            try
            {
                body.Pointer = Virtual<CreateLivingCall>(_world, 0xb0)(_world, 4, &pose,
                    body.ForeignPointer, body.ForeignKind, physicalId, request.EntityParameters.ReadByte(0x70) ?? 0);
            }
            catch (Exception error) { NativeInvocationFailed(error); throw; }
            if (body.Pointer == 0) throw new InvalidOperationException("The native living entity factory returned null.");
            ValidateBorrowedLivingEntity(body);
            try { Virtual<LivingReferenceCall>(body.Pointer, 0x10)(body.Pointer); body.ReferenceHeld = true; }
            catch (Exception error) { NativeInvocationFailed(error); throw; }
            var authoredFlags = request.EntityParameters.ReadUInt32(0x14) ?? 0;
            if (authoredFlags != 0) SetLivingFlags(body, authoredFlags, 0);
            // Native3207CFD0 simulation params: only these fields are authored; unused vector
            // components/padding in the zeroed call-local buffer are not fabricated input values.
            var simulation = new byte[0x54];
            Word(simulation, 0, 10); Word(simulation, 4, 0x80000000);
            foreach (var offset in new[] { 8, 0x0c, 0x10, 0x14, 0x20, 0x24, 0x30, 0x34, 0x38, 0x44, 0x50 })
                Word(simulation, offset, 0xffbfffff);
            Word(simulation, 0x3c, 1); Word(simulation, 0x40, 0x80000000);
            SetLivingParameters(body, simulation, 0);
            SetLivingFlags(body, 0x02000000, 0);
            body.DimensionsAccepted = SetLivingParameters(body, dimensions, 0) != 0;
            body.DynamicsAccepted = SetLivingParameters(body, dynamics, 0) != 0;
            if (nativeEntityFlag30) SetLivingFlags(body, 0x10000000, -1);
            SetLivingFlags(body, 0x09000000, 0);
            CompleteCall();
            body.Configured = true;
            return body;
        }
        catch (Exception error) { _fault = error; throw; }
    }

    /// <summary>
    /// Releases ONE retired actor's ushort identity so the same character can re-enter this world.
    ///
    /// This is deliberately narrow, and it exists because the measured alternative was that re-entry
    /// could never work at all. Live evidence (round 58), one re-entry attempt on zone 129:
    /// <code>
    /// attempt=1 nativeEntityId=66319  ushort=783 reserved=1
    /// attempt=2 nativeEntityId=131855 ushort=783 reserved=1
    /// attempt=3 nativeEntityId=197391 ushort=783 reserved=1
    /// </code>
    /// The engine's allocator advances the HIGH half of its id by 0x10000 per spawn, so the low 16
    /// bits never move and every fresh spawn truncates onto the same reserved identity. Respawning
    /// therefore cannot escape the collision, and <c>reserved=1</c> says the only id in question is
    /// the one the SAME player just vacated.
    ///
    /// Guards, because "reserved until world teardown" was a deliberate policy and this is the one
    /// measured case where it is provably unnecessary:
    /// <list type="bullet">
    /// <item>The identity must currently be reserved.</item>
    /// <item>NO living body may still hold it - a released identity that is still in use would hand
    /// the same physical id to two actors.</item>
    /// <item>Standalone worlds (no engine lease) are unaffected: they are torn down wholesale.</item>
    /// </list>
    /// </summary>
    /// <returns>True when the identity was released; false when it was not reserved or still in use.</returns>
    public bool ReleaseRetiredPhysicalId(ushort physicalId)
    {
        using var operation = Enter();
        if (_engineLease is null) return false;              // standalone worlds tear down wholesale
        if (!_engineLease.ReservedLivingIds.Contains(physicalId)) return false;
        // LIVENESS is the native pointer, not list membership: a retired body keeps its wrapper until
        // world teardown but has Pointer == 0 and is fully detached (RetireBorrowedLivingEntity,
        // :103 and the detachment assertions at :99-101). Releasing while a body still holds native
        // memory would hand one physical id to two actors, so that case stays refused.
        if (_livingEntities.Any(entity => entity.PhysicalEntityId == physicalId && entity.Pointer != 0)) return false;
        _engineLease.ReservedLivingIds.Remove(physicalId);
        return true;
    }

    /// <summary>
    /// Read-only mirror of the reservation guard inside CreateLivingEntity (:91-94): the exact
    /// same ushort truncation and the exact same membership test, for admission pre-checks.
    /// This never adds, removes or weakens a reservation, and no ID is ever released before
    /// world teardown — a reserved result tells the caller to spawn a fresh native entity
    /// instead (ZonePlayerActorFactory's fresh-ID retry, bounded to 8 attempts).
    /// </summary>
    public bool IsPhysicalIdReserved(uint entityId)
    {
        using var operation = Enter();
        var physicalId = unchecked((ushort)entityId);
        return _engineLease is null
            ? _livingEntities.Any(entity => entity.PhysicalEntityId == physicalId)
            : _engineLease.ReservedLivingIds.Contains(physicalId);
    }

    /// <summary>
    /// Read-only count of reserved ushort identities for this world generation (engine lease:
    /// the reservation set; standalone: every claimed identity still rooted in the living list),
    /// surfaced for structured exhaustion diagnostics only. Reservation logic is untouched.
    /// </summary>
    public int ReservedPhysicalIdCount
    {
        get
        {
            using var operation = Enter();
            return _engineLease is null
                ? _livingEntities.Select(entity => entity.PhysicalEntityId).Distinct().Count()
                : _engineLease.ReservedLivingIds.Count;
        }
    }

    private void SetLivingFlags(LivingEntity body, uint flags, int threadSafe)
    {
        var values = new byte[16];
        Word(values, 0, 15); Word(values, 4, 0x80000000);
        Word(values, 8, flags); Word(values, 12, 0x80000000);
        SetLivingParameters(body, values, threadSafe);
    }

    private int SetLivingParameters(LivingEntity body, byte[] parameters, int threadSafe)
    {
        int result;
        try
        {
            fixed (byte* pointer = parameters)
                result = Virtual<LivingParametersCall>(body.Pointer, 0x38)(body.Pointer, pointer, threadSafe);
        }
        catch (Exception error) { NativeInvocationFailed(error); throw; }
        // Original proxy ignores these results; type10 is emitted even though living returns zero.
        return result;
    }

    private ZoneNativeLivingStatus GetLivingStatus(LivingEntity body)
    {
        using var operation = Enter();
        return ReadLivingStatusCore(body);
    }

    /// <summary>
    /// The status read itself, with the operation gate ALREADY HELD by the caller — the native
    /// calls reenter the engine, so this must never open its own operation.
    /// </summary>
    private ZoneNativeLivingStatus ReadLivingStatusCore(LivingEntity body)
    {
        if (!ReferenceEquals(body.Owner, this) || body.Pointer == 0 || !body.Configured || body.ReferenceReleased)
            throw new ObjectDisposedException(nameof(LivingEntity));
        ValidateBorrowedLivingEntity(body);
        // 350724C0 writes through +77, including borrowed geometry pointers which are never exposed.
        var pose = new byte[0x78]; Word(pose, 0, 1); Word(pose, 4, uint.MaxValue); Word(pose, 8, uint.MaxValue);
        // 35029A30 status2 writes through +A7, including borrowed native pointers likewise discarded.
        var living = new byte[0xa8]; Word(living, 0, 2);
        int poseResult, livingResult;
        try
        {
            var getStatus = Virtual<LivingStatusCall>(body.Pointer, 0x48);
            fixed (byte* posePointer = pose) poseResult = getStatus(body.Pointer, posePointer);
            fixed (byte* livingPointer = living) livingResult = getStatus(body.Pointer, livingPointer);
        }
        catch (Exception error) { NativeInvocationFailed(error); throw; }
        CompleteCall();
        if (poseResult == 0 || livingResult == 0) throw new InvalidOperationException("Native living status was not available.");
        return new(ReadVector(pose, 0x18), new(ReadSingle(pose, 0x3c), ReadSingle(pose, 0x40),
            ReadSingle(pose, 0x44), ReadSingle(pose, 0x48)), ReadVector(living, 0x18),
            ReadVector(living, 0x24), BinaryPrimitives.ReadInt32LittleEndian(living.AsSpan(4)) != 0, ReadSingle(living, 8));
    }

    private void DestroyLivingEntity(LivingEntity body)
    {
        if (body.Pointer == 0 && (_engineLease is null || body.Lease is null)) return;
        // Fully retired wrappers tolerate cleanup after world disposal. A borrowed wrapper whose
        // native body retired but provider cleanup is pending still enters the retained owner gate.
        using var operation = Enter(allowFaulted: true);
        if (!ReferenceEquals(body.Owner, this)) throw new InvalidOperationException("The living entity belongs to another native world.");
        RetireLivingEntity(body);
    }

    private void RetireLivingEntities()
    {
        foreach (var body in _livingEntities) RetireLivingEntity(body);
    }

    private void RetireLivingEntity(LivingEntity body)
    {
        if (_engineLease is not null)
        {
            RetireBorrowedLivingEntity(body);
            return;
        }
        if (body.Pointer == 0) return;
        // Any existing callback error has already surfaced to the caller. A new cleanup call
        // must report only errors produced during that call, so safe retirement can recover.
        _callbackError = null;
        body.Configured = false;
        try
        {
            // Plain proxy3207E070 owns exactly one AddRef, releases it, then requests world destroy.
            // No skeleton provider shares this body. Keep the lease until the owning world retires.
            if (body.ReferenceHeld && !body.ReferenceReleased)
            {
                Virtual<LivingReferenceCall>(body.Pointer, 0x18)(body.Pointer);
                body.ReferenceReleased = true;
            }
            var result = Virtual<DestroyLivingCall>(_world, 0xc0)(_world, body.Pointer, 0, 0);
            if (result == 0) throw new InvalidOperationException("Native living destruction did not complete its admission.");
            body.Pointer = 0;
        }
        catch (Exception error) { NativeInvocationFailed(error); throw; }
        CompleteCall();
    }

    private void ReleaseLivingLeases()
    {
        // Called only after native world Release returned. Lease failure retains the module claim.
        foreach (var body in _livingEntities)
        {
            body.Lease?.Dispose();
            body.Lease = null;
        }
        _livingEntities.Clear();
    }

    /// <summary>An owned no-skeleton body. Dispose requests native retirement; status is invalid afterward.</summary>
    public sealed class LivingEntity : IDisposable
    {
        internal ZoneNativeTerrainPhysicsWorld Owner { get; }
        internal nint Pointer;
        internal nint ForeignPointer;
        internal short ForeignKind = -1;
        internal IZoneNativePhysicsForeignDataLease? Lease;
        internal bool ReferenceHeld;
        internal bool ReferenceReleased;
        internal bool Configured;
        internal bool LeaseReleaseStarted;
        public ushort PhysicalEntityId { get; }
        public bool DimensionsAccepted { get; internal set; }
        public bool DynamicsAccepted { get; internal set; }
        internal LivingEntity(ZoneNativeTerrainPhysicsWorld owner, ushort id) { Owner = owner; PhysicalEntityId = id; }
        public ZoneNativeLivingStatus GetStatus() => Owner.GetLivingStatus(this);
        public void Dispose() => Owner.DestroyLivingEntity(this);
    }

    private static void Word(Span<byte> data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data[offset..], value);
    private static float ReadSingle(byte[] data, int offset) => BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)));
    private static Vector3 ReadVector(byte[] data, int offset) => new(ReadSingle(data, offset), ReadSingle(data, offset + 4), ReadSingle(data, offset + 8));

    [StructLayout(LayoutKind.Explicit, Size = 0x48)]
    private struct LivingPose
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(4)] public Vector3 Position;
        [FieldOffset(0x10)] public Quaternion Orientation;
        [FieldOffset(0x20)] public float Scale;
        [FieldOffset(0x28)] public nint Matrix;
        [FieldOffset(0x30)] public nint Matrix3;
        [FieldOffset(0x38)] public int SimulationClass;
        [FieldOffset(0x3c)] public int RecalculateBounds;
        [FieldOffset(0x40)] public byte BoundsOnly;
        [FieldOffset(0x44)] public int ThreadSafe;
    }

    // ------------------------------------------------------------------
    // Living move action (RE-VERIFIED 2026-09-23; evidence below).
    // ------------------------------------------------------------------

    /// <summary>
    /// pe_action as the native action handler reads it (cryphysics_dedicate.dll base 0x35000000,
    /// body vtable +0x50 = 3502A300, whose own literal is "CLivingEntity:Action(action_move)"):
    /// type 1 = move, mode 0/3 = absolute position, mode 2 = relative delta; both set the dirty
    /// flag body+0x77 |= 2. Decompile and reproduce steps: AAEmuResearch/ZoneAuthority/pose-write.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct LivingAction
    {
        public int Type;
        public float X;
        public float Y;
        public float Z;
        public int Mode;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int LivingActionCall(nint entity, void* action, int threadSafe);

    /// <summary>Native body class byte; the producer applies INLINE only when it is greater than this.</summary>
    private const byte InlineActionClassFloor = 6;

    /// <summary>
    /// Applies the recovered living MOVE action and VERIFIES it against the native read-back.
    ///
    /// The producer (35023650) takes the inline branch — apply now, no queue — only when
    /// <c>body+0x47 &gt; 6</c> and <c>threadSafe &gt; -1</c>; otherwise the record is deferred to a
    /// pump that only exists inside the native frame loop, and the body does not move. Rather than
    /// assume, this method applies and then READS THE BODY BACK: a landed move returns true with
    /// the observed status, and anything else returns false with the exact refusal — the body class
    /// byte, the native return code and the before/after positions. Motion is therefore never
    /// reported unless the engine actually moved the body.
    ///
    /// Velocity and impulse are deliberately NOT routed here: the recovered action ABI is
    /// positional, so a velocity is expressed as a per-frame relative move by the frame owner.
    /// </summary>
    public bool TryApplyLivingMove(LivingEntity body, Vector3 value, bool absolute,
        out ZoneNativeLivingStatus observed, out string refusal)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!Finite(value)) throw new ArgumentOutOfRangeException(nameof(value),
            "A living move requires a finite world vector (engine-local frame).");
        ZoneNativeLivingStatus before, after;
        byte bodyClass;
        int result;
        // ONE operation gate for the whole apply-and-verify: the native status and action calls
        // reenter the engine, so nothing inside may open a second operation.
        using (var operation = Enter())
        {
            before = ReadLivingStatusCore(body);
            bodyClass = Marshal.ReadByte(body.Pointer + 0x47);
            if (bodyClass <= InlineActionClassFloor)
            {
                observed = before;
                refusal = $"the native body class byte 0x{bodyClass:X2} is at or below the inline-action floor " +
                    $"0x{InlineActionClassFloor:X2}, so action move is DEFERRED to the native frame pump and the " +
                    $"body did not move (position stayed {before.Position})";
                return false;
            }
            var vtable = Marshal.ReadIntPtr(body.Pointer);
            var slot = vtable == 0 ? 0 : Marshal.ReadIntPtr(vtable + 0x50);
            if (slot == 0)
            {
                observed = before;
                refusal = "the living proxy published a null action entry at vtable +0x50; no move was applied";
                return false;
            }
            var action = new LivingAction { Type = 1, X = value.X, Y = value.Y, Z = value.Z, Mode = absolute ? 0 : 2 };
            var call = Marshal.GetDelegateForFunctionPointer<LivingActionCall>(slot);
            try
            {
                result = call(body.Pointer, &action, 0);
            }
            catch (Exception error) { NativeInvocationFailed(error); throw; }
            CompleteCall();
            after = ReadLivingStatusCore(body);
        }
        observed = after;
        var expected = absolute ? value : before.Position + value;
        if (result == 0 || !Landed(after.Position, expected))
        {
            refusal = $"action move (type 1, mode {(absolute ? 0 : 2)}) returned {result} and the read-back did not " +
                $"land: before={before.Position} requested={value} expected={expected} after={after.Position}";
            return false;
        }
        refusal = string.Empty;
        return true;
    }

    private static bool Landed(Vector3 observed, Vector3 expected) =>
        Math.Abs(observed.X - expected.X) < 0.001f && Math.Abs(observed.Y - expected.Y) < 0.001f &&
        Math.Abs(observed.Z - expected.Z) < 0.001f;
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint CreateLivingCall(nint world, int type, LivingPose* pose, nint foreignData, int foreignKind, int id, byte nativeEntity70);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int LivingReferenceCall(nint entity);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int LivingParametersCall(nint entity, void* parameters, int threadSafe);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int LivingStatusCall(nint entity, void* status);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int DestroyLivingCall(nint world, nint entity, int mode, int threadSafe);
}
