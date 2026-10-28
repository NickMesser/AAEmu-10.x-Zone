namespace AAEmu.Zone.Protocol.Messages;

/// <summary>Signed native cooldown adjustment: percent takes precedence over milliseconds; count is used only for charges.</summary>
public readonly record struct SkillCooldownAdjustment(int Percent, int Count, int Milliseconds)
{
    internal static SkillCooldownAdjustment Read(ref ZoneReader reader) =>
        new(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
}

/// <summary>WZ 0x39, native 39364D00: reset one skill, one tag, and the explicitly selected related timers.</summary>
public readonly record struct SkillCooldownReset(uint ObjectId, uint SkillId, uint TagId, bool GlobalCooldown,
    bool SkillTags, bool TaggedSkills, bool TaggedSkillTags)
{
    public static SkillCooldownReset Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var value = new SkillCooldownReset(reader.ReadBc(), reader.ReadUInt32(), reader.ReadUInt32(),
            reader.ReadBoolean(), reader.ReadBoolean(), reader.ReadBoolean(), reader.ReadBoolean());
        reader.EnsureFullyConsumed();
        return value;
    }
}

/// <summary>WZ 0x86, native 393652F0: adjust skill/tag timers, optionally including related tags and tagged skills.</summary>
public readonly record struct SkillCooldownReduce(uint ObjectId, uint SkillId, uint TagId, SkillCooldownAdjustment Adjustment,
    bool SkillTags, bool TaggedSkills, bool TaggedSkillTags)
{
    public static SkillCooldownReduce Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var value = new SkillCooldownReduce(reader.ReadBc(), reader.ReadUInt32(), reader.ReadUInt32(),
            SkillCooldownAdjustment.Read(ref reader), reader.ReadBoolean(), reader.ReadBoolean(), reader.ReadBoolean());
        reader.EnsureFullyConsumed();
        return value;
    }
}

/// <summary>WZ 0x88, native 393653F0: adjust an existing charge-recovery timer for a content skill.</summary>
public readonly record struct ChargeSkillCooldownChange(uint ObjectId, uint SkillId, SkillCooldownAdjustment Adjustment)
{
    public static ChargeSkillCooldownChange Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var value = new ChargeSkillCooldownChange(reader.ReadBc(), reader.ReadUInt32(), SkillCooldownAdjustment.Read(ref reader));
        reader.EnsureFullyConsumed();
        return value;
    }
}
