using AAEmu.Zone.Content;

namespace AAEmu.Zone.Hosting;

/// <summary>
/// The content set a zone host loads once from the decrypted game database before it connects
/// to World. Stats and models load first because the composed-engine checks need nothing else.
/// </summary>
public sealed record ZoneHostContent(
    ZoneStatContent Stats,
    ZoneModelContent Models,
    ZoneNpcContent Npcs,
    ZoneNpcSpawnerControllerContent SpawnerControllers,
    ZoneFactionContent Factions,
    ZoneSceneContent Scene,
    ZoneWorldContent World,
    ZoneAchievementContent Achievements,
    ZoneLootContent Loot,
    ZoneConfigContent Config,
    ZoneLocationContent Locations,
    ZoneCharacterContent Characters)
{
    public static ZoneHostContent Load(ZoneContentDatabase content, ZoneStatContent stats, ZoneModelContent models)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new(stats, models,
            content.LoadNpcContent(models, loadSpawnerContent: true),
            content.LoadNpcSpawnerControllerContent(),
            content.LoadFactionContent(),
            content.LoadSceneContent(),
            content.LoadWorldContent(),
            content.LoadAchievementContent(),
            content.LoadLootContent(),
            content.LoadConfigContent(),
            content.LoadLocationContent(),
            content.LoadCharacterContent());
    }

    /// <summary>One line per content family, printed by the server and the content check alike.</summary>
    public void Print(ZoneContent zone, bool battlefield)
    {
        var npcs = Npcs;
        var controllers = SpawnerControllers;
        Console.WriteLine($"Content verified: {zone.Name}, key {zone.Key}, group {zone.GroupId} ({zone.GroupName}).");
        Console.WriteLine($"Battlefield Zone key: {battlefield}.");
        Console.WriteLine($"Stat content loaded: {Stats.Formulas.Count} unit formulas, {Stats.Npcs.Count} NPC templates, {Stats.Houses.Count} housing templates.");
        Console.WriteLine($"Buff content loaded: {Stats.Buffs.Templates.Count} templates, {Stats.Buffs.Passives.Count} passive definitions.");
        Console.WriteLine($"Skill content loaded: {Stats.Skills.Skills.Count} definitions.");
        Console.WriteLine($"Model content loaded: {Models.Models.Count} scalar definitions, {Models.Subtypes.Count} subtype identities.");
        Console.WriteLine($"Actor model content loaded: {Models.ActorModels.Rows.Count} actor rows, {Models.ActorModels.StanceRows.Count} stance rows.");
        Console.WriteLine($"NPC content loaded: {npcs.Templates.Count} templates, {npcs.Groups.Count} groups, {npcs.AggroLinks.Count} aggro links, {npcs.AiParameters.Count} AI parameter sets.");
        Console.WriteLine($"NPC posture content loaded: {npcs.Postures.Rows.Count} rows, {npcs.Postures.Sets.Count} sets.");
        Console.WriteLine($"NPC spawner content loaded: {npcs.Spawners.Spawners.Count} definitions, {npcs.Spawners.MemberRows.Count} member rows.");
        Console.WriteLine($"NPC spawner controllers loaded: {controllers.Towers.Count} towers, {controllers.SourcePhases.Count} phases, {controllers.SourceTargets.Count} targets, {controllers.Schedules.Count} schedules, {controllers.SourceScheduleLinks.Count} schedule links.");
        Console.WriteLine($"Unresolved spawner controller relations: {controllers.UnresolvedPhaseParentCount} phase parents, {controllers.UnresolvedTargetParentCount} target parents, {controllers.UnresolvedScheduleParentCount} schedule parents, {controllers.UnresolvedTargetCount} typed targets.");
        Console.WriteLine($"Faction content loaded: {Factions.Factions.Count} definitions, {Factions.Relations.Count} static relations.");
        Console.WriteLine($"Scene content loaded: {Scene.Areas.Count} areas, {Scene.Conflicts.Count} conflict policies, {Scene.PvpPolicies.Count} PVP policies, {Scene.Festivals.Count} festivals.");
        Console.WriteLine($"World content loaded: {World.Entries.Count} filter descriptors.");
        Console.WriteLine($"Achievement dependencies loaded: {Achievements.Achievements.Count} achievements, {Achievements.Objectives.Count} objectives, {Achievements.Prerequisites.Count} prerequisites.");
        Console.WriteLine($"Loot content loaded: {Loot.Packs.Count} packs, {Loot.NpcPackIds.Count} NPC mappings, {Loot.Packs.Values.Count(pack => pack.Metadata is null)} packs without declared metadata.");
        Console.WriteLine($"Config content loaded: {Config.Constants.Count} constants, {Config.Options.Count} options, {Config.Tags.Count} tags.");
        Console.WriteLine($"Location content loaded: {Locations.ReturnPoints.Count} return points, {Locations.SubZones.Count} sub-zones ({Locations.SubZonesForZone(zone).Count} in this zone group).");
        Console.WriteLine($"Character content loaded: {Characters.Characters.Count} characters, {Characters.DefaultSkills.Count} default skills, {Characters.IdleBuffs.Count} idle buffs, {Characters.Supplies.Count} supplies.");
    }

    public void PrintPlacements(ZoneNpcSpawnerPlacementContent placements)
    {
        var resolved = placements.Rows.Count(row => Npcs.Spawners.Spawners.ContainsKey(row.SpawnerType));
        Console.WriteLine($"Authored NPC spawners loaded: {placements.Rows.Count} placements, {placements.RejectedRows.Count} rejected rows, {resolved} placements with resolved content, {placements.Rows.Sum(row => row.Placement.Points.Count)} points, {placements.Rows.Sum(row => row.Placement.Triangles.Count)} triangles.");
    }
}
