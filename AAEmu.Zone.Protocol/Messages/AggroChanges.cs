using AAEmu.Zone.Protocol.Units;

namespace AAEmu.Zone.Protocol.Messages;

/// <summary>3936DDC0: three unit object IDs, a raw aggro amount, hostility and the complete cast action.</summary>
public sealed record AggroUpdate(uint SkillTargetId, uint SourceId, uint UnitInChargeId, uint Amount,
    bool Hostile, UnitCastAction Action)
{
    public static AggroUpdate Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var message = new AggroUpdate(reader.ReadBc(), reader.ReadBc(), reader.ReadBc(), reader.ReadUInt32(),
            reader.ReadBoolean(), UnitCastAction.Read(ref reader));
        reader.EnsureFullyConsumed();
        return message;
    }

    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter();
        writer.WriteBc(SkillTargetId); writer.WriteBc(SourceId); writer.WriteBc(UnitInChargeId);
        writer.WriteUInt32(Amount); writer.WriteBoolean(Hostile); Action.Write(writer);
        return new(WzOpcodes.UpdateAggro, writer.ToArray());
    }
}

/// <summary>393685A0; handler 3936E200 indexes the NPC owner map directly by NPC identity ID.</summary>
public sealed record AggroReset(uint NpcId, int DamageSelector, int HealingSelector, int DirectSelector, int Value)
{
    public static AggroReset Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var message = new AggroReset(reader.ReadBc(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
        reader.EnsureFullyConsumed();
        return message;
    }

    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter(); writer.WriteBc(NpcId);
        writer.WriteInt32(DamageSelector); writer.WriteInt32(HealingSelector); writer.WriteInt32(DirectSelector); writer.WriteInt32(Value);
        return new(WzOpcodes.AggroReset, writer.ToArray());
    }
}

/// <summary>3936C3B0: remove a unit object ID from reverse NPC memberships without changing death state.</summary>
public readonly record struct FakeDeath(uint ObjectId)
{
    public static FakeDeath Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body); var message = new FakeDeath(reader.ReadBc());
        reader.EnsureFullyConsumed(); return message;
    }
    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter(); writer.WriteBc(ObjectId);
        return new(WzOpcodes.FakeDeath, writer.ToArray());
    }
}

/// <summary>393684D0/39364EF0: both fields are unit object IDs; identical IDs are a handler no-op.</summary>
public readonly record struct AggroCopy(uint SourceId, uint DestinationId)
{
    public static AggroCopy Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body); var message = new AggroCopy(reader.ReadBc(), reader.ReadBc());
        reader.EnsureFullyConsumed(); return message;
    }
    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter(); writer.WriteBc(SourceId); writer.WriteBc(DestinationId);
        return new(WzOpcodes.AggroCopy, writer.ToArray());
    }
}
