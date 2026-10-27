namespace AAEmu.Zone.Protocol.Units;

public sealed record UnitAttachment(sbyte Point, uint ParentId)
{
    internal static UnitAttachment Read(ref ZoneReader reader)
    {
        var point = reader.ReadSByte();
        return new(point, point == -1 ? 0 : reader.ReadBc());
    }
}

public sealed record UnitBonding(sbyte Point, uint DoodadId = 0, int Space = 0, int Spot = 0, uint Kind = 0)
{
    internal static UnitBonding Read(ref ZoneReader reader)
    {
        var point = reader.ReadSByte();
        return point == -1 ? new(point) : new(point, reader.ReadBc(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadUInt32());
    }
}

public sealed record FarmFieldPosture(uint Type, float GrowRate, uint RandomSeed, byte Flags);
public sealed record TurretPosture(float Pitch, float Yaw);
public sealed record ActorPosture(uint AnimationAction, bool Activate);

public sealed record UnitPosture(byte Type, bool Looted, byte HouseOpenings = 0, ActorPosture? Actor = null,
    FarmFieldPosture? FarmField = null, TurretPosture? Turret = null)
{
    internal static UnitPosture Read(ref ZoneReader reader)
    {
        // FUN_39387550: only discriminators 1/4/7/8 append data. Others carry just type/looted.
        var type = reader.ReadByte();
        var looted = reader.ReadBoolean();
        return type switch
        {
            1 => new(type, looted, HouseOpenings: reader.ReadByte()),
            4 => new(type, looted, Actor: new(reader.ReadUInt32(), reader.ReadBoolean())),
            7 => new(type, looted, FarmField: new(reader.ReadUInt32(), reader.ReadSingle(), reader.ReadUInt32(), reader.ReadByte())),
            8 => new(type, looted, Turret: new(reader.ReadSingle(), reader.ReadSingle())),
            _ => new(type, looted)
        };
    }
}

public sealed record UnitFirstHitter(uint ObjectId, uint TeamId);
public sealed record UnitAction(int State, uint Time = 0, uint Duration = 0, float MaxHorizontalVelocity = 0, float MaxVerticalVelocity = 0)
{
    internal static UnitAction Read(ref ZoneReader reader)
    {
        // WZ envelope 39394CC0 and nested 3937E8B0; zero is the no-action sentinel.
        var state = reader.ReadInt32();
        return state == 0 ? new(0) : new(state, reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadSingle(), reader.ReadSingle());
    }
}
