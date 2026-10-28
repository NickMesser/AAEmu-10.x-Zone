using System.Numerics;
using AAEmu.Zone.Physics;

namespace AAEmu.Zone.Tests;

public sealed class ManagedPrimitiveGeometryTests
{
    [Test]
    public async Task SphereRaysCoverOutsideInsideTangentMissAndLongSegments()
    {
        var scene = Scene(ZoneManagedPrimitiveKind.Sphere);
        await Assert.That(scene.Raycast(new(-3, 0, 0), new(6, 0, 0), out var hit)).IsTrue();
        await Near(hit.Distance, 2); await Near(hit.Normal, -Vector3.UnitX);
        await Assert.That(scene.Raycast(Vector3.Zero, new(2, 0, 0), out hit)).IsTrue();
        await Near(hit.Distance, 1); await Near(hit.Normal, Vector3.UnitX);
        await Assert.That(scene.Raycast(new(-3, 1, 0), new(6, 0, 0), out hit)).IsTrue();
        await Near(hit.Distance, 3); await Near(hit.Normal, Vector3.UnitY);
        await Assert.That(scene.Raycast(new(-3, 1.01f, 0), new(6, 0, 0), out _)).IsFalse();
        await Assert.That(scene.Raycast(new(-3, 0, 0), new(1.9f, 0, 0), out _)).IsFalse();
        await Assert.That(scene.Raycast(new(-1000000, 0, 0), new(2000000, 0, 0), out hit)).IsTrue();
        await Near(hit.Position, -Vector3.UnitX, .01f);
        await Assert.That(hit.PhysicalMaterialId).IsEqualTo((ushort)37);
    }

    [Test]
    public async Task CylinderRaysCoverSideCapsParallelTangentAndRim()
    {
        var scene = Scene(ZoneManagedPrimitiveKind.Cylinder);
        await Assert.That(scene.Raycast(new(3, 0, 0), new(-6, 0, 0), out var hit)).IsTrue();
        await Near(hit.Distance, 2); await Near(hit.Normal, Vector3.UnitX);
        await Assert.That(scene.Raycast(new(0, 0, 5), new(0, 0, -10), out hit)).IsTrue();
        await Near(hit.Distance, 3); await Near(hit.Normal, Vector3.UnitZ);
        await Assert.That(scene.Raycast(Vector3.Zero, new(0, 0, 4), out hit)).IsTrue();
        await Near(hit.Distance, 2); await Near(hit.Normal, Vector3.UnitZ);
        await Assert.That(scene.Raycast(new(2, 0, 5), new(0, 0, -10), out _)).IsFalse();
        await Assert.That(scene.Raycast(new(-3, 1, 0), new(6, 0, 0), out hit)).IsTrue();
        await Near(hit.Normal, Vector3.UnitY);
        await Assert.That(scene.Raycast(new(3, 0, 4), new(-4, 0, -4), out hit)).IsTrue();
        await Near(hit.Position, new(1, 0, 2)); await Near(hit.Normal.Length(), 1);
    }

    [Test]
    public async Task CapsuleRaysExcludeInternalHemisphereSurfaces()
    {
        var scene = Scene(ZoneManagedPrimitiveKind.Capsule);
        await Assert.That(scene.Raycast(new(0, 0, 5), new(0, 0, -10), out var hit)).IsTrue();
        await Near(hit.Distance, 2); await Near(hit.Normal, Vector3.UnitZ);
        await Assert.That(scene.Raycast(Vector3.Zero, new(0, 0, 5), out hit)).IsTrue();
        await Near(hit.Distance, 3); await Near(hit.Position.Z, 3);
        await Assert.That(scene.Raycast(new(3, 0, 2.5f), new(-6, 0, 0), out hit)).IsTrue();
        await Near(hit.Position, new(MathF.Sqrt(.75f), 0, 2.5f));
        await Near(hit.Normal, new(MathF.Sqrt(.75f), 0, .5f));
        await Assert.That(scene.Raycast(new(3, 0, 0), new(-6, 0, 0), out hit)).IsTrue();
        await Near(hit.Distance, 2);
        var zeroSegment = new ZoneManagedStaticGeometry();
        zeroSegment.AddInstance(new ZoneManagedPrimitiveGeometry(ZoneManagedPrimitiveKind.Capsule, Vector3.Zero, Vector3.UnitZ, 1, 0, 9), Matrix4x4.Identity);
        await Assert.That(zeroSegment.Raycast(new(0, 0, 3), new(0, 0, -6), out hit)).IsTrue();
        await Near(hit.Position.Z, 1);
    }

    [Test]
    public async Task AffineRaysUseAnalyticSurfacesAndOutwardInverseTransposeNormals()
    {
        var shear = Matrix4x4.Identity; shear.M21 = .4f; shear.M31 = .2f;
        foreach (var kind in Enum.GetValues<ZoneManagedPrimitiveKind>())
            foreach (var local in new[] { Matrix4x4.CreateScale(2, .5f, 3), Matrix4x4.CreateScale(-2, .5f, 3), shear })
            {
                var transform = local * Matrix4x4.CreateRotationY(.3f) * Matrix4x4.CreateRotationZ(.6f) * Matrix4x4.CreateTranslation(1000, 2000, 3000);
                var scene = Scene(kind, transform);
                var origin = Vector3.Transform(new(-3, 0, 0), transform);
                var displacement = Vector3.TransformNormal(new(6, 0, 0), transform);
                Matrix4x4.Invert(transform, out var inverse);
                var expectedNormal = Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitX, Matrix4x4.Transpose(inverse)));
                await Assert.That(scene.Raycast(origin, displacement, out var hit)).IsTrue();
                await Near(hit.Position, Vector3.Transform(-Vector3.UnitX, transform), .002f);
                await Near(hit.Distance, displacement.Length() / 3, .003f);
                await Near(hit.Normal, expectedNormal, .001f);
            }
    }

    [Test]
    public async Task SolidOverlapUsesCurvedSurfacesInsteadOfTheirBoundingBoxes()
    {
        foreach (var kind in Enum.GetValues<ZoneManagedPrimitiveKind>())
        {
            var scene = Scene(kind);
            await Assert.That(scene.TryCollide(Box(.1f), Vector3.Zero)).IsTrue();
            await Assert.That(scene.TryCollide(Box(.1f), new(.9f, .9f, 0))).IsFalse();
            await Assert.That(scene.TryCollide(Box(.1f), new(.7f, .7f, 0))).IsTrue();
        }
        var capsule = Scene(ZoneManagedPrimitiveKind.Capsule);
        await Assert.That(capsule.TryCollide(Box(.1f), new(.9f, .9f, 2.5f))).IsFalse();
        await Assert.That(capsule.TryCollide(Box(.1f), new(0, 0, 2.9f))).IsTrue();
        var cylinder = Scene(ZoneManagedPrimitiveKind.Cylinder);
        await Assert.That(cylinder.TryCollide(Box(.1f), new(1.05f, 0, 2.05f))).IsTrue();
        await Assert.That(cylinder.TryCollide(Box(.1f), new(0, 0, 2.2f))).IsFalse();
    }

    [Test]
    public async Task KnownSphereAndCylinderAndCapsuleSweepDistancesMatchIndependentGeometry()
    {
        foreach (var kind in Enum.GetValues<ZoneManagedPrimitiveKind>())
        {
            var scene = Scene(kind);
            await Assert.That(scene.TrySweep(Box(.2f), new(-5, 0, 0), new(10, 0, 0), out var hit)).IsTrue();
            await Near(hit.Distance, 3.8f, .003f); await Near(hit.Normal, -Vector3.UnitX, .01f);
            await Assert.That(hit.PhysicalMaterialId).IsEqualTo((ushort)37);
            var bottom = kind == ZoneManagedPrimitiveKind.Sphere ? -1 : kind == ZoneManagedPrimitiveKind.Cylinder ? -2 : -3;
            await Assert.That(scene.TrySweep(Box(.2f), new(0, 0, -5), new(0, 0, 10), out hit)).IsTrue();
            await Near(hit.Distance, 5 + bottom - .2f, .003f); await Near(hit.Normal, -Vector3.UnitZ, .01f);
            await Assert.That(scene.TrySweep(Box(.2f), new(-5, 2, 0), new(10, 0, 0), out _)).IsFalse();
        }
    }

    [Test]
    public async Task FastSweepsAndZoneLocalCoordinatesRemainWithinWorldTolerance()
    {
        foreach (var coordinate in new[] { 0, 1000, 8000 })
        {
            var transform = Matrix4x4.CreateTranslation(coordinate, coordinate, coordinate);
            var scene = Scene(ZoneManagedPrimitiveKind.Sphere, transform);
            await Assert.That(scene.TrySweep(Box(.2f), new(coordinate - 20000, coordinate, coordinate), new(40000, 0, 0), out var hit)).IsTrue();
            await Near(hit.Distance, 19998.8f, .005f);
            await Near(hit.Normal, -Vector3.UnitX, .01f);
            await Assert.That(scene.TryCollide(Box(.1f), new(coordinate + .9f, coordinate + .9f, coordinate))).IsFalse();
        }
    }

    [Test]
    public async Task TangentSweepsContactWithinSeparationToleranceAndClearNearMisses()
    {
        var scene = Scene(ZoneManagedPrimitiveKind.Sphere);
        var shape = new ZonePhysicsShape(new(.2f, .3f, .4f), true);
        await Assert.That(scene.TrySweep(shape, new(-5, 1.3f, 0), new(10, 0, 0), out var hit)).IsTrue();
        await Assert.That(hit.Distance).IsGreaterThan(4.7f);
        await Assert.That(hit.Distance).IsLessThan(4.81f);
        await Assert.That(Vector3.Dot(hit.Normal, Vector3.UnitX)).IsLessThanOrEqualTo(0f);
        await Assert.That(scene.TrySweep(shape, new(-5, 1.31f, 0), new(10, 0, 0), out _)).IsFalse();
    }

    [Test]
    public async Task NonuniformMirroredAndShearedSolidsUseAffineSupportMaps()
    {
        var shear = Matrix4x4.Identity; shear.M21 = .4f;
        foreach (var kind in Enum.GetValues<ZoneManagedPrimitiveKind>())
            foreach (var local in new[] { Matrix4x4.CreateScale(2, .5f, 3), Matrix4x4.CreateScale(-2, .5f, 3), shear })
            {
                var transform = local * Matrix4x4.CreateRotationZ(.4f) * Matrix4x4.CreateTranslation(1000, 2000, 3000);
                var scene = Scene(kind, transform);
                var center = Vector3.Transform(Vector3.Zero, transform);
                await Assert.That(scene.TryCollide(Box(.05f), center)).IsTrue();
                await Assert.That(scene.TryCollide(Box(.02f), Vector3.Transform(new(.9f, .9f, 0), transform))).IsFalse();
                var outward = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, local * Matrix4x4.CreateRotationZ(.4f)));
                await Assert.That(scene.TrySweep(Box(.05f), center - outward * 10, outward * 20, out var hit)).IsTrue();
                await Assert.That(hit.Distance).IsGreaterThan(7f);
                await Assert.That(hit.Distance).IsLessThan(10f);
            }
    }

    [Test]
    public async Task FilteringAndRemovalComposeAnalyticAndMeshInstances()
    {
        var scene = Scene(ZoneManagedPrimitiveKind.Sphere);
        var cylinder = scene.AddInstance(Primitive(ZoneManagedPrimitiveKind.Cylinder), Matrix4x4.CreateTranslation(5, 0, 0));
        await Assert.That(scene.Raycast(new(-3, 0, 0), new(12, 0, 0), out var hit)).IsTrue();
        await Near(hit.Distance, 2);
        await Assert.That(scene.TrySweep(Box(.2f), new(-3, 0, 0), new(12, 0, 0), out hit, minimumNormalZ: .8f)).IsFalse();
        scene.RemoveInstance(cylinder);
        await Assert.That(scene.InstanceCount).IsEqualTo(1);
        scene.Clear();
        await Assert.That(scene.TryCollide(Box(.1f), Vector3.Zero)).IsFalse();
        foreach (var kind in Enum.GetValues<ZoneManagedPrimitiveKind>())
        {
            var filtered = Scene(kind);
            await Assert.That(filtered.Raycast(new(0, 0, 5), new(0, 0, -10), out var lower, maximumNormalZ: -.7f)).IsTrue();
            await Near(lower.Normal, -Vector3.UnitZ);
            await Near(lower.Position.Z, kind == ZoneManagedPrimitiveKind.Sphere ? -1 : kind == ZoneManagedPrimitiveKind.Cylinder ? -2 : -3);
        }
    }

    [Test]
    public async Task AuthoredCentersAndArbitraryAxesPreserveCapAndRadialDistances()
    {
        var axis = Vector3.Normalize(new Vector3(1, 1, 1));
        var radial = Vector3.Normalize(Vector3.Cross(axis, Vector3.UnitZ));
        var center = new Vector3(3, 4, 5);
        foreach (var kind in new[] { ZoneManagedPrimitiveKind.Cylinder, ZoneManagedPrimitiveKind.Capsule })
        {
            var scene = new ZoneManagedStaticGeometry();
            scene.AddInstance(new ZoneManagedPrimitiveGeometry(kind, center, axis, 1, 2, 19), Matrix4x4.Identity);
            await Assert.That(scene.Raycast(center + axis * 6, -axis * 12, out var axial)).IsTrue();
            await Near(axial.Distance, kind == ZoneManagedPrimitiveKind.Cylinder ? 4 : 3, .001f);
            await Near(axial.Normal, axis, .001f);
            await Assert.That(scene.Raycast(center + radial * 4, -radial * 8, out var side)).IsTrue();
            await Near(side.Distance, 3, .001f); await Near(side.Normal, radial, .001f);
        }
    }

    [Test]
    public async Task AnalyticFloorFilteringKeepsAFartherTriangleWall()
    {
        var scene = new ZoneManagedStaticGeometry();
        scene.AddInstance(new ZoneManagedPrimitiveGeometry(ZoneManagedPrimitiveKind.Cylinder,
            new(0, 0, -.1f), Vector3.UnitZ, 100, .1f, 5), Matrix4x4.Identity);
        scene.AddInstance(new ZoneManagedTriangleMesh([new(5, -10, -10), new(5, 10, -10), new(5, 0, 10)], [0, 1, 2], [9]), Matrix4x4.Identity);
        await Assert.That(scene.TrySweep(Box(.5f), new(0, 0, 1), new(10, 0, -2), out var floor)).IsTrue();
        await Assert.That(floor.PhysicalMaterialId).IsEqualTo((ushort)5);
        await Assert.That(scene.TrySweep(Box(.5f), new(0, 0, 1), new(10, 0, -2), out var wall, maximumNormalZ: .6f)).IsTrue();
        await Assert.That(wall.PhysicalMaterialId).IsEqualTo((ushort)9);
        await Near(wall.Normal, -Vector3.UnitX);
    }

    [Test]
    public async Task InvalidDescriptorsAreRejectedAndZeroLengthQueriesMiss()
    {
        await Assert.That(() => new ZoneManagedPrimitiveGeometry(ZoneManagedPrimitiveKind.Sphere, Vector3.Zero, Vector3.Zero, 0, 0, 0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new ZoneManagedPrimitiveGeometry(ZoneManagedPrimitiveKind.Cylinder, Vector3.Zero, Vector3.Zero, 1, 2, 0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new ZoneManagedPrimitiveGeometry(ZoneManagedPrimitiveKind.Capsule, Vector3.Zero, Vector3.UnitZ, 1, -1, 0)).Throws<ArgumentOutOfRangeException>();
        var scene = Scene(ZoneManagedPrimitiveKind.Sphere);
        await Assert.That(scene.Raycast(Vector3.Zero, Vector3.Zero, out _)).IsFalse();
        await Assert.That(scene.TrySweep(Box(.1f), Vector3.Zero, Vector3.Zero, out _)).IsFalse();
    }

    private static ZoneManagedPrimitiveGeometry Primitive(ZoneManagedPrimitiveKind kind) => new(kind, Vector3.Zero,
        kind == ZoneManagedPrimitiveKind.Sphere ? Vector3.Zero : Vector3.UnitZ, 1, kind == ZoneManagedPrimitiveKind.Sphere ? 0 : 2, 37);
    private static ZoneManagedStaticGeometry Scene(ZoneManagedPrimitiveKind kind, Matrix4x4? transform = null)
    {
        var scene = new ZoneManagedStaticGeometry(); scene.AddInstance(Primitive(kind), transform ?? Matrix4x4.Identity); return scene;
    }
    private static ZonePhysicsShape Box(float half) => new(new(half), false);
    private static async Task Near(float actual, float expected, float tolerance = .0001f)
        => await Assert.That(MathF.Abs(actual - expected)).IsLessThan(tolerance);
    private static async Task Near(Vector3 actual, Vector3 expected, float tolerance = .0001f)
        => await Assert.That(Vector3.Distance(actual, expected)).IsLessThan(tolerance);
}
