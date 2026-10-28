using System.Numerics;
using AAEmu.Zone.Content;

namespace AAEmu.Zone.Navigation;

public enum ZoneNavigationSegmentOutcome { Clear, Blocked, MissingData, Unsupported }
public readonly record struct ZoneNavigationSegmentAdmission(ZoneNavigationSegmentOutcome Outcome, string? Diagnostic = null);

/// <summary>
/// Read-only scene admission. Implementations bind the real agent/stance and the scene's existing
/// physics owner. Missing support or an unavailable query must never be returned as clear space.
/// </summary>
public interface IZoneNavigationSceneQueries
{
    ZoneNavigationSegmentAdmission CheckWalkSegment(Vector3 from, Vector3 to, float passRadius);
    ZoneNavigationSegmentAdmission CheckLineOfSight(Vector3 from, Vector3 to);
}

public sealed record ZoneNavigationPathRequest(Vector3 Start, Vector3 End, float PassRadius)
{
    public uint CapabilityMask { get; init; } = 6;
    /// <summary>Explicit endpoint connection limit; not a native nearest-node tolerance.</summary>
    public float MaximumEndpointDistance { get; init; } = 2;
    public int MaximumExpandedNodes { get; init; } = 65536;
}

public enum ZoneNavigationSearchOutcome
{ Found, NoRoute, MissingData, UnsupportedEndpoint, ForbiddenEndpoint, BlockedEndpoint, SearchBudgetExceeded, Retired }

/// <summary>Copied route. The scene must revalidate its publication before consuming movement.</summary>
public sealed class ZoneNavigationPathResult
{
    internal ZoneNavigationTiledGraphSnapshot Publication { get; }
    public ulong Revision => Publication.Revision;
    public ZoneNavigationSearchOutcome Outcome { get; }
    public IReadOnlyList<ZoneNavigationPoint> Points { get; }
    public IReadOnlyList<uint> NodeIds { get; }
    public int ExpandedNodes { get; }
    public int UnsupportedLinks { get; }
    public int BlockedSegments { get; }
    public int MissingSegments { get; }
    public string? Diagnostic { get; }
    public float PassRadius { get; }
    internal ZoneNavigationPathResult(ZoneNavigationTiledGraphSnapshot publication, ZoneNavigationSearchOutcome outcome,
        IEnumerable<ZoneNavigationPoint> points, IEnumerable<uint> nodes, int expanded, int unsupported,
        int blocked, int missing, string? diagnostic, float passRadius)
    {
        Publication = publication; Outcome = outcome; Points = Array.AsReadOnly(points.ToArray()); NodeIds = Array.AsReadOnly(nodes.ToArray());
        ExpandedNodes = expanded; UnsupportedLinks = unsupported; BlockedSegments = blocked; MissingSegments = missing; Diagnostic = diagnostic; PassRadius = passRadius;
    }
    public ZoneNavigationPath CopyPath()
    {
        if (Outcome != ZoneNavigationSearchOutcome.Found) throw new InvalidOperationException("Only a found route supplies path points.");
        if (!Publication.Current) throw new InvalidOperationException("A retired navigation publication cannot supply a live movement path.");
        var path = new ZoneNavigationPath();
        foreach (var point in Points) path.PushBack(point, force: true);
        path.CreationParameters = new() { Start = Points[0].Position, End = Points[^1].Position };
        return path;
    }
}

public sealed partial class ZoneNavigationTiledGraph
{
    private QueryIndex? _queryIndex;
    private bool _searching;

    public bool IsCurrent(ZoneNavigationPathResult result) => !IsDisposed &&
        ReferenceEquals(result.Publication, _snapshot) && result.Publication.Current;

    public ZoneNavigationSegmentAdmission CheckWalkSegment(Vector3 from, Vector3 to, float passRadius, IZoneNavigationSceneQueries queries)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this); ArgumentNullException.ThrowIfNull(queries);
        ValidatePoint(from); ValidatePoint(to);
        if (!float.IsFinite(passRadius) || passRadius < 0) throw new ArgumentOutOfRangeException(nameof(passRadius));
        var graph = _snapshot;
        if (_queryIndex is not { } index || !ReferenceEquals(index.Graph, graph)) _queryIndex = new(graph, ZoneOrigin);
        if (!_queryIndex.Covered(from, to)) return new(ZoneNavigationSegmentOutcome.MissingData, "The segment crosses an unloaded navigation tile.");
        if (_queryIndex.Forbidden(from, to, passRadius)) return new(ZoneNavigationSegmentOutcome.Blocked, "The segment intersects an authored forbidden shape or boundary.");
        var result = queries.CheckWalkSegment(from, to, passRadius);
        return !IsDisposed && ReferenceEquals(graph, _snapshot) && graph.Current ? result :
            new(ZoneNavigationSegmentOutcome.Unsupported, "The navigation scene retired during segment admission.");
    }

    public ZoneNavigationSegmentAdmission CheckLineOfSight(Vector3 from, Vector3 to, IZoneNavigationSceneQueries queries)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this); ArgumentNullException.ThrowIfNull(queries);
        ValidatePoint(from); ValidatePoint(to); var publication = _snapshot;
        var result = queries.CheckLineOfSight(from, to);
        return !IsDisposed && ReferenceEquals(publication, _snapshot) && publication.Current ? result :
            new(ZoneNavigationSegmentOutcome.Unsupported, "The navigation scene retired during the visibility query.");
    }

    /// <summary>
    /// A* over the admitted authored directed topology, with Euclidean center-to-center cost.
    /// It does not synthesize connections across disconnected meshes. Every selected segment
    /// requires real scene admission, loaded tile coverage and authored forbidden clearance.
    /// Native exposure/water costs, smart objects, flight, volumes and roads are not reproduced.
    /// </summary>
    public ZoneNavigationPathResult FindPath(ZoneNavigationPathRequest request, IZoneNavigationSceneQueries queries)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this); ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(queries);
        ValidatePoint(request.Start); ValidatePoint(request.End);
        if (!float.IsFinite(request.PassRadius) || request.PassRadius < 0 ||
            !float.IsFinite(request.MaximumEndpointDistance) || request.MaximumEndpointDistance <= 0 || request.MaximumExpandedNodes <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Navigation requires finite nonnegative radius and positive search/connection bounds.");
        if (_searching) throw new InvalidOperationException("A navigation scene cannot recursively search its graph.");
        _searching = true;
        try { return new Search(this, request, queries).Run(); }
        finally { _searching = false; }
    }

    private static void ValidatePoint(Vector3 value)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            throw new ArgumentOutOfRangeException(nameof(value), "Navigation positions must be finite.");
    }

    private sealed class Search
    {
        private readonly ZoneNavigationTiledGraph _owner;
        private readonly ZoneNavigationTiledGraphSnapshot _graph;
        private readonly QueryIndex _index;
        private readonly ZoneNavigationPathRequest _request;
        private readonly IZoneNavigationSceneQueries _queries;
        private int _expanded, _unsupported, _blocked, _missing;
        private string? _diagnostic;
        private bool Current => !_owner.IsDisposed && ReferenceEquals(_owner._snapshot, _graph) && _graph.Current;
        internal Search(ZoneNavigationTiledGraph owner, ZoneNavigationPathRequest request, IZoneNavigationSceneQueries queries)
        {
            _owner = owner; _graph = owner._snapshot; _request = request; _queries = queries;
            if (owner._queryIndex is not { } index || !ReferenceEquals(index.Graph, _graph)) owner._queryIndex = new(_graph, owner.ZoneOrigin);
            _index = owner._queryIndex;
        }
        internal ZoneNavigationPathResult Run()
        {
            if (!_index.Covered(_request.Start) || !_index.Covered(_request.End)) return Result(ZoneNavigationSearchOutcome.MissingData, "An endpoint has no admitted navigation tile.");
            if (_index.Forbidden(_request.Start, _request.Start, _request.PassRadius) || _index.Forbidden(_request.End, _request.End, _request.PassRadius))
                return Result(ZoneNavigationSearchOutcome.ForbiddenEndpoint, "An endpoint intersects an authored forbidden shape or boundary.");
            ZoneNavigationTypeSelection startType, endType;
            try { startType = _graph.ModifierQuery.Classify(_request.Start, _request.CapabilityMask); endType = _graph.ModifierQuery.Classify(_request.End, _request.CapabilityMask); }
            catch (NotSupportedException error) { return Result(ZoneNavigationSearchOutcome.UnsupportedEndpoint, error.Message); }
            if (startType.NavigationType is not (2 or 4) || endType.NavigationType is not (2 or 4))
                return Result(ZoneNavigationSearchOutcome.UnsupportedEndpoint, "This search supports ground triangles and owned ordinary waypoint regions only.");
            var starts = Endpoints(_request.Start, startType, true); if (!Current) return Result(ZoneNavigationSearchOutcome.Retired);
            var ends = Endpoints(_request.End, endType, false); if (!Current) return Result(ZoneNavigationSearchOutcome.Retired);
            if (starts.Count == 0 || ends.Count == 0)
                return Result(_missing > 0 ? ZoneNavigationSearchOutcome.MissingData : _blocked > 0 ? ZoneNavigationSearchOutcome.BlockedEndpoint : ZoneNavigationSearchOutcome.UnsupportedEndpoint,
                    "No reachable endpoint triangle/waypoint lies within the explicit connection bound.");
            var goals = ends.ToDictionary(node => node.Id);
            var open = new PriorityQueue<uint, (double Cost, uint Id)>();
            var cost = new Dictionary<uint, double>(); var parent = new Dictionary<uint, uint>(); var closed = new HashSet<uint>();
            foreach (var node in starts)
            { var distance = Distance(_request.Start, node.Position); cost[node.Id] = distance; open.Enqueue(node.Id, (distance + Distance(node.Position, _request.End), node.Id)); }
            while (open.TryDequeue(out var id, out _))
            {
                if (!closed.Add(id)) continue;
                if (_expanded >= _request.MaximumExpandedNodes) return Result(ZoneNavigationSearchOutcome.SearchBudgetExceeded, "The explicit node expansion budget was reached.");
                _expanded++; var node = _graph.Nodes[id];
                if (goals.ContainsKey(id))
                {
                    var ids = new List<uint> { id }; while (parent.TryGetValue(ids[^1], out var previous)) ids.Add(previous); ids.Reverse();
                    var points = new List<ZoneNavigationPoint> { new(_request.Start, (uint)startType.NavigationType) };
                    points.AddRange(ids.Select(index => new ZoneNavigationPoint(_graph.Nodes[index].Position, _graph.Nodes[index].NavigationType)));
                    points.Add(new(_request.End, (uint)endType.NavigationType));
                    return new(_graph, ZoneNavigationSearchOutcome.Found, points, ids, _expanded, _unsupported, _blocked, _missing, _diagnostic, _request.PassRadius);
                }
                foreach (var link in node.Links)
                {
                    var target = _graph.Nodes[link.TargetId];
                    if (!Supported(target)) { _unsupported++; continue; }
                    // 3104FB18 admits a directed half only when its signed-centimeter value
                    // is >= the actor's pass radius. This field is clearance, not travel cost.
                    if (link.TraversalLimit < _request.PassRadius || closed.Contains(target.Id)) continue;
                    var candidate = cost[id] + Distance(node.Position, target.Position);
                    if (cost.TryGetValue(target.Id, out var best) && candidate >= best) continue;
                    if (!Segment(node.Position, target.Position)) { if (!Current) return Result(ZoneNavigationSearchOutcome.Retired); continue; }
                    cost[target.Id] = candidate; parent[target.Id] = id;
                    open.Enqueue(target.Id, (candidate + Distance(target.Position, _request.End), target.Id));
                }
            }
            return Result(_missing > 0 ? ZoneNavigationSearchOutcome.MissingData : ZoneNavigationSearchOutcome.NoRoute,
                _diagnostic ?? "No admitted directed route connects the endpoint regions.");
        }
        private List<ZoneNavigationTiledNode> Endpoints(Vector3 position, ZoneNavigationTypeSelection selection, bool start)
        {
            var nodes = new List<(double Distance, ZoneNavigationTiledNode Node)>();
            foreach (var id in _index.EndpointCandidates(position, _request.MaximumEndpointDistance))
            {
                var node = _graph.Nodes[id];
                if (node.NavigationType != selection.NavigationType || !Supported(node)) continue;
                double distance;
                if (node.NavigationType == 2)
                {
                    if (!_index.TriangleHeight(node, position, out var height)) continue;
                    distance = Math.Abs(position.Z - height);
                }
                else { if (node.RegionId != selection.RegionId) continue; distance = Distance(position, node.Position); }
                if (distance > _request.MaximumEndpointDistance) continue;
                if (Segment(start ? position : node.Position, start ? node.Position : position)) nodes.Add((distance, node));
                if (!Current) break;
            }
            return nodes.OrderBy(item => item.Distance).ThenBy(item => item.Node.Id).Select(item => item.Node).ToList();
        }
        private bool Supported(ZoneNavigationTiledNode node) => (node.NavigationType & _request.CapabilityMask) != 0 &&
            (node.NavigationType == 2 && node.VertexIndices.Count == 3 && node.VertexIndices.All(i => i >= 0 && i < _graph.Vertices.Count) ||
             node.NavigationType == 4 && node.RegionId >= 0);
        private bool Segment(Vector3 from, Vector3 to)
        {
            if (!_index.Covered(from, to)) { _missing++; return false; }
            if (_index.Forbidden(from, to, _request.PassRadius)) { _blocked++; return false; }
            var admission = _queries.CheckWalkSegment(from, to, _request.PassRadius);
            if (!Current) return false;
            switch (admission.Outcome)
            {
                case ZoneNavigationSegmentOutcome.Clear: return true;
                case ZoneNavigationSegmentOutcome.MissingData: _missing++; break;
                case ZoneNavigationSegmentOutcome.Blocked: _blocked++; break;
                default: _unsupported++; break;
            }
            _diagnostic = admission.Diagnostic; return false;
        }
        private ZoneNavigationPathResult Result(ZoneNavigationSearchOutcome outcome, string? diagnostic = null)
            => new(_graph, outcome, [], [], _expanded, _unsupported, _blocked, _missing, diagnostic ?? _diagnostic, _request.PassRadius);
    }

    private static double Distance(Vector3 a, Vector3 b)
    { var x = (double)a.X - b.X; var y = (double)a.Y - b.Y; var z = (double)a.Z - b.Z; return Math.Sqrt(x * x + y * y + z * z); }

    private sealed class QueryIndex
    {
        internal readonly ZoneNavigationTiledGraphSnapshot Graph;
        private readonly ZoneMapCoordinate _origin;
        private readonly HashSet<ZoneMapCoordinate> _tiles;
        private readonly Dictionary<(int, int), List<uint>> _nodeCells = [];
        private readonly List<uint> _largeNodes = [];
        private readonly Dictionary<(int, int), List<ZoneNavigationTiledArea>> _areaCells = [];
        private readonly List<ZoneNavigationTiledArea> _largeAreas = [];
        internal QueryIndex(ZoneNavigationTiledGraphSnapshot graph, ZoneMapCoordinate origin)
        {
            Graph = graph; _origin = origin; _tiles = graph.Tiles.Select(tile => tile.Coordinate).ToHashSet();
            foreach (var node in graph.Nodes.Values)
            {
                var min = node.Position; var max = min;
                if (node.NavigationType == 2 && node.VertexIndices.Count == 3 && node.VertexIndices.All(i => i >= 0 && i < graph.Vertices.Count))
                    foreach (var vertex in node.VertexIndices.Select(i => graph.Vertices[i].Position)) { min = Vector3.Min(min, vertex); max = Vector3.Max(max, vertex); }
                Add(min, max, node.Id, _nodeCells, _largeNodes);
            }
            foreach (var area in graph.ForbiddenAreas.Concat(graph.DesignerForbiddenAreas).Concat(graph.ForbiddenBoundaries))
                Add(area.BoundsMinimum, area.BoundsMaximum, area, _areaCells, _largeAreas);
        }
        internal IEnumerable<uint> EndpointCandidates(Vector3 point, float radius) =>
            Query(point - new Vector3(radius, radius, 0), point + new Vector3(radius, radius, 0), _nodeCells, _largeNodes).Distinct();
        internal bool TriangleHeight(ZoneNavigationTiledNode node, Vector3 point, out double height)
        {
            height = 0; if (node.VertexIndices.Count != 3) return false;
            var a = Graph.Vertices[node.VertexIndices[0]].Position; var b = Graph.Vertices[node.VertexIndices[1]].Position; var c = Graph.Vertices[node.VertexIndices[2]].Position;
            var determinant = ((double)b.Y - c.Y) * ((double)a.X - c.X) + ((double)c.X - b.X) * ((double)a.Y - c.Y);
            if (determinant == 0) return false;
            var u = (((double)b.Y - c.Y) * ((double)point.X - c.X) + ((double)c.X - b.X) * ((double)point.Y - c.Y)) / determinant;
            var v = (((double)c.Y - a.Y) * ((double)point.X - c.X) + ((double)a.X - c.X) * ((double)point.Y - c.Y)) / determinant;
            if (u < 0 || v < 0 || u + v > 1) return false;
            height = u * a.Z + v * b.Z + (1 - u - v) * c.Z; return true;
        }
        internal bool Covered(Vector3 point)
        {
            var x = Math.Floor((double)point.X / 256) + (double)_origin.X * 4;
            var y = Math.Floor((double)point.Y / 256) + (double)_origin.Y * 4;
            return x >= int.MinValue && x <= int.MaxValue && y >= int.MinValue && y <= int.MaxValue && _tiles.Contains(new((int)x, (int)y));
        }
        internal bool Covered(Vector3 from, Vector3 to)
        {
            if (!Covered(from) || !Covered(to)) return false;
            var cuts = new List<double> { 0, 1 };
            if (!Cuts(from.X, to.X) || !Cuts(from.Y, to.Y)) return false;
            cuts.Sort();
            for (var i = 1; i < cuts.Count; i++)
            {
                var t = (cuts[i - 1] + cuts[i]) * .5;
                if (!Covered(new((float)(from.X + ((double)to.X - from.X) * t), (float)(from.Y + ((double)to.Y - from.Y) * t), 0))) return false;
            }
            return true;
            bool Cuts(float first, float last)
            {
                if (first == last) return true;
                var low = Math.Floor(Math.Min((double)first, last) / 256) + 1;
                var high = Math.Floor(Math.Max((double)first, last) / 256);
                if (high - low > 4096) return false;
                for (var cell = low; cell <= high; cell++) { var t = (cell * 256 - first) / ((double)last - first); if (t > 0 && t < 1) cuts.Add(t); }
                return true;
            }
        }
        internal bool Forbidden(Vector3 from, Vector3 to, float radius)
        {
            var min = Vector3.Min(from, to) - new Vector3(radius, radius, 0); var max = Vector3.Max(from, to) + new Vector3(radius, radius, 0);
            foreach (var area in Query(min, max, _areaCells, _largeAreas).Distinct())
            {
                if (area.Contains(from) || area.Contains(to)) return true;
                for (var i = 0; i < area.Points.Count; i++)
                    if (SegmentDistanceSquared(from, to, area.Points[i], area.Points[(i + 1) % area.Points.Count]) <= (double)radius * radius) return true;
            }
            return false;
        }
        private static void Add<T>(Vector3 min, Vector3 max, T value, Dictionary<(int, int), List<T>> cells, List<T> large)
        {
            var x0 = Cell(min.X); var x1 = Cell(max.X); var y0 = Cell(min.Y); var y1 = Cell(max.Y);
            var width = (long)x1 - x0 + 1; var height = (long)y1 - y0 + 1;
            if (width > 4096 || height > 4096 || width * height > 4096) { large.Add(value); return; }
            for (var x = x0; x <= x1; x++) for (var y = y0; y <= y1; y++)
            { if (!cells.TryGetValue((x, y), out var bucket)) cells.Add((x, y), bucket = []); bucket.Add(value); }
        }
        private static IEnumerable<T> Query<T>(Vector3 min, Vector3 max, Dictionary<(int, int), List<T>> cells, List<T> large)
        {
            foreach (var value in large) yield return value;
            var x0 = Cell(min.X); var x1 = Cell(max.X); var y0 = Cell(min.Y); var y1 = Cell(max.Y);
            var width = (long)x1 - x0 + 1; var height = (long)y1 - y0 + 1;
            if (width > 65536 || height > 65536 || width * height > 65536) throw new NotSupportedException("Navigation query exceeds the explicit spatial query bound.");
            for (var x = x0; x <= x1; x++) for (var y = y0; y <= y1; y++) if (cells.TryGetValue((x, y), out var bucket)) foreach (var value in bucket) yield return value;
        }
        private static int Cell(float value)
        { var cell = Math.Floor((double)value / 64); return cell > int.MinValue && cell < int.MaxValue ? (int)cell : throw new NotSupportedException("Navigation coordinates exceed the spatial index."); }
        private static double SegmentDistanceSquared(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            var dx = (double)b.X - a.X; var dy = (double)b.Y - a.Y; var ex = (double)d.X - c.X; var ey = (double)d.Y - c.Y;
            var determinant = dx * ey - dy * ex;
            if (determinant != 0)
            { var x = (double)c.X - a.X; var y = (double)c.Y - a.Y; var s = (x * ey - y * ex) / determinant; var t = (x * dy - y * dx) / determinant; if (s >= 0 && s <= 1 && t >= 0 && t <= 1) return 0; }
            return Math.Min(Math.Min(Point(a, c, d), Point(b, c, d)), Math.Min(Point(c, a, b), Point(d, a, b)));
            static double Point(Vector3 p, Vector3 first, Vector3 last)
            { var x = (double)last.X - first.X; var y = (double)last.Y - first.Y; var square = x * x + y * y; var t = square > 0 ? Math.Clamp((((double)p.X - first.X) * x + ((double)p.Y - first.Y) * y) / square, 0, 1) : 0; var rx = p.X - (first.X + t * x); var ry = p.Y - (first.Y + t * y); return rx * rx + ry * ry; }
        }
    }
}
