using System.Buffers.Binary;
using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Content;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.Tests;

public sealed class ManagedStancePipelineTests
{
    [Test]
    public async Task ValidWireSlotChangesBeforePhysicsAndPublishesAcceptedStanceWithItsSpeed()
    {
        using var scene = new Fixture();
        scene.Begin(1, stance: 6);
        await Assert.That(scene.Actor.LivingStances!.CurrentSlot).IsEqualTo(1);
        await Assert.That(scene.Actor.LivingStances.DesiredSlot).IsEqualTo(6);
        await Assert.That(scene.Backend.Attempts).IsEqualTo(0);

        scene.Finish();
        await Assert.That(scene.Backend.Events.ToArray()).IsEquivalentTo(new[] { "stance", "inertia", "step" });
        await Assert.That(scene.Backend.Attempts).IsEqualTo(1);
        await Assert.That(scene.Actor.LivingStances.CurrentSlot).IsEqualTo(6);
        await Assert.That(scene.Actor.LivingStances.Current.AuthoredRow!.StanceId).IsEqualTo(7);
        await Assert.That(scene.Backend.AcceptedStance!.Size.Z).IsEqualTo(.1f);
        // Actor flag bit zero selects the original .5 walking scalar.
        await Assert.That(MathF.Abs(scene.Backend.StepVelocity.Length() - 1)).IsLessThan(.0001f);
        await Assert.That(scene.LastOutgoingStance()).IsEqualTo((sbyte?)6);
    }

    [Test]
    public async Task RefusedGrowthKeepsAcceptedLimitsAndOutgoingThenRetriesWithoutAnotherRequest()
    {
        using var scene = new Fixture();
        scene.Backend.AcceptChanges = false;
        scene.Begin(1, stance: 0);
        scene.Finish();
        await Assert.That(scene.Actor.LivingStances!.CurrentSlot).IsEqualTo(1);
        await Assert.That(scene.Actor.LivingStances.DesiredSlot).IsEqualTo(0);
        await Assert.That(scene.Backend.Attempts).IsEqualTo(1);
        await Assert.That(MathF.Abs(scene.Backend.StepVelocity.Length() - 2.5f)).IsLessThan(.0001f);
        await Assert.That(scene.LastOutgoingStance()).IsEqualTo((sbyte?)1);
        await Assert.That(scene.Diagnostics.StanceChangeRefusals).IsEqualTo(1L);

        // -1 supplies no new stance wish. The pending standing request must survive it.
        scene.Backend.AcceptChanges = true;
        scene.Begin(2, stance: -1);
        scene.Finish();
        await Assert.That(scene.Backend.Attempts).IsEqualTo(2);
        await Assert.That(scene.Actor.LivingStances.CurrentSlot).IsEqualTo(0);
        await Assert.That(scene.Actor.LivingStances.DesiredSlot).IsEqualTo(0);
        await Assert.That(scene.Actor.LivingStances.LastRefusal).IsNull();
        await Assert.That(MathF.Abs(scene.Backend.StepVelocity.Length() - 3.5f)).IsLessThan(.0001f);
        await Assert.That(scene.LastOutgoingStance()).IsEqualTo((sbyte?)0);

        scene.Begin(3);
        scene.Finish();
        await Assert.That(scene.Backend.Attempts).IsEqualTo(2);
    }

    [Test]
    public async Task UndefinedSlotNineIsReportedWithoutCallingPhysicalDimensionsOrPublishingTheWish()
    {
        using var scene = new Fixture();
        scene.Begin(1, stance: 9);
        scene.Finish();
        await Assert.That(scene.Backend.Attempts).IsEqualTo(0);
        await Assert.That(scene.Actor.LivingStances!.CurrentSlot).IsEqualTo(1);
        await Assert.That(scene.Actor.LivingStances.DesiredSlot).IsEqualTo(9);
        await Assert.That(scene.Diagnostics.StanceChangeRefusals).IsEqualTo(1L);
        await Assert.That(scene.Diagnostics.LastRefusalMessage!).Contains("slot 9");
        await Assert.That(scene.LastOutgoingStance()).IsEqualTo((sbyte?)1);

        // A later request for the accepted slot cancels the unsupported pending wish.
        scene.Begin(2, stance: 1);
        scene.Finish();
        await Assert.That(scene.Actor.LivingStances.HasPendingChange).IsFalse();
        await Assert.That(scene.Actor.LivingStances.LastRefusal).IsNull();
        await Assert.That(scene.Backend.Attempts).IsEqualTo(0);
        await Assert.That(scene.Diagnostics.StanceChangeRefusals).IsEqualTo(1L);
    }

    [Test]
    public async Task InvalidInputCannotStageAStanceAndOldBindingsCannotChangeAReenteredGeneration()
    {
        using var scene = new Fixture();
        scene.Services.ApplyDirectMovement(scene.Unit, new()
        {
            Pose = scene.Unit.Pose, Stance = 0, Velocity = new(float.NaN, 0, 0)
        }, 0);
        var binding = scene.Bindings.GetOrLiveCreate(scene.Unit)!;
        binding.Controller.Apply(new() { Flags = 8, Stance = 0, Delta = new(float.NaN, 0, 0) });
        await Assert.That(scene.Diagnostics.RejectedInputs).IsEqualTo(2L);
        await Assert.That(scene.Actor.LivingStances!.DesiredSlot).IsEqualTo(1);
        await Assert.That(scene.Backend.Attempts).IsEqualTo(0);

        var old = scene.Actor;
        var oldState = old.LivingStances;
        scene.Reenter();
        binding.Controller.Apply(new() { Flags = 8, Stance = 0 });
        await Assert.That(scene.Actor.Handle.Generation).IsGreaterThan(old.Handle.Generation);
        await Assert.That(scene.Actor.LivingStances!.CurrentSlot).IsEqualTo(1);
        await Assert.That(scene.Actor.LivingStances.DesiredSlot).IsEqualTo(1);
        await Assert.That(ReferenceEquals(oldState, scene.Actor.LivingStances)).IsFalse();
        await Assert.That(scene.Backend.Attempts).IsEqualTo(0);
        await Assert.That(scene.Diagnostics.SkippedActors).IsGreaterThan(0L);
    }

    [Test]
    public async Task FrameDynamicsUseAuthoredInertiaAndTheOriginalSelectedMovementFlagGate()
    {
        using var scene = new Fixture();
        // Native CActor starts flags+124 at zero, so an idle first frame still installs DB inertia.
        scene.Begin(1);
        scene.Finish();
        await Assert.That(scene.Backend.StepInertia).IsEqualTo(20f);

        scene.Begin(2, stance: 1, movementFlag: true);
        scene.Finish();
        await Assert.That(scene.Backend.StepInertia).IsEqualTo(0f);
        scene.Begin(3, stance: 1, movementFlag: false);
        scene.Finish();
        await Assert.That(scene.Backend.StepInertia).IsEqualTo(20f);
        await Assert.That(scene.Backend.InertiaUpdates).IsEqualTo(3);
        await Assert.That(scene.Backend.Attempts).IsEqualTo(0);
    }

    [Test]
    public async Task LivingFrameRequestsNormalWishWithoutFeedingActualVerticalVelocityBack()
    {
        var backend = new LivingRequestBackend();
        using var scene = new Fixture(testBackend: backend);
        backend.ApplyVelocity(scene.Actor.Handle, new(0, 0, -6));
        scene.Begin(1, stance: 1);
        var directWrites = backend.VelocityWrites;
        scene.PrePhase();
        await Assert.That(backend.Requests.Count).IsEqualTo(1);
        await Assert.That(backend.Requests[0].Actor).IsEqualTo(scene.Actor.Handle);
        await Assert.That(backend.Requests[0].Mode).IsEqualTo(ZonePhysicsLivingVelocityMode.Normal);
        await Assert.That(backend.Requests[0].Velocity.Z).IsEqualTo(0f);
        await Assert.That(MathF.Abs(backend.Requests[0].Velocity.Length() - 2.5f)).IsLessThan(.0001f);
        await Assert.That(backend.VelocityWrites).IsEqualTo(directWrites);
        backend.CopyStatus(scene.Actor.Handle, out var actual);
        await Assert.That(actual.Velocity.Z).IsEqualTo(-6f);
        scene.StepPhysics();
        scene.PostPhase();

        // A real nonzero requested Z survives admission; only actual falling/tangent Z is excluded.
        var binding = scene.Bindings.GetOrLiveCreate(scene.Unit)!;
        binding.Controller.Apply(new() { Delta = Vector3.UnitZ, MovementScalar = 1, Stance = -1 });
        await Assert.That(backend.Requests[^1].Velocity.Z).IsGreaterThan(0f);
        await Assert.That(MathF.Abs(backend.Requests[^1].Velocity.Length() - 5)).IsLessThan(.0001f);
        await Assert.That(backend.Requests[^1].Mode).IsEqualTo(ZonePhysicsLivingVelocityMode.Normal);
    }

    [Test]
    public async Task LivingRequestRefusalDoesNotFallBackAndInvalidOrRetiredInputCannotReachTheCapability()
    {
        var backend = new LivingRequestBackend { RefuseRequests = true };
        using var scene = new Fixture(testBackend: backend);
        scene.Begin(1, stance: 1);
        var directWrites = backend.VelocityWrites;
        scene.PrePhase();
        await Assert.That(backend.Requests.Count).IsEqualTo(1);
        await Assert.That(scene.Diagnostics.MotionApplicationRefusals).IsEqualTo(1L);
        await Assert.That(scene.Diagnostics.LastRefusalMessage!).Contains("ApplyLivingVelocityRequest");
        await Assert.That(backend.VelocityWrites).IsEqualTo(directWrites);
        scene.StepPhysics();
        scene.PostPhase();

        var old = scene.Bindings.GetOrLiveCreate(scene.Unit)!;
        old.Controller.Apply(new() { Delta = new(float.NaN, 0, 0), MovementScalar = 1 });
        await Assert.That(scene.Diagnostics.RejectedInputs).IsEqualTo(1L);
        await Assert.That(backend.Requests.Count).IsEqualTo(1);
        scene.Reenter();
        old.Controller.Apply(new() { Delta = Vector3.UnitX, MovementScalar = 1 });
        await Assert.That(backend.Requests.Count).IsEqualTo(1);
        await Assert.That(scene.Diagnostics.SkippedActors).IsGreaterThan(0L);
    }

    [Test]
    public async Task KinematicRegistrationKeepsGenericMotionEvenWhenBackendHasLivingRequestCapability()
    {
        var backend = new LivingRequestBackend();
        using var scene = new Fixture(testBackend: backend, profile: ZonePhysicsActorProfile.Kinematic);
        backend.ApplyVelocity(scene.Actor.Handle, new(0, 0, -6));
        scene.Begin(1, stance: 1);
        var directWrites = backend.VelocityWrites;
        scene.PrePhase();
        await Assert.That(backend.Requests.Count).IsEqualTo(0);
        await Assert.That(backend.VelocityWrites).IsGreaterThan(directWrites);
        backend.CopyStatus(scene.Actor.Handle, out var actual);
        await Assert.That(actual.Velocity.Z).IsEqualTo(-6f);
        await Assert.That(actual.Velocity.Length()).IsGreaterThan(6f);
        scene.StepPhysics();
        scene.PostPhase();
    }

    [Test]
    public async Task RealLivingFrameDrainsNormalWishSeparatelyFromFallingActualVelocity()
    {
        var backend = new ZoneManagedPhysicsBackend(new(new AAEmu.Zone.Terrain.ZoneTerrainHeightField()));
        using var scene = new Fixture(backend, gravity: 25);
        backend.ApplyVelocity(scene.Actor.Handle, new(0, 0, -2));
        scene.Begin(1, stance: 1, extraActorFlags: 4);
        scene.PrePhase();
        backend.CopyLivingControlState(scene.Actor.Handle, out var pending);
        await Assert.That(pending.HasPendingVelocityRequest).IsTrue();
        scene.StepPhysics();
        backend.CopyLivingControlState(scene.Actor.Handle, out var applied);
        await Assert.That(applied.HasPendingVelocityRequest).IsFalse();
        await Assert.That(applied.LastVelocityMode).IsEqualTo(ZonePhysicsLivingVelocityMode.Normal);
        await Assert.That(applied.RequestedVelocity.Z).IsEqualTo(0f);
        await Assert.That(MathF.Abs(applied.RequestedVelocity.Length() - 2.5f)).IsLessThan(.0001f);
        backend.CopyStatus(scene.Actor.Handle, out var status);
        await Assert.That(status.Velocity.Z).IsLessThan(-2f);
        await Assert.That(status.IsFlying).IsTrue();
        await Assert.That(status.CachedLivingGroundHeight).IsEqualTo((float?)-1e10f);
        await Assert.That(scene.Correction.GetLivingStatus().Native58).IsEqualTo(-1e10f);
        var expectedCorrectedZ = (scene.LastAdmitted.Velocity.Z - status.Velocity.Z) * .02f + status.Velocity.Z;
        scene.PostPhase();
        backend.CopyLivingControlState(scene.Actor.Handle, out var corrected);
        await Assert.That(corrected.LastVelocityMode).IsEqualTo(ZonePhysicsLivingVelocityMode.Normal);
        await Assert.That(corrected.LivingFlag2).IsFalse();
        await Assert.That(corrected.RequestedVelocity.X).IsEqualTo(scene.LastAdmitted.Velocity.X);
        // Native+C0 applies its filtered Z to both actual and requested fields, without a mode3 action.
        await Assert.That(corrected.RequestedVelocity.Z).IsEqualTo(expectedCorrectedZ);
        backend.CopyStatus(scene.Actor.Handle, out status);
        await Assert.That(status.Velocity.Z).IsEqualTo(expectedCorrectedZ);

        scene.Begin(2);
        scene.Finish();
        backend.CopyLivingControlState(scene.Actor.Handle, out corrected);
        await Assert.That(corrected.LastVelocityMode).IsEqualTo(ZonePhysicsLivingVelocityMode.Normal);
        await Assert.That(corrected.LivingFlag2).IsFalse();
    }

    [Test]
    public async Task LivingCorrectionPreservesTheFullTupleAndDoesNotDecomposeItIntoGenericMotion()
    {
        var backend = new CorrectionBackend();
        using var scene = new Fixture(testBackend: backend);
        var action = new ZoneActorMovementCorrectionAction(new(11, 12, 13), new(1, 2, 3), new(4, 5, 6),
            true, true, true, 7, 8);
        var velocityWrites = backend.VelocityWrites;
        var poseWrites = backend.PoseWrites;
        scene.Correction.Apply(action);
        await Assert.That(backend.Corrections.Count).IsEqualTo(1);
        await Assert.That(backend.Corrections[0].Actor).IsEqualTo(scene.Actor.Handle);
        await Assert.That(backend.Corrections[0].Action).IsEqualTo(action);
        await Assert.That(backend.VelocityWrites).IsEqualTo(velocityWrites);
        await Assert.That(backend.PoseWrites).IsEqualTo(poseWrites);

        var old = scene.Correction;
        scene.Reenter();
        await Assert.That(() => old.Apply(action)).Throws<ObjectDisposedException>();
        await Assert.That(backend.Corrections.Count).IsEqualTo(1);
    }

    [Test]
    public async Task LivingCorrectionRefusalHasNoGenericFallbackAndKinematicRegistrationDoesNotUseIt()
    {
        var backend = new CorrectionBackend { RefuseCorrections = true };
        using (var scene = new Fixture(testBackend: backend))
        {
            var action = new ZoneActorMovementCorrectionAction(Vector3.Zero, Vector3.One, null,
                false, false, false, 0, 0);
            scene.Correction.Apply(action);
            await Assert.That(scene.Diagnostics.MotionApplicationRefusals).IsEqualTo(1L);
            await Assert.That(scene.Diagnostics.LastRefusalMessage!).Contains("LivingCorrection");
            await Assert.That(backend.PoseWrites).IsEqualTo(0);
            await Assert.That(backend.VelocityWrites).IsEqualTo(0);
        }

        var generic = new CorrectionBackend();
        using var kinematic = new Fixture(testBackend: generic, profile: ZonePhysicsActorProfile.Kinematic);
        kinematic.Correction.Apply(new(Vector3.Zero, Vector3.One, null, false, false, false, 0, 0));
        await Assert.That(generic.Corrections.Count).IsEqualTo(0);
        await Assert.That(generic.PoseWrites).IsEqualTo(1);
        await Assert.That(generic.VelocityWrites).IsEqualTo(2);
    }

    [Test]
    public async Task LivingCorrectionQueuesItsVerticalTargetRatherThanDiscardingItAsAnOrdinaryPose()
    {
        var backend = new ZoneManagedPhysicsBackend(new(new AAEmu.Zone.Terrain.ZoneTerrainHeightField()));
        using var scene = new Fixture(backend);
        var target = scene.Actor.Position + Vector3.UnitZ;
        scene.Correction.Apply(new(target, Vector3.Zero, Vector3.Zero, false, false, true, 0, 0));
        backend.CopyStatus(scene.Actor.Handle, out var before);
        await Assert.That(before.Position).IsEqualTo(scene.Actor.Position);
        scene.Begin(1);
        scene.Finish();
        backend.CopyStatus(scene.Actor.Handle, out var after);
        await Assert.That(after.Position).IsEqualTo(target);
        backend.CopyLivingControlState(scene.Actor.Handle, out var control);
        await Assert.That(control.LastVelocityMode).IsEqualTo(ZonePhysicsLivingVelocityMode.Normal);
        await Assert.That(control.LivingFlag2).IsFalse();
    }

    [Test]
    public async Task CopiedBlockedGroundStatusWithoutARealFloorSelectsTheFilteredCorrectionBranch()
    {
        var backend = new CorrectionBackend();
        using var scene = new Fixture(testBackend: backend);
        backend.CopyStatus(scene.Actor.Handle, out var initial);
        // The correction consumer accepts the copied blocked-grounded/no-floor status shape.
        // Its actual miss-cache producer is covered by the real freefall control above.
        backend.SetCopiedStatus(scene.Actor.Handle, initial with
        {
            IsGrounded = true, IsFlying = false, Velocity = new(0, 0, 2), CachedLivingGroundHeight = -1e10f,
        });
        scene.Begin(1, stance: 1, moving: false, extraActorFlags: 4, verticalVelocity: 1000);
        scene.PrePhase();
        scene.StepPhysics();
        backend.CopyStatus(scene.Actor.Handle, out var status);
        await Assert.That(status.IsGrounded).IsTrue();
        await Assert.That(status.CachedLivingGroundHeight).IsEqualTo((float?)-1e10f);
        var copied = scene.Correction.GetLivingStatus();
        await Assert.That(copied.Native04).IsEqualTo(0);
        await Assert.That(copied.Native58).IsEqualTo(-1e10f);
        // Being blocked can report grounded without a real support plane. The cache sentinel
        // still selects the filtered branch even when actor flag4 requests raw grounded Z.
        var expected = (scene.LastAdmitted.Velocity.Z - status.Velocity.Z) * .02f + status.Velocity.Z;
        await Assert.That(MathF.Abs(expected - scene.LastAdmitted.Velocity.Z)).IsGreaterThan(.1f);
        scene.PostPhase();
        await Assert.That(backend.Corrections.Count).IsEqualTo(1);
        await Assert.That(backend.Corrections[0].Action.Velocity.Z).IsEqualTo(expected);
    }

    [Test]
    public async Task AuthoredZeroFallAngleDoesNotGroundAFlatSupportAtTheStrictCosineBoundary()
    {
        foreach (var angle in new[] { 0f, 50f })
        {
            var statics = new ZoneManagedStaticGeometry();
            var backend = new ZoneManagedPhysicsBackend(new(new AAEmu.Zone.Terrain.ZoneTerrainHeightField()), staticGeometry: statics);
            using var scene = new Fixture(backend, minimumFallAngle: angle);
            statics.AddInstance(new ZoneManagedTriangleMesh(
                [new(-10, -10, 0), new(10, -10, 0), new(10, 10, 0), new(-10, 10, 0)],
                [0, 1, 2, 0, 2, 3]), Matrix4x4.CreateTranslation(scene.Actor.Position));
            // A nonzero wish enters the native-style update instead of the idle shortcut.
            for (uint frame = 1; frame <= 3; frame++)
            {
                scene.Begin(frame, stance: frame == 1 ? (sbyte)1 : null);
                scene.Finish();
                backend.CopyStatus(scene.Actor.Handle, out var status);
                await Assert.That(status.IsGrounded).IsEqualTo(angle != 0);
                await Assert.That(status.IsFlying).IsEqualTo(angle == 0);
                await Assert.That(status.Position.Z).IsEqualTo(scene.Actor.Position.Z);
                await Assert.That(status.CachedLivingGroundHeight).IsEqualTo((float?)scene.Actor.Position.Z);
                await Assert.That(scene.Correction.GetLivingStatus().Native58).IsEqualTo(scene.Actor.Position.Z);
            }
        }
    }

    [Test]
    public async Task RealManagedFrameDrainsShrinkBeforeRecoveryAndRetriesBlockedStandingAfterCeilingRemoval()
    {
        var statics = new ZoneManagedStaticGeometry();
        var backend = new ZoneManagedPhysicsBackend(new(new AAEmu.Zone.Terrain.ZoneTerrainHeightField()), staticGeometry: statics);
        using var scene = new Fixture(backend);
        var start = scene.Actor.Position;
        statics.AddInstance(new ZoneManagedTriangleMesh(
            [new(-10, -10, 0), new(10, -10, 0), new(10, 10, 0), new(-10, 10, 0)],
            [0, 1, 2, 0, 2, 3]), Matrix4x4.CreateTranslation(start));
        var roof = statics.AddInstance(Roof(), Matrix4x4.CreateTranslation(start));
        var headProbe = start + new Vector3(.3f, 0, 1.5f);
        var probe = ZonePhysicsShape.Box(new(.005f));
        // This point is beside the narrow roof, so only the actor can answer the query.
        await Assert.That(statics.TryCollide(probe, headProbe)).IsFalse();
        await Assert.That(backend.TryCollide(probe, headProbe, null)).IsTrue();

        scene.Begin(1, stance: 6, moving: false);
        scene.PrePhase();
        await Assert.That(scene.Actor.LivingStances!.CurrentSlot).IsEqualTo(6);
        backend.CopyDescription(scene.Actor.Handle, out var pending);
        await Assert.That(pending.Shape.SegmentHalfHeight).IsEqualTo(.32f);
        await Assert.That(backend.TryReadLivingStanceUpdate(scene.Actor.Handle, out var queued)).IsTrue();
        await Assert.That(queued.Phase).IsEqualTo(ZoneManagedLivingStancePhase.Queued);
        await Assert.That(backend.TryCollide(probe, headProbe, null)).IsTrue();
        scene.StepPhysics();
        scene.PostPhase();
        backend.CopyDescription(scene.Actor.Handle, out var crouching);
        await Assert.That(crouching.Shape.SegmentHalfHeight).IsEqualTo(.1f);
        await Assert.That(backend.TryCollide(probe, headProbe, null)).IsFalse();
        backend.CopyStatus(scene.Actor.Handle, out var shrunk);
        // If old-body recovery ran before the queued shrink, the roof would eject this body.
        await Assert.That(Vector3.Distance(shrunk.Position, start)).IsLessThan(.001f);
        await Assert.That(scene.LastOutgoingStance()).IsEqualTo((sbyte?)6);

        scene.Begin(2, stance: 0, moving: false);
        scene.PrePhase();
        await Assert.That(scene.Actor.LivingStances.CurrentSlot).IsEqualTo(6);
        await Assert.That(scene.Actor.LivingStances.DesiredSlot).IsEqualTo(0);
        await Assert.That(backend.TryReadLivingStanceUpdate(scene.Actor.Handle, out queued)).IsTrue();
        await Assert.That(queued.PreflightAccepted).IsFalse();
        await Assert.That(queued.Phase).IsEqualTo(ZoneManagedLivingStancePhase.Queued);
        scene.StepPhysics();
        scene.PostPhase();
        backend.CopyDescription(scene.Actor.Handle, out var blocked);
        await Assert.That(blocked.Shape.SegmentHalfHeight).IsEqualTo(.1f);
        await Assert.That(backend.TryReadLivingStanceUpdate(scene.Actor.Handle, out var refused)).IsTrue();
        await Assert.That(refused.Phase).IsEqualTo(ZoneManagedLivingStancePhase.RefusedAtDrain);
        await Assert.That(scene.LastOutgoingStance()).IsNull();

        statics.RemoveInstance(roof);
        scene.Begin(3);
        scene.PrePhase();
        await Assert.That(scene.Actor.LivingStances.CurrentSlot).IsEqualTo(0);
        backend.CopyDescription(scene.Actor.Handle, out pending);
        await Assert.That(pending.Shape.SegmentHalfHeight).IsEqualTo(.1f);
        scene.StepPhysics();
        scene.PostPhase();
        backend.CopyDescription(scene.Actor.Handle, out var standing);
        await Assert.That(standing.Shape.SegmentHalfHeight).IsEqualTo(.6f);
        await Assert.That(backend.TryCollide(probe, headProbe, null)).IsTrue();
        await Assert.That(scene.LastOutgoingStance()).IsEqualTo((sbyte?)0);
    }

    private static ZoneManagedTriangleMesh Roof() => new(
        [new(-.1f, -5, 1.15f), new(.1f, -5, 1.15f), new(.1f, 5, 1.15f), new(-.1f, 5, 1.15f),
         new(-.1f, -5, 3.45f), new(.1f, -5, 3.45f), new(.1f, 5, 3.45f), new(-.1f, 5, 3.45f)],
        [0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4,
         1, 2, 6, 1, 6, 5, 2, 3, 7, 2, 7, 6, 3, 0, 4, 3, 4, 7], solidConvex: true);

    [Test]
    public async Task AcceptedKindOnlyRequestCanBeRefusedAtDrainWhileActorAndPhysicalStateStayDistinct()
    {
        var statics = new ZoneManagedStaticGeometry();
        var backend = new ZoneManagedPhysicsBackend(new(new AAEmu.Zone.Terrain.ZoneTerrainHeightField()), staticGeometry: statics);
        using var scene = new Fixture(backend, initialCylinder: true);
        statics.AddInstance(new ZoneManagedTriangleMesh(
            [new(.3f, -10, -10), new(.3f, 10, -10), new(.3f, 10, 10), new(.3f, -10, 10)],
            [0, 1, 2, 0, 2, 3]), Matrix4x4.CreateTranslation(scene.Actor.Position));
        // Explicitly malformed preexisting overlap, like the isolated original kind-only
        // controls. This verifies queue ownership, not normal clear admission or idle recovery.
        scene.Begin(1, stance: 2, moving: false);
        scene.PrePhase();
        await Assert.That(scene.Actor.LivingStances!.CurrentSlot).IsEqualTo(2);
        await Assert.That(scene.Actor.LivingStances.DesiredSlot).IsEqualTo(2);
        await Assert.That(backend.TryReadLivingStanceUpdate(scene.Actor.Handle, out var queued)).IsTrue();
        await Assert.That(queued.PreflightAccepted).IsTrue();
        await Assert.That(queued.Queries).IsEqualTo(0);
        backend.CopyDescription(scene.Actor.Handle, out var oldBody);
        await Assert.That(oldBody.Shape.Kind).IsEqualTo(ZonePhysicsShapeKind.Cylinder);

        scene.StepPhysics();
        scene.PostPhase();
        await Assert.That(backend.TryReadLivingStanceUpdate(scene.Actor.Handle, out var refused)).IsTrue();
        await Assert.That(refused.PreflightAccepted).IsTrue();
        await Assert.That(refused.Phase).IsEqualTo(ZoneManagedLivingStancePhase.RefusedAtDrain);
        backend.CopyDescription(scene.Actor.Handle, out var applied);
        await Assert.That(applied.Shape.Kind).IsEqualTo(ZonePhysicsShapeKind.Cylinder);
        await Assert.That(scene.Actor.LivingStances.Current.Physicalization.UseCapsule).IsTrue();
        await Assert.That(scene.LastOutgoingStance()).IsEqualTo((sbyte?)2);

        // CActor compares its accepted current/request slots. A refused physical drain does
        // not invent a different actor slot or automatically create another accepted request.
        scene.Begin(2);
        scene.Finish();
        await Assert.That(backend.TryReadLivingStanceUpdate(scene.Actor.Handle, out var retained)).IsTrue();
        await Assert.That(retained.Sequence).IsEqualTo(refused.Sequence);
        await Assert.That(scene.Actor.LivingStances.HasPendingChange).IsFalse();
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly ZoneActorMovementWorldOrigin Origin = new(9, 14);
        private readonly byte[] _body = Convert.FromHexString(UnitStateSnapshotTests.PlayerEnterBody);
        private readonly UnitStateSnapshot _snapshot;
        private readonly ZonePlayerActorFactory _actors;
        private readonly ZoneUnitSpatialIndex _spatial;
        private readonly ZoneMovementFrameClock _clock = new();
        private readonly ZoneMovementFramePhases _phases;
        private readonly IZonePhysicsBackend _physics;
        private readonly IZonePhysicsMotion _motion;
        private ZoneActorMovementController _controller = null!;
        private ZoneMovementFrame _frame;
        public StanceBackend Backend { get; }
        public ZoneUnitRegistry Registry { get; }
        public ZoneUnit Unit { get; private set; } = null!;
        public ZonePlayerActor Actor { get; private set; } = null!;
        public ZoneMovementDiagnostics Diagnostics { get; } = new();
        public ZoneMovementInputBindings Bindings { get; }
        public ZoneMovementServices Services { get; }
        public ZoneActorMovementState LastAdmitted => _controller.LastAdmitted!;
        public ZoneMovementCorrectionPhysics Correction => new(Actor, Unit, _motion, Diagnostics);

        private readonly ZonePhysicsActorProfile _profile;

        public Fixture(ZoneManagedPhysicsBackend? managedBackend = null, bool initialCylinder = false,
            StanceBackend? testBackend = null, ZonePhysicsActorProfile profile = ZonePhysicsActorProfile.Living,
            float gravity = 0, float minimumFallAngle = 70)
        {
            Backend = testBackend ?? new();
            _profile = profile;
            _physics = (IZonePhysicsBackend?)managedBackend ?? Backend;
            _snapshot = UnitStateSnapshot.Read(_body);
            var models = new ZoneModelContent([new() { Id = _snapshot.ModelId, Subtype = "ActorModel", SubtypeId = 1 }],
                actorModels: new ZoneActorModelContent([new() { Id = 1, PhysicsLivingMass = 80, PhysicsLivingGravity = gravity, TurnSpeed = 180, GameInertia = 20,
                    PhysicsLivingMinSlideAngle = 36, PhysicsLivingMaxClimbAngle = 54, PhysicsLivingMinFallAngle = minimumFallAngle }],
                [Stance(1, 1, .6f, 1.4f, 7), Stance(2, 2, .32f, 1.2f, 5) with { UseCapsule = !initialCylinder },
                 Stance(3, 7, .1f, .6f, 2), Stance(4, 3, .32f, 1.2f, 5)]));
            _actors = new(models, worldOrigin: Origin);
            _spatial = new(64, new(9, 14, 11, 15));
            Registry = new(playerActors: _actors, spatialIndex: _spatial,
                modelPosition: unit => ZoneMovementPoseMath.ToMeters(unit.Pose.Position));
            var motion = new ZoneOwnedMovementMotion(_physics, (IZonePhysicsMotion?)managedBackend ?? Backend);
            _motion = motion;
            Bindings = new(_clock, _actors, motion, Diagnostics);
            Services = new(_clock, motion, Bindings, Diagnostics);
            _phases = new(Registry, _physics, _actors,
                new ZoneMovementInputServices(Registry, Diagnostics, Bindings),
                new ZoneMovementCorrectionServices(Registry, _actors, motion, _clock, Diagnostics, Origin),
                new ZoneMovementOutgoing(Registry, Diagnostics, localOnWire: false),
                _clock, Diagnostics, Bindings, Origin);
            Admit();
        }

        private void Admit()
        {
            Registry.Create(_body);
            Unit = Registry.Units[_snapshot.ObjectId];
            Actor = _actors.FindByUnitObjectId(_snapshot.ObjectId)!;
            var initial = ZoneActorPhysicalizationRequest.BuildInitial(Actor.Model, Actor.Scale, false, _ => null);
            var description = ZonePhysicsActorDescription.FromLivingRequest(initial);
            if (_profile == ZonePhysicsActorProfile.Kinematic)
                description = description with { Profile = _profile, LivingMovement = null };
            _physics.AddActor(Actor.Handle, description);
            _physics.Teleport(Actor.Handle, Actor.Position, Actor.Orientation);
            Actor.BindPhysicsAuthority(_physics, description);
            _controller = new(Registry, Unit, Services);
        }

        public void Begin(uint frame, sbyte? stance = null, bool moving = true, bool movementFlag = true,
            ushort extraActorFlags = 0, short verticalVelocity = 0)
        {
            _frame = new(frame, TimeSpan.FromMilliseconds(20), 1000 + frame * 400, 1 + frame * .4f);
            _clock.SetFrame(_frame);
            if (stance is not null)
                _controller.Receive(new(1, _frame.ServerMilliseconds, 4, 0, 0, _snapshot.Position,
                    moving ? new(5000, 0, verticalVelocity) : new(0, 0, verticalVelocity), default,
                    new UnitActorMovement(moving ? new(127, 0, 0) : default, stance.Value, 0,
                        (ushort)((movementFlag ? 1 : 0) | extraActorFlags))));
        }

        public void Finish()
        {
            PrePhase();
            StepPhysics();
            PostPhase();
        }

        public void PrePhase() => _phases.PrePhase(_frame);
        public void StepPhysics() => _phases.StepPhysics(_frame.Elapsed);
        public void PostPhase() => _phases.PostPhase(_frame);

        public sbyte? LastOutgoingStance()
        {
            sbyte? result = null;
            while (Registry.TryDequeueNotification(out var notification))
            {
                if (notification.Opcode != ZwOpcodes.UnitMovements) continue;
                if (BinaryPrimitives.ReadInt32LittleEndian(notification.Body.Span) != 1)
                    throw new InvalidDataException("The single-actor stance fixture emitted an unexpected batch.");
                result = ((UnitActorMovement)UnitMovement.Read(notification.Body.Span[4..]).Movement.Details).Stance;
            }
            return result;
        }

        public void Reenter()
        {
            _physics.RemoveActor(Actor.Handle);
            _actors.Retire(Actor.Handle);
            Admit();
        }

        public void Dispose()
        {
            _actors.RetireAll(_physics);
            _spatial.Dispose();
        }

        private static ZoneGameStanceDefinition Stance(uint id, int stanceId, float halfHeight, float colliderHeight, float speed) => new()
        {
            Id = id, ActorModelId = 1, StanceId = stanceId,
            SizeX = .4f, SizeY = .4f, SizeZ = halfHeight, HeightCollider = colliderHeight,
            UseCapsule = true, MaxSpeed = speed, NormalSpeed = speed
        };
    }

    // This isolates the neutral capability contract from the native/managed resize solver.
    // The real receive, prephase, physics-step and outgoing owners remain composed above.
    private class StanceBackend : IZonePhysicsBackend, IZonePhysicsMotion, IZonePhysicsLivingStances, IZonePhysicsLivingDynamics
    {
        private readonly Dictionary<ZoneActorHandle, ZonePhysicsActorStatus> _actors = [];
        public bool AcceptChanges { get; set; } = true;
        public int Attempts { get; private set; }
        public List<string> Events { get; } = [];
        public ZoneActorPhysicalizationStance? AcceptedStance { get; private set; }
        public Vector3 StepVelocity { get; private set; }
        public float Inertia { get; private set; } = 8;
        public float StepInertia { get; private set; }
        public int InertiaUpdates { get; private set; }
        public int VelocityWrites { get; private set; }
        public int PoseWrites { get; private set; }
        public void AddActor(ZoneActorHandle actor, ZonePhysicsActorDescription description) =>
            _actors.Add(actor, new(default, Quaternion.Identity, default, false, true));
        public void RemoveActor(ZoneActorHandle actor) => _actors.Remove(actor);
        public void Teleport(ZoneActorHandle actor, Vector3 position, Quaternion orientation) =>
            _actors[actor] = new(position, orientation, default, false, true);
        public void CopyStatus(ZoneActorHandle actor, out ZonePhysicsActorStatus status) => status = _actors[actor];
        public void SetCopiedStatus(ZoneActorHandle actor, ZonePhysicsActorStatus status)
        {
            _ = _actors[actor];
            _actors[actor] = status;
        }
        public bool TryChangeLivingStance(ZoneActorHandle actor, ZoneActorPhysicalizationStance stance, out string refusal)
        {
            _ = _actors[actor];
            Events.Add("stance"); Attempts++;
            refusal = AcceptChanges ? string.Empty : "Authored standing dimensions are blocked by the fixture ceiling.";
            if (AcceptChanges) AcceptedStance = stance;
            return AcceptChanges;
        }
        public void SetLivingInertia(ZoneActorHandle actor, float inertia)
        {
            _ = _actors[actor];
            Events.Add("inertia"); InertiaUpdates++; Inertia = inertia;
        }
        public void SetLivingCollisionActive(ZoneActorHandle actor, bool active) =>
            throw new NotSupportedException("This input-order fixture only supplies inertia dynamics.");
        public void ApplyVelocity(ZoneActorHandle actor, Vector3 velocity)
        {
            VelocityWrites++;
            _actors[actor] = _actors[actor] with { Velocity = velocity };
        }
        public void ApplyImpulse(ZoneActorHandle actor, Vector3 impulse) =>
            _actors[actor] = _actors[actor] with { Velocity = _actors[actor].Velocity + impulse };
        public void ApplyPose(ZoneActorHandle actor, Vector3 position, Quaternion orientation)
        {
            PoseWrites++;
            _actors[actor] = _actors[actor] with { Position = position, Orientation = orientation, Velocity = default };
        }
        public void Step(TimeSpan elapsed)
        {
            Events.Add("step");
            foreach (var actor in _actors.Keys.ToArray())
            {
                var body = _actors[actor]; StepVelocity = body.Velocity;
                StepInertia = Inertia;
                _actors[actor] = body with { Position = body.Position + body.Velocity * (float)elapsed.TotalSeconds };
            }
        }
        public bool TrySweep(ZonePhysicsShape shape, Vector3 origin, Vector3 displacement, ZoneActorHandle? ignoreActor, out ZonePhysicsHit hit)
        { hit = default; return false; }
        public bool TryCollide(ZonePhysicsShape shape, Vector3 position, ZoneActorHandle? ignoreActor) => false;
        public bool TryGround(Vector3 origin, float maxDistance, out ZonePhysicsGroundHit ground)
        { ground = default; return false; }
    }

    private sealed class LivingRequestBackend : StanceBackend, IZonePhysicsLivingMovementRequests
    {
        public List<(ZoneActorHandle Actor, Vector3 Velocity, ZonePhysicsLivingVelocityMode Mode)> Requests { get; } = [];
        public bool RefuseRequests { get; init; }
        public void ApplyLivingVelocityRequest(ZoneActorHandle actor, Vector3 requestedVelocity,
            ZonePhysicsLivingVelocityMode mode = ZonePhysicsLivingVelocityMode.Normal)
        {
            CopyStatus(actor, out _);
            Requests.Add((actor, requestedVelocity, mode));
            if (RefuseRequests) throw new NotSupportedException("The fixture refuses this living request.");
        }
    }

    private sealed class CorrectionBackend : StanceBackend, IZonePhysicsLivingCorrections
    {
        public List<(ZoneActorHandle Actor, ZoneActorMovementCorrectionAction Action)> Corrections { get; } = [];
        public bool RefuseCorrections { get; init; }
        public bool TryApplyLivingCorrection(ZoneActorHandle actor, ZoneActorMovementCorrectionAction correction,
            out string refusal)
        {
            CopyStatus(actor, out _);
            Corrections.Add((actor, correction));
            refusal = RefuseCorrections ? "The fixture refuses its copied network correction." : string.Empty;
            return !RefuseCorrections;
        }
    }
}
