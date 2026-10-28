using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Tests;

public sealed class ManagedLivingIntegrityTests
{
    [Test]
    public async Task PostMovementIntegrityBlocksAnIntersectingBodyWithoutInventingFloorSupport()
    {
        var heights = new ZoneTerrainHeightField();
        heights.LoadCell(0, 0, TerrainHeightFieldTests.CreateCell(0, 0));
        var backend = new ZoneManagedPhysicsBackend(new(heights));
        var actor = new ZoneActorHandle(1601, 1);
        var description = ZonePhysicsActorDescription.FromLivingStance(
            new(new(.08f, .08f, .16f), default, default, .4f, 0, 5, true, false), 80, -25);
        backend.AddActor(actor, description with { LivingMovement = description.LivingMovement!.WithInertia(0) });
        var position = new Vector3(20, 30, -.5f);
        backend.Teleport(actor, position, Quaternion.Identity);
        backend.ApplyLivingVelocityRequest(actor, new(1e-6f, 0, 0));

        for (var frame = 1; frame <= 4; frame++)
        {
            backend.Step(TimeSpan.FromMilliseconds(10));
            backend.CopyStatus(actor, out var status);
            await Assert.That(status.Position).IsEqualTo(position);
            await Assert.That(status.IsGrounded).IsEqualTo(frame % 2 == 0);
            await Assert.That(status.CachedLivingGroundHeight).IsEqualTo((float?)-1e10f);
            await Assert.That(backend.OwnedPhysics.TryReadGroundHeight(actor, out _)).IsFalse();
            await Assert.That(backend.TryReadLastRecovery(actor, out _)).IsFalse();
            if (frame % 2 == 0) await Assert.That(status.Velocity).IsEqualTo(Vector3.Zero);
            else await Assert.That(status.Velocity.Z).IsEqualTo(-.25f);
        }
    }
}
