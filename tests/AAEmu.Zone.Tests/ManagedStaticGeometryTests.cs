using System.Numerics;

using AAEmu.Zone.Physics;

namespace AAEmu.Zone.Tests;

public sealed class ManagedStaticGeometryTests
{
    [Test]
    public async Task SharedMeshCopiesItsInputsAndReturnsCopiedTriangles()
    {
        Vector3[] vertices = [new(0, 0, 0), new(4, 0, 0), new(0, 4, 0)];
        int[] indices = [0, 1, 2];
        ushort[] materials = [17];
        var mesh = new ZoneManagedTriangleMesh(vertices, indices, materials);
        vertices[0] = new(1000);
        indices[0] = 999;
        materials[0] = 99;
        mesh.GetTriangle(0, out var a, out _, out _, out var material);
        await Assert.That(a).IsEqualTo(Vector3.Zero);
        await Assert.That(material).IsEqualTo((ushort)17);
        var geometry = new ZoneManagedStaticGeometry();
        geometry.AddInstance(mesh, Matrix4x4.Identity);
        geometry.AddInstance(mesh, Matrix4x4.CreateTranslation(10, 0, 0));
        await Assert.That(geometry.Raycast(new(1, 1, 3), new(0, 0, -6), out var hit)).IsTrue();
        await Assert.That(hit.Distance).IsEqualTo(3f);
        await Assert.That(hit.PhysicalMaterialId).IsEqualTo((ushort)17);
        await Assert.That(geometry.Raycast(new(11, 1, 3), new(0, 0, -6), out hit)).IsTrue();
        await Assert.That(hit.PhysicalMaterialId).IsEqualTo((ushort)17);
    }

    [Test]
    public async Task AffineInstancesPreserveWorldDistancesNormalsAndMaterialsFromBothSides()
    {
        Vector3[] vertices = [new(-2, -2, 0), new(2, -2, 2), new(-2, 2, 0)];
        var mesh = new ZoneManagedTriangleMesh(vertices, [0, 1, 2], [42]);
        var shear = Matrix4x4.Identity;
        shear.M21 = .35f;
        shear.M31 = .2f;
        var rotation = Matrix4x4.CreateRotationX(.4f) * Matrix4x4.CreateRotationZ(.7f);
        Matrix4x4[] transforms =
        [
            Matrix4x4.Identity,
            Matrix4x4.CreateTranslation(20, 30, 10),
            Matrix4x4.CreateScale(3) * rotation * Matrix4x4.CreateTranslation(20, 30, 10),
            Matrix4x4.CreateScale(2, .5f, 3) * rotation * Matrix4x4.CreateTranslation(20, 30, 10),
            Matrix4x4.CreateScale(-2, .5f, 3) * rotation * Matrix4x4.CreateTranslation(20, 30, 10),
            shear * rotation * Matrix4x4.CreateTranslation(20, 30, 10),
        ];
        foreach (var transform in transforms)
        {
            var geometry = new ZoneManagedStaticGeometry();
            geometry.AddInstance(mesh, transform);
            var a = Vector3.Transform(vertices[0], transform);
            var b = Vector3.Transform(vertices[1], transform);
            var c = Vector3.Transform(vertices[2], transform);
            var center = (a + b + c) / 3;
            var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            await Assert.That(geometry.Raycast(center + normal * 5, -normal * 10, out var hit)).IsTrue();
            await Assert.That(MathF.Abs(hit.Distance - 5)).IsLessThan(.001f);
            await Assert.That(Vector3.Distance(hit.Normal, normal)).IsLessThan(.0001f);
            await Assert.That(Vector3.Distance(hit.Position, center)).IsLessThan(.001f);
            await Assert.That(hit.PhysicalMaterialId).IsEqualTo((ushort)42);
            await Assert.That(geometry.Raycast(center - normal * 5, normal * 10, out hit)).IsTrue();
            await Assert.That(Vector3.Distance(hit.Normal, normal)).IsLessThan(.0001f);
        }
    }

    [Test]
    public async Task NearestTriangleAndInstanceWinAndRaySegmentsStayBounded()
    {
        var mesh = new ZoneManagedTriangleMesh(
            [new(0, 0, 0), new(4, 0, 0), new(0, 4, 0), new(0, 0, 2), new(4, 0, 2), new(0, 4, 2)],
            [0, 1, 2, 3, 4, 5], [5, 9]);
        var geometry = new ZoneManagedStaticGeometry();
        geometry.AddInstance(mesh, Matrix4x4.Identity);
        var high = geometry.AddInstance(mesh, Matrix4x4.CreateTranslation(0, 0, 10));
        await Assert.That(geometry.Raycast(new(1, 1, 20), new(0, 0, -30), out var hit)).IsTrue();
        await Assert.That(hit.Position.Z).IsEqualTo(12f);
        await Assert.That(hit.PhysicalMaterialId).IsEqualTo((ushort)9);
        await Assert.That(geometry.Raycast(new(1, 1, 20), new(0, 0, -7), out _)).IsFalse();
        geometry.RemoveInstance(high);
        await Assert.That(geometry.TryGround(new(1, 1, 20), 30, out var ground)).IsTrue();
        await Assert.That(ground.Position.Z).IsEqualTo(2f);
        await Assert.That(ground.Normal).IsEqualTo(Vector3.UnitZ);
        await Assert.That(geometry.Raycast(new(1, 1, 20), Vector3.Zero, out _)).IsFalse();
    }

    [Test]
    public async Task ClosedMeshesHaveEmptyInteriorsAndDoNotFillTheSpaceBelowThem()
    {
        var geometry = new ZoneManagedStaticGeometry();
        geometry.AddInstance(Cube(), Matrix4x4.Identity);
        var shape = Box(.2f);
        await Assert.That(geometry.TryCollide(shape, new(0, 0, 2))).IsFalse();
        await Assert.That(geometry.TryCollide(shape, new(0, 0, -2))).IsFalse();
        await Assert.That(geometry.TryCollide(shape, new(0, 0, .1f))).IsTrue();
        await Assert.That(geometry.TryCollide(shape, new(1.9f, 0, 2))).IsTrue();
        await Assert.That(geometry.TryGround(new(0, 0, 2), 3, out var floor)).IsTrue();
        await Assert.That(floor.Position.Z).IsEqualTo(0f);
        await Assert.That(geometry.Raycast(new(0, 0, 2), new(0, 0, 5), out var ceiling)).IsTrue();
        await Assert.That(ceiling.Position.Z).IsEqualTo(4f);
        await Assert.That(ceiling.Normal).IsEqualTo(Vector3.UnitZ); // Raw rays retain authored winding on backfaces.
    }

    [Test]
    public async Task ExplicitSolidProxyContainsEmbeddedQueriesWhileIdenticalSurfaceMeshStaysEmpty()
    {
        var surface = Cube();
        var solid = Cube(solidConvex: true);
        var surfaceScene = new ZoneManagedStaticGeometry();
        var solidScene = new ZoneManagedStaticGeometry();
        surfaceScene.AddInstance(surface, Matrix4x4.Identity);
        solidScene.AddInstance(solid, Matrix4x4.Identity);
        await Assert.That(surface.SolidConvex).IsFalse();
        await Assert.That(solid.SolidConvex).IsTrue();
        await Assert.That(surfaceScene.TryCollide(Box(.1f), new(0, 0, 2))).IsFalse();
        await Assert.That(solidScene.TryCollide(Box(.1f), new(0, 0, 2))).IsTrue();
        await Assert.That(solidScene.LastQueryStatistics.TriangleTests).IsEqualTo(0);
        await Assert.That(solidScene.TryCollide(Box(.1f), new(0, 0, -1))).IsFalse();
        // Authored proxies may duplicate positions for each face instead of sharing indices.
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        for (var triangle = 0; triangle < solid.TriangleCount; triangle++)
        {
            solid.GetTriangle(triangle, out var a, out var b, out var c, out _);
            indices.AddRange([vertices.Count, vertices.Count + 1, vertices.Count + 2]);
            vertices.AddRange([a, b, c]);
        }
        var splitFaces = new ZoneManagedTriangleMesh(vertices.ToArray(), indices.ToArray(), solidConvex: true);
        var splitScene = new ZoneManagedStaticGeometry();
        splitScene.AddInstance(splitFaces, Matrix4x4.Identity);
        await Assert.That(splitScene.TryCollide(Box(.1f), new(0, 0, 2))).IsTrue();
    }

    [Test]
    public async Task SolidContainmentSurvivesAffineRotationNonuniformAndMirroredInstances()
    {
        var rotation = Matrix4x4.CreateRotationX(.6f) * Matrix4x4.CreateRotationZ(.3f);
        var shear = Matrix4x4.Identity;
        shear.M21 = .35f;
        foreach (var local in new[] { Matrix4x4.Identity, Matrix4x4.CreateScale(2, .5f, 3), Matrix4x4.CreateScale(-2, .5f, 3), shear })
        {
            var transform = local * rotation * Matrix4x4.CreateTranslation(20, 30, 10);
            var solidScene = new ZoneManagedStaticGeometry();
            var surfaceScene = new ZoneManagedStaticGeometry();
            solidScene.AddInstance(Cube(solidConvex: true), transform);
            surfaceScene.AddInstance(Cube(), transform);
            var center = Vector3.Transform(new(0, 0, 2), transform);
            var outside = Vector3.Transform(new(3, 0, 2), transform);
            var face = Vector3.Transform(new(2, 0, 2), transform);
            await Assert.That(solidScene.TryCollide(Box(.02f), center)).IsTrue();
            await Assert.That(surfaceScene.TryCollide(Box(.02f), center)).IsFalse();
            await Assert.That(solidScene.TryCollide(Box(.02f), outside)).IsFalse();
            await Assert.That(solidScene.TryCollide(Box(.02f), face)).IsTrue();
            await Assert.That(surfaceScene.TryCollide(Box(.02f), face)).IsTrue();
        }
    }

    [Test]
    public async Task SolidHalfspacesRejectEmptyCornersOfAConvexResourcesBounds()
    {
        var tetrahedron = new ZoneManagedTriangleMesh(
            [Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ],
            [0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3], solidConvex: true);
        var geometry = new ZoneManagedStaticGeometry();
        geometry.AddInstance(tetrahedron, Matrix4x4.Identity);
        await Assert.That(geometry.TryCollide(Box(.01f), new(.2f, .2f, .2f))).IsTrue();
        await Assert.That(geometry.TryCollide(Box(.01f), new(.8f, .8f, .8f))).IsFalse();
    }

    [Test]
    public async Task BoxSweepsHitBothSidesContinuouslyWithCeilingNormals()
    {
        var geometry = new ZoneManagedStaticGeometry();
        geometry.AddInstance(Quad(20, 0, 37), Matrix4x4.Identity);
        var shape = new ZonePhysicsShape(new(.5f, .5f, 1), false);
        await Assert.That(geometry.TrySweep(shape, new(0, 0, 8), new(0, 0, -20), out var hit)).IsTrue();
        await Assert.That(MathF.Abs(hit.Distance - 7)).IsLessThan(.0001f);
        await Assert.That(hit.Normal).IsEqualTo(Vector3.UnitZ);
        await Assert.That(MathF.Abs(hit.Position.Z)).IsLessThan(.0001f);
        await Assert.That(hit.PhysicalMaterialId).IsEqualTo((ushort)37);
        await Assert.That(geometry.TrySweep(shape, new(0, 0, -8), new(0, 0, 20), out hit)).IsTrue();
        await Assert.That(MathF.Abs(hit.Distance - 7)).IsLessThan(.0001f);
        await Assert.That(hit.Normal).IsEqualTo(-Vector3.UnitZ);
        await Assert.That(geometry.TrySweep(shape, new(0, 0, -8), new(0, 0, 20), out _, minimumNormalZ: 0)).IsFalse();
        await Assert.That(geometry.TrySweep(shape, new(0, 0, -8), new(0, 0, 20), out _, maximumNormalZ: -.7f)).IsTrue();
    }

    [Test]
    public async Task TangentialTravelAcrossFloorTrianglesAndMovementAwayStayClear()
    {
        var geometry = new ZoneManagedStaticGeometry();
        geometry.AddInstance(Quad(40), Matrix4x4.Identity);
        var shape = new ZonePhysicsShape(new(.5f, .5f, 1), true);
        await Assert.That(geometry.TryCollide(shape, new(0, 0, 1))).IsTrue();
        await Assert.That(geometry.TrySweep(shape, new(0, 0, 1), new(20, 10, 0), out _)).IsFalse();
        await Assert.That(geometry.TrySweep(shape, new(0, 0, 1), new(0, 0, 10), out _)).IsFalse();
        await Assert.That(geometry.TrySweep(shape, new(0, 0, 1), Vector3.Zero, out _)).IsFalse();
        await Assert.That(geometry.CapsulesUseBoundingBoxes).IsTrue();
    }

    [Test]
    public async Task RotatedAndMirroredWallsUseWorldSpaceShapeExtents()
    {
        foreach (var scale in new[] { new Vector3(2, .5f, 3), new Vector3(-2, .5f, 3) })
        {
            var geometry = new ZoneManagedStaticGeometry();
            geometry.AddInstance(Quad(20, 0, 37), Matrix4x4.CreateScale(scale) *
                Matrix4x4.CreateRotationY(MathF.PI / 2) * Matrix4x4.CreateTranslation(5, 0, 0));
            var shape = Box(1);
            await Assert.That(geometry.TrySweep(shape, Vector3.Zero, new(10, 0, 0), out var hit)).IsTrue();
            await Assert.That(MathF.Abs(hit.Distance - 4)).IsLessThan(.001f);
            await Assert.That(Vector3.Distance(hit.Normal, -Vector3.UnitX)).IsLessThan(.0001f);
            await Assert.That(MathF.Abs(hit.Position.X - 5)).IsLessThan(.001f);
            await Assert.That(geometry.TryCollide(shape, new(5, 0, 0))).IsTrue();
            await Assert.That(geometry.TryCollide(shape, new(2, 0, 0))).IsFalse();
        }
    }

    [Test]
    public async Task TriangleSatRejectsTheEmptyCornerOfTransformedMeshBounds()
    {
        var mesh = new ZoneManagedTriangleMesh([new(0, 0, 0), new(4, 0, 0), new(0, 4, 0)], [0, 1, 2]);
        var transform = Matrix4x4.CreateScale(2, .5f, 3) * Matrix4x4.CreateRotationZ(.4f) * Matrix4x4.CreateTranslation(20, 30, 10);
        var geometry = new ZoneManagedStaticGeometry();
        geometry.AddInstance(mesh, transform);
        await Assert.That(geometry.TryCollide(Box(.05f), Vector3.Transform(new(1, 1, 0), transform))).IsTrue();
        await Assert.That(geometry.TryCollide(Box(.05f), Vector3.Transform(new(2.5f, 2.5f, 0), transform))).IsFalse();
        await Assert.That(geometry.LastQueryStatistics.TriangleTests).IsEqualTo(1);
    }

    [Test]
    public async Task MeshVertexAndDiagonalBoundaryContactsRemainBounded()
    {
        var geometry = new ZoneManagedStaticGeometry();
        geometry.AddInstance(new ZoneManagedTriangleMesh([new(0, 0, 0), new(4, 0, 0), new(0, 4, 0)], [0, 1, 2]), Matrix4x4.Identity);
        await Assert.That(geometry.Raycast(new(4, 0, 3), new(0, 0, -6), out _)).IsTrue();
        await Assert.That(geometry.Raycast(new(2, 2, 3), new(0, 0, -6), out _)).IsTrue();
        await Assert.That(geometry.Raycast(new(2.001f, 2.001f, 3), new(0, 0, -6), out _)).IsFalse();
        var shape = Box(.5f);
        await Assert.That(geometry.TrySweep(shape, new(4.5f, 0, 2), new(0, 0, -4), out var vertexHit)).IsTrue();
        await Assert.That(MathF.Abs(vertexHit.Distance - 1.5f)).IsLessThan(.0001f);
        await Assert.That(geometry.TrySweep(shape, new(4.51f, 0, 2), new(0, 0, -4), out _)).IsFalse();
        await Assert.That(geometry.TrySweep(shape, new(2.5f, 2.5f, 2), new(0, 0, -4), out _)).IsTrue();
        await Assert.That(geometry.TrySweep(shape, new(2.51f, 2.51f, 2), new(0, 0, -4), out _)).IsFalse();
    }

    [Test]
    public async Task NonuniformMirroredAndShearedInstancesKeepExactBoxContacts()
    {
        Vector3[] vertices = [new(-2, -2, 0), new(2, -2, 2), new(-2, 2, 0)];
        var mesh = new ZoneManagedTriangleMesh(vertices, [0, 1, 2]);
        var shear = Matrix4x4.Identity;
        shear.M21 = .35f;
        shear.M31 = .2f;
        var rotation = Matrix4x4.CreateRotationX(.4f) * Matrix4x4.CreateRotationZ(.7f);
        foreach (var local in new[] { Matrix4x4.CreateScale(2, .5f, 3), Matrix4x4.CreateScale(-2, .5f, 3), shear })
        {
            var transform = local * rotation * Matrix4x4.CreateTranslation(20, 30, 10);
            var a = Vector3.Transform(vertices[0], transform);
            var b = Vector3.Transform(vertices[1], transform);
            var c = Vector3.Transform(vertices[2], transform);
            var point = (a + b + c) / 3;
            var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            var shape = new ZonePhysicsShape(new(.1f, .12f, .08f), false);
            var radius = Vector3.Dot(Vector3.Abs(normal), shape.HalfExtents);
            var geometry = new ZoneManagedStaticGeometry();
            geometry.AddInstance(mesh, transform);
            await Assert.That(geometry.TrySweep(shape, point + normal * (radius + 2), -normal * 4, out var hit)).IsTrue();
            await Assert.That(MathF.Abs(hit.Distance - 2)).IsLessThan(.001f);
            await Assert.That(Vector3.Distance(hit.Normal, normal)).IsLessThan(.0001f);
            await Assert.That(geometry.TrySweep(shape, point - normal * (radius + 2), normal * 4, out hit)).IsTrue();
            await Assert.That(MathF.Abs(hit.Distance - 2)).IsLessThan(.001f);
            await Assert.That(Vector3.Distance(hit.Normal, -normal)).IsLessThan(.0001f);
            await Assert.That(geometry.TryCollide(shape, point)).IsTrue();
            await Assert.That(geometry.TryCollide(shape, point + normal * (radius + .1f))).IsFalse();
            await Assert.That(geometry.TryCollide(shape, point - normal * (radius + .1f))).IsFalse();
        }
    }

    [Test]
    public async Task NormalFilteringSkipsNearFloorsAndKeepsFartherWalls()
    {
        var geometry = new ZoneManagedStaticGeometry();
        geometry.AddInstance(Quad(100, 0, 4), Matrix4x4.Identity);
        geometry.AddInstance(Quad(100, 0, 9), Matrix4x4.CreateRotationY(MathF.PI / 2) * Matrix4x4.CreateTranslation(5, 0, 0));
        var shape = Box(.5f);
        var displacement = new Vector3(10, 0, -2);
        await Assert.That(geometry.TrySweep(shape, new(0, 0, 1), displacement, out var hit)).IsTrue();
        await Assert.That(hit.PhysicalMaterialId).IsEqualTo((ushort)4);
        await Assert.That(hit.Normal).IsEqualTo(Vector3.UnitZ);
        await Assert.That(geometry.TrySweep(shape, new(0, 0, 1), displacement, out hit, maximumNormalZ: .6f)).IsTrue();
        await Assert.That(hit.PhysicalMaterialId).IsEqualTo((ushort)9);
        await Assert.That(Vector3.Distance(hit.Normal, -Vector3.UnitX)).IsLessThan(.0001f);
        await Assert.That(MathF.Abs(hit.Distance - displacement.Length() * .45f)).IsLessThan(.001f);
        var downward = new ZoneManagedStaticGeometry();
        downward.AddInstance(new ZoneManagedTriangleMesh([new(-10, -10, 0), new(10, -10, 0), new(0, 10, 0)], [0, 2, 1]), Matrix4x4.Identity);
        await Assert.That(downward.Raycast(new(0, 0, 2), new(0, 0, -4), out var underside)).IsTrue();
        await Assert.That(underside.Normal).IsEqualTo(-Vector3.UnitZ);
        await Assert.That(downward.TryGround(new(0, 0, 2), 4, out _)).IsFalse();
    }

    [Test]
    public async Task PerMeshBvhPrunesTriangleTestsForRaysSweepsAndOverlaps()
    {
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        for (var x = 0; x < 32; x++)
            for (var y = 0; y < 32; y++)
            {
                var index = vertices.Count;
                vertices.Add(new(x * 10, y * 10, 0));
                vertices.Add(new(x * 10 + 1, y * 10, 0));
                vertices.Add(new(x * 10, y * 10 + 1, 0));
                indices.AddRange([index, index + 1, index + 2]);
            }
        var mesh = new ZoneManagedTriangleMesh(vertices.ToArray(), indices.ToArray());
        var geometry = new ZoneManagedStaticGeometry();
        geometry.AddInstance(mesh, Matrix4x4.Identity);
        await Assert.That(mesh.BvhNodeCount).IsGreaterThan(1);
        await Assert.That(geometry.Raycast(new(.25f, .25f, 5), new(0, 0, -10), out _)).IsTrue();
        await Assert.That(geometry.LastQueryStatistics.TriangleTests).IsEqualTo(1);
        await Assert.That(geometry.TrySweep(Box(.1f), new(.25f, .25f, 5), new(0, 0, -10), out _)).IsTrue();
        await Assert.That(geometry.LastQueryStatistics.TriangleTests).IsEqualTo(1);
        await Assert.That(geometry.TryCollide(Box(.1f), new(.25f, .25f, 0))).IsTrue();
        await Assert.That(geometry.LastQueryStatistics.TriangleTests).IsEqualTo(1);
    }

    [Test]
    public async Task SceneBucketsPruneThousandsOfInstancesSharingOneResource()
    {
        var mesh = new ZoneManagedTriangleMesh([new(0, 0, 0), new(2, 0, 0), new(0, 2, 0)], [0, 1, 2], [12]);
        var geometry = new ZoneManagedStaticGeometry();
        var selected = default(ZoneManagedStaticInstanceHandle);
        for (var x = 0; x < 50; x++)
            for (var y = 0; y < 50; y++)
            {
                var handle = geometry.AddInstance(mesh, Matrix4x4.CreateTranslation(x * 128, y * 128, 0));
                if (x == 25 && y == 25) selected = handle;
            }
        await Assert.That(geometry.InstanceCount).IsEqualTo(2500);
        await Assert.That(geometry.Raycast(new(3200.5f, 3200.5f, 10), new(0, 0, -20), out var hit)).IsTrue();
        await Assert.That(hit.PhysicalMaterialId).IsEqualTo((ushort)12);
        await Assert.That(geometry.LastQueryStatistics.InstanceBoundsTests).IsLessThan(5);
        await Assert.That(geometry.LastQueryStatistics.CandidateInstances).IsEqualTo(1);
        await Assert.That(geometry.LastQueryStatistics.TriangleTests).IsEqualTo(1);
        geometry.RemoveInstance(selected);
        await Assert.That(geometry.Raycast(new(3200.5f, 3200.5f, 10), new(0, 0, -20), out _)).IsFalse();
        await Assert.That(geometry.Raycast(new(3072.5f, 3200.5f, 10), new(0, 0, -20), out _)).IsTrue();
    }

    [Test]
    public async Task UnloadRetiresHandlesWithoutInvalidatingCopiedHitsOrSharedMeshes()
    {
        var geometry = new ZoneManagedStaticGeometry();
        var mesh = Quad(10);
        var first = geometry.AddInstance(mesh, Matrix4x4.Identity);
        await Assert.That(geometry.Raycast(new(0, 0, 1), new(0, 0, -2), out var hit)).IsTrue();
        await Assert.That(geometry.RemoveInstance(first)).IsTrue();
        await Assert.That(geometry.RemoveInstance(first)).IsFalse();
        await Assert.That(geometry.Contains(first)).IsFalse();
        await Assert.That(geometry.Raycast(new(0, 0, 1), new(0, 0, -2), out _)).IsFalse();
        await Assert.That(hit.Position.Z).IsEqualTo(0f);
        var second = geometry.AddInstance(mesh, Matrix4x4.Identity);
        geometry.Clear();
        var third = geometry.AddInstance(mesh, Matrix4x4.Identity);
        await Assert.That(second.Value).IsGreaterThan(first.Value);
        await Assert.That(third.Value).IsGreaterThan(second.Value);
        await Assert.That(geometry.RemoveInstance(second)).IsFalse();
        await Assert.That(geometry.InstanceCount).IsEqualTo(1);
    }

    [Test]
    public async Task OversizedInstancesAndLargeQueriesKeepCorrectHitsThroughFallbackBounds()
    {
        var geometry = new ZoneManagedStaticGeometry();
        var handle = geometry.AddInstance(Quad(1), Matrix4x4.CreateScale(10000));
        await Assert.That(geometry.Raycast(new(-2000, -2000, 5), new(0, 0, -10), out _)).IsTrue();
        await Assert.That(geometry.Raycast(new(-10000, -10000, 10), new(20000, 20000, -20), out var hit)).IsTrue();
        await Assert.That(Vector3.Distance(hit.Position, Vector3.Zero)).IsLessThan(.001f);
        await Assert.That(geometry.LastQueryStatistics.TriangleTests).IsLessThan(3);
        geometry.RemoveInstance(handle);
        await Assert.That(geometry.TryCollide(Box(.5f), Vector3.Zero)).IsFalse();
    }

    [Test]
    public async Task InvalidResourcesTransformsAndQueriesAreRejected()
    {
        await Assert.That(() => new ZoneManagedTriangleMesh([], [])).Throws<ArgumentException>();
        await Assert.That(() => new ZoneManagedTriangleMesh([Vector3.Zero], [0, 1, 0])).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new ZoneManagedTriangleMesh([new(float.NaN, 0, 0)], [0, 0, 0])).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new ZoneManagedTriangleMesh([Vector3.Zero], [0, 0, 0], [1, 2])).Throws<ArgumentException>();
        await Assert.That(() => new ZoneManagedTriangleMesh([Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [0, 1, 2], solidConvex: true)).Throws<ArgumentException>();
        var geometry = new ZoneManagedStaticGeometry();
        var mesh = Quad(1);
        await Assert.That(() => geometry.AddInstance(mesh, Matrix4x4.CreateScale(0))).Throws<ArgumentException>();
        var projective = Matrix4x4.Identity;
        projective.M14 = .1f;
        await Assert.That(() => geometry.AddInstance(mesh, projective)).Throws<ArgumentException>();
        await Assert.That(() => geometry.Raycast(new(float.PositiveInfinity, 0, 0), Vector3.UnitX, out _)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => geometry.TrySweep(Box(0), Vector3.Zero, Vector3.UnitX, out _)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => geometry.Raycast(Vector3.Zero, Vector3.UnitX, out _, minimumNormalZ: .8f, maximumNormalZ: .2f)).Throws<ArgumentOutOfRangeException>();
    }

    private static ZonePhysicsShape Box(float halfExtent) => new(new(halfExtent), false);

    private static ZoneManagedTriangleMesh Quad(float halfSize, float height = 0, ushort material = 0) => new(
        [new(-halfSize, -halfSize, height), new(halfSize, -halfSize, height), new(-halfSize, halfSize, height), new(halfSize, halfSize, height)],
        [0, 1, 2, 1, 3, 2], [material, material]);

    private static ZoneManagedTriangleMesh Cube(bool solidConvex = false) => new(
        [new(-2, -2, 0), new(2, -2, 0), new(-2, 2, 0), new(2, 2, 0), new(-2, -2, 4), new(2, -2, 4), new(-2, 2, 4), new(2, 2, 4)],
        [0, 1, 2, 1, 3, 2, 4, 5, 6, 5, 7, 6, 0, 4, 1, 1, 4, 5, 2, 3, 6, 3, 7, 6, 0, 2, 4, 2, 6, 4, 1, 5, 3, 3, 5, 7], solidConvex: solidConvex);
}
