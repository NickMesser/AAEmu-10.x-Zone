using System.Collections.Frozen;
using Microsoft.Data.Sqlite;
using static AAEmu.Zone.Content.NativeContentReader;

namespace AAEmu.Zone.Content;

/// <summary>Read-only lookup of the numeric `constants` table by constant id.</summary>
public interface IZoneConstantProvider
{
    bool TryGet(uint id, out float value);
    float Get(uint id, float fallback);
}

/// <summary>Read-only lookup of `options` rows by option_item_type_id.</summary>
public interface IZoneOptionProvider
{
    bool TryGetOption(uint optionItemTypeId, out ZoneOptionDefinition option);
}

/// <summary>39D1A050 columns in native order; the two flags use the native 'T'/'t'/'1' text test.</summary>
public sealed record ZoneOptionDefinition(uint OptionItemTypeId, bool IsCvarGroup, int OptionItemSaveLevelTypeId, bool Visible);

/// <summary>39BFF050/39C05DF0 keep the raw id and name; native localisation of `name` is a separate overlay.</summary>
public sealed record ZoneTagDefinition(uint Id, string Name);

/// <summary>
/// Global configuration tables: `constants` (39C80E80), `options` (39D1A050) and `tags` (39BFF050).
/// Constants and options are exposed as key lookups so movement/stat code can consume them later.
/// </summary>
public sealed class ZoneConfigContent : IZoneConstantProvider, IZoneOptionProvider
{
    private readonly FrozenDictionary<uint, float> _constants;
    private readonly FrozenDictionary<uint, ZoneOptionDefinition> _options;

    public IReadOnlyDictionary<uint, float> Constants => _constants;
    public IReadOnlyDictionary<uint, ZoneOptionDefinition> Options => _options;
    public IReadOnlyDictionary<uint, ZoneTagDefinition> Tags { get; }

    public ZoneConfigContent(IEnumerable<KeyValuePair<uint, float>> constants,
        IEnumerable<ZoneOptionDefinition> options, IEnumerable<ZoneTagDefinition> tags)
    {
        _constants = ContentKeys.Unique("constants", constants, row => row.Key).ToDictionary(row => row.Key, row => row.Value).ToFrozenDictionary();
        _options = ContentKeys.Unique("options", options, row => row.OptionItemTypeId).ToDictionary(row => row.OptionItemTypeId).ToFrozenDictionary();
        Tags = ContentKeys.Unique("tags", tags, row => row.Id).ToDictionary(row => row.Id).ToFrozenDictionary();
    }

    public bool TryGet(uint id, out float value) => _constants.TryGetValue(id, out value);
    public float Get(uint id, float fallback) => _constants.TryGetValue(id, out var value) ? value : fallback;
    public bool TryGetOption(uint optionItemTypeId, out ZoneOptionDefinition option) => _options.TryGetValue(optionItemTypeId, out option!);

    internal static ZoneConfigContent Load(SqliteConnection connection)
    {
        var constants = new List<KeyValuePair<uint, float>>();
        // 39C80E80 reads value with sqlite3_column_double and narrows it to float.
        Read(connection, "SELECT id, value FROM constants ORDER BY id",
            reader => constants.Add(new(UInt32(reader, 0), Single(reader, 1))));
        var options = new List<ZoneOptionDefinition>();
        Read(connection, "SELECT option_item_type_id, is_cvar_group, option_item_save_level_type_id, visible FROM options ORDER BY option_item_type_id",
            reader => options.Add(new(UInt32(reader, 0), Boolean(reader, 1), Int32(reader, 2), Boolean(reader, 3))));
        var tags = new List<ZoneTagDefinition>();
        Read(connection, "SELECT id, name FROM tags ORDER BY id", reader => tags.Add(new(UInt32(reader, 0), String(reader, 1))));
        return new(constants, options, tags);
    }
}

internal static class ContentKeys
{
    /// <summary>Rejects duplicate primary keys with the table name instead of an anonymous dictionary error.</summary>
    internal static List<T> Unique<T, TKey>(string table, IEnumerable<T> rows, Func<T, TKey> key) where TKey : notnull
    {
        var seen = new HashSet<TKey>();
        var list = new List<T>();
        foreach (var row in rows)
        {
            if (!seen.Add(key(row)))
                throw new InvalidDataException($"Table '{table}' contains duplicate key {key(row)}.");
            list.Add(row);
        }
        return list;
    }
}
