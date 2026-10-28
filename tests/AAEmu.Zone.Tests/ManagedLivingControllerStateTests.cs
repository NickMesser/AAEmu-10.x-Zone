using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Tests;

public sealed class ManagedLivingControllerStateTests
{
    [Test]
    public async Task QueuedModeThreeCannotOverwriteInactiveActualVelocity()
    {
        var backend = Backend();
        var actor = new ZoneActorHandle(1401, 1);
        backend.AddActor(actor, Description() with { CollisionActive = false });
        var origin = new Vector3(10, 20, 100);
        backend.Teleport(actor, origin, Quaternion.Identity);
        backend.ApplyLivingVelocityRequest(actor, new(2, 3, 4), ZonePhysicsLivingVelocityMode.Immediate);
        backend.CopyStatus(actor, out var pending);
        backend.CopyLivingControlState(actor, out var before);
        await Assert.That(pending.Velocity).IsEqualTo(Vector3.Zero);
        await Assert.That(before.HasPendingVelocityRequest).IsTrue();
        await Assert.That(before.LivingFlag2).IsFalse();

        backend.Step(TimeSpan.FromMilliseconds(20));
        backend.CopyStatus(actor, out var applied);
        backend.CopyLivingControlState(actor, out var control);
        await Assert.That(applied.Position).IsEqualTo(origin + new Vector3(2, 3, 4) * .02f);
        await Assert.That(applied.Velocity).IsEqualTo(Vector3.Zero);
        await Assert.That(control.LivingFlag2).IsTrue();
        await Assert.That(control.HasPendingVelocityRequest).IsFalse();
    }

    [Test]
    public async Task ModeThreeFlagSurvivesItsOneStepOverrideUntilModeZeroDrains()
    {
        var backend = Backend();
        var actor = new ZoneActorHandle(1402, 1);
        backend.AddActor(actor, Description());
        backend.Teleport(actor, new(10, 20, 100), Quaternion.Identity);
        backend.ApplyLivingVelocityRequest(actor, Vector3.UnitX, ZonePhysicsLivingVelocityMode.Immediate);
        for (var step = 0; step < 3; step++)
        {
            backend.Step(TimeSpan.FromMilliseconds(20));
            backend.CopyLivingControlState(actor, out var control);
            await Assert.That(control.LivingFlag2).IsTrue();
        }
        backend.ApplyLivingVelocityRequest(actor, Vector3.Zero);
        backend.CopyLivingControlState(actor, out var queued);
        await Assert.That(queued.LivingFlag2).IsTrue();
        backend.Step(TimeSpan.FromMilliseconds(20));
        backend.CopyLivingControlState(actor, out var normal);
        await Assert.That(normal.LivingFlag2).IsFalse();
        await Assert.That(normal.LastVelocityMode).IsEqualTo(ZonePhysicsLivingVelocityMode.Normal);
    }

    [Test]
    public async Task IdleStepsStillAgeImpulseRecoveryBeforeTheNextMovementWish()
    {
        var backend = Backend();
        var actor = new ZoneActorHandle(1406, 1);
        var description = Description();
        backend.AddActor(actor, description with { LivingMovement = description.LivingMovement!.WithInertia(0) });
        backend.Teleport(actor, new(10, 20, 100), Quaternion.Identity);
        backend.ApplyImpulse(actor, Vector3.Zero);
        backend.CopyLivingControlState(actor, out var before);
        await Assert.That(before.ImpulseRecoveryTimeRemaining).IsEqualTo(5f);
        backend.Step(TimeSpan.FromSeconds(6));
        backend.CopyLivingControlState(actor, out var idle);
        await Assert.That(idle.LastStepUsedIdleShortcut).IsTrue();
        await Assert.That(idle.ImpulseRecoveryTimeRemaining).IsEqualTo(0f);
        backend.ApplyLivingVelocityRequest(actor, Vector3.UnitX);
        backend.Step(TimeSpan.FromMilliseconds(20));
        backend.CopyLivingControlState(actor, out var moving);
        await Assert.That(moving.LastStepUsedIdleShortcut).IsFalse();
        await Assert.That(moving.ImpulseRecoveryTimeRemaining).IsEqualTo(0f);
    }

    [Test]
    public async Task RefusedNumericalMovementCannotAgeRecoveryOrCommitStagedControllerState()
    {
        var backend = Backend();
        var actor = new ZoneActorHandle(1407, 1);
        var description = Description();
        backend.AddActor(actor, description with { LivingMovement = description.LivingMovement!.WithInertia(0) });
        backend.Teleport(actor, new(10, 20, 100), Quaternion.Identity);
        backend.ApplyImpulse(actor, Vector3.Zero);
        backend.ApplyVelocity(actor, new(1e18f, 0, 0));
        backend.CopyStatus(actor, out var before);
        backend.CopyLivingControlState(actor, out var beforeControl);
        // The bounded catch-up subdivision cannot represent this travel's squared length.
        await Assert.That(() => backend.Step(TimeSpan.FromSeconds(100000)))
            .Throws<ArgumentOutOfRangeException>();
        backend.CopyStatus(actor, out var after);
        backend.CopyLivingControlState(actor, out var afterControl);
        await Assert.That(after).IsEqualTo(before);
        await Assert.That(afterControl).IsEqualTo(beforeControl);
    }

    [Test]
    public async Task NoChangeAgainstClampedSupportHeightRetainsTheRawPrimitive()
    {
        var backend = Backend();
        var actor = new ZoneActorHandle(1403, 1);
        var initial = Stance() with { Size = new(.4f, .4f, .0001f) };
        var description = ZonePhysicsActorDescription.FromLivingStance(initial, 80, 0);
        backend.AddActor(actor, description);
        backend.Teleport(actor, new(10, 20, 100), Quaternion.Identity);
        var noChange = initial with { Size = new(.4f, .4f, description.LivingMovement!.SupportHalfHeight) };
        await Assert.That(backend.TryChangeLivingStance(actor, noChange, out _)).IsTrue();
        backend.Step(TimeSpan.FromMilliseconds(20));
        backend.CopyDescription(actor, out var queued);
        await Assert.That(queued.Shape.SegmentHalfHeight).IsEqualTo(.0001f);
        await Assert.That(backend.TryApplyLivingStanceImmediatelyForDiagnostic(actor, noChange, out _)).IsTrue();
        backend.CopyDescription(actor, out var immediate);
        await Assert.That(immediate.Shape.SegmentHalfHeight).IsEqualTo(.0001f);
        await Assert.That(immediate.LivingMovement!.SegmentHalfHeight).IsEqualTo(.0001f);
    }

    [Test]
    public async Task OtherLivingHeightShortcutRetainsNativeOperationOrderAtEightKilometers()
    {
        var backend = Backend();
        var actor = new ZoneActorHandle(1404, 1);
        var other = new ZoneActorHandle(1405, 1);
        var stance = Stance() with { HeightCollider = .4f, HeightPivot = .2f };
        var otherStance = Stance() with { HeightCollider = 1, HeightPivot = 1 };
        backend.AddActor(actor, ZonePhysicsActorDescription.FromLivingStance(stance, 80, 0));
        backend.AddActor(other, ZonePhysicsActorDescription.FromLivingStance(otherStance, 80, 0));
        backend.Teleport(actor, new(0, 0, 8000), Quaternion.Identity);
        backend.Teleport(other, new(0, 0, 7999.56f), Quaternion.Identity);
        // Reassociating origin+(hc-hp) would miss this strict segment-only overlap.
        var growth = stance with { Size = new(.41f, .41f, .32f) };
        await Assert.That(backend.TryChangeLivingStance(actor, growth, out var refusal)).IsFalse();
        await Assert.That(refusal).Contains("unprojection range");
        backend.Step(TimeSpan.FromMilliseconds(20));
        backend.CopyDescription(actor, out var applied);
        await Assert.That(applied.Shape.Radius).IsEqualTo(.4f);
        await Assert.That(backend.TryReadLivingStanceUpdate(actor, out var report)).IsTrue();
        await Assert.That(report.Phase).IsEqualTo(ZoneManagedLivingStancePhase.RefusedAtDrain);
    }

    private static ZoneManagedPhysicsBackend Backend() => new(new(new ZoneTerrainHeightField()));
    private static ZoneActorPhysicalizationStance Stance() =>
        new(new(.4f, .4f, .32f), default, default, 1.2f, .15f, 5, true, false);
    private static ZonePhysicsActorDescription Description() => ZonePhysicsActorDescription.FromLivingStance(Stance(), 80, 0);
}
