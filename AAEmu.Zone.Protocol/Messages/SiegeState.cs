namespace AAEmu.Zone.Protocol.Messages;

/// <summary>39BDC750: the two type fields and six area/building limits retain their native widths.</summary>
public sealed record SiegeAreaRules(uint Type, uint SecondaryType, byte MaxGates, byte MaxWalls,
    short RadiusDeclare, short RadiusDominion, short RadiusOffenseHq, short RadiusSiege)
{
    internal static SiegeAreaRules Read(ref ZoneReader reader) => new(reader.ReadUInt32(), reader.ReadUInt32(),
        reader.ReadByte(), reader.ReadByte(), reader.ReadInt16(), reader.ReadInt16(), reader.ReadInt16(), reader.ReadInt16());
    internal void Write(ZoneWriter writer)
    {
        writer.WriteUInt32(Type); writer.WriteUInt32(SecondaryType); writer.WriteByte(MaxGates); writer.WriteByte(MaxWalls);
        writer.WriteInt16(RadiusDeclare); writer.WriteInt16(RadiusDominion); writer.WriteInt16(RadiusOffenseHq); writer.WriteInt16(RadiusSiege);
    }
    public static SiegeAreaRules Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0);
}

public sealed record SiegeBuilding(uint ObjectId, UnitWorldPosition Position);

/// <summary>39CC2DC0: fixed native storage holds 50 members and 100 buildings.</summary>
public sealed record SiegeFaction(uint FactionId, uint ObjectId, UnitWorldPosition Position, byte MemberLimit,
    IReadOnlyList<long> Members, IReadOnlyList<SiegeBuilding> Buildings, uint TeamId, uint ScorePoint)
{
    public const int MaximumMembers = 50;
    public const int MaximumBuildings = 100;
    public static SiegeFaction Empty { get; } = new(0, 0, default, MaximumMembers, [], [], 0, 0);

    internal static SiegeFaction Read(ref ZoneReader reader)
    {
        var faction = reader.ReadUInt32(); var objectId = reader.ReadBc(); var position = UnitWorldPosition.Read(ref reader);
        var limit = reader.ReadByte(); var count = reader.ReadByte();
        ValidateCounts(limit, count, 0);
        var members = new long[count];
        for (var i = 0; i < members.Length; i++) members[i] = reader.ReadInt64();
        var buildingCount = reader.ReadByte();
        ValidateCounts(limit, count, buildingCount);
        var buildings = new SiegeBuilding[buildingCount];
        for (var i = 0; i < buildings.Length; i++) buildings[i] = new(reader.ReadBc(), UnitWorldPosition.Read(ref reader));
        return new(faction, objectId, position, limit, Array.AsReadOnly(members), Array.AsReadOnly(buildings),
            reader.ReadUInt32(), reader.ReadUInt32());
    }

    internal void Write(ZoneWriter writer)
    {
        ValidateCounts(MemberLimit, Members.Count, Buildings.Count);
        writer.WriteUInt32(FactionId); writer.WriteBc(ObjectId); Position.Write(writer);
        writer.WriteByte(MemberLimit); writer.WriteByte((byte)Members.Count);
        foreach (var member in Members) writer.WriteInt64(member);
        writer.WriteByte((byte)Buildings.Count);
        foreach (var building in Buildings) { writer.WriteBc(building.ObjectId); building.Position.Write(writer); }
        writer.WriteUInt32(TeamId); writer.WriteUInt32(ScorePoint);
    }

    private static void ValidateCounts(int limit, int members, int buildings)
    {
        if (limit > MaximumMembers || members > MaximumMembers || buildings > MaximumBuildings)
            throw new InvalidDataException("Siege roster or building count exceeds native fixed storage.");
        // A snapshot can contain more members than its current limit. Only subsequent additions check that limit.
    }
}

/// <summary>39CC2830/39CC4250: durations for periods 1..4, times, extra scalar, signed period and participants.</summary>
public sealed record SiegeStatus(IReadOnlyList<uint> Durations, ulong StartedAt, ulong FixedAt, uint Extra,
    sbyte Period, SiegeFaction Defender, IReadOnlyList<SiegeFaction> Offense, bool SecondHalf)
{
    public static SiegeStatus Empty { get; } = new(Array.AsReadOnly(new uint[4]), 0, 0, 0, 0, SiegeFaction.Empty, [], false);

    internal static SiegeStatus Read(ref ZoneReader reader)
    {
        var durations = new uint[4];
        for (var i = 0; i < durations.Length; i++) durations[i] = reader.ReadUInt32();
        var started = reader.ReadUInt64(); var fixedAt = reader.ReadUInt64(); var extra = reader.ReadUInt32();
        var period = reader.ReadSByte(); var defender = SiegeFaction.Read(ref reader);
        // Every faction occupies at least 38 wire bytes, including its empty roster/building counts.
        var offense = new SiegeFaction[reader.ReadCount(38)];
        for (var i = 0; i < offense.Length; i++) offense[i] = SiegeFaction.Read(ref reader);
        return new(Array.AsReadOnly(durations), started, fixedAt, extra, period, defender,
            Array.AsReadOnly(offense), reader.ReadBoolean());
    }

    internal void Write(ZoneWriter writer)
    {
        if (Durations.Count != 4) throw new InvalidDataException("Siege status requires four period durations.");
        foreach (var duration in Durations) writer.WriteUInt32(duration);
        writer.WriteUInt64(StartedAt); writer.WriteUInt64(FixedAt); writer.WriteUInt32(Extra); writer.WriteSByte(Period);
        Defender.Write(writer); writer.WriteUInt32((uint)Offense.Count);
        foreach (var faction in Offense) faction.Write(writer);
        writer.WriteBoolean(SecondHalf);
    }
}

/// <summary>WZSiegeState 0x62, 39367F70: rules, full siege status, owner faction.</summary>
public sealed record SiegeState(SiegeAreaRules Rules, SiegeStatus Status, uint OwnerFactionId)
{
    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter(); Rules.Write(writer); Status.Write(writer); writer.WriteUInt32(OwnerFactionId);
        return new(WzOpcodes.SiegeState, writer.ToArray());
    }
    public static SiegeState Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var value = new SiegeState(SiegeAreaRules.Read(ref reader), SiegeStatus.Read(ref reader), reader.ReadUInt32());
        reader.EnsureFullyConsumed(); return value;
    }
}

/// <summary>WZDominionData 0x60, 39362370/39CC4310. Five money values remain in native order.</summary>
public sealed record DominionData(ushort ZoneGroupId, uint OwnerFactionId, uint HouseId, uint TaxRate,
    UnitWorldPosition Position, IReadOnlyList<ulong> MoneyAmounts, ulong LastPaidTime, ulong LastSiegeEndTime,
    ulong ReignStartTime, ulong LastTaxRateChangedTime, uint Point, uint ObjectId, SiegeAreaRules Rules,
    SiegeStatus Siege, ulong NonPvpStart, ushort NonPvpDuration)
{
    public static DominionData Empty(ushort group) => new(group, 0, 0, 0, default, Array.AsReadOnly(new ulong[5]),
        0, 0, 0, 0, 0, 0, SiegeAreaRules.Empty, SiegeStatus.Empty, 0, 0);

    public ZoneFrame ToFrame()
    {
        if (MoneyAmounts.Count != 5) throw new InvalidDataException("Dominion requires five money amounts.");
        var writer = new ZoneWriter();
        writer.WriteUInt16(ZoneGroupId); writer.WriteUInt32(OwnerFactionId); writer.WriteUInt32(HouseId);
        writer.WriteUInt32(TaxRate); Position.Write(writer);
        foreach (var money in MoneyAmounts) writer.WriteUInt64(money);
        writer.WriteUInt64(LastPaidTime); writer.WriteUInt64(LastSiegeEndTime); writer.WriteUInt64(ReignStartTime);
        writer.WriteUInt64(LastTaxRateChangedTime); writer.WriteUInt32(Point); writer.WriteBc(ObjectId);
        Rules.Write(writer); Siege.Write(writer); writer.WriteUInt64(NonPvpStart); writer.WriteUInt16(NonPvpDuration);
        return new(WzOpcodes.DominionData, writer.ToArray());
    }

    public static DominionData Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var group = reader.ReadUInt16(); var faction = reader.ReadUInt32(); var house = reader.ReadUInt32(); var tax = reader.ReadUInt32();
        var position = UnitWorldPosition.Read(ref reader); var money = new ulong[5];
        for (var i = 0; i < money.Length; i++) money[i] = reader.ReadUInt64();
        var value = new DominionData(group, faction, house, tax, position, Array.AsReadOnly(money),
            reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt32(),
            reader.ReadBc(), SiegeAreaRules.Read(ref reader), SiegeStatus.Read(ref reader), reader.ReadUInt64(), reader.ReadUInt16());
        reader.EnsureFullyConsumed(); return value;
    }
}

public sealed record DominionDeleted
{
    public ZoneFrame ToFrame() => new(WzOpcodes.DominionDeleted, ReadOnlyMemory<byte>.Empty);
    public static DominionDeleted Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body); reader.EnsureFullyConsumed(); return new();
    }
}

/// <summary>39367FD0: faction u32, member i64, added bool.</summary>
public sealed record SiegeMember(uint FactionId, long CharacterId, bool Added)
{
    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter(); writer.WriteUInt32(FactionId); writer.WriteInt64(CharacterId); writer.WriteBoolean(Added);
        return new(WzOpcodes.SiegeMember, writer.ToArray());
    }
    public static SiegeMember Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body); var value = new SiegeMember(reader.ReadUInt32(), reader.ReadInt64(), reader.ReadBoolean());
        reader.EnsureFullyConsumed(); return value;
    }
}

public sealed record SiegeSecondHalf(bool SecondHalf)
{
    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter(); writer.WriteBoolean(SecondHalf); return new(WzOpcodes.SiegeSecondHalf, writer.ToArray());
    }
    public static SiegeSecondHalf Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body); var value = new SiegeSecondHalf(reader.ReadBoolean()); reader.EnsureFullyConsumed(); return value;
    }
}
