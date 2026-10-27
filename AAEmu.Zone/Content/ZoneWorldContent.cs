using System.Collections.Frozen;
using System.Text;
using AAEmu.Zone.Protocol.Messages;
using Microsoft.Data.Sqlite;
using static AAEmu.Zone.Content.NativeContentReader;

namespace AAEmu.Zone.Content;

public enum WorldContentKind
{
    Craft = 0, Zone = 1, TowerDef = 2, GameSchedule = 3, Transfer = 4, NpcSpawner = 5,
    QuestContext = 6, DoodadAlmighty = 7, Character = 8, LootPack = 9, AppellationMerit = 10,
    ScheduleItem = 11, ArchePass = 12, Instance = 13, Appellation = 14, Achievement = 15,
    TaggedBuff = 16, FestivalZone = 17
}

public sealed record WorldContentDefinition(uint Id, WorldContentKind Kind, uint ContentId, WorldContentName Name);

/// <summary>All world_contents descriptors from native loader 39C69C40; names use SQLite C-string semantics.</summary>
public sealed class ZoneWorldContent
{
    public IReadOnlyList<WorldContentDefinition> Entries { get; }
    public IReadOnlyDictionary<uint, int> ZoneKeys { get; }

    public ZoneWorldContent(IEnumerable<WorldContentDefinition> entries, IEnumerable<KeyValuePair<uint, int>>? zoneKeys = null)
    {
        var rows = entries.ToArray();
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            if (!Enum.IsDefined(row.Kind)) throw new InvalidDataException("Unknown World content kind.");
            var name = row.Name.Bytes;
            var end = name.IndexOf((byte)0);
            rows[i] = row with
            {
                ContentId = row.Kind == WorldContentKind.FestivalZone ? unchecked((ushort)row.ContentId) : row.ContentId,
                Name = new(end < 0 ? name : name[..end])
            };
        }
        Entries = Array.AsReadOnly(rows);
        ZoneKeys = (zoneKeys ?? []).ToFrozenDictionary();
    }

    internal static ZoneWorldContent Load(SqliteConnection connection)
    {
        var rows = new List<WorldContentDefinition>();
        // BLOB casts preserve the SQLite UTF-8 bytes, including invalid UTF-8, before C-string truncation.
        Read(connection, "SELECT id, CAST(content_type AS BLOB), content_id, CAST(name AS BLOB) FROM world_contents", reader =>
        {
            var type = Encoding.Latin1.GetString(TextBytes(reader, 1));
            var kind = type switch
            {
                "Craft" => WorldContentKind.Craft, "Zone" => WorldContentKind.Zone, "TowerDef" => WorldContentKind.TowerDef,
                "GameSchedule" => WorldContentKind.GameSchedule, "Transfer" => WorldContentKind.Transfer,
                "NpcSpawner" => WorldContentKind.NpcSpawner, "QuestContext" => WorldContentKind.QuestContext,
                "DoodadAlmighty" => WorldContentKind.DoodadAlmighty, "Character" => WorldContentKind.Character,
                "LootPack" => WorldContentKind.LootPack, "AppellationMerit" => WorldContentKind.AppellationMerit,
                "ScheduleItem" => WorldContentKind.ScheduleItem, "ArchePass" => WorldContentKind.ArchePass,
                "Instance" => WorldContentKind.Instance, "Appellation" => WorldContentKind.Appellation,
                "Achievement" => WorldContentKind.Achievement, "TaggedBuff" => WorldContentKind.TaggedBuff,
                "FestivalZone" => WorldContentKind.FestivalZone,
                _ => throw new InvalidDataException($"Unknown world_contents type '{type}' for row {UInt32(reader, 0)}.")
            };
            rows.Add(new(UInt32(reader, 0), kind, UInt32(reader, 2), new(TextBytes(reader, 3))));
        });
        var zoneKeys = new Dictionary<uint, int>();
        Read(connection, "SELECT id, zone_key FROM zones", reader => zoneKeys.Add(UInt32(reader, 0), Int32(reader, 1)));
        return new(rows, zoneKeys);
    }

    private static byte[] TextBytes(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return [];
        var bytes = reader.GetFieldValue<byte[]>(ordinal);
        var end = Array.IndexOf(bytes, (byte)0);
        return end < 0 ? bytes : bytes[..end];
    }
}
