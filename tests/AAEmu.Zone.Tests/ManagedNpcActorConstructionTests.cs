using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Content;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Terrain;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.Tests;

public sealed class ManagedNpcActorConstructionTests
{
    [Test]
    public async Task ActualWorldModelInstallsDescriptorScalarsAndIndependentActorHealth()
    {
        using var world = new World(templateModel: new() { Id=99,Subtype="PrefabModel",SubtypeId=456 });
        var actor = world.State;
        await Assert.That(actor.Npc.Model.Id).IsEqualTo(99u);
        await Assert.That(actor.ModelId).IsEqualTo(17u);
        await Assert.That(actor.ActorModelId).IsEqualTo(8u);
        //Distinct values retain native descriptor-to-field order, including the integer casts.
        await Assert.That(actor.GameParams).IsEqualTo(new ZoneManagedNpcGameParameters(
            101,102,103,104,105,106,107,108,109,110,111,112,113,114,20,11,117,118,119));
        await Assert.That(actor.Health.CurrentHealth).IsEqualTo(0f);
        await Assert.That(actor.Health.MaximumHealth).IsEqualTo(10f);
        await Assert.That(world.Npc.Unit.Snapshot.PreciseHealth).IsNotEqualTo(0L);
        await Assert.That(actor.Stances.Default.Physicalization.Size).IsEqualTo(new Vector3(.3f,.7f,.3f));
        await Assert.That(() => actor.CopyAnimatedParameters()).Throws<InvalidOperationException>();
    }

    [Test]
    [Arguments(0u, ZoneManagedNpcActorClass.Player)]
    [Arguments(1u, ZoneManagedNpcActorClass.Quadruped)]
    [Arguments(2u, ZoneManagedNpcActorClass.Monster)]
    [Arguments(3u, ZoneManagedNpcActorClass.Quadruped)]
    [Arguments(99u, ZoneManagedNpcActorClass.Player)]
    public async Task OriginalActorFactorySelectsTheDerivedClassFromMovementId(uint movement, ZoneManagedNpcActorClass expected)
    {
        using var world = new World(Definition() with { MovementId=movement });
        await Assert.That(world.State.ClassKind).IsEqualTo(expected);
    }

    [Test]
    public async Task RealPhysicalizationInstallsAnimatedParametersAndTheirBodyConsumerWithoutMovingTheActor()
    {
        using var world = new World();
        using var body = world.CreateBody();
        body.RequestVelocity(new(2,0,0));
        world.Backend.Step(TimeSpan.FromMilliseconds(20)); //Drain the real wish before the separate immediate collider callback.
        var before = body.CopyState();
        await Assert.That(before.AppliedDescription.LivingMovement!.Inertia).IsEqualTo(8f);
        await Assert.That(before.AppliedDescription.LivingMovement.InertiaAcceleration).IsEqualTo(0f);
        var parameters = world.State.Physicalized(body, 0);
        using var animated = new ZoneAnimatedColliderController(() => world.State.IsCurrent && body.IsCurrent,
            body,parameters.Inertia,parameters.InertiaAcceleration,()=>false);
        animated.SetFrame(7); //Initial native reset frame0, observed later prephysics frame7; no invented force event.
        await Assert.That(animated.Request((int)ZoneAnimatedColliderMode.Pushable,3)).IsEqualTo(ZoneAnimatedColliderUpdateOutcome.Applied);
        var after = body.CopyState();
        await Assert.That(after.AppliedDescription.LivingMovement!.Inertia).IsEqualTo(20f);
        await Assert.That(after.AppliedDescription.LivingMovement.InertiaAcceleration).IsEqualTo(11f);
        await Assert.That(after.AppliedDescription.LivingMovement.ImpulseRecoveryTime).IsEqualTo(5f);
        await Assert.That(after.Status.Position).IsEqualTo(before.Status.Position);
        await Assert.That(after.Status.Velocity).IsEqualTo(before.Status.Velocity);
        await Assert.That(after.Control.RequestedVelocity).IsEqualTo(before.Control.RequestedVelocity);
        await Assert.That(after.Status.Orientation).IsEqualTo(before.Status.Orientation);
        await Assert.That(world.State.CopyAnimatedParameters()).IsEqualTo(new ZoneManagedNpcAnimatedParameters(20,11,5));
        await Assert.That(parameters.ParameterFlagsToSet).IsEqualTo(0x18u);
        await Assert.That(world.State.PhysicalizedFrame).IsEqualTo((int?)0);
        //Neither body creation nor this constructor event is a second scene pose publisher.
        await Assert.That(world.Npc.Unit.Pose).IsEqualTo(world.InitialUnitPose);
    }

    [Test]
    public async Task WrongPhysicalGenerationCannotInstallAndRetiredBodyCannotSupplyParameters()
    {
        using var first = new World(); using var other = new World();
        using var wrong = other.CreateBody();
        await Assert.That(() => first.State.Physicalized(wrong,1)).Throws<InvalidOperationException>();
        await Assert.That(first.State.PhysicalizedFrame).IsNull();
        using var body = first.CreateBody();
        first.State.Physicalized(body,1);
        body.Dispose();
        await Assert.That(() => first.State.CopyAnimatedParameters()).Throws<ObjectDisposedException>();
        await Assert.That(first.State.IsCurrent).IsTrue(); //Construction does not own the body lifetime.
    }

    [Test]
    public async Task BodyOwnerCallbackRetirementCannotCommitAnInstallation()
    {
        using var world = new World();
        var retire = false;
        using var body = world.CreateBody(() => { if(retire) world.State.Dispose(); return true; });
        retire = true;
        await Assert.That(() => world.State.Physicalized(body,4)).Throws<ObjectDisposedException>();
        await Assert.That(world.State.PhysicalizedFrame).IsNull();
        await Assert.That(world.Construction.Count).IsEqualTo(0);
        await Assert.That(body.IsCurrent).IsTrue();
    }

    [Test]
    public async Task MatchingNumericHandleOnAnotherBackendCannotSubstituteForTheActualNpcOwner()
    {
        using var actual = new World(); using var other = new World();
        var forgedNpc = other.Admission.Npc(actual.Npc.ObjectId,actual.Npc.Id,actual.Models.Models[17],17);
        using var forgedBody = other.Bodies.Create(actual.State.Actor,forgedNpc,new(1100,1100,12),Quaternion.Identity,()=>true);
        await Assert.That(forgedBody.Actor).IsEqualTo(actual.State.Actor);
        await Assert.That(forgedBody.Admission.ModelId).IsEqualTo(actual.State.ModelId);
        await Assert.That(forgedBody.IsCurrent).IsTrue();
        await Assert.That(() => actual.State.Physicalized(forgedBody,1)).Throws<InvalidOperationException>();
        await Assert.That(actual.State.PhysicalizedFrame).IsNull();
    }

    [Test]
    public async Task ReusedNumericIdentityGetsFreshHealthAndStateAndOldDisposalCannotRetireIt()
    {
        using var world = new World();
        var old = world.State;
        old.Health.CopyFields(4,21);
        world.Admission.Catalog.Retire(world.Npc);
        world.Admission.Registry.RemovePrepared(world.Npc.Unit);
        var replacement = world.Admission.Npc(world.Npc.ObjectId,world.Npc.Id,world.Models.Models[17],17);
        world.Admission.Publish(replacement);
        var handle = world.Admission.Catalog.Register(replacement);
        using var fresh = world.Construction.Create(handle,replacement);
        await Assert.That(handle.Generation).IsGreaterThan(old.Actor.Generation);
        await Assert.That(old.IsCurrent).IsFalse();
        await Assert.That(world.Construction.Find(old.Actor)).IsNull();
        await Assert.That(fresh.Health.MaximumHealth).IsEqualTo(10f);
        old.Dispose();
        await Assert.That(ReferenceEquals(world.Construction.Find(handle),fresh)).IsTrue();
    }

    [Test]
    public async Task InvalidDescriptorAdmissionIsAtomicAndDuplicateOwnerIsRejected()
    {
        using var world = new World();
        await Assert.That(() => world.Construction.Create(world.State.Actor,world.Npc)).Throws<InvalidOperationException>();
        var invalid = Models(Definition() with { GameInertia=float.NaN });
        using var owner = new ZoneManagedNpcActorConstruction(world.Admission.Registry,world.Admission.Catalog,invalid);
        await Assert.That(() => owner.Create(world.State.Actor,world.Npc)).Throws<NotSupportedException>();
        await Assert.That(owner.Count).IsEqualTo(0);
        world.Construction.Dispose();
        await Assert.That(world.State.IsDisposed).IsTrue();
        await Assert.That(world.Construction.Find(world.State.Actor)).IsNull();
    }

    private static ZoneActorModelDefinition Definition() => new()
    {
        Id=8,MovementId=2,GameMaxGrabMass=101,GameMaxGrabVolume=102,GameSprintMultiplier=103,
        GameForwardMultiplier=104,GameForwardDiagonalMultiplier=105,GameStrafeMultiplier=106,
        GameBackwardMultiplier=107,GameBackwardDiagonalMultiplier=108,GameGrabMultiplier=109,
        GameWalkMultiplier=110,GameWalkForwardDiagonalMultiplier=111,GameWalkStrafeMultiplier=112,
        GameWalkBackwardMultiplier=113,GameWalkBackwardDiagonalMultiplier=114,
        GameInertia=20,GameInertiaAccel=11,GameJumpHeight=117,GameLeanShift=118,GameLeanAngle=119,
        PhysicsLivingMass=80,PhysicsLivingGravity=25,PhysicsLivingKAirControl=.1f,
        PhysicsLivingMinSlideAngle=45,PhysicsLivingMaxClimbAngle=50,PhysicsLivingMinFallAngle=50,
        PhysicsLivingMaxVelGround=10,PhysicsLivingTimeImpulseRecover=5,
    };
    private static ZoneModelContent Models(ZoneActorModelDefinition definition) => new(
        [new() {Id=17,Name="constructed_npc",Subtype="ActorModel",SubtypeId=8}],actorModels:new(
        [definition],[new() {Id=1,ActorModelId=8,StanceId=2,SizeX=.3f,SizeY=.7f,SizeZ=.3f,
            HeightCollider=1.2f,HeightPivot=.1f,UseCapsule=true,MaxSpeed=3}]));

    private sealed class World : IDisposable
    {
        public ManagedNpcAdmissionTests.Fixture Admission { get; } = new();
        public ZoneModelContent Models { get; }
        public Npcs.ZoneNpc Npc { get; }
        public ZoneUnitPose InitialUnitPose { get; }
        public ZoneManagedPhysicsBackend Backend { get; } = new(new(new ZoneTerrainHeightField()));
        public ZoneManagedNpcPhysics Bodies { get; }
        public ZoneManagedNpcActorConstruction Construction { get; }
        public ZoneManagedNpcActorConstructionLease State { get; }
        public World(ZoneActorModelDefinition? definition=null,ZoneModelDefinition? templateModel=null)
        {
            Models=ManagedNpcActorConstructionTests.Models(definition??Definition());
            Npc=Admission.Npc(model:templateModel??Models.Models[17],unitModelId:17);
            InitialUnitPose=Npc.Unit.Pose;
            Admission.Publish(Npc);var handle=Admission.Catalog.Register(Npc);
            Bodies=new(Backend,Models,_=>143);
            Construction=new(Admission.Registry,Admission.Catalog,Models);State=Construction.Create(handle,Npc);
        }
        public ZoneManagedNpcBodyLease CreateBody(Func<bool>? extraCurrent=null) => Bodies.Create(State.Actor,Npc,
            new(1100,1100,12),Quaternion.Identity,
            ()=>ReferenceEquals(Admission.Catalog.Find(State.Actor),Npc)&&(extraCurrent?.Invoke()??true));
        public void Dispose() { Construction.Dispose();Bodies.Dispose();Admission.Dispose(); }
    }
}
