using System.Numerics;

using AAEmu.Zone.Navigation;

namespace AAEmu.Zone.Tests;

public sealed class NavigationPathTests
{
    private static ZoneNavigationPoint Point(float x, float y = 0, float z = 0, uint navigation = 1) => new(new(x, y, z), navigation);

    [Test]
    public async Task EmptyAndSinglePointPathsPreserveOutputAndDoNotFormASegment()
    {
        var path = new ZoneNavigationPath();
        await Assert.That(path.Version).IsEqualTo(-1);
        await Assert.That(path.PathEndIsAsRequested).IsTrue();
        var position = new Vector3(7, 8, 9);
        await Assert.That(path.TryGetPosition(3, false, false, ref position)).IsFalse();
        await Assert.That(position).IsEqualTo(new Vector3(7, 8, 9));
        path.ReplaceFront(Point(4));
        await Assert.That(path.Version).IsEqualTo(-1);
        path.PushBack(Point(2, 3, 4)); path.CurrentFraction = float.NaN;
        await Assert.That(path.HasPath).IsFalse();
        await Assert.That(path.GetLength()).IsEqualTo(0);
        await Assert.That(path.GetMovementOutput().Count).IsEqualTo(0);
        await Assert.That(path.TryGetPosition(float.NaN, true, true, ref position)).IsTrue();
        await Assert.That(position).IsEqualTo(new Vector3(2, 3, 4));
    }

    [Test]
    public async Task DuplicateToleranceUsesEachAxisAndStillAdvancesVersion()
    {
        var path = new ZoneNavigationPath(); path.PushBack(Point(0));
        path.PushBack(Point(0.05f, 0.05f, 0.05f));
        await Assert.That(path.Count).IsEqualTo(1);
        await Assert.That(path.Version).IsEqualTo(1);
        path.PushBack(Point(MathF.BitIncrement(0.05f)));
        await Assert.That(path.Count).IsEqualTo(2);
        path.PushFront(Point(0), true);
        await Assert.That(path.Count).IsEqualTo(3);
        path.Version = int.MaxValue; path.PushFront(Point(0));
        await Assert.That(path.Count).IsEqualTo(3);
        await Assert.That(path.Version).IsEqualTo(int.MinValue);
    }

    [Test]
    public async Task DuplicateNavigationFlagsFlattenHeightAndSmartObjectsAlwaysInsert()
    {
        foreach (var (firstType, nextType) in new[] { (2u, 1u), (1u, 4u), (6u, 1u) })
        {
            var path = new ZoneNavigationPath(); path.PushBack(Point(0, navigation: firstType));
            path.PushBack(Point(0, z: 100, navigation: nextType));
            await Assert.That(path.Count).IsEqualTo(1);
        }
        foreach (var (firstType, nextType) in new[] { (0x80u, 1u), (1u, 0x80u) })
        {
            var path = new ZoneNavigationPath(); path.PushBack(Point(0, navigation: firstType));
            path.PushFront(Point(0, navigation: nextType));
            await Assert.That(path.Count).IsEqualTo(2);
        }
    }

    [Test]
    public async Task UnorderedCoordinatesAreInsertedExceptHeightWhenNavigationFlattensIt()
    {
        foreach (var position in new[] { new Vector3(float.NaN, 0, 0), new Vector3(0, float.NaN, 0), new Vector3(0, 0, float.NaN) })
        {
            var path = new ZoneNavigationPath(); path.PushBack(Point(0)); path.PushBack(new(position, 1));
            await Assert.That(path.Count).IsEqualTo(2);
        }
        var flat = new ZoneNavigationPath(); flat.PushBack(Point(0, navigation: 2)); flat.PushBack(Point(0, z: float.NaN));
        await Assert.That(flat.Count).IsEqualTo(1);
    }

    [Test]
    public async Task AdvanceReturnsTheNewSegmentDestinationAndResetsFractionOnlyAtTheLastPoint()
    {
        var path = new ZoneNavigationPath();
        foreach (var x in new[] { 0, 2, 4, 6 }) path.PushBack(Point(x));
        path.CurrentFraction = 0.25f;
        var next = Point(-1);
        await Assert.That(path.TryAdvance(ref next)).IsTrue();
        await Assert.That(next).IsEqualTo(Point(4));
        await Assert.That(path.Points[0]).IsEqualTo(Point(2));
        await Assert.That(path.CurrentFraction).IsEqualTo(0.25f);
        await Assert.That(path.TryAdvance(ref next)).IsTrue();
        await Assert.That(next).IsEqualTo(Point(6));
        next = Point(-2);
        await Assert.That(path.TryAdvance(ref next)).IsFalse();
        await Assert.That(next).IsEqualTo(Point(-2));
        await Assert.That(path.CurrentFraction).IsEqualTo(0);
        await Assert.That(path.Count).IsEqualTo(1);
        var version = path.Version;
        await Assert.That(path.TryAdvance(ref next)).IsFalse();
        await Assert.That(path.Version).IsEqualTo(version);
    }

    [Test]
    public async Task SamplingAndLengthStartAtTheCurrentFractionAndCrossSegmentBoundaries()
    {
        var path = new ZoneNavigationPath();
        path.PushBack(Point(0)); path.PushBack(Point(4)); path.PushBack(Point(4, 3));
        path.CurrentFraction = 0.25f;
        var position = default(Vector3);
        path.TryGetPosition(0, false, false, ref position);
        await Assert.That(position).IsEqualTo(new Vector3(1, 0, 0));
        await Assert.That(path.GetLength()).IsEqualTo(6);
        path.TryGetPosition(4, false, false, ref position);
        await Assert.That(position).IsEqualTo(new Vector3(4, 1, 0));
        path.TryGetPosition(-1, false, false, ref position);
        await Assert.That(position).IsEqualTo(Vector3.Zero);
    }

    [Test]
    public async Task FlatteningUsesHorizontalDistanceWhileInterpolatingHeight()
    {
        var path = new ZoneNavigationPath(); path.PushBack(Point(0)); path.PushBack(Point(3, 0, 4));
        path.CurrentFraction = 0.5f;
        await Assert.That(path.GetLength()).IsEqualTo(2.5f);
        await Assert.That(path.GetLength(true)).IsEqualTo(1.5f);
        var position = default(Vector3); path.TryGetPosition(0.75f, true, false, ref position);
        await Assert.That(position).IsEqualTo(new Vector3(2.25f, 0, 3));
        path.TryGetPosition(0.75f, false, false, ref position);
        await Assert.That(MathF.Abs(position.X - 1.95f) < 0.000001f).IsTrue();
        await Assert.That(MathF.Abs(position.Z - 2.6f) < 0.000001f).IsTrue();
    }

    [Test]
    public async Task SamplingBeyondTheEndPreservesNativeZeroTangentExtrapolation()
    {
        var path = new ZoneNavigationPath(); path.PushBack(Point(0)); path.PushBack(Point(3, 4, 5));
        var position = default(Vector3);
        foreach (var distance in new[] { 100f, float.PositiveInfinity, float.NaN })
        {
            path.TryGetPosition(distance, false, true, ref position);
            await Assert.That(position).IsEqualTo(new Vector3(3, 4, 5));
        }
        path.Clear(); path.PushBack(Point(2, 3, 4)); path.PushBack(Point(2, 3, 4), true);
        path.TryGetPosition(0, false, false, ref position);
        await Assert.That(position).IsEqualTo(new Vector3(2, 3, 4));
    }

    [Test]
    public async Task CopyOwnsItsPointStorageWhileRetainingSharedSmartObjectData()
    {
        var from = new object(); var to = new object(); var metadata = new ZoneSmartObjectNavigationData(from, to);
        var source = new ZoneNavigationPath { EndDirection = Vector3.UnitY, CurrentFraction = 0.5f, StuckTime = 8, DiscardedPathLength = 4, PathEndIsAsRequested = false };
        source.PushBack(Point(0)); source.PushBack(new(new(4, 0, 0), 0x80, metadata, 4));
        var copy = source.Clone(); var version = source.Version;
        await Assert.That(copy.Version).IsEqualTo(version + 1);
        await Assert.That(copy.Points.SequenceEqual(source.Points)).IsTrue();
        await Assert.That(ReferenceEquals(copy.GetEndSmartObjectData(), metadata)).IsTrue();
        source.Clear();
        await Assert.That(copy.Count).IsEqualTo(2);
        await Assert.That(copy.CurrentFraction).IsEqualTo(0.5f);
        await Assert.That(copy.StuckTime).IsEqualTo(8);
        await Assert.That(copy.DiscardedPathLength).IsEqualTo(4);
        await Assert.That(copy.PathEndIsAsRequested).IsFalse();
        await Assert.That(ReferenceEquals(copy.GetEndSmartObjectData()?.From, from)).IsTrue();
        copy.CopyFrom(copy);
        await Assert.That(copy.Version).IsEqualTo(version + 2);
        await Assert.That(copy.Count).IsEqualTo(2);
        copy.ReplaceFront(new(Vector3.Zero, 0x80, metadata, 5));
        var next = default(ZoneNavigationPoint); copy.TryAdvance(ref next);
        await Assert.That(copy.Count).IsEqualTo(1);
        await Assert.That(ReferenceEquals(copy.GetEndSmartObjectData()?.To, to)).IsTrue();
    }

    [Test]
    public async Task ClearResetsProgressAndPreservesConfigurationAndExistingOutputSnapshots()
    {
        var path = new ZoneNavigationPath { EndDirection = new(2, 3, 4), CurrentFraction = 0.5f, StuckTime = 7, DiscardedPathLength = 9, PathEndIsAsRequested = false };
        path.PushBack(Point(0)); path.PushBack(Point(1)); path.PushBack(Point(2));
        var output = path.GetMovementOutput(); var version = path.Version;
        await Assert.That(output.Count).IsEqualTo(2);
        await Assert.That(output[0]).IsEqualTo(new ZoneNavigationMovementPoint(new(2, 0, 0), new(2, 3, 4)));
        await Assert.That(output[1]).IsEqualTo(new ZoneNavigationMovementPoint(new(1, 0, 0), Vector3.Zero));
        path.Clear();
        await Assert.That(path.Version).IsEqualTo(version + 1);
        await Assert.That(path.Count).IsEqualTo(0);
        await Assert.That(path.CurrentFraction).IsEqualTo(0);
        await Assert.That(path.StuckTime).IsEqualTo(0);
        await Assert.That(path.DiscardedPathLength).IsEqualTo(0);
        await Assert.That(path.PathEndIsAsRequested).IsTrue();
        await Assert.That(path.EndDirection).IsEqualTo(new Vector3(2, 3, 4));
        await Assert.That(output[0].Position).IsEqualTo(new Vector3(2, 0, 0));
        path.Clear();
        await Assert.That(path.Version).IsEqualTo(version + 2);
    }

    [Test]
    public async Task EndSmartObjectQueryRequiresBothNavigationTypeAndMethod()
    {
        var metadata = new ZoneSmartObjectNavigationData(new object(), new object());
        foreach (var (type, method, expected) in new[] { (0x80u, 4, true), (0x80u, 5, true), (0x80u, 3, false), (1u, 4, false) })
        {
            var path = new ZoneNavigationPath(); path.PushBack(new(Vector3.Zero, type, metadata, method));
            await Assert.That(ReferenceEquals(path.GetEndSmartObjectData(), metadata)).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task CreationParametersStartWithTheNativeDefaultsAndRemainIndependentOfTheOutputDirection()
    {
        var path = new ZoneNavigationPath(); var parameters = path.CreationParameters;
        await Assert.That(parameters.ForceBuildingId).IsEqualTo(-1);
        await Assert.That(parameters).IsEqualTo(new ZoneNavigationPathCreationParameters());
        await Assert.That(path.DebugLines.Count + path.DebugSpheres.Count + path.ObjectsAdjustedFor.Count).IsEqualTo(0);
        path.CreationParameters = parameters with { EndDirection = Vector3.UnitY, EndDistance = 7, ClosedLoop = true };
        path.EndDirection = Vector3.UnitZ;
        path.PushBack(Point(0)); path.PushBack(Point(2));
        await Assert.That(path.GetMovementOutput()[0].Direction).IsEqualTo(Vector3.UnitZ);
        path.Clear();
        await Assert.That(path.CreationParameters.EndDirection).IsEqualTo(Vector3.UnitY);
        await Assert.That(path.CreationParameters.EndDistance).IsEqualTo(7);
        await Assert.That(path.CreationParameters.ClosedLoop).IsTrue();
    }

    [Test]
    public async Task CompleteCopyPreservesEveryCreationFieldAndOwnsItsDiagnosticCollections()
    {
        var parameters = new ZoneNavigationPathCreationParameters
        {
            Start = new(1, 2, 3), End = new(4, 5, 6), StartDirection = new(7, 8, 9), EndDirection = new(10, 11, 12),
            ForceBuildingId = 41, AllowDangerousDestination = true, PrecalculatedPath = true,
            InhibitPathRegeneration = true, ContinueMovingAtEnd = true, IsDirectional = true,
            EndDistance = BitConverter.UInt32BitsToSingle(0x7fc00123), ClosedLoop = true
        };
        var source = new ZoneNavigationPath { CreationParameters = parameters };
        var line = new ZoneNavigationDebugLine(new(1, 2, 3), new(4, 5, 6), new(1, 0.5f, -1, 2));
        var sphere = new ZoneNavigationDebugSphere(new(7, 8, 9), -2, new(3, 4, 5, 6));
        source.AddDebugLine(line, true); source.AddDebugSphere(sphere, true);
        var adjusted = new object(); source.AddObjectAdjustedFor(adjusted);
        var copy = source.Clone();
        await Assert.That(copy.CreationParameters).IsEqualTo(parameters);
        await Assert.That(BitConverter.SingleToUInt32Bits(copy.CreationParameters.EndDistance)).IsEqualTo(0x7fc00123u);
        source.Clear(); source.ClearObjectsAdjustedFor(); source.CreationParameters = new();
        await Assert.That(copy.DebugLines.Single()).IsEqualTo(line);
        await Assert.That(copy.DebugSpheres.Single()).IsEqualTo(sphere);
        await Assert.That(ReferenceEquals(copy.ObjectsAdjustedFor.Single(), adjusted)).IsTrue();
        await Assert.That(copy.CreationParameters).IsEqualTo(parameters);
        var version = copy.Version; copy.CopyFrom(copy);
        await Assert.That(copy.Version).IsEqualTo(version + 1);
        await Assert.That(copy.DebugLines.Count + copy.DebugSpheres.Count + copy.ObjectsAdjustedFor.Count).IsEqualTo(3);
    }

    [Test]
    public async Task DiagnosticGeometryUsesTheCurrentDebugFlagAndHasAnIndependentClearPhase()
    {
        var path = new ZoneNavigationPath(); path.PushBack(Point(1)); path.CurrentFraction = 0.5f;
        var line = new ZoneNavigationDebugLine(Vector3.Zero, Vector3.One, new(2, -1, 3, 0.5f));
        var sphere = new ZoneNavigationDebugSphere(Vector3.One, float.NegativeInfinity, new(1, 2, 3, 4));
        var version = path.Version;
        path.AddDebugLine(line, false); path.AddDebugSphere(sphere, false);
        await Assert.That(path.DebugLines.Count + path.DebugSpheres.Count).IsEqualTo(0);
        path.AddDebugLine(line, true); path.AddDebugLine(line, true); path.AddDebugSphere(sphere, true);
        await Assert.That(path.DebugLines.Count).IsEqualTo(2);
        await Assert.That(path.DebugSpheres.Single()).IsEqualTo(sphere);
        await Assert.That(path.Version).IsEqualTo(version);
        path.ClearDebugGeometry();
        await Assert.That(path.DebugLines.Count + path.DebugSpheres.Count).IsEqualTo(0);
        await Assert.That(path.Points.Single()).IsEqualTo(Point(1));
        await Assert.That(path.CurrentFraction).IsEqualTo(0.5f);
        await Assert.That(path.Version).IsEqualTo(version);
    }

    [Test]
    public async Task ClearingAPathKeepsAdjustedObjectIdentityAndOrderUntilTheExplicitAdjustmentPhase()
    {
        var first = new object(); var second = new object(); var path = new ZoneNavigationPath();
        foreach (var item in new[] { first, null, second, first }) path.AddObjectAdjustedFor(item);
        var copy = path.Clone(); var version = path.Version;
        path.Clear();
        await Assert.That(path.ObjectsAdjustedFor.Count).IsEqualTo(4);
        await Assert.That(ReferenceEquals(path.ObjectsAdjustedFor[0], first)).IsTrue();
        await Assert.That(path.ObjectsAdjustedFor[1]).IsNull();
        await Assert.That(ReferenceEquals(path.ObjectsAdjustedFor[2], second)).IsTrue();
        await Assert.That(ReferenceEquals(path.ObjectsAdjustedFor[3], first)).IsTrue();
        path.ClearObjectsAdjustedFor();
        await Assert.That(path.Version).IsEqualTo(version + 1);
        await Assert.That(path.ObjectsAdjustedFor.Count).IsEqualTo(0);
        await Assert.That(copy.ObjectsAdjustedFor.Count).IsEqualTo(4);
        await Assert.That(ReferenceEquals(copy.ObjectsAdjustedFor[3], first)).IsTrue();
    }
}
