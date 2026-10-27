using System.Numerics;
using AAEmu.Zone.Protocol.Units;

namespace AAEmu.Zone.Protocol.Messages;

/// <summary>3938DE50: only 1, 2, 3, 4, 6 and 9 append a union payload.</summary>
public sealed record NpcSpawnReason(byte Kind = 0, uint Id = 0, ushort Index = 0, UnitCastAction? Action = null)
{
    internal static NpcSpawnReason Read(ref ZoneReader reader) => reader.ReadByte() switch
    {
        1 => new(1, reader.ReadUInt32(), reader.ReadUInt16()),
        2 => new(2, reader.ReadUInt32()),
        4 => new(4, reader.ReadUInt32()),
        9 => new(9, reader.ReadUInt32()),
        3 => new(3, Action: UnitCastAction.Read(ref reader)),
        6 => new(6, Action: UnitCastAction.Read(ref reader)),
        var kind => new(kind)
    };

    internal void Write(ZoneWriter writer)
    {
        writer.WriteByte(Kind);
        switch (Kind)
        {
            case 1:
                writer.WriteUInt32(Id); writer.WriteUInt16(Index);
                break;
            case 2 or 4 or 9:
                writer.WriteUInt32(Id);
                break;
            case 3 or 6:
                (Action ?? throw new InvalidDataException("NPC spawn reason requires a cast action.")).Write(writer);
                break;
        }
    }
}

/// <summary>3938E040: the group ID is always present on the binary wire.</summary>
public readonly record struct NpcSpawnGroup(uint Type, uint Id, byte MemberIndex)
{
    internal static NpcSpawnGroup Read(ref ZoneReader reader) => new(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadByte());
    internal void Write(ZoneWriter writer)
    {
        writer.WriteUInt32(Type); writer.WriteUInt32(Id); writer.WriteByte(MemberIndex);
    }
}

/// <summary>ZWSpawnNpc and WZNpcSpawnFailed share 3938E0E0, including the variable creator/name/reason tail.</summary>
public sealed record NpcSpawnRequest(uint SpawnerId, uint SpawnerType, byte MemberIndex, byte PartIndex,
    ushort TableIndex, uint TemplateId, NpcSpawnGroup Group, Vector3 Position, float Yaw, float Scale,
    UnitIdentity Creator, string Name, string MasterName, uint FactionId, NpcSpawnReason Reason,
    bool DespawnOnCreatorDeath, bool UseSummonerAggroTarget, float LifeTime, bool IsFactionPermission)
{
    public static NpcSpawnRequest Read(ReadOnlySpan<byte> body)
    {
        NpcWire.CheckSize(body);
        var reader = new ZoneReader(body);
        var value = new NpcSpawnRequest(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadByte(), reader.ReadByte(),
            reader.ReadUInt16(), reader.ReadUInt32(), NpcSpawnGroup.Read(ref reader),
            new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()), reader.ReadSingle(), reader.ReadSingle(),
            UnitIdentity.Read(ref reader), reader.ReadString(1024), reader.ReadString(128), reader.ReadUInt32(),
            NpcSpawnReason.Read(ref reader), reader.ReadBoolean(), reader.ReadBoolean(), reader.ReadSingle(), reader.ReadBoolean());
        reader.EnsureFullyConsumed();
        value.Validate();
        return value;
    }

    public ZoneFrame ToFrame() => new(ZwOpcodes.SpawnNpc, WriteBody());
    public ZoneFrame ToFailureFrame() => new(WzOpcodes.NpcSpawnFailed, WriteBody());

    private byte[] WriteBody()
    {
        Validate();
        var writer = new ZoneWriter();
        writer.WriteUInt32(SpawnerId); writer.WriteUInt32(SpawnerType);
        writer.WriteByte(MemberIndex); writer.WriteByte(PartIndex); writer.WriteUInt16(TableIndex); writer.WriteUInt32(TemplateId);
        Group.Write(writer);
        writer.WriteSingle(Position.X); writer.WriteSingle(Position.Y); writer.WriteSingle(Position.Z);
        writer.WriteSingle(Yaw); writer.WriteSingle(Scale);
        Creator.Write(writer); writer.WriteString(Name, 1024); writer.WriteString(MasterName, 128); writer.WriteUInt32(FactionId);
        Reason.Write(writer); writer.WriteBoolean(DespawnOnCreatorDeath); writer.WriteBoolean(UseSummonerAggroTarget);
        writer.WriteSingle(LifeTime); writer.WriteBoolean(IsFactionPermission);
        return writer.ToArray();
    }

    private void Validate()
    {
        NpcWire.RequireFinite(Position.X, Position.Y, Position.Z, Yaw, Scale, LifeTime);
    }
}

public sealed record NpcStateMetadata(uint SpawnerId, byte MemberIndex, byte PartIndex, ushort TableIndex,
    UnitIdentity Creator, bool DespawnOnCreatorDeath, bool UseSummonerAggroTarget, float LifeTime,
    NpcSpawnReason Reason, float SpawningEffectTime, NpcSpawnGroup Group)
{
    public static NpcStateMetadata FromRequest(NpcSpawnRequest request, float spawningEffectTime = 0) =>
        new(request.SpawnerId, request.MemberIndex, request.PartIndex, request.TableIndex, request.Creator,
            request.DespawnOnCreatorDeath, request.UseSummonerAggroTarget, request.LifeTime, request.Reason,
            spawningEffectTime, request.Group);
}

/// <summary>393700B0: spawn metadata, shared unit state and buffs, effect time and group; no UnitAction.</summary>
public sealed record NpcStateSnapshot(NpcStateMetadata Metadata, UnitStateSnapshot Unit)
{
    public static NpcStateSnapshot Read(ReadOnlySpan<byte> body)
    {
        NpcWire.CheckSize(body);
        var reader = new ZoneReader(body);
        var spawner = reader.ReadUInt32(); var member = reader.ReadByte(); var part = reader.ReadByte(); var table = reader.ReadUInt16();
        var creator = UnitIdentity.Read(ref reader);
        var despawn = reader.ReadBoolean(); var useAggro = reader.ReadBoolean(); var lifeTime = reader.ReadSingle();
        var reason = NpcSpawnReason.Read(ref reader);
        var unit = UnitStateSnapshot.Read(ref reader, includeAction: false);
        var effectTime = reader.ReadSingle(); var group = NpcSpawnGroup.Read(ref reader);
        reader.EnsureFullyConsumed();
        NpcWire.RequireFinite(lifeTime, effectTime);
        if (unit.Identity.Kind != ZoneUnitKind.Npc)
            throw new InvalidDataException("WZNpcState requires an NPC unit identity.");
        return new(new(spawner, member, part, table, creator, despawn, useAggro, lifeTime, reason, effectTime, group), unit);
    }

    /// <summary>Wraps a complete shared unit-and-buffs body produced by World, validating the final envelope.</summary>
    public static ZoneFrame CreateFrame(NpcStateMetadata metadata, ReadOnlySpan<byte> unitStateAndBuffs)
    {
        var writer = new ZoneWriter();
        writer.WriteUInt32(metadata.SpawnerId); writer.WriteByte(metadata.MemberIndex); writer.WriteByte(metadata.PartIndex);
        writer.WriteUInt16(metadata.TableIndex); metadata.Creator.Write(writer);
        writer.WriteBoolean(metadata.DespawnOnCreatorDeath); writer.WriteBoolean(metadata.UseSummonerAggroTarget);
        writer.WriteSingle(metadata.LifeTime); metadata.Reason.Write(writer);
        writer.WriteBytes(unitStateAndBuffs); writer.WriteSingle(metadata.SpawningEffectTime); metadata.Group.Write(writer);
        var body = writer.ToArray();
        Read(body);
        return new(WzOpcodes.NpcState, body);
    }
}

/// <summary>3938DF30, wrapped by WZNpcSpawnerEvent 3936E0A0.</summary>
public sealed record NpcSpawnerEventMessage(uint SpawnerId, byte Event, NpcSpawnReason Reason, UnitIdentity Creator,
    uint FactionId, bool DespawnOnCreatorDeath, bool UseSummonerAggroTarget, float LifeTime)
{
    public static NpcSpawnerEventMessage Read(ReadOnlySpan<byte> body)
    {
        NpcWire.CheckSize(body);
        var reader = new ZoneReader(body);
        var value = new NpcSpawnerEventMessage(reader.ReadUInt32(), reader.ReadByte(), NpcSpawnReason.Read(ref reader),
            UnitIdentity.Read(ref reader), reader.ReadUInt32(), reader.ReadBoolean(), reader.ReadBoolean(), reader.ReadSingle());
        reader.EnsureFullyConsumed();
        NpcWire.RequireFinite(value.LifeTime);
        return value;
    }

    public ZoneFrame ToFrame()
    {
        NpcWire.RequireFinite(LifeTime);
        var writer = new ZoneWriter();
        writer.WriteUInt32(SpawnerId); writer.WriteByte(Event); Reason.Write(writer); Creator.Write(writer);
        writer.WriteUInt32(FactionId); writer.WriteBoolean(DespawnOnCreatorDeath); writer.WriteBoolean(UseSummonerAggroTarget);
        writer.WriteSingle(LifeTime);
        return new(WzOpcodes.NpcSpawnerEvent, writer.ToArray());
    }
}

internal static class NpcWire
{
    internal static void CheckSize(ReadOnlySpan<byte> body)
    {
        if (body.Length > ZoneFrame.MaxBodySize) throw new InvalidDataException("NPC message exceeds the native frame limit.");
    }

    // Replacement input validation; the native serializers themselves accept raw f32 values.
    internal static void RequireFinite(params ReadOnlySpan<float> values)
    {
        foreach (var value in values)
            if (!float.IsFinite(value)) throw new InvalidDataException("NPC message contains a non-finite position or timing value.");
    }
}
