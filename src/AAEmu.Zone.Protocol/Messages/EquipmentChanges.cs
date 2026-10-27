using AAEmu.Zone.Protocol.Units;

namespace AAEmu.Zone.Protocol.Messages;

public sealed record EquipmentChange(byte Slot, UnitItem Item, bool RandomAttributesActive);

/// <summary>3936BA00: bc, u8 count, (s8 slot, full item) entries, u64 flags indexed by entry.</summary>
public sealed record EquipmentChanged(uint ObjectId, IReadOnlyList<EquipmentChange> Changes)
{
    public static EquipmentChanged Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var id = reader.ReadBc();
        var count = reader.ReadByte();
        // Native clamps its fixed array; reject the oversized body instead of truncating it.
        if (count > 34) throw new InvalidDataException("Too many equipment changes.");
        var changes = new EquipmentChange[count];
        for (var index = 0; index < count; index++)
        {
            var slot = reader.ReadSByte();
            if (slot is < 0 or >= 34) throw new InvalidDataException("Invalid equipment slot.");
            changes[index] = new((byte)slot, UnitItem.Read(ref reader, EquipmentEncoding.Full), false);
        }
        var flags = reader.ReadUInt64();
        reader.EnsureFullyConsumed();
        for (var index = 0; index < count; index++)
            changes[index] = changes[index] with { RandomAttributesActive = (flags & (1UL << index)) != 0 };
        return new(id, Array.AsReadOnly(changes));
    }

    public ZoneFrame ToFrame()
    {
        if (Changes.Count > 34) throw new InvalidDataException("Too many equipment changes.");
        var writer = new ZoneWriter();
        writer.WriteBc(ObjectId);
        writer.WriteByte((byte)Changes.Count);
        ulong flags = 0;
        for (var index = 0; index < Changes.Count; index++)
        {
            var change = Changes[index];
            if (change.Slot >= 34) throw new InvalidDataException("Invalid equipment slot.");
            writer.WriteByte(change.Slot);
            change.Item.WriteFull(writer);
            if (change.RandomAttributesActive) flags |= 1UL << index;
        }
        writer.WriteUInt64(flags);
        return new(WzOpcodes.UnitEquipmentChanged, writer.ToArray());
    }
}

/// <summary>3936BBE0: bc and one u64 mask; only the 34 native equipment slots participate.</summary>
public readonly record struct EquipmentActivationChanged(uint ObjectId, ulong Flags)
{
    public const ulong SlotMask = (1UL << 34) - 1;

    public static EquipmentActivationChanged Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var result = new EquipmentActivationChanged(reader.ReadBc(), reader.ReadUInt64() & SlotMask);
        reader.EnsureFullyConsumed();
        return result;
    }

    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter();
        writer.WriteBc(ObjectId);
        writer.WriteUInt64(Flags & SlotMask);
        return new(WzOpcodes.UnitEquipmentsRndAttrUnitModifierAvtivateChanged, writer.ToArray());
    }
}
