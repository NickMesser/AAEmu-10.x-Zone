using AAEmu.Zone.Protocol.Units;

namespace AAEmu.Zone.Protocol.Messages;

/// <summary>
/// Skill caster union used by the skill packets: type byte, bc object id, then a per-type tail
/// (Item: u64 item id, u32 template, u8, u64; Mount: u32 mount skill template; Unit/Unk1/Doodad: none).
/// Types above 4 are rejected because the tail length is unknown.
/// </summary>
public readonly record struct SkillCasterRef(byte Type, uint ObjectId, ulong ItemId, uint Template, byte ItemType1, ulong ItemType2)
{
    internal static SkillCasterRef Read(ref ZoneReader reader)
    {
        var type = reader.ReadByte();
        var objectId = reader.ReadBc();
        return type switch
        {
            0 or 1 or 4 => new(type, objectId, 0, 0, 0, 0),
            2 => new(type, objectId, reader.ReadUInt64(), reader.ReadUInt32(), reader.ReadByte(), reader.ReadUInt64()),
            3 => new(type, objectId, 0, reader.ReadUInt32(), 0, 0),
            _ => throw new InvalidDataException($"Unknown skill caster type {type}.")
        };
    }
}

/// <summary>Skill target union: Unit(0)/Doodad(4) carry a bc id; Position(1), Position2(2), Item(3) and Position3(5) as in World <c>SkillCastTarget</c>.</summary>
public readonly record struct SkillTargetRef(byte Type, uint ObjectId)
{
    internal static SkillTargetRef Read(ref ZoneReader reader)
    {
        var type = reader.ReadByte();
        switch (type)
        {
            case 0 or 4:
                return new(type, reader.ReadBc());
            case 1: // x i64, y i64, z f32, rot f32, three bc ids
                reader.ReadBytes(24);
                var id = reader.ReadBc(); reader.ReadBc(); reader.ReadBc();
                return new(type, id);
            case 2: // x i64, y i64, z f32, end xyz, normal xyz
                reader.ReadBytes(8 + 8 + 4 + 12 + 12);
                return new(type, 0);
            case 3: // bc, u64 item id, u32, u8
                var item = reader.ReadBc(); reader.ReadBytes(8 + 4 + 1);
                return new(type, item);
            case 5: // x i64, y i64, z f32, pitch f32
                reader.ReadBytes(24);
                return new(type, 0);
            default:
                throw new InvalidDataException($"Unknown skill target type {type}.");
        }
    }
}

/// <summary>Copied native result tail: byte +0, uint16 +2, uint32 +4, bool +8. Code zero is success.</summary>
public readonly record struct SkillCastResult(byte Code, ushort Detail, uint Parameter, bool Flag)
{
    public static SkillCastResult Success => new(0, 0, 0, true);
}

internal static class SkillWire
{
    /// <summary>World WriteSkillCastTail: flag byte, then u8 (bit0), u16 (bit1), u32 (bit2), bool (bit3).</summary>
    internal static SkillCastResult ReadCastTail(ref ZoneReader reader)
    {
        var flags = reader.ReadByte();
        return new((flags & 1) != 0 ? reader.ReadByte() : (byte)0,
            (flags & 2) != 0 ? reader.ReadUInt16() : (ushort)0,
            (flags & 4) != 0 ? reader.ReadUInt32() : 0,
            (flags & 8) == 0 || reader.ReadBoolean());
    }
}

/// <summary>WZ SkillStarted 0x002B (native 39363460 -> StartSkill 39456D70): skill id, timeline, caster, target, cast time, cast tail.</summary>
public readonly record struct SkillStarted(uint SkillId, ushort Timeline, SkillCasterRef Caster, SkillTargetRef Target, uint CastTime)
{
    public SkillCastResult Result { get; init; } = SkillCastResult.Success;
    public static SkillStarted Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var value = new SkillStarted(reader.ReadUInt32(), reader.ReadUInt16(), SkillCasterRef.Read(ref reader), SkillTargetRef.Read(ref reader), reader.ReadUInt32());
        value = value with { Result = SkillWire.ReadCastTail(ref reader) };
        reader.EnsureFullyConsumed();
        return value;
    }
}

/// <summary>WZ SkillFired 0x002C (native 393634B0 -> FireSkill 39457170): skill id, timeline, caster, target, cast tail, trailing bool.</summary>
public readonly record struct SkillFired(uint SkillId, ushort Timeline, SkillCasterRef Caster, SkillTargetRef Target, bool Flag)
{
    public SkillCastResult Result { get; init; } = SkillCastResult.Success;
    public static SkillFired Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var skillId = reader.ReadUInt32();
        var timeline = reader.ReadUInt16();
        var caster = SkillCasterRef.Read(ref reader);
        var target = SkillTargetRef.Read(ref reader);
        var result = SkillWire.ReadCastTail(ref reader);
        var value = new SkillFired(skillId, timeline, caster, target, reader.ReadBoolean()) { Result = result };
        reader.EnsureFullyConsumed();
        return value;
    }
}

/// <summary>WZ SkillEnded 0x002D (native 39363510 -> EndSkill 39456B20): u16 timeline and caster.</summary>
public readonly record struct SkillEnded(ushort Timeline, SkillCasterRef Caster)
{
    public static SkillEnded Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var value = new SkillEnded(reader.ReadUInt16(), SkillCasterRef.Read(ref reader));
        reader.EnsureFullyConsumed();
        return value;
    }
}

/// <summary>WZ SkillStopped 0x002E (native 39363530 -> 39456600): bc unit, i32 skill type (not a timeline).</summary>
public readonly record struct SkillStopped(uint ObjectId, int SkillId)
{
    /// <summary>Legacy misnomer. The wire member is skillType/type, and native compares it to skill type 2.</summary>
    public int Timeline => SkillId;
    public static SkillStopped Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var value = new SkillStopped(reader.ReadBc(), reader.ReadInt32());
        reader.EnsureFullyConsumed();
        return value;
    }
}

/// <summary>WZ CastingStopped 0x002F (native 39363550 -> 39456630): bc unit, i16 timeline, i32 type (skill id), i32 duration.</summary>
public readonly record struct CastingStopped(uint ObjectId, short Timeline, int SkillId, int Duration)
{
    /// <summary>Native initializes a uint32 to zero and reads uint16 into it; the wire bits are not sign-extended.</summary>
    public uint TimelineId => unchecked((ushort)Timeline);
    public static CastingStopped Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var value = new CastingStopped(reader.ReadBc(), reader.ReadInt16(), reader.ReadInt32(), reader.ReadInt32());
        reader.EnsureFullyConsumed();
        return value;
    }
}

/// <summary>Plot object union: Unit(1) is a bc id; Position(2) is two poses (11-byte position + 3 sbyte axis-angle each) and three bc ids.</summary>
public readonly record struct PlotObjectRef(byte Type, uint ObjectId)
{
    internal static PlotObjectRef Read(ref ZoneReader reader)
    {
        var type = reader.ReadByte();
        switch (type)
        {
            case 1:
                return new(type, reader.ReadBc());
            case 2:
                reader.ReadBytes(2 * (11 + 3));
                var id = reader.ReadBc(); reader.ReadBc(); reader.ReadBc();
                return new(type, id);
            default:
                throw new InvalidDataException($"Unknown plot object type {type}.");
        }
    }
}

/// <summary>WZ PlotEvent 0x003A (native 393636D0 -> ZonePlotMan::PlayEvent 39455730).</summary>
public readonly record struct PlotEvent(
    ushort Timeline, uint EventId, uint SkillId, PlotObjectRef Caster, PlotObjectRef Target, ulong ItemId, uint ObjectId,
    uint CastTimeMs, uint ChannelingTimeMs, bool ConditionOk, bool Last, uint[] TargetUnitIds)
{
    /// <summary>The second bc ID before channeling time; native retains it separately from ObjectId.</summary>
    public uint ChannelingObjectId { get; init; }
    public static PlotEvent Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var timeline = reader.ReadUInt16();
        var eventId = reader.ReadUInt32();
        var skillId = reader.ReadUInt32();
        var caster = PlotObjectRef.Read(ref reader);
        var target = PlotObjectRef.Read(ref reader);
        var itemId = reader.ReadUInt64();
        var objectId = reader.ReadBc();
        var castTime = reader.ReadUInt32();
        var channelingObjectId = reader.ReadBc();
        var channeling = reader.ReadUInt32();
        var condition = reader.ReadBoolean();
        var last = reader.ReadBoolean();
        var count = reader.ReadCount(3);
        var targets = new uint[count];
        for (var i = 0; i < count; i++) targets[i] = reader.ReadBc();
        reader.EnsureFullyConsumed();
        return new(timeline, eventId, skillId, caster, target, itemId, objectId, castTime, channeling, condition, last, targets)
            { ChannelingObjectId = channelingObjectId };
    }
}

/// <summary>WZ PlotEnded 0x003B (native 39363760 -> 39455D20): i16 timeline.</summary>
public readonly record struct PlotEnded(short Timeline)
{
    public static PlotEnded Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var value = new PlotEnded(reader.ReadInt16());
        reader.EnsureFullyConsumed();
        return value;
    }
}

/// <summary>WZ PlotCastingStopped 0x003C / PlotChannelingStopped 0x003D (native 39363780 / 393637B0): i16 timeline, i32 duration, bool last event.</summary>
public readonly record struct PlotStopped(short Timeline, int Duration, bool LastEvent)
{
    public static PlotStopped Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var value = new PlotStopped(reader.ReadInt16(), reader.ReadInt32(), reader.ReadBoolean());
        reader.EnsureFullyConsumed();
        return value;
    }
}

/// <summary>WZ UnitHealed 0x0031 (native 393635B0 -> 394576B0): cast action, caster, bc target, heal type/hit type, amount, overflow, crit, element, result, bc unit in charge.</summary>
public readonly record struct UnitHealed(
    UnitCastAction Action, SkillCasterRef Caster, uint TargetId, byte HealType, byte HitType, long Amount, long Overflow,
    bool Critical, uint ElementHeal, bool ShowElementEffect, uint ElementType, byte Result, uint UnitInCharge)
{
    public static UnitHealed Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var action = UnitCastAction.Read(ref reader);
        var caster = SkillCasterRef.Read(ref reader);
        var value = new UnitHealed(action, caster, reader.ReadBc(), reader.ReadByte(), reader.ReadByte(), reader.ReadInt64(), reader.ReadInt64(),
            reader.ReadBoolean(), reader.ReadUInt32(), reader.ReadBoolean(), reader.ReadUInt32(), reader.ReadByte(), reader.ReadBc());
        reader.EnsureFullyConsumed();
        return value;
    }
}

/// <summary>
/// WZ UnitDamaged 0x0030 (native 39363580 -> 393225F0). Not present in the native capture; layout from
/// World UnitDamagedWire plus trailing f32. A set debug bit (flag 0x10) is rejected because its block is unrecovered.
/// </summary>
public readonly record struct UnitDamaged(
    UnitCastAction Action, SkillCasterRef Caster, uint CasterId, uint TargetId, byte CrimeState, uint Damage, uint Absorbed, uint ManaBurn,
    byte HoldableId, uint ElementDamage, bool ShowElementEffect, uint ElementType, ushort HitInfo, byte Flag, byte Result, float Am)
{
    public static UnitDamaged Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var action = UnitCastAction.Read(ref reader);
        var caster = SkillCasterRef.Read(ref reader);
        var casterId = reader.ReadBc();
        var targetId = reader.ReadBc();
        var crime = reader.ReadByte();
        var first = reader.ReadPisc(2);
        var second = reader.ReadPisc(3);
        var holdable = reader.ReadByte();
        var element = reader.ReadUInt32();
        var show = reader.ReadBoolean();
        var elementType = reader.ReadUInt32();
        var hit = reader.ReadUInt16();
        var flag = reader.ReadByte();
        var result = reader.ReadByte();
        if ((flag & 0x10) != 0) throw new InvalidDataException("UnitDamaged debug block is not supported.");
        var value = new UnitDamaged(action, caster, casterId, targetId, crime, first[0], first[1], second[2], holdable, element, show, elementType, hit, flag, result, reader.ReadSingle());
        reader.EnsureFullyConsumed();
        return value;
    }
}
