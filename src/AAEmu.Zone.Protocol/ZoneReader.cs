using System.Buffers.Binary;
using System.Text;

namespace AAEmu.Zone.Protocol;

/// <summary>Bounded reader for native wire primitives. Parsing never mutates simulation state.</summary>
public ref struct ZoneReader(ReadOnlySpan<byte> bytes)
{
    private ReadOnlySpan<byte> _remaining = bytes;

    public int Remaining => _remaining.Length;

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        if (count < 0 || count > _remaining.Length)
            throw new InvalidDataException($"Zone body is truncated: need {count}, have {_remaining.Length} bytes.");
        var value = _remaining[..count];
        _remaining = _remaining[count..];
        return value;
    }

    public byte ReadByte() => ReadBytes(1)[0];
    public sbyte ReadSByte() => unchecked((sbyte)ReadByte());
    public bool ReadBoolean() => ReadByte() != 0;
    public short ReadInt16() => BinaryPrimitives.ReadInt16LittleEndian(ReadBytes(2));
    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(ReadBytes(2));
    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(4));
    public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(ReadBytes(4));
    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(ReadBytes(8));
    public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(ReadBytes(8));
    public float ReadSingle() => BinaryPrimitives.ReadSingleLittleEndian(ReadBytes(4));

    public uint ReadBc()
    {
        var value = ReadBytes(3);
        return (uint)(value[0] | value[1] << 8 | value[2] << 16);
    }

    /// <summary>FUN_3937E2D0: groups of four integers, two width bits per integer.</summary>
    public uint[] ReadPisc(int count)
    {
        if (count < 0 || (long)count + (count + 3L) / 4 > Remaining)
            throw new InvalidDataException("Packed integer count exceeds the remaining Zone body.");
        var values = new uint[count];
        for (var start = 0; start < count; start += 4)
        {
            var widths = ReadByte();
            for (var index = 0; index < Math.Min(4, count - start); index++)
            {
                var width = ((widths >> (index * 2)) & 3) + 1;
                var bytes = ReadBytes(width);
                uint value = 0;
                for (var offset = 0; offset < width; offset++)
                    value |= (uint)bytes[offset] << (offset * 8);
                values[start + index] = value;
            }
        }
        return values;
    }

    /// <summary>Validate wire counts before allocating; the enclosing frame is the memory bound.</summary>
    public int ReadCount(int minimumEntrySize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumEntrySize, 1);
        var count = ReadUInt32();
        if (count > Remaining / minimumEntrySize)
            throw new InvalidDataException("Collection count exceeds the remaining Zone body.");
        return (int)count;
    }

    public string ReadString(int maxBytes = ushort.MaxValue)
    {
        var length = ReadUInt16();
        if (length > maxBytes)
            throw new InvalidDataException($"Zone string exceeds its {maxBytes}-byte native limit.");
        return Encoding.UTF8.GetString(ReadBytes(length));
    }

    public void EnsureFullyConsumed()
    {
        if (!_remaining.IsEmpty)
            throw new InvalidDataException($"Zone body has {_remaining.Length} unexpected trailing bytes.");
    }
}
