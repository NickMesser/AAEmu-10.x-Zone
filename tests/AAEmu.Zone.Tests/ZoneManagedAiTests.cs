using System.Numerics;

using AAEmu.Zone.Actors;
using AAEmu.Zone.Ai;
using AAEmu.Zone.Native.Ai;

namespace AAEmu.Zone.Tests;

public sealed class ZoneManagedAiTests
{
    private static uint _nextId = 0x70000000;
    private static ZoneActorHandle Handle(uint generation = 1) => new(Interlocked.Increment(ref _nextId), generation);

    private sealed class Catalog
    {
        public readonly Dictionary<ZoneActorHandle, ZoneActorKind> Actors = [];
        public ZoneActorKind Kind(ZoneActorHandle handle) => Actors.TryGetValue(handle, out var kind)
            ? kind : throw new InvalidOperationException("The generation has no live scene actor.");
    }

    [Test]
    public async Task AdmissionRejectsPlayersUnknownAndRetiredGenerationsAndSharesOwnership()
    {
        var catalog = new Catalog();
        var npc = Handle();
        var player = Handle();
        catalog.Actors.Add(npc, ZoneActorKind.Npc);
        catalog.Actors.Add(player, ZoneActorKind.Player);
        using var provider = new ZoneManagedAiProvider(catalog.Kind, _ => new ZoneManagedAiActor());
        using var other = new ZoneManagedAiProvider(catalog.Kind, _ => new ZoneManagedAiActor());
        await Assert.That(() => provider.CreateNpc(player)).Throws<ArgumentException>();
        await Assert.That(() => provider.CreateNpc(Handle())).Throws<InvalidOperationException>();
        provider.CreateNpc(npc);
        await Assert.That(() => other.CreateNpc(npc)).Throws<InvalidOperationException>();
        await Assert.That(ZoneAiBackendBindings.IsBound(npc)).IsTrue();
        provider.RemoveNpc(npc);
        provider.RemoveNpc(npc);
        await Assert.That(ZoneAiBackendBindings.IsBound(npc)).IsFalse();
        await Assert.That(() => provider.CreateNpc(npc)).Throws<InvalidOperationException>();
        await Assert.That(() => provider.SetActive(npc, true)).Throws<InvalidOperationException>();
        var replacement = npc with { Generation = 2 };
        catalog.Actors.Remove(npc);
        catalog.Actors.Add(replacement, ZoneActorKind.Npc);
        provider.CreateNpc(replacement);
        await Assert.That(provider.Count).IsEqualTo(1);
    }

    [Test]
    public async Task NativeAdapterRespectsManagedClaimAndReleasesFailedConstruction()
    {
        if (!OperatingSystem.IsWindows()) return; // The legacy adapter probes kernel32 module handles.
        var catalog = new Catalog();
        var npc = Handle();
        catalog.Actors.Add(npc, ZoneActorKind.Npc);
        using var managed = new ZoneManagedAiProvider(catalog.Kind, _ => new ZoneManagedAiActor());
        using var native = new ZoneNativeAiProvider(new NativeCatalog(catalog));
        managed.CreateNpc(npc);
        await Assert.That(() => native.CreateNpc(npc)).Throws<InvalidOperationException>();
        managed.RemoveNpc(npc);
        await Assert.That(() => native.CreateNpc(npc)).Throws<NotSupportedException>();
        await Assert.That(ZoneAiBackendBindings.IsBound(npc)).IsFalse();
        using var nextProvider = new ZoneManagedAiProvider(catalog.Kind, _ => new ZoneManagedAiActor());
        nextProvider.CreateNpc(npc);
        await Assert.That(nextProvider.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ConstructionFailureReleasesClaimAndCannotResurrectDuringRemoval()
    {
        var catalog = new Catalog();
        var npc = Handle();
        catalog.Actors.Add(npc, ZoneActorKind.Npc);
        using (var failed = new ZoneManagedAiProvider(catalog.Kind, _ => throw new NotSupportedException("missing authored behavior")))
        {
            await Assert.That(() => failed.CreateNpc(npc)).Throws<NotSupportedException>();
            await Assert.That(failed.Count).IsEqualTo(0);
            await Assert.That(ZoneAiBackendBindings.IsBound(npc)).IsFalse();
        }
        ZoneManagedAiProvider? provider = null;
        ZoneManagedAiActor? behavior = null;
        provider = new(catalog.Kind, actor =>
        {
            behavior = new();
            provider!.RemoveNpc(actor);
            return behavior;
        });
        using (provider)
        {
            await Assert.That(() => provider.CreateNpc(npc)).Throws<InvalidOperationException>();
            await Assert.That(behavior!.IsDisposed).IsTrue();
            await Assert.That(ZoneAiBackendBindings.IsBound(npc)).IsFalse();
        }
    }

    [Test]
    public async Task BehaviorInstancesCannotBeSharedOrReusedAndRetirementRetainsTheClaimThroughCallbacks()
    {
        var catalog = new Catalog();
        var first = Handle();
        var second = Handle();
        catalog.Actors.Add(first, ZoneActorKind.Npc);
        catalog.Actors.Add(second, ZoneActorKind.Npc);
        var claimedDuringRetirement = false;
        using var behavior = new ZoneManagedAiActor(retire: () => claimedDuringRetirement = ZoneAiBackendBindings.IsBound(first));
        using var provider = new ZoneManagedAiProvider(catalog.Kind, _ => behavior);
        provider.CreateNpc(first);
        await Assert.That(() => provider.CreateNpc(second)).Throws<InvalidOperationException>();
        await Assert.That(behavior.IsDisposed).IsFalse(); // Rejecting a duplicate never disposes its actual owner's actor.
        await Assert.That(ZoneAiBackendBindings.IsBound(second)).IsFalse();
        provider.RemoveNpc(first);
        await Assert.That(claimedDuringRetirement).IsTrue();
        await Assert.That(ZoneAiBackendBindings.IsBound(first)).IsFalse();
        await Assert.That(() => provider.CreateNpc(second)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task RetirementErrorsStillReleaseAllBehaviorAndBackendClaims()
    {
        var catalog = new Catalog();
        var first = Handle();
        var second = Handle();
        catalog.Actors.Add(first, ZoneActorKind.Npc);
        catalog.Actors.Add(second, ZoneActorKind.Npc);
        using var firstActor = new ZoneManagedAiActor(retire: () => throw new InvalidOperationException("script cleanup failed"));
        using var secondActor = new ZoneManagedAiActor();
        using var provider = new ZoneManagedAiProvider(catalog.Kind, handle => handle == first ? firstActor : secondActor);
        provider.CreateNpc(first);
        provider.CreateNpc(second);
        await Assert.That(provider.Dispose).Throws<AggregateException>();
        await Assert.That(firstActor.IsDisposed).IsTrue();
        await Assert.That(secondActor.IsDisposed).IsTrue();
        await Assert.That(ZoneAiBackendBindings.IsBound(first)).IsFalse();
        await Assert.That(ZoneAiBackendBindings.IsBound(second)).IsFalse();
        provider.Dispose();
    }

    [Test]
    public async Task ScheduledUpdatesWrapPauseAndNeverReplayABacklog()
    {
        var catalog = new Catalog();
        var npc = Handle();
        catalog.Actors.Add(npc, ZoneActorKind.Npc);
        var elapsed = new List<float>();
        using var actor = new ZoneManagedAiActor(update: (_, seconds) => elapsed.Add(seconds));
        using var provider = new ZoneManagedAiProvider(catalog.Kind, _ => actor);
        provider.CreateNpc(npc);
        provider.SetUpdateSchedule(npc, TimeSpan.FromMilliseconds(20));
        provider.Pump(uint.MaxValue - 10);
        provider.Pump(8);
        await Assert.That(actor.UpdateCount).IsEqualTo(0u);
        provider.Pump(9);
        await Assert.That(actor.UpdateCount).IsEqualTo(1u);
        await Assert.That(Math.Abs(elapsed[0] - .020f)).IsLessThan(.00001f);
        provider.Pump(1000);
        await Assert.That(actor.UpdateCount).IsEqualTo(2u);
        provider.Pump(1000);
        await Assert.That(actor.UpdateCount).IsEqualTo(2u);
        provider.SetActive(npc, false);
        provider.Pump(2000);
        provider.SetActive(npc, true);
        provider.Pump(3000);
        provider.Pump(3019);
        await Assert.That(actor.UpdateCount).IsEqualTo(2u);
        provider.Pump(3020);
        await Assert.That(actor.UpdateCount).IsEqualTo(3u);
        provider.SetUpdateSchedule(npc, null);
        provider.Pump(4000);
        await Assert.That(actor.UpdateCount).IsEqualTo(3u);
        await Assert.That(() => provider.SetUpdateSchedule(npc, TimeSpan.FromTicks(0))).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => provider.SetUpdateSchedule(npc, TimeSpan.FromMilliseconds((double)int.MaxValue + 1))).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task CallbackCancellationAndRemovalWinOverTheCurrentPump()
    {
        var catalog = new Catalog();
        var first = Handle();
        var second = Handle();
        catalog.Actors.Add(first, ZoneActorKind.Npc);
        catalog.Actors.Add(second, ZoneActorKind.Npc);
        ZoneManagedAiProvider? provider = null;
        using var firstActor = new ZoneManagedAiActor(update: (_, _) =>
        {
            provider!.SetUpdateSchedule(first, null);
            provider.RemoveNpc(second);
        });
        using var secondActor = new ZoneManagedAiActor();
        using (provider = new(catalog.Kind, handle => handle == first ? firstActor : secondActor))
        {
            provider.CreateNpc(first);
            provider.CreateNpc(second);
            provider.SetUpdateSchedule(first, TimeSpan.FromMilliseconds(1));
            provider.SetUpdateSchedule(second, TimeSpan.FromMilliseconds(1));
            provider.Pump(0);
            provider.Pump(1);
            provider.Pump(2);
            await Assert.That(firstActor.UpdateCount).IsEqualTo(1u);
            await Assert.That(secondActor.UpdateCount).IsEqualTo(0u);
            await Assert.That(secondActor.IsDisposed).IsTrue();
        }
    }

    [Test]
    public async Task SignalsObserveTheTargetAndRetiredTargetsAreCleared()
    {
        var catalog = new Catalog();
        var npc = Handle();
        var target = Handle();
        catalog.Actors.Add(npc, ZoneActorKind.Npc);
        catalog.Actors.Add(target, ZoneActorKind.Player);
        ZoneManagedAiActor? actor = null;
        ZoneActorHandle? seenTarget = null;
        using var actorLifetime = actor = new(signal: _ => seenTarget = actor!.Target);
        using var provider = new ZoneManagedAiProvider(catalog.Kind, _ => actor);
        provider.CreateNpc(npc);
        provider.SetTarget(npc, target);
        provider.SendSignal(npc, new(0, "OnTalk\0ignored", 0, 55, null));
        await Assert.That(seenTarget).IsEqualTo(target);
        await Assert.That(actor.LastSignal!.Name).IsEqualTo("OnTalk");
        await Assert.That(actor.LastSignal.Crc).IsNotEqualTo(0u);
        catalog.Actors.Remove(target);
        provider.SendSignal(npc, new(0, "OnTalk", 0, 55, null));
        await Assert.That(seenTarget).IsNull();
        await Assert.That(provider.GetState(npc).Target).IsNull();
        await Assert.That(() => provider.SetTarget(npc, target)).Throws<InvalidOperationException>();
        await Assert.That(() => provider.SendSignal(npc, new(0, new string('é', 25), 0, 0, null))).Throws<ArgumentException>();
        catalog.Actors.Remove(npc);
        provider.Pump(100);
        await Assert.That(actor.IsDisposed).IsTrue();
        await Assert.That(provider.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ManagedActorRunsExistingGoalExecutorAndWakesWaitSignalGoals()
    {
        var catalog = new ZoneGoalPipeCatalog();
        catalog.Create("wait_for_talk")!.Append(ZoneGoalOperation.WaitSignal, true, ZoneGoalGrouping.None,
            new() { Text = "OnTalk" }, _ => { });
        var services = new PipeServices();
        using var pipes = new ZoneGoalPipeController(catalog, services, new());
        using var actor = new ZoneManagedAiActor(goalPipes: pipes);
        await Assert.That(pipes.Select(ZoneGoalPipeFlags.RunOnce, "wait_for_talk")).IsTrue();
        actor.Update(100, .1f);
        await Assert.That(pipes.Executor.ActiveGoalCount).IsEqualTo(1);
        actor.SendSignal(new(0, "OnTalk", 0, 0, null));
        actor.Update(200, .1f);
        await Assert.That(pipes.Executor.ActiveGoalCount).IsEqualTo(0);
        await Assert.That(actor.Capabilities.GoalPipeExecution).IsTrue();
        using var idle = new ZoneManagedAiActor();
        await Assert.That(idle.Capabilities.GoalPipeExecution).IsFalse();
        await Assert.That(() => idle.SendSignal(new(0, "OnTalk", 0, 0, null))).Throws<NotSupportedException>();
    }

    private sealed class PipeServices : IZoneGoalPipeActorServices
    {
        public IZoneGoalOperation CreateOperation(ZoneGoalDefinition definition, ZoneGoalExecutor executor) =>
            new ZoneWaitSignalGoal(definition.Parameters.Text, ZoneSignalMatchKind.Name, "", 0, 0, executor.SignalWaits, () => 1000);
        public IZoneGoalOperation? AuxiliaryGoal => null;
        public uint NextRandom() => 0;
        public bool EvaluateBranch(ZoneGoalParameters parameters) => throw new NotSupportedException();
        public Vector3? GetAttentionTargetPosition() => null;
        public void SetArgument(object? argument) { }
        public void ClearAttentionTarget() { }
        public void ResetActorAfterClear() { }
        public void PreparePipeSelection() { }
        public void ResetForPipeSelection(ZoneGoalPipeFlags flags) { }
        public void ResetAfterPipeClear() { }
        public void EnableSignals() { }
        public void ClearSelectionTarget() { }
        public void ClearSelectionControls() { }
        public void RecordPipeEvent(int kind, string? name) { }
        public void ActorPipeEvent(int kind, string? name) { }
        public void Diagnostic(string message) { }
        public void ReportSubPipeCount(int count, string rootName, string? insertingName) { }
        public void ReportActiveGoalCount(int count, string? rootName, ZoneGoalDefinition lastGoal) { }
        public void ReportInsertionChain(IReadOnlyList<ZoneGoalPipeInstance> chain) { }
    }

    private sealed class NativeCatalog(Catalog catalog) : IZoneNativeAiActorView
    {
        public ZoneActorKind GetKind(ZoneActorHandle actor) => catalog.Kind(actor);
    }
}
