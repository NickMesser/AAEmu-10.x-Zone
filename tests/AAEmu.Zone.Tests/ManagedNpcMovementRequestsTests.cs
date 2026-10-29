using System.Numerics;
using AAEmu.Zone.Hosting;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Protocol.Units;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Scripting;

namespace AAEmu.Zone.Tests;

/// <summary>Copied original proxy output crosses the actual composed NPC frame and wire owners.</summary>
public sealed class ManagedNpcMovementRequestsTests
{
    [Test]
    public async Task ProxyReadsThePreviousWalkingNormalSpeedBeforeTheCurrentSetterChangesThePhysicalRequest()
    {
        using var world = new ManagedNpcPositionChecksTests.World(movementRequests: true, movementNormalSpeed: 6,
            movementWalkMultiplier: .5f);
        Activate(world);
        var requests = world.Fixture.MovementRequests!;
        var walkingOutput = Forward() with { MovementUrgency = .3f };
        requests.SubmitProxyOutput(world.Actor, walkingOutput);
        await Near(requests.Find(world.Actor.Handle)!.Request!.Delta, new(0, .5f, 0));
        world.StepFrame(2);
        //224 reads old walking=false (normal6), then256 sets walking=true. The
        //current derived actor consumes new normal3, so its first wish is1.5.
        await Near(requests.Find(world.Actor.Handle)!.LastVelocity, new(0, 1.5f, 0));
        requests.SubmitProxyOutput(world.Actor, walkingOutput);
        await Near(requests.Find(world.Actor.Handle)!.Request!.Delta, Vector3.UnitY);
        world.StepFrame(3);
        await Near(requests.Find(world.Actor.Handle)!.LastVelocity, new(0, 3, 0));
        await Assert.That(world.Motion.CopyReplicationState(world.Actor).ActorFlags & 1).IsEqualTo(1);
    }

    [Test]
    public async Task WalkingLatchOverridesPositiveInertiaAfterAnimatedRestorationWithoutChangingAcceleration()
    {
        using var world = new ManagedNpcPositionChecksTests.World(movementRequests: true, movementNormalSpeed: 6);
        Activate(world);
        var requests = world.Fixture.MovementRequests!;
        requests.SubmitProxyOutput(world.Actor, Forward() with { MovementUrgency = .3f });
        world.StepFrame(2);
        await Assert.That(requests.Find(world.Actor.Handle)!.Walking).IsTrue();
        await Assert.That(world.Body.CopyState().AppliedDescription.LivingMovement!.Inertia).IsEqualTo(0f);
        await Assert.That(world.Body.CopyState().AppliedDescription.LivingMovement!.InertiaAcceleration).IsEqualTo(11f);
        //30518020 does not invoke the walking setter in its nonmoving branch. Its
        //actual old latch survives even when the actor is given a zero movement wish.
        requests.SubmitProxyOutput(world.Actor, Forward() with { MoveDirection = Vector3.Zero });
        world.StepFrame(3);
        await Assert.That(requests.Find(world.Actor.Handle)!.Walking).IsTrue();
        await Assert.That(world.Body.CopyState().AppliedDescription.LivingMovement!.Inertia).IsEqualTo(0f);
        requests.SubmitProxyOutput(world.Actor, Forward());
        world.StepFrame(4);
        await Assert.That(requests.Find(world.Actor.Handle)!.Walking).IsFalse();
        await Assert.That(world.Body.CopyState().AppliedDescription.LivingMovement!.Inertia).IsEqualTo(20f);
        await Assert.That(world.Body.CopyState().AppliedDescription.LivingMovement!.InertiaAcceleration).IsEqualTo(11f);
    }

    [Test]
    public async Task CachedAiPhysicsStartsAtZeroThenCopiesStatusVectorsAndPreservesThemAcrossRelocationAndInactiveDynamics()
    {
        using var world = new ManagedNpcPositionChecksTests.World(movementRequests: true, movementNormalSpeed: 6, stepInitial: false);
        await Assert.That(world.Motion.CopyAiPhysicsState(world.Actor)).IsEqualTo(default(ZoneManagedNpcAiPhysicsState));
        world.StepFrame(1);
        var initial = world.Motion.CopyAiPhysicsState(world.Actor);
        await Assert.That(initial.Gravity).IsEqualTo(new Vector3(0, 0, -25));
        await Assert.That(initial.Velocity).IsEqualTo(world.Body.CopyState().Status.Velocity);
        Activate(world);
        world.Fixture.MovementRequests!.SubmitProxyOutput(world.Actor, Forward());
        world.StepFrame(2);
        var moving = world.Motion.CopyAiPhysicsState(world.Actor);
        await Assert.That(moving.Velocity.Y).IsGreaterThan(0f);
        await Assert.That(moving.Gravity).IsEqualTo(initial.Gravity);
        await Assert.That(moving.Gravity).IsNotEqualTo(moving.Velocity);
        world.RelocateForSetup(world.Position + new Vector3(2, 0, 0));
        await Assert.That(world.Motion.CopyAiPhysicsState(world.Actor)).IsEqualTo(moving);
        //The original inactive path skips392E0C80. CActor228 becomes zero while
        //the independent AIObject16/28 caches retain their last actual source copy.
        world.Fixture.Components.SetColliderMode(world.Actor, 1);
        world.StepFrame(3);
        await Assert.That(world.Body.CopyState().AppliedDescription.CollisionActive).IsFalse();
        await Assert.That(world.Motion.CopyAiPhysicsState(world.Actor)).IsEqualTo(moving);
        await Assert.That(world.Motion.CopyReplicationState(world.Actor).ActorVelocity).IsEqualTo(Vector3.Zero);
        var writer = new ZoneWriter(); writer.WriteBc(world.Actor.Npc.ObjectId);
        world.Fixture.Admission.Registry.Remove(writer.ToArray());
        await Assert.That(() => world.Motion.CopyAiPhysicsState(world.Actor)).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task ActualProxyRequestReachesOneNormalLivingStepAndThePublishedMovementRow()
    {
        using var world = new ManagedNpcPositionChecksTests.World(movementRequests: true, movementNormalSpeed: 6);
        Activate(world);
        var requests = world.Fixture.MovementRequests!;
        var owner = requests.Find(world.Actor.Handle)!;
        var before = world.Body.CopyState();
        await Assert.That(requests.SubmitProxyOutput(world.Actor, Forward())).IsTrue();
        //A CAIProxy update changes the cached actor request, never the applied body pose.
        await Assert.That(world.Body.CopyState().Status).IsEqualTo(before.Status);
        await Near(owner.Request!.Delta, new(0, .5f, 0));
        await Assert.That(owner.PreparedFrameCount).IsEqualTo(0ul);
        world.StepFrame();
        var first = world.Body.CopyState();
        //Native grounded inertia relaxes actual velocity after this frame's position
        //integration. The second ordinary frame then travels with that copied velocity.
        await Assert.That(first.Status.Position).IsEqualTo(before.Status.Position);
        await Assert.That(first.Status.Velocity.Y).IsGreaterThan(0f);
        world.StepFrame(3);
        var after = world.Body.CopyState();
        await Assert.That(after.Control.LastVelocityMode).IsEqualTo(ZonePhysicsLivingVelocityMode.Normal);
        await Assert.That(after.Control.LivingFlag2).IsFalse();
        await Assert.That(after.Control.HasPendingVelocityRequest).IsFalse();
        await Assert.That(after.Status.Position.Y).IsGreaterThan(before.Status.Position.Y);
        await Near(owner.LastVelocity, new(0, 3, 0));
        await Assert.That(owner.PreparedFrameCount).IsEqualTo(2ul);
        await world.AssertPublished(after.Status.Position);
        var sample = world.Motion.CopyReplicationState(world.Actor);
        await Assert.That(sample.ActorVelocity).IsEqualTo(after.Status.Velocity);
        await Near(sample.Delta, new(0, .5f, 0));
        await Assert.That(sample.Stance).IsEqualTo((sbyte)1);
        world.Replication.Reset(world.Actor);
        await Assert.That(world.Replication.Sample(world.Actor, world.Fixture.CurrentFrame)).IsTrue();
        await Assert.That(world.Replication.Flush()).IsEqualTo(1);
        var frame = world.Drain().Single(value => value.Opcode == ZwOpcodes.UnitMovements);
        var reader = new ZoneReader(frame.Body.Span);
        var count = reader.ReadInt32();
        var row = UnitMovement.Read(reader.ReadBytes(reader.Remaining));
        await Assert.That(count).IsEqualTo(1);
        var detail = (UnitActorMovement)row.Movement.Details;
        await Assert.That(row.ObjectId).IsEqualTo(world.Actor.Npc.ObjectId);
        await Assert.That(detail.Delta).IsEqualTo(UnitMovementQuantization.ActorDelta(sample.Delta));
        await Assert.That(detail.Stance).IsEqualTo(sample.Stance);
        await Assert.That(row.Movement.Time).IsEqualTo(world.Fixture.CurrentFrame.UnpausedMilliseconds!.Value);
        await Assert.That(row.Movement.Velocity).IsEqualTo(UnitMovementQuantization.ActorVelocity(sample.ActorVelocity));
    }

    [Test]
    public async Task HorizontalActorWishPreservesTheActualFallingVelocityAndNeverFeedsItBackAsWishZ()
    {
        using var world = new ManagedNpcPositionChecksTests.World(movementRequests: true, movementNormalSpeed: 6);
        Activate(world);
        world.RelocateForSetup(world.Position + new Vector3(0, 0, 5));
        world.StepFrame(2);
        var before = world.Body.CopyState();
        await Assert.That(before.Status.Velocity.Z).IsLessThan(0f);
        world.Fixture.MovementRequests!.SubmitProxyOutput(world.Actor, Forward());
        world.StepFrame(3);
        var after = world.Body.CopyState();
        await Assert.That(after.Control.RequestedVelocity.Z).IsEqualTo(0f);
        await Assert.That(after.Control.LastVelocityMode).IsEqualTo(ZonePhysicsLivingVelocityMode.Normal);
        await Assert.That(after.Control.LivingFlag2).IsFalse();
        await Assert.That(after.Status.Velocity.Z).IsLessThan(before.Status.Velocity.Z);
        await Assert.That(after.Status.Position.Z).IsLessThan(before.Status.Position.Z);
        await world.AssertPublished(after.Status.Position);
    }

    [Test]
    public async Task EntityRotationAndActorRequestUseTheNativeRotationThenVelocityOrder()
    {
        using var world = new ManagedNpcPositionChecksTests.World(movementRequests: true, movementNormalSpeed: 6);
        Activate(world);
        var before = world.Body.CopyState();
        world.Fixture.MovementRequests!.SubmitProxyOutput(world.Actor, Forward() with { MoveDirection = Vector3.UnitX });
        world.StepFrame();
        var owner = world.Fixture.MovementRequests.Find(world.Actor.Handle)!;
        var after = world.Body.CopyState();
        //The complete original derived pipeline controls establish this order: turning
        //the actor occurs before its cached local delta becomes a physical world wish.
        await Near(owner.LastVelocity, new(0, -3, 0), .00001f);
        var expected = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI * .5f);
        await Assert.That(MathF.Abs(Quaternion.Dot(after.Status.Orientation, expected))).IsGreaterThan(.999999f);
        await Assert.That(after.Status.Position).IsEqualTo(before.Status.Position);
        await Assert.That(after.Status.Velocity.Y).IsLessThan(0f);
        var model = world.Fixture.Components.CopyModelTransform(world.Actor.Npc.Unit);
        await Assert.That(model.Orientation).IsEqualTo(after.Status.Orientation);
        await Assert.That(world.Actor.Npc.Unit.Pose.Rotation).IsEqualTo(after.Status.Orientation);
        await world.AssertPublished(after.Status.Position);
    }

    [Test]
    public async Task InactiveEntityDoesNotAdmitProxyOutputAndRetiredRequestsCannotReachAReplacementGeneration()
    {
        using var world = new ManagedNpcPositionChecksTests.World(movementRequests: true, movementNormalSpeed: 6);
        var requests = world.Fixture.MovementRequests!;
        var old = world.Actor;
        var owner = requests.Find(old.Handle)!;
        await Assert.That(requests.SubmitProxyOutput(old, Forward())).IsFalse();
        await Assert.That(owner.Request).IsNull();
        Activate(world);
        requests.SubmitProxyOutput(old, Forward());
        world.Body.PrepareFrame();
        requests.PrepareFrame(old, new(2, TimeSpan.FromMilliseconds(20), 400040, .04f));
        await Assert.That(world.Body.CopyState().Control.HasPendingVelocityRequest).IsTrue();
        var writer = new ZoneWriter(); writer.WriteBc(old.Npc.ObjectId);
        await Assert.That(world.Fixture.Admission.Registry.Remove(writer.ToArray())).IsTrue();
        await Assert.That(owner.IsDisposed).IsTrue();
        await Assert.That(requests.Count).IsEqualTo(0);
        await Assert.That(() => requests.SubmitProxyOutput(old, Forward())).Throws<ObjectDisposedException>();
        var npc = world.Fixture.Admission.CreateFromWire(world.Fixture.Model.Id, new(1100, 1100, 10.1f));
        var handle = world.Fixture.Admission.Catalog.TryGet(npc)!.Value;
        await Assert.That(handle.UnitObjectId).IsEqualTo(old.Handle.UnitObjectId);
        await Assert.That(handle.Generation).IsGreaterThan(old.Handle.Generation);
        var replacement = requests.Find(handle)!;
        await Assert.That(replacement.Request).IsNull();
        await Assert.That(replacement.ProxyUpdateCount).IsEqualTo(0ul);
        await Assert.That(replacement.PreparedFrameCount).IsEqualTo(0ul);
        var replacementBody = world.Fixture.Bodies!.Find(handle)!;
        await Assert.That(replacementBody.CopyState().Control.HasPendingVelocityRequest).IsFalse();
        world.Fixture.Backend!.Step(TimeSpan.FromMilliseconds(20));
        await Assert.That(replacementBody.CopyState().Control.RequestedVelocity).IsEqualTo(Vector3.Zero);
        await Assert.That(replacementBody.CopyState().Status.Velocity.Y).IsEqualTo(0f);
    }

    [Test]
    public async Task UnownedNonidentityTurnSettingsRetireOnlyThroughTheRuntimeBoundaryBeforePhysicalMovement()
    {
        using var world = new ManagedNpcPositionChecksTests.World(movementRequests: true, movementNormalSpeed: 6,
            movementCVars: ZoneEngineCVars.Parse("ca_GameControlledStrafing=1\nac_enableProceduralLeaning=0\npl_curvingSlowdownSpeedScale=.5"));
        Activate(world);
        var owner = world.Fixture.MovementRequests!.Find(world.Actor.Handle)!;
        world.Fixture.MovementRequests.SubmitProxyOutput(world.Actor, Forward());
        var before = world.Actor.Npc.Unit.Pose;
        await Assert.That(() => world.StepFrame()).Throws<InvalidOperationException>();
        await Assert.That(world.Actor.Npc.Unit.Pose).IsEqualTo(before);
        await Assert.That(owner.IsDisposed).IsTrue();
        await Assert.That(world.Fixture.Runtime.Count).IsEqualTo(0);
        await Assert.That(world.Fixture.Admission.Provider.Count).IsEqualTo(0);
        await Assert.That(world.Fixture.Bodies!.Count).IsEqualTo(0);
        await Assert.That(world.Fixture.Admission.Catalog.GetAdmission(world.Actor.Handle)!.Refusal!
            .Contains("Nonidentity NPC curving slowdown", StringComparison.Ordinal)).IsTrue();
    }

    private static ZoneManagedNpcProxyMovementOutput Forward() =>
        new(1, default, false, default, Vector3.UnitY, 3, 1, false, 4, 0);

    private static void Activate(ManagedNpcPositionChecksTests.World world)
    {
        world.Fixture.Runtime.Actors.GameObjectActivationProfile = ZoneManagedNpcGameObjectActivationProfile.MultiplayerWorldServer;
        world.Actor.Npc.Activation.SetActive(true, world.Fixture.Admission.World.GetActivationServices(world.Actor.Npc));
        if (!world.Actor.UpdateProxyActivationPolicy()) throw new InvalidOperationException("Actual proxy activation refused its NPC.");
    }

    private static async Task Near(Vector3 actual, Vector3 expected, float tolerance = .0001f) =>
        await Assert.That(Vector3.Distance(actual, expected)).IsLessThan(tolerance);
}
