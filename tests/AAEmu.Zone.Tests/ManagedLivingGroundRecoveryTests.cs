using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Tests;

public sealed class ManagedLivingGroundRecoveryTests
{
    [Test]
    public async Task AuthoredSupportRaisesABuriedPivotWhileTheActualColliderIsAlreadyClear()
    {
        var statics = new ZoneManagedStaticGeometry();
        statics.AddInstance(Floor(0), Matrix4x4.Identity);
        var backend = new ZoneManagedPhysicsBackend(new(new ZoneTerrainHeightField()), staticGeometry: statics);
        var actor = new ZoneActorHandle(971, 1);
        var description = Living(pivot: .15f);
        backend.AddActor(actor, description);
        backend.Teleport(actor, new(20, 30, .05f), Quaternion.Identity);
        var center = new Vector3(20, 30, .05f + description.CenterOffsetZ);
        await Assert.That(statics.TryCollide(description.Shape, center)).IsFalse();

        backend.Step(TimeSpan.FromMilliseconds(10));
        backend.CopyStatus(actor, out var standing);
        await Assert.That(standing.IsGrounded).IsTrue();
        await Assert.That(MathF.Abs(standing.Position.Z - .15f)).IsLessThan(.0001f);
        await Assert.That(standing.Velocity).IsEqualTo(Vector3.Zero);
        await Assert.That(backend.OwnedPhysics.TryReadGroundHeight(actor, out var height)).IsTrue();
        await Assert.That(MathF.Abs(height)).IsLessThan(.00001f);
        await Assert.That(backend.GroundMaterialFor(actor)).IsEqualTo((ushort?)37);
    }

    [Test]
    public async Task AuthoredGroundRecoveryUsesColliderHeightMinusSegmentAndRejectsAnAscendingFoot()
    {
        var statics = new ZoneManagedStaticGeometry();
        statics.AddInstance(Floor(0), Matrix4x4.Identity);
        var description = Living();
        var query = new ZoneManagedSceneGroundQuery(new(new ZoneTerrainHeightField()), statics,
            actorDescription: _ => description);
        var actor = new ZoneActorHandle(972, 1);
        // hc-h = 1.2-.32 = .88. The collider's actual bottom is a separate quantity.
        await Assert.That(query.TryCharacterGround(actor, new(20, 30, -.87f), new(20, 30, -.87f),
            false, out var ground, out var material)).IsTrue();
        await Assert.That(MathF.Abs(ground.Z)).IsLessThan(.00001f);
        await Assert.That(material).IsEqualTo((ushort)37);
        await Assert.That(query.TryCharacterGround(actor, new(20, 30, -.9f), new(20, 30, -.9f),
            false, out _, out _)).IsFalse();
        await Assert.That(query.TryCharacterGround(actor, new(20, 30, -.2f), new(20, 30, -.1f),
            false, out _, out _)).IsFalse();
    }

    [Test]
    public async Task BoundedLivingSupportKeepsAnInteriorFloorAndRetiresWithItsGeometry()
    {
        var terrain = new ZoneTerrainHeightField();
        terrain.LoadCell(0, 0, TerrainHeightFieldTests.CreateCell(10, 0));
        var statics = new ZoneManagedStaticGeometry();
        var floor = statics.AddInstance(Floor(0), Matrix4x4.Identity);
        var query = new ZoneManagedSceneGroundQuery(new(terrain), statics, actorDescription: _ => Living());
        var actor = new ZoneActorHandle(973, 1);
        var foot = new Vector3(20, 30, -.1f);
        await Assert.That(query.TryCharacterGround(actor, foot, foot, false, out var ground, out _)).IsTrue();
        await Assert.That(MathF.Abs(ground.Z)).IsLessThan(.00001f);
        statics.RemoveInstance(floor);
        await Assert.That(query.TryCharacterGround(actor, foot, foot, false, out _, out _)).IsFalse();
        await Assert.That(MathF.Abs(ground.Z)).IsLessThan(.00001f);
    }

    [Test]
    public async Task HighCoordinateRecoveryRetainsTheOriginalStrictFloatBoundary()
    {
        var statics = new ZoneManagedStaticGeometry();
        statics.AddInstance(Floor(8000), Matrix4x4.Identity);
        var description = Living();
        var query = new ZoneManagedSceneGroundQuery(new(new ZoneTerrainHeightField()), statics,
            actorDescription: _ => description);
        var rise = 1.2f - .32f;
        var foot = new Vector3(20, 30, 8000 - rise);
        // Subtracting the rounded foot from the floor is less than rise, but the original
        // strict predicate compares foot > floor-rise. Equality must stay outside the bound.
        await Assert.That(8000 - foot.Z).IsLessThan(rise);
        await Assert.That(query.TryCharacterGround(new(975, 1), foot, foot, false, out _, out _)).IsFalse();
        foot.Z += .001f;
        await Assert.That(query.TryCharacterGround(new(975, 1), foot, foot, false, out _, out _)).IsTrue();
    }

    [Test]
    public async Task ExplicitKinematicProfilesRetainTheirTrajectoryBasedSupportContract()
    {
        var statics = new ZoneManagedStaticGeometry();
        statics.AddInstance(Floor(0), Matrix4x4.Identity);
        var query = new ZoneManagedSceneGroundQuery(new(new ZoneTerrainHeightField()), statics,
            actorDescription: _ => Living() with { Profile = ZonePhysicsActorProfile.Kinematic });
        var foot = new Vector3(20, 30, -.1f);
        await Assert.That(query.TryCharacterGround(new(974, 1), foot, foot, false, out _, out _)).IsFalse();
    }

    private static ZonePhysicsActorDescription Living(float pivot = 0) =>
        ZonePhysicsActorDescription.FromLivingStance(new(new(.4f, .4f, .32f), default, default,
            1.2f, pivot, 5, true, false), 80, gravity: 0);

    private static ZoneManagedTriangleMesh Floor(float height) => new(
        [new(0, 0, height), new(100, 0, height), new(100, 100, height), new(0, 100, height)],
        [0, 1, 2, 0, 2, 3], [37, 37]);
}
