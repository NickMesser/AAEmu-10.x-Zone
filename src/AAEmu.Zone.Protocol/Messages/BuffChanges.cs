using AAEmu.Zone.Protocol.Units;

namespace AAEmu.Zone.Protocol.Messages;

/// <summary>WZBuffCreated: 3936DC70 + BuffData 3938F7F0. Times are normalized to milliseconds.</summary>
public sealed record BuffCreated(UnitSkillCaster Caster, long CasterPersistentId, uint TargetId, uint Index,
    uint TemplateId, byte CasterLevel, ushort CasterAbilityLevel, uint SourceSkill, uint Stack,
    uint Charge, uint Duration, uint Elapsed, uint Tick)
{
    public static BuffCreated Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var caster = UnitSkillCaster.Read(ref reader);
        var persistent = reader.ReadInt64();
        var target = reader.ReadBc();
        var index = reader.ReadUInt32();
        var template = reader.ReadUInt32();
        var level = reader.ReadByte();
        var ability = reader.ReadUInt16();
        var skill = reader.ReadUInt32();
        var stack = reader.ReadUInt32();
        var timing = reader.ReadPisc(4);
        reader.EnsureFullyConsumed();
        return new(caster, persistent, target, index, template, level, ability, skill, stack,
            timing[0], unchecked(timing[1] * 10), unchecked(timing[2] * 10), unchecked(timing[3] * 10));
    }

    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter();
        Caster.Write(writer);
        writer.WriteInt64(CasterPersistentId);
        writer.WriteBc(TargetId);
        writer.WriteUInt32(Index);
        writer.WriteUInt32(TemplateId);
        writer.WriteByte(CasterLevel);
        writer.WriteUInt16(CasterAbilityLevel);
        writer.WriteUInt32(SourceSkill);
        writer.WriteUInt32(Stack);
        writer.WritePisc(Charge, Duration / 10, Elapsed / 10, Tick / 10);
        return new(WzOpcodes.BuffCreated, writer.ToArray());
    }

    public UnitBuff ToUnitBuff() => new(Index, Caster, CasterPersistentId, CasterLevel, CasterAbilityLevel,
        Duration, Elapsed, Tick, Tick == 0 ? 0 : Elapsed / Tick, TemplateId, Stack, Charge, SourceSkill);
}

/// <summary>WZBuffRemoved: 3936C130. Index identifies an instance, not a buff template.</summary>
public readonly record struct BuffRemoved(uint TargetId, uint Index)
{
    public static BuffRemoved Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var value = new BuffRemoved(reader.ReadBc(), reader.ReadUInt32());
        reader.EnsureFullyConsumed();
        return value;
    }

    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter();
        writer.WriteBc(TargetId);
        writer.WriteUInt32(Index);
        return new(WzOpcodes.BuffRemoved, writer.ToArray());
    }
}

/// <summary>WZBuffUpdated: 3936C1D0. Unlike creation, elapsed time is an unscaled i32 bit pattern.</summary>
public readonly record struct BuffUpdated(uint TargetId, uint Index, uint Stack, uint Charge, uint Elapsed, byte Reason)
{
    public static BuffUpdated Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var value = new BuffUpdated(reader.ReadBc(), reader.ReadUInt32(), reader.ReadUInt32(),
            reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadByte());
        reader.EnsureFullyConsumed();
        return value;
    }

    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter();
        writer.WriteBc(TargetId);
        writer.WriteUInt32(Index);
        writer.WriteUInt32(Stack);
        writer.WriteUInt32(Charge);
        writer.WriteUInt32(Elapsed);
        writer.WriteByte(Reason);
        return new(WzOpcodes.BuffUpdated, writer.ToArray());
    }
}
