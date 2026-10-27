using System.Collections.Frozen;
using AAEmu.Zone.Content;

namespace AAEmu.Zone.Simulation;

/// <summary>Immutable connection-specific achievement dependencies. This does not evaluate completion or award rewards.</summary>
public sealed class ZoneAchievementIndex
{
    public ZoneAchievementContent Source { get; }
    public IReadOnlyDictionary<uint, AchievementIndexDefinition> Achievements { get; }
    private readonly FrozenDictionary<uint, IReadOnlyList<uint>> _children;
    private readonly FrozenDictionary<uint, IReadOnlyList<uint>> _objectives;
    private readonly FrozenDictionary<uint, IReadOnlyList<uint>> _prerequisites;
    private readonly FrozenDictionary<uint, IReadOnlyList<uint>> _dependents;
    private readonly FrozenDictionary<uint, IReadOnlyList<uint>> _subCategoryMembers;
    private readonly FrozenDictionary<uint, IReadOnlyList<uint>> _recordAchievements;

    public ZoneAchievementIndex(ZoneAchievementContent source, IReadOnlySet<uint>? excludedIds = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source;
        var active = source.Achievements.Where(row => excludedIds?.Contains(row.Id) != true).ToArray();
        Achievements = active.ToFrozenDictionary(row => row.Id);
        // 0x340: a parent is an index key, even if its descriptor is absent or excluded.
        _children = Index(active.Where(row => row.ParentId != 0).Select(row => (row.ParentId, row.Id)));
        // 0x3C0: season_off only affects subcategory membership; the achievement remains available elsewhere.
        _subCategoryMembers = Index(active.Where(row => !row.SeasonOff &&
            source.SubCategories.TryGetValue(row.SubCategoryId, out var category) && category.RewardAchievementId != row.Id)
            .Select(row => (row.SubCategoryId, row.Id)));

        var objectives = source.Objectives.Where(row => Achievements.ContainsKey(row.AchievementId) &&
            source.Records.TryGetValue(row.RecordId, out var record) &&
            (record.Kind != 9 || Achievements.ContainsKey(unchecked((uint)record.Value1)))).ToArray();
        _objectives = Index(objectives.Select(row => (row.AchievementId, row.Id))); // 0x360
        _recordAchievements = Index(objectives.Select(row => (row.RecordId, row.AchievementId))); // 0x3E0

        var prerequisites = source.Prerequisites.Where(row => Achievements.ContainsKey(row.AchievementId) &&
            Achievements.ContainsKey(row.CompletedAchievementId)).ToArray();
        _prerequisites = Index(prerequisites.Select(row => (row.AchievementId, row.CompletedAchievementId))); // 0x380
        _dependents = Index(prerequisites.Select(row => (row.CompletedAchievementId, row.AchievementId))); // 0x3A0
    }

    public IReadOnlyList<uint> ChildrenOf(uint parentId) => _children.GetValueOrDefault(parentId) ?? [];
    public IReadOnlyList<uint> ObjectivesOf(uint achievementId) => _objectives.GetValueOrDefault(achievementId) ?? [];
    public IReadOnlyList<uint> PrerequisitesOf(uint achievementId) => _prerequisites.GetValueOrDefault(achievementId) ?? [];
    public IReadOnlyList<uint> DependentsOf(uint achievementId) => _dependents.GetValueOrDefault(achievementId) ?? [];
    public IReadOnlyList<uint> MembersOfSubCategory(uint categoryId) => _subCategoryMembers.GetValueOrDefault(categoryId) ?? [];
    public IReadOnlyList<uint> AchievementsForRecord(uint recordId) => _recordAchievements.GetValueOrDefault(recordId) ?? [];

    private static FrozenDictionary<uint, IReadOnlyList<uint>> Index(IEnumerable<(uint Key, uint Value)> rows) =>
        rows.GroupBy(row => row.Key).ToFrozenDictionary(group => group.Key,
            group => (IReadOnlyList<uint>)Array.AsReadOnly(group.Select(row => row.Value).ToArray()));
}
