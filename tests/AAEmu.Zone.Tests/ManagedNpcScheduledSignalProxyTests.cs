using AAEmu.Zone.Ai;
using AAEmu.Zone.Scene;

namespace AAEmu.Zone.Tests;

public sealed class ManagedNpcScheduledSignalProxyTests
{
    [Test]
    public async Task SignalGoalSubmitsUntilTheSameScheduledActorsGoalPhaseHasFinished()
    {
        using var fixture=new ManagedNpcActorSceneTests.Fixture();var actor=fixture.Admit();
        var observed=new List<int>();
        using var callback=fixture.Lua.CreateFunction(_=>{observed.Add(actor.Controls.BodyState);return[];});
        using var behavior=actor.Behavior!.RetainCurrent()!;behavior.Set("ObservePhase",callback);
        using var signal=new ZoneSignalGoal(0,"ObservePhase",0,0,actor.Services);
        await Assert.That(signal.Execute()).IsEqualTo(1);
        await Assert.That(observed.Count).IsEqualTo(0);
        await Assert.That(actor.Npc.AiSignals.Pending.Any(row=>row.Name=="ObservePhase")).IsTrue();
        var pipe=fixture.Scene.GoalPipes.Create("phase_before_proxy")!;
        Append(pipe,ZoneGoalOperation.BodyPosition,new(){First=2});
        actor.Entry.Phase.Pipes.Select(ZoneGoalPipeFlags.RunOnce,pipe.Name);
        actor.Update(1000,.02f);
        await Assert.That(observed.Count).IsEqualTo(1);
        await Assert.That(observed[0]).IsEqualTo(2);
        await Assert.That(actor.Npc.AiSignals.Pending.Any(row=>row.Name=="ObservePhase")).IsFalse();
    }

    [Test]
    public async Task AuthoredSignalOperationDrainsOnceAndDisabledActorDoesNotRunAProxyPhase()
    {
        using var fixture=new ManagedNpcActorSceneTests.Fixture();var actor=fixture.Admit();
        var delivered=0;
        using var callback=fixture.Lua.CreateFunction(_=>{delivered++;return[];});
        using var behavior=actor.Behavior!.RetainCurrent()!;behavior.Set("ObservePhase",callback);
        var pipe=fixture.Scene.GoalPipes.Create("scheduled_signal")!;
        Append(pipe,ZoneGoalOperation.Signal,new(){Text="ObservePhase"});
        actor.Entry.Phase.Pipes.Select(ZoneGoalPipeFlags.RunOnce,pipe.Name);
        actor.SetActive(false);actor.Update(1000,.02f);
        await Assert.That(delivered).IsEqualTo(0);
        actor.SetActive(true);actor.Update(1020,.02f);
        await Assert.That(delivered).IsEqualTo(1);
        actor.Update(1040,.02f);
        await Assert.That(delivered).IsEqualTo(1);
    }

    [Test]
    public async Task UnsupportedGoalPhaseCannotDrainPendingSignalsBeforeRuntimeRetirement()
    {
        using var fixture=new ManagedNpcActorSceneTests.Fixture();var actor=fixture.Admit();
        var delivered=0;
        using var callback=fixture.Lua.CreateFunction(_=>{delivered++;return[];});
        using var behavior=actor.Behavior!.RetainCurrent()!;behavior.Set("ObservePhase",callback);
        using var signal=new ZoneSignalGoal(0,"ObservePhase",0,0,actor.Services);signal.Execute();
        var pipe=fixture.Scene.GoalPipes.Create("unsupported_before_proxy")!;
        Append(pipe,ZoneGoalOperation.Branch,new(){Integer=7});
        actor.Entry.Phase.Pipes.Select(ZoneGoalPipeFlags.RunOnce,pipe.Name);
        await Assert.That(()=>actor.Update(1000,.02f)).Throws<NotSupportedException>();
        await Assert.That(delivered).IsEqualTo(0);
        await Assert.That(actor.Npc.AiSignals.Pending.Any(row=>row.Name=="ObservePhase")).IsTrue();
        fixture.Scene.Remove(actor.Handle);
        await Assert.That(()=>actor.Update(1020,.02f)).Throws<ObjectDisposedException>();
        await Assert.That(delivered).IsEqualTo(0);
    }

    [Test]
    public async Task GoalCallbackRetirementAndAbsentHandlerCannotConsumeAnUnownedProxyQueue()
    {
        using(var fixture=new ManagedNpcActorSceneTests.Fixture())
        {
            var actor=fixture.Admit();var delivered=0;
            using var callback=fixture.Lua.CreateFunction(_=>{delivered++;return[];});
            using var behavior=actor.Behavior!.RetainCurrent()!;behavior.Set("ObservePhase",callback);
            using var signal=new ZoneSignalGoal(0,"ObservePhase",0,0,actor.Services);signal.Execute();
            fixture.Scene.OperationFactory=(owner,_)=>new ActionGoal(()=>fixture.Scene.Remove(owner.Handle));
            var pipe=fixture.Scene.GoalPipes.Create("retire_before_proxy")!;
            Append(pipe,ZoneGoalOperation.UseSkill,new());
            actor.Entry.Phase.Pipes.Select(ZoneGoalPipeFlags.RunOnce,pipe.Name);
            actor.Update(1000,.02f);
            await Assert.That(delivered).IsEqualTo(0);
            await Assert.That(fixture.Scene.Find(actor.Handle)).IsNull();
        }
        using(var fixture=new ManagedNpcActorSceneTests.Fixture())
        {
            var actor=fixture.Admit();
            using var signal=new ZoneSignalGoal(0,"NoHandler",0,0,actor.Services);signal.Execute();
            actor.Behavior!.Dispose();
            actor.Update(1000,.02f);
            await Assert.That(actor.Npc.AiSignals.Pending.Any(row=>row.Name=="NoHandler")).IsTrue();
        }
    }

    private static void Append(ZoneGoalPipeDefinition pipe,ZoneGoalOperation operation,ZoneGoalParameters parameters) =>
        pipe.Append(operation,false,ZoneGoalGrouping.None,parameters,_=>{});
    private sealed class ActionGoal(Action action):IZoneGoalOperation
    {
        public int Execute(){action();return 4;}
        public void Reset(){}
        public void Dispose(){}
    }
}
