namespace AAEmu.Zone.Protocol.Messages;

/// <summary>WZConflictZoneState, body 39368670: raw group u16 and state u8.</summary>
public sealed record ConflictZoneState(ushort ZoneGroupId, byte State)
{
    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter();
        writer.WriteUInt16(ZoneGroupId);
        writer.WriteByte(State);
        return new(WzOpcodes.ConflictZoneState, writer.ToArray());
    }

    public static ConflictZoneState Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var result = new ConflictZoneState(reader.ReadUInt16(), reader.ReadByte());
        reader.EnsureFullyConsumed();
        return result;
    }
}
