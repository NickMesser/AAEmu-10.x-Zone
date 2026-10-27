namespace AAEmu.Zone.Protocol.Messages;

/// <summary>ZWJoin body: 0x39368A00; sender 0x39366760 uses protocol 1203 in both directions.</summary>
public readonly record struct ZoneJoin(
    uint ProtocolFrom, uint ProtocolTo, int ZoneKey, uint Ip, ushort Port,
    ulong AccountId, uint InstanceId, bool Dev)
{
    public const uint NativeProtocolVersion = 1203;
    public const int BodySize = 31;

    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter();
        writer.WriteUInt32(ProtocolFrom);
        writer.WriteUInt32(ProtocolTo);
        writer.WriteInt32(ZoneKey);
        writer.WriteUInt32(Ip);
        writer.WriteUInt16(Port);
        writer.WriteUInt64(AccountId);
        writer.WriteUInt32(InstanceId);
        writer.WriteBoolean(Dev);
        return new ZoneFrame(ZwOpcodes.Join, writer.ToArray());
    }

    public static ZoneJoin Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var message = new ZoneJoin(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadInt32(),
            reader.ReadUInt32(), reader.ReadUInt16(), reader.ReadUInt64(), reader.ReadUInt32(), reader.ReadBoolean());
        reader.EnsureFullyConsumed();
        return message;
    }
}
