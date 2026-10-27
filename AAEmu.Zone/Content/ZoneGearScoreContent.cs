using System.Collections.Frozen;
using Microsoft.Data.Sqlite;

namespace AAEmu.Zone.Content;

/// <summary>Gear-score inputs from general formulas and item metadata, separate from unit formulas.</summary>
public sealed class ZoneGearScoreContent
{
    public IReadOnlyDictionary<uint, ContentFormula> Formulas { get; }
    public IReadOnlyDictionary<byte, float> Grades { get; }
    public IReadOnlyDictionary<uint, int> Holdables { get; }
    public IReadOnlyDictionary<uint, int> WearableSlots { get; }
    public IReadOnlyDictionary<ushort, int> TemperingScales { get; }

    public ZoneGearScoreContent(IEnumerable<KeyValuePair<uint, ContentFormula>> formulas,
        IEnumerable<KeyValuePair<byte, float>> grades, IEnumerable<KeyValuePair<uint, int>> holdables,
        IEnumerable<KeyValuePair<uint, int>> wearableSlots, IEnumerable<KeyValuePair<ushort, int>> temperingScales)
    {
        Formulas = formulas.ToDictionary().ToFrozenDictionary();
        Grades = grades.ToDictionary().ToFrozenDictionary();
        Holdables = holdables.ToDictionary().ToFrozenDictionary();
        WearableSlots = wearableSlots.ToDictionary().ToFrozenDictionary();
        TemperingScales = temperingScales.ToDictionary().ToFrozenDictionary();
        if (Grades.Values.Any(value => !float.IsFinite(value)))
            throw new InvalidDataException("Non-finite gear-score grade multiplier.");
    }

    public ContentFormula Formula(uint kind) => Formulas.TryGetValue(kind, out var formula) ? formula :
        throw new InvalidDataException($"Missing gear-score formula {kind}.");

    internal static ZoneGearScoreContent Load(SqliteConnection connection)
    {
        var formulas = new Dictionary<uint, ContentFormula>();
        Read("SELECT id, formula FROM formulas WHERE id IN (30,31,32,56,57,69)", r =>
            formulas.Add(checked((uint)r.GetInt64(0)), new(r.GetString(1))));
        var grades = new Dictionary<byte, float>();
        // 39ADE330 descriptor+0x30 -> 39B1E510 grade vector+4 -> 39B1C480 item_grade.
        Read("SELECT id, var_holdable_dps FROM item_grades ORDER BY id", r =>
            grades.Add(checked((byte)r.GetInt32(0)), r.GetFloat(1)));
        var holdables = new Dictionary<uint, int>();
        Read("SELECT id, gear_score_multiplier FROM holdables", r => holdables.Add(checked((uint)r.GetInt64(0)), r.GetInt32(1)));
        var slots = new Dictionary<uint, int>();
        Read("SELECT id, gear_score_multiplier FROM wearable_slots", r => slots.Add(checked((uint)r.GetInt64(0)), r.GetInt32(1)));
        var scales = new Dictionary<ushort, int>();
        Read("SELECT id, scale FROM enchant_scale_ratios", r => scales.Add(checked((ushort)r.GetInt32(0)), r.GetInt32(1)));
        return new(formulas, grades, holdables, slots, scales);

        void Read(string sql, Action<SqliteDataReader> consume)
        {
            using var command = connection.CreateCommand(); command.CommandText = sql;
            using var reader = command.ExecuteReader(); while (reader.Read()) consume(reader);
        }
    }
}
