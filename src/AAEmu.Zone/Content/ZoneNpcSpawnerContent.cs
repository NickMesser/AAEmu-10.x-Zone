using System.Collections.Frozen;
using Microsoft.Data.Sqlite;
using static AAEmu.Zone.Content.NativeContentReader;

namespace AAEmu.Zone.Content;

/// <summary>Spawner descriptor fields read by 39349F80/3933C590.</summary>
public sealed record ZoneNpcSpawnerSchedule(float ResetHour, float StartHour, float EndHour, int MaximumPopulation, int MinimumPopulation);

public sealed record ZoneNpcSpawnerDefinition
{
    public uint Id { get; init; }
    public bool ActivationState { get; init; }
    public float ResetHour { get; init; }
    public float EndHour { get; init; }
    public int MaximumPopulation { get; init; }
    public int MinimumPopulation { get; init; }
    public string Name { get; init; } = "";
    public uint CategoryId { get; init; }
    public bool SaveInstance { get; init; }
    public float SpawnDelayMaximumSeconds { get; init; }
    public float SpawnDelayMinimumSeconds { get; init; }
    public float StartHour { get; init; }
    public int SuspendSpawnCount { get; init; }
    public float TestRadiusNpc { get; init; }
    public float TestRadiusPlayer { get; init; }
    public ZoneNpcSpawnerSchedule Schedule => new(ResetHour, StartHour, EndHour, MaximumPopulation, MinimumPopulation);
}

public enum ZoneNpcSpawnerMemberKind : uint { None, Npc, NpcGroup }
public sealed record ZoneNpcSpawnerMemberDefinition(uint Id, string? MemberType, uint MemberId, uint SpawnerId, float Weight);

public sealed class ZoneNpcSpawnerMember
{
    public ZoneNpcSpawnerMemberDefinition Definition { get; }
    public uint Id => Definition.Id;
    public uint SpawnerId => Definition.SpawnerId;
    public float Weight => Definition.Weight;
    public ZoneNpcSpawnerMemberKind Kind { get; }
    public NpcTemplateDefinition? Npc { get; }
    public NpcGroupContent? Group { get; }

    internal ZoneNpcSpawnerMember(ZoneNpcSpawnerMemberDefinition definition, ZoneNpcSpawnerMemberKind kind,
        NpcTemplateDefinition? npc, NpcGroupContent? group)
    { Definition = definition; Kind = kind; Npc = npc; Group = group; }
}

public sealed class ZoneNpcSpawnerType
{
    public ZoneNpcSpawnerDefinition Definition { get; }
    public uint Id => Definition.Id;
    public ZoneNpcSpawnerSchedule Schedule => Definition.Schedule;
    public IReadOnlyList<ZoneNpcSpawnerMember> Members { get; }

    internal ZoneNpcSpawnerType(ZoneNpcSpawnerDefinition definition, IEnumerable<ZoneNpcSpawnerMember> members)
    { Definition = definition; Members = Array.AsReadOnly(members.ToArray()); }
}

/// <summary>Native 39AFECF0/39AFEFF0 spawner scalars and shared polymorphic member references.</summary>
public sealed class ZoneNpcSpawnerContent
{
    public static ZoneNpcSpawnerContent Empty { get; } = new([], [],
        FrozenDictionary<uint, NpcTemplateDefinition>.Empty, FrozenDictionary<uint, NpcGroupContent>.Empty);
    public static ZoneNpcSpawnerContent Disabled { get; } = new([], [],
        FrozenDictionary<uint, NpcTemplateDefinition>.Empty, FrozenDictionary<uint, NpcGroupContent>.Empty, false);
    public bool IsEnabled { get; }
    public IReadOnlyList<ZoneNpcSpawnerMemberDefinition> SourceMembers { get; }
    public IReadOnlyList<ZoneNpcSpawnerMember> MemberRows { get; }
    public IReadOnlyDictionary<uint, ZoneNpcSpawnerMember> Members { get; }
    public IReadOnlyList<ZoneNpcSpawnerType> Rows { get; }
    public IReadOnlyDictionary<uint, ZoneNpcSpawnerType> Spawners { get; }

    public ZoneNpcSpawnerContent(IEnumerable<ZoneNpcSpawnerDefinition> spawners,
        IEnumerable<ZoneNpcSpawnerMemberDefinition> members,
        IReadOnlyDictionary<uint, NpcTemplateDefinition> npcs, IReadOnlyDictionary<uint, NpcGroupContent> groups)
        : this(spawners, members, npcs, groups, true) { }

    private ZoneNpcSpawnerContent(IEnumerable<ZoneNpcSpawnerDefinition> spawners,
        IEnumerable<ZoneNpcSpawnerMemberDefinition> members,
        IReadOnlyDictionary<uint, NpcTemplateDefinition> npcs, IReadOnlyDictionary<uint, NpcGroupContent> groups, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(spawners);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(npcs);
        ArgumentNullException.ThrowIfNull(groups);
        IsEnabled = enabled;
        var definitions = spawners.ToArray();
        var sourceMembers = members.ToArray();
        var accepted = new List<ZoneNpcSpawnerMember>();
        var byId = new Dictionary<uint, ZoneNpcSpawnerMember>();
        foreach (var definition in sourceMembers)
        {
            ArgumentNullException.ThrowIfNull(definition);
            var kind = CString(definition.MemberType) switch
            {
                "Npc" => ZoneNpcSpawnerMemberKind.Npc,
                "NpcGroup" => ZoneNpcSpawnerMemberKind.NpcGroup,
                _ => ZoneNpcSpawnerMemberKind.None
            };
            var npc = kind == ZoneNpcSpawnerMemberKind.Npc ? npcs.GetValueOrDefault(definition.MemberId) : null;
            var group = kind == ZoneNpcSpawnerMemberKind.NpcGroup ? groups.GetValueOrDefault(definition.MemberId) : null;
            // Recognized unresolved references are discarded; unknown types retain a null target.
            if ((kind == ZoneNpcSpawnerMemberKind.Npc && npc is null) ||
                (kind == ZoneNpcSpawnerMemberKind.NpcGroup && group is null)) continue;
            var member = new ZoneNpcSpawnerMember(definition, kind, npc, group);
            accepted.Add(member);
            byId[member.Id] = member;
        }
        var lastIndex = new Dictionary<uint, int>();
        for (var index = 0; index < definitions.Length; index++)
        {
            ArgumentNullException.ThrowIfNull(definitions[index]);
            lastIndex[definitions[index].Id] = index;
        }
        var bySpawner = accepted.ToLookup(member => member.SpawnerId);
        var rows = definitions.Select((definition, index) => new ZoneNpcSpawnerType(definition,
            lastIndex[definition.Id] == index ? bySpawner[definition.Id] : [])).ToArray();
        SourceMembers = Array.AsReadOnly(sourceMembers);
        MemberRows = accepted.AsReadOnly();
        Members = byId.ToFrozenDictionary();
        Rows = Array.AsReadOnly(rows);
        Spawners = lastIndex.ToFrozenDictionary(pair => pair.Key, pair => rows[pair.Value]);
    }

    internal static ZoneNpcSpawnerContent Load(SqliteConnection connection,
        IReadOnlyDictionary<uint, NpcTemplateDefinition> npcs, IReadOnlyDictionary<uint, NpcGroupContent> groups,
        bool enabled)
    {
        // X2 startup converts CSystem mode != 0 to catalog+21380. Both loaders skip their
        // queries when that explicit boolean is false; this does not infer native mode names.
        if (!enabled) return Disabled;
        var members = new List<ZoneNpcSpawnerMemberDefinition>();
        Read(connection, "SELECT id, member_type, member_id, npc_spawner_id, weight FROM npc_spawner_npcs",
            r => members.Add(new(UInt32(r, 0), String(r, 1), UInt32(r, 2), UInt32(r, 3), Single(r, 4))));
        var definitions = new List<ZoneNpcSpawnerDefinition>();
        Read(connection, """
            SELECT id, activation_state, destroyTime, endTime, maxPopulation, min_population, name,
                npc_spawner_category_id, save_indun, spawn_delay_max, spawn_delay_min, startTime,
                suspend_spawn_count, test_radius_npc, test_radius_pc FROM npc_spawners
            """, r => definitions.Add(new()
        {
            Id = UInt32(r, 0), ActivationState = Boolean(r, 1), ResetHour = Single(r, 2), EndHour = Single(r, 3),
            MaximumPopulation = Int32(r, 4), MinimumPopulation = Int32(r, 5), Name = CString(String(r, 6)),
            CategoryId = UInt32(r, 7), SaveInstance = Boolean(r, 8), SpawnDelayMaximumSeconds = Single(r, 9),
            SpawnDelayMinimumSeconds = Single(r, 10), StartHour = Single(r, 11), SuspendSpawnCount = Int32(r, 12),
            TestRadiusNpc = Single(r, 13), TestRadiusPlayer = Single(r, 14)
        }));
        return new(definitions, members, npcs, groups);
    }

    private static string CString(string? value)
    {
        value ??= "";
        var terminator = value.IndexOf('\0');
        return terminator < 0 ? value : value[..terminator];
    }
}
