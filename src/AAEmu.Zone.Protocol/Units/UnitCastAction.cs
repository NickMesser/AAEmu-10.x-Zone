namespace AAEmu.Zone.Protocol.Units;

/// <summary>Native CastAction union, 3938DAF0. Distinct from the UnitState movement action.</summary>
public abstract record UnitCastAction(byte Kind)
{
    internal static UnitCastAction Read(ref ZoneReader reader) => reader.ReadByte() switch
    {
        0 => new UnitSkillCast(reader.ReadUInt32(), reader.ReadUInt16()),
        1 => new UnitPlotCast(reader.ReadUInt32(), reader.ReadUInt16(), reader.ReadUInt32(), reader.ReadUInt32()),
        2 => new UnitBuffCast(reader.ReadUInt32(), reader.ReadBc(), reader.ReadUInt32(), reader.ReadBoolean(), reader.ReadBoolean()),
        3 => new UnitBuffTargetCast(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadBc()),
        4 => new UnitDestroyTargetCast(reader.ReadUInt32(), reader.ReadBc()),
        var kind => new UnitUnknownCast(kind) // Native has no payload for other discriminators.
    };

    public void Write(ZoneWriter writer)
    {
        writer.WriteByte(Kind);
        switch (this)
        {
            case UnitSkillCast { Kind: 0 } cast:
                writer.WriteUInt32(cast.SkillId); writer.WriteUInt16(cast.Timeline);
                break;
            case UnitPlotCast { Kind: 1 } cast:
                writer.WriteUInt32(cast.PlotId); writer.WriteUInt16(cast.Timeline);
                writer.WriteUInt32(cast.EventId); writer.WriteUInt32(cast.SkillId);
                break;
            case UnitBuffCast { Kind: 2 } cast:
                writer.WriteUInt32(cast.BuffTemplateId); writer.WriteBc(cast.TargetId); writer.WriteUInt32(cast.BuffIndex);
                writer.WriteBoolean(cast.FirstFlag); writer.WriteBoolean(cast.SecondFlag);
                break;
            case UnitBuffTargetCast { Kind: 3 } cast:
                writer.WriteUInt32(cast.PrimaryId); writer.WriteUInt32(cast.BuffTemplateId); writer.WriteBc(cast.TargetId);
                break;
            case UnitDestroyTargetCast { Kind: 4 } cast:
                writer.WriteUInt32(cast.PrimaryId); writer.WriteBc(cast.TargetId);
                break;
            case UnitUnknownCast { Kind: > 4 }:
                break;
            default:
                throw new InvalidDataException("Cast action does not match its native union discriminator.");
        }
    }
}

public sealed record UnitSkillCast(uint SkillId, ushort Timeline) : UnitCastAction(0);
public sealed record UnitPlotCast(uint PlotId, ushort Timeline, uint EventId, uint SkillId) : UnitCastAction(1);
public sealed record UnitBuffCast(uint BuffTemplateId, uint TargetId, uint BuffIndex, bool FirstFlag, bool SecondFlag) : UnitCastAction(2);
public sealed record UnitBuffTargetCast(uint PrimaryId, uint BuffTemplateId, uint TargetId) : UnitCastAction(3);
public sealed record UnitDestroyTargetCast(uint PrimaryId, uint TargetId) : UnitCastAction(4);
public sealed record UnitUnknownCast(byte Discriminator) : UnitCastAction(Discriminator);
