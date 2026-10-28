using System.Numerics;
using System.Text;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Ai;
using AAEmu.Zone.Content;
using AAEmu.Zone.Npcs;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Scripting;

namespace AAEmu.Zone.Tests;

public sealed class ManagedNpcScriptHostTests
{
    [Test]
    public async Task BootstrapBuildsRealCatalogsAndExposesOnlyTheInjectedClock()
    {
        using var fixture = new Fixture();
        await Assert.That(fixture.Host.Bootstrap().Ready).IsTrue();
        await Assert.That(fixture.Host.GoalPipes.Find("fixture_pipe")!.Goals.Count).IsEqualTo(1);
        await Assert.That(fixture.Host.GoalPipes.Find("fixture_pipe")!.Goals[0].Operation).IsEqualTo(ZoneGoalOperation.Timeout);
        await Assert.That(fixture.Host.TrackPatterns.Patterns["fixture_pattern"].Nodes.Count).IsEqualTo(1);
        await Assert.That(fixture.Host.CombatClasses.GetScale(0, 0)).IsEqualTo(1f);
        using var values = fixture.Lua.Execute("return os.time(),System.GetCurrTime(),System.IsEditor()");
        await Assert.That(values[0]).IsEqualTo(12345L);
        await Assert.That(values[1]).IsEqualTo(2.5d);
        await Assert.That(values[2] is false).IsTrue();
        await Assert.That(() => fixture.Lua.Execute("os.execute('unused')")).Throws<ZoneLuaException>();
        await Assert.That(() => fixture.Lua.Execute("AI.UnportedDecision()")).Throws<ZoneLuaException>();
    }

    [Test]
    public async Task EntityAndWorldUnitIdentitiesAreDistinctAndNpcMutatorsUseTheRealOwner()
    {
        using var fixture = new Fixture(); using var admitted = fixture.Admit();
        var npc = admitted.Actor.Npc;
        fixture.Lua.SetGlobal("entity", admitted.Prepared.EntityScript);
        using var values = fixture.Lua.Execute("""
            entity.unit:NpcActivateAggroUpdate(false)
            entity.unit:NpcActivateAITimeEvent(true,.1239)
            entity.unit:NpcSetInteractable(false)
            local a=entity:GetPos();local b=entity:GetPos()
            return entity:GetName(),entity.unit:GetId(),entity.unit:GetName(),a.x,a.y,a.z,a~=b,
                System.GetEntity(entity.id).id==entity.id
            """);
        await Assert.That(values[0]).IsEqualTo("allocated-entity-name");
        await Assert.That(values[1]).IsEqualTo((long)(float)npc.ObjectId);
        await Assert.That(values[2]).IsEqualTo(npc.Unit.Snapshot.Name);
        await Assert.That(values[3]).IsEqualTo(7L);
        await Assert.That(values[4]).IsEqualTo(8L);
        await Assert.That(values[5]).IsEqualTo(9L);
        await Assert.That(values[6] is true).IsTrue();
        await Assert.That(values[7] is true).IsTrue();
        await Assert.That(npc.ObjectId == npc.Id || npc.ObjectId == admitted.Actor.AiObject.EntityId).IsFalse();
        await Assert.That(npc.AggroEnabled).IsFalse();
        await Assert.That(npc.Timers.AiTimeEventEnabled).IsTrue();
        await Assert.That(npc.Timers.AiTimeEventInterval).IsEqualTo(123u);
        await Assert.That(npc.Talks.Interactable).IsFalse();
    }

    [Test]
    public async Task DefaultProfileRefusesTheFirstMissingEntityEffectBeforeClaimingInitialization()
    {
        using var fixture = new Fixture(new ZoneManagedNpcScriptServices());
        using var admitted = fixture.Admit();
        var readiness = fixture.Host.Initialize(admitted.Binding);
        await Assert.That(readiness.EntityInitialized).IsFalse();
        await Assert.That(readiness.SignalReady).IsFalse();
        await Assert.That(readiness.GameplayReady).IsFalse();
        await Assert.That(readiness.Refusal!.Contains("entity.SetScriptUpdateRate", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task DedicatedMovementTargetValidatesArgumentsWithoutUsingServicesOrChangingPose()
    {
        //Every service in this profile refuses. The original dedicated leaf needs none of them.
        using var fixture = new Fixture(new ZoneManagedNpcScriptServices()); using var admitted = fixture.Admit();
        fixture.Lua.SetGlobal("entity", admitted.Prepared.EntityScript);
        var position = admitted.Actor.AiObject.Position;
        using var values = fixture.Lua.Execute("""
            local before=entity:GetPos()
            local count=select('#',entity.actor:SetMovementTarget({x=100,y=200,z=300},
                {x=-11,y=-22,z=-33},{x=2,y=3,z=4},17))
            local after=entity:GetPos()
            return count,before.x==after.x and before.y==after.y and before.z==after.z
            """);
        await Assert.That(values[0]).IsEqualTo(0L);
        await Assert.That(values[1] is true).IsTrue();
        await Assert.That(admitted.Actor.AiObject.Position).IsEqualTo(position);
        foreach (var arguments in new[]
        {
            "nil,{x=0,y=0,z=0},{x=0,y=0,z=1},1",
            "{x=0,y=0,z=0},42,{x=0,y=0,z=1},1",
            "{x=0,y=0,z=0},{x=0,y=0,z=0},'invalid',1",
            "{x=0,y=0,z=0},{x=0,y=0,z=0},{x=0,y=0,z=1},nil"
        })
            await Assert.That(() => fixture.Lua.Execute("entity.actor:SetMovementTarget(" + arguments + ")")).Throws<ZoneLuaException>();
        await Assert.That(admitted.Actor.AiObject.Position).IsEqualTo(position);
    }

    [Test]
    public async Task AdmittedEffectsInitializeAndRealQueuedSignalsAdvanceTheAuthoredBehavior()
    {
        var effects = new Effects(); using var fixture = new Fixture(effects); using var admitted = fixture.Admit();
        var readiness = fixture.Host.Initialize(admitted.Binding);
        await Assert.That(readiness.SignalReady).IsTrue();
        await Assert.That(readiness.GameplayReady).IsFalse();
        await Assert.That(effects.EntityTimers[(admitted.Actor.Handle, 5)]).IsEqualTo(500);
        await Assert.That(effects.ScriptProxyResets).IsEqualTo(1);
        await Assert.That(effects.CombatInterval).IsEqualTo(.05f);
        await Assert.That(effects.PeaceInterval).IsEqualTo(1f / 3f);
        await Assert.That(effects.States[admitted.Actor.Handle]).IsEqualTo("Busy");
        await Assert.That(admitted.Actor.Npc.AggroEnabled).IsFalse();
        await Assert.That(admitted.Actor.Npc.Timers.AiTimeEventInterval).IsEqualTo(100u);
        await Assert.That(admitted.Actor.Entry.Phase.Pipes.CurrentPipe!.Name).IsEqualTo("spawn_wait");
        admitted.Actor.Npc.AiSignals.Send("OnAITimeEvent");
        admitted.Actor.Npc.AiSignals.Update();
        await Assert.That(admitted.Actor.Behavior!.CurrentName).IsEqualTo("idle");
        await Assert.That(admitted.Actor.Npc.Timers.AiTimeEventEnabled).IsFalse();
        await Assert.That(admitted.Actor.Npc.AggroEnabled).IsTrue();
        await Assert.That(fixture.Host.RecentErrors.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RetiredCallbacksCannotMutateANewActorReusingTheEntityId()
    {
        using var fixture = new Fixture(); using var old = fixture.Admit();
        fixture.Lua.SetGlobal("oldEntity", old.Prepared.EntityScript);
        var id = old.Actor.AiObject.EntityId;
        fixture.Scene.Remove(old.Actor.Handle);
        using var replacement = fixture.Admit(id);
        await Assert.That(() => fixture.Lua.Execute("oldEntity.unit:NpcActivateAggroUpdate(false)")).Throws<ZoneLuaException>();
        await Assert.That(replacement.Actor.Npc.AggroEnabled).IsTrue();
        fixture.Lua.SetGlobal("newEntity", replacement.Prepared.EntityScript);
        await Assert.That(() => fixture.Lua.Execute("newEntity.unit.NpcActivateAggroUpdate(newEntity,false)")).Throws<ZoneLuaException>();
        replacement.Binding.Dispose();
        await Assert.That(() => fixture.Lua.Execute("newEntity.unit:NpcActivateAggroUpdate(false)")).Throws<ZoneLuaException>();
        await Assert.That(replacement.Actor.Npc.AggroEnabled).IsTrue();
    }

    [Test]
    public async Task NonthrowingSystemErrorIsAttributedToTheInitializingGeneration()
    {
        using var fixture = new Fixture(new Effects()); using var admitted = fixture.Admit();
        admitted.Prepared.EntityScript.Set("rejectParameters", true);
        var result = fixture.Host.Initialize(admitted.Binding);
        await Assert.That(result.ParametersApplied).IsFalse();
        await Assert.That(result.SignalReady).IsFalse();
        await Assert.That(fixture.Host.RecentErrors.Single().Actor).IsEqualTo((ZoneActorHandle?)admitted.Actor.Handle);
        await Assert.That(fixture.Host.RecentErrors.Single().Message).IsEqualTo("authored parameter refusal");
    }

    /// <summary>Each admitted effect has a concrete owned output; omitted interface effects still refuse.</summary>
    private sealed class Effects : IZoneManagedNpcScriptServices
    {
        public Dictionary<(ZoneActorHandle, int), int> EntityTimers { get; } = [];
        public Dictionary<ZoneActorHandle, string> States { get; } = [];
        public int ScriptProxyResets { get; private set; }
        public float CombatInterval { get; private set; }
        public float PeaceInterval { get; private set; }
        public float GetActorMaximumHealth(ZoneManagedNpcActor actor) => 731;
        public void SetEntityTimer(ZoneManagedNpcActor actor, int timerId, int milliseconds) => EntityTimers[(actor.Handle, timerId)] = milliseconds;
        public void SetScriptUpdateRate(ZoneManagedNpcActor actor, float combatInterval, float peaceInterval)
        { ScriptProxyResets++; CombatInterval = combatInterval; PeaceInterval = peaceInterval; }
        public void SetSmartObjectState(ZoneManagedNpcActor actor, string state, bool modify) => States[actor.Handle] = state;
    }

    private sealed class Fixture : IDisposable
    {
        private static int _nextUnit = 700_000, _nextEntity = 11_000_000;
        private ulong _proxy = 0x9200;
        public ManagedNpcAdmissionTests.Fixture Admission { get; } = new();
        public ZoneManagedLuaRuntime Lua { get; } = new();
        public ZoneManagedNpcActorScene Scene { get; }
        public ZoneManagedNpcScriptHost Host { get; }
        public Fixture(IZoneManagedNpcScriptServices? effects = null)
        {
            var source = new Source();
            var loader = new ZoneScriptLoader(Lua, source, _ => { }, () => 0);
            Scene = new(Admission.Registry, Admission.Catalog, Lua, new(), _ => new(7, 8, 9),
                () => 2_500_000, () => 1, name => { if (!loader.ExecuteFile(name)) throw new ZoneLuaException(name); },
                TimeSpan.FromMilliseconds(20));
            Admission.World.AttachAiRuntime(Scene.Actors.AiRuntime);
            var content = new ZoneNpcContent([new() { Id = 11, AiFileId = 15, NpcAiParamId = 8 }], [], [], [], [],
                aiFiles: [new(15, "hold_position", "")], aiParameters: [new(8, "delay=1.5")]);
            Host = new(Lua, loader, content, Scene, effects ?? new ZoneManagedNpcScriptServices(), () => 2_500_000,
                () => DateTimeOffset.FromUnixTimeSeconds(12345));
            if (!Host.Bootstrap().Ready) throw new InvalidOperationException(Host.BootstrapReport.Refusal);
        }
        public Admitted Admit(uint entityId = 0)
        {
            var npc = Admission.Npc((uint)Interlocked.Increment(ref _nextUnit)); Admission.Publish(npc);
            var handle = Admission.Catalog.Register(npc);
            var actor = Scene.Prepare(handle, new() { EntityId = entityId == 0 ? (uint)Interlocked.Increment(ref _nextEntity) : entityId, Radius = .4f },
                ZoneManagedNpcActorScene.CreatePipeUserControls(), true);
            using var template = Host.Scripts.RetainGenericActorTemplate();
            var prepared = Host.Scripts.Prepare(11, new(actor.AiObject.EntityId), template, actor);
            var binding = Host.BindEntity(prepared, actor, "allocated-entity-name");
            Scene.Adopt(actor, 1, ++_proxy, prepared.EntityScript);
            Scene.RegisterBehavior(actor, prepared.DetachBehavior());
            return new(actor, prepared, binding);
        }
        public void Dispose()
        { try { Scene.Dispose(); } finally { try { Host.Dispose(); } finally { Lua.Dispose(); Admission.Dispose(); } } }
    }

    private sealed record Admitted(ZoneManagedNpcActor Actor, ZoneNpcAuthoredEntity Prepared, ZoneManagedNpcScriptBinding Binding) : IDisposable
    {
        public void Dispose() { Actor.Dispose(); Binding.Dispose(); Prepared.Dispose(); }
    }

    private sealed class Source : IZoneScriptSource
    {
        public ZoneScriptFile? Read(string filename)
        {
            var source = filename.ToLowerInvariant() switch
            {
                "scripts/common.lua" => """
                    function new(t)if type(t)~='table' then return t end;local r={};for k,v in pairs(t)do r[k]=new(v)end;return r end
                    """,
                "scripts/ai/aiconfig.lua" => """
                    AICharacter={AVAILABLE={},INTERNAL={},DEFAULT={}}
                    AICharacter.hold_position={AnyBehavior={GO_TO_SPAWN='spawning',GO_TO_IDLE='idle'},Constructor=function(self,e)
                        function e.AI:SetParamX2(p)
                            if e.rejectParameters then System.Error('authored parameter refusal');return end
                            self.param=assert(loadstring('return {'..p..'}'))()
                        end
                    end}
                    AIBehaviour={AVAILABLE={},INTERNAL={},DEFAULT={}}
                    AIBehaviour.spawning={Name='spawning',Constructor=function(self,e)
                        e.unit:NpcActivateAggroUpdate(false);e.unit:NpcActivateAITimeEvent(true,.1)
                        e.unit:SetSoState('Busy')
                        AI.BeginGoalPipe('spawn_wait');AI.PushGoal('run',1,0);AI.EndGoalPipe();e:SelectPipe(16,'spawn_wait')
                    end,Destructor=function(self,e)e.unit:NpcActivateAggroUpdate(true)end,
                    OnAITimeEvent=function(self,e)e.unit:NpcActivateAITimeEvent(false,0);AI.Signal(0,0,'GO_TO_IDLE',e.id)end}
                    AIBehaviour.idle={Name='idle'}
                    X2AI={commandSet={CanRunCommandSet=function()return false end,RunCommandSet=function()end,EndCommandSet=function()end}}
                    AI.CreateGoalPipe('fixture_pipe');AI.PushGoal('fixture_pipe','timeout',1,1)
                    AI.BeginTrackPattern('fixture_pattern',1,1,2);AI.AddPatternNode('root',0,0,0,2);AI.EndTrackPattern()
                    AI.AddCombatClass(0,{1})
                    """,
                "scripts/entities/actor/npc.lua" => """
                    BasicActor={ResetCommon=function(e)
                        e.lastHealth=e.actor:GetMaxHealth();e:SetTimer(5,500);e.AI={commandSet=new(X2AI.commandSet)}
                    end}
                    BasicAI={OnReset=function(e)e:NetPresent(1);e:SetScriptUpdateRate(.05,1/3);BasicActor.ResetCommon(e)end}
                    npc={ActorTemplate={Properties={},Server={OnInit=function(e)BasicAI.OnReset(e)end}}}
                    function npc:Create(name,source)_G[name]=new(self.ActorTemplate)end
                    """,
                _ => null
            };
            return source is null ? null : new(Encoding.UTF8.GetBytes(source), filename);
        }
    }
}
