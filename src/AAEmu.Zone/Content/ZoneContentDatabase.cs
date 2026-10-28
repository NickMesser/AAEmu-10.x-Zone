using Microsoft.Data.Sqlite;

namespace AAEmu.Zone.Content;

public sealed record ZoneContent(uint Id, int Key, string Name, uint GroupId, string GroupName);

/// <summary>The configured decrypted game.compact export is opened read-only, without pooling.</summary>
public sealed class ZoneContentDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public ZoneContentDatabase(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.GetFileName(path).Equals("compact.sqlite", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use the configured decrypted game.compact database, not compact.sqlite.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The configured decrypted game-content database does not exist.", fullPath);

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        try
        {
            _connection.Open();
            using var command = _connection.CreateCommand();
            command.CommandText = "PRAGMA query_only = ON";
            command.ExecuteNonQuery();
        }
        catch
        {
            _connection.Dispose();
            throw;
        }
    }

    public ZoneContent ResolveZone(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT z.id, z.zone_key, z.name, z.group_id, g.name
            FROM zones z LEFT JOIN zone_groups g ON g.id = z.group_id
            WHERE z.name = $name COLLATE NOCASE
            """;
        command.Parameters.AddWithValue("$name", name);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidDataException($"Zone '{name}' was not found in the configured game-content database.");
        if (reader.IsDBNull(3) || reader.IsDBNull(4))
            throw new InvalidDataException($"Zone '{name}' has no matching zone_groups row.");
        var zone = new ZoneContent(checked((uint)reader.GetInt64(0)), reader.GetInt32(1), reader.GetString(2),
            checked((uint)reader.GetInt64(3)), reader.GetString(4));
        if (zone.Key <= 0 || reader.Read())
            throw new InvalidDataException($"Zone '{name}' has an invalid key or ambiguous content rows.");

        return zone;
    }

    public void Dispose() => _connection.Dispose();

    public ZoneStatContent LoadStatContent() => ZoneStatContent.Load(_connection);

    public ZoneModelContent LoadModelContent() => ZoneModelContent.Load(_connection);

    public ZoneModelAuxiliaryContent LoadModelAuxiliaryContent() => ZoneModelAuxiliaryContent.Load(_connection);

    public ZoneActorModelContent LoadActorModelContent() => ZoneActorModelContent.Load(_connection);

    public ZoneNpcPostureContent LoadNpcPostureContent() => ZoneNpcPostureContent.Load(_connection);

    public ZoneNpcSkillContent LoadNpcSkillContent() => ZoneNpcSkillContent.Load(_connection);

    public ZoneNpcContent LoadNpcContent(ZoneModelContent? models = null, bool loadSpawnerContent = true) =>
        ZoneNpcContent.Load(_connection, models ?? ZoneModelContent.Load(_connection), loadSpawnerContent);

    public ZoneNpcSpawnerContent LoadNpcSpawnerContent(ZoneNpcContent npcContent, bool loadSpawnerContent)
    {
        ArgumentNullException.ThrowIfNull(npcContent);
        return ZoneNpcSpawnerContent.Load(_connection, npcContent.Templates, npcContent.Groups, loadSpawnerContent);
    }

    public ZoneFactionContent LoadFactionContent() => ZoneFactionContent.Load(_connection);

    public ZoneSceneContent LoadSceneContent() => ZoneSceneContent.Load(_connection);

    /// <summary>constants, options and tags (39C80E80, 39D1A050, 39BFF050).</summary>
    public ZoneConfigContent LoadConfigContent() => ZoneConfigContent.Load(_connection);

    /// <summary>return_points and sub_zones (39B07660, 39CF5990).</summary>
    public ZoneLocationContent LoadLocationContent() => ZoneLocationContent.Load(_connection);

    /// <summary>characters plus character_default_skills/idle_buffs/supplies (39B03750 and companions).</summary>
    public ZoneCharacterContent LoadCharacterContent() => ZoneCharacterContent.Load(_connection);

    public ZoneWorldContent LoadWorldContent() => ZoneWorldContent.Load(_connection);

    public ZoneAchievementContent LoadAchievementContent() => ZoneAchievementContent.Load(_connection);

    public ZoneLootContent LoadLootContent() => ZoneLootContent.Load(_connection);

    public ZoneNpcSpawnerControllerContent LoadNpcSpawnerControllerContent() => ZoneNpcSpawnerControllerContent.Load(_connection);

    /// <summary>39BFB630 matches battle_fields.zone_key, not a unit's world ID or the battlefield row ID.</summary>
    public bool IsBattlefield(int zoneKey)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT zone_key FROM battle_fields";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            if ((reader.IsDBNull(0) ? 0 : unchecked((int)reader.GetInt64(0))) == zoneKey) return true;
        return false;
    }
}
