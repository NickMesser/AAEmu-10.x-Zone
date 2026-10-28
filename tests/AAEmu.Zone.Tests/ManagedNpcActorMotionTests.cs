using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Content;
using AAEmu.Zone.Hosting;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Scripting;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Tests;

public sealed class ManagedNpcActorMotionTests
{
    [Test]
    public async Task FreshCActorHasTheFinalNativeConstructorStateAndNeedsActualFrameMetadata()
    {
        using var world = new World();
        await Assert.That(world.Lease.State.Mode).IsEqualTo(ZoneManagedNpcActorMotionMode.Air);
        await Assert.That(world.Lease.State.AirborneSeconds).IsEqualTo(1f);
        await Assert.That(world.Lease.State.GroundedSeconds).IsEqualTo(0f);
        await Assert.That(world.Motion.CopyPuppetAlertness(world.Actor)).IsEqualTo((int?)0);
        await Assert.That(() => world.Motion.CopyReplicationState(world.Actor)).Throws<NotSupportedException>();
        world.Step();
        var state = world.Motion.CopyReplicationState(world.Actor);
        await Assert.That(state.Timestamp).IsEqualTo(7001u); //Independent of World-aligned100001ms.
        await Assert.That(state.ModelCanReplicate).IsFalse();
        await Assert.That(state.Delta).IsEqualTo(Vector3.Zero); //Actual CActor movement request constructor, not velocity.
        world.Motion.CompleteNpcInitialization(world.Actor);
        await Assert.That(world.Motion.CopyReplicationState(world.Actor).ModelCanReplicate).IsTrue();
    }

    [Test]
    public async Task RealShortFallAndLandingAdvanceActorTimersAndOneTransitionWithoutPublishingAUnitPose()
    {
        using var world = new World(z:10.5f);
        var originalUnitPose = world.Npc.Unit.Pose;
        world.Step();
        await Assert.That(world.Lease.State.ActorVelocity.Z).IsLessThan(0f);
        await Assert.That(world.Lease.State.AirborneSeconds).IsGreaterThan(1f);
        await Assert.That(world.Lease.State.GroundedSeconds).IsEqualTo(0f);
        for (var i=0;i<100 && world.Lease.State.GroundedSeconds==0;i++) world.Step();
        var landed = world.Motion.CopyReplicationState(world.Actor);
        await Assert.That(world.Lease.State.Mode).IsEqualTo(ZoneManagedNpcActorMotionMode.Ground);
        await Assert.That(world.Lease.State.AirborneSeconds).IsEqualTo(0f);
        await Assert.That(world.Lease.State.GroundedSeconds).IsEqualTo(.02f);
        await Assert.That(landed.ActorFlags & 0x14).IsEqualTo(0x14);
        await Assert.That(world.Lease.LandingCount).IsEqualTo(1ul);
        await Assert.That(world.Lease.State.FirstLanding).IsFalse();
        world.Step();
        await Assert.That(world.Lease.State.GroundedSeconds).IsEqualTo(.04f);
        await Assert.That(world.Motion.CopyReplicationState(world.Actor).ActorFlags & 0x10).IsEqualTo(0);
        await Assert.That(world.Lease.LandingCount).IsEqualTo(1ul);
        await Assert.That(world.Npc.Unit.Pose).IsEqualTo(originalUnitPose);
    }

    [Test]
    public async Task RelocationCopiesFreshBodyAndAcceptedStanceWhileActorVelocityAndTimestampStayAtTheirProducer()
    {
        using var world = new World(z:11);
        world.Step();
        var before = world.Motion.CopyReplicationState(world.Actor);
        var next = before.Body.Status.Position + new Vector3(4,3,2);
        new ZoneManagedNpcRelocation(world.Bodies).Relocate(world.Actor,next,_=>{});
        var after = world.Motion.CopyReplicationState(world.Actor);
        await Assert.That(after.Body.Status.Position).IsEqualTo(next);
        await Assert.That(after.ActorVelocity).IsEqualTo(before.ActorVelocity);
        await Assert.That(after.Timestamp).IsEqualTo(before.Timestamp);
        await Assert.That(world.Lease.State.Frame).IsEqualTo((uint?)1);
    }

    [Test]
    public async Task ZeroPhysicalGravityClearsActorVelocityWithoutClearingTheActualBodyVelocity()
    {
        using var world = new World(gravity:0,z:20);
        world.Body.RequestVelocity(new(3,0,0),ZonePhysicsLivingVelocityMode.Immediate);
        world.Step();
        var state = world.Motion.CopyReplicationState(world.Actor);
        await Assert.That(state.Body.Status.Velocity.X).IsGreaterThan(0f);
        await Assert.That(state.ActorVelocity).IsEqualTo(Vector3.Zero);
        await Assert.That(state.Delta).IsEqualTo(Vector3.Zero);
    }

    [Test]
    public async Task AllowStrafingIsTheRealGuardedAIStateAndUsesCachedTemplateModelRatherThanTheWireModel()
    {
        using var world = new World(templateSlope:true,wireSlope:false);
        world.Step();
        await Assert.That(world.Motion.CopyReplicationState(world.Actor).GuardedAllowStrafing).IsEqualTo((bool?)false);
        world.Actor.Controls.AllowStrafing=true;
        await Assert.That(world.Motion.CopyReplicationState(world.Actor).GuardedAllowStrafing).IsEqualTo((bool?)true);
        using var opposite = new World(templateSlope:false,wireSlope:true);
        opposite.Actor.Controls.AllowStrafing=true;opposite.Step();
        await Assert.That(opposite.Motion.CopyReplicationState(opposite.Actor).GuardedAllowStrafing).IsNull();
    }

    [Test]
    public async Task UnprovedWetProfileAndMissingFrameClockRefuseBeforeCommittingAnyCActorStatistics()
    {
        using var world = new World(ocean:10);
        world.Backend.Step(TimeSpan.FromMilliseconds(20));
        var before = world.Lease.State;
        await Assert.That(() => world.Motion.Update(world.Actor,world.Body.CopyState(),world.Frame(1))).Throws<NotSupportedException>();
        await Assert.That(world.Lease.State).IsEqualTo(before);
        using var dry = new World();
        dry.Backend.Step(TimeSpan.FromMilliseconds(20));
        await Assert.That(() => dry.Motion.Update(dry.Actor,dry.Body.CopyState(),dry.Frame(1) with {UnpausedMilliseconds=null})).Throws<NotSupportedException>();
        await Assert.That(dry.Lease.State.Frame).IsNull();
    }

    [Test]
    public async Task DuplicateFrameForgedSnapshotAndGenerationRetirementCannotCommit()
    {
        using var world = new World();world.Step();var before=world.Lease.State;
        await Assert.That(() => world.Motion.Update(world.Actor,world.Body.CopyState(),world.Frame(1))).Throws<InvalidOperationException>();
        await Assert.That(() => world.Motion.Update(world.Actor,world.Body.CopyState() with
            {Status=world.Body.CopyState().Status with {Position=new(1,2,3)}},world.Frame(2))).Throws<InvalidOperationException>();
        await Assert.That(world.Lease.State).IsEqualTo(before);
        world.Admission.Catalog.Retire(world.Actor.Handle);
        await Assert.That(() => world.Motion.CopyReplicationState(world.Actor)).Throws<ObjectDisposedException>();
        await Assert.That(world.Lease.State).IsEqualTo(before);
        world.Lease.Dispose();await Assert.That(world.Motion.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AssetCallbackRetirementCannotPublishAThresholdOrMotionSample()
    {
        using var world = new World();
        world.Backend.Step(TimeSpan.FromMilliseconds(20));
        world.Assets.Opened=()=>world.Construction.Dispose();
        var before=world.Lease.State;
        await Assert.That(() => world.Motion.Update(world.Actor,world.Body.CopyState(),world.Frame(1))).Throws<ObjectDisposedException>();
        await Assert.That(world.Lease.State).IsEqualTo(before);
        await Assert.That(world.Motion.Find(world.Actor.Handle)).IsNull();
    }

    [Test]
    public async Task InitialThresholdUsesOriginalSemanticPriorityLastCRCMatchAndAbsolutePoseMeters()
    {
        var assets=new Assets();assets.Bytes=Skeleton(("camera",8),("Bone_Swim",1.1f),("BONE_SWIM",1.4f),("Bip01 Spine3",6));
        var definition=ZoneCharacterDefinitionContent.Load(assets,"objects/motion.chr");
        var result=ZoneCharacterInitialSwimThreshold.Load(assets,definition);
        await Assert.That(result.BoneIndex).IsEqualTo(2);
        await Assert.That(result.Height).IsEqualTo(1.4f);
        assets.Bytes=Skeleton(("root",0));
        definition=ZoneCharacterDefinitionContent.Load(assets,"objects/motion.chr");
        await Assert.That(() => ZoneCharacterInitialSwimThreshold.Load(assets,definition)).Throws<NotSupportedException>();
    }

    private static byte[] Skeleton(params (string Name,float Height)[] bones)
    {
        const int chunk=44;var size=48+bones.Length*324;
        using var output=new MemoryStream();using var writer=new BinaryWriter(output,Encoding.UTF8,true);
        writer.Write("CryTek\0\0"u8);writer.Write(0xffff0000u);writer.Write(0x745u);writer.Write(20);
        writer.Write(1);writer.Write(0xacdc0000u);writer.Write(0x801u);writer.Write(chunk);writer.Write(19);writer.Write(size);
        writer.Write(0xacdc0000u);writer.Write(0x801u);writer.Write(chunk);writer.Write(19);writer.Write(new byte[size-16]);
        var bytes=output.ToArray();
        for(var i=0;i<bones.Length;i++)
        {
            var at=chunk+48+i*324;Encoding.UTF8.GetBytes(bones[i].Name).CopyTo(bytes,at+216);
            foreach(var diagonal in new[]{0,5,10})BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at+276+diagonal*4,4),BitConverter.SingleToInt32Bits(1));
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at+320,4),BitConverter.SingleToInt32Bits(bones[i].Height));
        }
        return bytes;
    }
    internal sealed class Assets:IZoneAssetSource
    {
        internal byte[] Bytes=Skeleton(("camera",1.5f));internal Action? Opened;
        public Stream? OpenRead(string virtualPath){Opened?.Invoke();return virtualPath=="game/objects/motion.chr"?new MemoryStream(Bytes,false):null;}
    }
    internal sealed class World:IDisposable
    {
        public ManagedNpcAdmissionTests.Fixture Admission{get;}=new();
        public ZoneManagedLuaRuntime Lua{get;}=new();public Assets Assets{get;}=new();
        public ZoneManagedPhysicsBackend Backend{get;}public ZoneManagedNpcPhysics Bodies{get;}
        public ZoneManagedNpcActorScene Actors{get;}public ZoneManagedNpcActorConstruction Construction{get;}
        public ZoneManagedNpcActor Actor{get;}public Npcs.ZoneNpc Npc{get;}public ZoneManagedNpcBodyLease Body{get;}
        public ZoneManagedCharacterSlots Characters{get;}public ZoneManagedWaterQueries Water{get;}
        public ZoneManagedNpcActorMotion Motion{get;}public ZoneManagedNpcActorMotionLease Lease{get;}
        public ZoneModelContent Models{get;}
        private uint _frame;
        public World(float gravity=25,float z=10.1f,float ocean=0,bool templateSlope=false,bool wireSlope=false,
            float airResistance=0,ZoneEngineCVars? engineCVars=null,bool supplyTerrain=true)
        {
            var rows=new[]{new ZoneActorModelDefinition{Id=8,ModelFile="objects/motion.chr",SlopeAlignment=wireSlope,
                PhysicsLivingMass=80,PhysicsLivingGravity=gravity,PhysicsLivingKAirControl=.1f,
                PhysicsLivingAirResistance=airResistance,
                PhysicsLivingMinSlideAngle=45,PhysicsLivingMinFallAngle=50,PhysicsLivingMaxClimbAngle=50,
                PhysicsLivingTimeImpulseRecover=1,PhysicsLivingMaxVelGround=10},
                new ZoneActorModelDefinition{Id=9,SlopeAlignment=templateSlope}};
            var models=new ZoneModelContent([new(){Id=17,Name="wire_motion",Subtype="ActorModel",SubtypeId=8},
                new(){Id=19,Name="cached_template",Subtype="ActorModel",SubtypeId=9}],actorModels:new(rows,
                [new(){Id=1,ActorModelId=8,StanceId=2,SizeX=.3f,SizeY=.7f,SizeZ=.3f,
                    HeightCollider=1.2f,HeightPivot=.1f,UseCapsule=true,ViewOffsetZ=1.5f}]));
            Models=models;
            Npc=Admission.Npc(model:models.Models[19],unitModelId:17,cleanSkillState:true);Admission.Publish(Npc);
            var handle=Admission.Catalog.Register(Npc);
            Actors=new(Admission.Registry,Admission.Catalog,Lua,new(),unit=>ZoneMovementPoseMath.ToMeters(unit.Pose.Position),
                ()=>0,()=>1,_=>{},TimeSpan.FromMilliseconds(20));
            Actor=Actors.Adopt(handle,new(){EntityId=99_000_001},5,1,ZoneManagedNpcActorScene.CreatePipeUserControls(),true,null);
            var heights=new ZoneTerrainHeightField();heights.LoadCell(1,1,TerrainHeightFieldTests.CreateCell(10,0));
            Backend=new(new(heights));Bodies=new(Backend,models,_=>143);
            Body=Bodies.Create(handle,Npc,new(1100,1100,z),Quaternion.Identity,()=>ReferenceEquals(Actors.Find(handle),Actor));
            Construction=new(Admission.Registry,Admission.Catalog,models);Construction.Create(handle,Npc).Physicalized(Body,0);
            Characters=new(Assets,h=>ReferenceEquals(Actors.Find(h),Actor));Characters.BindInitial(handle,models.ActorForModel(17)!);
            var layout=new ManagedSceneActivationTests.Fixture((9,14),(10,15)).Layout;
            var waterCells=layout.Cells.Select(placement=>new ZoneManagedSceneCellLoadReport(placement,
                new(ZoneTerrainCellLoadStatus.Empty),new(null,null,null,null,
                    ["Objects: authored asset is absent.","BigObjects: authored asset is absent."]))).ToArray();
            Water=new(ocean,waterCells,ZoneManagedWaterQueryProfile.StaticServerWithoutOceanVortex);
            Motion=new(Actors,Bodies,Construction,Water,Characters,Assets,models,
                supplyTerrain?heights:null,engineCVars);Lease=Motion.Bind(Actor);
        }
        public ZoneMovementFrame Frame(uint number)=>new(number,TimeSpan.FromMilliseconds(20),100000+number,number*.02f)
            {LocalGameMilliseconds=number*20,UnpausedMilliseconds=7000+number};
        public void Step(){Backend.Step(TimeSpan.FromMilliseconds(20));Motion.Update(Actor,Body.CopyState(),Frame(++_frame));}
        public void Dispose(){Motion.Dispose();Water.Dispose();Characters.Dispose();Construction.Dispose();Bodies.Dispose();Actors.Dispose();Admission.Dispose();Lua.Dispose();}
    }
}
