namespace AAEmu.Zone.Protocol.Messages;

/// <summary>
/// 0x3936A540, handled by 0x393648D0. Both type fields remain independent wire values.
/// Feature data is binary, despite using the native length-prefixed string serializer.
/// </summary>
public sealed record ZoneJoinResponse(
    ushort Reason, ReadOnlyMemory<byte> Features, bool Battlefield,
    ushort Type, bool Pvp, bool Duel, ushort ConflictType, int PeaceMinutes, ReadOnlyMemory<byte> Filter)
{
    public const int MaxFeatureBytes = 31;

    public bool HasFeature(int bit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bit);
        return bit / 8 < Features.Length && (Features.Span[bit / 8] & (1 << (bit % 8))) != 0;
    }

    public ZoneFrame ToFrame()
    {
        if (Features.Length > MaxFeatureBytes)
            throw new InvalidDataException("Join feature data exceeds the native 31-byte limit.");
        var writer = new ZoneWriter();
        writer.WriteUInt16(Reason);
        writer.WriteUInt16((ushort)Features.Length);
        writer.WriteBytes(Features.Span);
        writer.WriteBoolean(Battlefield);
        writer.WriteUInt16(Type);
        writer.WriteBoolean(Pvp);
        writer.WriteBoolean(Duel);
        writer.WriteUInt16(ConflictType);
        writer.WriteInt32(PeaceMinutes);
        writer.WriteInt32(Filter.Length);
        writer.WriteBytes(Filter.Span);
        return new ZoneFrame(WzOpcodes.JoinResponse, writer.ToArray());
    }

    public static ZoneJoinResponse Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var reason = reader.ReadUInt16();
        var featureSize = reader.ReadUInt16();
        if (featureSize > MaxFeatureBytes)
            throw new InvalidDataException("Join feature data exceeds the native 31-byte limit.");
        var features = reader.ReadBytes(featureSize).ToArray();
        var battlefield = reader.ReadBoolean();
        var type = reader.ReadUInt16();
        var pvp = reader.ReadBoolean();
        var duel = reader.ReadBoolean();
        var conflictType = reader.ReadUInt16();
        var peaceMinutes = reader.ReadInt32();
        var filterSize = reader.ReadInt32();
        var filter = reader.ReadBytes(filterSize).ToArray();
        reader.EnsureFullyConsumed();
        return new ZoneJoinResponse(reason, features, battlefield, type, pvp, duel, conflictType, peaceMinutes, filter);
    }
}
