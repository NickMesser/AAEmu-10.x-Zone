using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Protocol.Units;

namespace AAEmu.Zone.Combat;

public static class ZoneEquipmentLifecycle
{
    public static UnitEquipment Change(UnitEquipment current, EquipmentChanged message)
    {
        var slots = current.Slots.ToDictionary(slot => slot.Slot);
        var flags = current.CharacterFlags & EquipmentActivationChanged.SlotMask;
        // 3935CDC0 copies each entry in order, including its activation flag. Last duplicate wins.
        foreach (var change in message.Changes)
        {
            if (change.Slot >= 34) throw new InvalidDataException("Invalid equipment slot.");
            if (change.Item.TemplateId == 0) slots.Remove(change.Slot);
            else slots[change.Slot] = new(change.Slot, EquipmentEncoding.Full, change.Item);
            var bit = 1UL << change.Slot;
            flags = change.RandomAttributesActive ? flags | bit : flags & ~bit;
        }
        var ordered = slots.Values.OrderBy(slot => slot.Slot).ToArray();
        var valid = ordered.Where(slot => slot.Item.TemplateId != 0).Aggregate(0UL, (mask, slot) => mask | (1UL << slot.Slot));
        return new(valid, Array.AsReadOnly(ordered), flags);
    }
}
