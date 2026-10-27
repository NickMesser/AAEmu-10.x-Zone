using System.Buffers.Binary;
namespace AAEmu.Zone.Simulation;

/// <summary>
/// Native 31108238/31105020 paired-link storage. Each 40-byte slot owns two directed IDs;
/// allocation takes the last free slot of each 128-slot block and release clears the whole pair.
/// Adjacency and graph reference counts belong to the graph owner, not this storage pool.
/// </summary>
public sealed class ZoneNavigationGraphLinkPool
{
    private readonly List<byte[]> _blocks = [];
    private readonly HashSet<uint> _allocated = [];
    private uint _freeHead;
    public int BlockCount => _blocks.Count;
    public int AllocatedPairCount => _allocated.Count;
    public IEnumerable<uint> AllocatedPairs => _allocated.Order();

    public uint Allocate()
    {
        if (_freeHead == 0)
        {
            var blockIndex = _blocks.Count;
            _blocks.Add(new byte[128 * 40]);
            // Native initializes slot0 but omits it from the initial free chain.
            for (var i = 1; i < 128; i++)
            {
                var id = checked((uint)(blockIndex * 256 + 2 + i * 2));
                WriteWord(id, 4, _freeHead & 0x7fffff);
                _freeHead = id;
            }
        }
        var result = _freeHead;
        _freeHead = ReadWord(result, 4) & 0x7fffff;
        WriteWord(result, 4, ReadWord(result, 4) & 0xff800000);
        _allocated.Add(result);
        return result;
    }

    public bool Release(uint directedId)
    {
        var pairId = directedId & ~1u;
        if (!_allocated.Remove(pairId)) return false;
        Pair(pairId).Clear();
        WriteWord(pairId, 4, _freeHead & 0x7fffff);
        _freeHead = pairId;
        return true;
    }

    public bool IsAllocated(uint directedId) => _allocated.Contains(directedId & ~1u);
    public byte[] SnapshotPair(uint directedId)
    { RequireAllocated(directedId); return Pair(directedId).ToArray(); }
    public uint GetNext(uint directedId) { RequireAllocated(directedId); return ReadWord(directedId, Direction(directedId) + 4) & 0x7fffff; }
    public uint GetTarget(uint directedId) { RequireAllocated(directedId); return (ReadWord(directedId, Direction(directedId) + 8) >> 8) & 0xfffff; }
    public void SetNext(uint directedId, uint next)
    { RequireAllocated(directedId); var offset = Direction(directedId) + 4; WriteWord(directedId, offset, (ReadWord(directedId, offset) & 0xff800000) | (next & 0x7fffff)); }
    public void SetTarget(uint directedId, uint target)
    { RequireAllocated(directedId); var offset = Direction(directedId) + 8; WriteWord(directedId, offset, (ReadWord(directedId, offset) & 0xf00000ff) | ((target & 0xfffff) << 8)); }

    /// <summary>31083160 initializes current and original cost; 31104EE8 updates only current cost.</summary>
    public void SetCost(uint directedId, float value, bool initialize)
    {
        RequireAllocated(directedId);
        var quantized = Math.Clamp(Truncate(value * 100f), -32767, 32767);
        if (initialize && quantized == 0 && value != 0 && value > 0) quantized = 1;
        BinaryPrimitives.WriteInt16LittleEndian(Pair(directedId)[Direction(directedId)..], (short)quantized);
        if (initialize) BinaryPrimitives.WriteInt16LittleEndian(Pair(directedId)[(Direction(directedId) + 2)..], (short)quantized);
    }

    public void ApplyAuthoredMetadata(uint directedId, ReadOnlySpan<byte> record)
    {
        RequireAllocated(directedId);
        if (record.Length != 44) throw new ArgumentException("A native graph link record is exactly 44 bytes.", nameof(record));
        SetCost(directedId, BinaryPrimitives.ReadSingleLittleEndian(record[20..]), true);
        var offset = Direction(directedId) + 8;
        WriteWord(directedId, offset, (ReadWord(directedId, offset) & 0x0fffffff) | ((uint)(record[40] & 3) << 28) | ((uint)record[41] << 30));
        record.Slice(8, 12).CopyTo(Pair(directedId)[28..]);
        record.Slice(24, 4).CopyTo(Pair(directedId)[24..]);
        // These signed fields straddle the packed adjacency/target words in each half.
        WriteBoundedFloatShort(directedId, 19, BinaryPrimitives.ReadSingleLittleEndian(record[32..]));
        WriteBoundedFloatShort(directedId, 7, BinaryPrimitives.ReadSingleLittleEndian(record[36..]));
        WriteWord(directedId, 4, (ReadWord(directedId, 4) & ~0x800000u) | ((uint)(record[42] & 2) << 22));
        WriteWord(directedId, 16, ReadWord(directedId, 16) | 0x800000);
    }

    internal void SetMidpoint(uint id, System.Numerics.Vector3 position)
    {
        RequireAllocated(id);
        BinaryPrimitives.WriteSingleLittleEndian(Pair(id)[28..], position.X);
        BinaryPrimitives.WriteSingleLittleEndian(Pair(id)[32..], position.Y);
        BinaryPrimitives.WriteSingleLittleEndian(Pair(id)[36..], position.Z);
    }

    internal void RestoreOriginalCost(uint id)
    {
        RequireAllocated(id);
        var offset = Direction(id);
        Pair(id).Slice(offset + 2, 2).CopyTo(Pair(id)[offset..]);
    }

    private void WriteBoundedFloatShort(uint id, int offset, float value)
    {
        var scaled = value * 100f;
        // Native COMISS/JBE branches retain NaN; CVTTSS2SI leaves zero in both stored bytes.
        var bounded = scaled < -32767f ? -32767f : scaled > 32767f ? 32767f : scaled;
        BinaryPrimitives.WriteInt16LittleEndian(Pair(id)[offset..], (short)Truncate(bounded));
    }
    private static int Truncate(float value) => float.IsNaN(value) || value >= 2147483648f || value < -2147483648f ? int.MinValue : (int)value;
    private static int Direction(uint id) => (int)(id & 1) * 12;
    private Span<byte> Pair(uint id)
    { var index = (id >> 1) - 1; return _blocks[(int)(index >> 7)].AsSpan((int)(index & 127) * 40, 40); }
    private uint ReadWord(uint id, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Pair(id)[offset..]);
    private void WriteWord(uint id, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Pair(id)[offset..], value);
    private void RequireAllocated(uint id)
    { if (!IsAllocated(id)) throw new InvalidOperationException($"Graph link {id} is not allocated."); }
}
