using System.Buffers.Binary;
using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Content;
using AAEmu.Zone.Npcs;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Scripting;
using AAEmu.Zone.Terrain;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.Tests;

public sealed class ManagedNpcMovementFrameTests
{
    private static readonly ZoneActorMovementWorldOrigin Origin = new(9, 14);

    [Test]
    public async Task FallingNpcAndWallContactPublishThroughUnitSpatialAiAndActualOutgoingCodec()
    {
        using var world = new World();
        world.Statics.AddInstance(new ZoneManagedTriangleMesh(
            [new(1090,1098,9),new(1090,1102,9),new(1090,1102,15),new(1090,1098,15)], [0,1,2,0,2,3]), Matrix4x4.Identity);
        var initialCell = world.Spatial.GetMembership(world.Npc.Unit)!.Value.CellX;
        var observerCalls = 0;
        world.Metadata = (actor, snapshot, frame) =>
        {
            var local = ZoneMovementPoseMath.ToLocalMeters(actor.Npc.Unit.Pose.Position, Origin);
            if (local != snapshot.Status.Position || actor.AiObject.Position != local ||
                world.Spatial.GetMembership(actor.Npc.Unit)!.Value.Position != local)
                throw new InvalidOperationException("Outgoing metadata was requested before pose/spatial/AI publication.");
            observerCalls++;
            //Explicit copied plain CActor control fields. This is not a Puppet-state guess.
            return new() { ActorFlags = 4, Alertness = 1, Delta = new(.5f,0,0), Stance = 8,
                Pose = new(ZoneMovementPoseMath.ToUnitWorldPosition(new(999)), Quaternion.Identity), Velocity = new(99) };
        };
        world.Body.RequestVelocity(new(3, 0, 0));
        var messages = new List<UnitMovement>();
        for (var i = 0; i < 100; i++) { world.Tick(); messages.AddRange(world.ReadMovements()); }
        var state = world.Body.CopyState();
        await Assert.That(state.Status.IsGrounded).IsTrue();
        await Assert.That(state.Status.Position.X).IsGreaterThan(1089f);
        await Assert.That(state.Status.Position.X).IsLessThan(1089.71f);
        await Assert.That(MathF.Abs(state.Status.Position.Z - 10.1f)).IsLessThan(.003f);
        await Assert.That(world.Spatial.GetMembership(world.Npc.Unit)!.Value.CellX).IsGreaterThan(initialCell);
        await Assert.That(world.Spatial.GetMembership(world.Npc.Unit)!.Value.Position).IsEqualTo(state.Status.Position);
        await Assert.That(world.Actor.AiObject.Position).IsEqualTo(state.Status.Position);
        await Assert.That(ZoneMovementPoseMath.ToLocalMeters(world.Npc.Unit.Pose.Position, Origin)).IsEqualTo(state.Status.Position);
        await Assert.That(world.Inner.StepCalls).IsEqualTo(100);
        await Assert.That(observerCalls).IsEqualTo(100);
        await Assert.That(world.Phases.LastPublishedCount).IsEqualTo(1);
        await Assert.That(messages.Count).IsGreaterThan(10);
        var last = messages[^1];
        await Assert.That(last.ObjectId).IsEqualTo(world.Npc.ObjectId);
        await Assert.That(Math.Abs(last.Movement.Position.X - world.Npc.Unit.Pose.Position.X)).IsLessThan(.003);
        await Assert.That(Math.Abs(last.Movement.Position.Z - state.Status.Position.Z)).IsLessThan(.003f);
        var actorMove = (UnitActorMovement)last.Movement.Details!;
        await Assert.That(actorMove.Stance).IsEqualTo((sbyte)1); //Accepted body-owner slot replaces provider's8.
        await Assert.That(actorMove.Alertness).IsEqualTo((sbyte)1);
        await Assert.That(actorMove.Flags).IsEqualTo((ushort)4);
        await Assert.That(last.Movement.DecodedVelocity.Length()).IsLessThan(4f); //Never provider's(99,99,99).
        await Assert.That(last.Movement.Time).IsGreaterThan(0u);
    }

    [Test]
    public async Task MissingCActorMetadataStillPublishesRealPhysicsAndReportsOneNamedEncodeRefusal()
    {
        using var world = new World(withMetadata: false);
        var initial = world.Npc.Unit.Pose;
        world.Tick(); world.Tick();
        await Assert.That(world.Npc.Unit.Pose.Position.Z).IsLessThan(initial.Position.Z);
        await Assert.That(world.Phases.LastPublishedCount).IsEqualTo(1);
        await Assert.That(world.Phases.LastEncodedCount).IsEqualTo(0);
        await Assert.That(world.Actor.AiObject.Position).IsEqualTo(world.Body.CopyState().Status.Position);
        await Assert.That(world.ReadMovements().Count).IsEqualTo(0);
        await Assert.That(world.Diagnostics.PhaseRefusals).IsEqualTo(1L);
        await Assert.That(world.Diagnostics.LastRefusalMessage!.Contains("copied CActor", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task UnportedOptionalOutgoingFieldsRefuseWithoutChangingThePhysicalPublication()
    {
        using var world = new World();
        world.Metadata = (_, _, _) => new() { GroundUnitId = 42, ActorFlags = 4 };
        world.Tick();
        await Assert.That(world.Phases.LastPublishedCount).IsEqualTo(1);
        await Assert.That(world.ReadMovements().Count).IsEqualTo(0);
        await Assert.That(world.Diagnostics.PhaseRefusals).IsEqualTo(1L);
        world.Metadata = (_, _, _) => new() { ActorFlags = 4, Alertness = 0 };
        world.Tick();
        await Assert.That(world.ReadMovements().Count).IsEqualTo(1);
    }

    [Test]
    public async Task RetirementBetweenSharedStepAndNpcPublicationNeverCommitsOrReplicatesTheOldOwner()
    {
        using var world = new World();
        var unit = world.Npc.Unit; var before = unit.Pose;
        world.Inner.AfterPost = world.Retire;
        world.Tick();
        await Assert.That(unit.Pose).IsEqualTo(before);
        await Assert.That(world.Phases.LastPublishedCount).IsEqualTo(0);
        await Assert.That(world.ReadMovements().Count).IsEqualTo(0);
        await Assert.That(world.Bodies.Count).IsEqualTo(0);
        await Assert.That(world.Spatial.Count).IsEqualTo(0);
    }

    [Test]
    public async Task MetadataCallbackReplacementCannotPublishAnUnstagedBodyOrLeakTheOldGeneration()
    {
        using var world = new World();
        var old = world.Body.Actor;
        world.Metadata = (_, _, _) => { world.Replace(); return new() { ActorFlags = 4 }; };
        world.Tick();
        await Assert.That(world.Body.Actor.Generation).IsGreaterThan(old.Generation);
        await Assert.That(world.ReadMovements().Count).IsEqualTo(0);
        world.Metadata = (_, _, _) => new() { ActorFlags = 4, Alertness = 0 };
        world.Tick();
        var emitted = world.ReadMovements().Single();
        await Assert.That(emitted.ObjectId).IsEqualTo(world.Npc.ObjectId);
        await Assert.That(Math.Abs(emitted.Movement.Position.X - world.Npc.Unit.Pose.Position.X)).IsLessThan(.003);
        await Assert.That(world.Bodies.Count).IsEqualTo(1);
    }

    [Test]
    public async Task WireCoordinatesUseTheSameSceneOriginAndExplicitEncoderConvention()
    {
        using var world = new World(localOnWire: true);
        world.Tick();
        var output = world.ReadMovements().Single();
        var local = world.Body.CopyState().Status.Position;
        await Assert.That(Math.Abs(output.Movement.Position.X - local.X)).IsLessThan(.003);
        await Assert.That(Math.Abs(output.Movement.Position.Y - local.Y)).IsLessThan(.003);
        await Assert.That(world.Npc.Unit.Pose.Position.X).IsGreaterThan(9000d);
        await Assert.That(world.Actor.AiObject.Position).IsEqualTo(local);
    }

    [Test]
    public async Task GuardedDecoratorCannotDoubleStepOrTakeOwnershipOfTheBodyOnDisposal()
    {
        using var world = new World();
        var frame = world.NextFrame();
        await Assert.That(() => world.Phases.StepPhysics(frame.Elapsed)).Throws<InvalidOperationException>();
        world.Phases.PrePhase(frame);
        await Assert.That(() => world.Phases.PrePhase(frame)).Throws<InvalidOperationException>();
        await Assert.That(() => world.Phases.PostPhase(frame)).Throws<InvalidOperationException>();
        await Assert.That(() => world.Phases.StepPhysics(TimeSpan.FromMilliseconds(1))).Throws<ArgumentOutOfRangeException>();
        world.Phases.StepPhysics(frame.Elapsed);
        await Assert.That(() => world.Phases.StepPhysics(frame.Elapsed)).Throws<InvalidOperationException>();
        await Assert.That(() => world.Phases.PostPhase(frame with { Frame = frame.Frame + 1 })).Throws<InvalidOperationException>();
        world.Phases.PostPhase(frame);
        await Assert.That(world.Inner.StepCalls).IsEqualTo(1);
        world.Phases.Dispose();
        await Assert.That(world.Body.IsCurrent).IsTrue();
        await Assert.That(() => world.Phases.PrePhase(world.NextFrame())).Throws<ObjectDisposedException>();
    }

    private sealed class World : IDisposable
    {
        public ZoneUnitSpatialIndex Spatial { get; } = new(64, new(9,14,11,15));
        public ManagedNpcAdmissionTests.Fixture Admission { get; }
        public ZoneManagedStaticGeometry Statics { get; } = new();
        public ZoneManagedPhysicsBackend Backend { get; }
        public ZoneManagedNpcPhysics Bodies { get; }
        public ZoneManagedLuaRuntime Lua { get; } = new();
        public ZoneManagedNpcActorScene Actors { get; }
        public ZoneNpc Npc { get; private set; } = null!;
        public ZoneManagedNpcActor Actor { get; private set; } = null!;
        public ZoneManagedNpcBodyLease Body { get; private set; } = null!;
        public ZoneMovementDiagnostics Diagnostics { get; } = new();
        public CountingPhases Inner { get; }
        public ZoneManagedNpcMovementFramePhases Phases { get; }
        public Func<ZoneManagedNpcActor, ZoneManagedNpcBodySnapshot, ZoneMovementFrame, ZoneActorMovementState?>? Metadata { get; set; } =
            (_, _, _) => new() { ActorFlags = 4, Alertness = 0 };
        private readonly ZoneModelContent _models;
        private uint _frame;
        private static Vector3 Local(ZoneUnit unit) => ZoneMovementPoseMath.ToLocalMeters(unit.Pose.Position, Origin);

        public World(bool withMetadata = true, bool localOnWire = false)
        {
            Admission = new(spatialIndex: Spatial, modelPosition: Local);
            _models = new([new() { Id=17,Name="movement_npc",Subtype="ActorModel",SubtypeId=8 }], actorModels: new(
                [new() { Id=8,PhysicsLivingMass=80,PhysicsLivingGravity=25,PhysicsLivingKAirControl=.1f,
                    PhysicsLivingMinSlideAngle=45,PhysicsLivingMaxClimbAngle=50,PhysicsLivingMinFallAngle=50,
                    PhysicsLivingMaxVelGround=10,PhysicsLivingTimeImpulseRecover=5 }],
                [new() { Id=1,ActorModelId=8,StanceId=2,SizeX=.3f,SizeY=.7f,SizeZ=.3f,
                    HeightCollider=1.2f,HeightPivot=.1f,UseCapsule=true,MaxSpeed=3 }]));
            var terrain = new ZoneTerrainHeightField(); terrain.LoadCell(1,1,TerrainHeightFieldTests.CreateCell(10,0));
            Backend = new(new(terrain),staticGeometry:Statics); Bodies = new(Backend,_models,_=>143);
            Actors = new(Admission.Registry,Admission.Catalog,Lua,new(),Local,()=>0,()=>1,_=>{},TimeSpan.FromMilliseconds(20));
            Admit(Admission.Npc(model:_models.Models[17],unitModelId:17));
            var players = new ZonePlayerActorFactory(_models,worldOrigin:Origin);
            var clock = new ZoneMovementFrameClock(); var motion = new ZoneOwnedMovementMotion(Backend,Backend);
            var bindings = new ZoneMovementInputBindings(clock,players,motion,Diagnostics);
            var outgoing = new ZoneMovementOutgoing(Admission.Registry,Diagnostics,localOnWire);
            Inner = new(new ZoneMovementFramePhases(Admission.Registry,Backend,players,
                new ZoneMovementInputServices(Admission.Registry,Diagnostics,bindings),
                new ZoneMovementCorrectionServices(Admission.Registry,players,motion,clock,Diagnostics,Origin),
                outgoing,clock,Diagnostics,bindings,Origin));
            Phases = new(Inner,Admission.Registry,Admission.Catalog,Actors,Bodies,outgoing,Diagnostics,Origin,
                withMetadata ? (actor,body,frame)=>Metadata?.Invoke(actor,body,frame) : null);
        }
        private void Admit(ZoneNpc npc)
        {
            Npc=npc; var position=new Vector3(1087.8f,1100,12);
            npc.Unit.CommitPose(new(ZoneMovementPoseMath.ToWorldPosition(position,Origin),Quaternion.Identity));
            Admission.Publish(npc); var handle=Admission.Catalog.Register(npc);
            Actor=Actors.Adopt(handle,new() {EntityId=npc.ObjectId+10_000_000,Position=position},5,(ulong)handle.Generation<<20,
                ZoneManagedNpcActorScene.CreatePipeUserControls(),true,null);
            Body=Bodies.Create(handle,npc,position,Quaternion.Identity,
                ()=>ReferenceEquals(Admission.Catalog.Find(handle),npc)&&ReferenceEquals(Actors.Find(handle)?.Npc,npc)&&Admission.Registry.IsCurrent(npc.Unit));
            Admission.Registry.RegisterSpatial(npc.Unit);
        }
        public void Retire()
        { Body.Dispose(); Actor.Dispose(); Admission.Catalog.Retire(Npc); Admission.Registry.RemovePrepared(Npc.Unit); }
        public void Replace()
        {
            var old=Npc; Retire();
            Admit(Admission.Npc(old.ObjectId,old.Id,_models.Models[17],17));
        }
        public ZoneMovementFrame NextFrame() => new(++_frame,TimeSpan.FromMilliseconds(20),_frame*20,_frame*.02f);
        public void Tick()
        { var frame=NextFrame(); Phases.PrePhase(frame); Phases.StepPhysics(frame.Elapsed); Phases.PostPhase(frame); }
        public IReadOnlyList<UnitMovement> ReadMovements()
        {
            var result=new List<UnitMovement>();
            while(Admission.Registry.TryDequeueNotification(out var notification))
            {
                if(notification.Opcode!=ZwOpcodes.UnitMovements) continue;
                if(BinaryPrimitives.ReadInt32LittleEndian(notification.Body.Span)!=1) throw new InvalidDataException("Expected one exact NPC movement entry.");
                result.Add(UnitMovement.Read(notification.Body.Span[4..]));
            }
            return result;
        }
        public void Dispose()
        { Phases.Dispose(); Bodies.Dispose(); Actors.Dispose(); Admission.Dispose(); Lua.Dispose(); Spatial.Dispose(); }
    }

    private sealed class CountingPhases(IZoneMovementFramePhases inner) : IZoneMovementFramePhases
    {
        public int StepCalls { get; private set; }
        public Action? AfterPost { get; set; }
        public int PrePhase(in ZoneMovementFrame frame) => inner.PrePhase(frame);
        public void StepPhysics(TimeSpan elapsed) { StepCalls++; inner.StepPhysics(elapsed); }
        public int PostPhase(in ZoneMovementFrame frame) { var count=inner.PostPhase(frame); AfterPost?.Invoke(); return count; }
    }
}
