using System.Numerics;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Tests;

public sealed class ManagedDirectedUnprojectionTests
{
    [Test]
    public async Task UpwardPlaneExitDoesNotProjectOrReuseTheShorterDownwardMtd()
    {
        var scene = Plane(0);
        foreach (var query in Queries)
        {
            var center = new Vector3(-3, -2, -.2f);
            var result = scene.GetUpwardUnprojections(query, center).Single();
            await Near(result.Depth, query.BoundingHalfExtents.Z + .2f);
            await Near(result.NormalFallbackDepth, query.BoundingHalfExtents.Z - .2f);
            await Near(result.Direction, Vector3.UnitZ); await Near(result.NormalFallbackDirection, -Vector3.UnitZ);
            await Near(result.Position.Z, 0);
            await Assert.That(result.PhysicalMaterialId).IsEqualTo((ushort)17);
            await Assert.That(result.SeparationTolerance).IsEqualTo(0f);
            await VerifyExit(scene, query, center, result);
        }
    }

    [Test]
    public async Task SlopingCapsuleExitUsesTheHemisphereFootprintAndKeepsItsNormalFallback()
    {
        var scene = Plane(.5f);
        var query = ZonePhysicsShape.Capsule(.4f, .6f);
        var center = new Vector3(-3, -2, -1.7f); // The plane is z=x/2; this center is .2 below it.
        var result = scene.GetUpwardUnprojections(query, center).Single();
        await Near(result.Depth, .6f + .4f * MathF.Sqrt(1.25f) + .2f);
        await Near(result.NormalFallbackDepth, .4f + (.6f - .2f) / MathF.Sqrt(1.25f));
        await Near(result.NormalFallbackDirection, Vector3.Normalize(new(.5f, 0, -1)));
        await Assert.That(result.Depth).IsGreaterThan(result.NormalFallbackDepth);
        await VerifyExit(scene, query, center, result);
    }

    [Test]
    public async Task AllNineUprightActorPairsHaveIndependentAnalyticVerticalExits()
    {
        var queries = new[] { ZonePhysicsShape.Box(new(.3f, .4f, .5f)), ZonePhysicsShape.Capsule(.3f, .4f), ZonePhysicsShape.Cylinder(.3f, .4f) };
        var targets = new[] { ZonePhysicsShape.Box(Vector3.One), ZonePhysicsShape.Capsule(.8f, .6f), ZonePhysicsShape.Cylinder(.8f, .6f) };
        float[,] expected = { { 1.8f, 2.2f, 1.4f }, { 2f, 1.3f + MathF.Sqrt(1.16f), 1.6f }, { 1.7f, 2.1f, 1.3f } };
        var center = new Vector3(.1f, .2f, -.3f);
        for (var q = 0; q < 3; q++)
            for (var t = 0; t < 3; t++)
            {
                await Assert.That(ZoneManagedShapeQueries.TryUpwardUnprojection(queries[q], center, targets[t], Vector3.Zero, out var result)).IsTrue();
                await Near(result.Depth, expected[q, t]);
                await Assert.That(result.SeparationTolerance).IsEqualTo(0f);
                await Assert.That(ZoneManagedShapeQueries.TryPenetration(queries[q], center + Vector3.UnitZ * (result.Depth + .001f), targets[t], Vector3.Zero, out _)).IsFalse();
                await Assert.That(ZoneManagedShapeQueries.TryPenetration(queries[q], center + Vector3.UnitZ * (result.Depth - .002f), targets[t], Vector3.Zero, out _)).IsTrue();
            }
        // The shortest contact direction is horizontal. Dividing/projecting its MTD would
        // supply no valid upward exit; the rounded-box top is nevertheless analytically finite.
        var corner = new Vector3(1.3f, 1.3f, 0);
        await Assert.That(ZoneManagedShapeQueries.TryUpwardUnprojection(ZonePhysicsShape.Capsule(.5f, .4f), corner,
            ZonePhysicsShape.Box(Vector3.One), Vector3.Zero, out var rounded)).IsTrue();
        await Near(rounded.Depth, 1.4f + MathF.Sqrt(.07f));
        await Near(rounded.NormalFallbackDirection.Z, 0);
    }

    [Test]
    public async Task VerticalTriangleAndAffinePrimitiveExitStayOnTheInitialTarget()
    {
        var wall = new ZoneManagedStaticGeometry();
        wall.AddInstance(new ZoneManagedTriangleMesh([new(0, -5, -5), new(0, 5, -5), new(0, 0, 5)], [0, 1, 2], [11]), Matrix4x4.Identity);
        var box = ZonePhysicsShape.Box(new(.2f));
        var result = wall.GetUpwardUnprojections(box, new(-.1f, 0, 0)).Single();
        await Near(result.Depth, 5.2f); await Near(result.NormalFallbackDepth, .1f);
        await Assert.That(result.SeparationTolerance).IsEqualTo(0f);
        var capsule = ZonePhysicsShape.Capsule(.2f, .4f);
        var curved = wall.GetUpwardUnprojections(capsule, new(-.1f, 0, 0)).Single();
        await Near(curved.Depth, 5.4f + MathF.Sqrt(.03f), .0015f);
        await Assert.That(curved.SeparationTolerance).IsEqualTo(ZoneManagedStaticGeometry.AnalyticContactTolerance);

        var shear = Matrix4x4.Identity; shear.M21 = .3f;
        var transform = Matrix4x4.CreateScale(-2, 1, 3) * shear * Matrix4x4.CreateRotationZ(.4f) * Matrix4x4.CreateTranslation(8000, 8000, 100);
        foreach (var query in Queries)
        {
            var scene = new ZoneManagedStaticGeometry();
            scene.AddInstance(new ZoneManagedPrimitiveGeometry(ZoneManagedPrimitiveKind.Sphere, Vector3.Zero, Vector3.Zero, 1, 0, 37), transform);
            var center = new Vector3(8000, 8000, 100);
            var exit = scene.GetUpwardUnprojections(query, center).Single();
            await Near(exit.Depth, 3 + query.BoundingHalfExtents.Z, .0015f);
            await Assert.That(exit.Depth).IsGreaterThanOrEqualTo(3 + query.BoundingHalfExtents.Z);
            await Assert.That(exit.PhysicalMaterialId).IsEqualTo((ushort)37);
            await VerifyExit(scene, query, center, exit);
        }
    }

    [Test]
    public async Task ReverseAdvanceNeverSelectsAnUnrelatedOverheadSurface()
    {
        var scene = new ZoneManagedStaticGeometry();
        scene.AddInstance(new ZoneManagedPrimitiveGeometry(ZoneManagedPrimitiveKind.Sphere, Vector3.Zero, Vector3.Zero, 1, 0, 37), Matrix4x4.Identity);
        scene.AddInstance(new ZoneManagedTriangleMesh([new(-10, -10, 1.255f), new(10, -10, 1.255f), new(0, 10, 1.255f)], [0, 1, 2], [99]), Matrix4x4.Identity);
        var results = scene.GetUpwardUnprojections(ZonePhysicsShape.Capsule(.25f, 0), Vector3.Zero);
        await Assert.That(results.Count).IsEqualTo(1);
        await Near(results[0].Depth, 1.25f, .001f);
        await Assert.That(results[0].PhysicalMaterialId).IsEqualTo((ushort)37);
        await Near(results[0].NormalFallbackDepth, 1.25f);
    }

    [Test]
    public async Task StanceEnvelopeAdmitsWholeEntitiesWithoutClippingTheirCeilingParts()
    {
        var ceiling = new ZoneManagedTriangleMesh([new(-5, -5, 1.2f), new(5, -5, 1.2f), new(0, 5, 1.2f)], [0, 1, 2], [18]);
        var query = ZonePhysicsShape.Capsule(.4f, .9f);
        var envelope = new ZonePhysicsQueryBounds(new(-.4f, -.4f, 0), new(.4f, .4f, .9f));
        var isolated = new ZoneManagedStaticGeometry(); isolated.AddInstance(ceiling, Matrix4x4.Identity);
        await Assert.That(isolated.GetUpwardUnprojections(query, Vector3.Zero, envelope).Count).IsEqualTo(0);
        var multipart = new ZoneManagedStaticGeometry();
        multipart.AddInstance(ceiling, Matrix4x4.Identity, new ZonePhysicsQueryBounds(new(-5, -5, -1), new(5, 5, 1.2f)));
        var result = multipart.GetUpwardUnprojections(query, Vector3.Zero, envelope).Single();
        await Near(result.Depth, 2.5f); await Near(result.NormalFallbackDepth, .1f);
        await Near(result.NormalFallbackDirection, -Vector3.UnitZ);
        await Assert.That(result.PhysicalMaterialId).IsEqualTo((ushort)18);
    }

    [Test]
    public async Task TerrainExitsUseSurfaceIntersectionsAndSnapshotsSurviveUnload()
    {
        var heights = new ZoneTerrainHeightField(); heights.LoadCell(7, 7, TerrainHeightFieldTests.CreateCell(0, 0));
        var terrain = new ZoneManagedTerrainGeometry(heights);
        var query = ZonePhysicsShape.Capsule(.1f, .2f);
        var center = new Vector3(8000.4f, 8000.3f, -.05f);
        var results = terrain.GetUpwardUnprojections(query, center);
        await Assert.That(results.Count).IsGreaterThan(0);
        await Near(results[0].Depth, .35f);
        await Assert.That(results[0].NormalFallbackDepth).IsGreaterThan(0f);
        await Assert.That(terrain.GetUpwardUnprojections(query, new(center.X, center.Y, -10)).Count).IsEqualTo(0);
        heights.UnloadCell(7, 7);
        await Assert.That(terrain.GetUpwardUnprojections(query, center).Count).IsEqualTo(0);
        await Near(results[0].Depth, .35f);
        await Assert.That(() => { ((IList<ZonePhysicsDirectedUnprojection>)results)[0] = default; }).Throws<NotSupportedException>();
        foreach (var shape in Queries)
            await Assert.That(Plane(0).GetUpwardUnprojections(shape, new(-3, -2, shape.BoundingHalfExtents.Z)).Count).IsEqualTo(0);
    }

    private static readonly ZonePhysicsShape[] Queries =
    [ZonePhysicsShape.Box(new(.25f)), ZonePhysicsShape.Capsule(.25f, .5f), ZonePhysicsShape.Cylinder(.25f, .5f)];
    private static ZoneManagedStaticGeometry Plane(float slope)
    {
        var scene = new ZoneManagedStaticGeometry();
        scene.AddInstance(new ZoneManagedTriangleMesh([new(-20, -20, -20 * slope), new(20, -20, 20 * slope), new(-20, 20, -20 * slope), new(20, 20, 20 * slope)],
            [0, 1, 2, 1, 3, 2], [17, 17]), Matrix4x4.Identity);
        return scene;
    }
    private static async Task VerifyExit(ZoneManagedStaticGeometry scene, ZonePhysicsShape query, Vector3 center, ZonePhysicsDirectedUnprojection result)
    {
        await Assert.That(scene.GetPenetrations(query, center + Vector3.UnitZ * (result.Depth + .001f)).Count).IsEqualTo(0);
        await Assert.That(scene.GetPenetrations(query, center + Vector3.UnitZ * (result.Depth - .003f)).Count).IsGreaterThan(0);
    }
    private static async Task Near(float actual, float expected, float tolerance = .000002f) => await Assert.That(MathF.Abs(actual - expected)).IsLessThan(tolerance);
    private static async Task Near(Vector3 actual, Vector3 expected, float tolerance = .000002f) => await Assert.That(Vector3.Distance(actual, expected)).IsLessThan(tolerance);
}
