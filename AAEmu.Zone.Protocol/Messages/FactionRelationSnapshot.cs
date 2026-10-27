namespace AAEmu.Zone.Protocol.Messages;

/// <summary>FactionRelation serializer 0x39387240.</summary>
public sealed record ZoneFactionRelation(
    uint FirstId, uint SecondId, byte State, byte NextState, ulong UpdateTime, ulong ChangeTime,
    long UpdaterId, string UpdaterName, long ConfirmerId, string ConfirmerName)
{
    internal static ZoneFactionRelation Read(ref ZoneReader reader)
    {
        var first = reader.ReadUInt32();
        var second = reader.ReadUInt32();
        return new ZoneFactionRelation(Math.Min(first, second), Math.Max(first, second),
            reader.ReadByte(), reader.ReadByte(), reader.ReadUInt64(), reader.ReadUInt64(),
            reader.ReadInt64(), reader.ReadString(128), reader.ReadInt64(), reader.ReadString(128));
    }

    internal void Write(ZoneWriter writer)
    {
        writer.WriteUInt32(FirstId);
        writer.WriteUInt32(SecondId);
        writer.WriteByte(State);
        writer.WriteByte(NextState);
        writer.WriteUInt64(UpdateTime);
        writer.WriteUInt64(ChangeTime);
        writer.WriteInt64(UpdaterId);
        writer.WriteString(UpdaterName, 128);
        writer.WriteInt64(ConfirmerId);
        writer.WriteString(ConfirmerName, 128);
    }
}

/// <summary>WZFactionRelationList serializer 0x3936A890, at most 200 records per chunk.</summary>
public sealed record FactionRelationSnapshot(uint Total, IReadOnlyList<ZoneFactionRelation> Entries)
{
    public const int MaxEntries = 200;

    public ZoneFrame ToFrame()
    {
        if (Entries.Count > MaxEntries || Entries.Count > Total)
            throw new InvalidDataException("Invalid faction relation snapshot count.");
        var writer = new ZoneWriter();
        writer.WriteUInt32(Total);
        writer.WriteByte((byte)Entries.Count);
        foreach (var entry in Entries)
            entry.Write(writer);
        return new ZoneFrame(WzOpcodes.FactionRelationList, writer.ToArray());
    }

    public static FactionRelationSnapshot Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var total = reader.ReadUInt32();
        var count = reader.ReadByte();
        if (count > MaxEntries || count > total)
            throw new InvalidDataException("Invalid faction relation snapshot count.");
        var entries = new ZoneFactionRelation[count];
        for (var i = 0; i < count; i++)
            entries[i] = ZoneFactionRelation.Read(ref reader);
        reader.EnsureFullyConsumed();
        return new FactionRelationSnapshot(total, Array.AsReadOnly(entries));
    }
}
