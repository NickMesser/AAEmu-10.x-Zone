using System.Collections.Frozen;
using Microsoft.Data.Sqlite;
using static AAEmu.Zone.Content.NativeContentReader;

namespace AAEmu.Zone.Content;

/// <summary>Source-order np_skills row. Conditions are not skill IDs or goal-operation flags.</summary>
public sealed record ZoneNpcSkillRow(uint OwnerId, uint SkillId, int Condition, float Parameter1, float Parameter2);

/// <summary>
/// The skill fields consulted by the dedicated NPC picker and its validation/request path.
/// This does not resolve equipment ranges, skill modifiers, requirements or skill controllers.
/// </summary>
public sealed record ZoneNpcSkillDefinition(uint Id)
{
    public int TargetType { get; init; }
    public uint TargetUnitMask { get; init; }
    public int MinimumRange { get; init; }
    public int MaximumRange { get; init; }
    public int WeaponRangeSlot { get; init; } = -1;
    public int WeaponAngleSlot { get; init; } = -1;
    public int TargetAngle { get; init; } = 360;
    public int CastingMilliseconds { get; init; }
    public int ManaCost { get; init; }
    public float ManaLevelMultiplier { get; init; }
    public int Category { get; init; }
    public uint ControllerId { get; init; }
    public uint ToggleBuffId { get; init; }
    public bool TargetAlive { get; init; }
    public bool TargetDead { get; init; }
    public bool TargetMyNpc { get; init; }
    public bool CheckObstacle { get; init; }
    public float ValidHeight { get; init; }
    public bool ValidHeightEdgeToEdge { get; init; }
    public bool IgnoreGlobalCooldown { get; init; }
    public bool SkipValidateSource { get; init; }
    public ulong UseConditionBits { get; init; }
    public bool OrUnitRequirements { get; init; }
    public uint CombatResourceId { get; init; }
    public bool DefaultGlobalCooldown { get; init; }
    public int CustomGlobalCooldownMilliseconds { get; init; }
    public int WeaponGlobalCooldownSlot { get; init; } = -1;
    public bool PositionTarget => TargetType is 6 or 11 or 15 or 23;
}

/// <summary>
/// Original np_skills owner links, skill validation scalars and enabled Skill requirements.
/// Missing descriptor references remain in the child lists and are skipped by the picker.
/// </summary>
public sealed class ZoneNpcSkillContent
{
    public static ZoneNpcSkillContent Empty { get; } = new([], []);
    public IReadOnlyList<ZoneNpcSkillRow> Rows { get; }
    public IReadOnlyDictionary<uint, ZoneNpcSkillDefinition> Skills { get; }
    /// <summary>Buff descriptors proven to have none of this slice's unported source/target predicates or skill modifiers.</summary>
    public IReadOnlySet<uint> CleanEligibilityBuffs { get; }
    private readonly FrozenDictionary<uint, IReadOnlyList<ZoneNpcSkillRow>> _byOwner;
    private readonly FrozenDictionary<uint, IReadOnlyList<ZoneUnitRequirement>> _requirements;

    public ZoneNpcSkillContent(IEnumerable<ZoneNpcSkillRow> rows, IEnumerable<ZoneNpcSkillDefinition> skills,
        IEnumerable<KeyValuePair<uint, ZoneUnitRequirement>>? requirements = null, IEnumerable<uint>? cleanEligibilityBuffs = null)
    {
        ArgumentNullException.ThrowIfNull(rows); ArgumentNullException.ThrowIfNull(skills);
        var copy = rows.ToArray();
        Rows = Array.AsReadOnly(copy);
        Skills = skills.ToDictionary(row => row.Id).ToFrozenDictionary();
        CleanEligibilityBuffs = (cleanEligibilityBuffs ?? []).ToFrozenSet();
        _byOwner = copy.GroupBy(row => row.OwnerId).ToFrozenDictionary(group => group.Key,
            group => (IReadOnlyList<ZoneNpcSkillRow>)Array.AsReadOnly(group.ToArray()));
        _requirements = (requirements ?? []).GroupBy(row => row.Key).ToFrozenDictionary(group => group.Key,
            group => (IReadOnlyList<ZoneUnitRequirement>)Array.AsReadOnly(group.Select(row => row.Value).ToArray()));
    }

    public IReadOnlyList<ZoneNpcSkillRow> ForNpc(uint templateId) => _byOwner.GetValueOrDefault(templateId) ?? [];
    public IReadOnlyList<ZoneUnitRequirement> Requirements(uint skillId) => _requirements.GetValueOrDefault(skillId) ?? [];

    internal static ZoneNpcSkillContent Load(SqliteConnection connection)
    {
        var rows = new List<ZoneNpcSkillRow>();
        // Preserve source traversal: the original generated query has no ORDER BY.
        Read(connection, "SELECT owner_type, owner_id, skill_id, skill_use_condition_id, skill_use_param1, skill_use_param2 FROM np_skills", r =>
        {
            if (String(r, 0).Split('\0')[0] == "Npc")
                rows.Add(new(UInt32(r, 1), UInt32(r, 2), Int32(r, 3), Single(r, 4), Single(r, 5)));
        });
        var definitions = new List<ZoneNpcSkillDefinition>();
        Read(connection, """
            SELECT id, target_type_id, target_unit_param, min_range, max_range, weapon_slot_for_range_id,
                weapon_slot_for_angle_id, mana_cost, mana_level_md, category_id, skill_controller_id,
                toggle_buff_id, target_alive, target_dead, target_my_npc, check_obstacle, valid_height,
                valid_height_edge_to_edge, ignore_global_cooldown, skip_validate_source, use_condition_bits,
                or_unit_reqs, combat_resource_id, default_gcd, custom_gcd, weapon_gcd_id, target_angle, casting_time FROM skills
            """, r => definitions.Add(new(UInt32(r, 0))
        {
            TargetType = Int32(r, 1), TargetUnitMask = UInt32(r, 2), MinimumRange = Int32(r, 3),
            MaximumRange = Int32(r, 4), WeaponRangeSlot = Int32(r, 5), WeaponAngleSlot = Int32(r, 6),
            ManaCost = Int32(r, 7), ManaLevelMultiplier = Single(r, 8), Category = Int32(r, 9),
            ControllerId = UInt32(r, 10), ToggleBuffId = UInt32(r, 11), TargetAlive = Boolean(r, 12),
            TargetDead = Boolean(r, 13), TargetMyNpc = Boolean(r, 14), CheckObstacle = Boolean(r, 15),
            ValidHeight = Single(r, 16), ValidHeightEdgeToEdge = Boolean(r, 17), IgnoreGlobalCooldown = Boolean(r, 18),
            SkipValidateSource = Boolean(r, 19), UseConditionBits = r.IsDBNull(20) ? 0 : unchecked((ulong)r.GetInt64(20)),
            OrUnitRequirements = Boolean(r, 21), CombatResourceId = UInt32(r, 22), DefaultGlobalCooldown = Boolean(r, 23),
            CustomGlobalCooldownMilliseconds = Int32(r, 24), WeaponGlobalCooldownSlot = Int32(r, 25), TargetAngle = Int32(r, 26),
            CastingMilliseconds = Int32(r, 27)
        }));
        var requirements = new List<KeyValuePair<uint, ZoneUnitRequirement>>();
        Read(connection, "SELECT owner_type, owner_id, display_msg, kind_id, value1, value2, value3 FROM unit_reqs WHERE enable = 't'", r =>
        {
            if (String(r, 0).Split('\0')[0] == "Skill")
                requirements.Add(new(UInt32(r, 1), new(UInt32(r, 3), Int32(r, 4), Int32(r, 5), Int32(r, 6), Boolean(r, 2))));
        });
        // A clean source profile can execute without inventing active buff/skill-modifier
        // consumers. Unknown or effectful descriptors refuse when actually encountered.
        var effectful = new HashSet<uint>();
        Read(connection, "SELECT owner_type, owner_id FROM skill_modifiers WHERE enable = 't'", r =>
        { if (String(r, 0).Split('\0')[0] == "Buff") effectful.Add(UInt32(r, 1)); });
        Read(connection, "SELECT b.buff_id FROM tagged_buffs b JOIN unit_status_buff_tags s ON s.tag_id=b.tag_id", r => effectful.Add(UInt32(r, 0)));
        var clean = new List<uint>();
        Read(connection, """
            SELECT id, silence, root, sleep, stun, crippled, stealth, framehold, ragdoll, psychokinesis,
                gliding, knock_down, blank_minded, fastened, pacifist, off_passive, impossible_targeting,
                impossible_change_targeting, only_my_pet, only_pet_owner,
                disarmament_main_hand, disarmament_off_hand, disarmament_ranged, disarmament_musical,
                CAST(skill_controller_id AS INTEGER) FROM buffs
            """, r =>
        {
            var id = UInt32(r, 0);
            if (effectful.Contains(id)) return;
            for (var i = 1; i < 24; i++) if (Boolean(r, i)) return;
            if (UInt32(r, 24) == 0) clean.Add(id);
        });
        return new(rows, definitions, requirements, clean);
    }
}
