using System.Numerics;
using System.Text.Json;
using AAEmu.Zone.Ai;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Scripting;

namespace AAEmu.Zone.Tests;

public sealed class ManagedNpcPositionDistanceBranchTests
{
    [Test]
    public async Task ScalarDistanceMatches52OriginalBranchControlsIncludingNaNsOverflowAndEightKilometers()
    {
        using var stream = typeof(ManagedNpcPositionDistanceBranchTests).Assembly.GetManifestResourceStream(
            "AAEmu.Zone.Tests.Fixtures.npc-position-distance-native-controls.json")!;
        using var json=JsonDocument.Parse(stream);var count=0;
        foreach(var control in json.RootElement.GetProperty("controls").EnumerateArray())
        {
            var kind=control.GetProperty("branch").GetInt32() & ~0x100;
            var position=Vector(control.GetProperty(control.GetProperty("entityPresent").GetBoolean()?"entityBits":"aiBits"));
            var goal=Vector(control.GetProperty("goalBits"));
            var threshold=BitConverter.UInt32BitsToSingle(control.GetProperty("thresholdBits").GetUInt32());
            await Assert.That(ZoneManagedNpcPositionDistanceBranch.Evaluate(position,goal,threshold,kind==46))
                .IsEqualTo(control.GetProperty("result").GetBoolean());
            count++;
        }
        await Assert.That(count).IsEqualTo(52);
    }

    [Test]
    public async Task LiveEntityOriginWinsOverProxyViewHeightAndDistanceRetainsItsVerticalComponent()
    {
        using var fixture=new ManagedNpcActorSceneTests.Fixture();var actor=fixture.Admit();
        fixture.Positions[actor.Npc.ObjectId]=new(10,20,30);
        actor.AiObject.Position=new(10,20,31.7f);
        var atOrigin=new ZoneGoalParameters{Integer=45,Vector=new(10,20,30),First=.3f};
        await Assert.That(actor.Services.EvaluateBranch(atOrigin)).IsTrue();
        await Assert.That(actor.Services.EvaluateBranch(atOrigin with {Integer=46})).IsFalse();
        var above=atOrigin with {Vector=new(10,20,32),First=1.5f};
        await Assert.That(actor.Services.EvaluateBranch(above)).IsFalse();
        await Assert.That(actor.Services.EvaluateBranch(above with {Integer=46})).IsTrue();
    }

    [Test]
    [Arguments(45, false)]
    [Arguments(45, true)]
    [Arguments(46, false)]
    [Arguments(46, true)]
    public async Task RealExecutorAppliesNotExactlyOnceToBothPositionPredicates(int kind, bool invert)
    {
        using var fixture=new ManagedNpcActorSceneTests.Fixture();var actor=fixture.Admit();
        fixture.Positions[actor.Npc.ObjectId]=new(100,200,300);
        var pipe=fixture.Scene.GoalPipes.Create("position_branch_not")!;
        Append(ZoneGoalOperation.Branch,new(){Integer=kind | (invert?0x100:0),Integer2=2,
            Vector=new(100,200,300),First=0});
        Append(ZoneGoalOperation.BodyPosition,new(){First=1});
        Append(ZoneGoalOperation.Branch,new(){Integer=48,Integer2=1});
        Append(ZoneGoalOperation.BodyPosition,new(){First=2});
        actor.Entry.Phase.Pipes.Select(ZoneGoalPipeFlags.RunOnce,pipe.Name);
        actor.Entry.Phase.Pipes.Executor.Update();
        var raw=kind==45;
        await Assert.That(actor.Controls.BodyState).IsEqualTo(raw!=invert?2:1);
        void Append(ZoneGoalOperation operation,ZoneGoalParameters parameters) =>
            pipe.Append(operation,false,ZoneGoalGrouping.None,parameters,_=>{});
    }

    [Test]
    public async Task PositionCallbackRetirementCannotReturnAnOldGenerationPredicate()
    {
        using var admission=new ManagedNpcAdmissionTests.Fixture();using var lua=new ZoneManagedLuaRuntime();
        ZoneManagedNpcActorScene? scene=null;ZoneManagedNpcActor? actor=null;var retire=false;var reads=0;
        using var owner=scene=new(admission.Registry,admission.Catalog,lua,new(),_=>
        {
            reads++;if(retire)scene!.Remove(actor!.Handle);return new(3,4,5);
        },()=>0,()=>1,_=>{},TimeSpan.FromMilliseconds(20));
        var npc=admission.Npc();admission.Publish(npc);var handle=admission.Catalog.Register(npc);
        actor=scene.Adopt(handle,new(){EntityId=99000001},5,1,ZoneManagedNpcActorScene.CreatePipeUserControls(),true,null);
        retire=true;
        await Assert.That(()=>actor.Services.EvaluateBranch(new(){Integer=45,Vector=new(3,4,5),First=0}))
            .Throws<ObjectDisposedException>();
        var after=reads;
        await Assert.That(()=>actor.Services.EvaluateBranch(new(){Integer=46})).Throws<ObjectDisposedException>();
        await Assert.That(reads).IsEqualTo(after);
        await Assert.That(scene.Count).IsEqualTo(0);
    }

    private static Vector3 Vector(JsonElement e)=>new(BitConverter.UInt32BitsToSingle(e[0].GetUInt32()),
        BitConverter.UInt32BitsToSingle(e[1].GetUInt32()),BitConverter.UInt32BitsToSingle(e[2].GetUInt32()));
}
