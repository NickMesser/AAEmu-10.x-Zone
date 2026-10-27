using System.Collections.Frozen;
using Microsoft.Data.Sqlite;

namespace AAEmu.Zone.Content;

public sealed record ItemStatConfiguration(float Durability, float HoldableDurability, float WearableDurability,
    uint ItemStat, uint HoldableStat, uint WearableStat, uint StatValue,
    bool RandomAttributesEnabled, bool ReinforcementUsesFormula);
public sealed record ItemStatDefinition(uint Id, byte Implementation, short Level, uint ModifierSet = 0,
    uint SlotType = 0, uint HoldableId = 0, uint WearableType = 0, int DurabilityMultiplier = 100,
    uint RandomCategory = 0, bool HasEquipmentDefinition = false,
    int MinimumLevel = 0, int MaximumLevel = 0, uint SlaveEquipmentKind = 0);
public sealed record ItemGradeDefinition(byte Id, int StatMultiplier, float Durability);
public sealed record HoldableDefinition(uint Id, uint SlotType, int StatMultiplier, float DurabilityRatio);
public sealed record WearableClassification(uint ItemId, uint SlotType, uint ArmorType);
public sealed record WearableSlotDefinition(uint Id, uint SlotType, int Coverage);
public sealed record ItemAttributeWeights(uint Id, int Strength, int Dexterity, int Stamina, int Intelligence, int Spirit)
{
    public int Count => (Strength != 0 ? 1 : 0) + (Dexterity != 0 ? 1 : 0) + (Stamina != 0 ? 1 : 0) +
        (Intelligence != 0 ? 1 : 0) + (Spirit != 0 ? 1 : 0);
    public byte Total => unchecked((byte)(Strength + Dexterity + Stamina + Intelligence + Spirit));
    public byte Weight(uint attribute) => unchecked((byte)(attribute switch
        { 0 => Strength, 1 => Dexterity, 2 => Stamina, 3 => Intelligence, 4 => Spirit, _ => 0 }));
}
public sealed record RandomAttributeProperty(uint Category, byte Grade, int GradeExperience, int MaximumModifiers);
public sealed record RandomAttributeModifier(uint Group, byte Grade, uint Attribute, byte Type, int Minimum, int Maximum);

/// <summary>Native item stat inputs copied from the configured read-only content database.</summary>
public sealed class ZoneEquipmentContent
{
    public ItemStatConfiguration Configuration { get; }
    public IReadOnlyDictionary<uint, ItemStatDefinition> Items { get; }
    public IReadOnlyDictionary<byte, ItemGradeDefinition> Grades { get; }
    public IReadOnlyDictionary<uint, HoldableDefinition> Holdables { get; }
    public IReadOnlyDictionary<uint, uint> WeaponHoldables { get; }
    public IReadOnlyDictionary<uint, WearableClassification> WearableClassifications { get; }
    public IReadOnlySet<uint> ClassificationSlots { get; }
    public uint FullArmorCoverage { get; }
    public IReadOnlyDictionary<uint, ItemAttributeWeights> Weights { get; }
    public IReadOnlyDictionary<uint, int> Coverage { get; }
    public IReadOnlyDictionary<uint, float> WearableDurability { get; }
    public IReadOnlyDictionary<uint, (int Main, int Auxiliary)> Slots { get; }
    public IReadOnlyDictionary<(uint Category, byte Grade), RandomAttributeProperty> RandomProperties { get; }
    public IReadOnlyDictionary<(uint Group, byte Grade), RandomAttributeModifier> RandomModifiers { get; }
    public IReadOnlySet<(uint Slave, int Slot, uint Kind)> SlaveSlots { get; }

    public ZoneEquipmentContent(ItemStatConfiguration configuration, IEnumerable<ItemStatDefinition> items,
        IEnumerable<ItemGradeDefinition> grades, IEnumerable<HoldableDefinition> holdables,
        IEnumerable<ItemAttributeWeights> weights, IEnumerable<KeyValuePair<uint, int>> coverage,
        IEnumerable<KeyValuePair<uint, float>> wearableDurability,
        IEnumerable<KeyValuePair<uint, (int Main, int Auxiliary)>> slots,
        IEnumerable<RandomAttributeProperty>? randomProperties = null, IEnumerable<RandomAttributeModifier>? randomModifiers = null,
        IEnumerable<(uint Slave, int Slot, uint Kind)>? slaveSlots = null,
        IEnumerable<KeyValuePair<uint, uint>>? weaponHoldables = null,
        IEnumerable<WearableClassification>? wearableClassifications = null,
        IEnumerable<WearableSlotDefinition>? wearableSlots = null)
    {
        if (configuration.StatValue == 0 || !float.IsFinite(configuration.Durability) ||
            !float.IsFinite(configuration.HoldableDurability) || !float.IsFinite(configuration.WearableDurability))
            throw new InvalidDataException("Invalid item stat configuration.");
        Configuration = configuration;
        Items = items.ToDictionary(v => v.Id).ToFrozenDictionary();
        Grades = grades.ToDictionary(v => v.Id).ToFrozenDictionary();
        Holdables = holdables.ToDictionary(v => v.Id).ToFrozenDictionary();
        WeaponHoldables = (weaponHoldables ?? Items.Values.Where(value => value.Implementation == 1 && value.HasEquipmentDefinition)
            .Select(value => new KeyValuePair<uint, uint>(value.Id, value.HoldableId))).ToDictionary().ToFrozenDictionary();
        Weights = weights.ToDictionary(v => v.Id).ToFrozenDictionary();
        Coverage = coverage.ToDictionary().ToFrozenDictionary();
        WearableClassifications = (wearableClassifications ?? Items.Values
            .Where(value => value.HasEquipmentDefinition && value.Implementation is 2 or 24 or 30 or 34)
            .Select(value => new WearableClassification(value.Id, value.SlotType, value.WearableType)))
            .ToDictionary(value => value.ItemId).ToFrozenDictionary();
        if (WearableClassifications.Values.Any(value => value.ArmorType > 5))
            throw new InvalidDataException("Wearable classification exceeds the six native armor types.");
        var classificationSlots = (wearableSlots ?? Coverage.Select(value => new WearableSlotDefinition(value.Key, value.Key, value.Value))).ToArray();
        ClassificationSlots = classificationSlots.Select(value => value.SlotType).ToFrozenSet();
        // 39B2DCE0 sums rows by slot_type_id, with native i32 wrapping. The getter halves this as u32.
        FullArmorCoverage = classificationSlots.Where(value => value.SlotType is 1 or >= 3 and <= 8)
            .Aggregate(0u, (sum, value) => unchecked(sum + (uint)value.Coverage));
        WearableDurability = wearableDurability.ToDictionary().ToFrozenDictionary();
        Slots = slots.ToDictionary().ToFrozenDictionary();
        RandomProperties = (randomProperties ?? []).ToDictionary(v => (v.Category, v.Grade)).ToFrozenDictionary();
        RandomModifiers = (randomModifiers ?? []).ToDictionary(v => (v.Group, v.Grade)).ToFrozenDictionary();
        SlaveSlots = (slaveSlots ?? []).ToFrozenSet();
        if (RandomProperties.Values.Any(v => v.MaximumModifiers is < 0 or > 5))
            throw new InvalidDataException("Random item modifiers exceed the five native detail slots.");
    }

    internal static ZoneEquipmentContent Load(SqliteConnection connection)
    {
        var options = new Dictionary<uint, int>();
        // 39B2D810 indexes by id; kind_id categorizes the setting and is not its lookup key.
        Read("SELECT id, value FROM content_configs", r => options.Add(U(r, 0), r.GetInt32(1)));
        ItemStatConfiguration? configuration = null;
        Read("""
            SELECT durability_const, holdable_durability_const, wearable_durability_const,
              item_stat_const, holdable_stat_const, wearable_stat_const, stat_value_const
            FROM item_configs WHERE id=1
            """, r => configuration = new(r.GetFloat(0), r.GetFloat(1), r.GetFloat(2), U(r, 3), U(r, 4), U(r, 5), U(r, 6),
                options.GetValueOrDefault(86u) != 0, options.GetValueOrDefault(359u) != 0));
        if (configuration is null) throw new InvalidDataException("Missing native item configuration row.");
        var grades = new List<ItemGradeDefinition>();
        Read("SELECT id, stat_multiplier, durability_value FROM item_grades", r => grades.Add(new(B(r, 0), r.GetInt32(1), r.GetFloat(2))));
        var holdables = new List<HoldableDefinition>();
        Read("SELECT id, slot_type_id, stat_multiplier, durability_ratio FROM holdables", r => holdables.Add(new(U(r, 0), U(r, 1), r.GetInt32(2), r.GetFloat(3))));
        var holdableMap = holdables.ToDictionary(v => v.Id);
        var weights = new List<ItemAttributeWeights>();
        Read("SELECT id, str_weight, dex_weight, sta_weight, int_weight, spi_weight FROM equip_item_attr_modifiers", r =>
            weights.Add(new(U(r, 0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5))));
        var coverage = new Dictionary<uint, int>();
        var wearableSlots = new List<WearableSlotDefinition>();
        Read("SELECT id, coverage, slot_type_id FROM wearable_slots", r =>
        {
            coverage.Add(U(r, 0), r.GetInt32(1));
            wearableSlots.Add(new(U(r, 0), NativeContentReader.UInt32(r, 2), NativeContentReader.Int32(r, 1)));
        });
        var durability = new Dictionary<uint, float>();
        Read("SELECT armor_type_id, durability_ratio FROM wearable_kinds", r => durability.Add(U(r, 0), r.GetFloat(1)));
        var slots = new Dictionary<uint, (int, int)>();
        Read("SELECT id, COALESCE(default_equip_slot_id, -1), COALESCE(aux_equip_slot_id, -1) FROM enum_equip_slot_types", r =>
            slots.Add(U(r, 0), (r.GetInt32(1), r.GetInt32(2))));
        var items = new Dictionary<uint, ItemStatDefinition>();
        Read("SELECT id, impl_id, level, COALESCE(level_requirement,0), COALESCE(level_limit,0) FROM items", r =>
            items.Add(U(r, 0), new(U(r, 0), B(r, 1), checked((short)r.GetInt32(2)), MinimumLevel: r.GetInt32(3), MaximumLevel: r.GetInt32(4))));
        var weaponHoldables = new Dictionary<uint, uint>();
        Read("""
            SELECT item_id, COALESCE(mod_set_id,0), holdable_id, COALESCE(durability_multiplier,100),
              COALESCE(item_rnd_attr_category_id,0) FROM item_weapons WHERE item_id IS NOT NULL
            """, r =>
        {
            // 39AC3010 is an independent item_weapons index, even when the main item impl differs.
            weaponHoldables.Add(U(r, 0), U(r, 2));
            if (!items.TryGetValue(U(r, 0), out var item) || item.Implementation != 1) return;
            var holdable = U(r, 2);
            items[item.Id] = item with { ModifierSet = U(r, 1), HoldableId = holdable,
                SlotType = holdableMap.GetValueOrDefault(holdable)?.SlotType ?? 0,
                DurabilityMultiplier = r.GetInt32(3), RandomCategory = U(r, 4), HasEquipmentDefinition = true };
        });
        var wearableClassifications = new Dictionary<uint, WearableClassification>();
        foreach (var table in new[] { "item_armors", "item_accessories" })
            Read($"SELECT item_id, COALESCE(mod_set_id,0), COALESCE(slot_type_id,0), type_id, COALESCE(durability_multiplier,100), COALESCE(item_rnd_attr_category_id,0) FROM {table} WHERE item_id IS NOT NULL", r =>
            {
                // 39ABF900 / 39AC2FA0 are independent indexes. Armor wins when both define an item.
                wearableClassifications.TryAdd(U(r, 0), new(U(r, 0), U(r, 2), U(r, 3)));
                if (!items.TryGetValue(U(r, 0), out var item)) return;
                if (table == "item_armors" ? item.Implementation is not (2 or 30 or 34) : item.Implementation != 24) return;
                items[item.Id] = item with { ModifierSet = U(r, 1), SlotType = U(r, 2), WearableType = U(r, 3),
                    DurabilityMultiplier = r.GetInt32(4), RandomCategory = U(r, 5), HasEquipmentDefinition = true };
            });
        Read("SELECT item_id, backpack_type_id FROM item_backpacks WHERE item_id IS NOT NULL", r =>
        {
            if (items.TryGetValue(U(r, 0), out var item) && item.Implementation == 22)
                items[item.Id] = item with { SlotType = r.GetInt32(1) is 2 or 7 ? 30u : 0u };
        });
        Read("SELECT item_id, slave_equip_kind_id FROM item_slave_equipments", r =>
        {
            if (items.TryGetValue(U(r, 0), out var item) && item.Implementation == 28)
                items[item.Id] = item with { HasEquipmentDefinition = true, SlaveEquipmentKind = U(r, 1) };
        });
        // 39AFF650 links kinds to slots; 39B002E0 links slots to the owning slave template.
        var slaveSlots = new List<(uint, int, uint)>();
        Read("""
            SELECT s.slave_id, s.equip_slot_id, k.slave_equip_kind_id
            FROM slave_equip_slots s JOIN slave_equip_kind_lists k ON k.slave_equip_slot_id=s.id
            JOIN slaves owner ON owner.id=s.slave_id
            """, r => slaveSlots.Add((U(r, 0), r.GetInt32(1), U(r, 2))));
        var properties = new List<RandomAttributeProperty>();
        Read("SELECT item_rnd_attr_category_id, grade_id, grade_exp, max_unit_modifier_num FROM item_rnd_attr_category_properties ORDER BY id", r =>
            properties.Add(new(U(r, 0), B(r, 1), r.GetInt32(2), r.GetInt32(3))));
        var random = new List<RandomAttributeModifier>();
        Read("""
            SELECT m.group_id, m.grade_id, g.unit_attribute_id, g.unit_modifier_type_id, m.min, m.max
            FROM item_rnd_attr_unit_modifiers m JOIN item_rnd_attr_unit_modifier_groups g ON g.id=m.group_id ORDER BY m.id
            """, r => random.Add(new(U(r, 0), B(r, 1), U(r, 2), B(r, 3), r.GetInt32(4), r.GetInt32(5))));
        return new(configuration, items.Values, grades, holdables, weights, coverage, durability, slots, properties, random, slaveSlots,
            weaponHoldables, wearableClassifications.Values, wearableSlots);

        void Read(string sql, Action<SqliteDataReader> consume)
        {
            using var command = connection.CreateCommand(); command.CommandText = sql;
            using var reader = command.ExecuteReader(); while (reader.Read()) consume(reader);
        }
    }

    private static uint U(SqliteDataReader reader, int index) => checked((uint)reader.GetInt64(index));
    private static byte B(SqliteDataReader reader, int index) => checked((byte)reader.GetInt32(index));
}
