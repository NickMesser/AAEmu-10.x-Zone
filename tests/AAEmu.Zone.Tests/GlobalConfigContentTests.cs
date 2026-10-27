using Microsoft.Data.Sqlite;

using AAEmu.Zone.Content;

namespace AAEmu.Zone.Tests;

public sealed class GlobalConfigContentTests
{
    private const string Schema = """
        CREATE TABLE constants(id INTEGER PRIMARY KEY, value REAL);
        CREATE TABLE options(option_item_type_id INTEGER, is_cvar_group TEXT, option_item_save_level_type_id INTEGER, visible TEXT);
        CREATE TABLE tags(id INTEGER PRIMARY KEY, name TEXT);
        CREATE TABLE return_points(id INTEGER PRIMARY KEY, editor_name TEXT, name TEXT, use_additional TEXT);
        CREATE TABLE sub_zones(id INTEGER PRIMARY KEY, change_to_quest_sound_pack TEXT, h REAL, idx INTEGER, linked_zone_group_id INTEGER,
            name TEXT, parent_sub_zone_id INTEGER, quest_completed_id INTEGER, quest_sound_pack_id INTEGER, sound_id INTEGER,
            sound_pack_id INTEGER, w REAL, x REAL, y REAL);
        CREATE TABLE characters(id INTEGER PRIMARY KEY, char_gender_id INTEGER, char_race_id INTEGER, creatable TEXT, default_custom_id INTEGER,
            default_fx_voice_sound_pack_id INTEGER, default_resurrection_district_id INTEGER, default_return_district_id INTEGER,
            default_system_voice_sound_pack_id INTEGER, face_item_id INTEGER, faction_id INTEGER, model_id INTEGER,
            preview_cloth_pack_id INTEGER, starting_zone_id INTEGER);
        CREATE TABLE character_default_skills(character_id INTEGER, default_skill_id INTEGER);
        CREATE TABLE character_idle_buffs(id INTEGER PRIMARY KEY, buff_id INTEGER);
        CREATE TABLE character_supplies(id INTEGER PRIMARY KEY, ability_id INTEGER, amount INTEGER, grade_id INTEGER, item_id INTEGER);
        INSERT INTO constants VALUES (1, 2.5), (2, NULL);
        INSERT INTO options VALUES (7, 'f', 3, 't'), (8, 'T', NULL, NULL);
        INSERT INTO tags VALUES (1, 'alpha'), (2, NULL);
        INSERT INTO return_points VALUES (5, 'ed', 'Home', '1'), (6, NULL, NULL, 'f');
        INSERT INTO sub_zones VALUES (10, '1', 4.5, 2, 100, 'North', 0, 11, 12, 13, 14, 20.5, 1.5, 2.5),
            (11, 'f', 1, 0, 100, 'South', 10, 0, 0, 0, 0, 1, 2, 3), (12, 'f', 1, 0, 200, 'Elsewhere', 0, 0, 0, 0, 0, 1, 2, 3);
        INSERT INTO characters VALUES (1, 1, 2, 't', 3, 4, 5, 6, 7, 8, 9, 10, 11, 12);
        INSERT INTO character_default_skills VALUES (1, 30), (1, 31), (2, 32);
        INSERT INTO character_idle_buffs VALUES (1, 40);
        INSERT INTO character_supplies VALUES (1, 2, 3, 4, 5);
        """;

    private static ZoneContentDatabase Open(string root, string schema = Schema)
    {
        var path = Path.Combine(root, "game.compact");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = schema;
            command.ExecuteNonQuery();
        }
        return new ZoneContentDatabase(path);
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "zone-global-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Test]
    public async Task ConfigTablesParseWithNativeTypesAndAreLookedUpByKey()
    {
        var root = NewRoot();
        try
        {
            using var database = Open(root);
            var config = database.LoadConfigContent();
            await Assert.That(config.Constants.Count).IsEqualTo(2);
            await Assert.That(config.TryGet(1, out var value)).IsTrue();
            await Assert.That(value).IsEqualTo(2.5f);
            await Assert.That(config.Get(2, 9f)).IsEqualTo(0f); // NULL reads as 0, not the fallback
            await Assert.That(config.Get(99, 9f)).IsEqualTo(9f);
            await Assert.That(config.TryGet(99, out _)).IsFalse();
            await Assert.That(config.TryGetOption(7, out var seven)).IsTrue();
            await Assert.That(seven).IsEqualTo(new ZoneOptionDefinition(7, false, 3, true));
            await Assert.That(config.TryGetOption(8, out var eight)).IsTrue();
            await Assert.That(eight).IsEqualTo(new ZoneOptionDefinition(8, true, 0, false));
            await Assert.That(config.Tags[1].Name).IsEqualTo("alpha");
            await Assert.That(config.Tags[2].Name).IsEqualTo("");
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task ReturnPointsAndSubZonesAreFilteredByZoneGroup()
    {
        var root = NewRoot();
        try
        {
            using var database = Open(root);
            var locations = database.LoadLocationContent();
            await Assert.That(locations.ReturnPoints.Count).IsEqualTo(2);
            await Assert.That(locations.TryGetReturnPoint(5, out var home)).IsTrue();
            await Assert.That(home).IsEqualTo(new ReturnPointDefinition(5, "ed", "Home", true));
            await Assert.That(locations.TryGetReturnPoint(6, out var other)).IsTrue();
            await Assert.That(other.UseAdditional).IsFalse();
            await Assert.That(locations.TryGetReturnPoint(7, out _)).IsFalse();

            var zone = new ZoneContent(1, 456, "test_zone", 100, "group");
            var rows = locations.SubZonesForZone(zone);
            await Assert.That(rows.Select(row => row.Id).SequenceEqual(new uint[] { 10, 11 })).IsTrue();
            await Assert.That(rows[0]).IsEqualTo(new SubZoneDefinition(10, true, 4.5f, 2, 100, "North", 0, 11, 12, 13, 14, 20.5f, 1.5f, 2.5f));
            await Assert.That(locations.SubZonesForGroup(200).Count).IsEqualTo(1);
            await Assert.That(locations.SubZonesForGroup(300).Count).IsEqualTo(0);
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task CharacterTablesParseAndGroupDefaultSkills()
    {
        var root = NewRoot();
        try
        {
            using var database = Open(root);
            var characters = database.LoadCharacterContent();
            await Assert.That(characters.Characters[1]).IsEqualTo(new CharacterDefinition(1, 1, 2, true, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12));
            await Assert.That(characters.DefaultSkillsFor(1).SequenceEqual(new uint[] { 30, 31 })).IsTrue();
            await Assert.That(characters.DefaultSkillsFor(5).Count).IsEqualTo(0);
            await Assert.That(characters.IdleBuffs.Single()).IsEqualTo(new CharacterIdleBuffDefinition(1, 40));
            await Assert.That(characters.Supplies.Single()).IsEqualTo(new CharacterSupplyDefinition(1, 2, 3, 4, 5));
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task MissingTableOrColumnFailsWithItsName()
    {
        var root = NewRoot();
        try
        {
            using var noTable = Open(root, Schema.Replace("CREATE TABLE tags(id INTEGER PRIMARY KEY, name TEXT);", "")
                .Replace("INSERT INTO tags VALUES (1, 'alpha'), (2, NULL);", ""));
            var missingTable = await Assert.That(() => noTable.LoadConfigContent()).Throws<SqliteException>();
            await Assert.That(missingTable!.Message).Contains("tags");
        }
        finally { Directory.Delete(root, true); }

        root = NewRoot();
        try
        {
            using var noColumn = Open(root, Schema.Replace("starting_zone_id INTEGER);", "x INTEGER);")
                .Replace("INSERT INTO characters VALUES (1, 1, 2, 't', 3, 4, 5, 6, 7, 8, 9, 10, 11, 12);", ""));
            var missingColumn = await Assert.That(() => noColumn.LoadCharacterContent()).Throws<SqliteException>();
            await Assert.That(missingColumn!.Message).Contains("starting_zone_id");
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task DuplicateKeysAreRejectedWithTheTableName()
    {
        var root = NewRoot();
        try
        {
            using var database = Open(root, Schema.Replace("CREATE TABLE options(", "CREATE TABLE options(")
                + "INSERT INTO options VALUES (7, 'f', 1, 't');");
            var error = await Assert.That(() => database.LoadConfigContent()).Throws<InvalidDataException>();
            await Assert.That(error!.Message).Contains("options");
        }
        finally { Directory.Delete(root, true); }
    }
}
