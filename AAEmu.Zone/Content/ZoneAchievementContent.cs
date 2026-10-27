using System.Collections.Frozen;
using Microsoft.Data.Sqlite;
using static AAEmu.Zone.Content.NativeContentReader;

namespace AAEmu.Zone.Content;

public sealed record AchievementIndexDefinition(uint Id, uint SubCategoryId, uint ParentId, bool SeasonOff);
public sealed record AchievementSubCategoryIndexDefinition(uint Id, uint RewardAchievementId);
public sealed record AchievementObjectiveIndexDefinition(uint Id, uint AchievementId, uint RecordId);
public sealed record AchievementPrerequisiteDefinition(uint Id, uint AchievementId, uint CompletedAchievementId);
public sealed record CharacterRecordDefinition(uint Id, int Kind, int Value1, int Value2);

/// <summary>Unfiltered source rows for the six achievement dependency indexes built by native 39C56B80.</summary>
public sealed class ZoneAchievementContent
{
    public IReadOnlyList<AchievementIndexDefinition> Achievements { get; }
    public IReadOnlyDictionary<uint, AchievementSubCategoryIndexDefinition> SubCategories { get; }
    public IReadOnlyList<AchievementObjectiveIndexDefinition> Objectives { get; }
    public IReadOnlyList<AchievementPrerequisiteDefinition> Prerequisites { get; }
    public IReadOnlyDictionary<uint, CharacterRecordDefinition> Records { get; }

    public ZoneAchievementContent(IEnumerable<AchievementIndexDefinition> achievements,
        IEnumerable<AchievementSubCategoryIndexDefinition> subCategories,
        IEnumerable<AchievementObjectiveIndexDefinition> objectives,
        IEnumerable<AchievementPrerequisiteDefinition> prerequisites, IEnumerable<CharacterRecordDefinition> records)
    {
        // Native vectors retain descriptor iteration order and repeated relation values.
        Achievements = Array.AsReadOnly(achievements.ToArray());
        SubCategories = subCategories.ToFrozenDictionary(row => row.Id);
        Objectives = Array.AsReadOnly(objectives.ToArray());
        Prerequisites = Array.AsReadOnly(prerequisites.ToArray());
        Records = records.ToFrozenDictionary(row => row.Id);
    }

    internal static ZoneAchievementContent Load(SqliteConnection connection)
    {
        var achievements = new List<AchievementIndexDefinition>();
        Read(connection, "SELECT id, achievement_sub_category_id, parent_achievement_id, season_off FROM achievements", reader =>
            achievements.Add(new(UInt32(reader, 0), UInt32(reader, 1), UInt32(reader, 2), Boolean(reader, 3))));
        var subCategories = new List<AchievementSubCategoryIndexDefinition>();
        Read(connection, "SELECT id, reward_achievement_id FROM achievement_sub_categories", reader =>
            subCategories.Add(new(UInt32(reader, 0), UInt32(reader, 1))));
        var objectives = new List<AchievementObjectiveIndexDefinition>();
        Read(connection, "SELECT id, achievement_id, record_id FROM achievement_objectives", reader =>
            objectives.Add(new(UInt32(reader, 0), UInt32(reader, 1), UInt32(reader, 2))));
        var prerequisites = new List<AchievementPrerequisiteDefinition>();
        Read(connection, "SELECT id, my_achievement_id, completed_achievement_id FROM pre_completed_achievements", reader =>
            prerequisites.Add(new(UInt32(reader, 0), UInt32(reader, 1), UInt32(reader, 2))));
        var records = new List<CharacterRecordDefinition>();
        Read(connection, "SELECT id, kind_id, value1, value2 FROM char_records", reader =>
            records.Add(new(UInt32(reader, 0), Int32(reader, 1), Int32(reader, 2), Int32(reader, 3))));
        return new(achievements, subCategories, objectives, prerequisites, records);
    }
}
