using System.Buffers.Binary;
using System.Numerics;
using AAEmu.Zone.Content;

namespace AAEmu.Zone.Simulation;

/// <summary>
/// Defined bytes of a native parameter block. Unwritten bytes are not native zeroes: callers
/// copy only defined bytes into their own request storage. Sentinel bit patterns remain values.
/// </summary>
public sealed class ZoneActorPhysicsParameterFields
{
    private readonly byte[] _values;
    private readonly bool[] _defined;
    public int Length => _values.Length;

    internal ZoneActorPhysicsParameterFields(int length)
    { _values = new byte[length]; _defined = new bool[length]; }

    public bool IsDefined(int offset, int length = 1)
    {
        CheckRange(offset, length);
        for (var i = offset; i < offset + length; i++)
            if (!_defined[i]) return false;
        return true;
    }

    public uint? ReadUInt32(int offset) => IsDefined(offset, 4)
        ? BinaryPrimitives.ReadUInt32LittleEndian(_values.AsSpan(offset, 4)) : null;

    public float? ReadSingle(int offset) => ReadUInt32(offset) is { } bits
        ? BitConverter.UInt32BitsToSingle(bits) : null;

    public byte? ReadByte(int offset) => IsDefined(offset) ? _values[offset] : null;

    public void CopyDefinedTo(Span<byte> destination)
    {
        if (destination.Length < Length) throw new ArgumentException("Native parameter storage is too short.", nameof(destination));
        for (var i = 0; i < Length; i++)
            if (_defined[i]) destination[i] = _values[i];
    }

    internal void Word(int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_values.AsSpan(offset, 4), value);
        Array.Fill(_defined, true, offset, 4);
    }

    internal void Single(int offset, float value) => Word(offset, BitConverter.SingleToUInt32Bits(value));
    internal void Byte(int offset, byte value) { _values[offset] = value; _defined[offset] = true; }
    internal void NullPointer(int offset) { Word(offset, 0); Word(offset + 4, 0); }

    private void CheckRange(int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > Length - length)
            throw new ArgumentOutOfRangeException(nameof(offset));
    }
}

/// <summary>The scaled stance fields installed by 392DF300; speed does not scale.</summary>
public sealed record ZoneActorPhysicalizationStance(Vector3 Size, Vector3 ViewOffset,
    Vector3 ModelOffset, float HeightCollider, float HeightPivot, float MaximumSpeed,
    bool UseCapsule, bool IgnoreCollision);

/// <summary>
/// Content-to-request result for the initial default-stance call of CActor::Physicalize
/// (392E2F20). This neither loads character geometry nor creates a physical entity.
/// </summary>
public sealed class ZoneActorPhysicalizationRequest
{
    public const uint UnusedFloatBits = 0xFFBFFFFF;
    public const uint UnusedIntegerBits = 0x80000000;
    public const int DimensionsPointerOffset = 0x48;
    public const int DynamicsPointerOffset = 0x50;

    public ZoneActorModel ActorModel { get; }
    public float EntityScale { get; }
    public ZoneActorPhysicalizationStance Stance { get; }
    /// <summary>
    /// Scalar/null fields of Entity::Physicalize's 0x78-byte request. Offsets 48 and 50 are
    /// required pointers to Dimensions and Dynamics, not undefined native fields. The backend
    /// must bind those two pointers to its own live request storage before calling the engine.
    /// Other absent bytes are native padding or fields that this producer does not initialize.
    /// </summary>
    public ZoneActorPhysicsParameterFields EntityParameters { get; }
    public ZoneActorPhysicsParameterFields Dimensions { get; }
    public ZoneActorPhysicsParameterFields Dynamics { get; }
    /// <summary>392E2F20 also stores the resolved air-resistance value at actor+8F4.</summary>
    public float ActorAirResistance { get; }

    private ZoneActorPhysicalizationRequest(ZoneActorModel model, float scale,
        ZoneActorPhysicalizationStance stance, ZoneActorPhysicsParameterFields entity,
        ZoneActorPhysicsParameterFields dimensions, ZoneActorPhysicsParameterFields dynamics,
        float actorAirResistance)
    {
        ActorModel = model; EntityScale = scale; Stance = stance;
        EntityParameters = entity; Dimensions = dimensions; Dynamics = dynamics;
        ActorAirResistance = actorAirResistance;
    }

    /// <param name="nativeEntity70">
    /// Exact local-actor identity result used for Entity::Physicalize+70 in 392E2F20. Its
    /// actor/global identity comparison belongs to the caller, not the content database.
    /// </param>
    /// <param name="resolvePhysicalSurface">
    /// Required engine surface lookup boundary (3DEngine+6A0, manager+80, surface+08).
    /// Called once for a nonempty C string. A null result preserves the unused integer;
    /// a real default surface result, including ID zero, is retained unchanged.
    /// </param>
    public static ZoneActorPhysicalizationRequest BuildInitial(ZoneActorModel model, float entityScale,
        bool nativeEntity70, Func<string, ushort?> resolvePhysicalSurface)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(resolvePhysicalSurface);
        var source = model.Definition;
        // CActor constructor 392E6BC0 initializes each stance to a half-size box, zero
        // offsets/heights/speed and false flags. Missing content does not scale that default.
        var stance = new ZoneActorPhysicalizationStance(new(.5f), default, default, 0, 0, 0, false, false);
        // 392DF300 installs rows in source order then copies slot1 (content stance ID2)
        // over the default stance. ID0's earlier default write cannot survive that copy.
        foreach (var row in model.Stances)
        {
            if (row.StanceId != 2) continue;
            stance = new(new(row.SizeX * entityScale, row.SizeY * entityScale, row.SizeZ * entityScale),
                new(row.ViewOffsetX * entityScale, row.ViewOffsetY * entityScale, row.ViewOffsetZ * entityScale),
                new(row.ModelOffsetX * entityScale, row.ModelOffsetY * entityScale, row.ModelOffsetZ * entityScale),
                row.HeightCollider * entityScale, row.HeightPivot * entityScale, row.MaxSpeed,
                row.UseCapsule, row.IgnoreCollision);
        }

        // 392DF860 multiplies in float precision. No finite/positive clamp is present.
        var scaleCubed = (entityScale * entityScale) * entityScale;
        var entity = new ZoneActorPhysicsParameterFields(0x78);
        entity.Word(0x00, 4); // PE_LIVING
        entity.Word(0x04, 0); // character slot
        entity.Single(0x08, -1); // actor+520, constructor392E8820
        entity.Single(0x0C, (float)source.PhysicsMass * scaleCubed);
        entity.Word(0x10, uint.MaxValue);
        entity.Word(0x14, unchecked((uint)source.PhysicsFlags));
        entity.Word(0x18, uint.MaxValue);
        entity.Word(0x1C, 0);
        entity.NullPointer(0x20);
        entity.Word(0x28, uint.MaxValue); // actor+524, constructor392E8820
        entity.Single(0x2C, source.PhysicsStiffnessScale);
        entity.Byte(0x30, 0);
        entity.NullPointer(0x38);
        entity.NullPointer(0x40);
        entity.NullPointer(0x58);
        entity.NullPointer(0x60);
        entity.NullPointer(0x68);
        entity.Byte(0x70, nativeEntity70 ? (byte)1 : (byte)0);

        var dimensions = new ZoneActorPhysicsParameterFields(0x40);
        dimensions.Word(0x00, 1);
        dimensions.Single(0x04, stance.HeightPivot);
        dimensions.Single(0x08, 0);
        dimensions.Single(0x0C, stance.Size.X);
        dimensions.Single(0x10, stance.Size.Y);
        dimensions.Single(0x14, stance.Size.Z);
        dimensions.Single(0x18, stance.HeightCollider);
        dimensions.Single(0x1C, 0);
        dimensions.Single(0x20, 0);
        dimensions.Word(0x24, 0);
        dimensions.Word(0x28, 0);
        dimensions.Single(0x2C, 1);
        // Native comparison preserves an unordered NaN instead of replacing it with one.
        dimensions.Single(0x30, stance.HeightPivot <= 1 ? 1 : stance.HeightPivot);
        dimensions.Word(0x34, stance.UseCapsule ? 1u : 0u);
        dimensions.Word(0x38, 0);
        dimensions.Word(0x3C, 1);

        var dynamics = new ZoneActorPhysicsParameterFields(0x64);
        dynamics.Word(0x00, 4);
        foreach (var offset in new[] { 0x04, 0x08, 0x14, 0x20, 0x38 })
            dynamics.Word(offset, UnusedFloatBits);
        // +18 and +4C are not initialized. In particular the unused vector at +14 has
        // only its sentinel component written; do not fabricate the remaining component.
        dynamics.Single(0x0C, source.PhysicsLivingKAirControl);
        dynamics.Single(0x10, source.PhysicsLivingAirResistance);
        dynamics.Single(0x1C, source.PhysicsLivingGravity);
        dynamics.Single(0x28, (float)source.PhysicsLivingMass * scaleCubed);
        foreach (var offset in new[] { 0x24, 0x2C, 0x48, 0x58, 0x5C, 0x60 })
            dynamics.Word(offset, UnusedIntegerBits);
        dynamics.Single(0x30, source.PhysicsLivingMinSlideAngle);
        dynamics.Single(0x34, source.PhysicsLivingMaxClimbAngle);
        dynamics.Single(0x3C, source.PhysicsLivingMinFallAngle);
        dynamics.Single(0x40, source.PhysicsLivingMaxVelGround);
        dynamics.Single(0x44, source.PhysicsLivingTimeImpulseRecover);
        dynamics.Word(0x50, uint.MaxValue);
        dynamics.Word(0x54, uint.MaxValue);

        var material = ZoneActorModelContent.CString(source.PhysicsLivingColliderMat);
        if (material.Length != 0 && resolvePhysicalSurface(material) is { } surfaceId)
            dynamics.Word(0x2C, surfaceId);
        var airBits = BitConverter.SingleToUInt32Bits(source.PhysicsLivingAirResistance);
        var actorAirResistance = (airBits & 0xFFA00000) == 0xFFA00000 ? 0 : source.PhysicsLivingAirResistance;
        return new(model, entityScale, stance, entity, dimensions, dynamics, actorAirResistance);
    }
}
