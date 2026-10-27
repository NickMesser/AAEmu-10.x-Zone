using System.Buffers.Binary;
using System.Numerics;

namespace AAEmu.Zone.Content;

public sealed record ZoneTerrainHeightmapHeader(uint Version, uint ChunkSize, uint HeightMapSizeInUnits,
    uint UnitSizeMeters, uint SectorSizeMeters, uint SectorsTableSize, float HeightmapZRatio,
    float OceanWaterLevel);

/// <summary>
/// One authored terrain node, before the engine relocates its bounding box into a loaded cell.
/// Samples retain their native packed height and surface bits; they are never rescaled to centimeters.
/// </summary>
public sealed class ZoneTerrainHeightmapNode
{
    private readonly ushort[] _samples;
    private readonly float[] _lodErrors;
    private readonly byte[] _surfaces;
    private readonly byte[] _tail;

    public int SourceIndex { get; }
    public int LocalX { get; }
    public int LocalY { get; }
    public int SizeMeters { get; }
    public bool IsLeaf => SizeMeters == 64;
    public Vector3 AuthoredBoundsMin { get; }
    public Vector3 AuthoredBoundsMax { get; }
    public byte HoleFlags { get; }
    public float HeightOffset { get; }
    public float HeightRange { get; }
    public int SampleSize { get; }
    public ReadOnlySpan<ushort> PackedSamples => _samples;
    public ReadOnlySpan<float> LodErrors => _lodErrors;
    public ReadOnlySpan<byte> SurfaceTypes => _surfaces;
    public ReadOnlySpan<byte> OpaqueTail => _tail;

    internal ZoneTerrainHeightmapNode(int index, int x, int y, int size, Vector3 min, Vector3 max,
        byte holes, float offset, float range, int sampleSize, ushort[] samples, float[] lodErrors,
        byte[] surfaces, byte[] tail)
    {
        SourceIndex = index;
        LocalX = x;
        LocalY = y;
        SizeMeters = size;
        AuthoredBoundsMin = min;
        AuthoredBoundsMax = max;
        HoleFlags = holes;
        HeightOffset = offset;
        HeightRange = range;
        SampleSize = sampleSize;
        _samples = samples;
        _lodErrors = lodErrors;
        _surfaces = surfaces;
        _tail = tail;
    }

    /// <summary>300EA330: sample coordinates are X-major, including the authored edge sample.</summary>
    public float GetSampleHeight(int sampleX, int sampleY)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sampleX);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(sampleX, SampleSize);
        ArgumentOutOfRangeException.ThrowIfNegative(sampleY);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(sampleY, SampleSize);
        return (_samples[sampleX * SampleSize + sampleY] & 0xffe0) * HeightRange + HeightOffset;
    }

    /// <summary>
    /// 300D4450/300D4220 at an integer terrain unit (two meters) inside a 64-meter leaf.
    /// The native height query does not consult the hole flags. This is not a collision query.
    /// </summary>
    public bool TryGetHeightAtGridPoint(int gridX, int gridY, out float height)
    {
        height = 0;
        if (!IsLeaf || SampleSize == 0 || (uint)gridX >= 32 || (uint)gridY >= 32) return false;
        var shift = 0;
        for (var intervals = SampleSize - 1; intervals < 32; intervals *= 2) shift++;
        if (shift == 0)
        {
            height = GetSampleHeight(gridX, gridY);
            return true;
        }

        // 300E1EC0 stores the reciprocal of the integer expansion factor. Keep the original
        // operation order: interpolating decoded heights or pre-expanding the map rounds differently.
        var scale = 1f / (32 / (SampleSize - 1));
        var sampleX = gridX >> shift;
        var sampleY = gridY >> shift;
        var fractionX = gridX * scale - sampleX;
        var fractionY = gridY * scale - sampleY;
        var inverseX = 1f - fractionX;
        var inverseY = 1f - fractionY;
        var index = sampleX * SampleSize + sampleY;
        var value = (_samples[index + SampleSize] & 0xffe0) * fractionX * inverseY;
        value += (_samples[index] & 0xffe0) * inverseX * inverseY;
        value += (_samples[index + 1] & 0xffe0) * inverseX * fractionY;
        value += (_samples[index + SampleSize + 1] & 0xffe0) * fractionX * fractionY;
        height = value * HeightRange + HeightOffset;
        return true;
    }
}

/// <summary>
/// Read-only content for the native 1024-meter cell profile (two-meter units, 64-meter leaves).
/// 3015D4A0 reads header versions 23/24; 300E9600 consumes the packed version-5 node tree.
/// This parser deliberately validates malformed lengths and unsupported profiles before allocation.
/// Surface material resolution, engine cell relocation and cross-cell edge rewriting are separate.
/// </summary>
public sealed class ZoneTerrainHeightmapContent
{
    private readonly byte[] _surfaceIds;
    private readonly ZoneTerrainHeightmapNode?[] _leafIndex = new ZoneTerrainHeightmapNode?[256];

    public ZoneTerrainHeightmapHeader Header { get; }
    public IReadOnlyList<ZoneTerrainHeightmapNode> Nodes { get; }
    public IReadOnlyList<ZoneTerrainHeightmapNode> LeafNodes { get; }
    public int RawSampleCount { get; }
    public int ConsumedBytes { get; }
    public int TrailingByteCount { get; }
    /// <summary>Version 24's 32 material IDs; version 23 supplies zeroes (3015D4A0).</summary>
    public ReadOnlySpan<byte> SurfaceIdBytes => _surfaceIds;

    private ZoneTerrainHeightmapContent(ZoneTerrainHeightmapHeader header, byte[] surfaceIds,
        List<ZoneTerrainHeightmapNode> nodes, int consumed, int trailing)
    {
        Header = header;
        _surfaceIds = surfaceIds;
        Nodes = nodes.AsReadOnly();
        LeafNodes = Array.AsReadOnly(nodes.Where(node => node.IsLeaf).ToArray());
        RawSampleCount = nodes.Sum(node => node.PackedSamples.Length);
        ConsumedBytes = consumed;
        TrailingByteCount = trailing;
        foreach (var node in LeafNodes) _leafIndex[node.LocalX / 64 * 16 + node.LocalY / 64] = node;
    }

    public static ZoneTerrainHeightmapContent LoadFile(string path) => Read(File.ReadAllBytes(path));

    public static ZoneTerrainHeightmapContent Read(ReadOnlySpan<byte> data)
    {
        var reader = new Reader(data);
        var header = new ZoneTerrainHeightmapHeader(reader.UInt32(), reader.UInt32(), reader.UInt32(),
            reader.UInt32(), reader.UInt32(), reader.UInt32(), reader.Single(), reader.Single());
        if (header.Version is not (23 or 24))
            throw new InvalidDataException($"Unsupported terrain header version {header.Version}.");
        var surfaceIds = header.Version == 24 ? reader.Bytes(128) : new byte[128];
        if (header.HeightMapSizeInUnits != 4096 || header.UnitSizeMeters != 2 ||
            header.SectorSizeMeters != 64 || header.SectorsTableSize != 128)
            throw new InvalidDataException("The terrain reader supports the native 4096-unit, two-meter, 64-meter-sector cell profile.");
        var nodes = new List<ZoneTerrainHeightmapNode>(341);
        if (header.ChunkSize == 0)
            return new(header, surfaceIds, nodes, reader.Position, data.Length - reader.Position);
        if (header.ChunkSize < reader.Position || header.ChunkSize > data.Length)
            throw new InvalidDataException("Terrain chunk length is outside the supplied file.");
        var headerSize = reader.Position;
        reader = new Reader(data.Slice(headerSize, checked((int)header.ChunkSize) - headerSize));
        ReadNode(ref reader, nodes, 0, 0, 1024);
        if (reader.Remaining != 0)
            throw new InvalidDataException("Terrain chunk contains bytes beyond its complete node tree.");
        return new(header, surfaceIds, nodes, checked((int)header.ChunkSize), data.Length - (int)header.ChunkSize);
    }

    /// <summary>
    /// Integer cell-local unit coordinates, 0..511. Missing/outside data returns false;
    /// selecting or clamping a neighboring loaded engine cell is intentionally not performed here.
    /// </summary>
    public bool TryGetHeightAtGridPoint(int localGridX, int localGridY, out float height)
    {
        height = 0;
        if ((uint)localGridX >= 512 || (uint)localGridY >= 512) return false;
        var node = _leafIndex[(localGridX >> 5) * 16 + (localGridY >> 5)];
        return node is not null && node.TryGetHeightAtGridPoint(localGridX & 31, localGridY & 31, out height);
    }

    private static void ReadNode(ref Reader reader, List<ZoneTerrainHeightmapNode> nodes, int x, int y, int size)
    {
        if (reader.UInt32() != 5) throw new InvalidDataException("Terrain node version must be 5.");
        var min = new Vector3(reader.Single(), reader.Single(), reader.Single());
        var max = new Vector3(reader.Single(), reader.Single(), reader.Single());
        var holes = reader.Byte();
        var offset = reader.Single();
        var range = reader.Single();
        var sampleSize = reader.UInt32();
        var surfaceCount = reader.UInt32();
        if (sampleSize is not (0 or 2 or 3 or 5 or 9 or 17 or 33))
            throw new InvalidDataException($"Unsupported terrain sample width {sampleSize}.");
        if (surfaceCount > 32) throw new InvalidDataException("Terrain node exceeds the 32-entry surface table.");
        var samples = new ushort[checked((int)(sampleSize * sampleSize))];
        for (var i = 0; i < samples.Length; i++) samples[i] = reader.UInt16();
        var errors = new float[5];
        for (var i = 0; i < errors.Length; i++) errors[i] = reader.Single();
        var surfaces = reader.Bytes((int)surfaceCount);
        var tail = reader.Bytes(36);
        nodes.Add(new(nodes.Count, x, y, size, min, max, holes, offset, range, (int)sampleSize,
            samples, errors, surfaces, tail));
        if (size == 64) return;
        var half = size / 2;
        // 300E6FD0 creates children in this order; stored bounding boxes do not define topology.
        ReadNode(ref reader, nodes, x, y, half);
        ReadNode(ref reader, nodes, x + half, y, half);
        ReadNode(ref reader, nodes, x, y + half, half);
        ReadNode(ref reader, nodes, x + half, y + half, half);
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> _remaining = data;
        public int Position { get; private set; }
        public int Remaining => _remaining.Length;
        private ReadOnlySpan<byte> Take(int count)
        {
            if (_remaining.Length < count) throw new InvalidDataException("Terrain data is truncated.");
            var value = _remaining[..count];
            _remaining = _remaining[count..];
            Position += count;
            return value;
        }
        public byte Byte() => Take(1)[0];
        public ushort UInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public float Single() => BitConverter.UInt32BitsToSingle(UInt32());
        public byte[] Bytes(int count) => Take(count).ToArray();
    }
}
