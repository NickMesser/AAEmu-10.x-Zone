namespace AAEmu.Zone.Protocol.Messages;

/// <summary>0x39366620: one bool reqGameTime; sender 0x39332510 sets opcode 0x3C.</summary>
public readonly record struct ZoneHeartbeat(bool RequestGameTime)
{
    public ZoneFrame ToFrame() => new(ZwOpcodes.Heartbeat, new byte[] { RequestGameTime ? (byte)1 : (byte)0 });

    public static ZoneHeartbeat Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var message = new ZoneHeartbeat(reader.ReadBoolean());
        reader.EnsureFullyConsumed();
        return message;
    }
}
