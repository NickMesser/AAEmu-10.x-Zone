using System.Numerics;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Tests;

public sealed class ManagedTerrainNormalRecoveryTests
{
    [Test]
    public async Task OriginalSlopeInteriorRecoversAlongItsAuthoredNormal()
    {
        Vector3 a = new(510, 510, 306.75372314453125f), b = new(512, 510, 305.2325134277344f), c = new(510, 512, 306.939453125f);
        var query = ZonePhysicsShape.Capsule(.08f, .16f);
        var center = new Vector3(510.66666f, 510.66666f, 306.10855f + .4f);
        await Assert.That(ZoneManagedTerrainNormalPenetration.Try(query, center, a, b, c, 115, out var contact)).IsTrue();
        await Near(contact.Normal, new(.6037414f, -.07371297f, .7937649f), .000001f);
        await Near(contact.Depth, .04825f, .00005f);
        var correction = contact.Normal * (contact.Depth + .01f);
        await Near(correction.X, .03518f, .00005f); await Near(correction.Y, -.00431f, .00002f);
        await Assert.That(contact.Normal.Z).IsGreaterThan(0f);
        await Assert.That(contact.PhysicalMaterialId).IsEqualTo((ushort)115);
        await Assert.That(contact.IsSurfaceContact).IsTrue();
    }

    [Test]
    public async Task BelowPlaneRecoveryNeverUsesTheShorterBacksideExit()
    {
        Vector3 a = new(510, 510, 306.75372314453125f), b = new(512, 510, 305.2325134277344f), c = new(510, 512, 306.939453125f);
        var query = ZonePhysicsShape.Capsule(.08f, .16f);
        var center = new Vector3(510.66666f, 510.66666f, 305.80856f + .4f);
        await Assert.That(ZoneManagedTerrainNormalPenetration.Try(query, center, a, b, c, 115, out var contact)).IsTrue();
        await Near(contact.Depth, .28638f, .00006f);
        await Assert.That(contact.Normal.Z).IsGreaterThan(.79f);
        await Assert.That(contact.Depth).IsGreaterThan(2.5f * query.Radius);
        // The independently measured original ignored this over-bound front constraint. Returning
        // a nearest downward .12763m exit here would admit an entirely different recovery.
        await Assert.That(contact.Depth).IsGreaterThan(.12763f);
    }

    [Test]
    public async Task CoplanarGridSeamsSupplyFaceConstraintsForEveryShapeKind()
    {
        var heights = new ZoneTerrainHeightField();
        heights.LoadCell(1, 1, TerrainHeightFieldTests.CreateCell(0, .03125f,
            sample: (x, _, sx, _) => (ushort)((x / 2 + sx) * 32)));
        var terrain = new ZoneManagedTerrainGeometry(heights);
        var normal = Vector3.Normalize(new(-.5f, 0, 1));
        foreach (var shape in new[] { ZonePhysicsShape.Box(new(.3f, .4f, .5f)), ZonePhysicsShape.Capsule(.25f, .5f), ZonePhysicsShape.Cylinder(.25f, .5f) })
        {
            var center = new Vector3(1100, 1100, 38.2f); // Exactly on both authored grid seams.
            var contacts = terrain.GetPenetrations(shape, center);
            await Assert.That(contacts.Count).IsGreaterThan(0);
            var support = shape.Kind == ZonePhysicsShapeKind.Box
                ? .3f * MathF.Abs(normal.X) + .5f * normal.Z
                : shape.UseCapsule ? .25f + .5f * normal.Z : .25f * MathF.Abs(normal.X) + .5f * normal.Z;
            var expected = support - (center.Z - 38) * normal.Z;
            foreach (var contact in contacts)
            {
                await Near(contact.Depth, expected);
                await Near(contact.Normal, normal);
                await Assert.That(contact.Normal.Z).IsGreaterThan(.89f);
                await Near(contact.Position.Z, (contact.Position.X - 1024) * .5f, .00002f);
            }
            var corrected = center + contacts[0].Normal * (contacts[0].Depth + .001f);
            await Assert.That(terrain.GetPenetrations(shape, corrected).Count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task NormalRecoveryAndDirectedStanceExitHaveDifferentSlopeDepths()
    {
        var heights = new ZoneTerrainHeightField();
        heights.LoadCell(1, 1, TerrainHeightFieldTests.CreateCell(0, .03125f,
            sample: (x, _, sx, _) => (ushort)((x / 2 + sx) * 32)));
        var terrain = new ZoneManagedTerrainGeometry(heights);
        var query = ZonePhysicsShape.Capsule(.5f, .5f); var center = new Vector3(1100, 1100, 38.5f);
        var normal = terrain.GetPenetrations(query, center)[0];
        var directed = terrain.GetUpwardUnprojections(query, center).MaxBy(contact => contact.Depth);
        await Near(normal.Depth, .5f);
        await Near(directed.Depth, .5f * MathF.Sqrt(1.25f));
        await Assert.That(normal.Normal.X).IsLessThan(0f); await Assert.That(directed.Direction).IsEqualTo(Vector3.UnitZ);
    }

    [Test]
    public async Task BoundaryConstraintsKeepRealWitnessesAndNeverInventAnInfiniteFloor()
    {
        var heights = new ZoneTerrainHeightField(); heights.LoadCell(1, 1, TerrainHeightFieldTests.CreateCell(0, 0));
        var terrain = new ZoneManagedTerrainGeometry(heights); var query = ZonePhysicsShape.Capsule(1, .5f);
        var contacts = terrain.GetPenetrations(query, new(1023.5f, 1100, -.25f));
        await Assert.That(contacts.Count).IsGreaterThan(0);
        foreach (var contact in contacts)
        {
            await Assert.That(contact.Normal).IsEqualTo(Vector3.UnitZ);
            await Assert.That(contact.Position.X).IsGreaterThanOrEqualTo(1024f);
            await Near(contact.Position.Z, 0);
        }
        await Assert.That(terrain.GetPenetrations(query, new(1022.8f, 1100, -.25f)).Count).IsEqualTo(0);
        await Assert.That(terrain.GetPenetrations(query, new(1100, 1100, -1.5005f)).Count).IsEqualTo(0);
        heights.UnloadCell(1, 1);
        await Assert.That(terrain.GetPenetrations(query, new(1023.5f, 1100, -.25f)).Count).IsEqualTo(0);
        heights.LoadCell(1, 1, TerrainHeightFieldTests.CreateCell(0, 0, sample: (_, _, _, _) => 31));
        await Assert.That(terrain.GetPenetrations(query, new(1100, 1100, 0)).Count).IsEqualTo(0);
    }

    private static async Task Near(float actual, float expected, float tolerance = .00002f) => await Assert.That(MathF.Abs(actual - expected)).IsLessThan(tolerance);
    private static async Task Near(Vector3 actual, Vector3 expected, float tolerance = .00002f) => await Assert.That(Vector3.Distance(actual, expected)).IsLessThan(tolerance);
}
