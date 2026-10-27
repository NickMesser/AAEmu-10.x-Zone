namespace AAEmu.Zone.Protocol.Messages;

/// <summary>39345F20 maps the second wire integer to the preferred NPC template; only State==1 activates restoration.</summary>
public readonly record struct PersistentSpawner(uint Id, uint TemplateId, byte State);

/// <summary>0x39365A20 → 0x39B13390. Each entry occupies nine wire bytes.</summary>
public sealed record SpawnerSnapshot(bool Last, IReadOnlyList<PersistentSpawner> Entries)
{
    public const int MaxEntries = 100;

    public ZoneFrame ToFrame()
    {
        if (Entries.Count > MaxEntries)
            throw new InvalidDataException("A native spawner chunk holds at most 100 entries.");
        var writer = new ZoneWriter();
        writer.WriteBoolean(Last);
        writer.WriteByte((byte)Entries.Count);
        foreach (var entry in Entries)
        {
            writer.WriteUInt32(entry.Id);
            writer.WriteUInt32(entry.TemplateId);
            writer.WriteByte(entry.State);
        }
        return new ZoneFrame(WzOpcodes.SpawnerList, writer.ToArray());
    }

    public static SpawnerSnapshot Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var last = reader.ReadBoolean();
        var count = reader.ReadByte();
        if (count > MaxEntries)
            throw new InvalidDataException("A native spawner chunk holds at most 100 entries.");
        var entries = new PersistentSpawner[count];
        for (var i = 0; i < count; i++)
            entries[i] = new PersistentSpawner(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadByte());
        reader.EnsureFullyConsumed();
        return new SpawnerSnapshot(last, Array.AsReadOnly(entries));
    }
}
