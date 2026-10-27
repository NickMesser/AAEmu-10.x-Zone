using System.Collections.Frozen;
using Microsoft.Data.Sqlite;
using static AAEmu.Zone.Content.NativeContentReader;

namespace AAEmu.Zone.Content;

/// <summary>39B07660: id, editor_name, name, use_additional. The table has no zone column.</summary>
public sealed record ReturnPointDefinition(uint Id, string EditorName, string Name, bool UseAdditional);

/// <summary>
/// 39CF5990 columns in native order. LinkedZoneGroupId is the only zone linkage; x/y/w/h are the
/// native float rectangle.
/// </summary>
public sealed record SubZoneDefinition(
    uint Id, bool ChangeToQuestSoundPack, float H, int Idx, uint LinkedZoneGroupId, string Name,
    uint ParentSubZoneId, uint QuestCompletedId, uint QuestSoundPackId, uint SoundId, uint SoundPackId,
    float W, float X, float Y);

/// <summary>`return_points` (39AF3090/39B07660) and `sub_zones` (39CF4C10/39CF5990), queryable by zone.</summary>
public sealed class ZoneLocationContent
{
    private readonly FrozenDictionary<uint, ReturnPointDefinition> _returnPoints;
    private readonly FrozenDictionary<uint, SubZoneDefinition> _subZones;
    private readonly FrozenDictionary<uint, SubZoneDefinition[]> _subZonesByGroup;

    public IReadOnlyDictionary<uint, ReturnPointDefinition> ReturnPoints => _returnPoints;
    public IReadOnlyDictionary<uint, SubZoneDefinition> SubZones => _subZones;

    public ZoneLocationContent(IEnumerable<ReturnPointDefinition> returnPoints, IEnumerable<SubZoneDefinition> subZones)
    {
        _returnPoints = ContentKeys.Unique("return_points", returnPoints, row => row.Id).ToDictionary(row => row.Id).ToFrozenDictionary();
        var subs = ContentKeys.Unique("sub_zones", subZones, row => row.Id);
        _subZones = subs.ToDictionary(row => row.Id).ToFrozenDictionary();
        _subZonesByGroup = subs.GroupBy(row => row.LinkedZoneGroupId)
            .ToDictionary(group => group.Key, group => group.OrderBy(row => row.Id).ToArray()).ToFrozenDictionary();
    }

    public bool TryGetReturnPoint(uint id, out ReturnPointDefinition returnPoint) => _returnPoints.TryGetValue(id, out returnPoint!);

    /// <summary>Sub-zones linked to a zone group id (zones.group_id), ordered by id.</summary>
    public IReadOnlyList<SubZoneDefinition> SubZonesForGroup(uint zoneGroupId) =>
        _subZonesByGroup.TryGetValue(zoneGroupId, out var rows) ? rows : [];

    /// <summary>Sub-zones of a resolved zone: sub_zones link by zone group, and the zone carries its group id.</summary>
    public IReadOnlyList<SubZoneDefinition> SubZonesForZone(ZoneContent zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return SubZonesForGroup(zone.GroupId);
    }

    internal static ZoneLocationContent Load(SqliteConnection connection)
    {
        var returnPoints = new List<ReturnPointDefinition>();
        Read(connection, "SELECT id, editor_name, name, use_additional FROM return_points ORDER BY id", reader =>
            returnPoints.Add(new(UInt32(reader, 0), String(reader, 1), String(reader, 2), Boolean(reader, 3))));
        var subZones = new List<SubZoneDefinition>();
        Read(connection, """
            SELECT id, change_to_quest_sound_pack, h, idx, linked_zone_group_id, name, parent_sub_zone_id, quest_completed_id,
                quest_sound_pack_id, sound_id, sound_pack_id, w, x, y FROM sub_zones ORDER BY id
            """, reader => subZones.Add(new(UInt32(reader, 0), Boolean(reader, 1), Single(reader, 2), Int32(reader, 3),
                UInt32(reader, 4), String(reader, 5), UInt32(reader, 6), UInt32(reader, 7), UInt32(reader, 8),
                UInt32(reader, 9), UInt32(reader, 10), Single(reader, 11), Single(reader, 12), Single(reader, 13))));
        return new(returnPoints, subZones);
    }
}
