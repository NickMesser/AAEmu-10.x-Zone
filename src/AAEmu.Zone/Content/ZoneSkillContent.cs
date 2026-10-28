using System.Collections.Frozen;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Protocol.Units;
using Microsoft.Data.Sqlite;

namespace AAEmu.Zone.Content;

public sealed record SkillDefinition(uint Id, uint AbilityId)
{
    public uint CooldownMilliseconds { get; init; }
    public uint CooldownTagId { get; init; }
    public uint SecondCooldownTagId { get; init; }
    public uint ThirdCooldownTagId { get; init; }
    public uint ChargeCount { get; init; }
    public uint ChargeCooldownMilliseconds { get; init; }
    public bool SwitchToSkillCooldown { get; init; }
}

public readonly record struct SkillTagDefinition(uint SkillId, uint TagId);

/// <summary>Skill ownership and native ability experience thresholds from the configured content.</summary>
public sealed class ZoneSkillContent
{
    public static ZoneSkillContent Empty { get; } = new([], []);
    public IReadOnlyDictionary<uint, SkillDefinition> Skills { get; }
    private readonly FrozenDictionary<byte, int> _experience;
    private readonly FrozenDictionary<uint, IReadOnlySet<uint>> _tagsBySkill;
    private readonly FrozenDictionary<uint, IReadOnlySet<uint>> _skillsByTag;

    public ZoneSkillContent(IEnumerable<SkillDefinition> skills, IEnumerable<KeyValuePair<byte, int>> experience,
        IEnumerable<SkillTagDefinition>? tags = null)
    {
        Skills = skills.ToDictionary(v => v.Id).ToFrozenDictionary();
        _experience = experience.ToDictionary().ToFrozenDictionary();
        var links = (tags ?? []).Where(v => v.TagId != 0 && Skills.ContainsKey(v.SkillId)).ToArray();
        _tagsBySkill = links.GroupBy(v => v.SkillId).ToFrozenDictionary(g => g.Key,
            g => (IReadOnlySet<uint>)g.Select(v => v.TagId).ToFrozenSet());
        _skillsByTag = links.GroupBy(v => v.TagId).ToFrozenDictionary(g => g.Key,
            g => (IReadOnlySet<uint>)g.Select(v => v.SkillId).ToFrozenSet());
    }

    public IReadOnlySet<uint> TagsForSkill(uint skillId) => _tagsBySkill.GetValueOrDefault(skillId) ?? FrozenSet<uint>.Empty;
    public IReadOnlySet<uint> SkillsWithTag(uint tagId) => _skillsByTag.GetValueOrDefault(tagId) ?? FrozenSet<uint>.Empty;

    public byte LevelFromExperience(uint experience)
    {
        // 39CB72F0 compares signed i32 values, checks the next level, and caps at 100.
        // 39BDBE80 zero-initializes absent level rows before loading total_exp.
        for (byte level = 1; level <= 100; level++)
            if (unchecked((int)experience) < _experience.GetValueOrDefault((byte)(level + 1))) return level;
        return 100;
    }

    public byte AbilityLevel(UnitStateSnapshot snapshot, uint abilityId, byte primaryLevel)
    {
        // 39D1BDE0 uses the primary unit level for every noncharacter.
        if (snapshot.Identity.Kind != ZoneUnitKind.Character) return primaryLevel;
        if (abilityId == 30) return 0; // native None sentinel
        if (abilityId > 30) throw new InvalidDataException($"Invalid passive ability {abilityId}.");
        // 39CB7510 initializes General (0) to level one; it is absent from the wire's 1..29 entries.
        if (abilityId == 0 || snapshot.Character is null) return 1;
        return LevelFromExperience(snapshot.Character.Expertise[checked((int)abilityId - 1)].Experience);
    }

    internal static ZoneSkillContent Load(SqliteConnection connection)
    {
        var skills = new List<SkillDefinition>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, ability_id, cooldown_time, COALESCE(CAST(cooldown_tag_id AS INTEGER), 0),
                       COALESCE(CAST(second_cooldown_tag_id AS INTEGER), 0), COALESCE(CAST(third_cooldown_tag_id AS INTEGER), 0),
                       charge_count, charge_cooldown_time, switch_to_skill_cooldown FROM skills
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
                skills.Add(new(checked((uint)reader.GetInt64(0)), checked((uint)reader.GetInt64(1)))
                {
                    CooldownMilliseconds = checked((uint)reader.GetInt64(2)),
                    CooldownTagId = checked((uint)reader.GetInt64(3)),
                    SecondCooldownTagId = checked((uint)reader.GetInt64(4)),
                    ThirdCooldownTagId = checked((uint)reader.GetInt64(5)),
                    ChargeCount = checked((uint)reader.GetInt64(6)),
                    ChargeCooldownMilliseconds = checked((uint)reader.GetInt64(7)),
                    SwitchToSkillCooldown = NativeContentReader.Boolean(reader, 8)
                });
        }
        var experience = new Dictionary<byte, int>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, total_exp FROM levels";
            using var reader = command.ExecuteReader();
            while (reader.Read()) experience.Add(checked((byte)reader.GetInt64(0)), unchecked((int)reader.GetInt64(1)));
        }
        var tags = new List<SkillTagDefinition>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT CAST(skill_id AS INTEGER), CAST(tag_id AS INTEGER) FROM tagged_skills WHERE skill_id IS NOT NULL AND tag_id IS NOT NULL";
            using var reader = command.ExecuteReader();
            while (reader.Read()) tags.Add(new(checked((uint)reader.GetInt64(0)), checked((uint)reader.GetInt64(1))));
        }
        return new(skills, experience, tags);
    }
}
