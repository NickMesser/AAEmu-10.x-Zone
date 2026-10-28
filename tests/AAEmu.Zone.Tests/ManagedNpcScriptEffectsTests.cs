using System.Text;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Content;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Scripting;

namespace AAEmu.Zone.Tests;

public sealed class ManagedNpcScriptEffectsTests
{
    [Test]
    public async Task InitializationUsesIndependentCopiedActorHealthAndOwnsTheEntityTimer()
    {
        using var fixture = new Fixture(); using var actor = fixture.Admit();
        var readiness = fixture.Host.Initialize(actor.Binding);
        await Assert.That(readiness.SignalReady).IsTrue();
        await Assert.That(readiness.GameplayReady).IsFalse();
        await Assert.That(actor.Prepared.EntityScript.Get("lastHealth")).IsEqualTo(10L);
        await Assert.That(fixture.Effects.GetActorHealth(actor.Actor)).IsEqualTo(0f);
        await Assert.That(fixture.Effects.CopyState(actor.Actor).Timers).IsEqualTo(1);
        await Assert.That(actor.Actor.Npc.Timers.AiTimeEventEnabled).IsFalse();
        actor.Effects.Health.CopyFields(7, 731);
        using var values = fixture.Lua.Execute("return entity.actor:GetHealth(),entity.actor:GetMaxHealth()");
        await Assert.That(values[0]).IsEqualTo(7L);
        await Assert.That(values[1]).IsEqualTo(731L);
        await Assert.That(() => fixture.Lua.Execute("entity:HideAttachment(0,'unowned attachment',true,true)"))
            .Throws<ZoneLuaException>();
    }

    [Test]
    public async Task UpdateIntervalsNeedSelectionAndUseStrictFloatAccumulationOncePerTick()
    {
        using var fixture = new Fixture(); using var actor = fixture.Admit();
        fixture.Effects.SetScriptUpdateRate(actor.Actor, 1, 2);
        var initial = fixture.Effects.CopyState(actor.Actor);
        await Assert.That(initial.SelectedInterval).IsEqualTo(1f / 3f);
        await Assert.That(initial.AccumulatedTime).IsEqualTo(10f);
        fixture.Effects.Tick(TimeSpan.FromSeconds(.125), scriptsEnabled: false);
        await Assert.That(fixture.Effects.CopyState(actor.Actor).AccumulatedTime).IsEqualTo(10f);
        fixture.Effects.Tick(TimeSpan.FromSeconds(.125));
        using (var values = fixture.Lua.Execute("return #entity.updates,entity.updates[1]"))
        {
            await Assert.That(values[0]).IsEqualTo(1L);
            await Assert.That(values[1]).IsEqualTo(10.125d);
        }
        fixture.Effects.SelectScriptUpdateInterval(actor.Actor, true);
        fixture.Effects.Tick(TimeSpan.FromSeconds(1));
        fixture.Effects.Tick(TimeSpan.Zero);
        using (var values = fixture.Lua.Execute("return #entity.updates")) await Assert.That(values[0]).IsEqualTo(1L);
        fixture.Effects.Tick(TimeSpan.FromSeconds(.25));
        fixture.Effects.Tick(TimeSpan.FromSeconds(5));
        using (var values = fixture.Lua.Execute("return #entity.updates,entity.updates[2],entity.updates[3]"))
        {
            await Assert.That(values[0]).IsEqualTo(3L);
            await Assert.That(values[1]).IsEqualTo(1.25d);
            await Assert.That(values[2]).IsEqualTo(5L);
        }
        fixture.Effects.SetScriptUpdateRate(actor.Actor, 7, 9);
        await Assert.That(fixture.Effects.CopyState(actor.Actor).SelectedInterval).IsEqualTo(1f);
        fixture.Effects.SelectScriptUpdateInterval(actor.Actor, false);
        await Assert.That(fixture.Effects.CopyState(actor.Actor).SelectedInterval).IsEqualTo(9f);
    }

    [Test]
    public async Task DueBatchIsRemovedBeforeCallbacksAndReentrantTimersWaitForTheNextTick()
    {
        using var fixture = new Fixture(); using var actor = fixture.Admit();
        fixture.Lua.Execute("entity.reschedule=true;entity:SetTimer(1,10);entity:SetTimer(3,10)").Dispose();
        fixture.Now = 9_999;
        fixture.Effects.Tick(TimeSpan.Zero, scriptsEnabled: false);
        using (var values = fixture.Lua.Execute("return #entity.events")) await Assert.That(values[0]).IsEqualTo(0L);
        fixture.Now = 10_000;
        fixture.Effects.Tick(TimeSpan.Zero, scriptsEnabled: false);
        using (var values = fixture.Lua.Execute("return #entity.events,entity.events[1].id,entity.events[1].ms,entity.events[2].id"))
        {
            await Assert.That(values[0]).IsEqualTo(2L);
            await Assert.That(values[1]).IsEqualTo(1L);
            await Assert.That(values[2]).IsEqualTo(10L);
            await Assert.That(values[3]).IsEqualTo(3L);
        }
        await Assert.That(fixture.Effects.TimerCount).IsEqualTo(1);
        fixture.Effects.Tick(TimeSpan.Zero, scriptsEnabled: false);
        using (var values = fixture.Lua.Execute("return #entity.events,entity.events[3].id"))
        {
            await Assert.That(values[0]).IsEqualTo(3L);
            await Assert.That(values[1]).IsEqualTo(2L);
        }
    }

    [Test]
    public async Task ReplacementNegativeTimersAndNativeFloatArgumentsKeepTheirOriginalMeaning()
    {
        using var fixture = new Fixture(); using var actor = fixture.Admit();
        fixture.Lua.Execute("entity:SetTimer(8,1);entity:SetTimer(8,2);entity:SetTimer(9,1);entity:KillTimer(-1);entity:SetTimer(-7,-1)").Dispose();
        await Assert.That(fixture.Effects.TimerCount).IsEqualTo(1);
        fixture.Effects.Tick(TimeSpan.Zero, scriptsEnabled: false);
        using (var values = fixture.Lua.Execute("return #entity.events,entity.events[1].id,entity.events[1].ms"))
        {
            await Assert.That(values[0]).IsEqualTo(1L);
            await Assert.That(values[1]).IsEqualTo(-7L);
            await Assert.That(values[2]).IsEqualTo(-1L);
        }
        fixture.Effects.SetEntityTimer(actor.Actor, 16_777_217, 16_777_217);
        fixture.Now = 16_777_217_000;
        fixture.Effects.Tick(TimeSpan.Zero, scriptsEnabled: false);
        using (var values = fixture.Lua.Execute("return entity.events[2].id,entity.events[2].ms"))
        {
            await Assert.That(values[0]).IsEqualTo(16_777_216L);
            await Assert.That(values[1]).IsEqualTo(16_777_216L);
        }
    }

    [Test]
    public async Task RetirementAndEntityIdReuseNeverDispatchAnOldGenerationTimer()
    {
        using var fixture = new Fixture(); using var old = fixture.Admit();
        fixture.Effects.SetEntityTimer(old.Actor, 7, 0);
        var entityId = old.Actor.AiObject.EntityId;
        fixture.Scene.Remove(old.Actor.Handle);
        using var replacement = fixture.Admit(entityId);
        fixture.Effects.Tick(TimeSpan.Zero, scriptsEnabled: false);
        await Assert.That(old.Effects.IsDisposed).IsTrue();
        await Assert.That(fixture.Effects.TimerCount).IsEqualTo(0);
        using (var values = fixture.Lua.Execute("return #entity.events")) await Assert.That(values[0]).IsEqualTo(0L);
        await Assert.That(() => fixture.Effects.SetEntityTimer(old.Actor, 3, 0)).Throws<InvalidOperationException>();
        replacement.Effects.Dispose();
        await Assert.That(() => fixture.Lua.Execute("entity:SetTimer(3,0)")).Throws<ZoneLuaException>();
        await Assert.That(replacement.Actor.HasActor).IsTrue();
    }

    [Test]
    public async Task CallbackFailureFaultsOnlyItsOwnerAndNonthrowingSystemErrorIsAttributed()
    {
        using var fixture = new Fixture(); using var actor = fixture.Admit();
        fixture.Lua.Execute("entity.failTimer=true;entity:SetTimer(1,0);entity:SetTimer(2,0)").Dispose();
        var faults = fixture.Effects.Tick(TimeSpan.Zero);
        await Assert.That(faults.Count).IsEqualTo(1);
        await Assert.That(faults[0].Actor).IsEqualTo(actor.Actor.Handle);
        await Assert.That(faults[0].Callback).IsEqualTo("Server.OnTimer");
        await Assert.That(faults[0].Message.Contains("authored timer refusal", StringComparison.Ordinal)).IsTrue();
        await Assert.That(actor.Effects.IsFaulted).IsTrue();
        await Assert.That(actor.Effects.IsCurrent).IsFalse();
        await Assert.That(fixture.Host.RecentErrors.Single().Actor).IsEqualTo((ZoneActorHandle?)actor.Actor.Handle);
        using var other = fixture.Admit();
        fixture.Effects.SetEntityTimer(other.Actor, 4, 0);
        await Assert.That(fixture.Effects.Tick(TimeSpan.Zero, scriptsEnabled: false).Count).IsEqualTo(0);
        using var values = fixture.Lua.Execute("return #entity.events,entity.events[1].id");
        await Assert.That(values[0]).IsEqualTo(1L);
        await Assert.That(values[1]).IsEqualTo(4L);
    }

    internal sealed class Fixture : IDisposable
    {
        private static int _nextUnit = 900_000, _nextEntity = 12_000_000;
        private ulong _proxy = 0xC000;
        public long Now;
        public ManagedNpcAdmissionTests.Fixture Admission { get; } = new();
        public ZoneManagedLuaRuntime Lua { get; } = new();
        public ZoneManagedNpcActorScene Scene { get; }
        public ZoneManagedNpcScriptHost Host { get; }
        public ZoneManagedNpcScriptEffects Effects { get; }
        public Fixture(Func<Fixture, IZoneManagedNpcScriptServices>? configureServices = null)
        {
            var loader = new ZoneScriptLoader(Lua, new Source(), _ => { }, () => 0);
            Scene = new(Admission.Registry, Admission.Catalog, Lua, new(), _ => default,
                () => Now, () => 1, name => { if (!loader.ExecuteFile(name)) throw new ZoneLuaException(name); }, TimeSpan.FromMilliseconds(20));
            Admission.World.AttachAiRuntime(Scene.Actors.AiRuntime);
            Effects = new(Lua, () => Now);
            var content = new ZoneNpcContent([new() { Id = 11, AiFileId = 15 }], [], [], [], [], aiFiles: [new(15, "hold_position", "")]);
            Host = new(Lua, loader, content, Scene, configureServices?.Invoke(this) ?? Effects, () => Now, () => DateTimeOffset.UnixEpoch);
            if (!Host.Bootstrap().Ready) throw new InvalidOperationException(Host.BootstrapReport.Refusal);
        }
        public Admitted Admit(uint entityId = 0, ZoneModelDefinition? model = null, uint? unitModelId = null)
        {
            var npc = Admission.Npc((uint)Interlocked.Increment(ref _nextUnit), model: model, unitModelId: unitModelId); Admission.Publish(npc);
            var handle = Admission.Catalog.Register(npc);
            var actor = Scene.Prepare(handle, new() { EntityId = entityId == 0 ? (uint)Interlocked.Increment(ref _nextEntity) : entityId },
                ZoneManagedNpcActorScene.CreatePipeUserControls(), true);
            using var template = Host.Scripts.RetainGenericActorTemplate();
            var prepared = Host.Scripts.Prepare(11, new(actor.AiObject.EntityId), template, actor);
            var binding = Host.BindEntity(prepared, actor, "effects-fixture");
            Scene.Adopt(actor, 1, ++_proxy, prepared.EntityScript); Scene.RegisterBehavior(actor, prepared.DetachBehavior());
            var effects = Effects.BindEntity(binding, new());
            Lua.SetGlobal("entity", prepared.EntityScript);
            return new(actor, prepared, binding, effects);
        }
        public void Dispose()
        { try { Effects.Dispose(); } finally { try { Host.Dispose(); } finally { try { Scene.Dispose(); } finally { Lua.Dispose(); Admission.Dispose(); } } } }
    }

    internal sealed record Admitted(ZoneManagedNpcActor Actor, ZoneNpcAuthoredEntity Prepared,
        ZoneManagedNpcScriptBinding Binding, ZoneManagedNpcScriptEffectsLease Effects) : IDisposable
    {
        public void Dispose() { Effects.Dispose(); Binding.Dispose(); Actor.Dispose(); Prepared.Dispose(); }
    }

    private sealed class Source : IZoneScriptSource
    {
        public ZoneScriptFile? Read(string filename)
        {
            var source = filename.ToLowerInvariant() switch
            {
                "scripts/common.lua" => "function new(t)if type(t)~='table'then return t end;local r={};for k,v in pairs(t)do r[k]=new(v)end;return r end",
                "scripts/ai/aiconfig.lua" => """
                    AICharacter={AVAILABLE={},INTERNAL={},DEFAULT={}}
                    AICharacter.hold_position={AnyBehavior={GO_TO_SPAWN='spawning'},Constructor=function(self,e)function e.AI:SetParamX2(p)end end}
                    AIBehaviour={AVAILABLE={},INTERNAL={},DEFAULT={},spawning={Name='spawning',Constructor=function(self,e)end}}
                    X2AI={commandSet={CanRunCommandSet=function()return false end,RunCommandSet=function()end,EndCommandSet=function()end}}
                    """,
                "scripts/entities/actor/npc.lua" => """
                    BasicActor={ResetCommon=function(e)e.lastHealth=e.actor:GetMaxHealth();e:SetTimer(5,500);e.AI={commandSet=new(X2AI.commandSet)}end}
                    BasicAI={OnReset=function(e)e:SetScriptUpdateRate(.05,1/3);BasicActor.ResetCommon(e)end}
                    npc={ActorTemplate={Properties={},events={},updates={},Server={
                        OnInit=function(e)BasicAI.OnReset(e)end,
                        OnUpdate=function(e,dt)e.updates[#e.updates+1]=dt end,
                        OnTimer=function(e,id,ms)
                            e.events[#e.events+1]={id=id,ms=ms}
                            if e.failTimer then System.Error('authored timer refusal')end
                            if e.reschedule and id==1 then e.reschedule=false;e:SetTimer(2,0);e:KillTimer(3)end
                        end}}}
                    function npc:Create(name,source)_G[name]=new(self.ActorTemplate)end
                    """,
                _ => null
            };
            return source is null ? null : new(Encoding.UTF8.GetBytes(source), filename);
        }
    }
}
