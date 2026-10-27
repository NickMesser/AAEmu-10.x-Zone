using System.Collections.Frozen;
using Microsoft.Data.Sqlite;
using static AAEmu.Zone.Content.NativeContentReader;

namespace AAEmu.Zone.Content;

public sealed record ZoneAreaDefinition(uint Id, int Key, string Name, string DisplayText, ushort GroupId,
    uint FactionId, uint ClimateId, bool Closed, bool ClosedToForeigner, bool Integration);
public sealed record ZoneConflictPolicy(ushort GroupId, bool WarChaos);
public sealed record ZonePvpPolicy(ushort GroupId, bool Pvp);
public sealed record ZoneFestivalDefinition(ushort GroupId, string Name, bool Closed,
    ZoneCalendarDate Start, ZoneCalendarDate End, uint NuiaReturnPointId, uint HariharaReturnPointId,
    uint NationReturnPointId, uint PirateReturnPointId, uint NpcSpawnerId);

/// <summary>
/// Scene metadata used by ServerUnit relationships. Conflict and indun rows are policy
/// projections; their remaining gameplay definitions are separate content systems.
/// </summary>
public sealed class ZoneSceneContent
{
    public IReadOnlyDictionary<int, ZoneAreaDefinition> Areas { get; }
    public IReadOnlyDictionary<ushort, ZoneConflictPolicy> Conflicts { get; }
    public IReadOnlyDictionary<ushort, ZonePvpPolicy> PvpPolicies { get; }
    public IReadOnlyDictionary<ushort, ZoneFestivalDefinition> Festivals { get; }
    public IReadOnlySet<int> BattlefieldKeys { get; }

    public ZoneSceneContent(IEnumerable<ZoneAreaDefinition> areas, IEnumerable<ZoneConflictPolicy> conflicts,
        IEnumerable<ZonePvpPolicy> pvpPolicies, IEnumerable<ZoneFestivalDefinition> festivals, IEnumerable<int> battlefieldKeys)
    {
        // A duplicate key cannot identify one scene reliably; reject malformed zone content.
        Areas = areas.ToFrozenDictionary(row => row.Key);
        Conflicts = LastByGroup(conflicts, row => row.GroupId);
        PvpPolicies = LastByGroup(pvpPolicies, row => row.GroupId);
        Festivals = LastByGroup(festivals, row => row.GroupId);
        BattlefieldKeys = battlefieldKeys.ToFrozenSet();
    }

    /// <summary>39B4DDE0 samples time once while loading. Later clock changes do not rebuild this set.</summary>
    public IReadOnlySet<ushort> ActiveFestivalGroups(ulong loadedAtUnixSeconds, int contentDateOffsetMinutes)
    {
        var result = new HashSet<ushort>();
        foreach (var festival in Festivals.Values)
        {
            if (festival.Closed) continue;
            // 39B51EF0 first shifts the descriptor's dates; 39B4DDE0 then converts them again.
            var start = festival.Start.ShiftMinutes(contentDateOffsetMinutes).ToUnixTimeSeconds();
            var end = festival.End.ShiftMinutes(contentDateOffsetMinutes).ToUnixTimeSeconds();
            if (ZoneCalendarDate.PositionInPeriod(loadedAtUnixSeconds, start, end) == ZoneCalendarPosition.During)
                result.Add(festival.GroupId);
        }
        return result.ToFrozenSet();
    }

    private static FrozenDictionary<ushort, T> LastByGroup<T>(IEnumerable<T> rows, Func<T, ushort> key)
    {
        var result = new Dictionary<ushort, T>();
        // Generated native loaders retrieve the row by group and overwrite its fields.
        foreach (var row in rows) result[key(row)] = row;
        return result.ToFrozenDictionary();
    }

    internal static ZoneSceneContent Load(SqliteConnection connection)
    {
        var areas = new List<ZoneAreaDefinition>();
        Read(connection, "SELECT id, closed, closed_to_foreigner, display_text, faction_id, group_id, integration, name, zone_climate_id, zone_key FROM zones ORDER BY id", r =>
            areas.Add(new(UInt32(r, 0), Int32(r, 9), String(r, 7), String(r, 3), unchecked((ushort)Int32(r, 5)),
                UInt32(r, 4), UInt32(r, 8), Boolean(r, 1), Boolean(r, 2), Boolean(r, 6))));
        var conflicts = new List<ZoneConflictPolicy>();
        Read(connection, "SELECT zone_group_id, war_chaos FROM conflict_zones ORDER BY rowid", r =>
            conflicts.Add(new(unchecked((ushort)Int32(r, 0)), Boolean(r, 1))));
        var policies = new List<ZonePvpPolicy>();
        Read(connection, "SELECT zone_group_id, pvp FROM indun_zones ORDER BY rowid", r =>
            policies.Add(new(unchecked((ushort)Int32(r, 0)), Boolean(r, 1))));
        var festivals = new List<ZoneFestivalDefinition>();
        Read(connection, """
            SELECT zone_group_id, closed, ed_day, ed_hour, ed_min, ed_month, ed_year, harihara_return_point_id,
                name, nation_return_point_id, nuia_return_point_id, pirate_return_point_id,
                st_day, st_hour, st_min, st_month, st_year, npc_spawner_id FROM festival_zones ORDER BY rowid
            """, r => festivals.Add(new(unchecked((ushort)Int32(r, 0)), String(r, 8), Boolean(r, 1),
                new(Int32(r, 16), Int32(r, 15), Int32(r, 12), Int32(r, 13), Int32(r, 14)),
                new(Int32(r, 6), Int32(r, 5), Int32(r, 2), Int32(r, 3), Int32(r, 4)),
                UInt32(r, 10), UInt32(r, 7), UInt32(r, 9), UInt32(r, 11), UInt32(r, 17))));
        var battlefields = new List<int>();
        Read(connection, "SELECT zone_key FROM battle_fields", r => battlefields.Add(Int32(r, 0)));
        return new(areas, conflicts, policies, festivals, battlefields);
    }
}
