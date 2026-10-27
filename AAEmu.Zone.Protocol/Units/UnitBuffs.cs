namespace AAEmu.Zone.Protocol.Units;

public enum ZoneCasterKind : byte { Unit, Doodad, Item, Mount, Gimmick }
public sealed record UnitSkillCaster(ZoneCasterKind Kind, uint ObjectId, ulong ItemId = 0,
    uint ItemTemplateId = 0, byte ItemType = 0, long ItemValue = 0, uint MountSkillTemplateId = 0)
{
    internal void Write(ZoneWriter writer)
    {
        if (Kind > ZoneCasterKind.Gimmick)
            throw new InvalidDataException($"Unsupported skill caster discriminator {Kind}.");
        writer.WriteByte((byte)Kind);
        writer.WriteBc(ObjectId);
        if (Kind == ZoneCasterKind.Item)
        {
            writer.WriteUInt64(ItemId);
            writer.WriteUInt32(ItemTemplateId);
            writer.WriteByte(ItemType);
            writer.WriteInt64(ItemValue);
        }
        else if (Kind == ZoneCasterKind.Mount)
            writer.WriteUInt32(MountSkillTemplateId);
    }

    internal static UnitSkillCaster Read(ref ZoneReader reader)
    {
        // FUN_3938E850: every supported variant starts with type + bc; only item/mount append data.
        var kind = (ZoneCasterKind)reader.ReadByte();
        if (kind > ZoneCasterKind.Gimmick)
            throw new InvalidDataException($"Unsupported skill caster discriminator {kind}.");
        var objectId = reader.ReadBc();
        return kind switch
        {
            ZoneCasterKind.Item => new(kind, objectId, reader.ReadUInt64(), reader.ReadUInt32(), reader.ReadByte(), reader.ReadInt64()),
            ZoneCasterKind.Mount => new(kind, objectId, MountSkillTemplateId: reader.ReadUInt32()),
            _ => new(kind, objectId)
        };
    }
}

public sealed record UnitBuff(uint Index, UnitSkillCaster Caster, long CasterPersistentId, byte CasterLevel,
    ushort CasterAbilityLevel, uint Duration, uint Elapsed, uint Tick, uint TickIndex,
    uint TemplateId, uint Stack, uint Charge, uint SourceSkill)
{
    internal static UnitBuff Read(ref ZoneReader reader)
    {
        // FUN_3938FA00: the two PISC quartets must remain separate from surrounding records.
        var index = reader.ReadUInt32();
        var caster = UnitSkillCaster.Read(ref reader);
        var persistentId = reader.ReadInt64();
        var level = reader.ReadByte();
        var abilityLevel = reader.ReadUInt16();
        var timing = reader.ReadPisc(4);
        var buff = reader.ReadPisc(4);
        return new(index, caster, persistentId, level, abilityLevel, timing[0], timing[1], timing[2], timing[3],
            buff[0], buff[1], buff[2], buff[3]);
    }
}

public sealed record UnitBuffs(IReadOnlyList<UnitBuff> Good, IReadOnlyList<UnitBuff> Bad, IReadOnlyList<UnitBuff> Hidden)
{
    internal static UnitBuffs Read(ref ZoneReader reader) =>
        new(ReadList(ref reader, 32), ReadList(ref reader, 20), ReadList(ref reader, 28));

    private static System.Collections.ObjectModel.ReadOnlyCollection<UnitBuff> ReadList(ref ZoneReader reader, int maximum)
    {
        // FUN_3938FE70: fixed native capacities, each list has its own byte count.
        var count = reader.ReadByte();
        if (count > maximum)
            throw new InvalidDataException($"Unit buff list exceeds the native {maximum}-entry capacity.");
        var buffs = new UnitBuff[count];
        for (var i = 0; i < buffs.Length; i++)
            buffs[i] = UnitBuff.Read(ref reader);
        return Array.AsReadOnly(buffs);
    }
}
