using System.Numerics;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Scene;

namespace AAEmu.Zone.Tests;

public sealed class ManagedNpcColliderBodyTests
{
    [Test]
    public async Task DisabledActionWritesZeroWishAndClearsMode3WithoutResettingActualMotionOrPose()
    {
        using var world = new ManagedNpcPhysicsTests.World(); using var body = world.Admit(new(1100, 1100, 1000));
        body.RequestVelocity(new(3, 0, 0), ZonePhysicsLivingVelocityMode.Immediate);
        world.Backend.Step(TimeSpan.FromMilliseconds(20));
        var before = body.CopyState();
        await Assert.That(before.Control.LivingFlag2).IsTrue();
        body.Apply(ZoneAnimatedColliderParameters.Create(1, 0, 6, 2, false));
        var after = body.CopyState();
        await Assert.That(after.Control.RequestedVelocity).IsEqualTo(Vector3.Zero);
        await Assert.That(after.Control.LivingFlag2).IsFalse();
        await Assert.That(after.Control.ForceRequestedVelocityOnce).IsFalse();
        await Assert.That(after.Status.Position).IsEqualTo(before.Status.Position);
        await Assert.That(after.Status.Velocity).IsEqualTo(before.Status.Velocity);
        await Assert.That(after.Control.SuspensionOffset).IsEqualTo(before.Control.SuspensionOffset);
        await Assert.That(after.AppliedDescription.LivingMovement!.Inertia).IsEqualTo(6f);
        await Assert.That(after.AppliedDescription.LivingMovement.InertiaAcceleration).IsEqualTo(2f);
        await Assert.That(after.AppliedDescription.Gravity).IsEqualTo(-25f);
        await Assert.That(after.AppliedDescription.CollisionActive).IsFalse();
    }

    [Test]
    public async Task PartGeometryFlagsActuallyRemoveAndRestoreTheLivingTargetFromSweepsAndOverlaps()
    {
        using var world = new ManagedNpcPhysicsTests.World(); using var body = world.Admit(new(1100, 1100, 1000));
        body.CopyDescription(out var description);
        var center = new Vector3(1100, 1100, 1000 + description.CenterOffsetZ);
        var probe = ZonePhysicsShape.Box(new(.01f));
        await Assert.That(world.Backend.TryCollide(probe, center, null)).IsTrue();
        await Assert.That(world.Backend.TrySweep(probe, center - new Vector3(2, 0, 0), new(3, 0, 0), null, out _)).IsTrue();
        body.Apply(ZoneAnimatedColliderParameters.Create(1, 0, 8, 0, false));
        await Assert.That(world.Backend.TryCollide(probe, center, null)).IsFalse();
        await Assert.That(world.Backend.TrySweep(probe, center - new Vector3(2, 0, 0), new(3, 0, 0), null, out _)).IsFalse();
        var disabled = world.Backend.CopyAnimatedColliderState(body.Actor);
        await Assert.That(disabled.PartGeometryFlags & 2).IsEqualTo(0u);
        await Assert.That(disabled.EntityFlags).IsEqualTo(1603u);
        await Assert.That(disabled.PartColliderFlags).IsEqualTo(2u);
        await Assert.That(disabled.Type26Value).IsEqualTo((byte)0);
        body.Apply(ZoneAnimatedColliderParameters.Create(4, 0, 8, 0, false));
        await Assert.That(world.Backend.TryCollide(probe, center, null)).IsTrue();
        await Assert.That(world.Backend.TrySweep(probe, center - new Vector3(2, 0, 0), new(3, 0, 0), null, out var hit)).IsTrue();
        await Assert.That(hit.PhysicalMaterialId).IsEqualTo((ushort)143);
        await Assert.That(body.CopyState().AppliedDescription.CollisionActive).IsTrue();
        await Assert.That(body.PhysicalEntityType).IsEqualTo(4);
        body.Dispose(); await Assert.That(body.PhysicalEntityType).IsEqualTo(0);
        await Assert.That(() => body.Apply(ZoneAnimatedColliderParameters.Create(4, 0, 8, 0, false))).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task PendingBodyCommandsAreRefusedBeforeAnyActionOrParameterWrite()
    {
        using var world = new ManagedNpcPhysicsTests.World(); using var body = world.Admit(new(1100, 1100, 1000));
        body.RequestVelocity(new(3, 0, 0), ZonePhysicsLivingVelocityMode.Immediate);
        var before = body.CopyState(); var flags = world.Backend.CopyAnimatedColliderState(body.Actor);
        await Assert.That(() => body.Apply(ZoneAnimatedColliderParameters.Create(1, 0, 6, 2, false))).Throws<NotSupportedException>();
        await Assert.That(body.CopyState()).IsEqualTo(before);
        await Assert.That(world.Backend.CopyAnimatedColliderState(body.Actor)).IsEqualTo(flags);
        world.Backend.Step(TimeSpan.FromMilliseconds(20));
        await Assert.That(body.CopyState().Control.RequestedVelocity).IsEqualTo(new Vector3(3, 0, 0));
    }

    [Test]
    public async Task UnownedAuxiliaryMasksType26AndExtraRawWritesCannotPartiallyApplyTheResetAction()
    {
        using var world = new ManagedNpcPhysicsTests.World(); using var body = world.Admit(new(1100, 1100, 1000));
        body.RequestVelocity(new(3, 0, 0), ZonePhysicsLivingVelocityMode.Immediate);
        world.Backend.Step(TimeSpan.FromMilliseconds(20)); var before = body.CopyState();
        foreach (var update in new[] { ZoneAnimatedColliderParameters.Create(5, 0, 8, 0, false),
            ZoneAnimatedColliderParameters.Create(1, 4, 8, 0, false), ZoneAnimatedColliderParameters.Create(4, 2, 8, 0, false),
            ZoneAnimatedColliderParameters.Create(6, 0, 8, 0, false) })
        {
            await Assert.That(() => body.Apply(update)).Throws<NotSupportedException>();
            await Assert.That(body.CopyState()).IsEqualTo(before);
        }
        var unexpected = ZoneAnimatedColliderParameters.Create(1, 0, 8, 0, false);
        unexpected.Living.Single(0x0C, .25f);
        await Assert.That(() => body.Apply(unexpected)).Throws<NotSupportedException>();
        await Assert.That(body.CopyState()).IsEqualTo(before);
    }
}
