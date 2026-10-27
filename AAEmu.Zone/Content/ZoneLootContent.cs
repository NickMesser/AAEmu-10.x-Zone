using System.Collections.Frozen;
using Microsoft.Data.Sqlite;
using static AAEmu.Zone.Content.NativeContentReader;

namespace AAEmu.Zone.Content;

public sealed record LootPackMetadata(bool WarDrop, int ExtraGainActGroupId);
public sealed record LootPackDefinition(int Id, LootPackMetadata Metadata);
public sealed record LootItemDefinition(uint ItemId, byte Grade, int Minimum, int Maximum, int DropRate, bool AlwaysDrop, bool MessageToWorld);
public sealed record LootRowDefinition(int PackId, int GroupNumber, LootItemDefinition Item, int LootQuestId);
public sealed record LootGroupSettings(int PackId, int GroupNumber, int DropRate, int GradeDistributionId, ushort ZoneGroupId);
public sealed record LootActabilitySettings(int PackId, int GroupNumber, int MinimumDice, int MaximumDice);
public sealed record NpcLootPackDefinition(uint NpcId, int PackId);
public sealed record LootGroupDefinition(int Number, IReadOnlyList<LootItemDefinition> Items, ushort ZoneGroupId,
    int DropRate, int TotalWeight, bool HasQuestItems, int MinimumDice, int MaximumDice, int GradeDistributionId);

public sealed class ZoneLootPack
{
    public int Id { get; }
    // 39BB4740 leaves these fields uninitialized when a loot row creates an undeclared pack.
    public LootPackMetadata? Metadata { get; }
    public IReadOnlyList<LootGroupDefinition> Groups { get; }
    private readonly FrozenDictionary<int, LootGroupDefinition> _groups;

    internal ZoneLootPack(int id, LootPackMetadata? metadata, IEnumerable<LootGroupDefinition> groups)
    {
        Id = id; Metadata = metadata;
        Groups = Array.AsReadOnly(groups.OrderBy(group => group.Number).ToArray());
        _groups = Groups.ToFrozenDictionary(group => group.Number);
    }

    public LootGroupDefinition? FindGroup(int number) => _groups.GetValueOrDefault(number);
}

/// <summary>Native loot pack/group construction and unfiltered NPC mappings from the read-only content database.</summary>
public sealed class ZoneLootContent
{
    public IReadOnlyDictionary<int, ZoneLootPack> Packs { get; }
    public IReadOnlyDictionary<uint, IReadOnlyList<int>> NpcPackIds { get; }

    public ZoneLootContent(IEnumerable<LootPackDefinition> packs, IEnumerable<LootRowDefinition> rows,
        IEnumerable<LootGroupSettings> groups, IEnumerable<LootActabilitySettings> actability,
        IEnumerable<NpcLootPackDefinition> npcs)
    {
        var builders = new Dictionary<int, PackBuilder>();
        foreach (var pack in packs) GetPack(pack.Id).Metadata = pack.Metadata;
        foreach (var row in rows)
        {
            var pack = GetPack(row.PackId);
            if (!pack.Groups.TryGetValue(row.GroupNumber, out var group))
                pack.Groups.Add(row.GroupNumber, group = new(row.GroupNumber));
            group.Items.Add(row.Item);
            if (row.GroupNumber > 0)
            {
                group.TotalWeight = unchecked(group.TotalWeight + row.Item.DropRate);
                group.HasQuestItems |= row.LootQuestId > 0;
            }
        }
        // Settings do not create packs or groups; later rows replace earlier settings.
        foreach (var settings in groups)
            if (builders.TryGetValue(settings.PackId, out var pack) && pack.Groups.TryGetValue(settings.GroupNumber, out var group))
            {
                group.DropRate = settings.DropRate;
                group.GradeDistributionId = settings.GradeDistributionId;
                group.ZoneGroupId = settings.ZoneGroupId;
            }
        foreach (var settings in actability)
            if (builders.TryGetValue(settings.PackId, out var pack) && pack.Groups.TryGetValue(settings.GroupNumber, out var group))
            {
                group.MinimumDice = settings.MinimumDice;
                group.MaximumDice = settings.MaximumDice;
            }
        Packs = builders.ToFrozenDictionary(pair => pair.Key, pair => new ZoneLootPack(pair.Key, pair.Value.Metadata,
            pair.Value.Groups.Select(group => group.Value.Build(group.Key))));
        NpcPackIds = npcs.GroupBy(row => row.NpcId).ToFrozenDictionary(group => group.Key,
            group => (IReadOnlyList<int>)Array.AsReadOnly(group.Select(row => row.PackId).ToArray()));

        PackBuilder GetPack(int id)
        {
            if (!builders.TryGetValue(id, out var pack)) builders.Add(id, pack = new());
            return pack;
        }
    }

    private sealed class PackBuilder
    {
        internal LootPackMetadata? Metadata;
        internal readonly Dictionary<int, GroupBuilder> Groups = [];
    }

    private sealed class GroupBuilder(int number)
    {
        internal readonly List<LootItemDefinition> Items = [];
        internal ushort ZoneGroupId;
        internal int DropRate = 10_000_000;
        internal int TotalWeight = number == 0 ? 10_000_000 : 0;
        internal bool HasQuestItems;
        internal int MinimumDice;
        internal int MaximumDice;
        internal int GradeDistributionId;
        internal LootGroupDefinition Build(int groupNumber) => new(groupNumber, Items.AsReadOnly(), ZoneGroupId,
            DropRate, TotalWeight, HasQuestItems, MinimumDice, MaximumDice, GradeDistributionId);
    }

    internal static ZoneLootContent Load(SqliteConnection connection)
    {
        var packs = new List<LootPackDefinition>();
        Read(connection, "SELECT id, war_drop, extra_gain_act_group_id FROM loot_packs", reader =>
            packs.Add(new(Int32(reader, 0), new(Boolean(reader, 1), Int32(reader, 2)))));
        var rows = new List<LootRowDefinition>();
        // Exact native join and iteration order: missing item definitions contribute no loot rows.
        Read(connection, """
            SELECT loot_pack_id, loots.'group', item_id, grade_id, min_amount, max_amount, drop_rate,
                   always_drop, msg_to_world, items.loot_quest_id
            FROM loots INNER JOIN items ON loots.item_id = items.id
            """, reader => rows.Add(new(Int32(reader, 0), Int32(reader, 1),
                new(UInt32(reader, 2), unchecked((byte)Int32(reader, 3)), Int32(reader, 4), Int32(reader, 5),
                    Int32(reader, 6), Boolean(reader, 7), Boolean(reader, 8)), Int32(reader, 9))));
        var groups = new List<LootGroupSettings>();
        Read(connection, "SELECT pack_id, group_no, drop_rate, item_grade_distribution_id, zone_group_id FROM loot_groups", reader =>
            groups.Add(new(Int32(reader, 0), Int32(reader, 1), Int32(reader, 2), Int32(reader, 3), unchecked((ushort)Int32(reader, 4)))));
        var actability = new List<LootActabilitySettings>();
        Read(connection, "SELECT loot_pack_id, loot_group_id, min_dice, max_dice FROM loot_actability_groups", reader =>
            actability.Add(new(Int32(reader, 0), Int32(reader, 1), Int32(reader, 2), Int32(reader, 3))));
        var npcs = new List<NpcLootPackDefinition>();
        Read(connection, "SELECT npc_id, loot_pack_id FROM loot_pack_dropping_npcs", reader =>
            npcs.Add(new(UInt32(reader, 0), Int32(reader, 1))));
        return new(packs, rows, groups, actability, npcs);
    }
}
