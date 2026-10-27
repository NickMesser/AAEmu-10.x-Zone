using AAEmu.Zone.Content;
using AAEmu.Zone.Protocol.Units;

namespace AAEmu.Zone.Combat;

public readonly record struct UnitGearScore(int Total, int Bare);
public readonly record struct ItemGearScore(float Total, float Bare);

/// <summary>39CA1E70/39CA0610 gear score, including its distinct slot, detail and socket rules.</summary>
public sealed class ZoneGearScoreCalculator(ZoneEquipmentContent equipment, ZoneGearScoreContent content,
    ZoneReinforcementContent? reinforcements = null)
{
    public UnitGearScore Calculate(UnitEquipment currentEquipment, IReadOnlyList<EquipmentReinforcement>? levels = null)
    {
        float total = 0, bare = 0;
        // Native visits actual slots in ascending order; validity/activation flags do not filter them.
        foreach (var slot in currentEquipment.Slots.OrderBy(value => value.Slot))
        {
            if (!VisitsSlot(slot.Slot) || slot.Item.TemplateId == 0) continue;
            var bonus = ItemLevelBonus(slot.Item.TemplateId, slot.Slot, levels ?? []);
            var score = CalculateItem(slot.Item, bonus);
            total += score.Total;
            bare += score.Bare;
        }
        return new(checked((int)total), checked((int)bare));
    }

    public static bool VisitsSlot(int slot) => slot is >= 0 and <= 18 or 26 or 27 or 29 or 30 or 32;

    private float ItemLevelBonus(uint itemId, int slot, IReadOnlyList<EquipmentReinforcement> levels)
    {
        var entry = levels.FirstOrDefault(value => value.Slot == slot);
        if (entry is null || reinforcements is null ||
            !reinforcements.Levels.TryGetValue((slot, unchecked((sbyte)entry.Level)), out var definition)) return 0;
        var bonus = definition.ItemLevelBonus;
        if (!equipment.Configuration.ReinforcementUsesFormula) return bonus;
        var itemLevel = equipment.Items.TryGetValue(itemId, out var item) ? item.Level : 1;
        return content.Formula(69).Evaluate(name => name switch
        {
            "item_level" => itemLevel, "gain_item_level" => bonus,
            _ => throw new InvalidDataException($"Unresolved reinforcement formula input '{name}'.")
        });
    }

    public ItemGearScore CalculateItem(UnitItem instance, float itemLevelBonus = 0)
    {
        if (instance.TemplateId == 0 || !equipment.Items.TryGetValue(instance.TemplateId, out var item) ||
            instance.Details?.Type is not (1 or 9)) return default;
        var details = instance.Details.Type == 1 ? instance.Details.Equipment ??
            throw new InvalidDataException("Missing gear-score equipment details.") : null;
        uint formula;
        int weight;
        switch (item.Implementation)
        {
            case 1:
                if (!item.HasEquipmentDefinition || !content.Holdables.TryGetValue(item.HoldableId, out weight)) return default;
                formula = 30;
                break;
            case 2:
            case 24:
                if (!content.WearableSlots.TryGetValue(item.HasEquipmentDefinition ? item.SlotType : 0, out weight)) return default;
                formula = item.Implementation == 2 ? 56u : 57u;
                break;
            case 22:
                // The native scorer recognizes type-9 backpack details, irrespective of template backpack kind.
                if (!content.WearableSlots.TryGetValue(instance.Details.Type == 9 ? 30u : 0u, out weight)) return default;
                formula = 57;
                break;
            default:
                return default;
        }
        if (!content.Grades.TryGetValue(instance.Grade, out var grade))
            throw new InvalidDataException($"Unknown gear-score item grade {instance.Grade}.");
        // 39C9FC00: missing tempering definition yields zero, not the untempered multiplier.
        var scaling = content.TemperingScales.TryGetValue(details?.Rune ?? 0, out var scale) ?
            ((float)scale + 1000f) * 0.001f : 0;
        var itemLevel = (float)item.Level + itemLevelBonus;
        var multiplier = (float)(weight * 0.01); // native multiplies as double, then narrows
        var element = details?.ElementLevel ?? 0;
        var bare = Round(content.Formula(formula).Evaluate(name => name switch
        {
            "item_level" => itemLevel, "item_grade" => grade, "gear_score_multiplier" => multiplier,
            "scaling_multiplier" => scaling, "element_level" => element,
            _ => throw new InvalidDataException($"Unresolved gear-score formula input '{name}'.")
        }));
        var total = bare;
        if (details is not null)
        {
            if (details.Attributes.Count != 4 || details.Gems.Count != 14)
                throw new InvalidDataException("Invalid gear-score equipment detail layout.");
            total += Socket(details.Attributes[1], 32);
            // Gear score skips empty/unknown IDs and continues through all nine sockets.
            for (var i = 0; i < 9; i++) total += Socket(details.Gems[i], 31);
        }
        return new(total, bare);

        float Socket(uint id, uint kind)
        {
            if (id == 0 || !equipment.Items.TryGetValue(id, out var socket)) return 0;
            return Round(content.Formula(kind).Evaluate(name => name == "item_level" ? socket.Level :
                throw new InvalidDataException($"Unresolved socket-score formula input '{name}'.")));
        }
    }

    // 39B1C480/39B1C5A0/39B1C670 floor to one decimal before summing in f32.
    private static float Round(float value)
    {
        var scaled = value * 10f;
        if (!float.IsFinite(scaled)) throw new InvalidDataException("Non-finite gear score.");
        return MathF.Floor(scaled) / 10f;
    }
}
