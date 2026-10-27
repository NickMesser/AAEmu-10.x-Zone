using System.Collections.Frozen;
using AAEmu.Zone.Content;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.WorldState;

/// <summary>Connection-scoped exclusions from all 18 handlers registered by native 39C5BA90.</summary>
public sealed class ZoneWorldContentPolicy : IZoneRelationshipRuntime
{
    private static readonly FrozenDictionary<WorldContentKind, WorldContentName> Categories = new Dictionary<WorldContentKind, string>
    {
        [WorldContentKind.Craft] = "craft", [WorldContentKind.Zone] = "zone", [WorldContentKind.TowerDef] = "tower_def",
        [WorldContentKind.GameSchedule] = "game_schedule", [WorldContentKind.Transfer] = "transfer",
        [WorldContentKind.NpcSpawner] = "npc_spawner", [WorldContentKind.QuestContext] = "quest",
        [WorldContentKind.DoodadAlmighty] = "doodad", [WorldContentKind.Character] = "character",
        [WorldContentKind.LootPack] = "loot_pack", [WorldContentKind.AppellationMerit] = "appellation_merit",
        [WorldContentKind.ScheduleItem] = "schedule_item", [WorldContentKind.ArchePass] = "arche_pass",
        [WorldContentKind.Instance] = "instance", [WorldContentKind.Appellation] = "appellation",
        [WorldContentKind.Achievement] = "achievement", [WorldContentKind.TaggedBuff] = "tagged_buff",
        [WorldContentKind.FestivalZone] = "festival_zone"
    }.ToFrozenDictionary(pair => pair.Key, pair => WorldContentName.FromUtf8(pair.Value));

    private readonly FrozenDictionary<WorldContentKind, IReadOnlySet<uint>> _excludedIds;
    public IReadOnlySet<int> ExcludedZoneKeys { get; }
    public IReadOnlySet<ushort> ExcludedFestivalGroups { get; }

    public ZoneWorldContentPolicy(ZoneWorldContent content, WorldContentFilter filter)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(filter);
        var excluded = Enum.GetValues<WorldContentKind>().Where(kind => kind != WorldContentKind.Zone)
            .ToDictionary(kind => kind, _ => new HashSet<uint>());
        var zoneKeys = new HashSet<int>();
        foreach (var row in content.Entries)
        {
            // All descriptor handlers except LootPack skip the native zero sentinel.
            if ((row.ContentId == 0 && row.Kind != WorldContentKind.LootPack) ||
                !filter.Contains(Categories[row.Kind], row.Name)) continue;
            if (row.Kind == WorldContentKind.Zone)
            {
                // 39C58D30 resolves zones.id and inserts descriptor+24 (zone_key), even when that key is zero.
                if (content.ZoneKeys.TryGetValue(row.ContentId, out var key)) zoneKeys.Add(key);
            }
            else excluded[row.Kind].Add(row.ContentId);
        }
        _excludedIds = excluded.ToFrozenDictionary(pair => pair.Key, pair => (IReadOnlySet<uint>)pair.Value.ToFrozenSet());
        ExcludedZoneKeys = zoneKeys.ToFrozenSet();
        ExcludedFestivalGroups = _excludedIds[WorldContentKind.FestivalZone].Select(id => (ushort)id).ToFrozenSet();
    }

    /// <summary>Native content IDs. Zone exclusions use ExcludedZoneKeys instead of catalog IDs.</summary>
    public IReadOnlySet<uint> ExcludedIds(WorldContentKind kind) => _excludedIds.TryGetValue(kind, out var ids) ? ids :
        throw new ArgumentOutOfRangeException(nameof(kind), "Use ExcludedZoneKeys for Zone filters; other kinds must be defined.");

    public bool IsExcluded(WorldContentKind kind, uint contentId) => ExcludedIds(kind).Contains(contentId);
    public bool IsFestivalGuardExcluded(ushort groupId) => ExcludedFestivalGroups.Contains(groupId);

    public ZoneAchievementIndex ApplyTo(ZoneAchievementContent content) => new(content, ExcludedIds(WorldContentKind.Achievement));

    public ZoneLootIndex ApplyTo(ZoneLootContent content) => new(content, ExcludedIds(WorldContentKind.LootPack));

    /// <summary>Creates a connection-owned view of the currently implemented filtered stat inputs.</summary>
    public ZoneStatContent ApplyTo(ZoneStatContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var buffs = content.Buffs.WithExcludedRuntimeTags(ExcludedIds(WorldContentKind.TaggedBuff));
        var stamps = ExcludedIds(WorldContentKind.AppellationMerit);
        var appellations = content.UnfilteredAppellations is { } source && stamps.Count != 0
            ? new ZoneAppellationContent(source.Stamps.Values.Where(row => !stamps.Contains(row.Id))) : content.UnfilteredAppellations;
        return content.WithFilteredContent(buffs, appellations);
    }
}
