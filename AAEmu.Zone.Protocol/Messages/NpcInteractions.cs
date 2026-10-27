namespace AAEmu.Zone.Protocol.Messages;

/// <summary>39367870 / 39367940: player unit object ID, then NPC identity ID.</summary>
public readonly record struct NpcInteractionRequest(uint PlayerObjectId, uint NpcId)
{
    public static NpcInteractionRequest Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var message = new NpcInteractionRequest(reader.ReadBc(), reader.ReadBc());
        reader.EnsureFullyConsumed(); return message;
    }

    public ZoneFrame ToFrame(bool end = false)
    {
        var writer = new ZoneWriter(); writer.WriteBc(PlayerObjectId); writer.WriteBc(NpcId);
        return new(end ? WzOpcodes.InteractNpcEnd : WzOpcodes.InteractNpc, writer.ToArray());
    }
}

/// <summary>39368CC0 / 39343BC0: player and NPC unit object IDs, unlike the request's NPC identity.</summary>
public readonly record struct NpcInteractionEnded(uint PlayerObjectId, uint NpcObjectId)
{
    public static NpcInteractionEnded Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var message = new NpcInteractionEnded(reader.ReadBc(), reader.ReadBc());
        reader.EnsureFullyConsumed(); return message;
    }

    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter(); writer.WriteBc(PlayerObjectId); writer.WriteBc(NpcObjectId);
        return new(ZwOpcodes.NpcInteractionEnded, writer.ToArray());
    }
}

/// <summary>393694B0: NPC unit object ID and one boolean byte.</summary>
public readonly record struct NpcInteractionStatusChanged(uint NpcObjectId, bool Interactable)
{
    public static NpcInteractionStatusChanged Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var message = new NpcInteractionStatusChanged(reader.ReadBc(), reader.ReadBoolean());
        reader.EnsureFullyConsumed(); return message;
    }

    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter(); writer.WriteBc(NpcObjectId); writer.WriteBoolean(Interactable);
        return new(ZwOpcodes.NpcInteractionStatusChanged, writer.ToArray());
    }
}
