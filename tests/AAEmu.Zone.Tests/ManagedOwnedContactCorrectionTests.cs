using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Physics;

namespace AAEmu.Zone.Tests;

public sealed class ManagedOwnedContactCorrectionTests
{
    [Test]
    public async Task CollisionCorrectionPreservesOrientationGravityAndResolvedVelocityButClearsSupport()
    {
        var actor = new ZoneActorHandle(1101, 1);
        var orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .4f);
        var physics = new ZoneOwnedPlayerPhysics(new Floor());
        physics.AddActor(actor, new(2, 3, 10), orientation, gravity: -4);
        physics.Step(TimeSpan.FromMilliseconds(10));
        physics.TryReadGrounded(actor, out var grounded);
        await Assert.That(grounded).IsTrue();
        await Assert.That(physics.TryReadGroundMaterial(actor, out _)).IsTrue();

        var position = new Vector3(4, 5, 12);
        var velocity = new Vector3(3, 2, -.5f);
        physics.CorrectContactPose(actor, position, velocity);
        physics.TryReadPosition(actor, out var corrected);
        physics.TryReadVelocity(actor, out var retainedVelocity);
        physics.TryReadOrientation(actor, out var retainedOrientation);
        physics.TryReadGrounded(actor, out grounded);
        await Assert.That(corrected).IsEqualTo(position);
        await Assert.That(retainedVelocity).IsEqualTo(velocity);
        await Assert.That(retainedOrientation).IsEqualTo(orientation);
        await Assert.That(grounded).IsFalse();
        await Assert.That(physics.TryReadGroundHeight(actor, out _)).IsFalse();
        await Assert.That(physics.TryReadGroundMaterial(actor, out _)).IsFalse();

        physics.Step(TimeSpan.FromMilliseconds(100));
        physics.TryReadVelocity(actor, out var advancedVelocity);
        physics.TryReadPosition(actor, out var advancedPosition);
        await Assert.That(MathF.Abs(advancedVelocity.Z + .9f)).IsLessThan(.0001f);
        await Assert.That(MathF.Abs(advancedPosition.Z - 11.91f)).IsLessThan(.0001f);
        await Assert.That(advancedVelocity.X).IsEqualTo(3f);
        await Assert.That(advancedVelocity.Y).IsEqualTo(2f);
    }

    [Test]
    public async Task InvalidCollisionCorrectionLeavesPoseVelocityAndGroundContactIntact()
    {
        var actor = new ZoneActorHandle(1102, 1);
        var physics = new ZoneOwnedPlayerPhysics(new Floor());
        physics.AddActor(actor, new(2, 3, 10), Quaternion.Identity);
        physics.Step(TimeSpan.FromMilliseconds(10));
        physics.ApplyVelocity(actor, new(3, 2, 0));
        await Assert.That(() => physics.CorrectContactPose(actor, new(4, 5, 12), new(float.NaN, 2, 0)))
            .Throws<ArgumentException>();
        physics.TryReadPosition(actor, out var position);
        physics.TryReadVelocity(actor, out var velocity);
        physics.TryReadGrounded(actor, out var grounded);
        await Assert.That(position).IsEqualTo(new Vector3(2, 3, 10));
        await Assert.That(velocity).IsEqualTo(new Vector3(3, 2, 0));
        await Assert.That(grounded).IsTrue();
        await Assert.That(physics.TryReadGroundMaterial(actor, out var material)).IsTrue();
        await Assert.That(material).IsEqualTo((ushort)37);
    }

    [Test]
    public async Task OverflowingRotationIsRejectedBeforeTeleportChangesTheActor()
    {
        var actor = new ZoneActorHandle(1103, 1);
        var physics = new ZoneOwnedPlayerPhysics(new Floor());
        physics.AddActor(actor, new(2, 3, 10), Quaternion.Identity);
        physics.Step(TimeSpan.FromMilliseconds(10));
        await Assert.That(() => physics.Teleport(actor, new(4, 5, 12), new(1e30f, 1e30f, 1e30f, 1e30f)))
            .Throws<ArgumentException>();
        physics.TryReadPosition(actor, out var position);
        physics.TryReadGrounded(actor, out var grounded);
        await Assert.That(position).IsEqualTo(new Vector3(2, 3, 10));
        await Assert.That(grounded).IsTrue();
    }

    private sealed class Floor : ZoneOwnedPlayerPhysics.IGroundQuery
    {
        public bool TryGround(Vector3 from, Vector3 to, out Vector3 position, out ushort material)
        { position = new(to.X, to.Y, 10); material = 37; return true; }
    }
}
