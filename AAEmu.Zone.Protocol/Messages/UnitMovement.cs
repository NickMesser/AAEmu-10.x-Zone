using System.Numerics;
using AAEmu.Zone.Protocol.Units;

namespace AAEmu.Zone.Protocol.Messages;

/// <summary>WZ 09: native 3936B6E0, followed by the shared movement serializer 3938B0B0.</summary>
public sealed record UnitMovement(uint ObjectId, UnitMovementData Movement)
{
    public static UnitMovement Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var result = new UnitMovement(reader.ReadBc(), UnitMovementData.Read(ref reader));
        reader.EnsureFullyConsumed();
        return result;
    }

    public byte[] Write()
    {
        var writer = new ZoneWriter();
        writer.WriteBc(ObjectId);
        Movement.Write(writer);
        return writer.ToArray();
    }
}

/// <summary>Retains the exact quantized components; vehicle rotation is quaternion XYZ, not Euler angles.</summary>
public readonly record struct MovementVector16(short X, short Y, short Z)
{
    internal static MovementVector16 Read(ref ZoneReader reader) => new(reader.ReadInt16(), reader.ReadInt16(), reader.ReadInt16());
    internal void Write(ZoneWriter writer) { writer.WriteInt16(X); writer.WriteInt16(Y); writer.WriteInt16(Z); }
    public Vector3 Decode(float maximum) => new(X / 32767f * maximum, Y / 32767f * maximum, Z / 32767f * maximum);

    public Quaternion ToQuaternion(bool defaultKind = false)
    {
        var x = X / 32767f;
        var y = Y / 32767f;
        var z = Z / 32767f;
        // The default branch sums Y before X; retain the native single-precision evaluation order.
        var sum = defaultKind ? y * y + x * x + z * z : x * x + y * y + z * z;
        return new(x, y, z, sum <= 0.9999f ? MathF.Sqrt(1f - sum) : 0f);
    }
}

public readonly record struct MovementVector8(sbyte X, sbyte Y, sbyte Z)
{
    internal static MovementVector8 Read(ref ZoneReader reader) => new(reader.ReadSByte(), reader.ReadSByte(), reader.ReadSByte());
    internal void Write(ZoneWriter writer) { writer.WriteSByte(X); writer.WriteSByte(Y); writer.WriteSByte(Z); }
    public Vector3 Decode() => new(X / 127f, Y / 127f, Z / 127f);
}

public abstract record UnitMovementDetails;
public sealed record UnitDefaultMovement : UnitMovementDetails;
public sealed record UnitGroundMovement(byte Flags, ushort Part, ushort PartId, UnitPosition Position, UnitHeading Heading);
/// <summary>39389080: kind, three signed hundredths, then one bc for kinds 1, 2 or 3.</summary>
public sealed record UnitPushMovement(byte Kind, MovementVector16 Components, uint ObjectId)
{
    public Vector3 Vector => new(Components.X / 100f, Components.Y / 100f, Components.Z / 100f);
}
public sealed record UnitActorMovement(MovementVector8 Delta, sbyte Stance, sbyte Alertness, ushort Flags,
    ushort FallVelocity = 0, UnitGroundMovement? Ground = null, uint GroundObjectId = 0,
    uint ClimbData = 0, UnitPushMovement? Push = null, uint MaxPushedUnitId = 0) : UnitMovementDetails
{
    public float DecodedFallVelocity => (Flags & 0x80) == 0 ? 0f : FallVelocity / 65535f * 128f;
}
public sealed record UnitVehicleMovement(Vector3 AngularVelocity, float Steering, sbyte Throttle,
    IReadOnlyList<float> WheelVelocities) : UnitMovementDetails;
public sealed record UnitShipMovement(Vector3 AngularVelocity, sbyte Steering, sbyte Throttle, sbyte Rpm,
    ushort ZoneId, bool Stuck) : UnitMovementDetails;
public sealed record UnitShipRequestMovement(sbyte Throttle, sbyte Steering) : UnitMovementDetails;
public sealed record UnitTransferMovement(Vector3 AngularVelocity, int Steering, int PathPointIndex,
    float Speed, bool Reverse) : UnitMovementDetails;

public sealed record UnitMovementData(byte Kind, uint Time, byte Flags, uint SkillControllerType, byte Phase,
    UnitPosition Position, MovementVector16 Velocity, MovementVector16 Rotation, UnitMovementDetails Details)
{
    public bool HasPosition => Kind != 5;
    public Vector3 DecodedVelocity => Velocity.Decode(Kind == 1 ? 60f : Kind is 2 or 3 or 4 or 6 ? 30f : 50f);
    public Quaternion DecodedRotation => Kind == 1
        ? new UnitHeading(checked((sbyte)Rotation.X), checked((sbyte)Rotation.Y), checked((sbyte)Rotation.Z)).ToQuaternion()
        : Rotation.ToQuaternion(Kind is not (2 or 3 or 4 or 6));

    internal static UnitMovementData Read(ref ZoneReader reader)
    {
        var kind = reader.ReadByte();
        var time = reader.ReadUInt32();
        var flags = reader.ReadByte();
        var skillControllerType = (flags & 0x10) != 0 ? reader.ReadUInt32() : 0;
        var phase = (flags & 0x10) != 0 ? reader.ReadByte() : (byte)0;
        UnitPosition position = default;
        MovementVector16 velocity = default, rotation = default;
        if (kind != 5)
        {
            position = UnitPosition.Read(ref reader);
            velocity = MovementVector16.Read(ref reader);
            rotation = kind == 1
                ? new(reader.ReadSByte(), reader.ReadSByte(), reader.ReadSByte())
                : MovementVector16.Read(ref reader);
        }
        UnitMovementDetails details;
        switch (kind)
        {
            case 1:
                var delta = MovementVector8.Read(ref reader);
                var stance = reader.ReadSByte();
                var alertness = reader.ReadSByte();
                var actorFlags = reader.ReadUInt16();
                var fallVelocity = (actorFlags & 0x80) != 0 ? reader.ReadUInt16() : (ushort)0;
                UnitGroundMovement? ground = null;
                if ((actorFlags & 0x20) != 0)
                    ground = new(reader.ReadByte(), reader.ReadUInt16(), reader.ReadUInt16(),
                        UnitPosition.Read(ref reader), new UnitHeading(reader.ReadSByte(), reader.ReadSByte(), reader.ReadSByte()));
                var groundObjectId = (actorFlags & 0x60) != 0 ? reader.ReadUInt32() : 0;
                var climb = (actorFlags & 0x8040) != 0 ? reader.ReadUInt32() : 0;
                UnitPushMovement? push = null;
                if ((actorFlags & 0x8000) != 0)
                {
                    var pushKind = reader.ReadByte();
                    var components = MovementVector16.Read(ref reader);
                    push = new(pushKind, components, pushKind is 1 or 2 or 3 ? reader.ReadBc() : 0);
                }
                var maxPushed = (actorFlags & 0x100) != 0 ? reader.ReadUInt32() : 0;
                details = new UnitActorMovement(delta, stance, alertness, actorFlags, fallVelocity, ground, groundObjectId, climb, push, maxPushed);
                break;
            case 2:
            case 3:
                var angularVelocity = ReadVector(ref reader);
                var steering = reader.ReadSingle();
                var throttle = reader.ReadSByte();
                var count = reader.ReadByte();
                // Native storage has 18 slots. Reject an unsafe incoming count before allocating or reading it.
                if (count > 18) throw new InvalidDataException("Vehicle movement exceeds its 18 native wheel slots.");
                var wheels = new float[count];
                for (var index = 0; index < count; index++) wheels[index] = reader.ReadSingle();
                details = new UnitVehicleMovement(angularVelocity, steering, throttle, Array.AsReadOnly(wheels));
                break;
            case 4:
                details = new UnitShipMovement(ReadVector(ref reader), reader.ReadSByte(), reader.ReadSByte(),
                    reader.ReadSByte(), reader.ReadUInt16(), reader.ReadBoolean());
                break;
            case 5:
                details = new UnitShipRequestMovement(reader.ReadSByte(), reader.ReadSByte());
                break;
            case 6:
                details = new UnitTransferMovement(ReadVector(ref reader), reader.ReadInt32(), reader.ReadInt32(),
                    reader.ReadSingle(), reader.ReadBoolean());
                break;
            default:
                details = new UnitDefaultMovement();
                break;
        }
        return new(kind, time, flags, skillControllerType, phase, position, velocity, rotation, details);
    }

    internal void Write(ZoneWriter writer)
    {
        writer.WriteByte(Kind);
        writer.WriteUInt32(Time);
        writer.WriteByte(Flags);
        if ((Flags & 0x10) != 0) { writer.WriteUInt32(SkillControllerType); writer.WriteByte(Phase); }
        if (HasPosition)
        {
            WritePosition(writer, Position);
            Velocity.Write(writer);
            if (Kind == 1)
            {
                writer.WriteSByte(checked((sbyte)Rotation.X));
                writer.WriteSByte(checked((sbyte)Rotation.Y));
                writer.WriteSByte(checked((sbyte)Rotation.Z));
            }
            else Rotation.Write(writer);
        }
        switch (Kind, Details)
        {
            case (1, UnitActorMovement actor):
                actor.Delta.Write(writer);
                writer.WriteSByte(actor.Stance);
                writer.WriteSByte(actor.Alertness);
                writer.WriteUInt16(actor.Flags);
                if ((actor.Flags & 0x80) != 0) writer.WriteUInt16(actor.FallVelocity);
                if ((actor.Flags & 0x20) != 0)
                {
                    var ground = actor.Ground ?? throw new InvalidDataException("Ground movement fields are required by actor flag 20.");
                    writer.WriteByte(ground.Flags); writer.WriteUInt16(ground.Part); writer.WriteUInt16(ground.PartId);
                    WritePosition(writer, ground.Position);
                    writer.WriteSByte(ground.Heading.AxisX); writer.WriteSByte(ground.Heading.AxisY); writer.WriteSByte(ground.Heading.AxisZ);
                }
                if ((actor.Flags & 0x60) != 0) writer.WriteUInt32(actor.GroundObjectId);
                if ((actor.Flags & 0x8040) != 0) writer.WriteUInt32(actor.ClimbData);
                if ((actor.Flags & 0x8000) != 0)
                {
                    var push = actor.Push ?? throw new InvalidDataException("Push movement fields are required by actor flag 8000.");
                    writer.WriteByte(push.Kind); push.Components.Write(writer);
                    if (push.Kind is 1 or 2 or 3) writer.WriteBc(push.ObjectId);
                }
                if ((actor.Flags & 0x100) != 0) writer.WriteUInt32(actor.MaxPushedUnitId);
                break;
            case (2 or 3, UnitVehicleMovement vehicle):
                WriteVector(writer, vehicle.AngularVelocity); writer.WriteSingle(vehicle.Steering); writer.WriteSByte(vehicle.Throttle);
                var count = Math.Min(vehicle.WheelVelocities.Count, 18);
                writer.WriteByte((byte)count);
                for (var index = 0; index < count; index++) writer.WriteSingle(vehicle.WheelVelocities[index]);
                break;
            case (4, UnitShipMovement ship):
                WriteVector(writer, ship.AngularVelocity); writer.WriteSByte(ship.Steering); writer.WriteSByte(ship.Throttle);
                writer.WriteSByte(ship.Rpm); writer.WriteUInt16(ship.ZoneId); writer.WriteBoolean(ship.Stuck);
                break;
            case (5, UnitShipRequestMovement request):
                writer.WriteSByte(request.Throttle); writer.WriteSByte(request.Steering);
                break;
            case (6, UnitTransferMovement transfer):
                WriteVector(writer, transfer.AngularVelocity); writer.WriteInt32(transfer.Steering); writer.WriteInt32(transfer.PathPointIndex);
                writer.WriteSingle(transfer.Speed); writer.WriteBoolean(transfer.Reverse);
                break;
            case (not (1 or 2 or 3 or 4 or 5 or 6), UnitDefaultMovement):
                break;
            default:
                throw new InvalidDataException("Movement details do not match the native movement kind.");
        }
    }

    private static Vector3 ReadVector(ref ZoneReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    private static void WriteVector(ZoneWriter writer, Vector3 value) { writer.WriteSingle(value.X); writer.WriteSingle(value.Y); writer.WriteSingle(value.Z); }
    private static void WritePosition(ZoneWriter writer, UnitPosition position)
    {
        if (position.QuantizedZ > 0x3FFFFF) throw new InvalidDataException("Movement height exceeds the packed 22-bit field.");
        writer.WriteUInt32(position.QuantizedX); writer.WriteUInt32(position.QuantizedY); writer.WriteUInt16((ushort)position.QuantizedZ);
        writer.WriteByte((byte)((position.QuantizedZ >> 16) | (position.NegativeX ? 0x80u : 0) | (position.NegativeY ? 0x40u : 0)));
    }
}
