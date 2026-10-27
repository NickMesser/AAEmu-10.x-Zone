namespace AAEmu.Zone.Protocol.Messages;

/// <summary>3936A950 / 3936A970 carry the complete FactionDesc, including its owner and both timestamps.</summary>
public sealed record FactionCreated(ZoneFaction Faction)
{
    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter(); Faction.Write(writer);
        return new(WzOpcodes.FactionCreated, writer.ToArray());
    }

    public static FactionCreated Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body); var faction = ZoneFaction.Read(ref reader);
        reader.EnsureFullyConsumed(); return new(faction);
    }
}

public sealed record FactionSponsorChanged(ZoneFaction Faction)
{
    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter(); Faction.Write(writer);
        return new(WzOpcodes.FactionSponsorChanged, writer.ToArray());
    }

    public static FactionSponsorChanged Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body); var faction = ZoneFaction.Read(ref reader);
        reader.EnsureFullyConsumed(); return new(faction);
    }
}

public sealed record FactionDismissed(uint FactionId)
{
    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter(); writer.WriteUInt32(FactionId);
        return new(WzOpcodes.FactionDismissed, writer.ToArray());
    }

    public static FactionDismissed Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body); var message = new FactionDismissed(reader.ReadUInt32());
        reader.EnsureFullyConsumed(); return message;
    }
}

/// <summary>39366D20: two u32 IDs, two state bytes and two u64 timestamps.</summary>
public sealed record FactionRelationChanged(uint FirstId, uint SecondId, byte State, byte NextState, ulong UpdateTime, ulong ChangeTime)
{
    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter(); writer.WriteUInt32(FirstId); writer.WriteUInt32(SecondId);
        writer.WriteByte(State); writer.WriteByte(NextState); writer.WriteUInt64(UpdateTime); writer.WriteUInt64(ChangeTime);
        return new(WzOpcodes.FactionSetRelationState, writer.ToArray());
    }

    public static FactionRelationChanged Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var message = new FactionRelationChanged(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadByte(), reader.ReadByte(),
            reader.ReadUInt64(), reader.ReadUInt64());
        reader.EnsureFullyConsumed(); return message;
    }
}

/// <summary>3936B8C0: object ID is bc; old and new faction IDs are independent u32 values.</summary>
public sealed record UnitFactionChanged(uint ObjectId, uint OldFactionId, uint NewFactionId, bool Temporary)
{
    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter(); writer.WriteBc(ObjectId); writer.WriteUInt32(OldFactionId);
        writer.WriteUInt32(NewFactionId); writer.WriteBoolean(Temporary);
        return new(WzOpcodes.UnitFactionChanged, writer.ToArray());
    }

    public static UnitFactionChanged Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var message = new UnitFactionChanged(reader.ReadBc(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadBoolean());
        reader.EnsureFullyConsumed(); return message;
    }
}

public sealed record UnitExpeditionChanged(uint ObjectId, uint OldExpeditionId, uint NewExpeditionId)
{
    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter(); writer.WriteBc(ObjectId); writer.WriteUInt32(OldExpeditionId); writer.WriteUInt32(NewExpeditionId);
        return new(WzOpcodes.UnitExpeditionChanged, writer.ToArray());
    }

    public static UnitExpeditionChanged Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var message = new UnitExpeditionChanged(reader.ReadBc(), reader.ReadUInt32(), reader.ReadUInt32());
        reader.EnsureFullyConsumed(); return message;
    }
}

public sealed record ExpeditionWarState(uint FirstId, uint SecondId, bool Started)
{
    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter(); writer.WriteUInt32(FirstId); writer.WriteUInt32(SecondId); writer.WriteBoolean(Started);
        return new(WzOpcodes.ExpeditionWarState, writer.ToArray());
    }

    public static ExpeditionWarState Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body); var message = new ExpeditionWarState(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadBoolean());
        reader.EnsureFullyConsumed(); return message;
    }
}

public sealed record FactionIndependence(uint OldFactionId, uint NewFactionId, string Name, ulong CreatedTime)
{
    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter(); writer.WriteUInt32(OldFactionId); writer.WriteUInt32(NewFactionId);
        writer.WriteString(Name, 128); writer.WriteUInt64(CreatedTime);
        return new(WzOpcodes.FactionIndependence, writer.ToArray());
    }

    public static FactionIndependence Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var message = new FactionIndependence(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadString(128), reader.ReadUInt64());
        reader.EnsureFullyConsumed(); return message;
    }
}
