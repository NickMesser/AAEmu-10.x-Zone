using System.Buffers.Binary;
using System.Numerics;

using AAEmu.Zone.Content;

namespace AAEmu.Zone.Navigation;

/// <summary>
/// Native 310FF348/310FB7E8 paired connections and 31100518 individual-link detach.
/// Owns graph adjacency and node references; regional services and complete navigation startup
/// remain separate. Whole-node disconnection with regional root reattachment is not this API.
/// Loading uses dedicated Zone mode2: the native mode1-only authored-link filter does not apply.
/// </summary>
public sealed class ZoneNavigationGraphConnections : IDisposable
{
    private bool _disposed;
    private const uint MaximumPackedNodeId = 0xfffff;
    private readonly ZoneNavigationGraphNodeStage _nodes;
    private readonly ZoneNavigationGraphLinkPool _links = new();
    public IEnumerable<ZoneNavigationGraphNode> Nodes => _nodes.Pool.AllocatedNodes;
    public IReadOnlyDictionary<uint, uint> AuthoredIds => _nodes.AuthoredIds;
    public IReadOnlyList<uint> RowRuntimeIds => _nodes.RowRuntimeIds;
    public int AllocatedPairCount => _links.AllocatedPairCount;
    public IEnumerable<uint> AllocatedPairs => _links.AllocatedPairs;
    public uint InitialConnectionNodeId { get; private set; }
    public bool IsDisposed => _disposed;
    public uint RootId => _disposed ? 0 : _nodes.RootId;

    private ZoneNavigationGraphConnections(ZoneNavigationGraphNodeStage nodes)
    { _nodes = nodes; InitialConnectionNodeId = nodes.RootId; }

    public ZoneNavigationGraphNode? GetNode(uint runtimeId) => _nodes.Pool.Get(runtimeId);
    public ZoneNavigationGraphNode? FindReusableNode(Vector3 position, uint navigationType)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _nodes.FindReusable(position, navigationType);
    }
    public byte[] SnapshotPair(uint linkId) => _links.SnapshotPair(linkId);
    public uint GetNextLink(uint linkId) => _links.GetNext(linkId);
    public uint GetLinkTarget(uint linkId) => _links.GetTarget(linkId);

    public static ZoneNavigationGraphConnections Load(ZoneNavigationLegacyGraphContent content,
        Func<Vector3, int, int?>? resolveModifierId = null)
    {
        var owner = new ZoneNavigationGraphConnections(ZoneNavigationGraphNodeStage.Load(content, resolveModifierId));
        // Native truncates targets to20 bits. Reject an unsupported graph before linking it,
        // rather than retain adjacency to a different node after that truncation.
        if (owner.Nodes.Any(node => node.RuntimeId > MaximumPackedNodeId))
            throw new InvalidDataException("Graph runtime node IDs exceed the native20-bit link-target capacity.");
        foreach (var row in content.Links)
        {
            if (!owner.AuthoredIds.TryGetValue(row.SourceNodeId, out var source) ||
                !owner.AuthoredIds.TryGetValue(row.TargetNodeId, out var target) ||
                source == owner.RootId || target == owner.RootId) continue;
            var pair = owner.Connect(source, target, BinaryPrimitives.ReadSingleLittleEndian(row.SerializedBytes[20..]), 0);
            if (pair.Forward == 0) continue;
            owner._links.ApplyAuthoredMetadata(pair.Forward, row.SerializedBytes);
            owner._links.RestoreOriginalCost(pair.Reverse);
        }
        return owner;
    }

    public (uint Forward, uint Reverse) Connect(uint sourceId, uint targetId, float forwardCost, float reverseCost)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sourceId, MaximumPackedNodeId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targetId, MaximumPackedNodeId);
        var source = _nodes.Pool.Get(sourceId);
        var target = _nodes.Pool.Get(targetId);
        if (source is null || target is null || source == target) return default;
        var root = _nodes.Pool.Get(RootId)!;
        if ((source == root || target == root) && root.FirstLinkId != 0) return default;
        var forward = FindLink(sourceId, targetId);
        var reverse = FindLink(targetId, sourceId);
        if ((forward == 0) != (reverse == 0))
            throw new InvalidOperationException("Native graph connection requires both directed halves to have the same ownership state.");
        if (forward == 0)
        {
            forward = _links.Allocate(); reverse = forward ^ 1;
            if (reverse > 0x7fffff)
            {
                _links.Release(forward);
                throw new InvalidOperationException("Graph links exceed the native23-bit adjacency capacity.");
            }
            Append(source, forward); Append(target, reverse);
            _links.SetTarget(forward, targetId); _links.SetTarget(reverse, sourceId);
            _links.SetCost(forward, target == root ? -1 : forwardCost, true);
            _links.SetCost(reverse, source == root ? -1 : reverseCost, true);
            _links.SetMidpoint(forward, new Vector3((source.Position.X + target.Position.X) * 0.5f,
                (source.Position.Y + target.Position.Y) * 0.5f, (source.Position.Z + target.Position.Z) * 0.5f));
            source.ReferenceCount = unchecked((short)(source.ReferenceCount + 1));
            target.ReferenceCount = unchecked((short)(target.ReferenceCount + 1));
        }
        else
        {
            // Native zero is the keep-current-cost sentinel when reconnecting an existing pair.
            if (forwardCost != 0) _links.SetCost(forward, forwardCost, false);
            if (reverseCost != 0) _links.SetCost(reverse, reverseCost, false);
        }
        if (root.FirstLinkId == 0)
        {
            var initial = source.NavigationType == 2 ? source : target.NavigationType == 2 ? target : null;
            if (initial is not null)
            {
                Connect(RootId, initial.RuntimeId, 100, -1);
                InitialConnectionNodeId = initial.RuntimeId;
            }
        }
        return (forward, reverse);
    }

    public uint FindLink(uint sourceId, uint targetId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var current = _nodes.Pool.Get(sourceId)?.FirstLinkId ?? 0;
        while (current != 0)
        {
            if (_links.GetTarget(current) == targetId) return current;
            current = _links.GetNext(current);
        }
        return 0;
    }

    /// <summary>Detaches one native pair without the automatic root reattachment of whole-node disconnect.</summary>
    public bool DisconnectLink(uint sourceId, uint directedLinkId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var source = _nodes.Pool.Get(sourceId);
        if (source is null || !_links.IsAllocated(directedLinkId)) return false;
        var target = _nodes.Pool.Get(_links.GetTarget(directedLinkId));
        if (target is null || FindLink(sourceId, target.RuntimeId) != directedLinkId) return false;
        RemoveTarget(source, target.RuntimeId); RemoveTarget(target, sourceId);
        source.ReferenceCount = unchecked((short)(source.ReferenceCount - 1));
        target.ReferenceCount = unchecked((short)(target.ReferenceCount - 1));
        _links.Release(directedLinkId);
        return true;
    }

    public bool DeleteUnconnectedNode(uint runtimeId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var node = _nodes.Pool.Get(runtimeId);
        if (node is null) return false;
        if (runtimeId == RootId) throw new InvalidOperationException("The safe root is released by graph teardown.");
        if (node.FirstLinkId != 0 || node.ReferenceCount != 1)
            throw new InvalidOperationException("Disconnect every pair before releasing the graph's node reference.");
        node.ReferenceCount--;
        return _nodes.ReleaseNodeStorage(node);
    }

    public void Dispose()
    {
        if (_disposed) return;
        foreach (var pair in _links.AllocatedPairs.ToArray())
            DisconnectLink(_links.GetTarget(pair ^ 1), pair);
        foreach (var node in _nodes.Pool.AllocatedNodes.ToArray())
        {
            if (node.ReferenceCount != 1) throw new InvalidOperationException("Graph teardown encountered an external node reference.");
            node.ReferenceCount--;
            _nodes.ReleaseNodeStorage(node);
        }
        _nodes.Pool.ReleaseEmptyBlocks(0x1ff);
        _disposed = true;
        InitialConnectionNodeId = 0;
    }

    private void Append(ZoneNavigationGraphNode node, uint link)
    {
        if (node.FirstLinkId == 0) { node.FirstLinkId = link; return; }
        var previous = node.FirstLinkId;
        while (_links.GetNext(previous) != 0) previous = _links.GetNext(previous);
        _links.SetNext(previous, link);
    }

    private void RemoveTarget(ZoneNavigationGraphNode node, uint targetId)
    {
        uint previous = 0;
        var current = node.FirstLinkId;
        while (current != 0)
        {
            var next = _links.GetNext(current);
            if (_links.GetTarget(current) == targetId)
            {
                if (previous == 0) node.FirstLinkId = next; else _links.SetNext(previous, next);
            }
            else previous = current;
            current = next;
        }
    }
}
