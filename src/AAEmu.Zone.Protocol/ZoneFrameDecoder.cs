using System.Buffers.Binary;

namespace AAEmu.Zone.Protocol;

/// <summary>
/// Incremental decoder for one TCP connection. Retains at most one incomplete frame,
/// regardless of the number of coalesced frames in a receive callback. Not thread safe.
/// </summary>
public sealed class ZoneFrameDecoder
{
    private byte[] _buffer = new byte[256];
    private int _count;
    private int _expected = sizeof(ushort);

    public int BufferedBytes => _count;

    public void Feed(ReadOnlySpan<byte> bytes, Action<ZoneFrame> receive)
    {
        ArgumentNullException.ThrowIfNull(receive);
        while (!bytes.IsEmpty)
        {
            var take = Math.Min(_expected - _count, bytes.Length);
            bytes[..take].CopyTo(_buffer.AsSpan(_count));
            _count += take;
            bytes = bytes[take..];
            if (_count < _expected)
                return;

            if (_expected == sizeof(ushort))
            {
                var length = BinaryPrimitives.ReadUInt16LittleEndian(_buffer);
                if (length < sizeof(ushort))
                    throw new InvalidDataException("Zone frame length must include a two-byte opcode.");
                _expected = length + sizeof(ushort);
                if (_buffer.Length < _expected)
                    Array.Resize(ref _buffer, _expected);
                continue;
            }

            var opcode = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(2));
            var body = _buffer.AsMemory(ZoneFrame.HeaderSize, _count - ZoneFrame.HeaderSize).ToArray();
            _count = 0;
            _expected = sizeof(ushort);
            receive(new ZoneFrame(opcode, body));
        }
    }

    public void Complete()
    {
        if (_count != 0)
            throw new EndOfStreamException($"Zone connection ended with {_count} bytes of an incomplete frame.");
    }
}
