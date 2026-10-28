namespace AAEmu.Zone.Protocol.Messages;

/// <summary>
/// Tower def identity shared by every TowerDef packet: i32 tower_defs.id then u16 zone group
/// (native handlers 39363FC0/39363FE0/39364000/39364020 pass <c>a2+16</c> as a {u32, u16} key).
/// </summary>
public readonly record struct TowerDefKey(uint TowerDefId, ushort ZoneGroup)
{
    internal static TowerDefKey Read(ref ZoneReader reader) => new(reader.ReadUInt32(), reader.ReadUInt16());
}

/// <summary>WZ TowerDefQueryPlayability 0x0066 (native 39363FC0 -> 393591D0): key only (6 bytes).</summary>
public readonly record struct TowerDefQueryPlayability(TowerDefKey Key)
{
    public static TowerDefQueryPlayability Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var message = new TowerDefQueryPlayability(TowerDefKey.Read(ref reader));
        reader.EnsureFullyConsumed();
        return message;
    }
}

/// <summary>WZ TowerDefStart 0x0067 / TowerDefEnd 0x0068 (native 39363FE0 / 39364000): key plus i32 spot index.</summary>
public readonly record struct TowerDefSpotMessage(TowerDefKey Key, int SpotIndex)
{
    public static TowerDefSpotMessage Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var message = new TowerDefSpotMessage(TowerDefKey.Read(ref reader), reader.ReadInt32());
        reader.EnsureFullyConsumed();
        return message;
    }
}

/// <summary>WZ TowerDefWaveStart 0x0069 (native 39364020): key, spot index and wave step.</summary>
public readonly record struct TowerDefWaveStart(TowerDefKey Key, int SpotIndex, int Step)
{
    public static TowerDefWaveStart Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var message = new TowerDefWaveStart(TowerDefKey.Read(ref reader), reader.ReadInt32(), reader.ReadInt32());
        reader.EnsureFullyConsumed();
        return message;
    }
}

/// <summary>WZ TowerDefReload 0x0065 (native 39363FA0): empty body.</summary>
public readonly record struct TowerDefReload
{
    public static TowerDefReload Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        reader.EnsureFullyConsumed();
        return default;
    }
}

/// <summary>ZW TowerDefReportPlayability 0x0032 (native packet id 50, built in 393591D0): key then u32 playable spot count.</summary>
public readonly record struct TowerDefReportPlayability(TowerDefKey Key, uint PlayableSpots)
{
    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter();
        writer.WriteUInt32(Key.TowerDefId); writer.WriteUInt16(Key.ZoneGroup); writer.WriteUInt32(PlayableSpots);
        return new(ZwOpcodes.TowerDefReportPlayability, writer.ToArray());
    }
}
