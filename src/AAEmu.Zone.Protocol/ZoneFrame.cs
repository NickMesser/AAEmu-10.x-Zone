using System.Buffers.Binary;

namespace AAEmu.Zone.Protocol;

/// <summary>
/// Native WZ/ZW framing: little-endian u16 length, u16 opcode, then body.
/// Length includes the opcode and excludes the length field.
/// </summary>
public readonly record struct ZoneFrame(ushort Opcode, ReadOnlyMemory<byte> Body)
{
    public const int HeaderSize = 4;
    public const int MaxBodySize = ushort.MaxValue - sizeof(ushort);

    public byte[] Encode()
    {
        if (Body.Length > MaxBodySize)
            throw new InvalidDataException($"Zone body exceeds the {MaxBodySize}-byte wire limit.");

        var bytes = new byte[HeaderSize + Body.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)(Body.Length + sizeof(ushort)));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), Opcode);
        Body.Span.CopyTo(bytes.AsSpan(HeaderSize));
        return bytes;
    }

    /// <summary>Returns null only at a frame boundary; EOF inside a frame is an error.</summary>
    public static async ValueTask<ZoneFrame?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new byte[sizeof(ushort)];
        var received = await stream.ReadAsync(header, cancellationToken).ConfigureAwait(false);
        if (received == 0)
            return null;
        await stream.ReadExactlyAsync(header.AsMemory(received), cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(header);
        if (length < sizeof(ushort))
            throw new InvalidDataException("Zone frame length must include a two-byte opcode.");

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return new ZoneFrame(BinaryPrimitives.ReadUInt16LittleEndian(payload), payload.AsMemory(2));
    }
}
