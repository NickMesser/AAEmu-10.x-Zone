using AAEmu.Zone.Content;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Protocol.Units;

namespace AAEmu.Zone.Simulation;

public sealed class ZoneReinforcementCalculator(ZoneReinforcementContent content)
{
    // 39CE8F90/39CE8D50: exact slot and signed-byte level match; absent slots have no item-level bonus.
    public float ItemLevelBonus(IReadOnlyList<EquipmentReinforcement> levels, int slot)
    {
        var entry = levels.FirstOrDefault(value => value.Slot == slot);
        return entry is not null && content.Levels.TryGetValue((slot, unchecked((sbyte)entry.Level)), out var definition)
            ? definition.ItemLevelBonus : 0;
    }

    // 39CE95E0 fixes these groups. 39CE9C50/39CEA0C0 add max(1, signed-byte level), wrapping at each addition.
    public sbyte GroupLevel(IReadOnlyList<EquipmentReinforcement> levels, int group)
    {
        int[] slots = group switch
        {
            1 => content.UseBundleEffects ? [15, 16, 17] : [15, 16, 17, 18],
            2 => [0, 2, 3, 4, 5, 6, 7],
            3 => content.UseBundleEffects ? [1, 9, 10, 11, 12, 18] : [1, 9, 10, 11, 12],
            _ => []
        };
        sbyte total = 0;
        foreach (var slot in slots)
        {
            var entry = levels.FirstOrDefault(value => value.Slot == slot);
            var level = entry is null ? 1 : Math.Max(1, (int)unchecked((sbyte)entry.Level));
            total = unchecked((sbyte)(total + level));
        }
        return total;
    }

    internal void Append(ZoneStatContent stats, UnitStateSnapshot state, List<(StatModifier Modifier, int Level, int Stack)> modifiers)
    {
        // 39BF6510 calls the reinforcement visitor for characters only; 39BF6340 excludes transformations.
        if (state.Identity.Kind != ZoneUnitKind.Character || state.SecondaryLevel != 0) return;
        var levels = state.Character?.Reinforcements ?? [];
        if (content.UseBundleEffects)
        {
            var offense = GroupLevel(levels, 1); var defense = GroupLevel(levels, 2); var support = GroupLevel(levels, 3);
            foreach (var effect in content.BundleEffects)
                if (offense >= effect.OffenseLevel && defense >= effect.DefenseLevel && support >= effect.SupportLevel)
                    Add("EquipSlotReinforceBundleEffect", effect.Id);
        }
        else
        {
            for (var group = 1; group <= 3; group++)
            {
                var level = GroupLevel(levels, group);
                foreach (var effect in content.SetEffects)
                    if (effect.Attribute == group && level >= effect.RequiredLevel) Add("EquipSlotReinforceSetEffect", effect.Id);
            }
        }
        foreach (var selected in state.Character?.LevelEffects ?? [])
        {
            if (!content.Modifiers.TryGetValue(selected.EffectId, out var modifier) || modifier.Type > 1) continue;
            modifiers.Add((new(modifier.Id, "EquipSlotReinforceUnitModifier", selected.EffectId,
                modifier.Attribute, modifier.Type, modifier.Value, 0, 0), 0, 1));
        }

        void Add(string owner, uint id) => modifiers.AddRange(stats.Modifiers(owner, id).Select(modifier => (modifier, 0, 1)));
    }
}
