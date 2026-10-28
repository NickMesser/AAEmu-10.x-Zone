using System.Numerics;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Tests;

public sealed class ManagedStanceDiskPreflightTests
{
    [Test]
    public async Task InternalDiskAgainstASolidHasTheActualZeroHeightVerticalExit()
    {
        var disk = ZonePhysicsShape.Cylinder(.5f, 0); var target = ZonePhysicsShape.Box(Vector3.One);
        await Assert.That(ZoneManagedShapeQueries.TryUpwardUnprojectionForStancePreflight(disk, Vector3.Zero,
            target, Vector3.Zero, out var result)).IsTrue();
        await Near(result.Depth, 1); await Near(result.NormalFallbackDepth, 1);
        await Assert.That(ZoneManagedShapeQueries.TryUpwardUnprojectionForStancePreflight(disk, new(0, 0, 1.001f),
            target, Vector3.Zero, out _)).IsFalse();
        await Assert.That(() => ZoneManagedShapeQueries.TryUpwardUnprojection(disk, Vector3.Zero, target, Vector3.Zero, out _)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task SlopingDiskExitUsesItsCircleWithoutACapsuleHemisphere()
    {
        var disk = ZonePhysicsShape.Cylinder(.5f, 0);
        var scene = new ZoneManagedStaticGeometry();
        scene.AddInstance(new ZoneManagedTriangleMesh([new(-20, -20, -10), new(20, -20, 10), new(-20, 20, -10)], [0, 1, 2], [17]), Matrix4x4.Identity);
        var result = scene.GetUpwardUnprojectionsForStancePreflight(disk, new(-3, -2, -1.5f)).Single();
        await Near(result.Depth, .25f);
        await Assert.That(result.PhysicalMaterialId).IsEqualTo((ushort)17);
        await Assert.That(() => scene.GetUpwardUnprojections(disk, new(-3, -2, -1.5f))).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ParallelDiskTouchDoesNotDemandAnEpaVolumeOrInventPositiveDepth()
    {
        var disk = ZonePhysicsShape.Cylinder(.5f, 0);
        var heights = new ZoneTerrainHeightField(); heights.LoadCell(1, 1, TerrainHeightFieldTests.CreateCell(0, 0));
        var terrain = new ZoneManagedTerrainGeometry(heights);
        foreach (var z in new[] { -.0005f, 0, .0005f })
            await Assert.That(terrain.GetUpwardUnprojectionsForStancePreflight(disk, new(1100, 1100, z)).Count).IsEqualTo(0);
        await Assert.That(() => terrain.GetUpwardUnprojections(disk, new(1100, 1100, 0))).Throws<ArgumentOutOfRangeException>();
    }

    private static async Task Near(float actual, float expected, float tolerance = .0002f) => await Assert.That(MathF.Abs(actual - expected)).IsLessThan(tolerance);
}
