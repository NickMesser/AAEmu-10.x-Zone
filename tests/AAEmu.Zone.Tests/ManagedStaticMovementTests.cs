using System.Buffers.Binary;
using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Content;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Terrain;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.Tests;

public sealed class ManagedStaticMovementTests
{
    [Test]
    public async Task RelayedMovementCannotCorrectThroughAWallAfterPhysicsOrPublishAnUnblockedPose()
    {
        var body = Convert.FromHexString(UnitStateSnapshotTests.PlayerEnterBody);
        var snapshot = UnitStateSnapshot.Read(body);
        var origin = new ZoneActorMovementWorldOrigin(9, 14);
        var models = new ZoneModelContent([new() { Id = snapshot.ModelId, Subtype = "ActorModel", SubtypeId = 1 }],
            actorModels: new ZoneActorModelContent(
                [new() { Id = 1, PhysicsLivingMass = 80, GameJumpHeight = 1, TurnSpeed = 180 }],
                [new() { Id = 1, ActorModelId = 1, StanceId = 2, MaxSpeed = 5, NormalSpeed = 5, SizeX = .4f, SizeY = .4f, SizeZ = .9f }]));
        var actors = new ZonePlayerActorFactory(models, worldOrigin: origin);
        var registry = new ZoneUnitRegistry(playerActors: actors);
        registry.Create(body);
        var unit = registry.Units[snapshot.ObjectId];
        var actor = actors.FindByUnitObjectId(snapshot.ObjectId)!;
        var start = actor.Position;
        var wallX = start.X + .5f;
        var statics = new ZoneManagedStaticGeometry();
        statics.AddInstance(new(
            [new(wallX, start.Y - 10, start.Z - 10), new(wallX, start.Y + 10, start.Z - 10),
             new(wallX, start.Y + 10, start.Z + 10), new(wallX, start.Y - 10, start.Z + 10)],
            [0, 1, 2, 0, 2, 3]), Matrix4x4.Identity);
        var backend = new ZoneManagedPhysicsBackend(new(new ZoneTerrainHeightField()), staticGeometry: statics);
        backend.AddActor(actor.Handle, new(new(new(.4f, .4f, .9f), true), 80));
        backend.Teleport(actor.Handle, start, actor.Orientation);
        actor.BindPhysicsAuthority(backend);
        var diagnostics = new ZoneMovementDiagnostics();
        var clock = new ZoneMovementFrameClock();
        var motion = new ZoneOwnedMovementMotion(backend, backend);
        var bindings = new ZoneMovementInputBindings(clock, actors, motion, diagnostics);
        var services = new ZoneMovementServices(clock, motion, bindings, diagnostics);
        var controller = new ZoneActorMovementController(registry, unit, services);
        var outgoing = new ZoneMovementOutgoing(registry, diagnostics, localOnWire: false);
        var phases = new ZoneMovementFramePhases(registry, backend, actors,
            new ZoneMovementInputServices(registry, diagnostics, bindings),
            new ZoneMovementCorrectionServices(registry, actors, motion, clock, diagnostics, origin),
            outgoing, clock, diagnostics, bindings, origin);
        var requested = snapshot.Position with { QuantizedX = snapshot.Position.QuantizedX + 512 };
        for (uint index = 1; index <= 3; index++)
        {
            var frame = new ZoneMovementFrame(index, TimeSpan.FromMilliseconds(33), 1000 + index * 33, 1 + index * .033f);
            clock.SetFrame(frame);
            controller.Receive(new(1, frame.ServerMilliseconds, 0, 0, 0, requested, new(2000, 0, 0), default,
                new UnitActorMovement(new(127, 0, 0), 2, 0, 1)));
            phases.PrePhase(frame);
            phases.StepPhysics(frame.Elapsed);
            phases.PostPhase(frame);
            backend.CopyStatus(actor.Handle, out var status);
            await Assert.That(status.Position.X).IsLessThanOrEqualTo(wallX - .399f);
            await Assert.That(unit.Pose.Position.X).IsLessThan(snapshot.Position.X + .102);
            while (registry.TryDequeueNotification(out var notification))
            {
                if (notification.Opcode != ZwOpcodes.UnitMovements) continue;
                await Assert.That(BinaryPrimitives.ReadInt32LittleEndian(notification.Body.Span)).IsEqualTo(1);
                var movement = UnitMovement.Read(notification.Body.Span[4..]);
                await Assert.That(movement.Movement.Position.X).IsLessThan(snapshot.Position.X + .102);
            }
        }
        await Assert.That(unit.Pose.Position.X).IsGreaterThan(snapshot.Position.X + .04);
        actors.RetireAll(backend);
    }
}
