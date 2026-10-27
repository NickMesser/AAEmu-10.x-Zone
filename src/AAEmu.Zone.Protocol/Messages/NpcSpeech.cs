namespace AAEmu.Zone.Protocol.Messages;

/// <summary>3936CAC0 / 39388A30: two unit IDs, byte kind, then text (0) or a dialogue ID (1/2).</summary>
public sealed record NpcSpeech(uint SpeakerObjectId, uint TargetObjectId, byte Kind, string Text = "", uint MessageId = 0)
{
    public const int MaximumTextBytes = 1023;

    public static NpcSpeech Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var speaker = reader.ReadBc(); var target = reader.ReadBc(); var kind = reader.ReadByte();
        var text = kind == 0 ? reader.ReadString(MaximumTextBytes) : "";
        var id = kind is 1 or 2 ? reader.ReadUInt32() : 0;
        reader.EnsureFullyConsumed(); return new(speaker, target, kind, text, id);
    }

    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter(); writer.WriteBc(SpeakerObjectId); writer.WriteBc(TargetObjectId); writer.WriteByte(Kind);
        if (Kind == 0) writer.WriteString(Text.Split('\0')[0], MaximumTextBytes);
        else if (Kind is 1 or 2) writer.WriteUInt32(MessageId);
        return new(ZwOpcodes.NpcSaid, writer.ToArray());
    }
}

/// <summary>3936D160: the Zone's target change has no World-only force flag.</summary>
public readonly record struct ZoneTargetChanged(uint ObjectId, uint TargetId)
{
    public static ZoneTargetChanged Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body); var value = new ZoneTargetChanged(reader.ReadBc(), reader.ReadBc());
        reader.EnsureFullyConsumed(); return value;
    }
    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter(); writer.WriteBc(ObjectId); writer.WriteBc(TargetId);
        return new(ZwOpcodes.ChangeTarget, writer.ToArray());
    }
}
