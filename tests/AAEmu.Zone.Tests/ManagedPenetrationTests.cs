using System.Numerics;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Tests;

public sealed class ManagedPenetrationTests
{
    [Test]
    public async Task PlaneDepthIsRealUnprojectionAndContainsNoControllerGap()
    {
        var scene = Floor();
        foreach (var (shape, expected) in new[] { (ZonePhysicsShape.Box(new(.5f)), .3f), (ZonePhysicsShape.Capsule(.25f, .5f), .55f), (ZonePhysicsShape.Cylinder(.25f, .5f), .3f) })
        {
            var contacts = scene.GetPenetrations(shape, new(-3, -2, .2f));
            await Assert.That(contacts.Count).IsGreaterThan(0);
            await Near(contacts[0].Depth, expected);
            await Near(contacts[0].Normal, Vector3.UnitZ);
            await Near(contacts[0].Position.Z, 0);
            await Assert.That(contacts[0].IsSurfaceContact).IsTrue();
            await Assert.That(contacts[0].PhysicalMaterialId).IsEqualTo((ushort)17);
            var moved = new Vector3(-3, -2, .2f) + contacts[0].Normal * contacts[0].Depth;
            await Assert.That(scene.GetPenetrations(shape, moved).All(contact => contact.Depth <= ZoneManagedStaticGeometry.PenetrationTolerance)).IsTrue();
        }
    }

    [Test]
    public async Task InternalCoplanarFloorEdgesDoNotProduceOpposingRecoveryWalls()
    {
        var scene = Floor();
        foreach (var (shape, expected) in new[] { (ZonePhysicsShape.Box(new(.5f)), .5f), (ZonePhysicsShape.Capsule(.25f, .5f), .75f), (ZonePhysicsShape.Cylinder(.25f, .5f), .5f) })
        {
            var first = scene.GetPenetrations(shape, Vector3.Zero);
            var second = scene.GetPenetrations(shape, Vector3.Zero);
            await Assert.That(first.Count).IsGreaterThan(0);
            await Near(first[0].Depth, expected);
            await Near(first[0].Normal, Vector3.UnitZ);
            await Assert.That(first[0].IsAmbiguous).IsTrue();
            await Assert.That(first[0].Normal).IsEqualTo(second[0].Normal);
        }
    }

    [Test]
    public async Task CapsuleOuterCornerDepthMatchesRoundedDistance()
    {
        var capsule = ZonePhysicsShape.Capsule(.5f, .5f);
        var box = ZonePhysicsShape.Box(Vector3.One);
        await Assert.That(ZoneManagedShapeQueries.TryPenetration(capsule, new(1.3f, 1.3f, 0), box, Vector3.Zero, out var contact)).IsTrue();
        await Near(contact.Depth, .5f - MathF.Sqrt(.18f));
        await Near(contact.Normal, Vector3.Normalize(new(1, 1, 0)));
        await Near(contact.Position, new(1, 1, 0));
        await Assert.That(contact.IsSurfaceContact).IsFalse();
        await Assert.That(contact.IsAmbiguous).IsFalse();
    }

    [Test]
    public async Task EntirelyContainedCapsuleUsesSolidVolumeDepth()
    {
        var capsule = ZonePhysicsShape.Capsule(.5f, 1);
        var box = ZonePhysicsShape.Box(new(2));
        await Assert.That(ZoneManagedShapeQueries.TryPenetration(capsule, Vector3.Zero, box, Vector3.Zero, out var contact)).IsTrue();
        await Near(contact.Depth, 2.5f);
        await Assert.That(contact.IsAmbiguous).IsTrue();
        await Assert.That(MathF.Abs(contact.Normal.Z)).IsLessThan(.001f);
        await Assert.That(ZoneManagedShapeQueries.TryPenetration(capsule, contact.Normal * (contact.Depth + .0002f), box, Vector3.Zero, out _)).IsFalse();
    }

    [Test]
    public async Task EveryRegisteredActorShapePairHasASeparatingCopiedTranslation()
    {
        var shapes = new[] { ZonePhysicsShape.Box(new(.5f, .6f, .7f)), ZonePhysicsShape.Capsule(.4f, .5f), ZonePhysicsShape.Cylinder(.4f, .5f) };
        foreach (var query in shapes)
            foreach (var target in shapes)
            {
                var center = new Vector3(.2f, .1f, .15f);
                await Assert.That(ZoneManagedShapeQueries.TryPenetration(query, center, target, Vector3.Zero, out var contact)).IsTrue();
                await Assert.That(float.IsFinite(contact.Depth) && contact.Depth > 0).IsTrue();
                await Near(contact.Normal.Length(), 1);
                var moved = center + contact.Normal * (contact.Depth + .0002f);
                await Assert.That(ZoneManagedShapeQueries.TryPenetration(query, moved, target, Vector3.Zero, out var residual) && residual.Depth > ZoneManagedStaticGeometry.PenetrationTolerance).IsFalse();
                var shortMove = center + contact.Normal * (contact.Depth - .002f);
                await Assert.That(ZoneManagedShapeQueries.TryPenetration(query, shortMove, target, Vector3.Zero, out _)).IsTrue();
            }
    }

    [Test]
    public async Task BoxCylinderTiesRetainAnOutwardAxialDirectionBelowTheTarget()
    {
        var box = ZonePhysicsShape.Box(new(.5f));
        var cylinder = ZonePhysicsShape.Cylinder(.5f, .5f);
        foreach (var (query, target) in new[] { (box, cylinder), (cylinder, box) })
            foreach (var z in new[] { -.5f, .5f })
            {
                var center = new Vector3(.5f, 0, z);
                await Assert.That(ZoneManagedShapeQueries.TryPenetration(query, center, target, Vector3.Zero, out var contact)).IsTrue();
                await Near(contact.Depth, .5f);
                await Assert.That(contact.IsAmbiguous).IsTrue();
                var moved = center + contact.Normal * (contact.Depth + .0002f);
                await Assert.That(ZoneManagedShapeQueries.TryPenetration(query, moved, target, Vector3.Zero, out _)).IsFalse();
            }
    }

    [Test]
    public async Task AnalyticSphereDepthMatchesIndependentInsideAndOutsideCases()
    {
        var scene = Sphere(Matrix4x4.Identity);
        var query = ZonePhysicsShape.Capsule(.5f, 0);
        var contacts = scene.GetPenetrations(query, new(1.2f, 0, 0));
        await Assert.That(contacts.Count).IsEqualTo(1);
        await Near(contacts[0].Depth, .3f); await Near(contacts[0].Normal, Vector3.UnitX);
        await Near(contacts[0].Position, Vector3.UnitX);
        var centered = scene.GetPenetrations(query, Vector3.Zero)[0];
        await Near(centered.Depth, 1.5f); await Assert.That(centered.IsAmbiguous).IsTrue();
        await Assert.That(scene.GetPenetrations(query, new(1.5002f, 0, 0)).Count).IsEqualTo(0);
    }

    [Test]
    public async Task AffineEllipsoidUsesActualConvexDepthAtCoordinateEightThousand()
    {
        var transform = Matrix4x4.CreateScale(2, 1, 3) * Matrix4x4.CreateRotationZ(.4f) * Matrix4x4.CreateTranslation(8000, 8000, 0);
        var scene = Sphere(transform);
        var contact = scene.GetPenetrations(ZonePhysicsShape.Capsule(.25f, 0), new(8000, 8000, 0))[0];
        await Near(contact.Depth, 1.25f, .0004f);
        var minor = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, Matrix4x4.CreateRotationZ(.4f)));
        await Assert.That(MathF.Abs(Vector3.Dot(contact.Normal, minor))).IsGreaterThan(.999f);
        await Assert.That(contact.IsAmbiguous).IsTrue();
        await Assert.That(contact.IsSurfaceContact).IsFalse();
    }

    [Test]
    public async Task ShearedPrimitiveDepthMatchesItsIndependentSmallestSingularRadius()
    {
        var shear = Matrix4x4.Identity; shear.M21 = .4f;
        var scene = Sphere(shear * Matrix4x4.CreateTranslation(1000, 2000, 3000));
        var contact = scene.GetPenetrations(ZonePhysicsShape.Capsule(.2f, 0), new(1000, 2000, 3000))[0];
        var smallest = (MathF.Sqrt(4 + .4f * .4f) - .4f) * .5f;
        await Near(contact.Depth, smallest + .2f, .0004f);
        await Near(contact.Normal.Length(), 1);
    }

    [Test]
    public async Task SolidConvexMeshContainmentDiffersFromIdenticalSurfaceInteriors()
    {
        foreach (var shape in new[] { ZonePhysicsShape.Box(new(.2f)), ZonePhysicsShape.Capsule(.2f, .3f), ZonePhysicsShape.Cylinder(.2f, .3f) })
        {
            var solid = new ZoneManagedStaticGeometry(); var surface = new ZoneManagedStaticGeometry();
            solid.AddInstance(Cube(true), Matrix4x4.Identity); surface.AddInstance(Cube(false), Matrix4x4.Identity);
            var contacts = solid.GetPenetrations(shape, Vector3.Zero);
            await Assert.That(contacts.Count).IsEqualTo(1);
            await Near(contacts[0].Depth, 1.2f, .0004f);
            await Assert.That(contacts[0].IsSurfaceContact).IsFalse();
            await Assert.That(contacts[0].PhysicalMaterialId).IsEqualTo((ushort)29);
            await Assert.That(surface.GetPenetrations(shape, Vector3.Zero).Count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task TerrainDepthUsesTrueLowerShapeAndHasNoInternalSidewalls()
    {
        var heights = new ZoneTerrainHeightField(); heights.LoadCell(7, 7, TerrainHeightFieldTests.CreateCell(0, 0));
        var terrain = new ZoneManagedTerrainGeometry(heights);
        foreach (var (shape, expected) in new[] { (ZonePhysicsShape.Box(new(.5f)), .3f), (ZonePhysicsShape.Capsule(.25f, .5f), .55f), (ZonePhysicsShape.Cylinder(.25f, .5f), .3f) })
        {
            var contact = terrain.GetPenetrations(shape, new(8000, 8000, .2f))[0];
            await Near(contact.Depth, expected); await Near(contact.Normal, Vector3.UnitZ);
            await Assert.That(contact.IsSurfaceContact).IsTrue();
            // Early overlap recovery uses the authored surface; entirely submerged bodies
            // have no contact with it. Ground support is a separate living-controller phase.
            await Assert.That(terrain.GetPenetrations(shape, new(8000, 8000, -2)).Count).IsEqualTo(0);
        }
        heights.UnloadCell(7, 7);
        await Assert.That(terrain.GetPenetrations(ZonePhysicsShape.Capsule(.5f, .5f), new(8000, 8000, -2)).Count).IsEqualTo(0);
    }

    [Test]
    public async Task SlopingTerrainCapsuleDepthUsesAnalyticHemisphereExtremum()
    {
        var heights = new ZoneTerrainHeightField();
        heights.LoadCell(1, 1, TerrainHeightFieldTests.CreateCell(0, .03125f, sample: (x, _, sx, _) => (ushort)((x / 2 + sx) * 32)));
        var terrain = new ZoneManagedTerrainGeometry(heights);
        var contact = terrain.GetUpwardUnprojections(ZonePhysicsShape.Capsule(.5f, .5f), new(1100, 1100, 38.5f)).MaxBy(contact => contact.Depth);
        await Near(contact.Depth, .5f * MathF.Sqrt(1.25f), .0002f);
        await Near(contact.Position.X, 1100 + .25f / MathF.Sqrt(1.25f), .0002f);
        await Near(contact.Direction, Vector3.UnitZ);
    }

    [Test]
    public async Task MultipleSurfaceContactsRemainCopiedAfterSceneUnload()
    {
        var scene = new ZoneManagedStaticGeometry();
        scene.AddInstance(new ZoneManagedTriangleMesh([new(0, -10, -10), new(0, 10, -10), new(0, 0, 10)], [0, 1, 2], [11]), Matrix4x4.Identity);
        scene.AddInstance(new ZoneManagedTriangleMesh([new(-10, 0, -10), new(10, 0, -10), new(0, 0, 10)], [0, 1, 2], [12]), Matrix4x4.Identity);
        var contacts = scene.GetPenetrations(ZonePhysicsShape.Box(new(.5f)), new(.2f, .3f, 0));
        await Assert.That(contacts.Count).IsEqualTo(2);
        await Near(contacts[0].Depth, .3f); await Near(contacts[0].Normal, Vector3.UnitX);
        await Near(contacts[1].Depth, .2f); await Near(contacts[1].Normal, Vector3.UnitY);
        scene.Clear();
        await Assert.That(contacts[0].PhysicalMaterialId).IsEqualTo((ushort)11);
        await Assert.That(() => { ((IList<ZonePhysicsPenetration>)contacts)[0] = default; }).Throws<NotSupportedException>();
    }

    [Test]
    public async Task RoundedOuterSurfaceCornerRetainsItsActualMinimumDirection()
    {
        var scene = new ZoneManagedStaticGeometry();
        scene.AddInstance(new ZoneManagedTriangleMesh([Vector3.Zero, new(2, 0, 0), new(0, 2, 0)], [0, 1, 2], [23]), Matrix4x4.Identity);
        var contact = scene.GetPenetrations(ZonePhysicsShape.Capsule(1, .5f), new(-.5f, -.5f, 0))[0];
        await Near(contact.Depth, 1 - MathF.Sqrt(.5f));
        await Near(contact.Normal, Vector3.Normalize(new(-1, -1, 0)));
        await Near(contact.Position, Vector3.Zero);
    }

    [Test]
    public async Task TerrainBoundaryCapsuleUsesClippedHemisphereRatherThanBoxDepth()
    {
        var heights = new ZoneTerrainHeightField(); heights.LoadCell(1, 1, TerrainHeightFieldTests.CreateCell(0, 0));
        var terrain = new ZoneManagedTerrainGeometry(heights);
        var capsule = ZonePhysicsShape.Capsule(1, .5f);
        var edge = terrain.GetUpwardUnprojections(capsule, new(1023.5f, 1100, -.25f))[0];
        await Near(edge.Depth, .75f + MathF.Sqrt(.75f));
        await Near(edge.Position.X, 1024);
        var corner = terrain.GetUpwardUnprojections(capsule, new(1023.5f, 1023.5f, -.25f))[0];
        await Near(corner.Depth, .75f + MathF.Sqrt(.5f));
        await Assert.That(terrain.GetUpwardUnprojections(capsule, new(1023, 1100, -.25f)).Count).IsEqualTo(0);
    }

    [Test]
    public async Task NonBoxConvexVolumeDepthUsesItsActualSlopingFaceAndMaterial()
    {
        var mesh = new ZoneManagedTriangleMesh([Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ],
            [0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3], [11, 12, 13, 14], solidConvex: true);
        var scene = new ZoneManagedStaticGeometry(); scene.AddInstance(mesh, Matrix4x4.Identity);
        var contact = scene.GetPenetrations(ZonePhysicsShape.Capsule(.1f, 0), new(.3f))[0];
        await Near(contact.Depth, .1f + .1f / MathF.Sqrt(3), .0003f);
        await Near(contact.Normal, Vector3.Normalize(Vector3.One), .001f);
        await Assert.That(contact.PhysicalMaterialId).IsEqualTo((ushort)14);
        await Assert.That(contact.IsSurfaceContact).IsFalse();
    }

    [Test]
    public async Task EveryQueryKindSeparatesFromShearedMirroredAnalyticTargets()
    {
        var shear = Matrix4x4.Identity; shear.M21 = .3f;
        var transform = Matrix4x4.CreateScale(-1.5f, .75f, 2) * shear * Matrix4x4.CreateRotationX(.25f) *
            Matrix4x4.CreateRotationZ(.4f) * Matrix4x4.CreateTranslation(20, 30, 40);
        foreach (var kind in Enum.GetValues<ZoneManagedPrimitiveKind>())
            foreach (var shape in new[] { ZonePhysicsShape.Box(new(.2f, .3f, .4f)), ZonePhysicsShape.Capsule(.25f, .4f), ZonePhysicsShape.Cylinder(.25f, .4f) })
            {
                var scene = new ZoneManagedStaticGeometry();
                scene.AddInstance(new ZoneManagedPrimitiveGeometry(kind, Vector3.Zero,
                    kind == ZoneManagedPrimitiveKind.Sphere ? Vector3.Zero : Vector3.UnitZ, 1, kind == ZoneManagedPrimitiveKind.Sphere ? 0 : 1, 37), transform);
                var center = new Vector3(20.2f, 30.1f, 40.1f);
                var contact = scene.GetPenetrations(shape, center)[0];
                await Assert.That(contact.Depth).IsGreaterThan(0f);
                await Near(contact.Normal.Length(), 1);
                var moved = center + contact.Normal * (contact.Depth + .0005f);
                await Assert.That(scene.GetPenetrations(shape, moved).All(value => value.Depth <= ZoneManagedStaticGeometry.PenetrationTolerance)).IsTrue();
            }
    }

    [Test]
    public async Task MirroredNonuniformConvexMeshHasRealTouchWitnessAtCoordinateEightThousand()
    {
        var rotation = Matrix4x4.CreateRotationZ(.4f);
        var transform = Matrix4x4.CreateScale(-2, .5f, 3) * rotation * Matrix4x4.CreateTranslation(8000, 8000, 0);
        var minor = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, rotation));
        foreach (var shape in new[] { ZonePhysicsShape.Box(new(.2f)), ZonePhysicsShape.Capsule(.2f, .3f), ZonePhysicsShape.Cylinder(.2f, .3f) })
        {
            var scene = new ZoneManagedStaticGeometry(); scene.AddInstance(Cube(true), transform);
            var contact = scene.GetPenetrations(shape, new(8000, 8000, 0))[0];
            var expected = shape.Kind == ZonePhysicsShapeKind.Box ? .5f + .2f * (MathF.Abs(minor.X) + MathF.Abs(minor.Y)) : .7f;
            await Near(contact.Depth, expected, .0004f);
            await Assert.That(MathF.Abs(Vector3.Dot(contact.Normal, minor))).IsGreaterThan(.999f);
            // A support vertex at z=3 would not be a contact with this small query at z=0.
            await Assert.That(MathF.Abs(contact.Position.Z)).IsLessThan(.6f);
            await Assert.That(contact.PhysicalMaterialId).IsEqualTo((ushort)29);
        }
    }

    [Test]
    public async Task CoplanarSlopingSeamMembershipProofDoesNotQuantizeItsTouchPointEarly()
    {
        var scene = new ZoneManagedStaticGeometry();
        scene.AddInstance(new ZoneManagedTriangleMesh([new(-20, -20, -10), new(20, -20, 10), new(-20, 20, -10), new(20, 20, 10)],
            [0, 1, 2, 1, 3, 2]), Matrix4x4.CreateTranslation(8000, 8000, 300));
        var contacts = scene.GetPenetrations(ZonePhysicsShape.Capsule(.5f, .7f), new(8000, 8000, 300));
        await Assert.That(contacts.Count).IsEqualTo(1);
        await Near(contacts[0].Depth, .5f + .7f / MathF.Sqrt(1.25f), .0002f);
        await Near(contacts[0].Normal, Vector3.Normalize(new(-.5f, 0, 1)));
        await Assert.That(contacts[0].IsAmbiguous).IsTrue();
    }

    private static ZoneManagedStaticGeometry Floor()
    {
        var scene = new ZoneManagedStaticGeometry();
        scene.AddInstance(new ZoneManagedTriangleMesh([new(-20, -20, 0), new(20, -20, 0), new(-20, 20, 0), new(20, 20, 0)], [0, 1, 2, 1, 3, 2], [17, 17]), Matrix4x4.Identity);
        return scene;
    }
    private static ZoneManagedStaticGeometry Sphere(Matrix4x4 transform)
    {
        var scene = new ZoneManagedStaticGeometry();
        scene.AddInstance(new ZoneManagedPrimitiveGeometry(ZoneManagedPrimitiveKind.Sphere, Vector3.Zero, Vector3.Zero, 1, 0, 37), transform);
        return scene;
    }
    private static ZoneManagedTriangleMesh Cube(bool solid) => new(
        [new(-1, -1, -1), new(1, -1, -1), new(-1, 1, -1), new(1, 1, -1), new(-1, -1, 1), new(1, -1, 1), new(-1, 1, 1), new(1, 1, 1)],
        [0, 1, 2, 1, 3, 2, 4, 5, 6, 5, 7, 6, 0, 4, 1, 1, 4, 5, 2, 3, 6, 3, 7, 6, 0, 2, 4, 2, 6, 4, 1, 5, 3, 3, 5, 7],
        Enumerable.Repeat((ushort)29, 12).ToArray(), solid);
    private static async Task Near(float actual, float expected, float tolerance = .0002f) => await Assert.That(MathF.Abs(actual - expected)).IsLessThan(tolerance);
    private static async Task Near(Vector3 actual, Vector3 expected, float tolerance = .0002f) => await Assert.That(Vector3.Distance(actual, expected)).IsLessThan(tolerance);
}
