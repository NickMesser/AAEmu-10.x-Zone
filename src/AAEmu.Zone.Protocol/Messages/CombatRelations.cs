namespace AAEmu.Zone.Protocol.Messages;

// The native CvF wire entry contains only the signed character key, state and reason.
// Its internal faction field is not serialized (39389790).
public sealed record CharacterCombatRelation(long CharacterId, byte State, byte Reason);
public sealed record FactionCombatRelation(uint FirstId, uint SecondId, byte State, byte Reason);

/// <summary>3936AC10 / 39389790: u8 count followed by signed i64, state u8, reason u8.</summary>
public sealed record CharacterCombatRelations(IReadOnlyList<CharacterCombatRelation> Entries)
{
    public const int MaxEntries = byte.MaxValue;
    public static CharacterCombatRelations Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var entries = new CharacterCombatRelation[reader.ReadByte()];
        for (var i = 0; i < entries.Length; i++)
            entries[i] = new(reader.ReadInt64(), reader.ReadByte(), reader.ReadByte());
        reader.EnsureFullyConsumed();
        return new(Array.AsReadOnly(entries));
    }

    public ZoneFrame ToFrame()
    {
        if (Entries.Count > MaxEntries) throw new ArgumentOutOfRangeException(nameof(Entries));
        var writer = new ZoneWriter(); writer.WriteByte((byte)Entries.Count);
        foreach (var entry in Entries)
        {
            writer.WriteInt64(entry.CharacterId); writer.WriteByte(entry.State); writer.WriteByte(entry.Reason);
        }
        return new(WzOpcodes.CvFCombatRelationship, writer.ToArray());
    }
}

/// <summary>3936DD40 / 3938FFC0: u8 count followed by two u32 IDs, state u8, reason u8.</summary>
public sealed record FactionCombatRelations(IReadOnlyList<FactionCombatRelation> Entries)
{
    public const int MaxEntries = byte.MaxValue;
    public static FactionCombatRelations Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var entries = new FactionCombatRelation[reader.ReadByte()];
        for (var i = 0; i < entries.Length; i++)
            entries[i] = new(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadByte(), reader.ReadByte());
        reader.EnsureFullyConsumed();
        return new(Array.AsReadOnly(entries));
    }

    public ZoneFrame ToFrame()
    {
        if (Entries.Count > MaxEntries) throw new ArgumentOutOfRangeException(nameof(Entries));
        var writer = new ZoneWriter(); writer.WriteByte((byte)Entries.Count);
        foreach (var entry in Entries)
        {
            writer.WriteUInt32(entry.FirstId); writer.WriteUInt32(entry.SecondId);
            writer.WriteByte(entry.State); writer.WriteByte(entry.Reason);
        }
        return new(WzOpcodes.FvFCombatRelationship, writer.ToArray());
    }
}
