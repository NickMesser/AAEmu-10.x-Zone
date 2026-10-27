using AAEmu.Zone.Content;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Protocol.Units;

namespace AAEmu.Zone.Combat;

/// <summary>Equipment inputs to the shared native flat/percentage stat accumulator.</summary>
public sealed class ZoneEquipmentCalculator(ZoneEquipmentContent content)
{
    // 39BF3530 deliberately mixes f32 concentration/rounding and f64 scaling.
    public int BaseAttribute(ItemStatDefinition item, byte grade, uint attribute, float levelBonus = 0)
    {
        if (attribute > 4 || !item.HasEquipmentDefinition || item.Implementation is not (1 or 2 or 24 or 30 or 34) ||
            item.ModifierSet == 0 || !content.Weights.TryGetValue(item.ModifierSet, out var weights) ||
            weights.Weight(attribute) == 0 || weights.Count == 0) return 0;
        if (weights.Total == 0) throw new InvalidDataException("Item attribute weights have a zero native denominator.");
        var config = content.Configuration;
        var concentration = MathF.Pow(weights.Count == 1 ? 3f : weights.Count == 2 ? 1.5f : 1f,
            1f / ((float)config.StatValue * 0.01f));
        var scale = config.ItemStat * 0.01 * (double)(item.Level + levelBonus) * concentration;
        double factor;
        if (item.Implementation == 1) factor = config.HoldableStat;
        else
        {
            scale = scale * config.WearableStat * 0.01;
            factor = Require(content.Coverage, item.SlotType, "wearable slot coverage");
        }
        var value = Round((float)(Require(content.Grades, grade, "item grade").StatMultiplier *
            ((weights.Count * scale * factor * 0.01 * weights.Weight(attribute)) / weights.Total) * 0.01));
        if (item.Implementation == 1 && content.Holdables.TryGetValue(item.HoldableId, out var holdable))
            value = Round((float)(holdable.StatMultiplier * (double)value * 0.01));
        return value;
    }

    // 39CA1F80 truncates/wraps twice after rounding the three source factors separately.
    public byte MaximumDurability(ItemStatDefinition item, byte grade)
    {
        if (!item.HasEquipmentDefinition || item.Implementation is not (1 or 2 or 24 or 30 or 34)) return 0;
        var config = content.Configuration;
        var gradeFactor = UnsignedRound(Require(content.Grades, grade, "item grade").Durability * 1000f);
        double raw;
        if (item.Implementation == 1)
            raw = UnsignedRound(Require(content.Holdables, item.HoldableId, "holdable").DurabilityRatio * 1000f) *
                (double)Round(config.HoldableDurability * 100f) * gradeFactor * 1e-8;
        else
            raw = UnsignedRound(Require(content.WearableDurability, item.WearableType, "wearable durability") * 1000f) *
                (double)Require(content.Coverage, item.SlotType, "wearable slot coverage") *
                Round(config.WearableDurability * 100f) * gradeFactor * 1e-10;
        if (!double.IsFinite(raw)) throw new InvalidDataException("Non-finite item durability.");
        if (raw is < 0 or > 255) return 0;
        var scaled = unchecked((byte)checked((long)((byte)raw * config.Durability)));
        return item.DurabilityMultiplier == 100 ? scaled :
            unchecked((byte)checked((long)(item.DurabilityMultiplier * 0.01f * scaled)));
    }

    internal void Append(ZoneStatContent stats, UnitStateSnapshot state, UnitEquipment equipment, byte unitLevel,
        List<(StatModifier Modifier, int Level, int Stack)> modifiers, ISet<string> unresolved)
    {
        var kind = state.Identity.Kind;
        if (kind is not (ZoneUnitKind.Character or ZoneUnitKind.Mate or ZoneUnitKind.Slave) ||
            kind == ZoneUnitKind.Character && state.SecondaryLevel != 0) return;
        foreach (var slot in equipment.Slots.OrderBy(value => value.Slot))
        {
            if (slot.Item.TemplateId == 0 || !VisitsSlot(kind, slot.Slot, state.Appearance.Hair?.VisualRace ?? 0)) continue;
            var item = slot.Item;
            content.Items.TryGetValue(item.TemplateId, out var definition);
            var details = item.Details?.Equipment;
            if (definition is not null && MaximumDurability(definition, item.Grade) != 0 && (details?.Durability ?? 0) == 0) continue;
            if (kind == ZoneUnitKind.Character && state.SlotSelectors.Span.Contains(slot.Slot)) continue;
            if (definition is not null)
            {
                var slaveEquipment = definition.Implementation == 28 && kind == ZoneUnitKind.Slave;
                if (slaveEquipment ? !FitsSlaveSlot(definition, state.Identity.TemplateId, slot.Slot, unitLevel) : !FitsSlot(definition, slot.Slot)) continue;
                var levelBonus = 0f;
                if (!content.Configuration.ReinforcementUsesFormula && state.Character?.Reinforcements is { Count: > 0 } levels)
                {
                    if (stats.Reinforcements is { } reinforcementContent)
                        levelBonus = new ZoneReinforcementCalculator(reinforcementContent).ItemLevelBonus(levels, slot.Slot);
                    else unresolved.Add("equipment item-level reinforcement");
                }
                for (uint attribute = 0; attribute < 5; attribute++)
                    Add(attribute, 0, BaseAttribute(definition, item.Grade, attribute, levelBonus), definition.Level);
                AddItemModifiers(definition);
                if (!slaveEquipment && details is not null && (equipment.CharacterFlags & (1UL << slot.Slot)) != 0)
                    AddRandom(definition, item.Grade, details);
            }
            // 39BF4100 still visits sockets when the parent item template is absent.
            if (details is null) continue;
            if (details.Attributes.Count != 4 || details.Gems.Count != 14)
                throw new InvalidDataException("Invalid equipment detail vectors.");
            AddSocket(details.Attributes[1]);
            for (var index = 0; index < 9 && details.Gems[index] != 0; index++) AddSocket(details.Gems[index]);
        }

        void AddItemModifiers(ItemStatDefinition item) =>
            modifiers.AddRange(stats.Modifiers("Item", item.Id).Select(modifier => (modifier, (int)item.Level, 1)));
        void AddSocket(uint template)
        {
            if (template != 0 && content.Items.TryGetValue(template, out var socket)) AddItemModifiers(socket);
        }
        void Add(uint attribute, byte type, int value, int level) =>
            modifiers.Add((new(0, "Equipment", 0, attribute, type, value, 0, 0), level, 1));
        void AddRandom(ItemStatDefinition item, byte grade, EquipmentDetails details)
        {
            if (!content.Configuration.RandomAttributesEnabled || item.RandomCategory == 0 ||
                !content.RandomProperties.TryGetValue((item.RandomCategory, grade), out var property)) return;
            if (details.Attributes.Count != 4 || details.Gems.Count != 14)
                throw new InvalidDataException("Invalid equipment detail vectors.");
            var ratio = property.GradeExperience < 1 ? 0f :
                (float)unchecked((int)details.Attributes[3]) / property.GradeExperience;
            for (var index = 0; index < property.MaximumModifiers; index++)
            {
                var group = details.Gems[9 + index];
                if (group == 0 || !content.RandomModifiers.TryGetValue((group, grade), out var modifier)) break;
                var value = checked((int)((float)unchecked(modifier.Maximum - modifier.Minimum) * ratio + modifier.Minimum));
                Add(modifier.Attribute, modifier.Type, value, unchecked((byte)item.Level));
            }
        }
    }

    internal static bool VisitsSlot(ZoneUnitKind kind, byte slot, byte visualRace) =>
        kind == ZoneUnitKind.Slave ? slot < 28 : slot < 19 || slot == 27 || slot == 32 && visualRace != 0;

    // 39CA69D0 uses equip requirements, not the item's stat level, attachment point, or dual slot.
    public bool FitsSlaveSlot(ItemStatDefinition item, uint slave, byte slot, byte level) =>
        item.Implementation == 28 && item.HasEquipmentDefinition && item.MinimumLevel <= level &&
        (item.MaximumLevel == 0 || level <= item.MaximumLevel) && content.SlaveSlots.Contains((slave, slot, item.SlaveEquipmentKind));

    private bool FitsSlot(ItemStatDefinition item, byte slot) =>
        item.Implementation is 1 or 2 or 24 or 30 or 34 or 22 &&
        (item.Implementation == 22 || item.HasEquipmentDefinition) &&
        (item.Implementation != 1 || content.Holdables.ContainsKey(item.HoldableId)) &&
        item.SlotType != 0 && content.Slots.TryGetValue(item.SlotType, out var slots) &&
        (slots.Main == slot || slots.Auxiliary == slot);

    private static int Round(float value) => checked((int)MathF.Floor(value + 0.5f));
    private static uint UnsignedRound(float value) => unchecked((uint)Round(value));
    private static TValue Require<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> values, TKey key, string name) where TKey : notnull =>
        values.TryGetValue(key, out var value) ? value : throw new InvalidDataException($"Missing {name} {key}.");
}
