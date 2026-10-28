using System.Text;
using AAEmu.Zone.Content;
using AAEmu.Zone.Scripting;

namespace AAEmu.Zone.Tests;

/// <summary>
/// Synthetic scripts exercise host orchestration and ownership. Original zone129 source execution
/// is an independent content probe, not replaced by these small fixtures or claimed by this suite.
/// </summary>
public sealed class ZoneNpcAuthoredScriptsTests
{
    [Test]
    public async Task NestedMissingImportCannotClaimBootstrapReadiness()
    {
        using var fixture = new Fixture(missingCharacter: true);
        var result = fixture.Scripts.Bootstrap();
        await Assert.That(result.Ready).IsFalse();
        await Assert.That(result.Refusal!.Contains("character.lua", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fixture.Loader.LoadedScripts.Contains("scripts/ai/aiconfig.lua")).IsTrue();
    }

    [Test]
    public async Task RepairedNestedImportIsRetriedDespiteSuccessfulOuterFileCache()
    {
        using var fixture = new Fixture(missingCharacter: true);
        await Assert.That(fixture.Scripts.Bootstrap().Ready).IsFalse();
        fixture.RestoreCharacter();
        await Assert.That(fixture.Scripts.Bootstrap().Ready).IsTrue();
        await Assert.That(fixture.Loader.FailureCount).IsEqualTo(1L);
    }

    [Test]
    public async Task AdoptionPrecedesOriginalEntityCharacterParameterAndBehaviorCalls()
    {
        using var fixture = new Fixture();
        await Assert.That(fixture.Scripts.Bootstrap().Ready).IsTrue();
        using var source = fixture.Scripts.RetainGenericActorTemplate();
        using var prepared = fixture.Scripts.Prepare(1, new(71), source, fixture.Services);
        await Assert.That(prepared.Readiness.SignalReady).IsFalse();
        await Assert.That(prepared.InitialBehaviorName).IsEqualTo("spawning");
        await Assert.That(fixture.Trace()).IsEqualTo("npc.Create");
        prepared.EntityScript.Set("adopted", true);
        using var behavior = prepared.DetachBehavior();
        var ready = fixture.Scripts.Initialize(prepared, () => true);
        await Assert.That(ready.SignalReady).IsTrue();
        await Assert.That(ready.GameplayReady).IsFalse();
        await Assert.That(fixture.Trace()).IsEqualTo("npc.Create,Server.OnInit,AI.OnReset,Actor.ResetCommon,character,parameters,spawning");
        using var values = fixture.Lua.Execute("return observedEntity.AI.param.delay,observedEntity.id==observedId");
        await Assert.That(values[0]).IsEqualTo((object)1.5d);
        await Assert.That(values[1] is true).IsTrue();
        await Assert.That(() => fixture.Scripts.Initialize(prepared, () => true)).Throws<InvalidOperationException>();
        await Assert.That(() => prepared.DetachBehavior()).Throws<InvalidOperationException>();
        behavior.Dispatch(new(0, "GO_TO_IDLE", 0, 0, null));
        await Assert.That(behavior.CurrentName).IsEqualTo("idle");
    }

    [Test]
    public async Task CachedNpcClassIsClonedWithoutRepeatingNpcCreate()
    {
        using var fixture = new Fixture(); fixture.Scripts.Bootstrap();
        using var sourceClass = fixture.Scripts.RetainGenericActorTemplate();
        using var prepared = fixture.Scripts.PrepareFromClass(1, new(74), sourceClass, fixture.Services);
        await Assert.That(fixture.Trace()).IsEqualTo("");
        prepared.EntityScript.Set("adopted", true);
        await Assert.That(fixture.Scripts.Initialize(prepared, () => true).SignalReady).IsTrue();
        await Assert.That(fixture.Trace()).IsEqualTo("Server.OnInit,AI.OnReset,Actor.ResetCommon,character,parameters,spawning");
    }

    [Test]
    public async Task MissingActorBindingsAndCallbackErrorsRemainNamedRefusals()
    {
        using var fixture = new Fixture();
        fixture.Scripts.Bootstrap();
        using var source = fixture.Scripts.RetainGenericActorTemplate();
        using (var unadopted = fixture.Scripts.Prepare(1, new(72), source, fixture.Services))
        {
            var result = fixture.Scripts.Initialize(unadopted, () => true);
            await Assert.That(result.EntityInitialized).IsFalse();
            await Assert.That(result.SignalReady).IsFalse();
            await Assert.That(result.Refusal!.Contains("Server.OnInit", StringComparison.Ordinal)).IsTrue();
        }
        using var prepared = fixture.Scripts.Prepare(1, new(73), source, fixture.Services);
        prepared.EntityScript.Set("adopted", true);
        prepared.EntityScript.Set("refuseCharacter", true);
        var refusal = fixture.Scripts.Initialize(prepared, () => true);
        await Assert.That(refusal.EntityInitialized).IsTrue();
        await Assert.That(refusal.CharacterConstructed).IsFalse();
        await Assert.That(refusal.SignalReady).IsFalse();
        await Assert.That(refusal.Refusal!.Contains("character refused", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fixture.Services.Errors.Count).IsEqualTo(1);
    }

    [Test]
    public async Task EntityStateAndReferencesBelongToOneOwnerAndBehaviorTransferIsExplicit()
    {
        using var fixture = new Fixture();
        fixture.Scripts.Bootstrap();
        using var source = fixture.Scripts.RetainGenericActorTemplate();
        var baseline = fixture.Lua.RetainedReferenceCount;
        var first = fixture.Scripts.Prepare(1, new(81), source, fixture.Services);
        var second = fixture.Scripts.Prepare(1, new(82), source, fixture.Services);
        first.EntityScript.Set("adopted", true);
        second.EntityScript.Set("adopted", true);
        fixture.Scripts.Initialize(first, () => true);
        fixture.Scripts.Initialize(second, () => true);
        using (var a = (ZoneLuaReference)first.EntityScript.Get("AI")!)
        using (var b = (ZoneLuaReference)second.EntityScript.Get("AI")!)
        {
            a.Set("ownerOnly", 42);
            await Assert.That(b.Get("ownerOnly")).IsNull();
        }
        var transferred = first.DetachBehavior();
        first.Dispose();
        await Assert.That(transferred.IsDisposed).IsFalse();
        await Assert.That(first.Readiness.SignalReady).IsFalse();
        transferred.Dispose();
        second.Dispose();
        await Assert.That(fixture.Lua.RetainedReferenceCount).IsEqualTo(baseline);
    }

    [Test]
    public async Task RetiredGenerationStopsBeforeCallbacksAndMissingDescriptorUsesAuthoredDefault()
    {
        using var fixture = new Fixture(missingAiDescriptor: true);
        fixture.Scripts.Bootstrap();
        using var source = fixture.Scripts.RetainGenericActorTemplate();
        using var prepared = fixture.Scripts.Prepare(1, new(91), source, fixture.Services);
        await Assert.That(prepared.CharacterName).IsEqualTo("default");
        var result = fixture.Scripts.Initialize(prepared, () => false);
        await Assert.That(result.EntityInitialized).IsFalse();
        await Assert.That(result.SignalReady).IsFalse();
        await Assert.That(result.Refusal!.Contains("retired", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fixture.Trace()).IsEqualTo("npc.Create");
    }

    [Test]
    public async Task RetirementInsideConstructorCannotRunRemainingInitialization()
    {
        using var fixture = new Fixture();
        fixture.Scripts.Bootstrap();
        using var source = fixture.Scripts.RetainGenericActorTemplate();
        using var prepared = fixture.Scripts.Prepare(1, new(92), source, fixture.Services);
        var current = true;
        using var retire = fixture.Lua.CreateFunction(_ => { current = false; prepared.Dispose(); return []; });
        prepared.EntityScript.Set("adopted", true);
        prepared.EntityScript.Set("Retire", retire);
        prepared.EntityScript.Set("retireDuringCharacter", true);
        var result = fixture.Scripts.Initialize(prepared, () => current);
        await Assert.That(result.SignalReady).IsFalse();
        await Assert.That(result.Refusal!.Contains("retired", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fixture.Trace()).IsEqualTo("npc.Create,Server.OnInit,AI.OnReset,Actor.ResetCommon,character");
    }

    [Test]
    public async Task NonthrowingAuthoredErrorChannelCanRefuseParameterReadiness()
    {
        using var fixture = new Fixture();
        fixture.Scripts.Bootstrap();
        using var source = fixture.Scripts.RetainGenericActorTemplate();
        using var prepared = fixture.Scripts.Prepare(1, new(93), source, fixture.Services);
        string? reported = null;
        using var errorChannel = fixture.Lua.CreateFunction(_ => { reported = "Parameter source was rejected by its authored setter."; return []; });
        prepared.EntityScript.Set("adopted", true);
        prepared.EntityScript.Set("rejectParameters", true);
        prepared.EntityScript.Set("ReportParameterError", errorChannel);
        var result = fixture.Scripts.Initialize(prepared, () => true, () => reported);
        await Assert.That(result.CharacterConstructed).IsTrue();
        await Assert.That(result.ParametersApplied).IsFalse();
        await Assert.That(result.BehaviorConstructed).IsFalse();
        await Assert.That(result.SignalReady).IsFalse();
        await Assert.That(result.Refusal!.Contains("SetParamX2", StringComparison.Ordinal)).IsTrue();
        await Assert.That(result.Refusal.Contains("Parameter source was rejected", StringComparison.Ordinal)).IsTrue();
    }

    private sealed class Fixture : IDisposable
    {
        public ZoneManagedLuaRuntime Lua { get; } = new();
        public ZoneScriptLoader Loader { get; }
        public ZoneNpcAuthoredScripts Scripts { get; }
        public Services Services { get; } = new();
        public Action RestoreCharacter { get; }
        public Fixture(bool missingCharacter = false, bool missingAiDescriptor = false)
        {
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["scripts/common.lua"] = """
                    trace={}
                    function mark(s) trace[#trace+1]=s end
                    function new(t)
                        if type(t)~='table' then return t end
                        local r={};for k,v in pairs(t) do r[k]=new(v) end;return r
                    end
                    """,
                ["scripts/ai/aiconfig.lua"] = """
                    AICharacter={AVAILABLE={test='character.lua'},INTERNAL={},DEFAULT={}}
                    AIBehaviour={AVAILABLE={},INTERNAL={},DEFAULT={}}
                    Script.ReloadScript('character.lua')
                    AIBehaviour.spawning={Name='spawning',Constructor=function(self,e) mark('spawning') end}
                    AIBehaviour.idle={Name='idle'}
                    X2AI={commandSet={CanRunCommandSet=function()return false end,
                        RunCommandSet=function()end,EndCommandSet=function()end}}
                    """,
                ["character.lua"] = """
                    AICharacter.test={AnyBehavior={GO_TO_SPAWN='spawning',GO_TO_IDLE='idle'},
                        Constructor=function(self,e)
                            if e.refuseCharacter then error('character refused') end
                            mark('character')
                            if e.retireDuringCharacter then e:Retire();return end
                            function e.AI:SetParamX2(text)
                                if e.rejectParameters then e:ReportParameterError();return end
                                mark('parameters');self.param=assert(loadstring('return {'..text..'}'))()
                            end
                        end}
                    AICharacter.default=AICharacter.test
                    """,
                ["Scripts/Entities/Actor/npc.lua"] = """
                    BasicActor={ResetCommon=function(e)
                        mark('Actor.ResetCommon');e.AI={commandSet=new(X2AI.commandSet)}
                    end}
                    BasicAI={OnReset=function(e)mark('AI.OnReset');BasicActor.ResetCommon(e)end}
                    npc={ActorTemplate={Properties={},PropertiesInstance={},Server={OnInit=function(e)
                        if not e.adopted then error('actor not adopted') end
                        mark('Server.OnInit');BasicAI.OnReset(e)
                        observedEntity=e;observedId=e.id
                    end}}}
                    function npc:Create(name,source)
                        mark('npc.Create');_G[name]=new(self.ActorTemplate)
                    end
                    """
            };
            var characterText = files["character.lua"];
            RestoreCharacter = () => files["character.lua"] = characterText;
            if (missingCharacter) files.Remove("character.lua");
            Loader = new(Lua, new Source(files), _ => { }, () => 0);
            Loader.InstallBindings();
            var content = new ZoneNpcContent([new() { Id = 1, AiFileId = 15, NpcAiParamId = 17 }], [], [], [], [],
                aiFiles: missingAiDescriptor ? [] : [new(15, "test", "")], aiParameters: [new(17, "delay=1.5")]);
            Scripts = new(Lua, Loader, content);
        }
        public string Trace()
        { using var result = Lua.Execute("return table.concat(trace,',')"); return (string)result[0]!; }
        public void Dispose() { Scripts.Dispose(); Lua.Dispose(); }
    }

    private sealed class Source(Dictionary<string, string> files) : IZoneScriptSource
    {
        public ZoneScriptFile? Read(string filename) => files.TryGetValue(filename, out var text)
            ? new(Encoding.UTF8.GetBytes(text), filename) : null;
    }

    private sealed class Services : IZoneNpcBehaviorServices
    {
        public List<string> Errors { get; } = [];
        public bool HasActor => true;
        public void LoadScript(string filename) => throw new InvalidOperationException($"Unexpected lazy import {filename}");
        public void ActorEvent(int kind, string name) { }
        public void RecordEvent(int kind, string name) { }
        public void ResetPipeUserAfterBehaviorExit() { }
        public void ChangeSmartObjectState(string change) { }
        public void NotifyAlert() { }
        public void NotifyActionDone() { }
        public void ScriptError(string callback, ZoneLuaException error) => Errors.Add(error.Message);
    }
}
