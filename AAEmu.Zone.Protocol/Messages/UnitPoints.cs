namespace AAEmu.Zone.Protocol.Messages;

/// <summary>WZUnitPoints: native serializer 3936BCF0 and handler 39364C30.</summary>
public readonly record struct UnitPoints(uint ObjectId, long PreciseHealth, long PreciseMana)
{
    public static UnitPoints Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var value = new UnitPoints(reader.ReadBc(), reader.ReadInt64(), reader.ReadInt64());
        reader.EnsureFullyConsumed();
        return value;
    }

    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter();
        writer.WriteBc(ObjectId);
        writer.WriteInt64(PreciseHealth);
        writer.WriteInt64(PreciseMana);
        return new(WzOpcodes.UnitPoints, writer.ToArray());
    }
}
