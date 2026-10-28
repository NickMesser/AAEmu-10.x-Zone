using System.Numerics;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Tests;

public sealed class ManagedCapsuleQueryTests
{
    [Test]
    public async Task TriangleCornersRejectCapsuleBoundingBoxFalsePositives()
    {
        var scene = TriangleScene();
        var capsule = ZonePhysicsShape.Capsule(1, .5f);
        await Assert.That(scene.TryCollide(capsule, new(2, 2, 0))).IsFalse();
        await Assert.That(scene.TryCollide(ZonePhysicsShape.Box(capsule.BoundingHalfExtents), new(2, 2, 0))).IsTrue();
        await Assert.That(scene.TryCollide(capsule, new(1.6f, 1.6f, 0))).IsTrue();
        await Assert.That(scene.CapsulesUseBoundingBoxes).IsFalse();
    }

    [Test]
    public async Task CapsuleSweepAroundTriangleVertexMatchesIndependentCircleDistance()
    {
        var scene = TriangleScene();
        var capsule = ZonePhysicsShape.Capsule(1, .5f);
        await Assert.That(scene.TrySweep(capsule, new(-3, -.5f, 0), new(6, 0, 0), out var hit)).IsTrue();
        await Near(hit.Distance, 3 - MathF.Sqrt(.75f), .003f);
        await Near(hit.Normal, new(-MathF.Sqrt(.75f), -.5f, 0), .003f);
        await Near(hit.Position, Vector3.Zero, .003f);
        await Assert.That(hit.PhysicalMaterialId).IsEqualTo((ushort)23);
    }

    [Test]
    public async Task CapsuleFloorUsesOuterRadiusAndAllowsTangentAndAwayMotion()
    {
        var scene = FloorScene();
        var capsule = ZonePhysicsShape.Capsule(.5f, 1);
        await Assert.That(scene.TryCollide(capsule, new(0, 0, 1.5f))).IsTrue();
        await Assert.That(scene.TryCollide(capsule, new(0, 0, 1.51f))).IsFalse();
        await Assert.That(scene.TrySweep(capsule, new(0, 0, 5), new(0, 0, -10), out var hit)).IsTrue();
        await Near(hit.Distance, 3.5f, .003f); await Near(hit.Normal, Vector3.UnitZ);
        await Assert.That(scene.TrySweep(capsule, new(0, 0, 1.5f), new(10, 0, 0), out _)).IsFalse();
        await Assert.That(scene.TrySweep(capsule, new(0, 0, 1.5f), new(0, 0, 5), out _)).IsFalse();
        await Assert.That(scene.TryCollide(capsule, new(0, 0, -10))).IsFalse();
    }

    [Test]
    public async Task ZeroSegmentCapsuleIsASphereAndCurveYIsUnused()
    {
        var scene = FloorScene();
        var sphere = new ZonePhysicsShape(new(.5f, 999, 0), true);
        await Assert.That(sphere.BoundingHalfExtents).IsEqualTo(new Vector3(.5f));
        await Assert.That(scene.TrySweep(sphere, new(0, 0, 5), new(0, 0, -10), out var hit)).IsTrue();
        await Near(hit.Distance, 4.5f, .003f);
        var small = TriangleScene();
        await Assert.That(small.TryCollide(sphere, new(0, 10, 0))).IsFalse();
    }

    [Test]
    public async Task RoundedCapsuleEndsDifferFromCylindersWithTheSameOuterBounds()
    {
        var scene = new ZoneManagedStaticGeometry();
        scene.AddInstance(new ZoneManagedTriangleMesh([new(.75f, -.05f, 1.4f), new(.85f, -.05f, 1.4f), new(.8f, .05f, 1.4f)], [0, 1, 2]), Matrix4x4.Identity);
        await Assert.That(scene.TryCollide(ZonePhysicsShape.Capsule(1, .5f), Vector3.Zero)).IsFalse();
        await Assert.That(scene.TryCollide(ZonePhysicsShape.Cylinder(1, 1.5f), Vector3.Zero)).IsTrue();
    }

    [Test]
    public async Task TerrainCapsulesPreserveSolidBelowHolesAndLoadedCellEdges()
    {
        var heights = new ZoneTerrainHeightField();
        heights.LoadCell(1, 1, TerrainHeightFieldTests.CreateCell(0, 0));
        var terrain = new ZoneManagedTerrainGeometry(heights);
        var capsule = ZonePhysicsShape.Capsule(1, .5f);
        await Assert.That(terrain.TryCollide(capsule, new(1100, 1100, -10))).IsTrue();
        await Assert.That(terrain.TryCollide(capsule, new(1023.2f, 1023.2f, -10))).IsFalse();
        await Assert.That(terrain.TryCollide(capsule, new(1023.4f, 1023.4f, -10))).IsTrue();
        await Assert.That(terrain.TryCollide(capsule, new(1023.2f, 1100, -10))).IsTrue();
        heights.UnloadCell(1, 1);
        await Assert.That(terrain.TryCollide(capsule, new(1100, 1100, -10))).IsFalse();
        heights.LoadCell(1, 1, TerrainHeightFieldTests.CreateCell(0, 0, sample: (_, _, _, _) => 31));
        await Assert.That(terrain.TryCollide(capsule, new(1100, 1100, -10))).IsFalse();
    }

    [Test]
    public async Task TerrainCapsuleSweepsRemainCorrectAtZoneCoordinateEightThousand()
    {
        var heights = new ZoneTerrainHeightField();
        heights.LoadCell(7, 7, TerrainHeightFieldTests.CreateCell(0, 0));
        var terrain = new ZoneManagedTerrainGeometry(heights);
        var capsule = ZonePhysicsShape.Capsule(.4f, .3f);
        await Assert.That(terrain.TrySweep(capsule, new(8000, 8000, 3), new(0, 0, -5), out var hit)).IsTrue();
        await Near(hit.Distance, 2.3f, .003f); await Near(hit.Normal, Vector3.UnitZ);
        await Assert.That(terrain.TrySweep(capsule, new(8000, 8000, .7f), new(5, 0, 0), out _)).IsFalse();
        await Assert.That(terrain.CapsulesUseBoundingBoxes).IsFalse();
    }

    [Test]
    public async Task TrueCapsulesAgainstAnalyticTargetsRejectRoundedCornerFalsePositives()
    {
        var sphere = PrimitiveScene(ZoneManagedPrimitiveKind.Sphere);
        var query = ZonePhysicsShape.Capsule(.5f, 1);
        await Assert.That(sphere.TryCollide(query, new(1.2f, 1.2f, 0))).IsFalse();
        await Assert.That(sphere.TryCollide(ZonePhysicsShape.Box(query.BoundingHalfExtents), new(1.2f, 1.2f, 0))).IsTrue();
        await Assert.That(sphere.TryCollide(query, new(1, 1, 0))).IsTrue();
        await Assert.That(sphere.TrySweep(query, new(3, 0, 0), new(-6, 0, 0), out var hit)).IsTrue();
        await Near(hit.Distance, 1.5f, .003f); await Near(hit.Normal, Vector3.UnitX, .005f);
        var cylinder = PrimitiveScene(ZoneManagedPrimitiveKind.Cylinder);
        query = ZonePhysicsShape.Capsule(.5f, .5f);
        await Assert.That(cylinder.TryCollide(query, new(1.3f, 0, 2.9f))).IsTrue();
        await Assert.That(cylinder.TryCollide(query, new(1.4f, 0, 3))).IsFalse();
        var capsule = PrimitiveScene(ZoneManagedPrimitiveKind.Capsule);
        await Assert.That(capsule.TrySweep(ZonePhysicsShape.Capsule(.5f, 1), new(0, 0, 5), new(0, 0, -10), out hit)).IsTrue();
        await Near(hit.Distance, .5f, .003f);
    }

    [Test]
    public async Task UprightCapsulesAgainstAffineEllipsoidsKeepKnownPoleDistances()
    {
        foreach (var mirror in new[] { 1, -1 })
        {
            var transform = Matrix4x4.CreateScale(mirror * 2, .5f, 3) * Matrix4x4.CreateRotationZ(.4f) * Matrix4x4.CreateTranslation(8000, 8000, 0);
            var scene = PrimitiveScene(ZoneManagedPrimitiveKind.Sphere, transform);
            await Assert.That(scene.TrySweep(ZonePhysicsShape.Capsule(.5f, 1), new(8000, 8000, 7), new(0, 0, -14), out var hit)).IsTrue();
            await Near(hit.Distance, 2.5f, .003f); await Near(hit.Normal, Vector3.UnitZ, .005f);
            await Assert.That(scene.TrySweep(ZonePhysicsShape.Cylinder(.5f, 1), new(8000, 8000, 7), new(0, 0, -14), out hit)).IsTrue();
            await Near(hit.Distance, 3, .003f);
        }
    }

    [Test]
    public async Task ActorCapsulesAndMixedBoxPairsUseActualRoundedGeometry()
    {
        var capsule = ZonePhysicsShape.Capsule(1, .5f);
        await Assert.That(ZoneManagedShapeQueries.Overlaps(capsule, new(1.8f, 1.8f, 0), capsule, Vector3.Zero)).IsFalse();
        await Assert.That(ZoneManagedShapeQueries.Overlaps(capsule, new(1.2f, 1.2f, 0), capsule, Vector3.Zero)).IsTrue();
        await Assert.That(ZoneManagedShapeQueries.Sweep(capsule, new(-5, 1, 0), new(10, 0, 0), capsule, Vector3.Zero,
            out var fraction, out var normal, out _)).IsTrue();
        await Near(fraction * 10, 5 - MathF.Sqrt(3), .003f);
        await Near(normal, new(-MathF.Sqrt(.75f), .5f, 0), .003f);
        var box = ZonePhysicsShape.Box(Vector3.One);
        capsule = ZonePhysicsShape.Capsule(.5f, 1);
        await Assert.That(ZoneManagedShapeQueries.Overlaps(capsule, new(1.4f, 1.4f, 0), box, Vector3.Zero)).IsFalse();
        await Assert.That(ZoneManagedShapeQueries.Overlaps(box, Vector3.Zero, capsule, new(1.4f, 1.4f, 0))).IsFalse();
        await Assert.That(ZoneManagedShapeQueries.Overlaps(capsule, new(1.3f, 1.3f, 0), box, Vector3.Zero)).IsTrue();
    }

    [Test]
    public async Task CylinderTriangleSweepsUseTheirActualCapAndRadialSupport()
    {
        var scene = FloorScene();
        var cylinder = ZonePhysicsShape.Cylinder(.5f, 1);
        await Assert.That(scene.TrySweep(cylinder, new(0, 0, 5), new(0, 0, -10), out var hit)).IsTrue();
        await Near(hit.Distance, 4, .003f); await Near(hit.Normal, Vector3.UnitZ, .005f);
        await Assert.That(scene.TrySweep(cylinder, new(0, 0, 1), new(0, 0, 5), out _)).IsFalse();
        var corner = TriangleScene();
        await Assert.That(corner.TryCollide(ZonePhysicsShape.Cylinder(1, 1), new(2, 2, 0))).IsFalse();
    }

    [Test]
    public async Task ActorBoxPairsKeepTheirExactSlabAndOverlapBoundary()
    {
        var box = ZonePhysicsShape.Box(Vector3.One);
        await Assert.That(ZoneManagedShapeQueries.Overlaps(box, new(2, 0, 0), box, Vector3.Zero)).IsTrue();
        await Assert.That(ZoneManagedShapeQueries.Overlaps(box, new(2.0005f, 0, 0), box, Vector3.Zero)).IsFalse();
        await Assert.That(ZoneManagedShapeQueries.Sweep(box, new(-6, 0, 0), new(10, 0, 0), box, Vector3.Zero,
            out var fraction, out var normal, out _)).IsTrue();
        await Assert.That(fraction).IsEqualTo(.4f);
        await Assert.That(normal).IsEqualTo(-Vector3.UnitX);
    }

    [Test]
    public async Task DegenerateAuthoredTrianglesRemainNoncollidableForEveryShapeKind()
    {
        var scene = new ZoneManagedStaticGeometry();
        scene.AddInstance(new ZoneManagedTriangleMesh([Vector3.Zero, Vector3.UnitX, Vector3.UnitX * 2], [0, 1, 2]), Matrix4x4.Identity);
        foreach (var shape in new[] { ZonePhysicsShape.Box(Vector3.One), ZonePhysicsShape.Capsule(1, 1), ZonePhysicsShape.Cylinder(1, 1) })
        {
            await Assert.That(scene.TryCollide(shape, Vector3.Zero)).IsFalse();
            await Assert.That(scene.TrySweep(shape, new(0, 0, 3), new(0, 0, -6), out _)).IsFalse();
        }
    }

    private static ZoneManagedStaticGeometry TriangleScene()
    {
        var scene = new ZoneManagedStaticGeometry();
        scene.AddInstance(new ZoneManagedTriangleMesh([Vector3.Zero, new(2, 0, 0), new(0, 2, 0)], [0, 1, 2], [23]), Matrix4x4.Identity);
        return scene;
    }
    private static ZoneManagedStaticGeometry FloorScene()
    {
        var scene = new ZoneManagedStaticGeometry();
        scene.AddInstance(new ZoneManagedTriangleMesh([new(-20, -20, 0), new(20, -20, 0), new(-20, 20, 0), new(20, 20, 0)], [0, 1, 2, 1, 3, 2]), Matrix4x4.Identity);
        return scene;
    }
    private static ZoneManagedStaticGeometry PrimitiveScene(ZoneManagedPrimitiveKind kind, Matrix4x4? transform = null)
    {
        var scene = new ZoneManagedStaticGeometry();
        scene.AddInstance(new ZoneManagedPrimitiveGeometry(kind, Vector3.Zero, kind == ZoneManagedPrimitiveKind.Sphere ? Vector3.Zero : Vector3.UnitZ,
            1, kind == ZoneManagedPrimitiveKind.Sphere ? 0 : 2, 31), transform ?? Matrix4x4.Identity);
        return scene;
    }
    private static async Task Near(float actual, float expected, float tolerance) => await Assert.That(MathF.Abs(actual - expected)).IsLessThan(tolerance);
    private static async Task Near(Vector3 actual, Vector3 expected, float tolerance = .0001f) => await Assert.That(Vector3.Distance(actual, expected)).IsLessThan(tolerance);
}
