namespace AAEmu.Zone.Protocol.Messages;

/// <summary>
/// ZW 0x35, serializer 3936D380: bc unit ID followed by the uncompressed World position
/// (39B113A0: signed 2^44 fixed-point X/Y and float Z). Body length is 23 bytes.
/// </summary>
public readonly record struct ZoneUnitLocation(uint ObjectId, UnitWorldPosition Position)
{
    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter();
        writer.WriteBc(ObjectId);
        Position.Write(writer);
        return new(ZwOpcodes.UnitLocation, writer.ToArray());
    }

    public static ZoneUnitLocation Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var value = new ZoneUnitLocation(reader.ReadBc(), UnitWorldPosition.Read(ref reader));
        reader.EnsureFullyConsumed();
        return value;
    }
}
