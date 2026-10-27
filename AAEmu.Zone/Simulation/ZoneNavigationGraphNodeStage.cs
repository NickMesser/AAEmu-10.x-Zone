using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Numerics;
using AAEmu.Zone.Content;
namespace AAEmu.Zone.Simulation;

/// <summary>
/// The node stage of native 31103988/31102C18, including initial safe node, typed storage,
/// nearby reuse and first-entry authored-ID admission. Links and regional registrations are
/// separate owner stages; completing this stage does not make navigation ready.
/// </summary>
public sealed class ZoneNavigationGraphNodeStage
{
    private readonly Dictionary<uint, uint> _authoredIds = [];
    private readonly ZoneNavigationGraphCandidateIndex _index;
    private readonly List<uint> _rowRuntimeIds = [];
    public ZoneNavigationGraphNodePool Pool { get; } = new();
    public uint RootId { get; private set; }
    public IReadOnlyDictionary<uint, uint> AuthoredIds { get; }
    public IReadOnlyList<uint> RowRuntimeIds { get; }

    private ZoneNavigationGraphNodeStage()
    {
        _index = new ZoneNavigationGraphCandidateIndex(Pool);
        AuthoredIds = new ReadOnlyDictionary<uint, uint>(_authoredIds);
        RowRuntimeIds = _rowRuntimeIds.AsReadOnly();
        var initial = Pool.Allocate(1, Vector3.Zero);
        initial.ReferenceCount = 1;
        _index.Add(initial);
        RootId = initial.RuntimeId;
    }

    public static ZoneNavigationGraphNodeStage Load(ZoneNavigationLegacyGraphContent content,
        Func<Vector3, int, int?>? resolveModifierId = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        // A type4/8 modifier query has observable runtime-ID and publication effects.
        if (resolveModifierId is null && content.Nodes.Any(row => row.NavigationType is 4 or 8))
            throw new ArgumentNullException(nameof(resolveModifierId), "Type4/8 nodes require the native modifier owner query.");
        var stage = new ZoneNavigationGraphNodeStage();
        foreach (var row in content.Nodes)
        {
            var node = stage.FindReusable(row.Position, row.NavigationType);
            if (node is null)
            {
                node = stage.Pool.Allocate(row.NavigationType, row.Position, row.NodeId);
                node.ReferenceCount = 1;
                stage._index.Add(node);
                switch (row.NavigationType)
                {
                    case 2:
                        var count = row.VertexIndices[2] >= 0 ? 3 : row.VertexIndices[1] >= 0 ? 2 : row.VertexIndices[0] >= 0 ? 1 : 0;
                        node.VertexIndices = Array.AsReadOnly(row.VertexIndices.Take(count).ToArray());
                        node.Flags = (byte)((row.Flags >> 4) & 3);
                        break;
                    case 4:
                    case 8:
                        node.RegionId = resolveModifierId!(row.Position, row.NavigationType == 4 ? 0 : 4) ?? -1;
                        node.Subtype = (byte)(row.Flags & 15);
                        node.Flags = row.Flags;
                        node.Parameters = Array.AsReadOnly(Enumerable.Range(0, 6)
                            .Select(i => BinaryPrimitives.ReadUInt32LittleEndian(row.SerializedBytes[(4 + i * 4)..])).ToArray());
                        break;
                    case 16:
                    case 32:
                        node.RegionId = BinaryPrimitives.ReadInt32LittleEndian(row.SerializedBytes[40..]);
                        break;
                    case 64:
                        node.RegionId = BinaryPrimitives.ReadInt32LittleEndian(row.SerializedBytes[4..]);
                        break;
                }
            }
            stage._rowRuntimeIds.Add(node.RuntimeId);
            // Native creates or reuses the row before attempting insertion. A rejected duplicate
            // can therefore leave an allocated orphan which remains owned by the graph pool.
            stage._authoredIds.TryAdd(row.NodeId, node.RuntimeId);
        }
        if (!stage._authoredIds.TryGetValue(1, out var root))
            throw new InvalidDataException("Native graph loading requires authored first-node key1.");
        var oldRoot = stage.Pool.Get(stage.RootId)!;
        oldRoot.ReferenceCount--;
        stage.ReleaseNodeStorage(oldRoot);
        stage.RootId = root;
        return stage;
    }

    public ZoneNavigationGraphNode? FindReusable(Vector3 position, uint navigationType)
        => _index.FindReusable(position, navigationType);

    internal bool ReleaseNodeStorage(ZoneNavigationGraphNode node)
    {
        _index.Remove(node);
        return Pool.Release(node.RuntimeId);
    }
}
