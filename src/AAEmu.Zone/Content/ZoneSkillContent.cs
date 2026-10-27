using System.Collections.Frozen;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Protocol.Units;
using Microsoft.Data.Sqlite;

namespace AAEmu.Zone.Content;

public sealed record SkillDefinition(uint Id, uint AbilityId);

/// <summary>Skill ownership and native ability experience thresholds from the configured content.</summary>
public sealed class ZoneSkillContent
{
    public static ZoneSkillContent Empty { get; } = new([], []);
    public IReadOnlyDictionary<uint, SkillDefinition> Skills { get; }
    private readonly FrozenDictionary<byte, int> _experience;

    public ZoneSkillContent(IEnumerable<SkillDefinition> skills, IEnumerable<KeyValuePair<byte, int>> experience)
    {
        Skills = skills.ToDictionary(v => v.Id).ToFrozenDictionary();
        _experience = experience.ToDictionary().ToFrozenDictionary();
    }

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
            command.CommandText = "SELECT id, ability_id FROM skills";
            using var reader = command.ExecuteReader();
            while (reader.Read()) skills.Add(new(checked((uint)reader.GetInt64(0)), checked((uint)reader.GetInt64(1))));
        }
        var experience = new Dictionary<byte, int>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, total_exp FROM levels";
            using var reader = command.ExecuteReader();
            while (reader.Read()) experience.Add(checked((byte)reader.GetInt64(0)), unchecked((int)reader.GetInt64(1)));
        }
        return new(skills, experience);
    }
}
