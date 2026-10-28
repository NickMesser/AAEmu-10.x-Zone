using System.Numerics;
using System.Text;
using AAEmu.Zone.Content;
using AAEmu.Zone.Hosting;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Scripting;

namespace AAEmu.Zone.Tests;

/// <summary>Synthetic authored callbacks verify the complete provider factory and its rollback.</summary>
public sealed class ManagedNpcRuntimeTests
{
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
        var otherId = ManagedNpcAdmissionTests.Fixture.FreshObjectId();
        var other = fixture.Admission.Npc(otherId, otherId + 1_000_000, fixture.Model);
        fixture.Admission.Publish(other); fixture.Admission.World.Initialize(other, null);
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

    private sealed class Fixture : IDisposable
    {
        public ManagedNpcAdmissionTests.Fixture Admission { get; }
        public ZoneManagedNpcProfileFactory Profiles { get; }
        public ZoneManagedNpcRuntime Runtime { get; }
        public ZoneSceneScriptClock Clock { get; } = new();
        public ZoneModelDefinition Model { get; } = new() { Id = 42, Name = "runtime_fixture", Subtype = "ActorModel", SubtypeId = 9 };
        public Fixture(bool withEffects = false, bool admitProfile = true)
        {
            ZoneManagedNpcRuntime? runtime = null;
            Admission = new(handle => runtime!.CreateActor(handle));
            Profiles = new(Admission.Registry, Admission.Catalog, _ => Vector3.Zero);
            var models = new ZoneModelContent([Model], actorModels: new ZoneActorModelContent([new() { Id = 9 }],
                Enumerable.Range(1, 9).Select(id => new ZoneGameStanceDefinition { Id = (uint)id, ActorModelId = 9, StanceId = id })));
            var content = new ZoneNpcContent([new() { Id = 11, NpcAiParamId = 8 }], [], [], [], [],
                aiParameters: [new(8, "delay=1.5")]);
            Runtime = runtime = new(Admission.Registry, Admission.Catalog, models, content, new Source(),
                _ => Vector3.Zero, () => Clock.GameMicroseconds, () => 1, () => DateTimeOffset.UnixEpoch,
                TimeSpan.FromMilliseconds(20), createProfile: admitProfile ? Profiles.Create : null,
                createScriptServices: withEffects ? lua => new ZoneManagedNpcScriptEffects(lua, () => Clock.GameMicroseconds) : null);
            Admission.World.AttachAiRuntime(Runtime.Actors.Actors.AiRuntime);
            if (!Runtime.Bootstrap().Ready) throw new InvalidOperationException(Runtime.Scripts.BootstrapReport.Refusal);
        }
        public void Dispose()
        { try { Admission.Dispose(); } finally { try { Runtime.Dispose(); } finally { Profiles.Dispose(); } } }
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
                    end}
                    AIBehaviour={AVAILABLE={},INTERNAL={},DEFAULT={},spawning={Name='spawning'},idle={Name='idle'}}
                    X2AI={commandSet={CanRunCommandSet=function()return false end,RunCommandSet=function()end,EndCommandSet=function()end}}
                    """,
                "scripts/entities/actor/npc.lua" => """
                    BasicActor={ResetCommon=function(e)e.AI={commandSet=new(X2AI.commandSet)}end}
                    BasicAI={OnReset=function(e)BasicActor.ResetCommon(e)end}
                    npc={ActorTemplate={Properties={},Server={OnTimer=function(e,id,ms)timerCalls=(timerCalls or 0)+1 end,OnUpdate=function(e)
                      if failUpdate and e.failUpdate then System.Error('controlled update refusal')end
                    end,OnInit=function(e)
                      observedEntity=e;if failInitialization then System.Error('controlled init refusal');return end;BasicAI.OnReset(e)
                      if testTimer then e:SetTimer(1,20)end
                    end}}}
                    function npc:Create(name,source)_G[name]=new(self.ActorTemplate)end
                    """,
                _ => null,
            };
            return source is null ? null : new(Encoding.UTF8.GetBytes(source), filename);
        }
    }
}
