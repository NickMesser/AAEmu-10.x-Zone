using System.Numerics;

using AAEmu.Zone.Combat;
using AAEmu.Zone.Content;

namespace AAEmu.Zone.Terrain;

/// <summary>
/// Terrain-only elevation in the native eight-by-eight loaded-cell window. Cell indices are engine
/// coordinates, not world-map coordinates. C3DEngine 30136B60 (+428) calls 300D4E00 with voxel queries
/// disabled for NPC placement. Missing terrain and rejected native queries return zero.
/// </summary>
public sealed partial class ZoneTerrainHeightField
{
    private readonly object _gate = new();
    private readonly Cell?[] _cells = new Cell?[64];
    public ZoneTerrainNodeTree? Nodes { get; private set; }

    /// <summary>300CFEC0/300D7910: initialize the exact heightfield's persistent tree between registrations.</summary>
    public ZoneTerrainNodeTree InitializeNodeTree(float oceanWaterLevel)
    {
        lock (_gate)
        {
            if (Nodes is not null || _cells.Any(cell => cell is not null))
                throw new InvalidOperationException("Terrain tree initialization requires an uninitialized, empty heightfield.");
            ResetPhysicsCacheCore();
            return Nodes = new(_gate, oceanWaterLevel);
        }
    }

    /// <summary>
    /// 300D0890 installs the authored data, then copies adjoining height edges in X-, X+, Y-, Y+
    /// order. Each destination keeps its own quantization and low surface bits. A zero-length native
    /// terrain chunk does not replace the existing cell (3015D4A0).
    /// </summary>
    public void LoadCell(int cellX, int cellY, ZoneTerrainHeightmapContent content)
    {
        ValidateCell(cellX, cellY);
        ArgumentNullException.ThrowIfNull(content);
        if (content.Header.ChunkSize == 0) return;
        var cell = new Cell(content);
        lock (_gate)
        {
            ResetPhysicsCacheCore();
            Nodes?.InstallCellCore(cellX, cellY, cell.Nodes);
            _cells[cellX * 8 + cellY] = cell;
            Nodes?.RefreshBounds(cellX, cellY);
            CopyCellSeamsCore(cellX, cellY, cell);
        }
    }

    // The engine owner must publish nodes before bounds/texture callbacks and header surface
    // overrides, then copy seams. The public content-only operation above remains atomic.
    internal void InstallCellSamples(int cellX, int cellY, ZoneTerrainHeightmapContent content)
    {
        ValidateCell(cellX, cellY);
        ArgumentNullException.ThrowIfNull(content);
        if (content.Header.ChunkSize == 0) return;
        var cell = new Cell(content);
        lock (_gate)
        {
            Nodes?.InstallCellCore(cellX, cellY, cell.Nodes);
            _cells[cellX * 8 + cellY] = cell;
        }
    }

    internal void CopyCellSeams(int cellX, int cellY)
    {
        ValidateCell(cellX, cellY);
        lock (_gate) CopyCellSeamsCore(cellX, cellY, _cells[cellX * 8 + cellY]);
    }

    private void CopyCellSeamsCore(int cellX, int cellY, Cell? cell)
    {
        if (cellX > 0) CopyXEdge(_cells[(cellX - 1) * 8 + cellY], cell);
        if (cellX < 7) CopyXEdge(cell, _cells[(cellX + 1) * 8 + cellY]);
        if (cellY > 0) CopyYEdge(_cells[cellX * 8 + cellY - 1], cell);
        if (cellY < 7) CopyYEdge(cell, _cells[cellX * 8 + cellY + 1]);
    }

    /// <summary>300D0030/300E8C20 removes this cell's samples; neighboring copied edges remain.</summary>
    public bool UnloadCell(int cellX, int cellY)
    {
        ValidateCell(cellX, cellY);
        lock (_gate)
        {
            var index = cellX * 8 + cellY;
            var present = _cells[index] is not null;
            Nodes?.UnloadCellCore(cellX, cellY);
            _cells[index] = null;
            return present;
        }
    }

    /// <summary>
    /// Engine-local meters: first two meters and coordinates at/above 8192 return zero. The native
    /// query expands four integer-grid corners, rejects any zero corner, then interpolates a terrain
    /// triangle and clamps negative elevation. Hole flags do not suppress this height-only query.
    /// </summary>
    public float GetHeight(float x, float y)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || x < 2 || y < 2 || x >= 8192 || y >= 8192)
            return 0;
        var gridX = x * .5f;
        var gridY = y * .5f;
        var integerX = (int)MathF.Floor(gridX);
        var integerY = (int)MathF.Floor(gridY);
        var fractionX = gridX - integerX;
        var fractionY = gridY - integerY;
        lock (_gate)
        {
            var cell = _cells[(integerX >> 9) * 8 + (integerY >> 9)];
            var leaf = cell?.Leaves[((integerX >> 5) & 15) * 16 + ((integerY >> 5) & 15)];
            if (leaf is null || leaf.Samples.Length == 0) return 0;
            var corners = leaf.GetHeightQuad(integerX & 31, integerY & 31);
            if (corners.X == 0 || corners.Y == 0 || corners.Z == 0 || corners.W == 0) return 0;
            float height;
            if (fractionY + fractionX >= 1f)
            {
                height = ((fractionY + fractionX) - 1f) * corners.W;
                height += (1f - fractionX) * corners.Z;
                height += (1f - fractionY) * corners.Y;
            }
            else
            {
                height = ((1f - fractionX) - fractionY) * corners.X;
                height += corners.Y * fractionX;
                height += corners.Z * fractionY;
            }
            // COMISS/JBE preserves an unordered NaN result as well as negative zero.
            return height < 0 ? 0 : height;
        }
    }

    private static void ValidateCell(int x, int y)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(x, 8);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, 8);
    }

    private static void CopyXEdge(Cell? left, Cell? right)
    {
        if (left is null || right is null) return;
        for (var y = 0; y < 16; y++)
        {
            var destination = left.Leaves[15 * 16 + y];
            var source = right.Leaves[y];
            if (destination.Samples.Length == 0 || source.Samples.Length == 0) continue;
            for (var sample = 0; sample < 33; sample++)
                destination.SetSampleHeight(32, sample, source.GetSampleHeight(0, sample));
        }
    }

    private static void CopyYEdge(Cell? bottom, Cell? top)
    {
        if (bottom is null || top is null) return;
        for (var x = 0; x < 16; x++)
        {
            var destination = bottom.Leaves[x * 16 + 15];
            var source = top.Leaves[x * 16];
            if (destination.Samples.Length == 0 || source.Samples.Length == 0) continue;
            for (var sample = 0; sample < 33; sample++)
                destination.SetSampleHeight(sample, 32, source.GetSampleHeight(sample, 0));
        }
    }

    private sealed class Cell
    {
        public NodeSamples[] Leaves { get; } = new NodeSamples[256];
        public NodeSamples[] Nodes { get; }

        public Cell(ZoneTerrainHeightmapContent content)
        {
            foreach (var node in content.Nodes)
            {
                // Native seam loops unconditionally use sample coordinates 0..32. All 25,500
                // border leaves in the configured authored files satisfy this precondition.
                if (node.IsLeaf && (node.LocalX is 0 or 960 || node.LocalY is 0 or 960) && node.SampleSize is not (0 or 33))
                    throw new InvalidDataException("A loaded terrain cell requires 33-sample border leaves for native seam copying.");
                foreach (var slot in node.SurfaceTypes)
                    if (slot >= 32) throw new InvalidDataException("A terrain node refers outside its cell's surface slots.");
            }
            Nodes = content.Nodes.Select(node => new NodeSamples(node)).ToArray();
            foreach (var node in Nodes)
                if (node.Level == 0) Leaves[node.LocalX / 64 * 16 + node.LocalY / 64] = node;
        }
    }

    internal sealed partial class NodeSamples
    {
        private static readonly float QuantizationThreshold = BitConverter.UInt32BitsToSingle(0x3423d7ae);
        private readonly float _offset;
        private readonly float _range;
        private readonly int _size;
        private readonly int _shift;
        private readonly float _scale;
        public ushort[] Samples { get; }
        public int Size => _size;
        public float Offset => _offset;
        public float Range => _range;
        public int LocalX { get; }
        public int LocalY { get; }
        public int SizeMeters { get; }
        public int Level { get; }
        public Vector3 AuthoredMin { get; }
        public Vector3 AuthoredMax { get; }
        public byte HoleFlags { get; }
        public float[] LodErrors { get; }
        public byte[] SurfaceSlots { get; }

        public NodeSamples(ZoneTerrainHeightmapNode node)
        {
            LocalX = node.LocalX; LocalY = node.LocalY; SizeMeters = node.SizeMeters;
            Level = BitOperations.Log2((uint)(node.SizeMeters / 64));
            AuthoredMin = node.AuthoredBoundsMin; AuthoredMax = node.AuthoredBoundsMax;
            HoleFlags = node.HoleFlags;
            LodErrors = node.LodErrors.ToArray(); SurfaceSlots = node.SurfaceTypes.ToArray();
            _offset = node.HeightOffset;
            _range = node.HeightRange;
            _size = node.SampleSize;
            Samples = node.PackedSamples.ToArray();
            if (_size == 0) return;
            for (var intervals = _size - 1; intervals < 32; intervals *= 2) _shift++;
            _scale = 1f / (32 / (_size - 1));
        }

        public float GetSampleHeight(int x, int y) => (Samples[x * _size + y] & 0xffe0) * _range + _offset;

        // 300EA370 truncates the float quotient as CVTTSS2SI, clamps to ushort, and retains surface bits.
        public void SetSampleHeight(int x, int y, float height)
        {
            var value = _range > QuantizationThreshold ? (height - _offset) / _range : 0;
            var encoded = Math.Clamp(ZoneStatMath.ToInt32(value), 0, 65535);
            var index = x * _size + y;
            Samples[index] = (ushort)((Samples[index] & 31) | (encoded & 0xffe0));
        }

        // 300D4670 expands the current coarse quad, including its edge. Calling the ordinary
        // integer query four times changes both the selected coarse quad and float operation order.
        public Vector4 GetHeightQuad(int x, int y)
        {
            if (_shift == 0)
                return new(GetSampleHeight(x, y), GetSampleHeight(x + 1, y),
                    GetSampleHeight(x, y + 1), GetSampleHeight(x + 1, y + 1));
            var sampleX = x >> _shift;
            var sampleY = y >> _shift;
            var fractionX = x * _scale - sampleX;
            var fractionY = y * _scale - sampleY;
            var index = sampleX * _size + sampleY;
            return new(Interpolate(fractionX, fractionY, false),
                Interpolate(fractionX + _scale, fractionY, true),
                Interpolate(fractionX, fractionY + _scale, false),
                Interpolate(fractionX + _scale, fractionY + _scale, false));

            float Interpolate(float fx, float fy, bool lowerFirst)
            {
                var inverseX = 1f - fx;
                var inverseY = 1f - fy;
                var lower = (Samples[index] & 0xffe0) * inverseX * inverseY;
                var upper = (Samples[index + _size] & 0xffe0) * fx * inverseY;
                var value = lowerFirst ? lower + upper : upper + lower;
                value += (Samples[index + 1] & 0xffe0) * inverseX * fy;
                value += (Samples[index + _size + 1] & 0xffe0) * fx * fy;
                return value * _range + _offset;
            }
        }
    }
}
