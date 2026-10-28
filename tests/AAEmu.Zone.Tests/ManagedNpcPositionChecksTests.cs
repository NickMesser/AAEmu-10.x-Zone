using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Hosting;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Scripting;

namespace AAEmu.Zone.Tests;

public sealed class ManagedNpcPositionChecksTests
{
    [Test]
    public async Task FreshSameRegionNoneSkipsAnchorsAndSurvivesItsFirstScheduledPositionCheckWithoutALuaSetter()
    {
        using var world=new World();var before=world.Body.CopyState();
        await Assert.That(world.Checks.CopyContext(world.Actor)).IsEqualTo(0);
        //The original NONE branch never reads these anchors, including before a behavior
        //constructor has installed its first NpcSetCheckPosContext request.
        world.RawAnchor("idlePos","not-a-vector");world.RawAnchor("talkPos","not-a-vector");
        await Assert.That(world.Checks.Check(world.Actor).Relocated).IsFalse();
        world.Fixture.Admission.Simulation.Update(128,false);
        await Assert.That(world.Actor.HasActor).IsTrue();
        await Assert.That(world.Checks.IsBound(world.Actor)).IsTrue();
        await Assert.That(world.Checks.CopyContext(world.Actor)).IsEqualTo(0);
        await Assert.That(world.Checks.CorrectionCount).IsEqualTo(0ul);
        await Assert.That(world.Body.CopyState()).IsEqualTo(before);
        await world.AssertPublished(before.Status.Position);
    }

    [Test]
    [Arguments(1,15f)]
    [Arguments(2,7f)]
    [Arguments(3,7f)]
    [Arguments(6,15f)]
    [Arguments(7,3f)]
    public async Task BoundLuaContextsUseStrictThreeDimensionalThresholdsAndTheOriginalAnchor(int context,float distance)
    {
        using var world=new World();
        world.SetContext(context);
        var position=world.Position;
        var field=context==3?"talkPos":"idlePos";
        world.Anchor(field,position+new Vector3(0,distance,0));
        await Assert.That(world.Checks.Check(world.Actor).Relocated).IsFalse();
        await Near(world.Position,position);
        var destination=position+new Vector3(0,distance,1);
        world.Anchor(field,destination);
        var beforeActor=world.Motion.CopyReplicationState(world.Actor);
        var result=world.Checks.Check(world.Actor);
        await Assert.That(result.Relocated).IsTrue();
        await Assert.That(result.RestoredPosture).IsEqualTo(context is 2 or 3);
        await world.AssertPublished(destination);
        //Relocation updates the model/body. CActor228 remains the earlier physical producer.
        var afterActor=world.Motion.CopyReplicationState(world.Actor);
        await Assert.That(afterActor.ActorVelocity).IsEqualTo(beforeActor.ActorVelocity);
        await Assert.That(afterActor.Timestamp).IsEqualTo(beforeActor.Timestamp);
        await Assert.That(afterActor.ModelCanReplicate).IsTrue(); //Actual Runtime postconstructor lifecycle.
        var frames=world.Drain();
        await Assert.That(frames.Count(frame=>frame.Opcode==ZwOpcodes.UnitModelPostureChanged)).IsEqualTo(context is 2 or 3?1:0);
        await Assert.That(world.Replication.CopyState(world.Actor).LastSync).IsEqualTo(0u);
    }

    [Test]
    [Arguments(0)]
    [Arguments(4)]
    [Arguments(5)]
    [Arguments(99)]
    public async Task SameRegionNoOpAndUnknownContextsDoNotInventAnchors(int context)
    {
        using var world=new World();world.SetContext(context);var before=world.Position;
        await Assert.That(world.Checks.Check(world.Actor).Relocated).IsFalse();
        await Near(world.Position,before);
        await Assert.That(world.Checks.CorrectionCount).IsEqualTo(0ul);
    }

    [Test]
    public async Task MissingWrongAndOtherRegionAnchorsNeverBecomeZeroOrAnArbitraryTeleport()
    {
        using var world=new World();world.SetContext(1);var before=world.Position;
        await Assert.That(world.Checks.Check(world.Actor).Relocated).IsFalse();
        world.RawAnchor("idlePos","wrong-type");
        await Assert.That(world.Checks.Check(world.Actor).Relocated).IsFalse();
        world.Anchor("idlePos",new(1160,1160,10.1f)); //A different canonical Zone row owns this sector.
        await Assert.That(ReferenceEquals(world.Fixture.Regions!.GetRegion(new(1160,1160,0)),world.Fixture.Regions.CurrentRegion)).IsFalse();
        await Assert.That(world.Checks.Check(world.Actor).Relocated).IsFalse();
        await Near(world.Position,before);
        await Assert.That(world.Checks.CorrectionCount).IsEqualTo(0ul);
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ActualGroupedNonLeaderSkipsTheThreeOriginalContextsWithoutElectingTheFirstMember(int context)
    {
        using var world=new World(group:23);world.SetContext(context);
        await Assert.That(world.Fixture.Groups!.GetGroupId(world.Actor)).IsEqualTo(23);
        var group=world.Fixture.Groups.CopyGroup(23)!;
        await Assert.That(group.Members.Single()).IsEqualTo(world.Actor.Handle);
        await Assert.That(group.HasLeaderObject).IsFalse();
        await Assert.That(world.Fixture.Groups.GetGroupLeader(23)).IsNull();
        world.Anchor(context==3?"talkPos":"idlePos",world.Position+new Vector3(0,50,0));
        await Assert.That(world.Checks.Check(world.Actor).Relocated).IsFalse();
        await Assert.That(world.Checks.CorrectionCount).IsEqualTo(0ul);
    }

    [Test]
    public async Task GroupedContextSixHasItsFiftyMeterThresholdAndContextSevenDoesNotInheritTheGroupSkip()
    {
        using var world=new World(group:23);world.SetContext(6);var position=world.Position;
        world.Anchor("idlePos",position+new Vector3(0,50,0));
        await Assert.That(world.Checks.Check(world.Actor).Relocated).IsFalse();
        world.Anchor("idlePos",position+new Vector3(0,50,1));
        await Assert.That(world.Checks.Check(world.Actor).Relocated).IsTrue();
        world.SetContext(7);world.Anchor("idlePos",world.Position+new Vector3(0,-4,0));
        await Assert.That(world.Checks.Check(world.Actor).Relocated).IsTrue();
        await Assert.That(world.Checks.CorrectionCount).IsEqualTo(2ul);
    }

    [Test]
    public async Task FreshOutsideRegionNoneWithoutALuaSetterRecoversSpawnAndForcesTheConcreteReplicaWireRecord()
    {
        using var world=new World();var spawn=world.Position;
        await Assert.That(world.Checks.CopyContext(world.Actor)).IsEqualTo(0);
        world.Anchor("spawnPos",spawn);
        world.RelocateForSetup(new(1160,1160,spawn.Z));
        await Assert.That(world.Motion.CopyPuppetAlertness(world.Actor)).IsEqualTo((int?)0);
        var result=world.Checks.Check(world.Actor);
        await Assert.That(result.Relocated).IsTrue();
        await Assert.That(world.Checks.CopyContext(world.Actor)).IsEqualTo(0);
        await world.AssertPublished(spawn);
        await Assert.That(world.Replication.Sample(world.Actor,world.Fixture.CurrentFrame)).IsTrue();
        await Assert.That(world.Replication.Flush()).IsEqualTo(1);
        var frame=world.Drain().Single(f=>f.Opcode==ZwOpcodes.UnitMovements);
        var movement=Decode(frame).Single();
        await Assert.That(movement.ObjectId).IsEqualTo(world.Actor.Npc.ObjectId);
        await Assert.That(movement.Movement.Kind).IsEqualTo((byte)1);
        await Assert.That(movement.Movement.Time).IsEqualTo(world.Fixture.CurrentFrame.UnpausedMilliseconds!.Value);
        await Assert.That(movement.Movement.Time).IsNotEqualTo(world.Fixture.CurrentFrame.ServerMilliseconds);
        await Near(new((float)movement.Movement.Position.X,(float)movement.Movement.Position.Y,movement.Movement.Position.Z),spawn,.002f);
        await Assert.That(((UnitActorMovement)movement.Movement.Details).Stance).IsEqualTo((sbyte)world.Body.CurrentStanceSlot);
        await Assert.That(world.Replication.CopyState(world.Actor).LastSync).IsEqualTo(world.Fixture.CurrentFrame.LocalGameMilliseconds!.Value);
    }

    [Test]
    public async Task OutsideRegionMissingSpawnRefusesBeforeAnyPhysicalOrPublicationMutation()
    {
        using var world=new World();world.SetContext(0);world.RelocateForSetup(new(1160,1160,10.1f));
        var before=world.Body.CopyState();var unit=world.Actor.Npc.Unit.Pose;
        await Assert.That(()=>world.Checks.Check(world.Actor)).Throws<NotSupportedException>();
        await Assert.That(world.Body.CopyState()).IsEqualTo(before);
        await Assert.That(world.Actor.Npc.Unit.Pose).IsEqualTo(unit);
        await Assert.That(world.Checks.CorrectionCount).IsEqualTo(0ul);
    }

    [Test]
    public async Task PerNpcSimulationRunsPositionAtItsOriginalCadenceIncludingTheCachedDeadBranch()
    {
        using var world=new World();world.SetContext(1);world.Anchor("idlePos",world.Position+new Vector3(0,30,0));
        await Assert.That(world.Fixture.Admission.Simulation.FindByObjectId(world.Actor.Npc.ObjectId)).IsEqualTo(world.Actor.Npc);
        for(uint frame=1;frame<128;frame++)world.Fixture.Admission.Simulation.Update(frame,false);
        await Assert.That(world.Checks.CorrectionCount).IsEqualTo(0ul);
        world.Fixture.Admission.Registry.Kill(new UnitDeath(world.Actor.Npc.ObjectId).ToFrame().Body.Span);
        await Assert.That(world.Actor.Npc.Unit.IsDead).IsTrue();
        //Position is outside the native !dead subbranches; it still runs onoffset&127==0.
        world.Fixture.Admission.Simulation.Update(128,false);
        await Assert.That(world.Checks.CorrectionCount).IsEqualTo(1ul);
        await world.AssertPublished(new(1100,1130,world.Position.Z));
        await Assert.That(world.Replication.PendingCount).IsEqualTo(0);
    }

    [Test]
    public async Task RetirementDiscardsPendingRowsAndSameNumericReentryKeepsIndependentCacheAndBodyGenerations()
    {
        using var world=new World();world.Replication.Reset(world.Actor);
        await Assert.That(world.Replication.Sample(world.Actor,world.Fixture.CurrentFrame)).IsTrue();
        var old=world.Actor;var body=world.Body;var oldCache=world.Replication.CopyState(old);
        var writer=new ZoneWriter();writer.WriteBc(old.Npc.ObjectId);
        await Assert.That(world.Fixture.Admission.Registry.Remove(writer.ToArray())).IsTrue();
        await Assert.That(body.IsDisposed).IsTrue();
        await Assert.That(world.Replication.PendingCount).IsEqualTo(0);
        await Assert.That(world.Replication.Flush()).IsEqualTo(0);
        await Assert.That(()=>world.Checks.Check(old)).Throws<ObjectDisposedException>();
        var replacement=world.Fixture.Admission.CreateFromWire(world.Fixture.Model.Id,new(1100,1100,10.1f));
        var handle=world.Fixture.Admission.Catalog.TryGet(replacement)!.Value;
        await Assert.That(handle.Generation).IsGreaterThan(old.Handle.Generation);
        var fresh=world.Fixture.Runtime.Actors.Find(handle)!;
        await Assert.That(fresh.HasActor).IsTrue();
        await Assert.That(world.Checks.CopyContext(fresh)).IsEqualTo(0);
        await Assert.That(world.Fixture.Motion!.Find(handle)!.State.Frame).IsNull();
        await Assert.That(world.Replication.CopyState(fresh)).IsNotEqualTo(oldCache);
        await Assert.That(world.Body.IsCurrent).IsFalse();
    }

    [Test]
    public async Task RealActorBufferDropsOnlyThe1001stRowAndRetirementSuppressesEveryUnflushedRow()
    {
        using var world=new World(fastest:true);world.Replication.Reset(world.Actor);
        for(var i=0;i<1001;i++)world.Replication.Sample(world.Actor,world.Fixture.CurrentFrame);
        await Assert.That(world.Replication.PendingCount).IsEqualTo(1000);
        await Assert.That(world.Replication.DroppedMovementCount).IsEqualTo(1ul);
        await Assert.That(world.Replication.SampledMovementCount).IsEqualTo(1001ul);
        await Assert.That(world.Replication.CopyState(world.Actor).LastSync).IsEqualTo(world.Fixture.CurrentFrame.LocalGameMilliseconds!.Value);
        await Assert.That(world.Replication.Flush()).IsEqualTo(1000);
        var movements=Decode(world.Drain().Single(frame=>frame.Opcode==ZwOpcodes.UnitMovements));
        await Assert.That(movements.Count).IsEqualTo(1000);
        await Assert.That(movements.All(m=>m.ObjectId==world.Actor.Npc.ObjectId)).IsTrue();
        world.Replication.Sample(world.Actor,world.Fixture.CurrentFrame);
        world.Fixture.Admission.World.Remove(world.Actor.Npc);
        await Assert.That(world.Replication.PendingCount).IsEqualTo(0);
        await Assert.That(world.Replication.Flush()).IsEqualTo(0);
    }

    private static List<UnitMovement> Decode(ZoneFrame frame)
    {
        var reader=new ZoneReader(frame.Body.Span);var count=reader.ReadInt32();var rows=new List<UnitMovement>();
        //These controls emit a single row or repeated copies of the same actor state.
        //Use the public shared decoder on each equally sized row, including its own full-consumption check.
        if(count<=0||reader.Remaining%count!=0)throw new InvalidDataException("Expected uniform actor rows.");
        var rowLength=reader.Remaining/count;
        for(var i=0;i<count;i++)rows.Add(UnitMovement.Read(reader.ReadBytes(rowLength)));
        reader.EnsureFullyConsumed();return rows;
    }
    private static async Task Near(Vector3 actual,Vector3 expected,float tolerance=.0001f)=>
        await Assert.That(Vector3.Distance(actual,expected)).IsLessThan(tolerance);

    private sealed class World:IDisposable
    {
        public ManagedNpcRuntimeTests.Fixture Fixture{get;}
        public ZoneManagedNpcActor Actor{get;}
        public ZoneManagedNpcBodyLease Body=>Fixture.Bodies!.Find(Actor.Handle)??_body;
        private readonly ZoneManagedNpcBodyLease _body;
        private readonly ZoneManagedNpcMovementFramePhases _phases;
        public ZoneManagedNpcPositionChecks Checks=>Fixture.PositionChecks!;
        public ZoneManagedNpcReplication Replication=>Fixture.Replication!;
        public ZoneManagedNpcActorMotion Motion=>Fixture.Motion!;
        public Vector3 Position=>Fixture.Components.CopyModelTransform(Actor.Npc.Unit).Position;
        public World(int group=0,bool fastest=false)
        {
            Fixture=new(withMovement:true,movementCVars:fastest?ZoneEngineCVars.Parse("npc_enable_fastest_sync=1"):null);
            if(group!=0)Fixture.Runtime.Lua.SetGlobal("testGroup",group);
            var npc=Fixture.Admission.CreateFromWire(Fixture.Model.Id,new(1100,1100,10.1f));
            var handle=Fixture.Admission.Catalog.TryGet(npc)!.Value;
            Actor=Fixture.Runtime.Actors.Find(handle)??throw new InvalidOperationException(Fixture.Admission.Catalog.GetAdmission(handle)?.Refusal);
            _body=Fixture.Bodies!.Find(handle)!;
            var diagnostics=new ZoneMovementDiagnostics();
            _phases=new(new BackendPhases(Fixture.Backend!),Fixture.Admission.Registry,Fixture.Admission.Catalog,
                Fixture.Runtime.Actors,Fixture.Bodies,new(Fixture.Admission.Registry,diagnostics,true),diagnostics,
                prePhysics:frame=>
                {
                    Fixture.CurrentFrame=frame;
                    RequireNoFault(Fixture.Runtime.PrePhysics(frame,Fixture.Admission.Provider.RemoveNpc));
                },
                publishPose:(actor,body,frame)=>RequireNoFault(Fixture.Runtime.PublishPhysicalPose(actor.Handle,body,frame,Fixture.Admission.Provider.RemoveNpc)),
                managerOwnsReplication:true);
            var frame=new ZoneMovementFrame(1,TimeSpan.FromMilliseconds(20),400020,.02f)
                {LocalGameMilliseconds=2020,UnpausedMilliseconds=7020};
            _phases.PrePhase(frame);_phases.StepPhysics(frame.Elapsed);_phases.PostPhase(frame);
            Drain();
        }
        public void SetContext(int context)
        {
            using var script=Actor.Entry.RetainScript()!;
            using var unit=(ZoneLuaReference)script.Get("unit")!;
            using var setter=(ZoneLuaReference)unit.Get("NpcSetCheckPosContext")!;
            Fixture.Runtime.Lua.Invoke(setter,unit,context);
        }
        public void RawAnchor(string field,object? value)
        {using var script=Actor.Entry.RetainScript()!;using var ai=(ZoneLuaReference)script.Get("AI")!;ai.Set(field,value);}
        public void Anchor(string field,Vector3 value)
        {using var table=Fixture.Runtime.Lua.CreateTable();table.Set("x",value.X);table.Set("y",value.Y);table.Set("z",value.Z);RawAnchor(field,table);}
        public void RelocateForSetup(Vector3 destination)=>new ZoneManagedNpcRelocation(Fixture.Bodies!).Relocate(Actor,destination,result=>
        {
            Actor.Npc.Unit.CommitPose(new(ZoneMovementPoseMath.ToUnitWorldPosition(result.Current.Status.Position),result.Current.Status.Orientation));
            if(!Fixture.Admission.Registry.UpdateSpatialMembership(Actor.Npc.Unit))throw new InvalidOperationException("Lost test spatial owner.");
            RequireNoFault(Fixture.Components.PublishPose(Actor.Handle));
        });
        public async Task AssertPublished(Vector3 position)
        {
            await Near(Position,position);await Near(Body.CopyState().Status.Position,position);
            await Near(ZoneMovementPoseMath.ToMeters(Actor.Npc.Unit.Pose.Position),position);
            await Near(Fixture.Spatial!.GetMembership(Actor.Npc.Unit)!.Value.Position,position);
            await Near(Actor.AiObject.Position,position+Body.CopyState().CurrentStance.ViewOffset);
            using var script=Actor.Entry.RetainScript()!;
            using var get=(ZoneLuaReference)script.Get("GetPos")!;
            using var values=Fixture.Runtime.Lua.Call(get,script);
            var returned=(ZoneLuaReference)values[0]!;
            await Near(new(Convert.ToSingle(returned.Get("x")),Convert.ToSingle(returned.Get("y")),Convert.ToSingle(returned.Get("z"))),position);
        }
        public List<ZoneFrame> Drain(){var frames=new List<ZoneFrame>();while(Fixture.Admission.Registry.TryDequeueNotification(out var frame))frames.Add(frame);return frames;}
        private static void RequireNoFault(IReadOnlyList<ZoneManagedNpcScriptFault> faults)
        {if(faults.Count!=0)throw new InvalidOperationException(string.Join("; ",faults.Select(f=>f.Message)));}
        public void Dispose(){_phases.Dispose();Fixture.Dispose();}
    }
    private sealed class BackendPhases(ZoneManagedPhysicsBackend backend):IZoneMovementFramePhases
    {
        public int PrePhase(in ZoneMovementFrame frame)=>0;
        public void StepPhysics(TimeSpan elapsed)=>backend.Step(elapsed);
        public int PostPhase(in ZoneMovementFrame frame)=>0;
    }
}
