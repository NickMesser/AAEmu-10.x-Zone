using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Content;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Scripting;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Tests;

public sealed class ManagedNpcAiPoseTests
{
    [Test]
    public async Task ActualFacingAndAcceptedStancePublishViewPositionWithoutChangingEntityOrBodyPose()
    {
        var rotation=Quaternion.CreateFromAxisAngle(Vector3.UnitZ,.7f);
        using var world=new World(rotation);
        var before=world.Body.CopyState();var unit=world.Npc.Unit.Pose;
        var sample=world.Pose.Refresh();
        var forward=Vector3.Transform(Vector3.UnitY,rotation);
        await Near(sample.BodyDirection,forward);await Near(sample.MoveDirection,forward);await Near(sample.ViewDirection,forward);
        await Near(sample.Position,before.Status.Position+Vector3.Transform(before.CurrentStance.ViewOffset,rotation));
        await Assert.That(sample.Position).IsNotEqualTo(sample.EntityPosition);
        await Assert.That(world.Actor.AiObject.Position).IsEqualTo(sample.Position);
        await Assert.That(world.Body.CopyState().Status.Position).IsEqualTo(before.Status.Position);
        await Assert.That(world.Npc.Unit.Pose).IsEqualTo(unit);
        await Assert.That(world.Pose.SampleCount).IsEqualTo(1ul);
    }

    [Test]
    public async Task BodyMoveAndViewAreIndependentAndReferencePredicateUsesTheOriginalGetters()
    {
        using var world=new World();
        var first=world.Pose.Refresh();
        world.Pose.CopyActorMovementOutput(new(2,0,0));
        world.Pose.CopyControllerLookTarget(first.Position+new Vector3(0,0,10));
        var sample=world.Pose.Refresh();
        await Near(sample.BodyDirection,Vector3.UnitY);await Near(sample.MoveDirection,Vector3.UnitX);await Near(sample.ViewDirection,Vector3.UnitZ);
        world.Actors.Points.SetReferencePosition(world.Actor,new(20,30,40));
        await Near(world.Actor.Points!.Reference.State.MoveDirection,Vector3.UnitX);
        await Assert.That(world.Actors.Points.IsSimilarReferenceDirection(world.Actor)).IsFalse();
        world.Actors.Points.SetReferenceDirection(world.Actor,Vector3.UnitY);
        await Assert.That(world.Actors.Points.IsSimilarReferenceDirection(world.Actor)).IsTrue();
        //View remains vertical. A mislabeled View/Body/Move mapping would fail this predicate.
        await Near(world.Actor.AiObject.ViewDirection,Vector3.UnitZ);
    }

    [Test]
    public async Task FallingVelocityDoesNotBecomeMoveDirectionAndDesiredPuppetControlsDoNotBecomeActorOutput()
    {
        using var world=new World();
        world.Actor.Controls.MovementDirection=new(1,0,0);
        world.Backend.Step(TimeSpan.FromMilliseconds(20));
        await Assert.That(world.Body.CopyState().Status.Velocity.Z).IsLessThan(0f);
        var sample=world.Pose.Refresh();
        await Near(sample.MoveDirection,Vector3.UnitY);
        await Assert.That(sample.ActorMovementOutput).IsEqualTo(Vector3.Zero);
    }

    [Test]
    public async Task UnownedPhysicalWishRefusesAtomicallyUntilTheActualActorOutputIsCopied()
    {
        using var world=new World();var before=world.Pose.Refresh();
        world.Body.RequestVelocity(new(2,0,0));world.Backend.Step(TimeSpan.FromMilliseconds(20));
        await Assert.That(() => world.Pose.Refresh()).Throws<NotSupportedException>();
        await Assert.That(world.Pose.Last).IsEqualTo(before);
        await Assert.That(world.Actor.AiObject.Position).IsEqualTo(before.Position);
        world.Pose.CopyActorMovementOutput(new(2,0,0));
        var sample=world.Pose.Refresh();await Near(sample.MoveDirection,Vector3.UnitX);
        await Assert.That(sample.ActorMovementOutput).IsNotEqualTo(world.Body.CopyState().Status.Velocity);
    }

    [Test]
    public async Task ViewTargetUsesThePreviousCachedViewPositionBeforeRefreshingTheNewPosition()
    {
        using var world=new World();var initial=world.Pose.Refresh();
        world.Pose.CopyControllerLookTarget(initial.Position+new Vector3(10,0,0));
        world.Backend.Teleport(world.Body.Actor,initial.EntityPosition+new Vector3(0,5,0),Quaternion.Identity);
        var first=world.Pose.Refresh();await Near(first.ViewDirection,Vector3.UnitX);
        await Near(first.Position,initial.Position+new Vector3(0,5,0));
        var second=world.Pose.Refresh();await Near(second.ViewDirection,Vector3.Normalize(new Vector3(10,-5,0)));
    }

    [Test]
    public async Task LookTargetCannotInventThePostResetViewCacheAndZeroTargetUsesFacing()
    {
        using var world=new World();
        world.Pose.CopyControllerLookTarget(new(100,20,30));
        await Assert.That(() => world.Pose.Refresh()).Throws<NotSupportedException>();
        await Assert.That(world.Pose.Last).IsNull();
        world.Pose.CopyControllerViewPosition(new(90,20,30));
        await Near(world.Pose.Refresh().ViewDirection,Vector3.UnitX);
        world.Pose.CopyControllerLookTarget(Vector3.Zero);
        await Near(world.Pose.Refresh().ViewDirection,Vector3.UnitY);
    }

    [Test]
    public async Task TinyMovementFallsBackToFacingAndCoincidentLookFallsBackToWorldForward()
    {
        using var world=new World();var first=world.Pose.Refresh();
        world.Pose.CopyActorMovementOutput(new(.0625f,0,0));
        world.Pose.CopyControllerLookTarget(first.Position);
        var sample=world.Pose.Refresh();await Near(sample.MoveDirection,Vector3.UnitY);await Near(sample.ViewDirection,Vector3.UnitY);
        world.Pose.CopyActorMovementOutput(new(.2f,0,0));
        await Near(world.Pose.Refresh().MoveDirection,Vector3.UnitX);
    }

    [Test]
    public async Task RetiredOwnerAndInvalidCopiedValuesCannotPublishAnotherPose()
    {
        using var world=new World();var first=world.Pose.Refresh();
        await Assert.That(() => world.Pose.CopyActorMovementOutput(new(float.NaN,0,0))).Throws<ArgumentException>();
        await Assert.That(() => world.Pose.CopyControllerLookTarget(new(float.MaxValue))).Throws<ArgumentException>();
        await Assert.That(() => world.Pose.CopyActorViewRotation(new(0,0,0,2))).Throws<NotSupportedException>();
        world.Construction.Dispose();
        await Assert.That(world.Poses.Find(world.Actor.Handle)).IsNull();
        await Assert.That(() => world.Pose.Refresh()).Throws<ObjectDisposedException>();
        await Assert.That(world.Actor.AiObject.Position).IsEqualTo(first.Position);
        world.Pose.Dispose();await Assert.That(world.Poses.Count).IsEqualTo(0);
    }

    [Test]
    public async Task BodyCurrencyCallbackCannotPublishAfterRetiringConstruction()
    {
        using var world=new World();var initial=world.Pose.Refresh();
        world.DuringBodyCheck=world.Construction.Dispose;
        await Assert.That(() => world.Pose.Refresh()).Throws<ObjectDisposedException>();
        await Assert.That(world.Pose.Last).IsEqualTo(initial);
        await Assert.That(world.Actor.AiObject.Position).IsEqualTo(initial.Position);
        await Assert.That(world.Pose.SampleCount).IsEqualTo(1ul);
    }

    private static async Task Near(Vector3 actual,Vector3 expected) =>
        await Assert.That(Vector3.Distance(actual,expected)).IsLessThan(.00001f);

    private sealed class World : IDisposable
    {
        public ManagedNpcAdmissionTests.Fixture Admission { get; }=new();
        public ZoneManagedLuaRuntime Lua { get; }=new();
        public ZoneManagedPhysicsBackend Backend { get; }=new(new(new ZoneTerrainHeightField()));
        public ZoneManagedNpcPhysics Bodies { get; }
        public ZoneManagedNpcActorScene Actors { get; }
        public ZoneManagedNpcActorConstruction Constructions { get; }
        public ZoneManagedNpcActorConstructionLease Construction { get; }
        public ZoneManagedNpcBodyLease Body { get; }
        public ZoneManagedNpcActor Actor { get; }
        public Npcs.ZoneNpc Npc { get; }
        public ZoneManagedNpcAiPose Poses { get; }
        public ZoneManagedNpcAiPoseLease Pose { get; }
        public Action? DuringBodyCheck { get; set; }
        public World(Quaternion? initialRotation=null)
        {
            var models=new ZoneModelContent([new(){Id=17,Name="pose_npc",Subtype="ActorModel",SubtypeId=8}],actorModels:new(
                [new(){Id=8,MovementId=0,PhysicsLivingMass=80,PhysicsLivingGravity=25,PhysicsLivingKAirControl=.1f,
                    PhysicsLivingMinSlideAngle=45,PhysicsLivingMinFallAngle=50,PhysicsLivingMaxClimbAngle=50,
                    PhysicsLivingTimeImpulseRecover=1,PhysicsLivingMaxVelGround=10}],
                [new(){Id=1,ActorModelId=8,StanceId=2,SizeX=.3f,SizeY=.7f,SizeZ=.3f,
                    HeightCollider=1.2f,HeightPivot=.1f,UseCapsule=true,ViewOffsetX=.1f,ViewOffsetY=.2f,ViewOffsetZ=1.5f}]));
            Npc=Admission.Npc(model:models.Models[17],unitModelId:17);
            var rotation=initialRotation??Quaternion.Identity;
            Npc.Unit.CommitPose(new(ZoneMovementPoseMath.ToUnitWorldPosition(new(10,20,30)),rotation));
            Admission.Publish(Npc);var handle=Admission.Catalog.Register(Npc);
            Actors=new(Admission.Registry,Admission.Catalog,Lua,new(),
                unit=>ZoneMovementPoseMath.ToMeters(unit.Pose.Position),()=>0,()=>1,_=>{},TimeSpan.FromMilliseconds(20));
            Actor=Actors.Adopt(handle,new(){EntityId=Npc.ObjectId+10_000_000},5,(ulong)handle.Generation<<20,
                ZoneManagedNpcActorScene.CreatePipeUserControls(),true,null);
            Bodies=new(Backend,models,_=>143);
            Body=Bodies.Create(handle,Npc,new(10,20,30),rotation,()=>
            { DuringBodyCheck?.Invoke();return ReferenceEquals(Actors.Find(handle),Actor); });
            Constructions=new(Admission.Registry,Admission.Catalog,models);Construction=Constructions.Create(handle,Npc);
            Construction.Physicalized(Body,0);
            Poses=new(Actors,Bodies);Pose=Poses.Bind(Actor,Construction);
        }
        public void Dispose(){Poses.Dispose();Constructions.Dispose();Bodies.Dispose();Actors.Dispose();Admission.Dispose();Lua.Dispose();}
    }
}
