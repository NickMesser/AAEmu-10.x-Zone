namespace AAEmu.Zone.Protocol.Messages;

/// <summary>Native WZ 0x54 and ZW 0x1D share the single-float body (0x39365C00).</summary>
public readonly record struct ZoneTimeOfDay(float Hour)
{
    public ZoneFrame ToWorldFrame() => Frame(ZwOpcodes.TimeOfDay);
    public ZoneFrame ToZoneFrame() => Frame(WzOpcodes.TimeOfDay);

    private ZoneFrame Frame(ushort opcode)
    {
        var writer = new ZoneWriter();
        writer.WriteSingle(Hour);
        return new(opcode, writer.ToArray());
    }

    public static ZoneTimeOfDay Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var result = new ZoneTimeOfDay(reader.ReadSingle());
        reader.EnsureFullyConsumed();
        return result;
    }
}

/// <summary>0x39365C30/0x39366260: hour, speed, start and end, each a float.</summary>
public readonly record struct ZoneDetailedTimeOfDay(float Hour, float Speed, float Start, float End)
{
    public ZoneFrame ToWorldFrame() => Frame(ZwOpcodes.DetailedTimeOfDay);
    public ZoneFrame ToZoneFrame() => Frame(WzOpcodes.DetailedTimeOfDay);

    private ZoneFrame Frame(ushort opcode)
    {
        var writer = new ZoneWriter();
        writer.WriteSingle(Hour);
        writer.WriteSingle(Speed);
        writer.WriteSingle(Start);
        writer.WriteSingle(End);
        return new(opcode, writer.ToArray());
    }

    public static ZoneDetailedTimeOfDay Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var result = new ZoneDetailedTimeOfDay(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        reader.EnsureFullyConsumed();
        return result;
    }
}
