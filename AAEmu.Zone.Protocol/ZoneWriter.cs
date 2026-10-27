using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace AAEmu.Zone.Protocol;

public sealed class ZoneWriter
{
    private readonly ArrayBufferWriter<byte> _buffer = new();

    public byte[] ToArray() => _buffer.WrittenSpan.ToArray();

    private Span<byte> Allocate(int length)
    {
        if (length < 0 || length > ZoneFrame.MaxBodySize - _buffer.WrittenCount)
            throw new InvalidDataException("Zone body exceeds the native frame limit.");
        var bytes = _buffer.GetSpan(length)[..length];
        _buffer.Advance(length);
        return bytes;
    }

    public void WriteBytes(ReadOnlySpan<byte> value) => value.CopyTo(Allocate(value.Length));
    public void WriteByte(byte value) => Allocate(1)[0] = value;
    public void WriteSByte(sbyte value) => WriteByte(unchecked((byte)value));
    public void WriteInt16(short value) => BinaryPrimitives.WriteInt16LittleEndian(Allocate(2), value);
    public void WriteBoolean(bool value) => WriteByte(value ? (byte)1 : (byte)0);
    public void WriteUInt16(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Allocate(2), value);
    public void WriteUInt32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Allocate(4), value);
    public void WriteInt32(int value) => BinaryPrimitives.WriteInt32LittleEndian(Allocate(4), value);
    public void WriteUInt64(ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(Allocate(8), value);
    public void WriteInt64(long value) => BinaryPrimitives.WriteInt64LittleEndian(Allocate(8), value);
    public void WriteSingle(float value) => BinaryPrimitives.WriteSingleLittleEndian(Allocate(4), value);

    public void WriteBc(uint value)
    {
        if (value > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(value), "A native bc object ID occupies three bytes.");
        var bytes = Allocate(3);
        bytes[0] = (byte)value;
        bytes[1] = (byte)(value >> 8);
        bytes[2] = (byte)(value >> 16);
    }

    public void WritePisc(params ReadOnlySpan<uint> values)
    {
        for (var start = 0; start < values.Length; start += 4)
        {
            var count = Math.Min(4, values.Length - start);
            byte widths = 0;
            for (var index = 0; index < count; index++)
                widths |= (byte)((Width(values[start + index]) - 1) << (index * 2));
            WriteByte(widths);
            for (var index = 0; index < count; index++)
            {
                var value = values[start + index];
                var width = Width(value);
                for (var offset = 0; offset < width; offset++)
                    WriteByte((byte)(value >> (offset * 8)));
            }
        }
    }

    private static int Width(uint value) => value <= 0xFF ? 1 : value <= 0xFFFF ? 2 : value <= 0xFFFFFF ? 3 : 4;

    public void WriteString(string value, int maxBytes = ushort.MaxValue)
    {
        var length = Encoding.UTF8.GetByteCount(value);
        if (length > maxBytes || length > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value), "String exceeds the native byte limit.");
        WriteUInt16((ushort)length);
        Encoding.UTF8.GetBytes(value, Allocate(length));
    }
}
