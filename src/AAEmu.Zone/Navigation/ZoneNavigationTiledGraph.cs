using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Numerics;
using AAEmu.Zone.Content;

namespace AAEmu.Zone.Navigation;

/// <summary>A copied obstacle vertex. The complete winning 32-byte record remains available.</summary>
public sealed class ZoneNavigationGraphVertex
{
    private readonly byte[] _bytes;
    public Vector3 Position { get; }
    public ReadOnlySpan<byte> SerializedBytes => _bytes;
    internal ZoneNavigationGraphVertex(ReadOnlySpan<byte> source, Vector3 position)
    {
        _bytes = source.ToArray(); Position = position;
        BinaryPrimitives.WriteSingleLittleEndian(_bytes, position.X);
        BinaryPrimitives.WriteSingleLittleEndian(_bytes.AsSpan(4), position.Y);
    }
}

/// <summary>Immutable runtime topology, separate from a tile's authored ID namespace.</summary>
public sealed record ZoneNavigationTiledNode(uint Id, uint NavigationType, Vector3 Position,
    int RegionId, byte Flags, byte Subtype, IReadOnlyList<int> VertexIndices, IReadOnlyList<uint> Parameters,
    IReadOnlyList<ZoneNavigationTiledLink> Links);

/// <summary>
/// A directed paired-link half. The stored scalar uses native signed-centimeter quantization;
/// serialized metadata remains copied rather than assigning unproved traversal meanings to it.
/// </summary>
public sealed class ZoneNavigationTiledLink
{
    private readonly byte[] _pair;
    public uint Id { get; }
    public uint TargetId { get; }
    public short QuantizedTraversalLimit { get; }
    public short OriginalQuantizedTraversalLimit { get; }
    public float TraversalLimit => QuantizedTraversalLimit * .01f;
    /// <summary>3104FB18 compares this clearance to the actor's pass radius, inclusive.</summary>
    public float PassRadius => TraversalLimit;
    public Vector3 Midpoint { get; }
    public ReadOnlySpan<byte> RuntimePairBytes => _pair;
    internal ZoneNavigationTiledLink(uint id, uint target, byte[] pair)
    {
        Id = id; TargetId = target; _pair = pair;
        var direction = (int)(id & 1) * 12;
        QuantizedTraversalLimit = BinaryPrimitives.ReadInt16LittleEndian(pair.AsSpan(direction));
        OriginalQuantizedTraversalLimit = BinaryPrimitives.ReadInt16LittleEndian(pair.AsSpan(direction + 2));
        Midpoint = new(BinaryPrimitives.ReadSingleLittleEndian(pair.AsSpan(28)),
            BinaryPrimitives.ReadSingleLittleEndian(pair.AsSpan(32)), BinaryPrimitives.ReadSingleLittleEndian(pair.AsSpan(36)));
    }
}

/// <summary>First byte-name admission within one native shape container.</summary>
public sealed class ZoneNavigationTiledArea
{
    private readonly ZoneNavigationShape _shape;
    public ZoneNavigationAreaName Name { get; }
    public IReadOnlyList<Vector3> Points => _shape.Points;
    public Vector3 BoundsMinimum => _shape.BoundsMinimum;
    public Vector3 BoundsMaximum => _shape.BoundsMaximum;
    internal ZoneNavigationTiledArea(ZoneNavigationAreaRow row) { Name = row.Name; _shape = new(row.Shape.Points); }
    public bool Contains(Vector3 position) => _shape.Contains(position);
}

public sealed record ZoneNavigationTileTopology(ZoneMapCoordinate Coordinate,
    IReadOnlyDictionary<uint, uint> AuthoredNodeIds, IReadOnlyList<uint> RowNodeIds,
    IReadOnlyList<int> VertexIndices, int AuthoredLinkCount);

/// <summary>One immutable publication. Retaining it is diagnostic data, not a live graph lease.</summary>
public sealed class ZoneNavigationTiledGraphSnapshot
{
    public ulong Revision { get; }
    public IReadOnlyDictionary<uint, ZoneNavigationTiledNode> Nodes { get; }
    public IReadOnlyList<ZoneNavigationGraphVertex> Vertices { get; }
    public IReadOnlyList<ZoneNavigationTileTopology> Tiles { get; }
    public IReadOnlyList<ZoneNavigationTileLoadResult> TileLoads { get; }
    public IReadOnlyList<ZoneNavigationTiledArea> ForbiddenAreas { get; }
    public IReadOnlyList<ZoneNavigationTiledArea> DesignerForbiddenAreas { get; }
    public IReadOnlyList<ZoneNavigationTiledArea> ForbiddenBoundaries { get; }
    public IReadOnlyList<ZoneNavigationRuntimeModifier> Modifiers { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    public int AllocatedLinkPairCount { get; }
    public bool AllRequestedTilesLoaded => TileLoads.Count != 0 && TileLoads.All(tile => tile.Status == ZoneNavigationTileLoadStatus.Loaded);

    internal readonly ZoneNavigationModifierRuntime ModifierOwner;
    internal readonly ZoneNavigationModifierQuery ModifierQuery;
    internal bool Current = true;
    internal ZoneNavigationTiledGraphSnapshot(ulong revision, Dictionary<uint, ZoneNavigationTiledNode> nodes,
        List<ZoneNavigationGraphVertex> vertices, List<ZoneNavigationTileTopology> tiles,
        ZoneNavigationTileLoadResult[] loads, List<ZoneNavigationTiledArea> forbidden,
        List<ZoneNavigationTiledArea> designer, List<ZoneNavigationTiledArea> boundaries,
        ZoneNavigationModifierRuntime modifiers, List<string> diagnostics, int pairs)
    {
        Revision = revision; Nodes = new ReadOnlyDictionary<uint, ZoneNavigationTiledNode>(nodes);
        Vertices = vertices.AsReadOnly(); Tiles = tiles.AsReadOnly(); TileLoads = Array.AsReadOnly(loads);
        ForbiddenAreas = forbidden.AsReadOnly(); DesignerForbiddenAreas = designer.AsReadOnly(); ForbiddenBoundaries = boundaries.AsReadOnly();
        ModifierOwner = modifiers; ModifierQuery = new(modifiers); Modifiers = Array.AsReadOnly(modifiers.Modifiers.ToArray());
        Diagnostics = diagnostics.AsReadOnly(); AllocatedLinkPairCount = pairs;
    }
}

/// <summary>
/// A scene's streamed graph owner. Imports preserve native XY vertex welding, typed node reuse,
/// per-tile authored-ID maps and paired adjacency. Tile node1 is an ordinary imported node:
/// this path never replaces the dedicated loader's global safe root or invents border links.
/// Tile retirement rebuilds a fresh publication; pending paths must validate its revision.
/// Calls belong to the scene dispatcher, independently of the single physics writer.
/// </summary>
public sealed partial class ZoneNavigationTiledGraph : IDisposable
{
    private ZoneNavigationTiledGraphSnapshot _snapshot;
    private readonly HashSet<ZoneNavigationPathFollower> _followers = [];
    public ZoneMapCoordinate ZoneOrigin { get; }
    public bool IsDisposed { get; private set; }
    public ZoneNavigationTiledGraphSnapshot Snapshot { get { ObjectDisposedException.ThrowIf(IsDisposed, this); return _snapshot; } }

    public ZoneNavigationTiledGraph(ZoneMapCoordinate zoneOrigin, IEnumerable<ZoneNavigationTileLoadResult> tiles)
    {
        ArgumentNullException.ThrowIfNull(tiles); ZoneOrigin = zoneOrigin;
        _snapshot = Build(zoneOrigin, tiles.ToArray(), 1);
    }

    public static ZoneNavigationTiledGraph Load(IZoneAssetSource assets, string worldName, ZoneWorldMapZone zone)
        => new(zone.Origin, ZoneNavigationTileContent.LoadZone(assets, worldName, zone));

    public bool RetireTile(ZoneMapCoordinate coordinate)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (!_snapshot.TileLoads.Any(load => load.Coordinate == coordinate)) return false;
        var replacement = Build(ZoneOrigin, _snapshot.TileLoads.Where(load => load.Coordinate != coordinate).ToArray(), checked(_snapshot.Revision + 1));
        _snapshot.Current = false; _snapshot.ModifierOwner.Reset(); _snapshot = replacement; _queryIndex = null;
        RetireFollowers();
        return true;
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true; _snapshot.Current = false; _snapshot.ModifierOwner.Reset(); _queryIndex = null;
        RetireFollowers();
    }

    internal void RegisterFollower(ZoneNavigationPathFollower follower)
    { ObjectDisposedException.ThrowIf(IsDisposed, this); _followers.Add(follower); }
    internal void UnregisterFollower(ZoneNavigationPathFollower follower) => _followers.Remove(follower);
    private void RetireFollowers()
    {
        List<Exception>? errors = null;
        foreach (var follower in _followers.ToArray())
            try { follower.RetirePublication(); } catch (Exception error) { (errors ??= []).Add(error); }
        if (errors is not null) throw new AggregateException("Navigation retired, but cancellation of an old movement wish failed.", errors);
    }

    private static ZoneNavigationTiledGraphSnapshot Build(ZoneMapCoordinate origin, ZoneNavigationTileLoadResult[] loads, ulong revision)
    {
        var seen = new HashSet<ZoneMapCoordinate>();
        string? worldRoot = null;
        foreach (var load in loads)
        {
            if (!seen.Add(load.Coordinate)) throw new ArgumentException("A navigation tile may appear only once in a publication.", nameof(loads));
            var marker = load.RootPath.LastIndexOf("/paths/", StringComparison.Ordinal);
            if (marker < 0) throw new ArgumentException("A streamed navigation result has no world tile root.", nameof(loads));
            var currentRoot = load.RootPath[..marker]; worldRoot ??= currentRoot;
            if (!StringComparer.Ordinal.Equals(worldRoot, currentRoot))
                throw new ArgumentException("One navigation publication cannot combine tiles from different worlds.", nameof(loads));
            if (load.Status == ZoneNavigationTileLoadStatus.Loaded != (load.Content is not null) ||
                load.Content is { } content && (content.Coordinate != load.Coordinate || content.ZoneOrigin != origin || content.RootPath != load.RootPath))
                throw new ArgumentException("Navigation tile result, origin and admitted content disagree.", nameof(loads));
        }
        return new Builder().Build(loads, revision);
    }

    private sealed class Builder
    {
        private readonly ZoneNavigationGraphNodePool _pool = new();
        private readonly ZoneNavigationGraphCandidateIndex _nodes;
        private readonly ZoneNavigationGraphLinkPool _links = new();
        private readonly Dictionary<uint, List<uint>> _adjacency = [];
        private readonly Dictionary<(uint, uint), uint> _directions = [];
        private readonly List<ZoneNavigationGraphVertex> _vertices = [];
        private readonly Dictionary<(long, long), List<int>> _vertexCells = [];
        private readonly ZoneNavigationModifierRuntime _modifiers = new();
        private readonly ZoneNavigationModifierQuery _modifierQuery;
        private readonly List<ZoneNavigationTileTopology> _tiles = [];
        private readonly List<string> _diagnostics = [];
        private readonly List<ZoneNavigationTiledArea> _forbidden = [], _designer = [], _boundaries = [];
        private readonly HashSet<string> _forbiddenNames = new(StringComparer.Ordinal), _designerNames = new(StringComparer.Ordinal), _boundaryNames = new(StringComparer.Ordinal);
        internal Builder() { _nodes = new(_pool); _modifierQuery = new(_modifiers); }

        internal ZoneNavigationTiledGraphSnapshot Build(ZoneNavigationTileLoadResult[] loads, ulong revision)
        {
            foreach (var load in loads)
            {
                if (load.Content is not { } tile) { _diagnostics.Add(load.Diagnostic ?? "Navigation tile unavailable."); continue; }
                Import(tile);
            }
            var nodes = _pool.AllocatedNodes.ToDictionary(node => node.RuntimeId, node =>
                new ZoneNavigationTiledNode(node.RuntimeId, node.NavigationType, node.Position, node.RegionId,
                    node.Flags, node.Subtype, Array.AsReadOnly(node.VertexIndices.ToArray()), Array.AsReadOnly(node.Parameters.ToArray()),
                    Array.AsReadOnly(_adjacency[node.RuntimeId].Select(link => new ZoneNavigationTiledLink(link,
                        _links.GetTarget(link), _links.SnapshotPair(link))).ToArray())));
            return new(revision, nodes, _vertices, _tiles, loads, _forbidden, _designer, _boundaries, _modifiers, _diagnostics, _links.AllocatedPairCount);
        }

        private void Import(ZoneNavigationTileContent tile)
        {
            // 31149764 publishes areas before this tile's node/modifier queries.
            Areas(tile.Areas.ForbiddenAreaRows, _forbiddenNames, _forbidden);
            Areas(tile.Areas.DesignerForbiddenAreaRows, _designerNames, _designer);
            Areas(tile.Areas.ForbiddenBoundaryRows, _boundaryNames, _boundaries);
            _modifiers.ImportStreaming(tile.Areas);
            var vertices = tile.Vertices.Rows.Select(row => WeldVertex(row, tile)).ToArray();
            var map = new Dictionary<uint, uint>(); var rows = new List<uint>();
            foreach (var row in tile.Network.Nodes)
            {
                var position = tile.Translate(row.Position); Finite(position);
                var node = _nodes.FindReusable(position, row.NavigationType);
                if (node is null)
                {
                    node = _pool.Allocate(row.NavigationType, position, row.NodeId);
                    if (node.RuntimeId > 0xfffff) throw new NotSupportedException("Combined navigation graph exceeds native20-bit link targets.");
                    _nodes.Add(node); _adjacency.Add(node.RuntimeId, []);
                    if (row.NavigationType == 2)
                    {
                        var indices = row.VertexIndices.ToArray();
                        for (var index = 0; index < 3; index++)
                        {
                            if (indices[index] < 0) continue;
                            if (indices[index] >= vertices.Length) { indices[0] = -1; break; }
                            indices[index] = vertices[indices[index]];
                        }
                        var count = indices[2] >= 0 ? 3 : indices[1] >= 0 ? 2 : indices[0] >= 0 ? 1 : 0;
                        node.VertexIndices = Array.AsReadOnly(indices.Take(count).ToArray()); node.Flags = (byte)((row.Flags >> 4) & 3);
                        if (node.VertexIndices.Any(index => index < 0 || index >= _vertices.Count))
                            _diagnostics.Add($"Tile {tile.Coordinate}, node {row.NodeId} contains an invalid obstacle vertex reference.");
                    }
                    else if (row.NavigationType is 4 or 8)
                    {
                        node.RegionId = _modifierQuery.ResolveModifierId(position, row.NavigationType == 4 ? 0 : 4) ?? -1;
                        if (node.RegionId < 0) _diagnostics.Add($"Tile {tile.Coordinate}, node {row.NodeId}: navigation type {row.NavigationType} has no containing authored modifier.");
                        node.Subtype = (byte)(row.Flags & 15); node.Flags = row.Flags;
                        node.Parameters = Array.AsReadOnly(Enumerable.Range(0, 6).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(row.SerializedBytes[(4 + i * 4)..])).ToArray());
                    }
                    else if (row.NavigationType is 16 or 32) node.RegionId = BinaryPrimitives.ReadInt32LittleEndian(row.SerializedBytes[40..]);
                    else if (row.NavigationType == 64) node.RegionId = BinaryPrimitives.ReadInt32LittleEndian(row.SerializedBytes[4..]);
                }
                rows.Add(node.RuntimeId); map.TryAdd(row.NodeId, node.RuntimeId);
            }
            foreach (var row in tile.Network.Links)
            {
                if (!map.TryGetValue(row.SourceNodeId, out var source) || !map.TryGetValue(row.TargetNodeId, out var target) || source == target) continue;
                var scalar = BinaryPrimitives.ReadSingleLittleEndian(row.SerializedBytes[20..]);
                var id = Connect(source, target, scalar);
                var translated = row.SerializedBytes.ToArray();
                var midpoint = tile.Translate(new(BinaryPrimitives.ReadSingleLittleEndian(translated.AsSpan(8)),
                    BinaryPrimitives.ReadSingleLittleEndian(translated.AsSpan(12)), BinaryPrimitives.ReadSingleLittleEndian(translated.AsSpan(16))));
                Finite(midpoint);
                BinaryPrimitives.WriteSingleLittleEndian(translated.AsSpan(8), midpoint.X);
                BinaryPrimitives.WriteSingleLittleEndian(translated.AsSpan(12), midpoint.Y);
                _links.ApplyAuthoredMetadata(id, translated); _links.RestoreOriginalCost(id ^ 1);
            }
            _tiles.Add(new(tile.Coordinate, new ReadOnlyDictionary<uint, uint>(map), rows.AsReadOnly(), Array.AsReadOnly(vertices), tile.Network.AuthoredLinkCount));
        }

        private uint Connect(uint source, uint target, float scalar)
        {
            if (_directions.TryGetValue((source, target), out var current))
            { if (scalar != 0) _links.SetCost(current, scalar, false); return current; }
            var forward = _links.Allocate(); var reverse = forward ^ 1;
            _links.SetTarget(forward, target); _links.SetTarget(reverse, source);
            _links.SetCost(forward, scalar, true); _links.SetCost(reverse, 0, true);
            _directions.Add((source, target), forward); _directions.Add((target, source), reverse);
            _adjacency[source].Add(forward); _adjacency[target].Add(reverse);
            return forward;
        }

        private int WeldVertex(ZoneNavigationLegacyVertexRow row, ZoneNavigationTileContent tile)
        {
            var bytes = row.SerializedBytes;
            var position = tile.Translate(new(BinaryPrimitives.ReadSingleLittleEndian(bytes),
                BinaryPrimitives.ReadSingleLittleEndian(bytes[4..]), BinaryPrimitives.ReadSingleLittleEndian(bytes[8..])));
            Finite(position); var x = VertexCell(position.X); var y = VertexCell(position.Y); var found = int.MaxValue;
            for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
                if (_vertexCells.TryGetValue((x + dx, y + dy), out var candidates))
                    foreach (var candidate in candidates)
                    {
                        var previous = _vertices[candidate].Position; var rx = previous.X - position.X; var ry = previous.Y - position.Y;
                        // 311A1D14 compares XY only, retaining the first record even across floors.
                        if ((double)(rx * rx + ry * ry) < 1e-8 && candidate < found) found = candidate;
                    }
            if (found != int.MaxValue) return found;
            var index = _vertices.Count; _vertices.Add(new(bytes, position));
            if (!_vertexCells.TryGetValue((x, y), out var bucket)) _vertexCells.Add((x, y), bucket = []);
            bucket.Add(index); return index;
        }

        private static long VertexCell(float value)
        {
            var cell = Math.Floor((double)value / .0001);
            if (cell <= long.MinValue + 1d || cell >= long.MaxValue - 1d)
                throw new NotSupportedException("Navigation vertex coordinates exceed the finite weld index.");
            return (long)cell;
        }
        private static void Areas(IEnumerable<ZoneNavigationAreaRow> rows, HashSet<string> names, List<ZoneNavigationTiledArea> output)
        {
            foreach (var row in rows)
            {
                foreach (var point in row.Shape.Points) Finite(point);
                if (names.Add(Convert.ToHexString(row.Name.Bytes))) output.Add(new(row));
            }
        }
        private static void Finite(Vector3 value)
        {
            if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
                throw new InvalidDataException("Authored navigation coordinates must remain finite after translation.");
        }
    }
}
