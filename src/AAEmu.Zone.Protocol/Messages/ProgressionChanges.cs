namespace AAEmu.Zone.Protocol.Messages;

/// <summary>WZBuffLearned (3936C7B0): passive definition ID, not its associated buff template.</summary>
public readonly record struct BuffLearned(uint ObjectId, uint PassiveId)
{
    public static BuffLearned Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var value = new BuffLearned(reader.ReadBc(), reader.ReadUInt32());
        reader.EnsureFullyConsumed();
        return value;
    }
    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter();
        writer.WriteBc(ObjectId); writer.WriteUInt32(PassiveId);
        return new(WzOpcodes.BuffLearned, writer.ToArray());
    }
}

/// <summary>WZSkillsReset (3936C870): remove skills and passives belonging to this ability.</summary>
public readonly record struct SkillsReset(uint ObjectId, byte AbilityId)
{
    public static SkillsReset Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var value = new SkillsReset(reader.ReadBc(), reader.ReadByte());
        reader.EnsureFullyConsumed();
        return value;
    }
    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter();
        writer.WriteBc(ObjectId); writer.WriteByte(AbilityId);
        return new(WzOpcodes.SkillsReset, writer.ToArray());
    }
}

/// <summary>WZLevelChanged (3936C920): primary level only.</summary>
public readonly record struct LevelChanged(uint ObjectId, byte Level)
{
    public static LevelChanged Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var value = new LevelChanged(reader.ReadBc(), reader.ReadByte());
        reader.EnsureFullyConsumed();
        return value;
    }
    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter();
        writer.WriteBc(ObjectId); writer.WriteByte(Level);
        return new(WzOpcodes.LevelChanged, writer.ToArray());
    }
}

/// <summary>WZAddSwapPassiveBuffs (3936C990): linked passive additions during an active grant.</summary>
public readonly record struct AddSwapPassiveBuffs(uint ObjectId, uint BuffTemplateId)
{
    public static AddSwapPassiveBuffs Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var value = new AddSwapPassiveBuffs(reader.ReadBc(), reader.ReadUInt32());
        reader.EnsureFullyConsumed();
        return value;
    }
    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter();
        writer.WriteBc(ObjectId); writer.WriteUInt32(BuffTemplateId);
        return new(WzOpcodes.AddSwapPassiveBuffs, writer.ToArray());
    }
}
