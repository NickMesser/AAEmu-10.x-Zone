using System.Buffers.Binary;
using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Content;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.Tests;

public sealed class ZoneMovementAuthorityTests
{
    private static readonly ZoneActorMovementWorldOrigin Origin = new(9, 14); // captured zone 129 layout

    private static ZoneModelContent Models(uint modelId) => new(
        [new() { Id = modelId, Subtype = "ActorModel", SubtypeId = 1 }],
        actorModels: new ZoneActorModelContent(
            [new() { Id = 1, PhysicsLivingMass = 80, PhysicsLivingGravity = 9.81f, GameJumpHeight = 1, TurnSpeed = 180 }],
            [new() { Id = 1, ActorModelId = 1, StanceId = 2, MaxSpeed = 5, NormalSpeed = 5, SizeX = .4f, SizeY = .4f, SizeZ = .9f }]));

    private sealed class Floor(float height) : ZoneOwnedPlayerPhysics.IGroundQuery
    {
        public bool TryGround(Vector3 from, Vector3 to, out Vector3 position, out ushort physicalMaterialId)
        {
            position = new(from.X, from.Y, height);
            physicalMaterialId = 7;
            return from.Z >= height && to.Z <= height;
        }
    }

    [Test]
    public async Task CapturedWorldPoseMapsToZoneLocalPhysicsAndBack()
    {
        var snapshot = UnitStateSnapshot.Read(Convert.FromHexString(UnitStateSnapshotTests.PlayerEnterBody));
        var world = ZoneUnitPose.FromSnapshot(snapshot).Position;
        var local = ZoneMovementPoseMath.ToLocalMeters(world, Origin);
        await Assert.That(Math.Abs(local.X - (snapshot.Position.X - 9216))).IsLessThan(.001);
        await Assert.That(Math.Abs(local.Y - (snapshot.Position.Y - 14336))).IsLessThan(.001);
        await Assert.That(ZoneMovementPoseMath.ToWorldPosition(local, Origin)).IsEqualTo(world);
        var correctionOrigin = ZoneActorMovementCorrectionMath.Origin(9, 14);
        await Assert.That(ZoneActorMovementCorrectionMath.Local(world, correctionOrigin)).IsEqualTo(local);
        var restored = ZoneActorMovementCorrectionMath.World(local, correctionOrigin);
        await Assert.That(restored.X).IsEqualTo(snapshot.Position.X);
        await Assert.That(restored.Y).IsEqualTo(snapshot.Position.Y);

        var actors = new ZonePlayerActorFactory(Models(snapshot.ModelId), worldOrigin: Origin);
        var registry = new ZoneUnitRegistry(playerActors: actors);
        registry.Create(Convert.FromHexString(UnitStateSnapshotTests.PlayerEnterBody));
        var actor = actors.FindByUnitObjectId(snapshot.ObjectId)!;
        await Assert.That(actor.Position).IsEqualTo(local);
        actors.Retire(actor.Handle);
    }

    [Test]
    public async Task OwnedAuthorityPublishesMovementAndSpatialStateWithoutReadingANativeBody()
    {
        var body = Convert.FromHexString(UnitStateSnapshotTests.PlayerEnterBody);
        var snapshot = UnitStateSnapshot.Read(body);
        var actors = new ZonePlayerActorFactory(Models(snapshot.ModelId), worldOrigin: Origin);
        using var spatial = new ZoneUnitSpatialIndex(64, new(9, 14, 11, 15));
        var registry = new ZoneUnitRegistry(spatialIndex: spatial,
            modelPosition: unit => ZoneMovementPoseMath.ToLocalMeters(unit.Pose.Position, Origin), playerActors: actors);
        registry.Create(body);
        var unit = registry.Units[snapshot.ObjectId];
        var actor = actors.FindByUnitObjectId(snapshot.ObjectId)!;
        var floor = new Floor(actor.Position.Z);
        var backend = new ZoneOwnedPhysicsBackend(new ZoneOwnedPlayerPhysics(floor), floor);
        backend.AddActor(actor.Handle, new(new(new(.4f, .4f, .9f), true), 80));
        backend.Teleport(actor.Handle, actor.Position, actor.Orientation);
        actor.BindPhysicsAuthority(backend);
        await Assert.That(actor.Body is null).IsTrue();
        await Assert.That(actor.TryCopyStatus(out var initial)).IsTrue();
        await Assert.That(initial.Position).IsEqualTo(actor.Position);

        var clock = new ZoneMovementFrameClock();
        var diagnostics = new ZoneMovementDiagnostics();
        var motion = new ZoneOwnedMovementMotion(backend);
        var bindings = new ZoneMovementInputBindings(clock, actors, motion, diagnostics);
        var services = new ZoneMovementServices(clock, motion, bindings, diagnostics);
        var controller = new ZoneActorMovementController(registry, unit, services);
        var outgoing = new ZoneMovementOutgoing(registry, diagnostics, localOnWire: false);
        var phases = new ZoneMovementFramePhases(registry, backend, actors,
            new ZoneMovementInputServices(registry, diagnostics, bindings),
            new ZoneMovementCorrectionServices(registry, actors, motion, clock, diagnostics, Origin),
            outgoing, clock, diagnostics, bindings, Origin);
        var frame = new ZoneMovementFrame(1, TimeSpan.FromMilliseconds(33), 1000, 1);
        clock.SetFrame(frame);
        backend.ApplyImpulse(actor.Handle, new(0, 0, 1));
        await Assert.That(phases.PrePhase(frame)).IsEqualTo(1); // idle bodies still publish physics before any client movement.
        phases.StepPhysics(frame.Elapsed);
        await Assert.That(phases.PostPhase(frame)).IsEqualTo(0); // no input metadata to replicate yet.
        await Assert.That(unit.Pose.Position.Z).IsGreaterThan(snapshot.Position.Z);
        frame = new(2, TimeSpan.FromMilliseconds(33), 1033, 1.033f);
        clock.SetFrame(frame);
        // A 2 m displacement on World's default continent wire, with ordinary actor movement.
        var moved = snapshot.Position with { QuantizedX = snapshot.Position.QuantizedX + 1024 };
        controller.Receive(new(1, 1033, 0, 0, 0, moved, new(2000, 0, 0), default,
            new UnitActorMovement(new(127, 0, 0), 2, 0, 1)));
        await Assert.That(phases.PrePhase(frame)).IsEqualTo(1);
        phases.StepPhysics(frame.Elapsed);
        await Assert.That(phases.PostPhase(frame)).IsEqualTo(1);
        await Assert.That(Math.Abs(unit.Pose.Position.X - moved.X)).IsLessThan(.002);
        await Assert.That(spatial.GetMembership(unit)!.Value.Position).IsEqualTo(ZoneMovementPoseMath.ToLocalMeters(unit.Pose.Position, Origin));
        await Assert.That(registry.TryDequeueNotification(out var output)).IsTrue();
        await Assert.That(output.Opcode).IsEqualTo(ZwOpcodes.UnitMovements);
        await Assert.That(BinaryPrimitives.ReadInt32LittleEndian(output.Body.Span)).IsEqualTo(1);
        var movement = UnitMovement.Read(output.Body.Span[4..]);
        await Assert.That(movement.ObjectId).IsEqualTo(snapshot.ObjectId);
        await Assert.That(Math.Abs(movement.Movement.Position.X - moved.X)).IsLessThan(.002);
        await Assert.That(Math.Abs(movement.Movement.Position.Y - snapshot.Position.Y)).IsLessThan(.002);
        await Assert.That(movement.Movement.Time).IsEqualTo(1033u);

        // Saturating a strongly negative physical velocity produces short.MinValue. The
        // moving/stopping classifier must widen before Abs, or one fall can kill the scene.
        outgoing.Sample(unit, new(ZoneMovementPoseMath.ToMeters(unit.Pose.Position), Quaternion.Identity,
            new(-100, -100, -100), false, false), controller.LastAdmitted!, frame);
        await Assert.That(outgoing.Build(frame)).IsEqualTo(1);
        await Assert.That(registry.TryDequeueNotification(out var saturated)).IsTrue();
        var fast = UnitMovement.Read(saturated.Body.Span[4..]);
        await Assert.That(fast.Movement.Velocity).IsEqualTo(new MovementVector16(-32767, -32767, -32767));

        actors.RetireAll(backend);
        await Assert.That(actor.TryCopyStatus(out _)).IsFalse();
        await Assert.That(registry.Units.Count).IsEqualTo(0);
        await Assert.That(actors.LiveCount).IsEqualTo(0);
        await Assert.That(() => backend.CopyStatus(actor.Handle, out _)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task RepeatedClientPoseCorrectionsPreserveOwnedJumpGravityAndLanding()
    {
        var floor = new Floor(100);
        var backend = new ZoneOwnedPhysicsBackend(new ZoneOwnedPlayerPhysics(floor), floor);
        var actor = new ZoneActorHandle(1, 1);
        backend.AddActor(actor, new(new(new(.4f, .4f, .9f), true), 80));
        backend.Teleport(actor, new(1, 2, 100), Quaternion.Identity);
        var motion = new ZoneOwnedMovementMotion(backend);
        motion.ApplyImpulse(actor, new(0, 0, 4.5f));
        var maximum = 100f;
        for (var step = 0; step < 120; step++)
        {
            motion.ApplyVelocity(actor, new(2, 0, 0));
            backend.Step(TimeSpan.FromMilliseconds(33));
            backend.CopyStatus(actor, out var before);
            motion.ApplyPose(actor, new(before.Position.X, 2, 100), Quaternion.Identity); // stale client height
            backend.CopyStatus(actor, out var after);
            await Assert.That(after.Position.Z).IsEqualTo(before.Position.Z);
            await Assert.That(after.Velocity).IsEqualTo(before.Velocity);
            maximum = Math.Max(maximum, after.Position.Z);
        }
        backend.CopyStatus(actor, out var landed);
        await Assert.That(maximum).IsGreaterThan(100.9f);
        await Assert.That(landed.Position.Z).IsEqualTo(100f);
        await Assert.That(landed.IsGrounded).IsTrue();
        await Assert.That(landed.Position.X).IsGreaterThan(8f);
        backend.RemoveActor(actor);
    }

    [Test]
    public async Task CollisionSweepCoversTheIntegratedStepRatherThanASecondOfVelocity()
    {
        var floor = new Floor(100);
        var query = new SweepProbe();
        var backend = new ZoneOwnedPhysicsBackend(new ZoneOwnedPlayerPhysics(floor), floor, query);
        var actor = new ZoneActorHandle(1, 1);
        backend.AddActor(actor, new(new(new(.4f, .4f, .9f), true), 80));
        backend.Teleport(actor, new(0, 0, 100), Quaternion.Identity);
        backend.ApplyVelocity(actor, new(5, 0, 0));
        backend.Step(TimeSpan.FromMilliseconds(33));
        backend.CopyStatus(actor, out var status);
        await Assert.That(Math.Abs(query.Displacement.X - .165f)).IsLessThan(.0001f);
        await Assert.That(status.Position.X).IsEqualTo(query.Displacement.X);
        backend.RemoveActor(actor);
    }

    private sealed class SweepProbe : IZonePhysicsBackend
    {
        public Vector3 Displacement { get; private set; }
        public bool TrySweep(ZonePhysicsShape shape, Vector3 origin, Vector3 displacement, ZoneActorHandle? ignoreActor, out ZonePhysicsHit hit)
        { Displacement = displacement; hit = default; return false; }
        public void AddActor(ZoneActorHandle actor, ZonePhysicsActorDescription description) => throw new NotSupportedException();
        public void RemoveActor(ZoneActorHandle actor) => throw new NotSupportedException();
        public void Step(TimeSpan elapsed) => throw new NotSupportedException();
        public void Teleport(ZoneActorHandle actor, Vector3 position, Quaternion orientation) => throw new NotSupportedException();
        public void CopyStatus(ZoneActorHandle actor, out ZonePhysicsActorStatus status) => throw new NotSupportedException();
        public bool TryCollide(ZonePhysicsShape shape, Vector3 position, ZoneActorHandle? ignoreActor) => throw new NotSupportedException();
        public bool TryGround(Vector3 origin, float maxDistance, out ZonePhysicsGroundHit ground) => throw new NotSupportedException();
    }
}
