using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Physics;

namespace AAEmu.Zone.Tests;

public sealed class ManagedLivingOrientationTests
{
    [Test]
    public async Task PendingOrientationIsCopiedFromTheOrderedCommandAndClearsWithDrainOrExactRetirement()
    {
        using var world = new ManagedNpcPhysicsTests.World(); using var body = world.Admit(new(1100, 1100, 1000));
        await Assert.That(body.TryReadPendingOrientation(out _)).IsFalse();
        var first = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .3f);
        var latest = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .7f);
        body.RequestOrientation(first);
        body.RequestVelocity(new(2, 0, 0), ZonePhysicsLivingVelocityMode.Immediate);
        body.RequestOrientation(latest);
        await Assert.That(body.TryReadPendingOrientation(out var pending)).IsTrue();
        await Assert.That(pending).IsEqualTo(latest);
        await Assert.That(body.CopyState().Status.Orientation).IsEqualTo(Quaternion.Identity);
        world.Backend.Step(TimeSpan.FromMilliseconds(20));
        await Assert.That(body.TryReadPendingOrientation(out _)).IsFalse();
        await Assert.That(body.CopyState().Status.Orientation).IsEqualTo(latest);
        body.RequestOrientation(first);
        var old = body.Actor;
        body.Dispose();
        var next = new ZoneActorHandle(old.UnitObjectId, old.Generation + 1);
        world.Backend.AddActor(next, body.Admission.Description);
        await Assert.That(world.Backend.TryReadPendingLivingOrientation(next, out _)).IsFalse();
        await Assert.That(() => world.Backend.TryReadPendingLivingOrientation(old, out _)).Throws<InvalidOperationException>();
        await Assert.That(() => body.TryReadPendingOrientation(out _)).Throws<ObjectDisposedException>();
        world.Backend.RemoveActor(next);
    }

    [Test]
    public async Task QueuedYawChangesOnlyOrientationAndPreservesTheActualSupportedMotor()
    {
        using var turned = new ManagedNpcPhysicsTests.World(); using var control = new ManagedNpcPhysicsTests.World();
        using var body = turned.Admit(new(1100, 1100, 10.1f)); using var other = control.Admit(new(1100, 1100, 10.1f));
        foreach (var pair in new[] { (turned, body), (control, other) })
        {
            pair.Item2.RequestVelocity(new(2, 0, 0), ZonePhysicsLivingVelocityMode.Immediate);
            pair.Item1.Backend.Step(TimeSpan.FromMilliseconds(20));
        }
        var previous = body.CopyState();
        var unitPose = body.Npc.Unit.Pose;
        var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .7f);
        body.RequestOrientation(yaw);
        await Assert.That(body.CopyState()).IsEqualTo(previous);
        turned.Backend.Step(TimeSpan.FromMilliseconds(20)); control.Backend.Step(TimeSpan.FromMilliseconds(20));
        var actual = body.CopyState(); var unchanged = other.CopyState();
        await Assert.That(actual.Status.Orientation).IsEqualTo(yaw);
        await Assert.That(actual.Status with { Orientation = unchanged.Status.Orientation }).IsEqualTo(unchanged.Status);
        await Assert.That(actual.Control).IsEqualTo(unchanged.Control);
        await Assert.That(actual.AppliedDescription).IsEqualTo(previous.AppliedDescription);
        await Assert.That(actual.Status.IsGrounded).IsTrue();
        await Assert.That(actual.Status.Velocity.X).IsGreaterThan(0f);
        await Assert.That(turned.Backend.GroundMaterialFor(body.Actor)).IsEqualTo(control.Backend.GroundMaterialFor(other.Actor));
        await Assert.That(body.Npc.Unit.Pose).IsEqualTo(unitPose);
    }

    [Test]
    public async Task OrientationPreservesStanceCorrectionAndWishCommandOrder()
    {
        foreach (var correctionFirst in new[] { false, true })
        {
            using var world = new ManagedNpcPhysicsTests.World(); using var body = world.Admit(new(1100, 1100, 1000));
            world.Statics.AddInstance(new ZoneManagedTriangleMesh(
                [new(1098, 1098, 1002.5f), new(1102, 1098, 1002.5f), new(1102, 1102, 1002.5f), new(1098, 1102, 1002.5f)],
                [0, 1, 2, 0, 2, 3]), Matrix4x4.Identity);
            body.RequestOrientation(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .3f));
            if (correctionFirst) Correction();
            body.RequestStanceSlot(0); body.PrepareFrame();
            var desired = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.2f);
            body.RequestOrientation(desired);
            if (!correctionFirst) Correction();
            body.RequestVelocity(new(2, 0, 0), ZonePhysicsLivingVelocityMode.Immediate);
            await Assert.That(body.CopyState().Control.HasPendingVelocityRequest).IsTrue();
            await Assert.That(body.CopyState().Status.Orientation).IsEqualTo(Quaternion.Identity);
            world.Backend.Step(TimeSpan.FromMilliseconds(20));
            var state = body.CopyState();
            await Assert.That(state.Status.Orientation).IsEqualTo(desired);
            await Assert.That(state.AppliedDescription.Shape.Radius).IsEqualTo(correctionFirst ? .5f : .3f);
            await Assert.That(state.LastStanceUpdate!.Value.Phase).IsEqualTo(correctionFirst
                ? ZoneManagedLivingStancePhase.Applied : ZoneManagedLivingStancePhase.RefusedAtDrain);
            await Assert.That(state.Control.RequestedVelocity).IsEqualTo(new Vector3(2, 0, 0));
            await Assert.That(state.Status.Velocity.X).IsEqualTo(2f);
            await Assert.That(state.CurrentStanceSlot).IsEqualTo(1);
            void Correction() => world.Backend.TryApplyLivingCorrection(body.Actor,
                new(new(1100, 1100, 990), new(2, 0, -1), new(5, 0, 0), false, false, true, 0, 0), out _);
        }
    }

    [Test]
    public async Task RetirementCancelsOnlyTheExactGenerationAndEquivalentQuaternionSignsSurvive()
    {
        using var world = new ManagedNpcPhysicsTests.World(); using var body = world.Admit(new(1100, 1100, 1000));
        var old = body.Actor;
        body.RequestOrientation(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .7f));
        body.Dispose();
        var replacement = new ZoneActorHandle(old.UnitObjectId, old.Generation + 1);
        world.Backend.AddActor(replacement, body.Admission.Description);
        world.Backend.Step(TimeSpan.FromMilliseconds(20));
        world.Backend.CopyStatus(replacement, out var fresh);
        await Assert.That(fresh.Orientation).IsEqualTo(Quaternion.Identity);
        await Assert.That(() => body.RequestOrientation(Quaternion.Identity)).Throws<ObjectDisposedException>();
        await Assert.That(() => world.Backend.SetLivingOrientation(old, Quaternion.Identity)).Throws<InvalidOperationException>();
        world.Backend.SetLivingOrientation(replacement, new(0, 0, 0, -1));
        world.Backend.Step(TimeSpan.FromMilliseconds(20));
        world.Backend.CopyStatus(replacement, out fresh);
        await Assert.That(fresh.Orientation).IsEqualTo(new Quaternion(0, 0, 0, -1));
        world.Backend.RemoveActor(replacement);
    }

    [Test]
    public async Task InvalidAndUnsupportedOrientationsRefuseBeforeChangingThePendingMotor()
    {
        using var world = new ManagedNpcPhysicsTests.World(); using var body = world.Admit(new(1100, 1100, 1000));
        body.RequestVelocity(new(2, 0, 0), ZonePhysicsLivingVelocityMode.Immediate);
        var before = body.CopyState();
        await Assert.That(() => body.RequestOrientation(new(float.NaN, 0, 0, 1))).Throws<ArgumentException>();
        await Assert.That(() => body.RequestOrientation(default)).Throws<ArgumentException>();
        await Assert.That(() => body.RequestOrientation(new(0, 0, 0, 2))).Throws<NotSupportedException>();
        await Assert.That(() => body.RequestOrientation(Quaternion.CreateFromAxisAngle(Vector3.UnitX, .1f))).Throws<NotSupportedException>();
        await Assert.That(body.CopyState()).IsEqualTo(before);
        world.Backend.Step(TimeSpan.FromMilliseconds(20));
        await Assert.That(body.CopyState().Status.Velocity.X).IsEqualTo(2f);
        await Assert.That(body.CopyState().Status.Orientation).IsEqualTo(Quaternion.Identity);
        var generic = new ZoneActorHandle(body.Actor.UnitObjectId + 10_000, 1);
        world.Backend.AddActor(generic, new(ZonePhysicsShape.Box(new(.3f)), 80));
        await Assert.That(() => world.Backend.SetLivingOrientation(generic, Quaternion.Identity)).Throws<NotSupportedException>();
        world.Backend.RemoveActor(generic);
    }
}
