using System.Buffers.Binary;
using System.Numerics;
using AAEmu.Zone.Content;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Tests;

public sealed class ManagedWaterQueriesTests
{
    [Test]
    public async Task ExplicitOceanAndNoOceanValuesRemainIndependentOfTerrainHeightAndObjectBounds()
    {
        var fixture = new ManagedSceneActivationTests.Fixture((9, 14));
        using var ocean = Queries(100, Cell(fixture));
        await Assert.That(ocean.Report.Complete).IsTrue();
        await Assert.That(ocean.GetWaterLevel(new(10, 10, 900), 2)).IsEqualTo(100f);
        await Assert.That(ocean.MaximumPotentialWaterLevel).IsEqualTo(100f);
        using var none = Queries(-1_000_000, Cell(fixture));
        await Assert.That(none.GetWaterLevel(new(10, 10, 900), 2)).IsEqualTo(ZoneManagedWaterQueries.NoWaterLevel);
        await Assert.That(none.MaximumPotentialWaterLevel).IsEqualTo(ZoneManagedWaterQueries.NoWaterLevel);
    }

    [Test]
    public async Task ConcaveLakeUsesItsActualContourAndFiniteStrictWorldBounds()
    {
        var fixture = new ManagedSceneActivationTests.Fixture((9, 14));
        Vector3[] contour = [new(10, 10, 100), new(40, 10, 100), new(40, 20, 100),
            new(20, 20, 100), new(20, 40, 100), new(10, 40, 100)];
        using var water = Queries(50, Cell(fixture, Water(1, 2, contour, 20)));
        await Assert.That(water.GetWaterLevel(new(15, 30, 99), 0)).IsEqualTo(100f);
        await Assert.That(water.GetWaterLevel(new(30, 30, 99), 100)).IsEqualTo(50f);
        await Assert.That(water.GetWaterLevel(new(15, 15, 105), 0)).IsEqualTo(100f);
        await Assert.That(water.GetWaterLevel(new(15, 15, 80), 2)).IsEqualTo(50f);
        await Assert.That(water.GetWaterLevel(new(15, 15, water.Report.Volumes[0].BoundsMax!.Value.Z), 100)).IsEqualTo(50f);
        await Assert.That(water.GetWaterLevel(new(10, 15, 100), 2)).IsEqualTo(50f);
    }

    [Test]
    public async Task PlanarRiverUsesTheRealBoundsDiagonalRangeAboveItsTriangleSurface()
    {
        var fixture = new ManagedSceneActivationTests.Fixture((9, 14));
        using var water = Queries(50, Cell(fixture, Water(2, 3, Square(100), 20, flow: 1.4f)));
        await Assert.That(water.Report.LoadedPhysicalAreas).IsEqualTo(1);
        await Assert.That(water.GetWaterLevel(new(20, 20, 99), 2)).IsEqualTo(100f);
        await Assert.That(water.GetWaterLevel(new(20, 20, 102), 2)).IsEqualTo(100f);
        await Assert.That(water.GetWaterLevel(new(20, 20, 102.1f), 2)).IsEqualTo(50f);
        await Assert.That(water.GetWaterLevel(new(20, 20, water.Report.Volumes[0].BoundsMax!.Value.Z), 100)).IsEqualTo(50f);
    }

    [Test]
    public async Task ZeroRangeRiverQueriesRefuseBeforeReturningAnUnownedMeshBoundaryValue()
    {
        var fixture = new ManagedSceneActivationTests.Fixture((9, 14));
        using var water = Queries(50, Cell(fixture, Water(2, 3, Square(100), 20)));
        foreach (var point in new[] { new Vector3(20, 20, 99), new Vector3(20, 20, 100.1f), new Vector3(20, 20, 100) })
            await Assert.That(() => water.GetWaterLevel(point, 0)).Throws<NotSupportedException>();
        await Assert.That(water.GetWaterLevel(new(100, 100, 99), 0)).IsEqualTo(50f);
        await Assert.That(water.MaximumPotentialWaterLevelAt(new(20, 20, 99))).IsGreaterThanOrEqualTo(100f);
    }

    [Test]
    public async Task FoldedRiverStripRefusesInsteadOfSubstitutingItsPolygonForItsMesh()
    {
        var fixture = new ManagedSceneActivationTests.Fixture((9, 14));
        Vector3[] folded = [new(10, 10, 100), new(40, 10, 100), new(40, 20, 100),
            new(20, 20, 100), new(20, 40, 100), new(10, 40, 100)];
        using var water = Queries(50, Cell(fixture, Water(19, 3, folded, 20)));
        await Assert.That(water.Report.UnsupportedRows).IsEqualTo(1);
        await Assert.That(() => water.GetWaterLevel(new(15, 15, 99), 2)).Throws<NotSupportedException>();
        await Assert.That(() => water.MaximumPotentialWaterLevel).Throws<NotSupportedException>();
    }

    [Test]
    public async Task FactoryProjectionUsesLakeFirstRenderHeightAndRiverBoundsPlaneInsteadOfRawAuthoredD()
    {
        var fixture = new ManagedSceneActivationTests.Fixture((9, 14));
        var lake = Water(3, 2, Square(42), 20, renderHeight: 100, rawPlaneHeight: 999, boundsHeight: 777);
        var river = Water(4, 3, Square(42, 50), 20, renderHeight: 10, rawPlaneHeight: 999, boundsHeight: 101);
        using var water = Queries(50, Cell(fixture, lake, river));
        await Assert.That(water.GetWaterLevel(new(20, 20, 100), 0)).IsEqualTo(100f);
        await Assert.That(water.GetWaterLevel(new(70, 70, 101), 2)).IsEqualTo(101f);
        await Assert.That(water.Report.Volumes[0].SurfaceHeight).IsEqualTo((float?)100);
        await Assert.That(water.Report.Volumes[1].SurfaceHeight).IsEqualTo((float?)101);
    }

    [Test]
    public async Task NativeIdDeduplicationKeepsTheFirstAreaAndDistinguishesEmptyLakeFromEmptyRiver()
    {
        var fixture = new ManagedSceneActivationTests.Fixture((9, 14));
        using var lake = Queries(50, Cell(fixture, Water(5, 2, [], 20), Water(5, 2, Square(100), 20)));
        await Assert.That(lake.Report.Complete).IsTrue();
        await Assert.That(lake.Report.LoadedPhysicalAreas).IsEqualTo(0);
        using var river = Queries(50, Cell(fixture, Water(6, 3, [], 20), Water(6, 3, Square(100), 20),
            Water(6, 3, Square(200), 20)));
        await Assert.That(river.Report.LoadedPhysicalAreas).IsEqualTo(1);
        await Assert.That(river.GetWaterLevel(new(20, 20, 100), 2)).IsEqualTo(100f);
    }

    [Test]
    public async Task MissingAndUnownedGeometryRefuseInsteadOfReturningAnOceanOnlyDryAnswer()
    {
        var fixture = new ManagedSceneActivationTests.Fixture((9, 14));
        var missing = new ZoneManagedSceneCellLoadReport(fixture.Layout.Cells[0], new(ZoneTerrainCellLoadStatus.Empty),
            new(null, null, null, null, ["Objects: invalid authored data"]));
        using var water = Queries(50, missing);
        await Assert.That(water.Report.Complete).IsFalse();
        await Assert.That(() => water.GetWaterLevel(new(20, 20, 100), 2)).Throws<NotSupportedException>();
        await Assert.That(() => water.MaximumPotentialWaterLevel).Throws<NotSupportedException>();
        var tilted = Water(7, 2, Square(100), 20);
        BinaryPrimitives.WriteSingleLittleEndian(tilted.AsSpan(0x47), .2f);
        using var unsupported = Queries(50, Cell(fixture, tilted));
        await Assert.That(unsupported.Report.UnsupportedRows).IsEqualTo(1);
        await Assert.That(() => unsupported.GetWaterLevel(new(20, 20, 100), 2)).Throws<NotSupportedException>();
        await Assert.That(() => new ZoneManagedWaterQueries(50, [Cell(fixture)], default)).Throws<NotSupportedException>();
    }

    [Test]
    public async Task KnownAbsentFilesAreAnExplicitEmptyInventoryWhileRetiredCellsCannotBeQueried()
    {
        var fixture = new ManagedSceneActivationTests.Fixture((9, 14), (10, 14));
        var empty = new ZoneManagedSceneCellLoadReport(fixture.Layout.Cells[1], new(ZoneTerrainCellLoadStatus.Empty),
            new(null, null, null, null, ["Objects: authored asset is absent.", "BigObjects: authored asset is absent."]));
        using var water = Queries(50, Cell(fixture, Water(8, 2,
            [new(1000, 10, 100), new(1040, 10, 100), new(1040, 40, 100), new(1000, 40, 100)], 20)), empty);
        await Assert.That(water.Report.Complete).IsTrue();
        await Assert.That(water.GetWaterLevel(new(1030, 20, 100), 2)).IsEqualTo(100f);
        water.RetireCell(new(0, 0));
        await Assert.That(water.GetWaterLevel(new(1030, 20, 100), 2)).IsEqualTo(50f);
        await Assert.That(water.MaximumPotentialWaterLevel).IsEqualTo(50f);
        await Assert.That(() => water.GetWaterLevel(new(1010, 20, 100), 2)).Throws<NotSupportedException>();
        water.Dispose(); await Assert.That(() => water.MaximumPotentialWaterLevel).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task OnlyTheFourNewestEligibleAreasContributeButTheDryCertificateIncludesEveryPossibleSurface()
    {
        var fixture = new ManagedSceneActivationTests.Fixture((9, 14));
        using var water = Queries(50, Cell(fixture, Water(10, 2, Square(200), 250),
            Water(11, 2, Square(100), 20), Water(12, 2, Square(99), 20),
            Water(13, 2, Square(98), 20), Water(14, 2, Square(97), 20)));
        await Assert.That(water.GetWaterLevel(new(20, 20, 100), 0)).IsEqualTo(100f);
        await Assert.That(water.MaximumPotentialWaterLevelAt(new(20, 20, 100))).IsGreaterThanOrEqualTo(200f);
        await Assert.That(water.MaximumPotentialWaterLevelAt(new(100, 100, 100))).IsEqualTo(50f);
    }

    [Test]
    public async Task NativePlaneOriginZeroSumFilterDoesNotBecomeAFabricatedSurface()
    {
        var fixture = new ManagedSceneActivationTests.Fixture((9, 14));
        using var water = Queries(50, Cell(fixture, Water(15, 2,
            [new(10, -10, 100), new(30, -10, 100), new(30, 10, 100), new(10, 10, 100)], 20)));
        await Assert.That(water.GetWaterLevel(new(20, 0, 100), 2)).IsEqualTo(50f);
        await Assert.That(water.MaximumPotentialWaterLevelAt(new(20, 0, 100))).IsGreaterThanOrEqualTo(100f);
    }

    [Test]
    public async Task FloatProjectionCertificateAtEightKilometersBoundsActualQueriesForEveryRange()
    {
        var fixture = new ManagedSceneActivationTests.Fixture((9, 14), (16, 14));
        var cell = new ZoneManagedSceneCellLoadReport(fixture.Layout.Cells[1], new(ZoneTerrainCellLoadStatus.Empty),
            new(Objects(), null, null, Objects(Water(16, 2, Square(326.5f, 810), 250)), []));
        using var water = Queries(100, Cell(fixture), cell);
        var maximum = water.MaximumPotentialWaterLevel;
        foreach (var z in new[] { 76.5001f, 100.12345f, 300.1111f, 326.5f, 333.3333f })
            foreach (var range in new[] { 0f, 2f, 20f, 100f })
            {
                var point = new Vector3(8000, 830, z);
                await Assert.That(water.GetWaterLevel(point, range)).IsLessThanOrEqualTo(maximum);
                await Assert.That(water.GetWaterLevel(point, range)).IsLessThanOrEqualTo(water.MaximumPotentialWaterLevelAt(point));
            }
        await Assert.That(maximum).IsLessThan(326.501f);
        await Assert.That(() => water.GetWaterLevel(new(float.NaN, 0, 0), 1)).Throws<InvalidDataException>();
        await Assert.That(() => water.GetWaterLevel(new(10, 10, 0), -1)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task NativeRoundedEdgeSearchRetainsItsAuthoredVertexBoundaryAtWorldCoordinates()
    {
        //Actual projected contour5402473402945602256. Original35047C80 then
        //3013FCB0 rejects this vertex for every range, while its center is wet.
        Vector3[] contour =
        [
            new(2264.329f, 706.25977f, 156.66205f), new(2266.3298f, 672.49133f, 156.66205f),
            new(2279.6912f, 651.9812f, 156.66205f), new(2288.582f, 652.7649f, 156.66205f),
            new(2292.7263f, 655.1339f, 156.66205f), new(2299.3071f, 663.44275f, 156.66205f),
            new(2295.9514f, 667.99097f, 156.66205f), new(2303.501f, 672.40906f, 156.66205f),
            new(2303.4775f, 689.03845f, 156.66205f), new(2300.2935f, 700.7787f, 156.66205f),
            new(2294.3293f, 707.13306f, 156.66205f)
        ];
        var geometry = new ZoneManagedWaterVolumeGeometry(contour, 1, false);
        foreach (var range in new[] { 0f, 2f, 100f })
        {
            await Assert.That(geometry.Contains(contour[0], range)).IsFalse();
            await Assert.That(geometry.Contains(geometry.Center, range)).IsTrue();
        }
    }

    private static ZoneManagedWaterQueries Queries(float ocean, params ZoneManagedSceneCellLoadReport[] cells) =>
        new(ocean, cells, ZoneManagedWaterQueryProfile.StaticServerWithoutOceanVortex);
    private static ZoneManagedSceneCellLoadReport Cell(ManagedSceneActivationTests.Fixture fixture, params byte[][] rows) =>
        new(fixture.Layout.Cells[0], new(ZoneTerrainCellLoadStatus.Empty), new(Objects(), null, null, Objects(rows), []));
    private static Vector3[] Square(float height, float offset = 0) =>
        [new(10 + offset, 10 + offset, height), new(40 + offset, 10 + offset, height),
         new(40 + offset, 40 + offset, height), new(10 + offset, 40 + offset, height)];
    private static ZoneCellObjectContent Objects(params byte[][] rows)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(0); writer.Write(0);
        foreach (var row in rows) { writer.Write((uint)ZoneCellObjectType.WaterVolume); writer.Write(row); }
        return ZoneCellObjectContent.Read(stream.ToArray(), ZoneCellObjectFileKind.BigObjects);
    }
    private static byte[] Water(ulong id, byte kind, Vector3[] physics, float depth, float flow = 0,
        float? renderHeight = null, float? rawPlaneHeight = null, float? boundsHeight = null)
    {
        var height = renderHeight ?? physics.FirstOrDefault().Z;
        Vector3[] render = Square(height);
        var bytes = new byte[119 + (render.Length + physics.Length) * 12];
        bytes[0x27] = kind; BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x2b), id);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x33), -1);
        F(0x37, .3f); F(0x4f, 1); F(0x53, -(rawPlaneHeight ?? height));
        V(12, new(1000, 1000, boundsHeight ?? height));
        F(0x6b, depth); F(0x6f, flow);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x67), render.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x73), physics.Length);
        var cursor = 119;
        foreach (var point in render.Concat(physics)) { V(cursor, point); cursor += 12; }
        return bytes;
        void F(int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset), value);
        void V(int offset, Vector3 value) { F(offset, value.X); F(offset + 4, value.Y); F(offset + 8, value.Z); }
    }
}
