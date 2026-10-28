using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Tests;

public sealed class ManagedLivingCorrectionTests
{
    [Test]
    public async Task NativeNetworkCorrectionRadiusBandKeepsItsNearFarAndForceBranches()
    {
        await Near(ZoneManagedPhysicsBackend.LivingCorrectionPosition(Vector3.Zero, new(.02f, 0, 0),
            Vector3.Zero, .4f, false).X, .00033f);
        await Near(ZoneManagedPhysicsBackend.LivingCorrectionPosition(Vector3.Zero, Vector3.UnitX,
            Vector3.Zero, .4f, false).X, .96f);
        await Near(ZoneManagedPhysicsBackend.LivingCorrectionPosition(Vector3.Zero, Vector3.UnitX,
            new(10, 0, 0), .4f, false).X, .8f);
        await Assert.That(ZoneManagedPhysicsBackend.LivingCorrectionPosition(Vector3.Zero, Vector3.One,
            Vector3.Zero, .4f, true)).IsEqualTo(Vector3.One);
    }

    [Test]
    public async Task CorrectionQueuesPoseAndCopiesActualAndOriginalWishesWithoutChangingMode()
    {
        var (backend, actor) = Body();
        var origin = new Vector3(10, 20, 100);
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .6f);
        backend.Teleport(actor, origin, rotation);
        var correction = new ZoneActorMovementCorrectionAction(origin + Vector3.One,
            new(3, 4, 5), new(.5f, 1, 2), false, false, true, 0, 0);
        await Assert.That(backend.TryApplyLivingCorrection(actor, correction, out _)).IsTrue();
        backend.CopyStatus(actor, out var submitted);
        backend.CopyLivingControlState(actor, out var control);
        await Assert.That(submitted.Position).IsEqualTo(origin);
        await Assert.That(submitted.Velocity).IsEqualTo(new Vector3(3, 4, 5));
        await Assert.That(control.RequestedVelocity).IsEqualTo(new Vector3(.5f, 1, 5));
        await Assert.That(control.LastVelocityMode).IsEqualTo(ZonePhysicsLivingVelocityMode.Normal);
        await Assert.That(control.LivingFlag2).IsFalse();

        backend.Step(TimeSpan.FromMilliseconds(20));
        backend.CopyStatus(actor, out var applied);
        await Assert.That(applied.Position).IsEqualTo(correction.Position + control.RequestedVelocity * .02f);
        await Assert.That(applied.Orientation).IsEqualTo(rotation);
        await Assert.That(applied.Velocity).IsEqualTo(submitted.Velocity);
    }

    [Test]
    public async Task MissingOriginalWishRetainsXYAndMissingSameGroundOwnerRefusesBeforeAnyWrite()
    {
        var (backend, actor) = Body();
        backend.ApplyVelocity(actor, new(1, 2, 3));
        var correction = new ZoneActorMovementCorrectionAction(new(4, 5, 6), new(7, 8, 9),
            null, false, true, true, 0, 0);
        await Assert.That(backend.TryApplyLivingCorrection(actor, correction, out _)).IsTrue();
        backend.CopyLivingControlState(actor, out var control);
        await Assert.That(control.RequestedVelocity).IsEqualTo(new Vector3(1, 2, 9));
        await Assert.That(control.LivingFlag2).IsTrue();
        backend.CopyStatus(actor, out var before);
        await Assert.That(backend.TryApplyLivingCorrection(actor,
            correction with { SameGround = true, Velocity = Vector3.Zero }, out var refusal)).IsFalse();
        await Assert.That(refusal).Contains("retained support");
        backend.CopyStatus(actor, out var after);
        backend.CopyLivingControlState(actor, out var afterControl);
        await Assert.That(after).IsEqualTo(before);
        await Assert.That(afterControl).IsEqualTo(control);
    }

    [Test]
    public async Task ExplicitTeleportDiscardsEarlierQueuedMotionAndCorrectionPose()
    {
        var (backend, actor) = Body();
        backend.ApplyLivingVelocityRequest(actor, Vector3.One, ZonePhysicsLivingVelocityMode.Immediate);
        await Assert.That(backend.TryApplyLivingCorrection(actor,
            new(Vector3.One, Vector3.One, Vector3.One, false, false, true, 0, 0), out _)).IsTrue();
        var target = new Vector3(10, 20, 100);
        backend.Teleport(actor, target, Quaternion.Identity);
        backend.Step(TimeSpan.FromMilliseconds(20));
        backend.CopyStatus(actor, out var after);
        backend.CopyLivingControlState(actor, out var control);
        await Assert.That(after.Position).IsEqualTo(target);
        await Assert.That(after.Velocity).IsEqualTo(Vector3.Zero);
        await Assert.That(control.RequestedVelocity).IsEqualTo(Vector3.Zero);
        await Assert.That(control.LivingFlag2).IsFalse();
    }

    [Test]
    public async Task ForcedUnrepresentableTravelRefusesBeforeImmediateFieldsOrPoseQueueChange()
    {
        var (backend, actor) = Body();
        backend.Teleport(actor, new(10, 20, 100), Quaternion.Identity);
        backend.CopyStatus(actor, out var before);
        backend.CopyLivingControlState(actor, out var beforeControl);
        await Assert.That(() => backend.TryApplyLivingCorrection(actor,
            new(new(float.MaxValue, 0, 0), Vector3.One, Vector3.One, false, false, true, 0, 0), out _))
            .Throws<ArgumentOutOfRangeException>();
        backend.CopyStatus(actor, out var after);
        backend.CopyLivingControlState(actor, out var afterControl);
        await Assert.That(after).IsEqualTo(before);
        await Assert.That(afterControl).IsEqualTo(beforeControl);
        backend.Step(TimeSpan.FromMilliseconds(20));
        backend.CopyStatus(actor, out var stepped);
        await Assert.That(stepped.Position).IsEqualTo(before.Position);
    }

    private static (ZoneManagedPhysicsBackend Backend, ZoneActorHandle Actor) Body()
    {
        var backend = new ZoneManagedPhysicsBackend(new(new ZoneTerrainHeightField()));
        var actor = new ZoneActorHandle(1501, 1);
        backend.AddActor(actor, ZonePhysicsActorDescription.FromLivingStance(
            new(new(.4f, .4f, .32f), default, default, 1.2f, .15f, 5, true, false), 80, 0)
            with { CollisionActive = false });
        return (backend, actor);
    }

    private static async Task Near(float actual, float expected) =>
        await Assert.That(MathF.Abs(actual - expected)).IsLessThan(.000001f);
}
