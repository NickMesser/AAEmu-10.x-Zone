using System.Numerics;
using System.Text;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Ai;
using AAEmu.Zone.Content;
using AAEmu.Zone.Hosting;
using AAEmu.Zone.Npcs;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Scripting;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Tests;

/// <summary>Synthetic authored callbacks verify the complete provider factory and its rollback.</summary>
public sealed class ManagedNpcRuntimeTests
{
    [Test]
    public async Task SceneComponentsReceiveAuthoredCallsAndRetireThroughTheCompositeLifecycle()
    {
        using var fixture = new Fixture(withComponents: true);
        fixture.Runtime.Lua.SetGlobal("testComponents", true);
        fixture.Runtime.Lua.SetGlobal("testTimer", true);
        var npc = fixture.Admission.Npc(model: fixture.Model, heading: new()); fixture.Admission.Publish(npc);
        fixture.Admission.World.Initialize(npc, null);
        var handle = fixture.Admission.Catalog.TryGet(npc)!.Value;
        if (fixture.Admission.Catalog.GetAdmission(handle)?.Refusal is { } refusal) throw new InvalidOperationException(refusal);
        var actor = fixture.Runtime.Actors.Find(handle)!;
        var services = (ZoneManagedNpcSceneScriptServices)fixture.Runtime.ScriptServices;
        await Assert.That(fixture.Runtime.Count).IsEqualTo(1);
        await Assert.That(services.Characters.LiveCount).IsEqualTo(1);
        await Assert.That(services.Characters.Find(handle)!.Definition.Skeleton.Bones.Count).IsEqualTo(1);
        await Assert.That(services.CopyLastColliderEvent(actor)).IsEqualTo(new ZoneManagedNpcColliderEvent(43, 1));
        await Assert.That(services.CopyColliderState(actor).SelectedMode).IsEqualTo(1);
        await Assert.That(fixture.Bodies!.Find(handle)!.CopyState().AppliedDescription.CollisionActive).IsFalse();
        await Assert.That(services.SmartObjects.HasState(actor, "Busy")).IsTrue();
        actor.ChangeSmartObjectState("Alert-Busy");
        await Assert.That(services.SmartObjects.HasState(actor, "Busy")).IsFalse();
        await Assert.That(services.SmartObjects.HasState(actor, "Alert")).IsTrue();
        await Assert.That(services.Effects.CopyState(actor).SelectedInterval).IsEqualTo(1f / 3f);
        services.SetScriptUpdateRate(actor, .05f, .3f);
        await Assert.That(services.Effects.CopyState(actor).SelectedInterval).IsEqualTo(1f / 3f);
        npc.Unit.SetCombat(true);
        await Assert.That(services.Effects.CopyState(actor).SelectedInterval).IsEqualTo(.05f);
        services.ClearCombat(actor);
        await Assert.That(npc.Unit.IsInCombat).IsFalse();
        await Assert.That(services.Effects.CopyState(actor).SelectedInterval).IsEqualTo(.3f);
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(20));
        fixture.Runtime.TickScripts(TimeSpan.FromMilliseconds(20), fixture.Admission.Provider.RemoveNpc);
        await Assert.That(fixture.Runtime.Lua.GetGlobal("timerCalls")).IsEqualTo(1L);
        fixture.Admission.World.Remove(npc);
        await Assert.That(services.Count).IsEqualTo(0);
        await Assert.That(services.Characters.LiveCount).IsEqualTo(0);
        await Assert.That(services.SmartObjects.Count).IsEqualTo(0);
        await Assert.That(services.Effects.Count).IsEqualTo(0);
        await Assert.That(fixture.Bodies.Count).IsEqualTo(0);
        await Assert.That(fixture.Backend!.OwnedPhysics.TryReadPosition(handle, out _)).IsFalse();
        npc.Unit.SetCombat(true); //The retired generation's event44 receiver was unsubscribed.
        await Assert.That(() => fixture.Runtime.Lua.Execute("observedEntity:SetColliderMode(1)")).Throws<ZoneLuaException>();
    }

    [Test]
    public async Task CompositeInitializationFailureUnwindsAlreadyMutatedPhysicalAndScriptOwners()
    {
        using var fixture = new Fixture(withComponents: true);
        fixture.Runtime.Lua.SetGlobal("testComponents", true);
        fixture.Runtime.Lua.SetGlobal("failInitialization", true);
        var npc = fixture.Admission.Npc(model: fixture.Model, heading: new()); fixture.Admission.Publish(npc);
        fixture.Admission.World.Initialize(npc, null);
        var services = (ZoneManagedNpcSceneScriptServices)fixture.Runtime.ScriptServices;
        var handle = fixture.Admission.Catalog.TryGet(npc)!.Value;
        await Assert.That(fixture.Admission.Catalog.GetAdmission(handle)!.Refusal!.Contains("controlled init refusal", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fixture.Runtime.RefusedActors).IsEqualTo(1);
        await Assert.That(fixture.Runtime.Count).IsEqualTo(0);
        await Assert.That(services.Count + services.Characters.LiveCount + services.SmartObjects.Count + services.Effects.Count).IsEqualTo(0);
        await Assert.That(fixture.Bodies!.Count).IsEqualTo(0);
        await Assert.That(fixture.Profiles.LiveCount).IsEqualTo(0);
    }

    [Test]
    public async Task WorldModelOverrideSelectsItsActorClassAndProfileInsteadOfTemplateMetadata()
    {
        using var fixture = new Fixture();
        var templateModel = new ZoneModelDefinition { Id = 99, Name = "metadata_prefab", Subtype = "PrefabModel" };
        var npc = fixture.Admission.Npc(model: templateModel, unitModelId: fixture.Model.Id);
        fixture.Admission.Publish(npc);
        fixture.Admission.World.Initialize(npc, null);
        await Assert.That(npc.Model.Id).IsEqualTo(99u);
        await Assert.That(fixture.Runtime.Count).IsEqualTo(1);
        await Assert.That(fixture.Profiles.LiveCount).IsEqualTo(1);
        await Assert.That(fixture.Runtime.ModelClassesPrepared).IsEqualTo(1);
        await Assert.That(fixture.Admission.World.HasSignalActor(npc)).IsTrue();
    }

    [Test]
    public async Task UnsupportedProductionProfileRefusesBeforeCloningOrRunningKnownFailingInitialization()
    {
        using var fixture = new Fixture(admitProfile: false);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var id = ManagedNpcAdmissionTests.Fixture.FreshObjectId();
            var npc = fixture.Admission.Npc(id, id + 1_000_000, fixture.Model); fixture.Admission.Publish(npc);
            fixture.Admission.World.Initialize(npc, null);
        }
        await Assert.That(fixture.Runtime.RefusedActors).IsEqualTo(4);
        await Assert.That(fixture.Runtime.ModelClassesPrepared).IsEqualTo(0);
        await Assert.That(fixture.Profiles.LiveCount).IsEqualTo(0);
        await Assert.That(fixture.Runtime.Count).IsEqualTo(0);
        await Assert.That(fixture.Runtime.Lua.GetGlobal("observedEntity")).IsNull();
    }

    [Test]
    public async Task OriginalClassCloneConstructorsAndSignalsRunInsideTheProviderGeneration()
    {
        using var fixture = new Fixture();
        var npc = fixture.Admission.Npc(model: fixture.Model); fixture.Admission.Publish(npc);
        fixture.Admission.World.Initialize(npc, null);
        var handle = fixture.Admission.Catalog.TryGet(npc)!.Value;
        var entry = fixture.Runtime.Actors.Actors.Find(npc)!;
        await Assert.That(fixture.Runtime.Count).IsEqualTo(1);
        await Assert.That(fixture.Profiles.LiveCount).IsEqualTo(1);
        await Assert.That(entry.EntityId).IsEqualTo(1u);
        await Assert.That(entry.EntityId == npc.ObjectId).IsFalse();
        await Assert.That(fixture.Admission.World.HasSignalActor(npc)).IsTrue();
        await Assert.That(fixture.Admission.World.HasAiActor(npc)).IsFalse();
        using var entity = entry.RetainScript()!;
        using var ai = (ZoneLuaReference)entity.Get("AI")!;
        using var parameters = (ZoneLuaReference)ai.Get("param")!;
        await Assert.That(parameters.Get("delay")).IsEqualTo(1.5d);
        fixture.Admission.Provider.SendSignal(handle, new(0, "GO_TO_IDLE", 0, 0, null));
        npc.AiSignals.Update();
        await Assert.That(fixture.Runtime.Actors.Find(handle)!.Behavior!.CurrentName).IsEqualTo("idle");
        fixture.Admission.World.Remove(npc);
        await Assert.That(fixture.Runtime.Count).IsEqualTo(0);
        await Assert.That(fixture.Profiles.LiveCount).IsEqualTo(0);
        await Assert.That(() => fixture.Runtime.Lua.Execute("observedEntity:GetName()")).Throws<ZoneLuaException>();
    }

    [Test]
    public async Task FailedInitializationUnwindsProxyBehaviorAndBindingsBeforeRefusalPublication()
    {
        using var fixture = new Fixture();
        fixture.Runtime.Lua.SetGlobal("failInitialization", true);
        var npc = fixture.Admission.Npc(model: fixture.Model); fixture.Admission.Publish(npc);
        fixture.Admission.World.Initialize(npc, null);
        var handle = fixture.Admission.Catalog.TryGet(npc)!.Value;
        await Assert.That(fixture.Runtime.RefusedActors).IsEqualTo(1);
        await Assert.That(fixture.Admission.Catalog.GetAdmission(handle)!.Refusal!.Contains("controlled init refusal", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fixture.Admission.Provider.Count).IsEqualTo(0);
        await Assert.That(fixture.Runtime.Actors.Count).IsEqualTo(0);
        await Assert.That(fixture.Profiles.LiveCount).IsEqualTo(0);
        await Assert.That(fixture.Profiles.RetiredCount).IsEqualTo(1ul);
        await Assert.That(fixture.Admission.World.HasSignalActor(npc)).IsFalse();
        await Assert.That(() => fixture.Runtime.Lua.Execute("observedEntity:GetName()")).Throws<ZoneLuaException>();
    }

    [Test]
    public async Task ScriptCallbackFaultRetiresItsProviderAndAllEffectsWithoutStoppingOtherActors()
    {
        using var fixture = new Fixture(withEffects: true);
        var npc = fixture.Admission.Npc(model: fixture.Model); fixture.Admission.Publish(npc);
        fixture.Admission.World.Initialize(npc, null);
        var handle = fixture.Admission.Catalog.TryGet(npc)!.Value;
        using (var entity = fixture.Runtime.Actors.Actors.Find(npc)!.RetainScript()!) entity.Set("failUpdate", true);
        fixture.Runtime.Actors.Find(handle)!.SetEntityActive(true);
        var otherId = ManagedNpcAdmissionTests.Fixture.FreshObjectId();
        var other = fixture.Admission.Npc(otherId, otherId + 1_000_000, fixture.Model);
        fixture.Admission.Publish(other); fixture.Admission.World.Initialize(other, null);
        fixture.Runtime.Actors.Find(fixture.Admission.Catalog.TryGet(other)!.Value)!.SetEntityActive(true);
        fixture.Runtime.Lua.SetGlobal("failUpdate", true);
        var faults = fixture.Runtime.TickScripts(TimeSpan.FromMilliseconds(20), fixture.Admission.Provider.RemoveNpc);
        await Assert.That(faults.Count).IsEqualTo(1);
        await Assert.That(faults[0].Actor).IsEqualTo(handle);
        await Assert.That(fixture.Admission.Provider.Count).IsEqualTo(1);
        await Assert.That(fixture.Runtime.Count).IsEqualTo(1);
        await Assert.That(((ZoneManagedNpcScriptEffects)fixture.Runtime.ScriptServices).Count).IsEqualTo(1);
        await Assert.That(fixture.Profiles.LiveCount).IsEqualTo(1);
        await Assert.That(fixture.Admission.Catalog.GetAdmission(handle)!.Phase).IsEqualTo(ZoneSceneNpcAdmissionPhase.Refused);
        await Assert.That(fixture.Runtime.TickScripts(TimeSpan.FromMilliseconds(20), fixture.Admission.Provider.RemoveNpc).Count).IsEqualTo(0);
    }

    [Test]
    public async Task EntityTimerUsesThePreciseFrameClockAtItsExactDeadline()
    {
        using var fixture = new Fixture(withEffects: true);
        fixture.Runtime.Lua.SetGlobal("testTimer", true);
        var npc = fixture.Admission.Npc(model: fixture.Model); fixture.Admission.Publish(npc);
        fixture.Admission.World.Initialize(npc, null);
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(20));
        fixture.Runtime.TickScripts(TimeSpan.FromMilliseconds(20), fixture.Admission.Provider.RemoveNpc);
        await Assert.That(fixture.Runtime.Lua.GetGlobal("timerCalls")).IsEqualTo(1L);
        await Assert.That(fixture.Clock.GameMicroseconds).IsEqualTo(20_000L);
        for (var frame = 1; frame < 50; frame++) fixture.Clock.Advance(TimeSpan.FromMilliseconds(20));
        await Assert.That(fixture.Clock.GameMicroseconds).IsEqualTo(1_000_000L);
    }

    [Test]
    public async Task RealConstructionSamplesTheSceneEpochAndInstallsItsOwnDynamicsOnlyAfterTheResetFrame()
    {
        using var fixture = new Fixture(withConstruction: true);
        await Assert.That(fixture.Runtime.PrePhysics(Frame(10), fixture.Admission.Provider.RemoveNpc).Count).IsEqualTo(0);
        fixture.Runtime.Lua.SetGlobal("testHealthAssociation", true);
        var npc = fixture.Admission.Npc(model: fixture.Model, heading: new()); fixture.Admission.Publish(npc);
        npc.Unit.CommitProgression(npc.Unit.Progression, new([], 17, 100, []));
        npc.Unit.Apply(new(npc.ObjectId, 400, 0));
        fixture.Admission.World.Initialize(npc, null);
        var handle = fixture.Admission.Catalog.TryGet(npc)!.Value;
        var actor = RequireActor(fixture, handle);
        var construction = fixture.Construction!.Find(handle)!;
        var body = fixture.Bodies!.Find(handle)!;
        var services = (ZoneManagedNpcSceneScriptServices)fixture.Runtime.ScriptServices;
        var initial = services.CopyColliderState(actor);
        await Assert.That(construction.PhysicalizedFrame).IsEqualTo((int?)10);
        await Assert.That(initial.PhysicalizedFrame).IsEqualTo(10);
        await Assert.That(initial.Frame).IsEqualTo(-1);
        await Assert.That(initial.ForceRebuild).IsFalse();
        await Assert.That(body.CopyState().AppliedDescription.LivingMovement!.Inertia).IsEqualTo(8f);
        await Assert.That(body.CopyState().AppliedDescription.LivingMovement!.InertiaAcceleration).IsEqualTo(0f);
        await Assert.That(construction.Health.HasServerUnit).IsTrue();
        await Assert.That(services.GetActorHealth(actor)).IsEqualTo(4L);
        await Assert.That(services.GetActorMaximumHealth(actor)).IsEqualTo(17L);
        using (var result = fixture.Runtime.Lua.Execute("return observedEntity.actor:GetHealth(), observedEntity.actor:GetMaxHealth(),observedEntity.initialHealth,observedEntity.initialMaxHealth,observedEntity.characterHealth,observedEntity.characterMaxHealth"))
        {
            await Assert.That(result[0]).IsEqualTo("4");
            await Assert.That(result[1]).IsEqualTo("17");
            await Assert.That(result[2]).IsEqualTo("100");
            await Assert.That(result[3]).IsEqualTo("100");
            await Assert.That(result[4]).IsEqualTo("4");
            await Assert.That(result[5]).IsEqualTo("17");
        }
        var before = body.CopyState();
        await Assert.That(fixture.Runtime.PrePhysics(Frame(10), fixture.Admission.Provider.RemoveNpc).Count).IsEqualTo(0);
        await Assert.That(services.CopyColliderState(actor).SelectedMode).IsEqualTo(0);
        await Assert.That(body.CopyState().AppliedDescription.LivingMovement!.Inertia).IsEqualTo(8f);
        await Assert.That(fixture.Runtime.PrePhysics(Frame(11), fixture.Admission.Provider.RemoveNpc).Count).IsEqualTo(0);
        var applied = body.CopyState();
        await Assert.That(services.CopyColliderState(actor).SelectedMode).IsEqualTo(4);
        await Assert.That(services.CopyColliderState(actor).ForceRebuild).IsFalse();
        await Assert.That(applied.AppliedDescription.LivingMovement!.Inertia).IsEqualTo(20f);
        await Assert.That(applied.AppliedDescription.LivingMovement!.InertiaAcceleration).IsEqualTo(11f);
        await Assert.That(applied.Status).IsEqualTo(before.Status);
        await Assert.That(applied.Control).IsEqualTo(before.Control);
        fixture.Admission.World.Remove(npc);
        await Assert.That(construction.IsDisposed).IsTrue();
        await Assert.That(fixture.Construction.Count).IsEqualTo(0);
        await Assert.That(fixture.Poses!.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RealProxyEyePositionSurvivesSceneAndActorRefreshWhileLuaGetPosRetainsTheEntityOrigin()
    {
        using var fixture = new Fixture(withConstruction: true);
        var npc = fixture.Admission.Npc(model: fixture.Model, heading: new()); fixture.Admission.Publish(npc);
        fixture.Admission.World.Initialize(npc, null);
        var handle = fixture.Admission.Catalog.TryGet(npc)!.Value;
        var actor = RequireActor(fixture, handle);
        var body = fixture.Bodies!.Find(handle)!;
        var pose = fixture.Poses!.Find(handle)!;
        var origin = body.CopyState().Status.Position;
        var eye = origin + new Vector3(0, 0, 1.5f);
        await Assert.That(pose.Last!.EntityPosition).IsEqualTo(origin);
        await Assert.That(pose.Last.Position).IsEqualTo(eye);
        await Assert.That(actor.AiObject.Position).IsEqualTo(eye);
        fixture.Runtime.Actors.RefreshPositions();
        fixture.Admission.Provider.Pump(1000); fixture.Admission.Provider.Pump(1020);
        await Assert.That(actor.AiObject.Position).IsEqualTo(eye);
        await Assert.That(body.CopyState().Status.Position).IsEqualTo(origin);
        using (var result = fixture.Runtime.Lua.Execute("local p=observedEntity:GetPos();return p.x,p.y,p.z"))
            await Assert.That(new Vector3(Convert.ToSingle(result[0]), Convert.ToSingle(result[1]), Convert.ToSingle(result[2]))).IsEqualTo(origin);
        var sampled = pose.SampleCount;
        await Assert.That(fixture.Runtime.PublishPose(handle, fixture.Admission.Provider.RemoveNpc).Count).IsEqualTo(0);
        await Assert.That(pose.SampleCount).IsEqualTo(sampled + 1);
        await Assert.That(actor.AiObject.Position).IsEqualTo(eye);
        fixture.Admission.World.Remove(npc);
        await Assert.That(pose.IsDisposed).IsTrue();
        await Assert.That(fixture.Runtime.Actors.ProxyPoses!.Find(handle)).IsNull();
    }

    [Test]
    public async Task UnsupportedGoalUpdateRetiresItsExactRuntimeBeforeBodyPreparationAndLeavesTheOtherActorRunning()
    {
        using var fixture = new Fixture(withConstruction: true);
        fixture.Runtime.PrePhysics(Frame(10), fixture.Admission.Provider.RemoveNpc);
        var npc = fixture.Admission.Npc(model: fixture.Model, heading: new()); fixture.Admission.Publish(npc);
        fixture.Admission.World.Initialize(npc, null);
        var handle = fixture.Admission.Catalog.TryGet(npc)!.Value;
        var actor = RequireActor(fixture, handle);
        var body = fixture.Bodies!.Find(handle)!;
        var construction = fixture.Construction!.Find(handle)!;
        var pose = fixture.Poses!.Find(handle)!;
        var otherId = ManagedNpcAdmissionTests.Fixture.FreshObjectId();
        var other = fixture.Admission.Npc(otherId, otherId + 1_000_000, fixture.Model, heading: new());
        fixture.Admission.Publish(other); fixture.Admission.World.Initialize(other, null);
        var otherHandle = fixture.Admission.Catalog.TryGet(other)!.Value;
        var otherActor = RequireActor(fixture, otherHandle);
        var otherBody = fixture.Bodies.Find(otherHandle)!;
        //These goal-execution controls explicitly activate/unhide the entity and enable AI
        //after the original initialevent6; they do not claim the hidden GameObject slot profile.
        actor.SetEntityHidden(false); actor.SetEntityActive(true);
        otherActor.SetEntityHidden(false); otherActor.SetEntityActive(true);
        actor.SetActive(true); otherActor.SetActive(true);
        var unsupported = fixture.Runtime.Actors.GoalPipes.Create("runtime_unowned_branch")!;
        unsupported.Append(ZoneGoalOperation.Branch, false, ZoneGoalGrouping.None, new() { Integer = 7 },
            message => throw new InvalidOperationException(message));
        actor.Entry.Phase.Pipes.Select(ZoneGoalPipeFlags.RunOnce, unsupported.Name);
        var supported = fixture.Runtime.Actors.GoalPipes.Create("runtime_owned_run")!;
        supported.Append(ZoneGoalOperation.Run, false, ZoneGoalGrouping.None, new() { First = 1 },
            message => throw new InvalidOperationException(message));
        otherActor.Entry.Phase.Pipes.Select(ZoneGoalPipeFlags.RunOnce, supported.Name);
        fixture.Admission.Provider.Pump(1000); fixture.Admission.Provider.Pump(1020);
        await Assert.That(fixture.Runtime.Count).IsEqualTo(2); //Fault queued; no mid-update destruction.
        await Assert.That(otherActor.Controls.MovementUrgency).IsEqualTo(1f);
        body.RequestStanceSlot(3); otherBody.RequestStanceSlot(3);
        IReadOnlyList<ZoneManagedNpcScriptFault> faults = [];
        var diagnostics = new ZoneMovementDiagnostics();
        using var phases = new ZoneManagedNpcMovementFramePhases(new BackendPhases(fixture.Backend!),
            fixture.Admission.Registry, fixture.Admission.Catalog, fixture.Runtime.Actors, fixture.Bodies,
            new(fixture.Admission.Registry, diagnostics, false), diagnostics,
            prePhysics: frame => faults = fixture.Runtime.PrePhysics(frame, fixture.Admission.Provider.RemoveNpc),
            publishPose: (current, _, _) => fixture.Runtime.PublishPose(current.Handle, fixture.Admission.Provider.RemoveNpc));
        var frame = Frame(11);
        await Assert.That(phases.PrePhase(frame)).IsEqualTo(1);
        await Assert.That(faults.Count).IsEqualTo(1);
        await Assert.That(faults[0].Actor).IsEqualTo(handle);
        await Assert.That(faults[0].Message.Contains("branch predicate 7", StringComparison.Ordinal)).IsTrue();
        await Assert.That(body.IsDisposed).IsTrue();
        await Assert.That(body.CurrentStanceSlot).IsEqualTo(ZoneActorStanceCatalog.InitialSlot);
        await Assert.That(otherBody.CurrentStanceSlot).IsEqualTo(3);
        await Assert.That(construction.IsDisposed).IsTrue(); await Assert.That(pose.IsDisposed).IsTrue();
        await Assert.That(fixture.Backend!.OwnedPhysics.TryReadPosition(handle, out _)).IsFalse();
        await Assert.That(fixture.Admission.Catalog.GetAdmission(handle)!.Phase).IsEqualTo(ZoneSceneNpcAdmissionPhase.Refused);
        await Assert.That(fixture.Runtime.Count).IsEqualTo(1); await Assert.That(fixture.Profiles.LiveCount).IsEqualTo(1);
        await Assert.That(fixture.Construction.Count).IsEqualTo(1); await Assert.That(fixture.Poses.Count).IsEqualTo(1);
        await Assert.That(fixture.Admission.Provider.Count).IsEqualTo(1);
        phases.StepPhysics(frame.Elapsed); phases.PostPhase(frame);
        await Assert.That(fixture.Bodies.Find(otherHandle)!.CopyState().CurrentStanceSlot).IsEqualTo(3);
        await Assert.That(fixture.Runtime.PrePhysics(Frame(12), fixture.Admission.Provider.RemoveNpc).Count).IsEqualTo(0);
        fixture.Admission.Provider.Pump(1040);
        await Assert.That(otherActor.HasActor).IsTrue();
    }

    private static ZoneMovementFrame Frame(uint frame) => new(frame, TimeSpan.FromMilliseconds(20), frame * 20, frame * .02f);
    private static ZoneManagedNpcActor RequireActor(Fixture fixture, ZoneActorHandle handle) => fixture.Runtime.Actors.Find(handle) ??
        throw new InvalidOperationException(fixture.Admission.Catalog.GetAdmission(handle)?.Refusal ?? "The runtime actor was not admitted.");

    private sealed class BackendPhases(ZoneManagedPhysicsBackend backend) : IZoneMovementFramePhases
    {
        public int PrePhase(in ZoneMovementFrame frame) => 0;
        public void StepPhysics(TimeSpan elapsed) => backend.Step(elapsed);
        public int PostPhase(in ZoneMovementFrame frame) => 0;
    }

    [Test]
    [Arguments("group", true)]
    [Arguments("position", false)]
    public async Task RecordedIdleConstructorFailureRetiresBeforeTheNextPhysicalOrEffectBoundary(string dependency, bool beforePhysics)
    {
        using var fixture = new Fixture(withConstruction: true);
        fixture.Runtime.Lua.SetGlobal("testTimer", true);
        var npc = fixture.Admission.Npc(model: fixture.Model, heading: new()); fixture.Admission.Publish(npc);
        fixture.Admission.World.Initialize(npc, null);
        var handle = fixture.Admission.Catalog.TryGet(npc)!.Value;
        var actor = RequireActor(fixture, handle);
        var body = fixture.Bodies!.Find(handle)!;
        var construction = fixture.Construction!.Find(handle)!;
        var pose = fixture.Poses!.Find(handle)!;
        using var entity = actor.Entry.RetainScript()!;
        entity.Set("failIdleConstructor", dependency);
        var otherId = ManagedNpcAdmissionTests.Fixture.FreshObjectId();
        var other = fixture.Admission.Npc(otherId, otherId + 1_000_000, fixture.Model, heading: new());
        fixture.Admission.Publish(other); fixture.Admission.World.Initialize(other, null);
        var otherHandle = fixture.Admission.Catalog.TryGet(other)!.Value;
        fixture.Admission.Provider.SendSignal(handle, new(0, "GO_TO_IDLE", 0, 0, null));
        npc.AiSignals.Update(); //The original behavior error channel deliberately does not throw.
        await Assert.That(actor.Behavior!.CurrentName).IsEqualTo("idle");
        await Assert.That(entity.Get("idleConstructorStarted")).IsEqualTo(1L);
        await Assert.That(entity.Get("idleConstructorFinished")).IsNull();
        await Assert.That(fixture.Runtime.Count).IsEqualTo(2);
        IReadOnlyList<ZoneManagedNpcScriptFault> faults;
        if (beforePhysics) faults = fixture.Runtime.PrePhysics(Frame(11), fixture.Admission.Provider.RemoveNpc);
        else
        {
            fixture.Clock.Advance(TimeSpan.FromMilliseconds(20));
            faults = fixture.Runtime.TickScripts(TimeSpan.FromMilliseconds(20), fixture.Admission.Provider.RemoveNpc);
            await Assert.That(fixture.Runtime.Lua.GetGlobal("timerCalls")).IsEqualTo(1L); //Only the healthy owner's due timer ran.
        }
        await Assert.That(faults.Count).IsEqualTo(1);
        await Assert.That(faults[0].Actor).IsEqualTo(handle);
        await Assert.That(faults[0].Callback).IsEqualTo("actor behavior callback");
        await Assert.That(faults[0].Message.Contains(dependency == "group" ? "group queries" : "NpcSetCheckPosContext", StringComparison.Ordinal)).IsTrue();
        await Assert.That(body.IsDisposed).IsTrue(); await Assert.That(construction.IsDisposed).IsTrue(); await Assert.That(pose.IsDisposed).IsTrue();
        await Assert.That(fixture.Runtime.Count).IsEqualTo(1); await Assert.That(fixture.Admission.Provider.Count).IsEqualTo(1);
        await Assert.That(fixture.Profiles.LiveCount).IsEqualTo(1);
        await Assert.That(fixture.Runtime.RefusedActors).IsEqualTo(1);
        await Assert.That(fixture.Admission.World.HasSignalActor(npc)).IsFalse();
        await Assert.That(fixture.Admission.World.HasSignalActor(other)).IsTrue();
        await Assert.That(fixture.Bodies.Find(otherHandle)!.IsCurrent).IsTrue();
        await Assert.That(fixture.Admission.Catalog.GetAdmission(handle)!.Phase).IsEqualTo(ZoneSceneNpcAdmissionPhase.Refused);
        await Assert.That(fixture.Runtime.PrePhysics(Frame(12), fixture.Admission.Provider.RemoveNpc).Count).IsEqualTo(0);
        await Assert.That(fixture.Runtime.TickScripts(TimeSpan.FromMilliseconds(20), fixture.Admission.Provider.RemoveNpc).Count).IsEqualTo(0);
        await Assert.That(fixture.Runtime.RefusedActors).IsEqualTo(1);
    }

    [Test]
    public async Task ActorUpdateObservesRecordedCallbackFailureWithoutReexecutingTheFailedOwner()
    {
        using var fixture = new Fixture(withConstruction: true);
        var npc = fixture.Admission.Npc(model: fixture.Model, heading: new()); fixture.Admission.Publish(npc);
        fixture.Admission.World.Initialize(npc, null);
        var handle = fixture.Admission.Catalog.TryGet(npc)!.Value;
        var actor = RequireActor(fixture, handle);
        using var entity = actor.Entry.RetainScript()!; entity.Set("failIdleConstructor", "group");
        actor.SetEntityHidden(false); actor.SetEntityActive(true);
        actor.SetActive(true);
        var callbackCalls = 0;
        //Declared goal callback exercises the production actor-update boundary; it claims
        //no skill implementation. The actual Lua constructor supplies the swallowed error.
        fixture.Runtime.Actors.OperationFactory = (current, _) => new CallbackGoal(() =>
        { callbackCalls++; current.Behavior!.SetBehavior("idle"); });
        var pipe = fixture.Runtime.Actors.GoalPipes.Create("recorded_constructor_failure")!;
        pipe.Append(ZoneGoalOperation.UseSkill, false, ZoneGoalGrouping.None, new(),
            message => throw new InvalidOperationException(message));
        actor.Entry.Phase.Pipes.Select(ZoneGoalPipeFlags.RunOnce, pipe.Name);
        fixture.Admission.Provider.Pump(1000); fixture.Admission.Provider.Pump(1020);
        var callsInFirstPhase = callbackCalls; //The executor may start and revisit an active goal in this same phase.
        await Assert.That(callsInFirstPhase).IsGreaterThan(0);
        fixture.Admission.Provider.Pump(1040);
        await Assert.That(callbackCalls).IsEqualTo(callsInFirstPhase);
        await Assert.That(actor.Behavior!.CurrentName).IsEqualTo("idle");
        await Assert.That(fixture.Runtime.Count).IsEqualTo(1); //Retirement remains at the next safe scene boundary.
        var faults = fixture.Runtime.PrePhysics(Frame(11), fixture.Admission.Provider.RemoveNpc);
        await Assert.That(faults.Count).IsEqualTo(1);
        await Assert.That(faults[0].Callback).IsEqualTo("actor behavior callback");
        await Assert.That(fixture.Runtime.RefusedActors).IsEqualTo(1);
        await Assert.That(fixture.Runtime.Count + fixture.Bodies!.Count + fixture.Poses!.Count + fixture.Profiles.LiveCount).IsEqualTo(0);
    }

    [Test]
    public async Task EffectEnteringAFailingBehaviorRetiresBeforeTheFollowingAiPump()
    {
        using var fixture = new Fixture(withConstruction: true);
        fixture.Runtime.Lua.SetGlobal("testTimer", true);
        var npc = fixture.Admission.Npc(model: fixture.Model, heading: new()); fixture.Admission.Publish(npc);
        fixture.Admission.World.Initialize(npc, null);
        var handle = fixture.Admission.Catalog.TryGet(npc)!.Value;
        var actor = RequireActor(fixture, handle);
        using var entity = actor.Entry.RetainScript()!;
        entity.Set("failIdleConstructor", "group"); entity.Set("enterBehaviorOnTimer", true);
        using var enter = fixture.Runtime.Lua.CreateFunction(_ => { actor.Behavior!.SetBehavior("idle"); return []; });
        fixture.Runtime.Lua.SetGlobal("EnterRecordedFailure", enter);
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(20));
        var faults = fixture.Runtime.TickScripts(TimeSpan.FromMilliseconds(20), fixture.Admission.Provider.RemoveNpc);
        await Assert.That(entity.Get("afterBehaviorOnTimer")).IsEqualTo(true); //The entity timer itself returned normally.
        await Assert.That(faults.Count).IsEqualTo(1);
        //The typed host failure is attributed to the enclosing protected timer callback.
        await Assert.That(faults[0].Callback).IsEqualTo("Server.OnTimer");
        await Assert.That(faults[0].Message.Contains("group queries", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fixture.Admission.Provider.Count + fixture.Runtime.Count + fixture.Bodies!.Count + fixture.Poses!.Count).IsEqualTo(0);
        await Assert.That(fixture.Admission.World.HasSignalActor(npc)).IsFalse();
        fixture.Admission.Provider.Pump(1000); fixture.Admission.Provider.Pump(1020);
        await Assert.That(fixture.Runtime.RefusedActors).IsEqualTo(1);
    }

    private sealed class CallbackGoal(Action callback) : IZoneGoalOperation
    {
        public int Execute() { callback(); return 1; }
        public void Reset() { }
        public void Dispose() { }
    }

    internal sealed class Fixture : IDisposable
    {
        public ManagedNpcAdmissionTests.Fixture Admission { get; }
        public ZoneManagedNpcProfileFactory Profiles { get; }
        public ZoneManagedNpcRuntime Runtime { get; }
        public ZoneSceneScriptClock Clock { get; } = new();
        public ZoneManagedPhysicsBackend? Backend { get; }
        public ZoneManagedNpcPhysics? Bodies { get; }
        public ZoneManagedNpcActorConstruction? Construction { get; }
        public ZoneManagedNpcAiPose? Poses { get; }
        public ZoneManagedNpcGroups? Groups { get; private set; }
        public ZoneManagedNpcActorMotion? Motion { get; }
        public ZoneManagedNpcMovementRequests? MovementRequests { get; }
        public ZoneManagedNpcMovementAbility? MovementAbility { get; }
        public ZoneStatContent? MovementStats { get; }
        public ZoneManagedNpcReplication? Replication { get; }
        public ZoneManagedNpcPositionChecks? PositionChecks { get; }
        public ZoneManagedNpcPostureState? Postures { get; private set; }
        public ZoneManagedWaterQueries? Water { get; }
        public ZoneManagedNpcRegions? Regions { get; }
        public AAEmu.Zone.Units.ZoneUnitSpatialIndex? Spatial { get; }
        public ZoneMovementFrame CurrentFrame { get; set; }
        public ZoneModelContent Models { get; }
        public ZoneManagedNpcSceneScriptServices Components => (ZoneManagedNpcSceneScriptServices)Runtime.ScriptServices;
        public ZoneModelDefinition Model { get; } = new() { Id = 42, Name = "runtime_fixture", Subtype = "ActorModel", SubtypeId = 9 };
        public Fixture(bool withEffects = false, bool admitProfile = true, bool withComponents = false, bool withConstruction = false,
            bool withMovement = false, ZoneEngineCVars? movementCVars = null, ZoneBuffContent? movementBuffContent = null,
            bool withGameRequests = false, bool withMovementRequests = false, float movementNormalSpeed = 0,
            bool withMovementAbility = false, ZoneManagedStaticGeometry? movementStaticGeometry = null,
            float movementWalkMultiplier = 1, Func<ZoneManagedNpcNavigation>? navigationFactory = null,
            Func<ZoneManagedNpcPuppetOutput>? puppetOutputFactory = null,
            IReadOnlyList<StatModifier>? movementStatModifiers = null)
        {
            withMovement |= withMovementRequests || withMovementAbility;
            withConstruction |= withMovement;
            withComponents |= withConstruction;
            ZoneManagedNpcRuntime? runtime = null;
            var models = new ZoneModelContent([Model], actorModels: new ZoneActorModelContent([new()
            {
                Id = 9, ModelFile = "objects/fixture.chr", PhysicsLivingMass = 80, PhysicsLivingGravity = 25,
                PhysicsLivingColliderMat = "mat_player_collider",
                GameInertia = withConstruction ? 20 : 0, GameInertiaAccel = withConstruction ? 11 : 0,
                GameWalkMultiplier = withMovementRequests ? movementWalkMultiplier : 0,
                PhysicsLivingMinSlideAngle = 45, PhysicsLivingMaxClimbAngle = 50, PhysicsLivingMinFallAngle = 50,
            }], Enumerable.Range(1, 9).Select(id => new ZoneGameStanceDefinition
            { Id = (uint)id, ActorModelId = 9, StanceId = id, SizeX = .3f, SizeY = .3f, SizeZ = .3f, HeightCollider = 1.2f, HeightPivot = .1f, UseCapsule = true,
                ViewOffsetZ = withConstruction ? 1.5f : 0, NormalSpeed = movementNormalSpeed })));
            Models = models;
            Vector3 Position(AAEmu.Zone.Units.ZoneUnit unit) => withMovement ? ZoneMovementPoseMath.ToMeters(unit.Pose.Position) :
                withComponents ? new(1100, 1100, 10.1f) : Vector3.Zero;
            var content = new ZoneNpcContent([new() { Id = 11, ModelId=Model.Id, NpcAiParamId = 8 }], [], [], [], [],
                aiParameters: [new(8, "delay=1.5")],models:models);
            MovementStats = withMovement ? new(new uint[]{14,15,22,23,24,25,26,27}.Select(kind=>
                new UnitFormulaDefinition(kind,AAEmu.Zone.Protocol.Units.ZoneUnitKind.Npc,kind,new ContentFormula("100"))),
                [],[new(11,0,0,0,0)],[],[],[],[],movementStatModifiers??[],[], movementBuffContent) : null;
            Admission = new(handle => runtime!.CreateActor(handle),content:withMovement?content:null,
                stats:MovementStats,
                modelPosition:withMovement?Position:null,sceneBounds:withMovement?new(0,0,3,3):null,
                engineCVars:movementCVars);
            Spatial = Admission.Registry.Spatial;
            Profiles = new(Admission.Registry, Admission.Catalog, Position, models);
            if (withComponents)
            {
                var heights = new AAEmu.Zone.Terrain.ZoneTerrainHeightField();
                heights.LoadCell(1, 1, TerrainHeightFieldTests.CreateCell(10, 0));
                Backend = new(new(heights), staticGeometry: movementStaticGeometry); Bodies = new(Backend, models, _ => 143);
            }
            if (withConstruction) Construction = new(Admission.Registry, Admission.Catalog, models);
            ZoneManagedNpcActorProfile CreateProfile(ZoneActorHandle actor, ZoneNpc npc, uint entityId) => Construction is { } construction
                ? Profiles.CreateWithConstruction(actor, npc, entityId, construction) : Profiles.Create(actor, npc, entityId);
            var assets=new Assets(withMovement);
            Runtime = runtime = new(Admission.Registry, Admission.Catalog, models, content, new Source(),
                Position, () => Clock.GameMicroseconds, () => 1, () => DateTimeOffset.UnixEpoch,
                TimeSpan.FromMilliseconds(20), createProfile: admitProfile ? CreateProfile : null,
                createScriptServices: withComponents ? lua => new ZoneManagedNpcSceneScriptServices(lua, models, assets,
                    () => Bodies!, _ => new(0, 0), Position, () => Clock.GameMicroseconds, () => false,
                    ZoneCharacterAttachmentPublicationProfile.BoneOnly,
                    requests: withGameRequests ? new(Admission.Registry, Admission.Catalog,
                        value => Admission.Registry.QueueNotification(value), actor =>
                        {
                            var state = actor.Npc.Unit.Casting.CopySnapshot();
                            return new(state.CastTimeline, state.CurrentSkill, state.ChannelTimeline);
                        }, queueWithActorScope: true) : null,
                    combat: new(Admission.Registry, Admission.Catalog, value => Admission.Registry.QueueNotification(value)),
                    construction: Construction, poses: withConstruction ? () => Poses! : null,
                    groups:withMovement?Groups=new(lua,content):null,
                    motion:withMovement?()=>Motion!:null,replication:withMovement?()=>Replication!:null,
                    positionChecks:withMovement?()=>PositionChecks!:null,
                    postures:withMovement?Postures=new(new([new(11,50,0,0,"")],[new(0,0)]),
                        new ZoneAnimationActionContent([]),()=>1,value=>Admission.Registry.QueueNotification(value),Admission.Registry):null,
                    npcSkills:withMovement?ZoneNpcSkillContent.Empty:null,
                    movementAbility:withMovementAbility?()=>MovementAbility!:null,
                    movementRequests:withMovementRequests?()=>MovementRequests!:null,
                    navigation:navigationFactory,puppetOutput:puppetOutputFactory) :
                    withEffects ? lua => new ZoneManagedNpcScriptEffects(lua, () => Clock.GameMicroseconds) : null);
            if (withConstruction) Runtime.Actors.ProxyPoses = Poses = new(Runtime.Actors, Bodies!);
            if (Runtime.ScriptServices is ZoneManagedNpcSceneScriptServices components)
                Runtime.Actors.SmartObjectStateConsumer = components.ModifySmartObjectStates;
            if(withMovement)
            {
                var sectors=string.Concat(from x in Enumerable.Range(0,16)from y in Enumerable.Range(0,16)select $"<s x='{x}' y='{y}'/>");
                var world=ZoneWorldMapContent.Read("<World name='movement' resolution='1024' unitSize='2' cellXCount='4' cellYCount='4'><ZoneList>"+
                    "<Zone id='129' name='admitted' originX='0' originY='0'><cellList><Cell x='0' y='0'><sectorList>"+sectors+
                    "</sectorList></Cell><Cell x='1' y='1'><sectorList>"+sectors+"</sectorList></Cell></cellList></Zone>"+
                    "<Zone id='140' name='other'><cellList><Cell x='1' y='1'><sectorList><s x='2' y='2'/></sectorList></Cell></cellList></Zone>"+
                    "</ZoneList></World>","movement");
                var layout=new ZoneWorldMapLayout(world,world.Zones[0]);Regions=new(layout);
                var cells=layout.Cells.Select(placement=>new ZoneManagedSceneCellLoadReport(placement,
                    new(ZoneTerrainCellLoadStatus.Empty),new(null,null,null,null,
                    ["Objects: authored asset is absent.","BigObjects: authored asset is absent."]))).ToArray();
                Water=new(0,cells,ZoneManagedWaterQueryProfile.StaticServerWithoutOceanVortex);
                Motion=new(Runtime.Actors,Bodies!,Construction!,Water,Components.Characters,assets,models);
                Replication=new(Admission.Registry,Runtime.Actors,Motion,movementCVars??ZoneEngineCVars.Empty,()=>0,true);
                PositionChecks=new(Runtime.Lua,Admission.Registry,Groups!,Regions,new(Bodies!),Bodies!,Components,
                    Motion,Replication,Postures!,default);
                if(withMovementRequests) MovementRequests=new(Runtime.Actors,Bodies!,Construction!,Motion,Poses!,Components,
                    MovementStats!,movementCVars);
                if(withMovementAbility) MovementAbility=new(Runtime.Actors,Motion,models);
                Admission.World.AttachManagedMovement(Runtime.Actors,Motion,PositionChecks,Replication,()=>CurrentFrame);
            }
            Admission.World.AttachAiRuntime(Runtime.Actors.Actors.AiRuntime);
            if (!Runtime.Bootstrap().Ready) throw new InvalidOperationException(Runtime.Scripts.BootstrapReport.Refusal);
        }
        public void Dispose()
        { try { Admission.Dispose(); } finally { try { Runtime.Dispose(); } finally { try { PositionChecks?.Dispose();Replication?.Dispose();MovementAbility?.Dispose();MovementRequests?.Dispose();Motion?.Dispose();
            Postures?.Dispose();Groups?.Dispose();Regions?.Dispose();Water?.Dispose();Poses?.Dispose(); Construction?.Dispose(); Bodies?.Dispose();Spatial?.Dispose(); } finally { Profiles.Dispose(); } } } }
    }

    private sealed class Source : IZoneScriptSource
    {
        public ZoneScriptFile? Read(string filename)
        {
            var source = filename.ToLowerInvariant() switch
            {
                "scripts/common.lua" => "function new(t) if type(t)~='table' then return t end;local r={};for k,v in pairs(t)do r[k]=new(v)end;return r end",
                "scripts/ai/aiconfig.lua" => """
                    AICharacter={AVAILABLE={},INTERNAL={},DEFAULT={}}
                    AICharacter.default={AnyBehavior={GO_TO_SPAWN='spawning',GO_TO_IDLE='idle'},Constructor=function(self,e)
                      e.AI.constructed=true;function e.AI:SetParamX2(p)self.param=assert(loadstring('return {'..p..'}'))()end
                      if testHealthAssociation then e.characterHealth=e.actor:GetHealth();e.characterMaxHealth=e.actor:GetMaxHealth()end
                    end}
                    AIBehaviour={AVAILABLE={},INTERNAL={},DEFAULT={},spawning={Name='spawning'},idle={Name='idle',Constructor=function(self,e)
                      e.idleConstructorStarted=(e.idleConstructorStarted or 0)+1
                      if e.failIdleConstructor=='group' then AI.GetGroupOf(e.id)end
                      if e.failIdleConstructor=='position' then e.unit:NpcSetCheckPosContext(1)end
                      e.idleConstructorFinished=true
                    end}}
                    X2AI={commandSet={CanRunCommandSet=function()return false end,RunCommandSet=function()end,EndCommandSet=function()end}}
                    """,
                "scripts/entities/actor/npc.lua" => """
                    BasicActor={ResetCommon=function(e)e.AI={commandSet=new(X2AI.commandSet)}end}
                    BasicAI={OnReset=function(e)BasicActor.ResetCommon(e)end}
                    npc={ActorTemplate={Properties={soclasses_SmartObjectClass='Npc'},PropertiesInstance={bAutoDisable=1},Server={OnTimer=function(e,id,ms)
                      timerCalls=(timerCalls or 0)+1
                      if e.enterBehaviorOnTimer then EnterRecordedFailure();e.afterBehaviorOnTimer=true end
                    end,OnUpdate=function(e)
                      if failUpdate and e.failUpdate then System.Error('controlled update refusal')end
                    end,OnInit=function(e)
                      observedEntity=e;BasicAI.OnReset(e)
                      if testGroup then e.Properties.groupid=testGroup end
                      if testHealthAssociation then e.initialHealth=e.actor:GetHealth();e.initialMaxHealth=e.actor:GetMaxHealth()end
                      if testComponents then e:HideAttachment(0,'Animated LAW',true,true);e:SetColliderMode(1);e.unit:SetSoState('Busy')end
                      if failInitialization then System.Error('controlled init refusal');return end
                      if testTimer then e:SetTimer(1,20)end
                    end}}}
                    function npc:Create(name,source)_G[name]=new(self.ActorTemplate)end
                    """,
                _ => null,
            };
            return source is null ? null : new(Encoding.UTF8.GetBytes(source), filename);
        }
    }

    private sealed class Assets(bool initialPose=false) : IZoneAssetSource
    {
        // One authored compiled-bones0x801 chunk; no attachment with the script's queried name.
        public Stream? OpenRead(string name)
        {
            if (name != "game/objects/fixture.chr") return null;
            const int chunk = 44, size = 48 + 324;
            using var output = new MemoryStream(); using var writer = new BinaryWriter(output, Encoding.UTF8, true);
            writer.Write("CryTek\0\0"u8); writer.Write(0xffff0000u); writer.Write(0x745u); writer.Write(20);
            writer.Write(1); writer.Write(0xacdc0000u); writer.Write(0x801u); writer.Write(chunk); writer.Write(19); writer.Write(size);
            writer.Write(0xacdc0000u); writer.Write(0x801u); writer.Write(chunk); writer.Write(19); writer.Write(new byte[size - 16]);
            var bytes = output.ToArray(); Encoding.UTF8.GetBytes(initialPose?"camera":"root").CopyTo(bytes, chunk + 48 + 216);
            if(initialPose)
            {
                foreach(var diagonal in new[]{0,5,10})System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(chunk+48+276+diagonal*4),1);
                System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(chunk+48+320),1.5f);
            }
            return new MemoryStream(bytes, false);
        }
    }
}
