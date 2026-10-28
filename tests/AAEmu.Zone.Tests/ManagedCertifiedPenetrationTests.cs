using System.Numerics;
using AAEmu.Zone.Physics;

namespace AAEmu.Zone.Tests;

public sealed class ManagedCertifiedPenetrationTests
{
    [Test]
    public async Task NearIsotropicCenteredShapesAttainTheIndependentInscribedRadiusBound()
    {
        foreach (var scale in Scales)
            foreach (var query in Queries)
            {
                var scene = Scene(scale);
                var contact = scene.GetPenetrations(query, Vector3.Zero).Single();
                // Every unit-direction target support is >=1 and query support is >=.2.
                // The +/-X supports attain1+.2, proving this exact depth independently of EPA.
                await Near(contact.Depth, 1.2f);
                await Near(contact.Normal, Vector3.UnitX);
                await Near(contact.Position, Vector3.UnitX);
                await Assert.That(contact.PhysicalMaterialId).IsEqualTo((ushort)37);
                await Assert.That(contact.IsAmbiguous).IsTrue();
                await Assert.That(contact.IsSurfaceContact).IsFalse();
                await VerifyTranslation(scene, query, Vector3.Zero, contact);
            }
    }

    [Test]
    public async Task AlignedInsideOffsetsHaveSignedExactDepthWithoutARecoveryGap()
    {
        foreach (var scale in Scales)
            foreach (var query in Queries)
                foreach (var side in new[] { -1, 1 })
                {
                    var scene = Scene(scale);
                    var center = new Vector3(side * .001f, 0, 0);
                    var contact = scene.GetPenetrations(query, center).Single();
                    await Near(contact.Depth, 1.199f);
                    await Near(contact.Normal, Vector3.UnitX * side);
                    await Near(contact.Position, Vector3.UnitX * side);
                    await Assert.That(contact.IsAmbiguous).IsFalse();
                    await VerifyTranslation(scene, query, center, contact);
                    await Assert.That(scene.GetPenetrations(query, new(side * 1.2f, 0, 0)).Count).IsEqualTo(0);
                    await Assert.That(scene.GetPenetrations(query, new(side * 1.201f, 0, 0)).Count).IsEqualTo(0);
                }
    }

    [Test]
    public async Task MirroredDiagonalTargetKeepsTheCertificateAtCoordinateEightThousand()
    {
        var primitive = new ZoneManagedPrimitiveGeometry(ZoneManagedPrimitiveKind.Sphere, new(.5f, .25f, .75f), Vector3.Zero, 1, 0, 91);
        var transform = Matrix4x4.CreateScale(-1, 1.01f, 1.02f) * Matrix4x4.CreateTranslation(8000, 8000, 8000);
        var targetCenter = Vector3.Transform(primitive.Center, transform);
        foreach (var query in Queries)
            foreach (var side in new[] { -1, 1 })
            {
                var scene = new ZoneManagedStaticGeometry(); scene.AddInstance(primitive, transform);
                var center = targetCenter + Vector3.UnitX * (side * .001f);
                var contact = scene.GetPenetrations(query, center).Single();
                await Near(contact.Depth, 1.2f - MathF.Abs(center.X - targetCenter.X));
                await Near(contact.Normal, Vector3.UnitX * side);
                await Near(contact.Position, targetCenter + Vector3.UnitX * side);
                await Assert.That(contact.PhysicalMaterialId).IsEqualTo((ushort)91);
                await VerifyTranslation(scene, query, center, contact);
            }
    }

    [Test]
    public async Task AxialCertificatesRequireTheActualQuerySupportToAttainTheBound()
    {
        var scene = Scene(new(1.01f, 1.02f, 1));
        foreach (var query in new[] { ZonePhysicsShape.Capsule(.2f, 0), ZonePhysicsShape.Cylinder(.3f, .2f), ZonePhysicsShape.Box(new(.3f, .4f, .2f)) })
        {
            var contact = scene.GetPenetrations(query, Vector3.Zero).Single();
            await Near(contact.Depth, 1.2f); await Near(contact.Normal, Vector3.UnitZ);
            await Near(contact.Position, Vector3.UnitZ);
            await VerifyTranslation(scene, query, Vector3.Zero, contact);
        }
        // A nonzero capsule segment cannot attain its inscribed radius along Z. Its actual
        // support has another .3m, so the global1.2m lower bound alone is not a valid answer.
        var otherScene = Scene(new(2, 3, 1));
        var capsule = ZonePhysicsShape.Capsule(.2f, .3f);
        var otherContact = otherScene.GetPenetrations(capsule, Vector3.Zero).Single();
        await Near(otherContact.Depth, 1.5f, .0002f);
        await Assert.That(MathF.Abs(otherContact.Normal.Z)).IsGreaterThan(.999f);
        await VerifyTranslation(otherScene, capsule, Vector3.Zero, otherContact);
    }

    private static readonly Vector3[] Scales = [new(1, 1.0001f, 1.0002f), new(1, 1.001f, 1.002f), new(1, 1.01f, 1.02f)];
    private static readonly ZonePhysicsShape[] Queries =
    [ZonePhysicsShape.Capsule(.2f, 0), ZonePhysicsShape.Capsule(.2f, .3f), ZonePhysicsShape.Cylinder(.2f, .3f)];

    private static ZoneManagedStaticGeometry Scene(Vector3 scale)
    {
        var scene = new ZoneManagedStaticGeometry();
        scene.AddInstance(new ZoneManagedPrimitiveGeometry(ZoneManagedPrimitiveKind.Sphere, Vector3.Zero, Vector3.Zero, 1, 0, 37), Matrix4x4.CreateScale(scale));
        return scene;
    }

    private static async Task VerifyTranslation(ZoneManagedStaticGeometry scene, ZonePhysicsShape query, Vector3 center, ZonePhysicsPenetration contact)
    {
        await Assert.That(scene.GetPenetrations(query, center + contact.Normal * (contact.Depth + .001f)).Count).IsEqualTo(0);
        var residual = scene.GetPenetrations(query, center + contact.Normal * (contact.Depth - .002f));
        await Assert.That(residual.Count).IsGreaterThan(0);
        await Assert.That(residual[0].Depth).IsGreaterThan(.001f);
    }

    private static async Task Near(float actual, float expected, float tolerance = .000001f) =>
        await Assert.That(MathF.Abs(actual - expected)).IsLessThan(tolerance);
    private static async Task Near(Vector3 actual, Vector3 expected, float tolerance = .000001f) =>
        await Assert.That(Vector3.Distance(actual, expected)).IsLessThan(tolerance);
}
