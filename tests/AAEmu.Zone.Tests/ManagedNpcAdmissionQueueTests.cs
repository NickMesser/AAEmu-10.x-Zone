using AAEmu.Zone.Actors;
using AAEmu.Zone.Ai;
using AAEmu.Zone.Hosting;

namespace AAEmu.Zone.Tests;

public sealed class ManagedNpcAdmissionQueueTests
{
    private static readonly ZoneActorHandle First = new(701,1), Second = new(702,1), Third = new(703,1);
    private static readonly ZoneAiSignal Signal = new(1,"OnControl",0,0,null);

    [Test]
    public async Task BurstEnqueueCreatesNothingAndOnePumpTransfersOnlyOneRealOwner()
    {
        var created=new Dictionary<ZoneActorHandle,Actor>();
        using var queue=new ZoneManagedNpcAdmissionQueue(handle=>created[handle]=new(),_=>true,(_,_)=>throw new InvalidOperationException());
        using var first=queue.Enqueue(First);using var second=queue.Enqueue(Second);using var third=queue.Enqueue(Third);
        first.SetActive(false);first.SetTarget(Second);second.Update(100,.02f);
        await Assert.That(created.Count).IsEqualTo(0);
        await Assert.That(((IZoneManagedAiActorReadiness)first).IsReady).IsFalse();
        await Assert.That(() => first.SendSignal(Signal)).Throws<NotSupportedException>();
        await Assert.That(queue.Pump(1)).IsEqualTo(1);
        await Assert.That(created.Count).IsEqualTo(1);await Assert.That(queue.PendingCount).IsEqualTo(2);await Assert.That(queue.ReadyCount).IsEqualTo(1);
        await Assert.That(((IZoneManagedAiActorReadiness)first).IsReady).IsTrue();
        await Assert.That(created[First].Active.Single()).IsFalse();
        await Assert.That(created[First].Targets.Single()).IsEqualTo((ZoneActorHandle?)Second);
        first.SendSignal(Signal);first.Update(200,.05f);
        await Assert.That(created[First].Signals.Single()).IsEqualTo(Signal);
        await Assert.That(created[First].Updates.Single()).IsEqualTo((200u,.05f));
        await Assert.That(queue.Pump(0)).IsEqualTo(0);
    }

    [Test]
    public async Task FactoryEnqueuedWorkWaitsForTheNextPumpAndExistingFifoOrderWins()
    {
        var order=new List<ZoneActorHandle>();ZoneManagedNpcAdmissionQueue queue=null!;IZoneManagedAiActor? later=null;
        using(queue=new(handle=>{order.Add(handle);if(handle==First)later=queue.Enqueue(Third);return new Actor();},_=>true,(_,_)=>{}))
        {
            using var first=queue.Enqueue(First);using var second=queue.Enqueue(Second);
            await Assert.That(queue.Pump(10)).IsEqualTo(2);
            await Assert.That(order.SequenceEqual([First,Second])).IsTrue();
            await Assert.That(queue.PendingCount).IsEqualTo(1);
            await Assert.That(((IZoneManagedAiActorReadiness)later!).IsReady).IsFalse();
            await Assert.That(queue.Pump(10)).IsEqualTo(1);
            await Assert.That(order.SequenceEqual([First,Second,Third])).IsTrue();
        }
    }

    [Test]
    public async Task CancellationRemovesNodesBeforeFactoryAndExactGenerationCannotReenter()
    {
        var calls=0;using var queue=new ZoneManagedNpcAdmissionQueue(_=>{calls++;return new Actor();},_=>true,(_,_)=>{});
        var pending=Enumerable.Range(1,100).Select(id=>queue.Enqueue(new((uint)id,1))).ToArray();
        foreach(var actor in pending)actor.Dispose();
        await Assert.That(queue.Count).IsEqualTo(0);await Assert.That(queue.Pump(1)).IsEqualTo(0);await Assert.That(calls).IsEqualTo(0);
        await Assert.That(() => queue.Enqueue(new(1,1))).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task FactoryCancellationCollectsTheReturnedActorExactlyOnceWithoutPublishingIt()
    {
        var actual=new Actor();ZoneManagedNpcAdmissionQueue queue=null!;
        using(queue=new(handle=>{queue.Remove(handle);return actual;},_=>true,(_,_)=>{}))
        {
            using var pending=queue.Enqueue(First);pending.SetActive(false);
            await Assert.That(queue.Pump(1)).IsEqualTo(1);
            await Assert.That(actual.Disposals).IsEqualTo(1);await Assert.That(actual.Active.Count).IsEqualTo(0);
            await Assert.That(((IZoneManagedAiActorReadiness)pending).IsReady).IsFalse();await Assert.That(queue.Count).IsEqualTo(0);
        }
        await Assert.That(actual.Disposals).IsEqualTo(1);
    }

    [Test]
    public async Task FactorySceneDisposalAlsoCollectsItsLateReturn()
    {
        var actual=new Actor();ZoneManagedNpcAdmissionQueue queue=null!;
        using(queue=new(_=>{queue.Dispose();return actual;},_=>true,(_,_)=>{}))
        {
            using var pending=queue.Enqueue(First);
            await Assert.That(queue.Pump(1)).IsEqualTo(1);await Assert.That(actual.Disposals).IsEqualTo(1);
            await Assert.That(queue.Count).IsEqualTo(0);await Assert.That(queue.ReadyCount).IsEqualTo(0);
        }
    }

    [Test]
    public async Task ReusedNumericIdentityCancelsTheOldConstructionAndDefersItsReplacement()
    {
        var replacement=new ZoneActorHandle(First.UnitObjectId,2);var current=First;
        var oldActual=new Actor();var newActual=new Actor();IZoneManagedAiActor? fresh=null;ZoneManagedNpcAdmissionQueue queue=null!;
        using(queue=new(handle=>
        {
            if(handle!=First)return newActual;
            current=replacement;fresh=queue.Enqueue(replacement);return oldActual;
        },handle=>handle==current,(_,_)=>{}))
        {
            using var old=queue.Enqueue(First);
            await Assert.That(queue.Pump(10)).IsEqualTo(1);await Assert.That(oldActual.Disposals).IsEqualTo(1);
            await Assert.That(queue.Count).IsEqualTo(1);await Assert.That(queue.ReadyCount).IsEqualTo(0);
            old.Dispose();await Assert.That(queue.Pump(1)).IsEqualTo(1);
            await Assert.That(((IZoneManagedAiActorReadiness)fresh!).IsReady).IsTrue();
            await Assert.That(newActual.Disposals).IsEqualTo(0);
        }
        await Assert.That(newActual.Disposals).IsEqualTo(1);
    }

    [Test]
    public async Task LatestPendingAndCallbackChangedControlsSettleBeforeReadiness()
    {
        IZoneManagedAiActor pending=null!;var readyDuringReplay=new List<bool>();var actual=new Actor();
        using var queue=new ZoneManagedNpcAdmissionQueue(_=>actual,_=>true,(_,_)=>{});
        actual.OnActive=value=>
        {readyDuringReplay.Add(((IZoneManagedAiActorReadiness)pending).IsReady);if(!value){pending.SetActive(true);pending.SetTarget(Third);}};
        actual.OnTarget=target=>{if(target==Third)pending.SetTarget(null);};
        using(pending=queue.Enqueue(First))
        {
            pending.SetActive(false);pending.SetTarget(Second);queue.Pump(1);
            await Assert.That(actual.Active.SequenceEqual([false,true])).IsTrue();
            await Assert.That(actual.Targets.SequenceEqual(new ZoneActorHandle?[]{Third,null})).IsTrue();
            await Assert.That(readyDuringReplay.All(value=>!value)).IsTrue();
            await Assert.That(((IZoneManagedAiActorReadiness)pending).IsReady).IsTrue();
        }
    }

    [Test]
    public async Task MissingRequestsPreserveActualConstructorStateAndReadyRetirementIsIdempotent()
    {
        var actual=new Actor();using var queue=new ZoneManagedNpcAdmissionQueue(_=>actual,_=>true,(_,_)=>{});
        var pending=queue.Enqueue(First);queue.Pump(1);
        await Assert.That(actual.Active.Count).IsEqualTo(0);await Assert.That(actual.Targets.Count).IsEqualTo(0);
        pending.Dispose();pending.Dispose();queue.Dispose();
        await Assert.That(actual.Disposals).IsEqualTo(1);await Assert.That(queue.Count).IsEqualTo(0);await Assert.That(queue.ReadyCount).IsEqualTo(0);
        await Assert.That(() => pending.Update(1,.02f)).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task ExpectedRefusalInvokesExactRetirementAndConsumerEnqueuedWorkIsDeferred()
    {
        var current=new HashSet<ZoneActorHandle>{First,Second};var refusals=new List<Exception>();IZoneManagedAiActor? later=null;
        var refusal=new NotSupportedException("actual authored dependency");ZoneManagedNpcAdmissionQueue queue=null!;
        using(queue=new(handle=>handle==First?throw refusal:new Actor(),current.Contains,(handle,error)=>
        {refusals.Add(error);current.Remove(handle);later=queue.Enqueue(Second);}))
        {
            using var pending=queue.Enqueue(First);
            await Assert.That(queue.Pump(10)).IsEqualTo(1);await Assert.That(ReferenceEquals(refusals.Single(),refusal)).IsTrue();
            await Assert.That(queue.RefusedCount).IsEqualTo(1ul);await Assert.That(queue.PendingCount).IsEqualTo(1);
            await Assert.That(((IZoneManagedAiActorReadiness)later!).IsReady).IsFalse();queue.Pump(1);
            await Assert.That(((IZoneManagedAiActorReadiness)later).IsReady).IsTrue();
        }
    }

    [Test]
    public async Task RefusalConsumerCannotLeaveTheExactAdmissionLive()
    {
        using var queue=new ZoneManagedNpcAdmissionQueue(_=>throw new NotSupportedException("dependency"),_=>true,(_,_)=>{});
        using var pending=queue.Enqueue(First);
        await Assert.That(() => queue.Pump(1)).Throws<InvalidOperationException>();await Assert.That(queue.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ProgrammingFailureReleasesStagedActorAndPropagatesWithoutContentRefusal()
    {
        var actual=new Actor{OnActive=_=>throw new InvalidOperationException("programming failure")};var refused=0;
        using var queue=new ZoneManagedNpcAdmissionQueue(_=>actual,_=>true,(_,_)=>refused++);
        using var pending=queue.Enqueue(First);pending.SetActive(false);
        await Assert.That(() => queue.Pump(1)).Throws<InvalidOperationException>();
        await Assert.That(actual.Disposals).IsEqualTo(1);await Assert.That(refused).IsEqualTo(0);await Assert.That(queue.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RecursivePumpAndUnstableControlConsumerCannotMonopolizeAFrame()
    {
        ZoneManagedNpcAdmissionQueue recursive=null!;
        using(recursive=new(_=>{recursive.Pump(1);return new Actor();},_=>true,(_,_)=>{}))
        {
            using var pending=recursive.Enqueue(First);
            await Assert.That(() => recursive.Pump(1)).Throws<InvalidOperationException>();await Assert.That(recursive.Count).IsEqualTo(0);
        }
        IZoneManagedAiActor toggled=null!;var actual=new Actor{OnActive=value=>toggled.SetActive(!value)};
        using var queue=new ZoneManagedNpcAdmissionQueue(_=>actual,_=>true,(_,_)=>{});
        using(toggled=queue.Enqueue(Second))
        {
            toggled.SetActive(false);await Assert.That(() => queue.Pump(1)).Throws<InvalidOperationException>();
            await Assert.That(actual.Active.Count).IsLessThanOrEqualTo(32);await Assert.That(actual.Disposals).IsEqualTo(1);
        }
    }

    [Test]
    public async Task ExactPredicateCallbackCannotPublishAfterCancelingItsCurrentEntry()
    {
        var cancel=false;var actual=new Actor();ZoneManagedNpcAdmissionQueue queue=null!;
        using(queue=new(_=>{cancel=true;return actual;},handle=>{if(cancel)queue.Remove(handle);return true;},(_,_)=>{}))
        {
            using var pending=queue.Enqueue(First);queue.Pump(1);
            await Assert.That(actual.Disposals).IsEqualTo(1);await Assert.That(((IZoneManagedAiActorReadiness)pending).IsReady).IsFalse();
            await Assert.That(queue.Count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task ReusedActualActorCannotBeStolenOrDoubleDisposedByANewWrapper()
    {
        var actual=new Actor();using var queue=new ZoneManagedNpcAdmissionQueue(_=>actual,_=>true,(_,_)=>{});
        using var first=queue.Enqueue(First);queue.Pump(1);using var second=queue.Enqueue(Second);
        await Assert.That(() => queue.Pump(1)).Throws<InvalidOperationException>();
        await Assert.That(actual.Disposals).IsEqualTo(0);await Assert.That(queue.ReadyCount).IsEqualTo(1);
        await Assert.That(((IZoneManagedAiActorReadiness)first).IsReady).IsTrue();first.SendSignal(Signal);
        await Assert.That(actual.Signals.Count).IsEqualTo(1);
    }

    [Test]
    public async Task SceneDisposalAttemptsEveryReturnedOwnerEvenWhenOneDestructorFails()
    {
        var first=new Actor{OnDispose=()=>throw new InvalidOperationException("cleanup")};var second=new Actor();
        var queue=new ZoneManagedNpcAdmissionQueue(handle=>handle==First?first:second,_=>true,(_,_)=>{});
        queue.Enqueue(First);queue.Enqueue(Second);queue.Pump(2);
        await Assert.That(() => queue.Dispose()).Throws<AggregateException>();
        await Assert.That(first.Disposals).IsEqualTo(1);await Assert.That(second.Disposals).IsEqualTo(1);
        await Assert.That(queue.Count).IsEqualTo(0);await Assert.That(queue.ReadyCount).IsEqualTo(0);
    }

    [Test]
    public async Task AdmissionNotificationSeesReadyReplayedControlsAndRunsExactlyOnce()
    {
        var actual=new Actor();IZoneManagedAiActor pending=null!;var notified=new List<ZoneActorHandle>();
        var readyAtNotification=false;var replayedAtNotification=false;
        using var queue=new ZoneManagedNpcAdmissionQueue(_=>actual,_=>true,(_,_)=>{},handle=>
        {
            notified.Add(handle);readyAtNotification=((IZoneManagedAiActorReadiness)pending).IsReady;
            replayedAtNotification=actual.Active.SequenceEqual([false])&&actual.Targets.SequenceEqual(new ZoneActorHandle?[]{Second});
            pending.SendSignal(Signal);
        });
        using(pending=queue.Enqueue(First))
        {
            pending.SetActive(false);pending.SetTarget(Second);queue.Pump(1);queue.Pump(1);
            await Assert.That(notified.SequenceEqual([First])).IsTrue();
            await Assert.That(readyAtNotification).IsTrue();await Assert.That(replayedAtNotification).IsTrue();
            await Assert.That(actual.Signals.Single()).IsEqualTo(Signal);
        }
    }

    [Test]
    public async Task AdmissionNotificationRetirementWinsAndCallbackEnqueuedWorkWaits()
    {
        var actual=new Actor();ZoneManagedNpcAdmissionQueue queue=null!;IZoneManagedAiActor? later=null;
        using(queue=new(handle=>handle==First?actual:new Actor(),_=>true,(_,_)=>{},handle=>
        {if(handle==First){queue.Remove(handle);later=queue.Enqueue(Second);}}))
        {
            using var pending=queue.Enqueue(First);
            await Assert.That(queue.Pump(10)).IsEqualTo(1);
            await Assert.That(actual.Disposals).IsEqualTo(1);await Assert.That(((IZoneManagedAiActorReadiness)pending).IsReady).IsFalse();
            await Assert.That(queue.ReadyCount).IsEqualTo(0);await Assert.That(queue.PendingCount).IsEqualTo(1);
            await Assert.That(((IZoneManagedAiActorReadiness)later!).IsReady).IsFalse();
            await Assert.That(queue.Pump(1)).IsEqualTo(1);await Assert.That(((IZoneManagedAiActorReadiness)later).IsReady).IsTrue();
        }
    }

    [Test]
    public async Task AdmissionNotificationContentRefusalReleasesOwnershipBeforeConsumer()
    {
        var current=true;var actual=new Actor();var refusal=new NotSupportedException("scheduling owner unavailable");
        var cleanAtRefusal=false;ZoneManagedNpcAdmissionQueue queue=null!;
        using(queue=new(_=>actual,_=>current,(_,error)=>
        {
            cleanAtRefusal=ReferenceEquals(error,refusal)&&actual.Disposals==1&&queue.ReadyCount==0&&queue.Count==0;
            current=false;
        },_=>throw refusal))
        {
            using var pending=queue.Enqueue(First);await Assert.That(queue.Pump(1)).IsEqualTo(1);
            await Assert.That(cleanAtRefusal).IsTrue();await Assert.That(queue.RefusedCount).IsEqualTo(1ul);
            await Assert.That(((IZoneManagedAiActorReadiness)pending).IsReady).IsFalse();
        }
    }

    [Test]
    public async Task RealProviderDoesNotArmItsScheduleUntilQueueReadinessCommits()
    {
        var handle=new ZoneActorHandle(81101,2026);var target=new ZoneActorHandle(81102,2026);
        var current=new HashSet<ZoneActorHandle>{handle,target};var actual=new Actor();
        ZoneManagedAiProvider provider=null!;var callbackSawReady=false;
        using var queue=new ZoneManagedNpcAdmissionQueue(_=>actual,current.Contains,(actor,_)=>
        {provider.RemoveNpc(actor);current.Remove(actor);},actor=>callbackSawReady=provider.IsReady(actor));
        using(provider=new(actor=>current.Contains(actor)?ZoneActorKind.Npc:throw new InvalidOperationException("retired"),queue.Enqueue))
        {
            provider.CreateNpc(handle);provider.SetUpdateSchedule(handle,TimeSpan.FromMilliseconds(100));
            provider.SetActive(handle,false);provider.SetTarget(handle,target);provider.SetActive(handle,true);
            provider.Pump(100);provider.Pump(1000);
            await Assert.That(provider.IsReady(handle)).IsFalse();
            await Assert.That(provider.GetState(handle).NextUpdateMilliseconds).IsNull();
            await Assert.That(actual.Updates.Count).IsEqualTo(0);
            await Assert.That(() => provider.SendSignal(handle,Signal)).Throws<NotSupportedException>();
            queue.Pump(1);
            await Assert.That(callbackSawReady).IsTrue();await Assert.That(provider.IsReady(handle)).IsTrue();
            await Assert.That(actual.Active.SequenceEqual([true])).IsTrue();
            await Assert.That(actual.Targets.Single()).IsEqualTo((ZoneActorHandle?)target);
            provider.Pump(2000);
            await Assert.That(provider.GetState(handle).NextUpdateMilliseconds).IsEqualTo((uint?)2100);
            await Assert.That(actual.Updates.Count).IsEqualTo(0);
            provider.Pump(2099);provider.Pump(2100);
            await Assert.That(actual.Updates.Single()).IsEqualTo((2100u,.1f));
            provider.RemoveNpc(handle);current.Remove(handle);provider.Pump(2200);
            await Assert.That(actual.Disposals).IsEqualTo(1);await Assert.That(queue.Count).IsEqualTo(0);
            await Assert.That(actual.Updates.Count).IsEqualTo(1);
        }
    }

    private sealed class Actor : IZoneManagedAiActor
    {
        public List<bool> Active { get; }=[];
        public List<ZoneActorHandle?> Targets { get; }=[];
        public List<ZoneAiSignal> Signals { get; }=[];
        public List<(uint,float)> Updates { get; }=[];
        public int Disposals { get; private set; }
        public Action<bool>? OnActive { get; set; }
        public Action<ZoneActorHandle?>? OnTarget { get; set; }
        public Action? OnDispose { get; set; }
        public void SetActive(bool active){Active.Add(active);OnActive?.Invoke(active);}
        public void SetTarget(ZoneActorHandle? target){Targets.Add(target);OnTarget?.Invoke(target);}
        public void SendSignal(ZoneAiSignal signal)=>Signals.Add(signal);
        public void Update(uint timeMilliseconds,float elapsedSeconds)=>Updates.Add((timeMilliseconds,elapsedSeconds));
        public void Dispose(){Disposals++;OnDispose?.Invoke();}
    }
}
