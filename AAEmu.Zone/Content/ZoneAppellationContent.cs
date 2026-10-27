using System.Collections.Frozen;
using Microsoft.Data.Sqlite;

namespace AAEmu.Zone.Content;

public sealed record AppellationStampDefinition(uint Id, int RequiredAppellationLevel);

/// <summary>Enabled appellation merits selected by the snapshot's fixed stamp field.</summary>
public sealed class ZoneAppellationContent(IEnumerable<AppellationStampDefinition> stamps)
{
    public IReadOnlyDictionary<uint, AppellationStampDefinition> Stamps { get; } =
        stamps.ToDictionary(stamp => stamp.Id).ToFrozenDictionary();

    internal static ZoneAppellationContent Load(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        // 39C8B020 loads merit descriptors; 39C56B80 exposes only enabled descriptors.
        command.CommandText = """
            SELECT id, req_app_level FROM appellation_merits
            WHERE substr(CAST(enable AS TEXT), 1, 1) IN ('t', 'T', '1') ORDER BY id
            """;
        using var reader = command.ExecuteReader();
        var stamps = new List<AppellationStampDefinition>();
        while (reader.Read())
            stamps.Add(new(checked((uint)reader.GetInt64(0)), reader.GetInt32(1)));
        return new(stamps);
    }
}
