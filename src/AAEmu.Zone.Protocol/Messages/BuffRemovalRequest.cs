namespace AAEmu.Zone.Protocol.Messages;

/// <summary>
/// ZWRemoveBuff: native body 39368E30 and initializer 39362840. The Zone requests a removal;
/// 3944FC90 retains its instance until World sends WZBuffRemoved. Script requests can select a template only.
/// </summary>
public readonly record struct BuffRemovalRequest(uint ObjectId, uint Index, uint TemplateId, byte Reason)
{
    public static BuffRemovalRequest Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var value = new BuffRemovalRequest(reader.ReadBc(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadByte());
        reader.EnsureFullyConsumed();
        return value;
    }

    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter();
        writer.WriteBc(ObjectId);
        writer.WriteUInt32(Index);
        writer.WriteUInt32(TemplateId);
        writer.WriteByte(Reason);
        return new(ZwOpcodes.RemoveBuff, writer.ToArray());
    }
}
