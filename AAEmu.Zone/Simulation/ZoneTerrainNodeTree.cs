using System.Numerics;
using AAEmu.Zone.Content;

namespace AAEmu.Zone.Simulation;

public readonly record struct ZoneTerrainNodeBounds(Vector3 Min, Vector3 Max);

/// <summary>
/// A persistent structural node. Mutable reads use the heightfield's gate; array access returns
/// snapshots. Cell unload retires payloads without replacing this node or its children.
/// </summary>
public sealed class ZoneTerrainNode
{
    internal readonly ZoneTerrainNodeTree Owner;
    internal ZoneTerrainNodeBounds BoundsValue;
    internal ZoneTerrainHeightField.NodeSamples? Samples;
    internal float[] LodErrorsValue = new float[5];
    internal byte[] SurfaceSlots = [];
    internal byte HoleFlagsValue;
    internal bool Dirty;
    internal short TextureIndexValue = -1;
    // Only bit 0x02 is used here. Its wider meaning/setter is not recovered yet.
    internal byte TextureAssignmentFlags;
    private IReadOnlyList<ZoneTerrainNode> _children = Array.Empty<ZoneTerrainNode>();
    public int X { get; }
    public int Y { get; }
    public int SizeMeters => 64 << Level;
    public int Level { get; }
    public ZoneTerrainNode? Parent { get; }
    public IReadOnlyList<ZoneTerrainNode> Children => _children;
    public ZoneTerrainNodeBounds Bounds { get { lock (Owner.Gate) return BoundsValue; } }
    public int SampleSize { get { lock (Owner.Gate) return Samples?.Size ?? 0; } }
    public float HeightOffset { get { lock (Owner.Gate) return Samples?.Offset ?? 0; } }
    public float HeightRange { get { lock (Owner.Gate) return Samples?.Range ?? 0; } }
    public byte HoleFlags { get { lock (Owner.Gate) return HoleFlagsValue; } }
    public bool IsMaterialDirty { get { lock (Owner.Gate) return Dirty; } }
    public short TextureIndex { get { lock (Owner.Gate) return TextureIndexValue; } }

    internal ZoneTerrainNode(ZoneTerrainNodeTree owner, int x, int y, int level, ZoneTerrainNode? parent)
    {
        Owner = owner; X = x; Y = y; Level = level; Parent = parent;
        BoundsValue = new(new(x, y, 9999999), new(x + SizeMeters, y + SizeMeters, -9999999));
    }

    internal void SetChildren(ZoneTerrainNode[] children) => _children = Array.AsReadOnly(children);
    public ushort[] CopyPackedSamples() { lock (Owner.Gate) return Samples?.Samples.ToArray() ?? []; }
    public float[] CopyLodErrors() { lock (Owner.Gate) return LodErrorsValue.ToArray(); }
    public byte[] CopySurfaceSlots() { lock (Owner.Gate) return SurfaceSlots.ToArray(); }

    /// <summary>Resolve the current slot value, including later version-24 material overrides.</summary>
    public ZoneTerrainSurfaceBinding GetSurfaceBinding(int index)
    {
        lock (Owner.Gate)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, SurfaceSlots.Length);
            return Owner.RequireSurfaces().GetBinding(X / 1024, Y / 1024, SurfaceSlots[index]);
        }
    }
}

/// <summary>
/// 300D7910/300E6FD0's complete 8192-metre tree, owned by the exact callback heightfield.
/// Node imports and seam sampling share one payload and one lock. This owns managed topology,
/// bounds, sample/LOD data and edge remaps; texture, procedural and renderer handles belong to
/// the required resource backend and must retire before the engine clears these payloads.
/// </summary>
public sealed partial class ZoneTerrainNodeTree
{
    private readonly ZoneTerrainNode[][] _levels = new ZoneTerrainNode[8][];
    private readonly int[][] _positiveX = new int[64][];
    private readonly int[][] _positiveY = new int[64][];
    private ZoneTerrainSurfaceBindings? _surfaces;
    internal object Gate { get; }
    public ZoneTerrainNode Root { get; }
    public IReadOnlyList<ZoneTerrainNode> Nodes { get; }
    public ZoneDedicatedTerrainTextures Textures { get; }
    public ZoneTerrainNodeResources SurfaceResources { get; }
    /// <summary>The already resolved engine ocean level, not a heightmap's authored level.</summary>
    public float OceanWaterLevel { get; }

    internal ZoneTerrainNodeTree(object gate, float oceanWaterLevel)
    {
        Gate = gate; OceanWaterLevel = oceanWaterLevel;
        for (var level = 0; level < 8; level++)
        {
            var width = 128 >> level;
            _levels[level] = new ZoneTerrainNode[width * width];
        }
        for (var cell = 0; cell < 64; cell++)
        {
            _positiveX[cell] = new int[32];
            _positiveY[cell] = new int[32];
        }
        var nodes = new List<ZoneTerrainNode>(21845);
        Root = Create(0, 0, 7, null);
        Nodes = nodes.AsReadOnly();
        Textures = new(this);
        SurfaceResources = new(this);

        ZoneTerrainNode Create(int x, int y, int level, ZoneTerrainNode? parent)
        {
            var node = new ZoneTerrainNode(this, x, y, level, parent);
            nodes.Add(node);
            _levels[level][(x / node.SizeMeters) * (128 >> level) + y / node.SizeMeters] = node;
            if (level != 0)
            {
                var half = node.SizeMeters / 2;
                node.SetChildren([Create(x, y, level - 1, node), Create(x + half, y, level - 1, node),
                    Create(x, y + half, level - 1, node), Create(x + half, y + half, level - 1, node)]);
                // Constructor aggregation starts at 99999; later bounds refresh starts at 9999999.
                Aggregate(node, 99999);
            }
            return node;
        }
    }

    public ZoneTerrainNode GetNode(int level, int x, int y)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(level, 8);
        var width = 128 >> level;
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(x, width);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, width);
        return _levels[level][x * width + y];
    }

    public ZoneTerrainNode GetCellRoot(int cellX, int cellY) => GetNode(4, cellX, cellY);

    internal void BindSurfaces(ZoneTerrainSurfaceBindings surfaces)
    {
        lock (Gate)
        {
            if (_surfaces is not null) throw new InvalidOperationException("The terrain tree already has its surface owner.");
            _surfaces = surfaces;
        }
    }

    internal ZoneTerrainSurfaceBindings RequireSurfaces() => _surfaces ??
        throw new InvalidOperationException("The terrain tree has not been bound to the engine's surface catalog.");

    // Called under Heights.Gate after validating/allocating the entire cell. Assignments below
    // do not allocate, so malformed data cannot publish half a new cell over the previous payloads.
    internal void InstallCellCore(int cellX, int cellY, ZoneTerrainHeightField.NodeSamples[] samples)
    {
        foreach (var sample in samples)
        {
            var x = cellX * 1024 + sample.LocalX;
            var y = cellY * 1024 + sample.LocalY;
            var node = _levels[sample.Level][(x / sample.SizeMeters) * (128 >> sample.Level) + y / sample.SizeMeters];
            var min = sample.AuthoredMin;
            var max = sample.AuthoredMax;
            max.Z = OceanWaterLevel <= max.Z ? max.Z : OceanWaterLevel;
            if (min.X != x) { max.X = (x - min.X) + max.X; min.X = x; }
            if (min.Y != y) { max.Y = (y - min.Y) + max.Y; min.Y = y; }
            node.BoundsValue = new(min, max);
            node.Samples = sample;
            node.LodErrorsValue = sample.LodErrors;
            node.SurfaceSlots = sample.SurfaceSlots;
            node.HoleFlagsValue = (byte)(sample.HoleFlags & 3);
        }
    }

    /// <summary>300E4EC0 then 300E5000: refresh the cell subtree and ancestors above cell level.</summary>
    public void RefreshBounds(int cellX, int cellY)
    {
        var cell = GetCellRoot(cellX, cellY);
        lock (Gate)
        {
            Refresh(cell, false);
            Refresh(Root, true);
        }

        static void Refresh(ZoneTerrainNode node, bool stopAtCells)
        {
            if (node.Level == 0 || (stopAtCells && node.Level == 4)) return;
            foreach (var child in node.Children) Refresh(child, stopAtCells);
            Aggregate(node, 9999999);
        }
    }

    internal void UnloadCellCore(int cellX, int cellY) => Unload(GetCellRoot(cellX, cellY));

    private static void Unload(ZoneTerrainNode node)
    {
        node.Samples = null;
        node.SurfaceSlots = [];
        var min = node.BoundsValue.Min; var max = node.BoundsValue.Max;
        min.Z = 1; max.Z = -1;
        node.BoundsValue = new(min, max);
        // LOD errors, hole flags, texture indices, structural nodes and surface bindings survive.
        foreach (var child in node.Children) Unload(child);
    }

    /// <summary>300D1560: neighbor slot to first matching own slot; 300EA930 dirties the subtree.</summary>
    public void RefreshEdgeMaterials(int cellX, int cellY, bool positiveY, bool positiveX)
    {
        var cell = GetCellRoot(cellX, cellY);
        lock (Gate)
        {
            var surfaces = RequireSurfaces();
            var index = cellX * 8 + cellY;
            if (positiveX) Refresh(_positiveX[index], cellX + 1, cellY);
            if (positiveY) Refresh(_positiveY[index], cellX, cellY + 1);
            MarkDirty(cell);

            void Refresh(int[] map, int nextX, int nextY)
            {
                Array.Clear(map);
                if (nextX == 8 || nextY == 8) return;
                for (var neighbor = 0; neighbor < 32; neighbor++)
                    for (var own = 0; own < 32; own++)
                        if (surfaces.GetBinding(nextX, nextY, neighbor).Surface.Id ==
                            surfaces.GetBinding(cellX, cellY, own).Surface.Id)
                        { map[neighbor] = own; break; }
            }
        }
    }

    public int[] CopyEdgeMaterialMap(int cellX, int cellY, bool positiveX)
    {
        _ = GetCellRoot(cellX, cellY);
        lock (Gate) return (positiveX ? _positiveX : _positiveY)[cellX * 8 + cellY].ToArray();
    }

    private static void MarkDirty(ZoneTerrainNode node)
    {
        node.Dirty = true;
        foreach (var child in node.Children) MarkDirty(child);
    }

    private static void Aggregate(ZoneTerrainNode node, float limit)
    {
        var min = new Vector3(limit); var max = new Vector3(-limit);
        foreach (var child in node.Children)
        {
            var next = child.BoundsValue;
            // Keep the recovered comparison/operand order, including NaNs and signed-zero ties.
            min.X = next.Min.X <= min.X ? next.Min.X : min.X;
            min.Y = min.Y < next.Min.Y ? min.Y : next.Min.Y;
            min.Z = min.Z < next.Min.Z ? min.Z : next.Min.Z;
            max.X = max.X <= next.Max.X ? next.Max.X : max.X;
            max.Y = next.Max.Y < max.Y ? max.Y : next.Max.Y;
            max.Z = next.Max.Z < max.Z ? max.Z : next.Max.Z;
        }
        node.BoundsValue = new(min, max);
    }
}
