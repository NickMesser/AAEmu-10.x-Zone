using System.Numerics;

namespace AAEmu.Zone.Protocol.Messages;

/// <summary>
/// WZActivateNpcSpawnersInArea (0x0042, native <c>sub_39363850</c>): f32 x, f32 y, f32 z, f32 radius,
/// u8 activate; 17 body bytes. Verified against the 14 native-baseline bodies.
/// </summary>
public readonly record struct ActivateNpcSpawnersInArea(Vector3 Center, float Radius, bool Activate)
{
    public static ActivateNpcSpawnersInArea Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var center = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        var value = new ActivateNpcSpawnersInArea(center, reader.ReadSingle(), reader.ReadBoolean());
        reader.EnsureFullyConsumed();
        return value;
    }

    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter();
        writer.WriteSingle(Center.X); writer.WriteSingle(Center.Y); writer.WriteSingle(Center.Z);
        writer.WriteSingle(Radius); writer.WriteBoolean(Activate);
        return new(WzOpcodes.ActivateNpcSpawnersInArea, writer.ToArray());
    }
}

/// <summary>WZUnitFlyingStateChanged (0x0036, native <c>sub_393636A0</c>): object bc and bool, four body bytes.</summary>
public readonly record struct UnitFlyingStateChanged(uint ObjectId, bool Flying)
{
    public static UnitFlyingStateChanged Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var value = new UnitFlyingStateChanged(reader.ReadBc(), reader.ReadBoolean());
        reader.EnsureFullyConsumed();
        return value;
    }

    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter(); writer.WriteBc(ObjectId); writer.WriteBoolean(Flying);
        return new(WzOpcodes.UnitFlyingStateChanged, writer.ToArray());
    }
}
