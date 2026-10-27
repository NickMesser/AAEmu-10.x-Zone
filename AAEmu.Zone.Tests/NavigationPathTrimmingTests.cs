using System.Numerics;
using AAEmu.Zone.Simulation;

namespace AAEmu.Zone.Tests;

public sealed class NavigationPathTrimmingTests
{
    private static ZoneNavigationPath StraightPath()
    {
        var path = new ZoneNavigationPath();
        foreach (var x in new[] { 0, 10, 20 }) path.PushBack(new(new(x, 0, 0)));
        return path;
    }

    [Test]
    public async Task EmptyAndSinglePointPathsAreUnchanged()
    {
        var path = new ZoneNavigationPath { CurrentFraction = 0.5f };
        path.TrimEnd(0);
        await Assert.That(path.Count).IsEqualTo(0);
        await Assert.That(path.Version).IsEqualTo(-1);
        path.PushBack(new(new(3, 4, 5))); var version = path.Version;
        path.TrimEnd(float.PositiveInfinity);
        await Assert.That(path.Points.Single().Position).IsEqualTo(new Vector3(3, 4, 5));
        await Assert.That(path.Version).IsEqualTo(version);
        await Assert.That(path.CurrentFraction).IsEqualTo(0.5f);
    }

    [Test]
    public async Task PositiveDistanceTrimsTheEndAndNegativeDistanceRetainsAPrefix()
    {
        var path = StraightPath(); path.EndDirection = Vector3.UnitY;
        var original = path.Clone();
        path.TrimEnd(3);
        await Assert.That(path.Points.Select(p => p.Position.X).SequenceEqual([0f, 10f, 17f])).IsTrue();
        await Assert.That(path.GetMovementOutput()[0]).IsEqualTo(new ZoneNavigationMovementPoint(new(17, 0, 0), Vector3.UnitY));
        path.TrimEnd(-3);
        await Assert.That(path.Points.Select(p => p.Position.X).SequenceEqual([0f, 3f])).IsTrue();
        await Assert.That(path.GetLength()).IsEqualTo(3);
        await Assert.That(original.Points[^1].Position.X).IsEqualTo(20);
    }

    [Test]
    public async Task ExactSegmentBoundaryRetainsTwoCoincidentBoundaryPoints()
    {
        var path = StraightPath(); var version = path.Version;
        path.TrimEnd(-10);
        await Assert.That(path.Points.Select(p => p.Position.X).SequenceEqual([0f, 10f, 10f])).IsTrue();
        await Assert.That(path.Version).IsEqualTo(version + 1);
        await Assert.That(path.GetMovementOutput().Count).IsEqualTo(2);
    }

    [Test]
    public async Task ZeroAndTinyPrefixesDuplicateTheEntireFirstPointIncludingItsSmartObjectData()
    {
        foreach (var distance in new[] { 0f, -0f, -MathF.BitDecrement(0.01f), 25f })
        {
            var metadata = new ZoneSmartObjectNavigationData(new object(), new object());
            var first = new ZoneNavigationPoint(Vector3.Zero, 0x80, metadata, 4);
            var path = StraightPath(); path.ReplaceFront(first); path.TrimEnd(distance);
            await Assert.That(path.Count).IsEqualTo(2);
            await Assert.That(path.Points.All(p => p == first)).IsTrue();
            await Assert.That(ReferenceEquals(path.GetEndSmartObjectData(), metadata)).IsTrue();
        }
        var threshold = StraightPath(); threshold.TrimEnd(-0.01f);
        await Assert.That(threshold.Points[^1].Position.X > 0).IsTrue();
    }

    [Test]
    public async Task ExcessiveRetainedLengthIsUnchangedAndMutationIncrementsVersionOnlyOnce()
    {
        var path = StraightPath(); path.Version = int.MaxValue;
        path.TrimEnd(-25); path.TrimEnd(-20);
        await Assert.That(path.Version).IsEqualTo(int.MaxValue);
        await Assert.That(path.Points[^1].Position.X).IsEqualTo(20);
        path.TrimEnd(3);
        await Assert.That(path.Version).IsEqualTo(int.MinValue);
        await Assert.That(path.Points[^1].Position.X).IsEqualTo(17);
    }

    [Test]
    public async Task TrimmingMeasuresRemainingLengthButCutsFromTheStoredFirstPoint()
    {
        var path = StraightPath(); path.CurrentFraction = 0.5f;
        await Assert.That(path.GetLength()).IsEqualTo(15);
        path.TrimEnd(5);
        await Assert.That(path.Points.Select(p => p.Position.X).SequenceEqual([0f, 10f, 10f])).IsTrue();
        await Assert.That(path.CurrentFraction).IsEqualTo(0.5f);
        await Assert.That(path.GetLength()).IsEqualTo(5);
    }

    [Test]
    public async Task HorizontalTrimmingStillInterpolatesTheEndpointHeight()
    {
        var path = new ZoneNavigationPath();
        path.PushBack(new(Vector3.Zero)); path.PushBack(new(new(3, 0, 4))); path.PushBack(new(new(6, 0, 4)));
        path.TrimEnd(-1.5f, true);
        await Assert.That(path.Points[^1].Position).IsEqualTo(new Vector3(1.5f, 0, 2));
        await Assert.That(path.GetLength(true)).IsEqualTo(1.5f);
        await Assert.That(path.GetLength()).IsEqualTo(2.5f);
    }

    [Test]
    public async Task CuttingIntoASmartObjectEndpointReplacesItsNavigationTypeAndDropsItsMetadata()
    {
        var metadata = new ZoneSmartObjectNavigationData(new object(), new object());
        var path = new ZoneNavigationPath();
        path.PushBack(new(Vector3.Zero, 4)); path.PushBack(new(new(10, 0, 0), 0x80, metadata, 5)); path.PushBack(new(new(20, 0, 0)));
        var original = path.Clone(); path.TrimEnd(-5);
        await Assert.That(path.Points[^1]).IsEqualTo(new ZoneNavigationPoint(new(5, 0, 0), 4));
        await Assert.That(ReferenceEquals(original.Points[1].SmartObjectData, metadata)).IsTrue();
        await Assert.That(original.Points[1].SmartObjectMethod).IsEqualTo(5);
    }

    [Test]
    public async Task TrimmingInsideASmartObjectSegmentLeavesTheWholePathUnchanged()
    {
        var metadata = new ZoneSmartObjectNavigationData(new object(), new object());
        var path = new ZoneNavigationPath();
        path.PushBack(new(Vector3.Zero, 0x80, metadata, 4)); path.PushBack(new(new(10, 0, 0), 0x80, metadata, 4)); path.PushBack(new(new(20, 0, 0)));
        var points = path.Points.ToArray(); var version = path.Version;
        path.TrimEnd(-5);
        await Assert.That(path.Points.SequenceEqual(points)).IsTrue();
        await Assert.That(path.Version).IsEqualTo(version);
    }

    [Test]
    public async Task TrimmingPreservesRequestProgressAndDiagnosticState()
    {
        var path = StraightPath();
        var parameters = new ZoneNavigationPathCreationParameters { Start = new(4, 5, 6), EndDistance = 2, ClosedLoop = true };
        path.CreationParameters = parameters; path.StuckTime = 7; path.DiscardedPathLength = 9; path.PathEndIsAsRequested = false;
        path.EndDirection = Vector3.UnitZ;
        var adjustedObject = new object(); path.AddObjectAdjustedFor(adjustedObject);
        var line = new ZoneNavigationDebugLine(Vector3.Zero, Vector3.One, Vector4.One);
        var sphere = new ZoneNavigationDebugSphere(Vector3.One, 2, Vector4.One);
        path.AddDebugLine(line, true); path.AddDebugSphere(sphere, true);
        path.TrimEnd(25);
        await Assert.That(path.CreationParameters).IsEqualTo(parameters);
        await Assert.That(path.StuckTime).IsEqualTo(7);
        await Assert.That(path.DiscardedPathLength).IsEqualTo(9);
        await Assert.That(path.PathEndIsAsRequested).IsFalse();
        await Assert.That(path.EndDirection).IsEqualTo(Vector3.UnitZ);
        await Assert.That(ReferenceEquals(path.ObjectsAdjustedFor.Single(), adjustedObject)).IsTrue();
        await Assert.That(path.DebugLines.Single()).IsEqualTo(line);
        await Assert.That(path.DebugSpheres.Single()).IsEqualTo(sphere);
    }

    [Test]
    public async Task NonfiniteDistancesPreserveNativeComparisonBehavior()
    {
        var path = StraightPath(); var version = path.Version;
        path.TrimEnd(float.NaN); path.TrimEnd(float.NegativeInfinity);
        await Assert.That(path.Version).IsEqualTo(version);
        path.TrimEnd(float.PositiveInfinity);
        await Assert.That(path.Points.All(p => p.Position == Vector3.Zero)).IsTrue();

        path = StraightPath(); path.PushBack(new(new(float.NaN, 0, 0)));
        await Assert.That(float.IsNaN(path.GetLength())).IsTrue();
        path.TrimEnd(-3);
        await Assert.That(path.Points.Select(p => p.Position.X).SequenceEqual([0f, 3f])).IsTrue();
    }
}
