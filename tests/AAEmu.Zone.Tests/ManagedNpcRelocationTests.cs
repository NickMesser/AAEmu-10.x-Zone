using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Scripting;

namespace AAEmu.Zone.Tests;

public sealed class ManagedNpcRelocationTests
{
    [Test]
    public async Task ActualResetPrecedesTheSolePublisherWithoutSteppingOrChangingRotationScaleAndDynamics()
    {
        using var world = new World(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .7f));
        world.Body.RequestVelocity(new(3, 2, 1), ZonePhysicsLivingVelocityMode.Immediate);
        world.Physics.Backend.Step(TimeSpan.FromMilliseconds(20));
        world.Physics.Backend.SetLivingInertia(world.Body.Actor, 0);
        world.Physics.Backend.ApplyImpulse(world.Body.Actor, Vector3.Zero);
        var previous = world.Body.CopyState(); var originalUnitPose = world.Actor.Npc.Unit.Pose;
        var originalAiPosition = world.Actor.AiObject.Position;
        var destination = new Vector3(1110, 1090, 900);
        var callbackCount = 0; var physicalResetWasVisible = false; var unitWasUnchanged = false;
        var result = world.Relocations.Relocate(world.Actor, destination, copied =>
        {
            callbackCount++;
            physicalResetWasVisible = world.Body.CopyState().Status.Position == destination &&
                world.Body.CopyState().Status.Velocity == Vector3.Zero && copied.Current.Control.RequestedVelocity == Vector3.Zero;
            unitWasUnchanged = world.Actor.Npc.Unit.Pose == originalUnitPose;
            world.Actor.Npc.Unit.CommitPose(new(ZoneMovementPoseMath.ToUnitWorldPosition(copied.Current.Status.Position), originalUnitPose.Rotation));
        });
        await Assert.That(callbackCount).IsEqualTo(1);
        await Assert.That(physicalResetWasVisible).IsTrue(); await Assert.That(unitWasUnchanged).IsTrue();
        await Assert.That(result.Previous).IsEqualTo(previous);
        await Assert.That(result.Current.Status.Position).IsEqualTo(destination);
        await Assert.That(result.Current.Status.Velocity).IsEqualTo(Vector3.Zero);
        await Assert.That(result.Current.Status.IsFlying).IsTrue();
        await Assert.That(result.Current.Status.Orientation).IsEqualTo(previous.Status.Orientation);
        await Assert.That(result.Current.AppliedDescription).IsEqualTo(previous.AppliedDescription);
        await Assert.That(result.EntityScale).IsEqualTo(previous.EntityScale);
        await Assert.That(result.Current.Control.ImpulseRecoveryTimeRemaining).IsEqualTo(5f);
        await Assert.That(result.Current.Control.CachedGroundHeight).IsEqualTo(previous.Control.CachedGroundHeight);
        await Assert.That(result.Current.Control.CachedGroundNormal).IsEqualTo(previous.Control.CachedGroundNormal);
        await Assert.That(result.Current.Control.LastVelocityMode).IsEqualTo(ZonePhysicsLivingVelocityMode.Immediate);
        await Assert.That(result.Current.Control.LivingFlag2).IsTrue();
        await Assert.That(result.Current.Control.SuspensionOffset).IsEqualTo(0f);
        await Assert.That(result.Current.Control.SuspensionVelocity).IsEqualTo(0f);
        await Assert.That(result.Current.Control.SuspensionAcceleration).IsEqualTo(previous.Control.SuspensionAcceleration);
        await Assert.That(result.Current.Control.SuspensionTime).IsEqualTo(1f);
        await Assert.That(world.Actor.Npc.Unit.Pose.Rotation).IsEqualTo(originalUnitPose.Rotation);
        await Assert.That(ZoneMovementPoseMath.ToMeters(world.Actor.Npc.Unit.Pose.Position)).IsEqualTo(destination);
        await Assert.That(world.Actor.AiObject.Position).IsEqualTo(originalAiPosition);
    }

    [Test]
    public async Task ActionTenRetainsTheModeThreeOverrideButCannotReplayItsOldWish()
    {
        using var world = new World();
        world.Body.RequestVelocity(new(9, 2, 3), ZonePhysicsLivingVelocityMode.Immediate);
        var result = world.Relocations.Relocate(world.Actor, new(1110, 1100, 1000), _ => { });
        await Assert.That(result.FlushedCommands).IsEqualTo(1);
        await Assert.That(result.Current.Control.RequestedVelocity).IsEqualTo(Vector3.Zero);
        await Assert.That(result.Current.Control.HasPendingVelocityRequest).IsFalse();
        await Assert.That(result.Current.Control.ForceRequestedVelocityOnce).IsTrue();
        await Assert.That(result.Current.Control.LivingFlag2).IsTrue();
        world.Physics.Backend.Step(TimeSpan.FromMilliseconds(20));
        var next = world.Body.CopyState();
        await Assert.That(next.Status.Position.X).IsEqualTo(1110f);
        await Assert.That(next.Status.Position.Y).IsEqualTo(1100f);
        await Assert.That(next.Control.ForceRequestedVelocityOnce).IsFalse();
        await Assert.That(next.Control.LivingFlag2).IsTrue();
    }

    [Test]
    public async Task RealGroundContactClearsWhileNativeFloorAndNormalCachesSurvive()
    {
        using var world = new World(position: new(1100, 1100, 10.1f));
        world.Physics.Backend.Step(TimeSpan.FromMilliseconds(20));
        var before = world.Body.CopyState();
        await Assert.That(before.Status.IsGrounded).IsTrue();
        await Assert.That(world.Physics.Backend.GroundMaterialFor(world.Body.Actor)).IsNotNull();
        var moved = world.Relocations.Relocate(world.Actor, new(1110, 1100, 1000), _ => { });
        await Assert.That(moved.Current.Status.IsGrounded).IsFalse();
        await Assert.That(world.Physics.Backend.GroundMaterialFor(world.Body.Actor)).IsNull();
        await Assert.That(moved.Current.Control.CachedGroundHeight).IsEqualTo(before.Control.CachedGroundHeight);
        await Assert.That(moved.Current.Control.CachedGroundNormal).IsEqualTo(before.Control.CachedGroundNormal);
    }

    [Test]
    public async Task EarlierStanceAndCorrectionCommandsFlushInTheirCallOrderRatherThanByCommandType()
    {
        foreach (var correctionFirst in new[] { false, true })
        {
            using var world = new World();
            world.Physics.Statics.AddInstance(new ZoneManagedTriangleMesh(
                [new(1098, 1098, 1002.5f), new(1102, 1098, 1002.5f), new(1102, 1102, 1002.5f), new(1098, 1102, 1002.5f)],
                [0, 1, 2, 0, 2, 3]), Matrix4x4.Identity);
            if (correctionFirst) Correction();
            world.Body.RequestStanceSlot(0); world.Body.PrepareFrame();
            await Assert.That(world.Body.CopyState().CurrentStanceSlot).IsEqualTo(1);
            if (!correctionFirst) Correction();
            var result = world.Relocations.Relocate(world.Actor, new(1110, 1100, 990), _ => { });
            await Assert.That(result.FlushedCommands).IsEqualTo(2);
            await Assert.That(result.Current.AppliedDescription.Shape.Radius).IsEqualTo(correctionFirst ? .5f : .3f);
            await Assert.That(result.Current.LastStanceUpdate!.Value.Phase).IsEqualTo(correctionFirst
                ? ZoneManagedLivingStancePhase.Applied : ZoneManagedLivingStancePhase.RefusedAtDrain);
            //A rejected actor preflight did not select a new actor slot, even if its queued
            //physical request later succeeds. Keep accepted/applied state independently copied.
            await Assert.That(result.Current.CurrentStanceSlot).IsEqualTo(1);
            await Assert.That(result.Current.DesiredStanceSlot).IsEqualTo(0);
            await Assert.That(result.Current.Control.RequestedVelocity).IsEqualTo(Vector3.Zero);
            await Assert.That(result.Current.Status.Velocity).IsEqualTo(Vector3.Zero);

            void Correction() => world.Physics.Backend.TryApplyLivingCorrection(world.Body.Actor,
                new(new(1100, 1100, 990), new(2, 0, -1), new(5, 0, 0), false, false, true, 0, 0), out _);
        }
    }

    [Test]
    public async Task SceneStepUsesTheSameOrderedQueueAndRetainsAQueuedAcceptedActorStance()
    {
        using var world = new World();
        world.Body.RequestStanceSlot(0); world.Body.PrepareFrame();
        await Assert.That(world.Body.CopyState().CurrentStanceSlot).IsEqualTo(0);
        world.Body.RequestVelocity(new(2, 0, 0), ZonePhysicsLivingVelocityMode.Immediate);
        world.Physics.Backend.Step(TimeSpan.FromMilliseconds(20));
        await Assert.That(world.Body.CopyState().AppliedDescription.Shape.Radius).IsEqualTo(.5f);
        await Assert.That(world.Body.CopyState().Status.Position.X).IsGreaterThan(1100f);
        var result = world.Relocations.Relocate(world.Actor, new(1110, 1100, 1000), _ => { });
        await Assert.That(result.Current.CurrentStanceSlot).IsEqualTo(0);
        await Assert.That(result.Current.AppliedDescription.Shape.Radius).IsEqualTo(.5f);
        await Assert.That(result.Current.LastStanceUpdate!.Value.Phase).IsEqualTo(ZoneManagedLivingStancePhase.Applied);
    }

    [Test]
    public async Task StrictEntityTranslationThresholdStillPerformsTheResetAtExactTouch()
    {
        using var world = new World(position: Vector3.Zero);
        world.Physics.Backend.ApplyVelocity(world.Body.Actor, new(1, 2, 3));
        var exact = world.Relocations.Relocate(world.Actor, new(.0001f, -.0001f, .0001f), _ => { });
        await Assert.That(exact.TranslationChanged).IsFalse();
        await Assert.That(exact.Current.Status.Position).IsEqualTo(Vector3.Zero);
        await Assert.That(exact.Current.Status.Velocity).IsEqualTo(Vector3.Zero);
        var above = new Vector3(MathF.BitIncrement(.0001f), 0, 0);
        var changed = world.Relocations.Relocate(world.Actor, above, _ => { });
        await Assert.That(changed.TranslationChanged).IsTrue();
        await Assert.That(changed.Current.Status.Position).IsEqualTo(above);
    }

    [Test]
    public async Task InvalidDestinationsRefuseBeforeFlushingCommandsOrInvokingThePublisher()
    {
        using var world = new World();
        world.Body.RequestStanceSlot(0); world.Body.PrepareFrame();
        world.Body.RequestVelocity(new(4, 0, 0), ZonePhysicsLivingVelocityMode.Immediate);
        var before = world.Body.CopyState(); var invoked = false;
        foreach (var destination in new[] { new Vector3(float.NaN, 0, 0), new Vector3(0, float.PositiveInfinity, 0) })
            await Assert.That(() => world.Relocations.Relocate(world.Actor, destination, _ => invoked = true)).Throws<ArgumentException>();
        await Assert.That(invoked).IsFalse();
        await Assert.That(world.Body.CopyState()).IsEqualTo(before);
        world.Physics.Backend.Step(TimeSpan.FromMilliseconds(20));
        await Assert.That(world.Body.CopyState().AppliedDescription.Shape.Radius).IsEqualTo(.5f);
        await Assert.That(world.Body.CopyState().Control.RequestedVelocity).IsEqualTo(new Vector3(4, 0, 0));
    }

    [Test]
    public async Task PublicationCannotReenterRelocationOrStepAndOtherActorsKeepTheirPendingCommands()
    {
        using var world = new World(); var other = new ZoneActorHandle(world.Body.Actor.UnitObjectId + 1_000_000, 1);
        world.Physics.Backend.AddActor(other, world.Body.Admission.Description);
        world.Physics.Backend.Teleport(other, new(1120, 1100, 1000), Quaternion.Identity);
        world.Physics.Backend.ApplyLivingVelocityRequest(other, new(7, 0, 0), ZonePhysicsLivingVelocityMode.Immediate);
        world.Physics.Backend.CopyStatus(other, out var previousOther);
        var refused = 0;
        world.Relocations.Relocate(world.Actor, new(1110, 1100, 1000), _ =>
        {
            try { world.Relocations.Relocate(world.Actor, Vector3.Zero, _ => { }); } catch (InvalidOperationException) { refused++; }
            try { new ZoneManagedNpcRelocation(world.Physics.Bodies).Relocate(world.Actor, Vector3.Zero, _ => { }); }
            catch (InvalidOperationException) { refused++; }
            try { world.Physics.Backend.Step(TimeSpan.FromMilliseconds(20)); } catch (InvalidOperationException) { refused++; }
        });
        await Assert.That(refused).IsEqualTo(3);
        world.Physics.Backend.CopyStatus(other, out var currentOther);
        world.Physics.Backend.CopyLivingControlState(other, out var otherControl);
        await Assert.That(currentOther).IsEqualTo(previousOther);
        await Assert.That(otherControl.HasPendingVelocityRequest).IsTrue();
        world.Physics.Backend.Step(TimeSpan.FromMilliseconds(20));
        world.Physics.Backend.CopyStatus(other, out var movedOther);
        await Assert.That(movedOther.Position.X).IsGreaterThan(previousOther.Position.X);
        world.Physics.Backend.RemoveActor(other);
    }

    [Test]
    public async Task PublisherFailureReportsFailureAfterOrderedEffectsAndReleasesTheBoundary()
    {
        using var world = new World(); var callbacks = 0;
        world.Body.RequestVelocity(new(9, 0, 0), ZonePhysicsLivingVelocityMode.Immediate);
        await Assert.That(() => world.Relocations.Relocate(world.Actor, new(1110, 1100, 1000), _ =>
        { callbacks++; throw new InvalidDataException("prepared publisher failed"); })).Throws<InvalidDataException>();
        await Assert.That(callbacks).IsEqualTo(1);
        await Assert.That(world.Body.CopyState().Status.Position).IsEqualTo(new Vector3(1110, 1100, 1000));
        await Assert.That(world.Body.CopyState().Status.Velocity).IsEqualTo(Vector3.Zero);
        world.Relocations.Relocate(world.Actor, new(1120, 1100, 1000), _ => callbacks++);
        await Assert.That(callbacks).IsEqualTo(2);
    }

    [Test]
    public async Task PublicationRetirementCancelsExactGenerationCommandsAndCannotReachAReplacementBody()
    {
        using var world = new World(); var old = world.Actor.Handle; var callbacks = 0;
        ZoneManagedNpcBodyLease? replacementBody = null;
        await Assert.That(() => world.Relocations.Relocate(world.Actor, new(1110, 1100, 1000), _ =>
        {
            callbacks++; world.Body.RequestVelocity(new(99, 0, 0), ZonePhysicsLivingVelocityMode.Immediate);
            world.Actors.Remove(old); world.Body.Dispose(); world.Physics.Admission.Catalog.Retire(old);
            world.Physics.Admission.Registry.RemovePrepared(world.Actor.Npc.Unit);
            var replacement = world.Physics.Admission.Npc(old.UnitObjectId, world.Actor.Npc.Id, world.Physics.Model);
            world.Physics.Admission.Publish(replacement); var next = world.Physics.Admission.Catalog.Register(replacement);
            var actor = world.Actors.Adopt(next, new() { EntityId = replacement.ObjectId + 10_000_000 }, 5,
                (ulong)next.Generation << 20, ZoneManagedNpcActorScene.CreatePipeUserControls(), true, null);
            replacementBody = world.Physics.Bodies.Create(next, replacement, new(1120, 1100, 1000), Quaternion.Identity,
                () => ReferenceEquals(world.Actors.Find(next), actor));
        })).Throws<ObjectDisposedException>();
        using var fresh = replacementBody!;
        await Assert.That(callbacks).IsEqualTo(1);
        await Assert.That(fresh.Actor.Generation).IsGreaterThan(old.Generation);
        await Assert.That(fresh.CopyState().Control.HasPendingVelocityRequest).IsFalse();
        world.Physics.Backend.Step(TimeSpan.FromMilliseconds(20));
        await Assert.That(fresh.CopyState().Status.Position.X).IsEqualTo(1120f);
        await Assert.That(world.Physics.Backend.OwnedPhysics.Owns(old)).IsFalse();
    }

    [Test]
    public async Task CommandsIssuedByThePublisherBelongAfterTheResetAndRunOnTheNextSceneStep()
    {
        using var world = new World();
        world.Relocations.Relocate(world.Actor, new(1110, 1100, 1000), _ =>
            world.Body.RequestVelocity(new(7, 0, 0), ZonePhysicsLivingVelocityMode.Immediate));
        await Assert.That(world.Body.CopyState().Control.HasPendingVelocityRequest).IsTrue();
        await Assert.That(world.Body.CopyState().Status.Position.X).IsEqualTo(1110f);
        world.Physics.Backend.Step(TimeSpan.FromMilliseconds(20));
        await Assert.That(world.Body.CopyState().Status.Position.X).IsGreaterThan(1110.1f);
    }

    private sealed class World : IDisposable
    {
        public ManagedNpcPhysicsTests.World Physics { get; } = new();
        public ZoneManagedLuaRuntime Lua { get; } = new();
        public ZoneManagedNpcActorScene Actors { get; }
        public ZoneManagedNpcActor Actor { get; }
        public ZoneManagedNpcBodyLease Body { get; }
        public ZoneManagedNpcRelocation Relocations { get; }
        public World(Quaternion? rotation = null, Vector3? position = null)
        {
            var origin = position ?? new Vector3(1100, 1100, 1000); var orientation = rotation ?? Quaternion.Identity;
            Physics.Npc.Unit.CommitPose(new(ZoneMovementPoseMath.ToUnitWorldPosition(origin), orientation));
            var handle = Physics.Admission.Catalog.Register(Physics.Npc);
            Actors = new(Physics.Admission.Registry, Physics.Admission.Catalog, Lua, new(),
                unit => ZoneMovementPoseMath.ToMeters(unit.Pose.Position), () => 0, () => 1, _ => { }, TimeSpan.FromMilliseconds(20));
            Actor = Actors.Adopt(handle, new() { EntityId = Physics.Npc.ObjectId + 10_000_000 }, 5,
                (ulong)handle.Generation << 20, ZoneManagedNpcActorScene.CreatePipeUserControls(), true, null);
            Body = Physics.Bodies.Create(handle, Physics.Npc, origin, orientation, () => ReferenceEquals(Actors.Find(handle), Actor));
            Relocations = new(Physics.Bodies);
        }
        public void Dispose() { Actors.Dispose(); Physics.Dispose(); Lua.Dispose(); }
    }
}
