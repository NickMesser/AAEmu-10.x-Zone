using System.Collections.Frozen;
using Microsoft.Data.Sqlite;

namespace AAEmu.Zone.Content;

public sealed record EquipmentCombatGrade(byte Id, float HoldableArmor, float HoldableDamage,
    float HoldableMagicDamage, float HoldableHealing, float HoldableMagicResistance,
    float WearableArmor, float WearableMagicResistance);
public sealed record HoldableCombatFormulas(uint Id, ContentFormula Damage, ContentFormula MagicDamage,
    ContentFormula Armor, ContentFormula Healing, ContentFormula MagicResistance,
    int Speed = 0, int DamageScale = 0, uint SlotType = 0);
public sealed record WearableDefense(uint ArmorType, uint SlotType, int Armor, int MagicResistance);

/// <summary>Native item formulas, their seven grade coefficients, and the wearable defense matrices.</summary>
public sealed class ZoneEquipmentCombatContent
{
    public IReadOnlyDictionary<byte, EquipmentCombatGrade> Grades { get; }
    public IReadOnlyDictionary<uint, HoldableCombatFormulas> Holdables { get; }
    public IReadOnlyDictionary<uint, ContentFormula> WearableFormulas { get; }
    public IReadOnlyDictionary<(uint ArmorType, uint SlotType), WearableDefense> Wearables { get; }

    public ZoneEquipmentCombatContent(IEnumerable<EquipmentCombatGrade> grades, IEnumerable<HoldableCombatFormulas> holdables,
        IEnumerable<KeyValuePair<uint, ContentFormula>> wearableFormulas, IEnumerable<WearableDefense> wearables)
    {
        Grades = grades.ToDictionary(value => value.Id).ToFrozenDictionary();
        Holdables = holdables.ToDictionary(value => value.Id).ToFrozenDictionary();
        WearableFormulas = wearableFormulas.ToDictionary().ToFrozenDictionary();
        Wearables = wearables.ToDictionary(value => (value.ArmorType, value.SlotType)).ToFrozenDictionary();
        if (Grades.Values.Any(grade => new[] { grade.HoldableArmor, grade.HoldableDamage, grade.HoldableMagicDamage,
                grade.HoldableHealing, grade.HoldableMagicResistance, grade.WearableArmor, grade.WearableMagicResistance }
            .Any(value => !float.IsFinite(value))))
            throw new InvalidDataException("Non-finite equipment combat grade multiplier.");
        if (Wearables.Keys.Any(key => key.ArmorType > 5 || key.SlotType > 33))
            throw new InvalidDataException("Wearable defense key exceeds the native 6 by 34 matrix.");
    }

    internal static ZoneEquipmentCombatContent Load(SqliteConnection connection)
    {
        var grades = new List<EquipmentCombatGrade>();
        // 39B1E510 builds seven f32 coefficients per grade; 39B1B8D0/39B1E240 select by formula kind.
        Read("""
            SELECT id, var_holdable_armor, var_holdable_dps, var_holdable_magic_dps,
                var_holdable_heal_dps, var_holdable_magic_resist, var_wearable_armor, var_wearable_magic_resistance
            FROM item_grades ORDER BY id
            """, r => grades.Add(new(checked((byte)r.GetInt32(0)), r.GetFloat(1), r.GetFloat(2), r.GetFloat(3),
                r.GetFloat(4), r.GetFloat(5), r.GetFloat(6), r.GetFloat(7))));
        var holdables = new List<HoldableCombatFormulas>();
        Read("SELECT id, formula_dps, formula_mdps, formula_armor, formula_hdps, formula_magic_resist, speed, damage_scale, slot_type_id FROM holdables",
            r => holdables.Add(new(checked((uint)r.GetInt64(0)), new(r.GetString(1)), new(r.GetString(2)),
                new(r.GetString(3)), new(r.GetString(4)), new(r.GetString(5)), r.GetInt32(6), r.GetInt32(7), checked((uint)r.GetInt64(8)))));
        var formulas = new Dictionary<uint, ContentFormula>();
        Read("SELECT kind_id, formula FROM wearable_formulas", r => formulas.Add(checked((uint)r.GetInt64(0)), new(r.GetString(1))));
        var wearables = new List<WearableDefense>();
        // 39B2D950/39B2DAC0 load basis points from wearables, independently of durability/coverage.
        Read("SELECT armor_type_id, slot_type_id, armor_bp, magic_resistance_bp FROM wearables",
            r => wearables.Add(new(checked((uint)r.GetInt64(0)), checked((uint)r.GetInt64(1)), r.GetInt32(2), r.GetInt32(3))));
        return new(grades, holdables, formulas, wearables);

        void Read(string sql, Action<SqliteDataReader> consume)
        {
            using var command = connection.CreateCommand(); command.CommandText = sql;
            using var reader = command.ExecuteReader(); while (reader.Read()) consume(reader);
        }
    }
}
