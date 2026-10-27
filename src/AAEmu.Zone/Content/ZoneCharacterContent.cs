using System.Collections.Frozen;
using Microsoft.Data.Sqlite;
using static AAEmu.Zone.Content.NativeContentReader;

namespace AAEmu.Zone.Content;

/// <summary>39B03750 columns in native order (all integer ids except Creatable).</summary>
public sealed record CharacterDefinition(
    uint Id, int CharGenderId, int CharRaceId, bool Creatable, uint DefaultCustomId, uint DefaultFxVoiceSoundPackId,
    uint DefaultResurrectionDistrictId, uint DefaultReturnDistrictId, uint DefaultSystemVoiceSoundPackId,
    uint FaceItemId, uint FactionId, uint ModelId, uint PreviewClothPackId, uint StartingZoneId);

/// <summary>39B46610: character_default_skills(character_id, default_skill_id).</summary>
public sealed record CharacterDefaultSkillDefinition(uint CharacterId, uint DefaultSkillId);

/// <summary>39C66520: character_idle_buffs(id, buff_id).</summary>
public sealed record CharacterIdleBuffDefinition(uint Id, uint BuffId);

/// <summary>39B03540: character_supplies(id, ability_id, amount, grade_id, item_id).</summary>
public sealed record CharacterSupplyDefinition(uint Id, uint AbilityId, int Amount, uint GradeId, uint ItemId);

/// <summary>`characters` and the `character_*` tables the native loader reads with it, for player actor creation.</summary>
public sealed class ZoneCharacterContent
{
    private readonly FrozenDictionary<uint, uint[]> _skillsByCharacter;

    public IReadOnlyDictionary<uint, CharacterDefinition> Characters { get; }
    public IReadOnlyList<CharacterDefaultSkillDefinition> DefaultSkills { get; }
    public IReadOnlyList<CharacterIdleBuffDefinition> IdleBuffs { get; }
    public IReadOnlyList<CharacterSupplyDefinition> Supplies { get; }

    public ZoneCharacterContent(IEnumerable<CharacterDefinition> characters,
        IEnumerable<CharacterDefaultSkillDefinition> defaultSkills,
        IEnumerable<CharacterIdleBuffDefinition> idleBuffs, IEnumerable<CharacterSupplyDefinition> supplies)
    {
        Characters = ContentKeys.Unique("characters", characters, row => row.Id).ToDictionary(row => row.Id).ToFrozenDictionary();
        DefaultSkills = Array.AsReadOnly(defaultSkills.ToArray());
        IdleBuffs = Array.AsReadOnly(idleBuffs.ToArray());
        Supplies = Array.AsReadOnly(supplies.ToArray());
        _skillsByCharacter = DefaultSkills.GroupBy(row => row.CharacterId)
            .ToDictionary(group => group.Key, group => group.Select(row => row.DefaultSkillId).ToArray()).ToFrozenDictionary();
    }

    public IReadOnlyList<uint> DefaultSkillsFor(uint characterId) =>
        _skillsByCharacter.TryGetValue(characterId, out var skills) ? skills : [];

    internal static ZoneCharacterContent Load(SqliteConnection connection)
    {
        var characters = new List<CharacterDefinition>();
        Read(connection, """
            SELECT id, char_gender_id, char_race_id, creatable, default_custom_id, default_fx_voice_sound_pack_id,
                default_resurrection_district_id, default_return_district_id, default_system_voice_sound_pack_id,
                face_item_id, faction_id, model_id, preview_cloth_pack_id, starting_zone_id FROM characters ORDER BY id
            """, reader => characters.Add(new(UInt32(reader, 0), Int32(reader, 1), Int32(reader, 2), Boolean(reader, 3),
                UInt32(reader, 4), UInt32(reader, 5), UInt32(reader, 6), UInt32(reader, 7), UInt32(reader, 8),
                UInt32(reader, 9), UInt32(reader, 10), UInt32(reader, 11), UInt32(reader, 12), UInt32(reader, 13))));
        var skills = new List<CharacterDefaultSkillDefinition>();
        Read(connection, "SELECT character_id, default_skill_id FROM character_default_skills ORDER BY character_id, default_skill_id",
            reader => skills.Add(new(UInt32(reader, 0), UInt32(reader, 1))));
        var buffs = new List<CharacterIdleBuffDefinition>();
        Read(connection, "SELECT id, buff_id FROM character_idle_buffs ORDER BY id", reader => buffs.Add(new(UInt32(reader, 0), UInt32(reader, 1))));
        var supplies = new List<CharacterSupplyDefinition>();
        Read(connection, "SELECT id, ability_id, amount, grade_id, item_id FROM character_supplies ORDER BY id",
            reader => supplies.Add(new(UInt32(reader, 0), UInt32(reader, 1), Int32(reader, 2), UInt32(reader, 3), UInt32(reader, 4))));
        return new(characters, skills, buffs, supplies);
    }
}
