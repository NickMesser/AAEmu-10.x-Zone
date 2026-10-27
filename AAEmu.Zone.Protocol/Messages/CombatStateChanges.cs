namespace AAEmu.Zone.Protocol.Messages;

/// <summary>Uncompressed native world position (39B113A0), in signed 2^44 fixed-point X/Y units.</summary>
public readonly record struct UnitWorldPosition(long FixedX, long FixedY, float Z)
{
    public double X => FixedX / 17592186044416.0;
    public double Y => FixedY / 17592186044416.0;

    internal static UnitWorldPosition Read(ref ZoneReader reader) => new(reader.ReadInt64(), reader.ReadInt64(), reader.ReadSingle());
    internal void Write(ZoneWriter writer)
    {
        writer.WriteInt64(FixedX); writer.WriteInt64(FixedY); writer.WriteSingle(Z);
    }
}

/// <summary>WZUnitDeath (3936BD80): death notification; HP/MP arrive independently.</summary>
public readonly record struct UnitDeath(uint ObjectId)
{
    public static UnitDeath Read(ReadOnlySpan<byte> body) => new(CombatStateWire.ReadObjectId(body));
    public ZoneFrame ToFrame() => CombatStateWire.Frame(WzOpcodes.UnitDeath, ObjectId);
}

/// <summary>WZUnitResurrection (3936BDD0): full world position and yaw in radians, 27 body bytes.</summary>
public readonly record struct UnitResurrection(uint ObjectId, UnitWorldPosition Position, float Yaw)
{
    public static UnitResurrection Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var value = new UnitResurrection(reader.ReadBc(), UnitWorldPosition.Read(ref reader), reader.ReadSingle());
        reader.EnsureFullyConsumed();
        value.Validate();
        return value;
    }
    public ZoneFrame ToFrame()
    {
        Validate();
        var writer = new ZoneWriter();
        writer.WriteBc(ObjectId); Position.Write(writer); writer.WriteSingle(Yaw);
        return new(WzOpcodes.UnitResurrection, writer.ToArray());
    }
    private void Validate()
    {
        if (!float.IsFinite(Position.Z) || !float.IsFinite(Yaw))
            throw new InvalidDataException("Resurrection position and yaw must be finite.");
    }
}

/// <summary>WZForceAttackSet (39366F90): object bc and bool, four body bytes.</summary>
public readonly record struct ForceAttackSet(uint ObjectId, bool Enabled)
{
    public static ForceAttackSet Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var value = new ForceAttackSet(reader.ReadBc(), reader.ReadBoolean());
        reader.EnsureFullyConsumed();
        return value;
    }
    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter(); writer.WriteBc(ObjectId); writer.WriteBoolean(Enabled);
        return new(WzOpcodes.ForceAttackSet, writer.ToArray());
    }
}

/// <summary>WZCombatEngaged (3936C310): object bc.</summary>
public readonly record struct CombatEngaged(uint ObjectId)
{
    public static CombatEngaged Read(ReadOnlySpan<byte> body) => new(CombatStateWire.ReadObjectId(body));
    public ZoneFrame ToFrame() => CombatStateWire.Frame(WzOpcodes.CombatEngaged, ObjectId);
}

/// <summary>WZCombatCleared (3936C360): object bc.</summary>
public readonly record struct CombatCleared(uint ObjectId)
{
    public static CombatCleared Read(ReadOnlySpan<byte> body) => new(CombatStateWire.ReadObjectId(body));
    public ZoneFrame ToFrame() => CombatStateWire.Frame(WzOpcodes.CombatCleared, ObjectId);
}

/// <summary>WZUnitDuelState (39367550): unit bc, duel object bc and unsigned team byte.</summary>
public readonly record struct UnitDuelState(uint ObjectId, uint DuelObjectId, byte Team)
{
    public static UnitDuelState Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var value = new UnitDuelState(reader.ReadBc(), reader.ReadBc(), reader.ReadByte());
        reader.EnsureFullyConsumed();
        return value;
    }
    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter(); writer.WriteBc(ObjectId); writer.WriteBc(DuelObjectId); writer.WriteByte(Team);
        return new(WzOpcodes.UnitDuelState, writer.ToArray());
    }
}

/// <summary>WZTargetChanged (3936BEC0): source bc, target bc and force-by-World bool.</summary>
public readonly record struct TargetChanged(uint ObjectId, uint TargetId, bool ForceByWorld)
{
    public static TargetChanged Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var value = new TargetChanged(reader.ReadBc(), reader.ReadBc(), reader.ReadBoolean());
        reader.EnsureFullyConsumed();
        return value;
    }
    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter(); writer.WriteBc(ObjectId); writer.WriteBc(TargetId); writer.WriteBoolean(ForceByWorld);
        return new(WzOpcodes.TargetChanged, writer.ToArray());
    }
}

internal static class CombatStateWire
{
    internal static uint ReadObjectId(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body); var objectId = reader.ReadBc(); reader.EnsureFullyConsumed();
        return objectId;
    }
    internal static ZoneFrame Frame(ushort opcode, uint objectId)
    {
        var writer = new ZoneWriter(); writer.WriteBc(objectId);
        return new(opcode, writer.ToArray());
    }
}
