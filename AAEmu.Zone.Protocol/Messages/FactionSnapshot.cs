namespace AAEmu.Zone.Protocol.Messages;

/// <summary>Native FactionDesc serializer 0x39386FB0, including both timestamps.</summary>
public sealed record ZoneFaction(
    uint Id, uint MotherId, string Name, long OwnerId, string OwnerName,
    byte UnitOwnerType, byte PoliticalSystem, ulong CreatedTime,
    bool AggroLink, bool DiplomacyTarget, byte AllowChangeName, ulong RenameTime, bool IntegrationFaction)
{
    internal static ZoneFaction Read(ref ZoneReader reader) => new(
        reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadString(128), reader.ReadInt64(), reader.ReadString(128),
        reader.ReadByte(), reader.ReadByte(), reader.ReadUInt64(), reader.ReadBoolean(), reader.ReadBoolean(),
        reader.ReadByte(), reader.ReadUInt64(), reader.ReadBoolean());

    internal void Write(ZoneWriter writer)
    {
        writer.WriteUInt32(Id);
        writer.WriteUInt32(MotherId);
        writer.WriteString(Name, 128);
        writer.WriteInt64(OwnerId);
        writer.WriteString(OwnerName, 128);
        writer.WriteByte(UnitOwnerType);
        writer.WriteByte(PoliticalSystem);
        writer.WriteUInt64(CreatedTime);
        writer.WriteBoolean(AggroLink);
        writer.WriteBoolean(DiplomacyTarget);
        writer.WriteByte(AllowChangeName);
        writer.WriteUInt64(RenameTime);
        writer.WriteBoolean(IntegrationFaction);
    }
}

/// <summary>WZFactionList serializer 0x3936A7D0, at most 20 records per chunk.</summary>
public sealed record FactionSnapshot(uint Total, IReadOnlyList<ZoneFaction> Entries)
{
    public const int MaxEntries = 20;

    public ZoneFrame ToFrame()
    {
        if (Entries.Count > MaxEntries || Entries.Count > Total)
            throw new InvalidDataException("Invalid faction snapshot count.");
        var writer = new ZoneWriter();
        writer.WriteUInt32(Total);
        writer.WriteByte((byte)Entries.Count);
        foreach (var entry in Entries)
            entry.Write(writer);
        return new ZoneFrame(WzOpcodes.FactionList, writer.ToArray());
    }

    public static FactionSnapshot Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var total = reader.ReadUInt32();
        var count = reader.ReadByte();
        if (count > MaxEntries || count > total)
            throw new InvalidDataException("Invalid faction snapshot count.");
        var entries = new ZoneFaction[count];
        for (var i = 0; i < count; i++)
            entries[i] = ZoneFaction.Read(ref reader);
        reader.EnsureFullyConsumed();
        return new FactionSnapshot(total, Array.AsReadOnly(entries));
    }
}
