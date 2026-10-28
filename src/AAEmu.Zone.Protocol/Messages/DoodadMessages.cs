using System.Numerics;

namespace AAEmu.Zone.Protocol.Messages;

/// <summary>
/// WZCreateDoodad (0x0073). Native handler <c>sub_393641A0</c> passes the decoded packet body (+16) to
/// the doodad manager <c>sub_39453AD0</c>. Field order follows World's <c>WZCreateDoodadPacket</c> and
/// is verified against the 4536 native-baseline bodies (all 115 bytes): bc objId, pisc[4]
/// {designId, modelId, backpackItemId, field30}, u8 flags, bc ownerObjId, bc parentObjId, u8 attachPoint,
/// 11-byte position, s16 roll/pitch/yaw, f32 scale, s64 ownerId, s64 itemTemplateId, u32 funcGroupId,
/// u32 growingTime, u64 plantTime, s32 family, s32 puzzleGroup, u8 ownerType, u32 ownerDbId, s32 data,
/// s32 data2, u64 updatedTime, [goods freshness: s64 + s64 + u16], s64 type6, s64 type7.
/// </summary>
public sealed record DoodadCreate(uint ObjectId, uint TemplateId, uint ModelId, uint BackpackItemId, uint Field30,
    byte Flags, uint OwnerObjectId, uint ParentObjectId, byte AttachPoint, Vector3 Position,
    short Roll, short Pitch, short Yaw, float Scale, long OwnerId, long ItemTemplateId, uint FuncGroupId,
    uint GrowingTime, ulong PlantTime, int Family, int PuzzleGroup, byte OwnerType, uint OwnerDbId,
    int Data, int Data2, ulong UpdatedTime, DoodadFreshness? Freshness, long Type6, long Type7)
{
    private const int FreshnessSize = sizeof(long) + sizeof(long) + sizeof(ushort);
    private const int TailSize = 2 * sizeof(long);

    public static DoodadCreate Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var objectId = reader.ReadBc();
        var pisc = reader.ReadPisc(4);
        var flags = reader.ReadByte();
        var ownerObject = reader.ReadBc();
        var parent = reader.ReadBc();
        var attach = reader.ReadByte();
        var position = reader.ReadPosition();
        var roll = reader.ReadInt16();
        var pitch = reader.ReadInt16();
        var yaw = reader.ReadInt16();
        var scale = reader.ReadSingle();
        var ownerId = reader.ReadInt64();
        var itemTemplate = reader.ReadInt64();
        var funcGroup = reader.ReadUInt32();
        var growing = reader.ReadUInt32();
        var plant = reader.ReadUInt64();
        var family = reader.ReadInt32();
        var puzzle = reader.ReadInt32();
        var ownerType = reader.ReadByte();
        var ownerDb = reader.ReadUInt32();
        var data = reader.ReadInt32();
        var data2 = reader.ReadInt32();
        var updated = reader.ReadUInt64();
        // World appends the freshness block only for goods/trade-goods backpacks, decided by an item
        // lookup the zone cannot repeat from the body alone; the remaining length is unambiguous.
        DoodadFreshness? freshness = null;
        if (reader.Remaining == FreshnessSize + TailSize)
            freshness = new(reader.ReadInt64(), reader.ReadInt64(), reader.ReadUInt16());
        var type6 = reader.ReadInt64();
        var type7 = reader.ReadInt64();
        reader.EnsureFullyConsumed();
        return new(objectId, pisc[0], pisc[1], pisc[2], pisc[3], flags, ownerObject, parent, attach, position,
            roll, pitch, yaw, scale, ownerId, itemTemplate, funcGroup, growing, plant, family, puzzle, ownerType,
            ownerDb, data, data2, updated, freshness, type6, type7);
    }
}

public readonly record struct DoodadFreshness(long FreshnessTime, long Unnamed1, ushort Unnamed2);

/// <summary>WZRemoveDoodad (0x0074); native <c>sub_393641D0</c> reads one u32 at +16.</summary>
public sealed record DoodadRemove(uint ObjectId)
{
    public static DoodadRemove Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var value = new DoodadRemove(reader.ReadBc());
        reader.EnsureFullyConsumed();
        return value;
    }

    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter();
        writer.WriteBc(ObjectId);
        return new(WzOpcodes.RemoveDoodad, writer.ToArray());
    }
}

/// <summary>WZDoodadChangePhase (0x0075); native <c>sub_393641F0</c> reads u32 objId (+16) and u32 funcGroup (+20).</summary>
public sealed record DoodadChangePhase(uint ObjectId, uint FuncGroupId)
{
    public static DoodadChangePhase Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var value = new DoodadChangePhase(reader.ReadBc(), reader.ReadUInt32());
        reader.EnsureFullyConsumed();
        return value;
    }

    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter();
        writer.WriteBc(ObjectId);
        writer.WriteUInt32(FuncGroupId);
        return new(WzOpcodes.DoodadChangePhase, writer.ToArray());
    }
}

/// <summary>WZVegetationCutdown (0x0076); native <c>sub_39364220</c> reads one u32 id at +16 (World writes it as a bc).</summary>
public sealed record VegetationCutdown(uint Id)
{
    public static VegetationCutdown Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var value = new VegetationCutdown(reader.ReadBc());
        reader.EnsureFullyConsumed();
        return value;
    }

    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter();
        writer.WriteBc(Id);
        return new(WzOpcodes.VegetationCutdown, writer.ToArray());
    }
}
