namespace AAEmu.Zone.Protocol.Messages;

/// <summary>3936CA50: NPC identity ID and signed removal delay in milliseconds.</summary>
public readonly record struct NpcRemoval(uint NpcId, int DelayMilliseconds)
{
    public static NpcRemoval Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body); var message = new NpcRemoval(reader.ReadBc(), reader.ReadInt32());
        reader.EnsureFullyConsumed(); return message;
    }
    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter(); writer.WriteBc(NpcId); writer.WriteInt32(DelayMilliseconds);
        return new(ZwOpcodes.RemoveNpc, writer.ToArray());
    }
}

/// <summary>393698C0: NPC identity ID followed by the target's unit object ID.</summary>
public readonly record struct AggroTargetHostile(uint NpcId, uint TargetId)
{
    public static AggroTargetHostile Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body); var message = new AggroTargetHostile(reader.ReadBc(), reader.ReadBc());
        reader.EnsureFullyConsumed(); return message;
    }
    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter(); writer.WriteBc(NpcId); writer.WriteBc(TargetId);
        return new(ZwOpcodes.MakeAggroTargetHostile, writer.ToArray());
    }
}

/// <summary>3936D500: both IDs are unit object IDs, unlike the hostility notification.</summary>
public readonly record struct NpcAbuserRegistered(uint NpcObjectId, uint TargetId)
{
    public static NpcAbuserRegistered Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body); var message = new NpcAbuserRegistered(reader.ReadBc(), reader.ReadBc());
        reader.EnsureFullyConsumed(); return message;
    }
    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter(); writer.WriteBc(NpcObjectId); writer.WriteBc(TargetId);
        return new(ZwOpcodes.RegisterNpcAbuser, writer.ToArray());
    }
}

/// <summary>3936D6B0: NPC unit object ID.</summary>
public readonly record struct NpcAbusersCleared(uint NpcObjectId)
{
    public static NpcAbusersCleared Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body); var message = new NpcAbusersCleared(reader.ReadBc());
        reader.EnsureFullyConsumed(); return message;
    }
    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter(); writer.WriteBc(NpcObjectId);
        return new(ZwOpcodes.ClearNpcAbusers, writer.ToArray());
    }
}

/// <summary>3936D590 / 3931DA70: unit object ID, byte count, then up to 100 bc target IDs.</summary>
public sealed record NpcAbusersUnregistered(uint NpcObjectId, IReadOnlyList<uint> TargetIds)
{
    public const int Capacity = 100;

    public static NpcAbusersUnregistered Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var npcId = reader.ReadBc(); var count = reader.ReadByte();
        if (count > Capacity) throw new InvalidDataException("NPC abuser notification exceeds its native capacity.");
        var ids = new uint[count];
        for (var index = 0; index < count; index++) ids[index] = reader.ReadBc();
        reader.EnsureFullyConsumed();
        return new(npcId, Array.AsReadOnly(ids));
    }

    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter(); writer.WriteBc(NpcObjectId);
        // Native clamps the outgoing count before serializing the byte and entries.
        var count = Math.Min(TargetIds.Count, Capacity); writer.WriteByte((byte)count);
        for (var index = 0; index < count; index++) writer.WriteBc(TargetIds[index]);
        return new(ZwOpcodes.UnregisterNpcAbusers, writer.ToArray());
    }
}
