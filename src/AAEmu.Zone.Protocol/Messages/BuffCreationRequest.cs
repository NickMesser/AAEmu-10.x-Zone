namespace AAEmu.Zone.Protocol.Messages;

/// <summary>
/// ZWCreateBuff requests World to apply a buff template. Native senders do not insert a Zone buff;
/// WZBuffCreated supplies the resulting instance. Body 39368D90, initializer 39362830.
/// </summary>
public readonly record struct BuffCreationRequest(uint ObjectId, uint TemplateId)
{
    public static BuffCreationRequest Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var value = new BuffCreationRequest(reader.ReadBc(), reader.ReadUInt32());
        reader.EnsureFullyConsumed();
        return value;
    }

    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter();
        writer.WriteBc(ObjectId);
        writer.WriteUInt32(TemplateId);
        return new(ZwOpcodes.CreateBuff, writer.ToArray());
    }
}
