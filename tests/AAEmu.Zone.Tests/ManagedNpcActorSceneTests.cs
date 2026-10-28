using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Ai;
using AAEmu.Zone.Npcs;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Scripting;

namespace AAEmu.Zone.Tests;

public sealed class ManagedNpcActorSceneTests
{
    [Test]
    public async Task PendingServicesDoNotPublishAnActorAndConstructorsResolveItsAdoptedOwner()
    {
        using var fixture = new Fixture();
        var npc = fixture.NewNpc();
        var handle = fixture.Admission.Catalog.Register(npc);
        var controls = ZoneManagedNpcActorScene.CreatePipeUserControls();
        using var pending = fixture.Scene.Prepare(handle, new() { EntityId = npc.ObjectId + 10_000_000, Radius = .4f }, controls, true);
        await Assert.That(pending.HasActor).IsFalse();
        await Assert.That(fixture.Scene.Count).IsEqualTo(0);
        await Assert.That(fixture.Scene.Actors.GetEntityId(npc.Unit)).IsNull();
        await Assert.That(() => pending.Update(1000, .02f)).Throws<ObjectDisposedException>();

        using var entity = fixture.Lua.CreateTable();
        entity.Set("id", new ZoneLuaHandle(pending.AiObject.EntityId));
        fixture.Scene.Adopt(pending, 1, 0x1200_0000, entity);
        fixture.RegisterBehavior(pending, entity);
        pending.Behavior!.SetBehavior("Idle");
        await Assert.That(entity.Get("constructed")).IsEqualTo(1L);
        await Assert.That(entity.Get("resolved") is true).IsTrue();
        await Assert.That(pending.Entry.Phase.Pipes.CurrentPipe!.Name).IsEqualTo("_first_");
        await Assert.That(pending.Controls.MovementUrgency).IsEqualTo(.4f);
        await Assert.That(pending.Controls.GoalMovementUrgency).IsEqualTo(.4f);
        await Assert.That(pending.Controls.SignalsEnabled).IsTrue();
        await Assert.That(pending.GameplayReady).IsFalse();
        await Assert.That(fixture.Errors.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ManagedProviderExecutesControlsAndSignalWaitsUsingTheSameActorState()
    {
        using var fixture = new Fixture();
        var actor = fixture.Admit();
        var snapshot = actor.Npc.Unit.Snapshot.Position;
        var pipe = fixture.Scene.GoalPipes.Create("controlled_wait")!;
        Append(pipe, ZoneGoalOperation.Run, new() { First = 1 });
        Append(pipe, ZoneGoalOperation.Continuous, new() { Boolean = true });
        Append(pipe, ZoneGoalOperation.BodyPosition, new() { First = 2 });
        Append(pipe, ZoneGoalOperation.WaitSignal, new() { Text = "OnContinue" }, true);
        Append(pipe, ZoneGoalOperation.Run, new() { First = 2 });
        actor.Entry.Phase.Pipes.Select(ZoneGoalPipeFlags.RunOnce, pipe.Name);
        fixture.Admission.Provider.Pump(1000);
        fixture.Admission.Provider.Pump(1020);
        await Assert.That(actor.Controls.MovementUrgency).IsEqualTo(1f);
        await Assert.That(actor.Controls.BodyState).IsEqualTo(2);
        await Assert.That(actor.Continuation.KeepMoving).IsTrue();
        await Assert.That(actor.Entry.Phase.Pipes.Executor.SignalWaits.Count).IsEqualTo(1);

        actor.Npc.AiSignals.Send("OnContinue");
        await Assert.That(actor.Entry.Phase.Pipes.Executor.SignalWaits.Count).IsEqualTo(0);
        actor.Npc.AiSignals.Update();
        fixture.Admission.Provider.Pump(1040);
        fixture.Admission.Provider.Pump(1060);
        await Assert.That(actor.Controls.MovementUrgency).IsEqualTo(1.4f);
        using var entity = fixture.Scene.Actors.GetEntityScript(actor.Entry.EntityId)!;
        await Assert.That(entity.Get("continued")).IsEqualTo(1L);
        await Assert.That(actor.Npc.Unit.Snapshot.Position).IsEqualTo(snapshot);
        await Assert.That(fixture.Admission.World.HasSignalActor(actor.Npc)).IsTrue();
        await Assert.That(fixture.Admission.World.HasAiActor(actor.Npc)).IsFalse();
        await Assert.That(fixture.Errors.Count).IsEqualTo(0);
    }

    [Test]
    public async Task LocateUsesLivePositionsAndRetirementClearsTargetBindingsAndIndexMembership()
    {
        using var fixture = new Fixture();
        var actor = fixture.Admit(1);
        var target = fixture.Admit(2);
        fixture.Positions[actor.Npc.ObjectId] = new(0, 0, 0);
        fixture.Positions[target.Npc.ObjectId] = new(3, 0, 0);
        var pipe = fixture.Scene.GoalPipes.Create("locate_owned")!;
        Append(pipe, ZoneGoalOperation.Locate, new() { Integer = 2, First = 5 });
        Append(pipe, ZoneGoalOperation.AcquireTarget, new());
        actor.Entry.Phase.Pipes.Select(ZoneGoalPipeFlags.RunOnce, pipe.Name);
        actor.Update(1020, .02f);
        await Assert.That(ReferenceEquals(actor.Argument, target.AiObject)).IsTrue();
        await Assert.That(ReferenceEquals(actor.AttentionTarget, target.AiObject)).IsTrue();
        await Assert.That(target.AiObject.BindingEntityId).IsEqualTo(actor.Entry.EntityId);
        await Assert.That(target.AiObject.Position).IsEqualTo(new Vector3(3, 0, 0));

        fixture.Admission.World.Remove(target.Npc);
        await Assert.That(actor.Argument).IsNull();
        await Assert.That(actor.AttentionTarget).IsNull();
        await Assert.That(target.AiObject.BindingEntityId).IsEqualTo(0u);
        await Assert.That(fixture.Scene.Objects.Count).IsEqualTo(1);
        await Assert.That(fixture.Scene.Actors.Find(target.Entry.EntityId)).IsNull();
        await Assert.That(target.HasActor).IsFalse();
        await Assert.That(() => target.Update(1040, .02f)).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task UnsupportedPathAndGameplayServicesRefuseWithoutPublishingAnIdleSuccess()
    {
        using var fixture = new Fixture();
        var actor = fixture.Admit();
        var pipe = fixture.Scene.GoalPipes.Create("requires_pathfinder")!;
        Append(pipe, ZoneGoalOperation.PathFind, new());
        await Assert.That(() => actor.Entry.Phase.Pipes.Select(ZoneGoalPipeFlags.RunOnce, pipe.Name)).Throws<NotSupportedException>();
        await Assert.That(actor.Entry.Phase.Pipes.CurrentPipe!.Name).IsEqualTo("_first_");
        await Assert.That(actor.Entry.Phase.Pipes.Executor.ActiveGoalCount).IsEqualTo(0);
        await Assert.That(() => actor.ChangeSmartObjectState("Busy")).Throws<NotSupportedException>();
        await Assert.That(() => actor.NotifyActionDone()).Throws<NotSupportedException>();
        await Assert.That(() => fixture.Scene.Actors.StartAuxiliaryBehavior(actor.Npc, "unknown", 1)).Throws<NotSupportedException>();
        await Assert.That(actor.GameplayReady).IsFalse();
    }

    [Test]
    public async Task RemovalDisposesBehaviorWaitsAndScriptReferencesBeforeLateCallbacks()
    {
        using var fixture = new Fixture();
        var actor = fixture.Admit();
        var behavior = actor.Behavior!;
        var pipe = fixture.Scene.GoalPipes.Create("retire_wait")!;
        Append(pipe, ZoneGoalOperation.WaitSignal, new() { Text = "OnContinue" }, true);
        actor.Entry.Phase.Pipes.Select(ZoneGoalPipeFlags.RunOnce, pipe.Name);
        actor.Update(1000, .02f);
        using var retainedGoal = actor.Entry.Phase.Pipes.Executor.RetainActiveGoal(0);
        var retiredControls = actor.Controls;
        fixture.Admission.World.Remove(actor.Npc);
        await Assert.That(behavior.IsDisposed).IsTrue();
        await Assert.That(actor.Entry.Phase.IsDisposed).IsTrue();
        await Assert.That(actor.Entry.Phase.Pipes.Executor.SignalWaits.Count).IsEqualTo(0);
        await Assert.That(fixture.Scene.Actors.GetEntityScript(actor.Entry.EntityId)).IsNull();
        await Assert.That(fixture.Scene.Objects.Count).IsEqualTo(0);
        await Assert.That(fixture.Scene.Count).IsEqualTo(0);
        actor.Npc.AiSignals.Send("OnContinue");
        await Assert.That(actor.Npc.AiSignals.Pending.Count).IsEqualTo(0);
        await Assert.That(retiredControls.MovementUrgency).IsEqualTo(.4f);
        await Assert.That(() => retainedGoal.Operation!.Execute()).Throws<ObjectDisposedException>();
        await Assert.That(() => actor.SetActive(true)).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task ConstructorRetirementCannotRepublishItsActorOrScriptHandler()
    {
        using var fixture = new Fixture();
        var actor = fixture.Admit();
        using var remove = fixture.Lua.CreateFunction(_ => { fixture.Scene.Remove(actor.Handle); return []; });
        var behavior = actor.Behavior!;
        using var current = behavior.RetainCurrent()!;
        current.Set("Constructor", remove);
        behavior.SetBehavior("Idle");
        await Assert.That(behavior.IsDisposed).IsTrue();
        await Assert.That(fixture.Scene.Count).IsEqualTo(0);
        await Assert.That(fixture.Scene.Objects.Count).IsEqualTo(0);
        await Assert.That(fixture.Scene.Actors.GetEntityScript(actor.Entry.EntityId)).IsNull();
        await Assert.That(fixture.Admission.World.HasSignalActor(actor.Npc)).IsFalse();
        await Assert.That(actor.HasActor).IsFalse();
    }

    private static void Append(ZoneGoalPipeDefinition pipe, ZoneGoalOperation operation, ZoneGoalParameters parameters,
        bool blocking = false) => pipe.Append(operation, blocking, ZoneGoalGrouping.None, parameters,
            message => throw new InvalidOperationException(message));

    internal sealed class Fixture : IDisposable
    {
        private static int _nextUnit = 600_000;
        private ulong _nextProxy = 0x1_0000;
        public ManagedNpcAdmissionTests.Fixture Admission { get; }
        public ZoneManagedLuaRuntime Lua { get; } = new();
        public ZoneManagedNpcActorScene Scene { get; }
        public Dictionary<uint, Vector3> Positions { get; } = [];
        public List<string> Errors { get; } = [];
        private readonly ZoneLuaReference _resolve;

        public Fixture()
        {
            ZoneManagedNpcActorScene? scene = null;
            Admission = new(handle => scene!.Find(handle) ?? throw new InvalidOperationException("Actor must be adopted before provider ownership."));
            Scene = scene = new(Admission.Registry, Admission.Catalog, Lua, new(), unit => Positions[unit.Snapshot.ObjectId],
                () => 1_000_000, () => 1, _ => throw new NotSupportedException("Unexpected script load."),
                TimeSpan.FromMilliseconds(20), Errors.Add);
            Admission.World.AttachAiRuntime(Scene.Actors.AiRuntime);
            _resolve = Lua.CreateFunction(arguments => [Scene.Actors.Find(unchecked((uint)((ZoneLuaHandle)arguments[0]!).Value)) is not null]);
            Lua.SetGlobal("ResolveOwned", _resolve);
        }

        public ZoneNpc NewNpc()
        {
            var npc = Admission.Npc((uint)Interlocked.Increment(ref _nextUnit));
            Admission.Publish(npc); Positions.Add(npc.ObjectId, Vector3.Zero);
            return npc;
        }

        public ZoneManagedNpcActor Admit(short objectType = 1)
        {
            var npc = NewNpc();
            var handle = Admission.Catalog.Register(npc);
            using var entity = Lua.CreateTable();
            var id = npc.ObjectId + 10_000_000;
            entity.Set("id", new ZoneLuaHandle(id));
            var actor = Scene.Adopt(handle, new() { EntityId = id, Radius = .4f }, objectType, ++_nextProxy,
                ZoneManagedNpcActorScene.CreatePipeUserControls(), true, entity);
            RegisterBehavior(actor, entity);
            actor.Behavior!.SetBehavior("Idle");
            Admission.World.Initialize(npc, null);
            return actor;
        }

        public void RegisterBehavior(ZoneManagedNpcActor actor, ZoneLuaReference entity)
        {
            using var returned = Lua.Execute("""
                return {Idle={Name='Idle',
                  Constructor=function(self,entity)
                    entity.constructed=(entity.constructed or 0)+1
                    entity.resolved=ResolveOwned(entity.id)
                  end,
                  OnContinue=function(self,entity) entity.continued=(entity.continued or 0)+1 end}}
                """);
            var tables = (ZoneLuaReference)returned[0]!;
            ZoneNpcBehavior? behavior = new(Lua, entity, tables, null, null, null, "Idle", "Idle", actor);
            try { Scene.RegisterBehavior(actor, behavior); behavior = null; }
            finally { behavior?.Dispose(); }
        }

        public void Dispose()
        {
            try { Admission.Dispose(); }
            finally { try { Scene.Dispose(); } finally { _resolve.Dispose(); Lua.Dispose(); } }
        }
    }
}
