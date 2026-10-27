using System.Numerics;
namespace AAEmu.Zone.Simulation;

/// <summary>A node owned by the typed native graph pool; runtime and authored IDs are distinct.</summary>
public sealed class ZoneNavigationGraphNode
{
    public uint RuntimeId { get; }
    public uint AuthoredId { get; }
    public uint NavigationType { get; }
    public Vector3 Position { get; internal set; }
    public bool IsAllocated { get; internal set; } = true;
    public uint FirstLinkId { get; internal set; }
    public short ReferenceCount { get; internal set; }
    public int RegionId { get; internal set; } = -1;
    public byte Subtype { get; internal set; }
    public byte Flags { get; internal set; }
    public IReadOnlyList<int> VertexIndices { get; internal set; } = Array.Empty<int>();
    public IReadOnlyList<uint> Parameters { get; internal set; } = Array.Empty<uint>();
    internal ZoneNavigationGraphNode(uint runtimeId, uint authoredId, uint type, Vector3 position)
    { RuntimeId = runtimeId; AuthoredId = authoredId; NavigationType = type; Position = position; }
}

/// <summary>
/// Native 3110B308/3110B3B8/3110B708 typed node storage. Each type reserves 128-slot blocks;
/// released slots are reused within their type, while authored IDs have a separate unsigned set.
/// This pool does not own graph adjacency, references or regional registration callbacks.
/// </summary>
public sealed class ZoneNavigationGraphNodePool
{
    private sealed class Block(uint type)
    {
        internal uint Type { get; } = type;
        internal ZoneNavigationGraphNode?[] Rows { get; } = new ZoneNavigationGraphNode?[128];
    }
    private readonly List<Block?> _blocks = [];
    private readonly int[] _lastBlocks = Enumerable.Repeat(-1, 9).ToArray();
    private readonly int[] _usedSlots = Enumerable.Repeat(128, 9).ToArray();
    private readonly Stack<uint>[] _free = Enumerable.Range(0, 9).Select(_ => new Stack<uint>()).ToArray();
    private readonly SortedSet<uint> _releasedAuthoredIds = [];
    private uint _greatestAuthoredId;
    public int BlockCount => _blocks.Count;
    public IEnumerable<ZoneNavigationGraphNode> AllocatedNodes => _blocks.Where(block => block is not null)
        .SelectMany(block => block!.Rows).Where(node => node is { IsAllocated: true }).Select(node => node!);

    public ZoneNavigationGraphNode Allocate(uint navigationType, Vector3 position, uint authoredId = 0)
    {
        var typeIndex = TypeIndex(navigationType);
        uint runtimeId;
        if (_free[typeIndex].TryPop(out var released)) runtimeId = released;
        else
        {
            if (_usedSlots[typeIndex] >= 128)
            {
                var index = _blocks.Count;
                // 3110B3B8 scans every hole and retains the last one, not the first.
                for (var i = 0; i < _blocks.Count; i++) if (_blocks[i] is null) index = i;
                var block = new Block(navigationType);
                if (index == _blocks.Count) _blocks.Add(block); else _blocks[index] = block;
                _lastBlocks[typeIndex] = index;
                _usedSlots[typeIndex] = 0;
            }
            runtimeId = checked((uint)(_lastBlocks[typeIndex] * 128 + 1 + _usedSlots[typeIndex]++));
        }
        if (authoredId == 0)
        {
            if (_releasedAuthoredIds.Count != 0)
            { authoredId = _releasedAuthoredIds.Min; _releasedAuthoredIds.Remove(authoredId); }
            else authoredId = unchecked(_greatestAuthoredId + 1);
        }
        if (_greatestAuthoredId < authoredId) _greatestAuthoredId = authoredId;
        var node = new ZoneNavigationGraphNode(runtimeId, authoredId, navigationType, position);
        if (navigationType == 64) node.RegionId = 0;
        if (navigationType is 4 or 8) node.Parameters = Array.AsReadOnly(new uint[] { 0, 0, 0, 0, 0, 0x3f800000 });
        _blocks[(int)((runtimeId - 1) >> 7)]!.Rows[(runtimeId - 1) & 127] = node;
        return node;
    }

    public ZoneNavigationGraphNode? Get(uint runtimeId)
    {
        if (runtimeId == 0) return null;
        var index = (runtimeId - 1) >> 7;
        if (index >= _blocks.Count) return null;
        var node = _blocks[(int)index]?.Rows[(runtimeId - 1) & 127];
        return node is { IsAllocated: true } ? node : null;
    }

    /// <summary>The graph owner must disconnect and release references before releasing storage.</summary>
    public bool Release(uint runtimeId)
    {
        var node = Get(runtimeId);
        if (node is null) return false;
        if (node.FirstLinkId != 0 || node.ReferenceCount != 0)
            throw new InvalidOperationException("Connected or referenced graph nodes cannot release their pool storage.");
        _releasedAuthoredIds.Add(node.AuthoredId);
        _free[TypeIndex(node.NavigationType)].Push(runtimeId);
        node.IsAllocated = false;
        node.VertexIndices = Array.Empty<int>();
        return true;
    }

    /// <summary>
    /// Native 3110B148 block release. Call after owner teardown; this operation does not add
    /// authored IDs to the release set. The block-index vector keeps holes for later allocation.
    /// </summary>
    public void ReleaseEmptyBlocks(uint navigationMask)
    {
        foreach (var node in AllocatedNodes)
            if ((node.NavigationType & navigationMask) != 0)
                throw new InvalidOperationException("Graph nodes must be released before their typed blocks.");
        for (var i = 0; i < _blocks.Count; i++)
            if (_blocks[i] is { } block && (block.Type & navigationMask) != 0) _blocks[i] = null;
        for (var i = 0; i < 9; i++)
            if ((navigationMask & (1u << i)) != 0)
            { _lastBlocks[i] = -1; _usedSlots[i] = 128; _free[i].Clear(); }
    }

    private static int TypeIndex(uint type)
    {
        if (type == 0 || type > 256 || (type & (type - 1)) != 0)
            throw new NotSupportedException($"Graph node type {type} has no supported native constructor.");
        return BitOperations.Log2(type);
    }
}
