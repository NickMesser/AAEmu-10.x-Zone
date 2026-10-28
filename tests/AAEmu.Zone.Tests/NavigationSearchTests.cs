using System.Numerics;
using AAEmu.Zone.Content;
using AAEmu.Zone.Navigation;

namespace AAEmu.Zone.Tests;

public sealed class NavigationSearchTests
{
    [Test]
    public async Task AuthoredDirectedRouteGoesAroundARealSceneObstacleAndPreservesItsCorners()
    {
        using var graph = RoutingGraph(); var queries = new WallQueries();
        var path = graph.FindPath(new(new(2, 10, 0), new(20, 10, 0), .25f), queries);
        await Assert.That(path.Outcome).IsEqualTo(ZoneNavigationSearchOutcome.Found);
        var ids = graph.Snapshot.Tiles[0].AuthoredNodeIds;
        await Assert.That(path.NodeIds.SequenceEqual(new[] { ids[1], ids[2], ids[3], ids[4], ids[5] })).IsTrue();
        await Assert.That(path.BlockedSegments > 0).IsTrue();
        await Assert.That(path.CopyPath().Count).IsEqualTo(7);
        await Assert.That(graph.IsCurrent(path)).IsTrue();
        await Assert.That(graph.CheckLineOfSight(new(2, 10, 1), new(20, 10, 1), queries).Outcome).IsEqualTo(ZoneNavigationSegmentOutcome.Blocked);
        await Assert.That(queries.WalkCalls > 0).IsTrue();
    }

    [Test]
    public async Task AuthoredForbiddenPolygonsBlockSegmentsEvenWhenBothEndpointsAreOutside()
    {
        using var graph = RoutingGraph(forbidden: true); var queries = new ClearQueries();
        var path = graph.FindPath(new(new(2, 10, 0), new(20, 10, 0), .25f), queries);
        await Assert.That(path.Outcome).IsEqualTo(ZoneNavigationSearchOutcome.Found);
        await Assert.That(path.NodeIds.Count).IsEqualTo(5);
        await Assert.That(graph.CheckWalkSegment(new(2, 10, 0), new(20, 10, 0), .25f, queries).Outcome).IsEqualTo(ZoneNavigationSegmentOutcome.Blocked);
        await Assert.That(graph.CheckWalkSegment(new(2, 7.8f, 0), new(20, 7.8f, 0), .25f, queries).Outcome).IsEqualTo(ZoneNavigationSegmentOutcome.Blocked);
        await Assert.That(graph.CheckWalkSegment(new(2, 7.8f, 0), new(20, 7.8f, 0), .1f, queries).Outcome).IsEqualTo(ZoneNavigationSegmentOutcome.Clear);
        var inside = graph.FindPath(new(new(9, 9, 0), new(20, 10, 0), .25f), queries);
        await Assert.That(inside.Outcome).IsEqualTo(ZoneNavigationSearchOutcome.ForbiddenEndpoint);
    }

    [Test]
    public async Task RadiusAdmissionIsInclusiveAndDoesNotInventReverseLinksOrUnsupportedTraversal()
    {
        using var graph = RoutingGraph(); var queries = new WallQueries();
        await Assert.That(graph.FindPath(new(new(2, 10, 0), new(20, 10, 0), .5f), queries).Outcome).IsEqualTo(ZoneNavigationSearchOutcome.Found);
        await Assert.That(graph.FindPath(new(new(2, 10, 0), new(20, 10, 0), MathF.BitIncrement(.5f)), queries).Outcome).IsEqualTo(ZoneNavigationSearchOutcome.NoRoute);
        await Assert.That(graph.FindPath(new(new(20, 10, 0), new(2, 10, 0), .25f), queries).Outcome).IsEqualTo(ZoneNavigationSearchOutcome.NoRoute);
        using var unsupported = RoutingGraph(unsupportedOnly: true);
        var refused = unsupported.FindPath(new(new(2, 10, 0), new(20, 10, 0), .25f), new ClearQueries());
        await Assert.That(refused.Outcome).IsEqualTo(ZoneNavigationSearchOutcome.NoRoute);
        await Assert.That(refused.UnsupportedLinks > 0).IsTrue();
        await Assert.That(unsupported.Snapshot.Nodes.Values.Any(node => node.NavigationType == 16)).IsTrue();
    }

    [Test]
    public async Task DisconnectedTrianglesBudgetAndMissingPhysicsHaveExplicitOutcomes()
    {
        using var disconnected = RoutingGraph(disconnected: true);
        await Assert.That(disconnected.FindPath(new(new(2, 10, 0), new(20, 10, 0), .25f), new ClearQueries()).Outcome).IsEqualTo(ZoneNavigationSearchOutcome.NoRoute);
        using var graph = RoutingGraph();
        await Assert.That(graph.FindPath(new(new(2, 10, 0), new(20, 10, 0), .25f) { MaximumExpandedNodes = 1 }, new WallQueries()).Outcome)
            .IsEqualTo(ZoneNavigationSearchOutcome.SearchBudgetExceeded);
        var missing = graph.FindPath(new(new(2, 10, 0), new(20, 10, 0), .25f), new CallbackQueries(() => new(ZoneNavigationSegmentOutcome.MissingData, "terrain cell unloaded")));
        await Assert.That(missing.Outcome).IsEqualTo(ZoneNavigationSearchOutcome.MissingData);
        await Assert.That(missing.Points.Count).IsEqualTo(0);
    }

    [Test]
    public async Task TileRetirementInvalidatesCompletedAndInFlightSearches()
    {
        using var graph = RoutingGraph(); var path = graph.FindPath(new(new(2, 10, 0), new(20, 10, 0), .25f), new WallQueries());
        graph.RetireTile(new(36, 56));
        await Assert.That(graph.IsCurrent(path)).IsFalse();
        await Assert.That(() => path.CopyPath()).Throws<InvalidOperationException>();
        using var other = RoutingGraph();
        var retired = other.FindPath(new(new(2, 10, 0), new(20, 10, 0), .25f), new CallbackQueries(() => { other.Dispose(); return new(ZoneNavigationSegmentOutcome.Clear); }));
        await Assert.That(retired.Outcome).IsEqualTo(ZoneNavigationSearchOutcome.Retired);
        await Assert.That(other.IsCurrent(retired)).IsFalse();
    }

    [Test]
    public async Task ALongAuthoredLinkCannotCrossAnUnloadedTileBetweenLoadedEndpoints()
    {
        var files = new NavigationTileFixture(); var first = new ZoneMapCoordinate(36, 56); var last = new ZoneMapCoordinate(38, 56);
        files.Add(first, [new(1, new(2, 10, 0)), new(5, new(532, 10, 0), A: 3, B: 4, C: 5)], [new(1, 5, 2)],
            [new(0, 8, 0), new(4, 8, 0), new(2, 12, 0), new(530, 8, 0), new(534, 8, 0), new(532, 12, 0)]);
        files.Add(last, [new(5, new(20, 10, 0))], [], [new(18, 8, 0), new(22, 8, 0), new(20, 12, 0)]);
        using var graph = new ZoneNavigationTiledGraph(files.Origin, [files.Load(first), files.Load(last)]);
        var result = graph.FindPath(new(new(2, 10, 0), new(532, 10, 0), .25f), new ClearQueries());
        await Assert.That(result.Outcome).IsEqualTo(ZoneNavigationSearchOutcome.MissingData);
        await Assert.That(result.MissingSegments > 0).IsTrue();
        await Assert.That(result.Points.Count).IsEqualTo(0);
    }

    [Test]
    public async Task EndpointAndForbiddenAdmissionWorkAtEightKilometerLocalCoordinates()
    {
        var tile = new ZoneMapCoordinate(67, 87); var offset = new Vector3(7936, 7936, 0);
        using var graph = RoutingGraph(forbidden: true, tile: tile);
        var result = graph.FindPath(new(new Vector3(2, 10, 0) + offset, new Vector3(20, 10, 0) + offset, .25f), new ClearQueries());
        await Assert.That(result.Outcome).IsEqualTo(ZoneNavigationSearchOutcome.Found);
        await Assert.That(result.NodeIds.Count).IsEqualTo(5);
    }

    internal static ZoneNavigationTiledGraph RoutingGraph(bool forbidden = false, bool disconnected = false, bool unsupportedOnly = false, ZoneMapCoordinate? tile = null)
    {
        var files = new NavigationTileFixture(); var coordinate = tile ?? new(36, 56);
        Vector3[] positions = [new(2, 10, 0), new(2, 4, 0), new(10, 4, 0), new(20, 4, 0), new(20, 10, 0), new(10, 10, 0)];
        var vertices = positions.SelectMany(p => new[] { p + new Vector3(-2, -2, 0), p + new Vector3(2, -2, 0), p + new Vector3(0, 2, 0) }).ToArray();
        var nodes = positions.Select((p, i) => new NavigationTileFixture.Node((uint)i + 1, p, i == 5 ? (ushort)16 : (ushort)2, i * 3, i * 3 + 1, i * 3 + 2)).ToArray();
        NavigationTileFixture.Link[] links = disconnected ? [] : unsupportedOnly ? [new(1, 6, 2), new(6, 5, 2)] : [new(1, 5, 2), new(1, 2, .5f), new(2, 3, 2), new(3, 4, 2), new(4, 5, 2)];
        files.Add(coordinate, nodes, links, vertices);
        if (forbidden) files.Files[files.Path(coordinate) + "/areasmission0.bai"] = NavigationTileFixture.Areas([("obstacle", new(8, 8, 0))], []);
        return new(files.Origin, [files.Load(coordinate)]);
    }

    internal class ClearQueries : IZoneNavigationSceneQueries
    {
        public virtual ZoneNavigationSegmentAdmission CheckWalkSegment(Vector3 from, Vector3 to, float passRadius) => new(ZoneNavigationSegmentOutcome.Clear);
        public virtual ZoneNavigationSegmentAdmission CheckLineOfSight(Vector3 from, Vector3 to) => new(ZoneNavigationSegmentOutcome.Clear);
    }
    internal sealed class WallQueries : ClearQueries
    {
        internal int WalkCalls;
        public override ZoneNavigationSegmentAdmission CheckWalkSegment(Vector3 from, Vector3 to, float passRadius)
        { WalkCalls++; return CheckLineOfSight(from, to); }
        public override ZoneNavigationSegmentAdmission CheckLineOfSight(Vector3 from, Vector3 to)
        {
            if (from.X == to.X) return new(ZoneNavigationSegmentOutcome.Clear);
            var t = (10 - from.X) / (to.X - from.X); var y = from.Y + (to.Y - from.Y) * t;
            return new(t >= 0 && t <= 1 && y >= 6 && y <= 14 ? ZoneNavigationSegmentOutcome.Blocked : ZoneNavigationSegmentOutcome.Clear);
        }
    }
    private sealed class CallbackQueries(Func<ZoneNavigationSegmentAdmission> callback) : ClearQueries
    { public override ZoneNavigationSegmentAdmission CheckWalkSegment(Vector3 from, Vector3 to, float passRadius) => callback(); }
}
