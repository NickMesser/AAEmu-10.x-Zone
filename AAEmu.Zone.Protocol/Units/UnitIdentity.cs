namespace AAEmu.Zone.Protocol.Units;

public enum ZoneUnitKind : byte
{
    Character, Npc, Slave, Housing, Transfer, Mate, Shipyard, Reserved7
}

/// <summary>Native UnitIdentity union, FUN_39387CA0. IDs retain their distinct wire domains.</summary>
public sealed record UnitIdentity(
    ZoneUnitKind Kind, long PersistentId = 0, ulong CharacterValue = 0, uint NpcObjectId = 0,
    uint TemplateId = 0, long OwnerId = 0, ushort Timeline = 0, byte OwnerWorldId = 0,
    ushort CompletedBuildSteps = 0, byte NpcFlags = 0)
{
    internal static UnitIdentity Read(ref ZoneReader reader)
    {
        var kind = (ZoneUnitKind)reader.ReadByte();
        return kind switch
        {
            ZoneUnitKind.Character => new(kind, PersistentId: reader.ReadInt64(), CharacterValue: reader.ReadUInt64()),
            ZoneUnitKind.Npc => new(kind, NpcObjectId: reader.ReadBc(), TemplateId: reader.ReadUInt32(),
                OwnerId: reader.ReadInt64(), NpcFlags: reader.ReadByte()),
            ZoneUnitKind.Slave => new(kind, PersistentId: reader.ReadInt64(), Timeline: reader.ReadUInt16(),
                TemplateId: reader.ReadUInt32(), OwnerId: reader.ReadInt64(), OwnerWorldId: reader.ReadByte()),
            ZoneUnitKind.Housing => new(kind, Timeline: reader.ReadUInt16(), TemplateId: reader.ReadUInt32(),
                CompletedBuildSteps: reader.ReadUInt16()),
            ZoneUnitKind.Transfer => new(kind, Timeline: reader.ReadUInt16(), TemplateId: reader.ReadUInt32()),
            ZoneUnitKind.Mate => new(kind, Timeline: reader.ReadUInt16(), TemplateId: reader.ReadUInt32(), OwnerId: reader.ReadInt64()),
            ZoneUnitKind.Shipyard => new(kind, PersistentId: reader.ReadInt64(), TemplateId: reader.ReadUInt32()),
            ZoneUnitKind.Reserved7 => new(kind, PersistentId: reader.ReadInt64(), Timeline: reader.ReadUInt16()),
            _ => throw new InvalidDataException($"Unsupported native unit identity {kind}.")
        };
    }

    public void Write(ZoneWriter writer)
    {
        writer.WriteByte((byte)Kind);
        switch (Kind)
        {
            case ZoneUnitKind.Character:
                writer.WriteInt64(PersistentId); writer.WriteUInt64(CharacterValue);
                break;
            case ZoneUnitKind.Npc:
                writer.WriteBc(NpcObjectId); writer.WriteUInt32(TemplateId);
                writer.WriteInt64(OwnerId); writer.WriteByte(NpcFlags);
                break;
            case ZoneUnitKind.Slave:
                writer.WriteInt64(PersistentId); writer.WriteUInt16(Timeline); writer.WriteUInt32(TemplateId);
                writer.WriteInt64(OwnerId); writer.WriteByte(OwnerWorldId);
                break;
            case ZoneUnitKind.Housing:
                writer.WriteUInt16(Timeline); writer.WriteUInt32(TemplateId); writer.WriteUInt16(CompletedBuildSteps);
                break;
            case ZoneUnitKind.Transfer:
                writer.WriteUInt16(Timeline); writer.WriteUInt32(TemplateId);
                break;
            case ZoneUnitKind.Mate:
                writer.WriteUInt16(Timeline); writer.WriteUInt32(TemplateId); writer.WriteInt64(OwnerId);
                break;
            case ZoneUnitKind.Shipyard:
                writer.WriteInt64(PersistentId); writer.WriteUInt32(TemplateId);
                break;
            case ZoneUnitKind.Reserved7:
                writer.WriteInt64(PersistentId); writer.WriteUInt16(Timeline);
                break;
            default:
                throw new InvalidDataException($"Unsupported native unit identity {Kind}.");
        }
    }
}

/// <summary>
/// FUN_39B11410's 11-byte fixed-point representation. Keep quantized values intact instead of
/// rounding through the World helper's four-decimal display conversion.
/// </summary>
public readonly record struct UnitPosition(uint QuantizedX, uint QuantizedY, uint QuantizedZ, bool NegativeX, bool NegativeY)
{
    public long FixedX => unchecked(((long)(int)QuantizedX << 35) * (NegativeX ? -1 : 1));
    public long FixedY => unchecked(((long)(int)QuantizedY << 35) * (NegativeY ? -1 : 1));
    public double X => FixedX / 17592186044416.0;
    public double Y => FixedY / 17592186044416.0;
    // Native single-precision constants at 3A107B1C/4C/50/54/58 are
    // 1/4194304, 4194304, 4096, -100, 4196 respectively.
    public float Z => QuantizedZ * (1f / 4194304f) * 4196f - 100f;

    internal static UnitPosition Read(ref ZoneReader reader)
    {
        var x = reader.ReadUInt32();
        var y = reader.ReadUInt32();
        var lowZ = reader.ReadUInt16();
        var high = reader.ReadByte();
        return new(x, y, (uint)(lowZ | (high & 0x3F) << 16), (high & 0x80) != 0, (high & 0x40) != 0);
    }
}
