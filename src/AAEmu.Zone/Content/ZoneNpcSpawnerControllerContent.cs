using System.Collections.Frozen;
using Microsoft.Data.Sqlite;
using static AAEmu.Zone.Content.NativeContentReader;

using AAEmu.Zone.Npcs;

namespace AAEmu.Zone.Content;

public sealed record ZoneNpcSpawnerTowerDefinition(uint Id, uint PrimarySpawnerType);
public sealed record ZoneNpcSpawnerTowerPhaseDefinition(uint Id, uint TowerId);
public sealed record ZoneNpcSpawnerTowerTargetDefinition(uint Id, uint PhaseId, string? TargetType,
    uint TargetId, bool DespawnOnNextStep);
public sealed record ZoneNpcSpawnerGameScheduleDefinition(uint Id);
public sealed record ZoneNpcSpawnerGameScheduleLink(uint Id, uint ScheduleId, uint SpawnerType);
public enum ZoneNpcSpawnerTowerTargetKind : uint { None, NpcSpawner, DoodadAlmighty }

public sealed class ZoneNpcSpawnerTowerTarget
{
    public ZoneNpcSpawnerTowerTargetDefinition Definition { get; }
    public ZoneNpcSpawnerTowerTargetKind Kind { get; }
    public bool IsResolved { get; }
    internal ZoneNpcSpawnerTowerTarget(ZoneNpcSpawnerTowerTargetDefinition definition,
        ZoneNpcSpawnerTowerTargetKind kind, bool resolved)
    { Definition = definition; Kind = kind; IsResolved = resolved; }
}

public sealed class ZoneNpcSpawnerTowerPhase
{
    public ZoneNpcSpawnerTowerPhaseDefinition Definition { get; }
    public IReadOnlyList<ZoneNpcSpawnerTowerTarget> Targets { get; }
    internal ZoneNpcSpawnerTowerPhase(ZoneNpcSpawnerTowerPhaseDefinition definition,
        IEnumerable<ZoneNpcSpawnerTowerTarget> targets)
    { Definition = definition; Targets = Array.AsReadOnly(targets.ToArray()); }
}

public sealed class ZoneNpcSpawnerTower
{
    public ZoneNpcSpawnerTowerDefinition Definition { get; }
    public IReadOnlyList<ZoneNpcSpawnerTowerPhase> Phases { get; }
    internal ZoneNpcSpawnerTower(ZoneNpcSpawnerTowerDefinition definition, IEnumerable<ZoneNpcSpawnerTowerPhase> phases)
    { Definition = definition; Phases = Array.AsReadOnly(phases.ToArray()); }
}

public sealed class ZoneNpcSpawnerGameSchedule
{
    public ZoneNpcSpawnerGameScheduleDefinition Definition { get; }
    public IReadOnlyList<ZoneNpcSpawnerGameScheduleLink> Spawners { get; }
    internal ZoneNpcSpawnerGameSchedule(ZoneNpcSpawnerGameScheduleDefinition definition,
        IEnumerable<ZoneNpcSpawnerGameScheduleLink> spawners)
    { Definition = definition; Spawners = Array.AsReadOnly(spawners.ToArray()); }
}

/// <summary>
/// Relationships consumed by native3935A140/39335100, loaded by39B9D1F0/39C85360/39C84A70
/// and39B9C4D0/39CD8CF0. This is the initial spawner-control projection, not the tower or
/// calendar simulation. Parent maps traverse unsigned IDs; child vectors retain SQL source order.
/// </summary>
public sealed class ZoneNpcSpawnerControllerContent
{
    public IReadOnlyList<ZoneNpcSpawnerTowerDefinition> SourceTowers { get; }
    public IReadOnlyList<ZoneNpcSpawnerTowerPhaseDefinition> SourcePhases { get; }
    public IReadOnlyList<ZoneNpcSpawnerTowerTargetDefinition> SourceTargets { get; }
    public IReadOnlyList<ZoneNpcSpawnerGameScheduleDefinition> SourceSchedules { get; }
    public IReadOnlyList<ZoneNpcSpawnerGameScheduleLink> SourceScheduleLinks { get; }
    public IReadOnlyList<ZoneNpcSpawnerTower> Towers { get; }
    public IReadOnlyList<ZoneNpcSpawnerTowerPhase> PhaseRows { get; }
    public IReadOnlyList<ZoneNpcSpawnerTowerTarget> TargetRows { get; }
    public IReadOnlyList<ZoneNpcSpawnerGameSchedule> Schedules { get; }
    public int UnresolvedPhaseParentCount { get; }
    public int UnresolvedTargetParentCount { get; }
    public int UnresolvedScheduleParentCount { get; }
    public int UnresolvedTargetCount { get; }

    public ZoneNpcSpawnerControllerContent(IEnumerable<ZoneNpcSpawnerTowerDefinition> towers,
        IEnumerable<ZoneNpcSpawnerTowerPhaseDefinition> phases, IEnumerable<ZoneNpcSpawnerTowerTargetDefinition> targets,
        IEnumerable<ZoneNpcSpawnerGameScheduleDefinition> schedules, IEnumerable<ZoneNpcSpawnerGameScheduleLink> scheduleLinks,
        IEnumerable<uint> npcSpawnerIds, IEnumerable<uint> doodadAlmightyIds)
    {
        SourceTowers = Snapshot(towers); SourcePhases = Snapshot(phases); SourceTargets = Snapshot(targets);
        SourceSchedules = Snapshot(schedules); SourceScheduleLinks = Snapshot(scheduleLinks);
        ArgumentNullException.ThrowIfNull(npcSpawnerIds); ArgumentNullException.ThrowIfNull(doodadAlmightyIds);
        var npcIds = npcSpawnerIds.ToFrozenSet(); var doodadIds = doodadAlmightyIds.ToFrozenSet();
        TargetRows = Array.AsReadOnly(SourceTargets.Select(source =>
        {
            var text = source.TargetType;
            if (text?.IndexOf('\0') is >= 0 and var end) text = text[..end];
            var kind = text switch
            {
                "NpcSpawner" => ZoneNpcSpawnerTowerTargetKind.NpcSpawner,
                "DoodadAlmighty" => ZoneNpcSpawnerTowerTargetKind.DoodadAlmighty,
                _ => ZoneNpcSpawnerTowerTargetKind.None
            };
            return new ZoneNpcSpawnerTowerTarget(source, kind, kind switch
            {
                ZoneNpcSpawnerTowerTargetKind.NpcSpawner => npcIds.Contains(source.TargetId),
                ZoneNpcSpawnerTowerTargetKind.DoodadAlmighty => doodadIds.Contains(source.TargetId),
                _ => false
            });
        }).ToArray());

        // Native generated descriptor indexes point to the last row for a duplicate ID. The
        // original physical phase rows still all participate in the later tower linking pass.
        var lastPhase = new Dictionary<uint, int>();
        for (var i = 0; i < SourcePhases.Count; i++) lastPhase[SourcePhases[i].Id] = i;
        var targetsByPhase = TargetRows.ToLookup(target => target.Definition.PhaseId);
        PhaseRows = Array.AsReadOnly(SourcePhases.Select((phase, i) => new ZoneNpcSpawnerTowerPhase(phase,
            lastPhase[phase.Id] == i ? targetsByPhase[phase.Id] : [])).ToArray());
        var towerMap = new SortedDictionary<uint, ZoneNpcSpawnerTowerDefinition>();
        foreach (var tower in SourceTowers) towerMap[tower.Id] = tower;
        var phasesByTower = PhaseRows.ToLookup(phase => phase.Definition.TowerId);
        Towers = Array.AsReadOnly(towerMap.Values.Select(tower => new ZoneNpcSpawnerTower(tower, phasesByTower[tower.Id])).ToArray());
        var scheduleMap = new SortedDictionary<uint, ZoneNpcSpawnerGameScheduleDefinition>();
        foreach (var schedule in SourceSchedules) scheduleMap[schedule.Id] = schedule;
        var linksBySchedule = SourceScheduleLinks.ToLookup(link => link.ScheduleId);
        Schedules = Array.AsReadOnly(scheduleMap.Values.Select(schedule =>
            new ZoneNpcSpawnerGameSchedule(schedule, linksBySchedule[schedule.Id])).ToArray());
        UnresolvedPhaseParentCount = SourcePhases.Count(phase => !towerMap.ContainsKey(phase.TowerId));
        UnresolvedTargetParentCount = SourceTargets.Count(target => !lastPhase.ContainsKey(target.PhaseId));
        UnresolvedScheduleParentCount = SourceScheduleLinks.Count(link => !scheduleMap.ContainsKey(link.ScheduleId));
        UnresolvedTargetCount = TargetRows.Count(target => target.Kind != ZoneNpcSpawnerTowerTargetKind.None && !target.IsResolved);
    }

    /// <summary>
    /// Project after actual factory admission. Native tower target traversal runs once per tower
    /// only after a nonzero primary has physical instances; it does not multiply phase targets by
    /// the number of primary instances. Schedule linking does not resolve a spawner content pointer.
    /// </summary>
    public ZoneNpcSpawnerPostLoadRelations ProjectPostLoadRelations(IEnumerable<uint> admittedSpawnerTypes)
    {
        ArgumentNullException.ThrowIfNull(admittedSpawnerTypes);
        var admitted = admittedSpawnerTypes.ToHashSet();
        var towers = new List<uint>();
        foreach (var tower in Towers)
        {
            var primary = tower.Definition.PrimarySpawnerType;
            if (tower.Phases.Count == 0 || primary == 0 || !admitted.Contains(primary)) continue;
            towers.Add(primary);
            foreach (var phase in tower.Phases)
                foreach (var target in phase.Targets)
                {
                    if (target.Kind != ZoneNpcSpawnerTowerTargetKind.NpcSpawner) continue;
                    // Native dereferences this descriptor pointer. Refuse a malformed reached
                    // relation explicitly instead of silently substituting the raw missing ID.
                    if (!target.IsResolved)
                        throw new InvalidDataException($"Tower target {target.Definition.Id} references missing NPC spawner {target.Definition.TargetId}.");
                    if (target.Definition.TargetId != 0) towers.Add(target.Definition.TargetId);
                }
        }
        return new(towers, Schedules.SelectMany(schedule => schedule.Spawners.Select(link => link.SpawnerType)));
    }

    private static IReadOnlyList<T> Snapshot<T>(IEnumerable<T> source) where T : class
    {
        ArgumentNullException.ThrowIfNull(source);
        var rows = source.ToArray();
        foreach (var row in rows) ArgumentNullException.ThrowIfNull(row);
        return Array.AsReadOnly(rows);
    }

    internal static ZoneNpcSpawnerControllerContent Load(SqliteConnection connection)
    {
        var towers = new List<ZoneNpcSpawnerTowerDefinition>();
        var phases = new List<ZoneNpcSpawnerTowerPhaseDefinition>();
        var targets = new List<ZoneNpcSpawnerTowerTargetDefinition>();
        var schedules = new List<ZoneNpcSpawnerGameScheduleDefinition>();
        var links = new List<ZoneNpcSpawnerGameScheduleLink>();
        var npcIds = new List<uint>(); var doodadIds = new List<uint>();
        Read(connection, "SELECT id FROM npc_spawners", row => npcIds.Add(UInt32(row, 0)));
        Read(connection, "SELECT id FROM doodad_almighties", row => doodadIds.Add(UInt32(row, 0)));
        // Full native projections preserve the source scan and require the configured schema.
        Read(connection, """
            SELECT id, broadcast_event_to_whole_seamless_world, color_end, color_progress, color_start, end_msg, first_wave_after, force_end_time, icon_key, kill_npc_count, kill_npc_id, operation_time_limit, start_day_of_week_bit, start_hour, start_minute, start_msg, target_npc_spawner_id, title_msg, tod, tod_day_interval, ref_zone_waiting, name, start_hour1, start_minute1, start_hour2, start_minute2, start_hour3, start_minute3, start_hour4, start_minute4, start_hour5, start_minute5, start_hour6, start_minute6, map_icon_path, map_icon_key, map_icon_tooltip, hud_icon_path, hud_icon_key, hud_icon_tooltip, sync_step_use, sound_pack_id FROM tower_defs
            """, row => towers.Add(new(UInt32(row, 0), UInt32(row, 16))));
        Read(connection, """
            SELECT id, cond_comp_by_and, cond_to_next_time, end_cond_comp_by_and, end_cond_to_check_time, msg, sound_pack_id, tower_def_id FROM tower_def_progs
            """, row => phases.Add(new(UInt32(row, 0), UInt32(row, 7))));
        Read(connection, """
            SELECT id, despawn_on_next_step, spawn_target_type, spawn_target_id, tower_def_prog_id FROM tower_def_prog_spawn_targets
            """, row => targets.Add(new(UInt32(row, 0), UInt32(row, 4), row.IsDBNull(2) ? null : String(row, 2), UInt32(row, 3), Boolean(row, 1))));
        Read(connection, """
            SELECT id, day_of_week_id, ed_day, ed_hour, ed_min, ed_month, ed_year, end_time, end_time_min, st_day, st_hour, st_min, st_month, st_year, start_time, start_time_min FROM game_schedules
            """, row => schedules.Add(new(UInt32(row, 0))));
        Read(connection, "SELECT id, game_schedule_id, spawner_id FROM game_schedule_spawners",
            row => links.Add(new(UInt32(row, 0), UInt32(row, 1), UInt32(row, 2))));
        return new(towers, phases, targets, schedules, links, npcIds, doodadIds);
    }
}
