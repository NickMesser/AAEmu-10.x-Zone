using AAEmu.Zone.Content;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Protocol.Units;

namespace AAEmu.Zone.Simulation;

/// <summary>Native equipment defense, power, weapon damage, attack intervals and defensive predicates.</summary>
public sealed class ZoneEquipmentCombatCalculator(ZoneStatContent content)
{
    public bool CanDefend(UnitStateSnapshot state, UnitEquipment equipment, uint attribute,
        IReadOnlyList<uint> passives, IReadOnlyList<UnitBuff> buffs, ISet<string> unresolved)
    {
        var main = equipment.Slots.FirstOrDefault(value => value.Slot == 15)?.Item ?? new UnitItem(0);
        var offhand = equipment.Slots.FirstOrDefault(value => value.Slot == 16)?.Item ?? new UnitItem(0);
        switch (attribute)
        {
            case 22: // 39D35850 needs the weapon descriptor, but neither its holdable nor the main item descriptor.
                return (Transformed(state) || HasWeapon(main.TemplateId, unresolved) && Durability(main) != 0) && !Excluded(state, 15);
            case 153:
                // 39D358E0 chooses the two-handed main hand before testing transformation.
                var twoHanded = main.TemplateId != 0 && PredicateHoldable(main.TemplateId, unresolved)?.SlotType == 16;
                var selected = twoHanded ? main : offhand;
                if (!(Transformed(state) || PositiveWeaponDamage(selected, unresolved) && Durability(selected) != 0) ||
                    Excluded(state, twoHanded ? (byte)15 : (byte)16)) return false;
                // 39D35970 tests the current passive vector and the active buff-ID index, not a tag or the passive's buff.
                // ServerUnit binds its owned ZoneBuffMan to the attribute context through 39BF4000.
                return passives.Contains(244u) && buffs.Any(buff => buff.TemplateId == 4270);
            case 177:
                return (Transformed(state) || PredicateHoldable(offhand.TemplateId, unresolved)?.SlotType == 20 && Durability(offhand) != 0) && !Excluded(state, 16);
            default: throw new ArgumentOutOfRangeException(nameof(attribute));
        }
    }

    public uint ArmorType(UnitStateSnapshot state, UnitEquipment equipment, IReadOnlyList<UnitBuff> buffs, ISet<string> unresolved)
    {
        if (Transformed(state))
        {
            // 39450C50 searches good, bad and hidden instances by template ID; presence includes one-time buffs.
            foreach (var (buff, type) in new (uint, uint)[] { (16551, 1), (16552, 2), (16553, 3) })
                if (buffs.Any(value => value.TemplateId == buff)) return type;
            return 0;
        }
        var counts = new int[6];
        foreach (var definition in ArmorPieces(state, equipment, unresolved))
            if (definition.SlotType != 0) counts[definition.ArmorType]++;
        for (uint type = 0; type < counts.Length; type++)
            if (counts[type] > 3) return type;
        return 0;
    }

    public uint ArmorCoverage(UnitStateSnapshot state, UnitEquipment equipment, int armorType, ISet<string> unresolved)
    {
        // 39D36130 uses the finished, narrowed armor_type even during transformation.
        if (armorType is < 1 or > 5) return 0;
        if (content.Equipment is not { } items) { unresolved.Add("armor classification content"); return 0; }
        var count = ArmorPieces(state, equipment, unresolved).Count(value => value.ArmorType == (uint)armorType);
        return count >= 7 ? items.FullArmorCoverage : items.FullArmorCoverage >> 1;
    }

    private IEnumerable<WearableClassification> ArmorPieces(UnitStateSnapshot state, UnitEquipment equipment, ISet<string> unresolved)
    {
        foreach (var slot in equipment.Slots)
        {
            // 39C9EF30 / 39CACE30: seven actual equipment slots, all requiring nonzero current durability.
            if (slot.Slot is not (0 or >= 2 and <= 7) || !Eligible(state, slot) || Durability(slot.Item) == 0) continue;
            if (content.Equipment is not { } items) { unresolved.Add("armor classification content"); continue; }
            if (items.WearableClassifications.TryGetValue(slot.Item.TemplateId, out var definition) &&
                items.ClassificationSlots.Contains(definition.SlotType)) yield return definition;
        }
    }

    private bool HasWeapon(uint templateId, ISet<string> unresolved)
    {
        if (content.Equipment is { } items) return items.WeaponHoldables.ContainsKey(templateId);
        if (templateId != 0) unresolved.Add("equipment combat templates");
        return false;
    }

    private HoldableDefinition? PredicateHoldable(uint templateId, ISet<string> unresolved) =>
        HasWeapon(templateId, unresolved) && content.Equipment!.WeaponHoldables.TryGetValue(templateId, out var holdable)
            ? content.Equipment.Holdables.GetValueOrDefault(holdable) : null;

    private bool PositiveWeaponDamage(UnitItem instance, ISet<string> unresolved)
    {
        var holdable = PredicateHoldable(instance.TemplateId, unresolved);
        if (holdable is null || !TryItem(instance, unresolved, out var item)) return false;
        var formula = Spec(holdable.Id, unresolved);
        if (formula is null) return false;
        // 39CA41F0 uses the signed template level and grade, with tempering but WITHOUT slot reinforcement.
        return Temper(Evaluate(formula.Damage, item.Level, Grade(instance.Grade).HoldableDamage), instance, unresolved) > 0;
    }

    public int Defense(UnitStateSnapshot state, UnitEquipment equipment, bool magic, ISet<string> unresolved)
    {
        if (Transformed(state)) return 0;
        var total = 0;
        foreach (var slot in equipment.Slots.OrderBy(value => value.Slot))
        {
            if (slot.Slot > 18 || !Eligible(state, slot)) continue;
            var durability = Durability(slot.Item);
            if (slot.Slot < 15 && durability == 0) continue;
            if (!TryItem(slot.Item, unresolved, out var item)) continue;
            if (slot.Slot < 15)
            {
                if (!item.HasEquipmentDefinition || item.Implementation is not (2 or 24 or 30 or 34)) continue;
                if (content.EquipmentCombat is not { } combat) { unresolved.Add("equipment combat formulas"); continue; }
                var basisPoints = combat.Wearables.TryGetValue((item.WearableType, item.SlotType), out var defense)
                    ? magic ? defense.MagicResistance : defense.Armor : 0;
                if (basisPoints == 0) continue; // Native skips even grade/formula lookup for a zero matrix entry.
                var grade = Grade(slot.Item.Grade);
                var formulaKind = magic ? 1u : 0u;
                if (!combat.WearableFormulas.TryGetValue(formulaKind, out var formula))
                    throw new InvalidDataException($"Missing wearable formula {formulaKind}.");
                var value = Evaluate(formula, Level(state, slot, item, unresolved), magic ? grade.WearableMagicResistance : grade.WearableArmor);
                value = Temper(value, slot.Item, unresolved);
                total = unchecked(total + ZoneStatMath.ToInt32((float)basisPoints * value * 0.0001f));
            }
            else if (TryHoldable(item, out var holdable))
            {
                var value = Holdable(state, slot, item, holdable.Id, magic ? 5u : 2u, unresolved);
                // Resistance narrows after EACH weapon addition, including a broken weapon's zero.
                total = magic ? ZoneStatMath.ToInt32(value + (float)total) : unchecked(total + ZoneStatMath.ToInt32(value));
            }
        }
        return total;
    }

    public float Power(UnitStateSnapshot state, UnitEquipment equipment, bool healing, ISet<string> unresolved)
    {
        if (Transformed(state)) return 0;
        var total = 0f;
        foreach (var slot in equipment.Slots.OrderBy(value => value.Slot))
        {
            if (slot.Slot is < 15 or > 18 || !Eligible(state, slot) || !TryItem(slot.Item, unresolved, out var item) ||
                !TryHoldable(item, out var holdable)) continue;
            // 39C9F340 applies only to healing; spell power accepts any holdable in slots 15..18.
            if (healing && (holdable.SlotType == 0 || !content.Equipment!.Slots.TryGetValue(holdable.SlotType, out var slots) ||
                slot.Slot != slots.Main && slot.Slot != slots.Auxiliary)) continue;
            total += Holdable(state, slot, item, holdable.Id, healing ? 4u : 1u, unresolved);
        }
        return total;
    }

    public int DamagePerSecond(UnitStateSnapshot state, UnitEquipment equipment, byte level, byte slot, ISet<string> unresolved)
    {
        // 39D35E40: only an empty main hand uses the unarmed fallback. Transformations
        // with an item still pass durability, exclusion and both descriptor lookups first.
        var instance = equipment.Slots.FirstOrDefault(value => value.Slot == slot);
        if (instance is null || instance.Item.TemplateId == 0) return slot == 15 ? Unarmed() : 0;
        if (Durability(instance.Item) == 0 || !Eligible(state, instance) ||
            !TryItem(instance.Item, unresolved, out var item) || !TryHoldable(item, out var holdable)) return 0;
        if (Transformed(state)) return Unarmed();
        return ZoneStatMath.ToInt32(Holdable(state, instance, item, holdable.Id, 0, unresolved) * 1000f);

        int Unarmed()
        {
            // The original zero-initialized globals at 3D8FBCE8/3D8FBCEC select holdable 0, grade 0.
            var fist = Spec(0, unresolved);
            return fist is null ? 0 : ZoneStatMath.ToInt32(Evaluate(fist.Damage, level, Grade(0).HoldableDamage) * 1000f);
        }
    }

    public int AttackInterval(UnitStateSnapshot state, UnitEquipment equipment, byte slot, Func<uint, long> attribute, ISet<string> unresolved)
    {
        var npc = state.Identity.Kind == ZoneUnitKind.Npc;
        var weapon = npc ? Spec(0, unresolved) : Weapon(state, equipment, slot, false, unresolved);
        // NPC 39D44A60 still reads the speed dependencies when its fist descriptor is absent.
        if (weapon is null && !npc) return 0;
        var speed = weapon?.Speed ?? 0;
        if (!npc && slot == 16) speed = unchecked(speed * 2);
        var primary = slot == 17 ? 55u : 54u;
        // 39BF6DB0 adds the low i32 values, then applies the PRIMARY attribute's limits once more.
        var sum = unchecked((int)attribute(primary) + (int)attribute(218));
        var limits = content.LimitsFor(primary);
        var adjustment = unchecked((int)Math.Clamp((long)sum, limits.Minimum, limits.Maximum));
        if (!npc && weapon!.SlotType == 16) adjustment = unchecked(adjustment + (int)attribute(75));
        var divisor = unchecked(Math.Max(adjustment, -999) + 1000);
        var numerator = unchecked(speed * 1000);
        // General 39D451A0 divides in i64; NPC 39D44A60 uses signed i32 division.
        return npc ? numerator / divisor : unchecked((int)((long)numerator / divisor));
    }

    public int Damage(UnitStateSnapshot state, UnitEquipment equipment, byte slot, bool minimum,
        Func<uint, long> attribute, ISet<string> unresolved)
    {
        var increase = slot == 17 ? 34u : 33u;
        if (state.Identity.Kind == ZoneUnitKind.Npc)
        {
            var addition = (float)attribute(increase) * 0.001f;
            var speed = Spec(0, unresolved)?.Speed ?? 0;
            var value = (float)speed * 0.001f * addition * (minimum ? 0.9f : 1.1f);
            return ZoneStatMath.ToInt32(slot == 16 ? value * 0.5f : value);
        }
        // 39D44FF0 reads both finished dependencies before resolving the weapon.
        var dps = (float)attribute((uint)(96 + slot - 15)) * 0.001f;
        var bonus = attribute(increase);
        var weapon = Weapon(state, equipment, slot, true, unresolved);
        if (weapon is null) return 0;
        var damage = (float)unchecked((uint)weapon.Speed) * (dps + (float)bonus * 0.001f) * 0.001f;
        // 39C9F3B0 uses signed damage_scale and rounds every operation as f32.
        var spread = (float)weapon.DamageScale * damage * 0.01f;
        return ZoneStatMath.ToInt32(minimum ? damage - spread : spread + damage);
    }

    private HoldableCombatFormulas? Weapon(UnitStateSnapshot state, UnitEquipment equipment, byte slot, bool requireItem, ISet<string> unresolved)
    {
        if (!Transformed(state))
        {
            var instance = equipment.Slots.FirstOrDefault(value => value.Slot == slot)?.Item;
            if (instance is { TemplateId: not 0 })
            {
                if (content.Equipment is not { } items) unresolved.Add("equipment combat templates");
                else if ((!requireItem || items.Items.ContainsKey(instance.TemplateId)) &&
                    items.WeaponHoldables.TryGetValue(instance.TemplateId, out var holdable) && items.Holdables.ContainsKey(holdable))
                    return Spec(holdable, unresolved);
            }
        }
        // General 3D8FBD38 and NPC 3D8FBD30 are separate zero-initialized holdable IDs.
        return Spec(0, unresolved);
    }

    private HoldableCombatFormulas? Spec(uint id, ISet<string> unresolved)
    {
        if (content.EquipmentCombat is not { } combat) { unresolved.Add("equipment combat formulas"); return null; }
        return combat.Holdables.GetValueOrDefault(id);
    }

    private float Holdable(UnitStateSnapshot state, UnitEquipmentSlot slot, ItemStatDefinition item, uint holdableId, uint kind, ISet<string> unresolved)
    {
        // Native checks the current byte directly, without consulting maximum durability or activation flags.
        if (Durability(slot.Item) == 0) return 0;
        if (content.EquipmentCombat is not { } combat) { unresolved.Add("equipment combat formulas"); return 0; }
        if (!combat.Holdables.TryGetValue(holdableId, out var formulas))
            throw new InvalidDataException($"Missing combat formulas for holdable {holdableId}.");
        var grade = Grade(slot.Item.Grade);
        var (formula, multiplier) = kind switch
        {
            0 => (formulas.Damage, grade.HoldableDamage),
            1 => (formulas.MagicDamage, grade.HoldableMagicDamage),
            2 => (formulas.Armor, grade.HoldableArmor),
            4 => (formulas.Healing, grade.HoldableHealing),
            5 => (formulas.MagicResistance, grade.HoldableMagicResistance),
            _ => throw new InvalidOperationException("Unsupported holdable combat formula kind.")
        };
        var value = Evaluate(formula, Level(state, slot, item, unresolved), multiplier);
        // The spell/healing visitors suppress tempering when the main item implementation is not Weapon.
        return kind is 1 or 4 && item.Implementation != 1 ? value : Temper(value, slot.Item, unresolved);
    }

    private float Level(UnitStateSnapshot state, UnitEquipmentSlot slot, ItemStatDefinition item, ISet<string> unresolved)
    {
        var selected = state.Character?.Reinforcements.FirstOrDefault(value => value.Slot == slot.Slot);
        if (selected is null) return item.Level;
        if (content.Reinforcements is not { } reinforcements) { unresolved.Add("equipment item-level reinforcement"); return item.Level; }
        if (!reinforcements.Levels.TryGetValue((slot.Slot, unchecked((sbyte)selected.Level)), out var definition)) return item.Level;
        var bonus = definition.ItemLevelBonus;
        if (content.Equipment!.Configuration.ReinforcementUsesFormula)
        {
            if (content.GearScores is not { } formulas) { unresolved.Add("equipment reinforcement formula"); return item.Level; }
            bonus = formulas.Formula(69).EvaluateNative(name => name switch
            {
                "item_level" => item.Level, "gain_item_level" => bonus,
                _ => throw new InvalidDataException($"Unresolved reinforcement formula input '{name}'.")
            });
        }
        return bonus + (float)item.Level;
    }

    private float Temper(float value, UnitItem item, ISet<string> unresolved)
    {
        if (content.GearScores is not { } scales) { unresolved.Add("equipment tempering scales"); return value; }
        var rune = item.Details is { Type: 1, Equipment: { } details } ? details.Rune : (ushort)0;
        var tempering = scales.TemperingScales.TryGetValue(rune, out var scale) ? (float)scale + 1000f : 0;
        // 39C9FC00 returns zero for absent definitions; these helpers scale only when positive.
        return tempering > 0 ? value * tempering * 0.001f : value;
    }

    private EquipmentCombatGrade Grade(byte id) => content.EquipmentCombat!.Grades.TryGetValue(id, out var grade) ? grade :
        throw new InvalidDataException($"Unknown equipment combat grade {id}.");

    private bool TryItem(UnitItem instance, ISet<string> unresolved, out ItemStatDefinition item)
    {
        item = null!;
        if (content.Equipment is not { } equipment) { unresolved.Add("equipment combat templates"); return false; }
        return equipment.Items.TryGetValue(instance.TemplateId, out item!);
    }

    private bool TryHoldable(ItemStatDefinition item, out HoldableDefinition holdable)
    {
        holdable = null!;
        return content.Equipment!.WeaponHoldables.TryGetValue(item.Id, out var holdableId) &&
            content.Equipment.Holdables.TryGetValue(holdableId, out holdable!);
    }

    private static bool Eligible(UnitStateSnapshot state, UnitEquipmentSlot slot) => slot.Item.TemplateId != 0 &&
        !Excluded(state, slot.Slot);
    private static bool Excluded(UnitStateSnapshot state, byte slot) =>
        state.Identity.Kind == ZoneUnitKind.Character && state.SlotSelectors.Span.Contains(slot);
    private static bool Transformed(UnitStateSnapshot state) => state.Identity.Kind == ZoneUnitKind.Character && state.SecondaryLevel != 0;
    // 393876A0 puts opaque bytes at Details+1; the combat helpers read Details+5
    // without checking its discriminator. Rune at Details+0x3C lies beyond every opaque payload.
    private static byte Durability(UnitItem item) => item.Details is { } details ? details.Type == 1 ?
        details.Equipment?.Durability ?? 0 : details.OpaqueData.Length > 4 ? details.OpaqueData.Span[4] : (byte)0 : (byte)0;
    private static float Evaluate(ContentFormula formula, float level, float grade) => formula.EvaluateNative(name => name switch
    {
        "item_level" => level, "item_grade" => grade,
        _ => throw new InvalidDataException($"Unresolved equipment combat formula input '{name}'.")
    });
}
