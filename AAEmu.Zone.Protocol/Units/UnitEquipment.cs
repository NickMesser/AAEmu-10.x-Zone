namespace AAEmu.Zone.Protocol.Units;

public enum EquipmentEncoding { TemplateOnly, Compact, Full }

public sealed record EquipmentDetails(byte Durability, short ChargeCount, long ChargeTime, ushort Rune,
    ushort EvolveChance, long ChargeProcTime, byte MappingFailBonus, byte ElementLevel,
    IReadOnlyList<uint> Attributes, IReadOnlyList<uint> Gems);

/// <summary>Types 2..14 are fixed-size native detail blobs; only type 1 uses nested fields/PISC.</summary>
public sealed record ItemDetails(byte Type, EquipmentDetails? Equipment, ReadOnlyMemory<byte> OpaqueData)
{
    internal void Write(ZoneWriter writer)
    {
        writer.WriteByte(Type);
        if (Type == 1)
        {
            var equipment = Equipment ?? throw new InvalidDataException("Missing equipment detail fields.");
            if (equipment.Attributes.Count != 4 || equipment.Gems.Count != 14)
                throw new InvalidDataException("Equipment details require a PISC quartet and fourteen gem values.");
            writer.WriteByte(equipment.Durability);
            writer.WriteInt16(equipment.ChargeCount);
            writer.WriteInt64(equipment.ChargeTime);
            writer.WriteUInt16(equipment.Rune);
            writer.WriteUInt16(equipment.EvolveChance);
            writer.WriteInt64(equipment.ChargeProcTime);
            writer.WriteByte(equipment.MappingFailBonus);
            writer.WriteByte(equipment.ElementLevel);
            writer.WritePisc(equipment.Attributes.ToArray());
            writer.WritePisc(equipment.Gems.ToArray());
        }
        else
        {
            if (OpaqueData.Length != OpaqueSize(Type))
                throw new InvalidDataException("Invalid item detail blob length.");
            writer.WriteBytes(OpaqueData.Span);
        }
    }

    internal static ItemDetails Read(ref ZoneReader reader)
    {
        // FUN_393876A0. Lengths exclude the detail discriminator itself.
        var type = reader.ReadByte();
        if (type == 1)
        {
            var equipment = new EquipmentDetails(reader.ReadByte(), reader.ReadInt16(), reader.ReadInt64(),
                reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadInt64(), reader.ReadByte(), reader.ReadByte(),
                Array.AsReadOnly(reader.ReadPisc(4)), Array.AsReadOnly(reader.ReadPisc(14)));
            return new(type, equipment, ReadOnlyMemory<byte>.Empty);
        }
        return new(type, null, reader.ReadBytes(OpaqueSize(type)).ToArray());
    }

    private static int OpaqueSize(byte type) => type switch
    {
        0 => 0, 2 => 33, 3 => 20, 4 => 9, 5 or 11 => 24, 6 or 7 => 16,
        8 or 14 => 8, 9 => 4, 10 => 12, 12 => 10, 13 => 13,
        _ => throw new InvalidDataException($"Unsupported item detail discriminator {type}.")
    };
}

public sealed record UnitItem(uint TemplateId, ulong Id = 0, byte Grade = 0, byte Flags = 0,
    int StackSize = 0, ItemDetails? Details = null, long CreationTime = 0, uint LifespanMinutes = 0,
    long MadeByUnitId = 0, byte WorldId = 0, ulong UnsecureTime = 0, ulong UnpackTime = 0, ulong ChargeUseSkillTime = 0)
{
    internal void WriteFull(ZoneWriter writer)
    {
        writer.WriteUInt32(TemplateId);
        if (TemplateId == 0) return;
        writer.WriteUInt64(Id);
        writer.WriteByte(Grade);
        writer.WriteByte(Flags);
        writer.WriteInt32(StackSize);
        (Details ?? new ItemDetails(0, null, ReadOnlyMemory<byte>.Empty)).Write(writer);
        writer.WriteInt64(CreationTime);
        writer.WriteUInt32(LifespanMinutes);
        writer.WriteInt64(MadeByUnitId);
        writer.WriteByte(WorldId);
        writer.WriteUInt64(UnsecureTime);
        writer.WriteUInt64(UnpackTime);
        writer.WriteUInt64(ChargeUseSkillTime);
    }

    internal static UnitItem Read(ref ZoneReader reader, EquipmentEncoding encoding)
    {
        // FUN_39387A80 (full), FUN_39387C20 (compact): zero template omits ALL subsequent fields.
        var template = reader.ReadUInt32();
        if (template == 0 || encoding == EquipmentEncoding.TemplateOnly)
            return new(template);
        var id = reader.ReadUInt64();
        var grade = reader.ReadByte();
        if (encoding == EquipmentEncoding.Compact)
            return new(template, id, grade);
        return new(template, id, grade, reader.ReadByte(), reader.ReadInt32(), ItemDetails.Read(ref reader),
            reader.ReadInt64(), reader.ReadUInt32(), reader.ReadInt64(), reader.ReadByte(),
            reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt64());
    }
}

public sealed record UnitEquipmentSlot(byte Slot, EquipmentEncoding Encoding, UnitItem Item);

/// <param name="CharacterFlags">Random-attribute activation bits. Initial snapshots carry them only for characters;
/// incremental equipment messages can update them for any unit kind.</param>
public sealed record UnitEquipment(ulong ValidFlags, IReadOnlyList<UnitEquipmentSlot> Slots, ulong CharacterFlags)
{
    internal static UnitEquipment Read(ref ZoneReader reader, ZoneUnitKind kind)
    {
        // FUN_3938AC90: high bits above slot 33 have no corresponding payload.
        var flags = reader.ReadUInt64();
        var slots = new List<UnitEquipmentSlot>();
        for (byte slot = 0; slot < 34; slot++)
        {
            if ((flags & (1UL << slot)) == 0)
                continue;
            EquipmentEncoding encoding;
            if (slot is >= 19 and <= 25 && kind != ZoneUnitKind.Slave)
                encoding = EquipmentEncoding.TemplateOnly;
            else if (kind == ZoneUnitKind.Npc)
                encoding = slot == 27 || slot >= 31 ? EquipmentEncoding.Full : EquipmentEncoding.Compact;
            else if (kind is ZoneUnitKind.Transfer or ZoneUnitKind.Shipyard)
                continue;
            else
                encoding = EquipmentEncoding.Full;
            slots.Add(new(slot, encoding, UnitItem.Read(ref reader, encoding)));
        }
        return new(flags, slots.AsReadOnly(), kind == ZoneUnitKind.Character ? reader.ReadUInt64() : 0);
    }
}
