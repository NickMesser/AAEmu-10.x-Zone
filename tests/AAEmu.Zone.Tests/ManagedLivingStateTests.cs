using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Tests;

public sealed class ManagedLivingStateTests
{
    [Test]
    public async Task ActiveLivingStatusTracksFreeFallLandingJumpAndTeleport()
    {
        var terrain = new ZoneTerrainHeightField();
        terrain.LoadCell(0, 0, TerrainHeightFieldTests.CreateCell(0, 0));
        var backend = new ZoneManagedPhysicsBackend(new(terrain));
        var actor = new ZoneActorHandle(1101, 1);
        backend.AddActor(actor, Description(gravity: -25));
        backend.Teleport(actor, new(20, 30, 2), Quaternion.Identity);

        backend.Step(TimeSpan.FromMilliseconds(10));
        backend.CopyStatus(actor, out var falling);
        await Assert.That(falling.IsFlying).IsTrue();
        await Assert.That(falling.IsGrounded).IsFalse();
        await Assert.That(falling.Velocity.Z).IsLessThan(0f);

        backend.Step(TimeSpan.FromSeconds(.5));
        backend.CopyStatus(actor, out var standing);
        await Assert.That(standing.IsFlying).IsFalse();
        await Assert.That(standing.IsGrounded).IsTrue();
        await Assert.That(MathF.Abs(standing.Position.Z)).IsLessThan(.00001f);

        backend.ApplyImpulse(actor, new(0, 0, 8));
        backend.Step(TimeSpan.FromMilliseconds(10));
        backend.CopyStatus(actor, out var jumping);
        await Assert.That(jumping.IsFlying).IsTrue();
        await Assert.That(jumping.IsGrounded).IsFalse();
        await Assert.That(jumping.Velocity.Z).IsGreaterThan(0f);

        backend.Step(TimeSpan.FromSeconds(1));
        backend.CopyStatus(actor, out var landed);
        await Assert.That(landed.IsFlying).IsFalse();
        await Assert.That(landed.IsGrounded).IsTrue();

        backend.Teleport(actor, new(5000, 5000, 20), Quaternion.Identity);
        backend.CopyStatus(actor, out var teleported);
        await Assert.That(teleported.IsFlying).IsTrue();
        await Assert.That(teleported.IsGrounded).IsFalse();
        await Assert.That(teleported.Velocity).IsEqualTo(Vector3.Zero);
        await Assert.That(backend.GroundMaterialFor(actor)).IsNull();

        backend.RemoveActor(actor);
        await Assert.That(() => backend.CopyStatus(actor, out _)).Throws<InvalidOperationException>();
        var next = new ZoneActorHandle(actor.UnitObjectId, 2);
        backend.AddActor(next, Description(gravity: -25));
        backend.Teleport(next, new(5000, 5000, 20), Quaternion.Identity);
        backend.CopyStatus(next, out var reentered);
        await Assert.That(reentered.IsFlying).IsTrue();
        await Assert.That(reentered.IsGrounded).IsFalse();
    }

    [Test]
    public async Task AirborneMeansUnsupportedEvenAtZeroVelocityAndZeroGravity()
    {
        var backend = new ZoneManagedPhysicsBackend(new(new ZoneTerrainHeightField()));
        var actor = new ZoneActorHandle(1102, 1);
        backend.AddActor(actor, Description(gravity: 0));
        var position = new Vector3(20, 30, 100);
        backend.Teleport(actor, position, Quaternion.Identity);
        backend.Step(TimeSpan.FromMilliseconds(10));
        backend.CopyStatus(actor, out var hovering);
        await Assert.That(hovering.Position).IsEqualTo(position);
        await Assert.That(hovering.Velocity).IsEqualTo(Vector3.Zero);
        await Assert.That(hovering.IsFlying).IsTrue();
        await Assert.That(hovering.IsGrounded).IsFalse();
    }

    private static ZonePhysicsActorDescription Description(float gravity) =>
        ZonePhysicsActorDescription.FromLivingStance(
            new(new(.4f, .4f, .32f), default, default, 1.2f, 0, 5, true, false), 80, gravity);
}
