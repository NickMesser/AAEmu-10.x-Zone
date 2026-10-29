using System.Globalization;
using System.Text.Json;
using AAEmu.Zone.Ai;
using AAEmu.Zone.Content;
using AAEmu.Zone.Npcs;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Scripting;

namespace AAEmu.Zone.Tests;

public sealed class ManagedNpcAutoDisableTests
{
    [Test]
    public async Task OriginalMachineControlsAgreeForProxyWordsLuaBindingsAndTheAdmittedGameObjectConsumer()
    {
        using var stream = typeof(ManagedNpcAutoDisableTests).Assembly.GetManifestResourceStream(
            "AAEmu.Zone.Tests.Fixtures.npc-auto-disable-native-controls.json")!;
        using var json = JsonDocument.Parse(stream);
        var checkedControls = 0;
        foreach (var control in json.RootElement.GetProperty("wordControls").EnumerateArray())
        {
            var activation = new ZoneActorActivationState(0);
            SeedWord(activation, control.GetProperty("before").GetInt16());
            List<int> policies = [];
            var global = control.GetProperty("globalOverride").GetBoolean();
            if (control.GetProperty("operation").GetString() == "set")
                activation.SetUpdateAlways(control.GetProperty("requested").GetInt16(), () => global, policies.Add);
            else activation.UpdateEntityPolicy(() => global, policies.Add);
            await Assert.That(activation.UpdateAlwaysRemaining).IsEqualTo(control.GetProperty("after").GetInt16());
            await Assert.That(policies.SequenceEqual(control.GetProperty("policyCalls").EnumerateArray().Select(x => x.GetInt32()))).IsTrue();
            checkedControls++;
        }
        using var fixture = new Fixture();
        using var ai = (ZoneLuaReference)fixture.Lua.GetGlobal("AI")!;
        foreach (var control in json.RootElement.GetProperty("bindingControls").EnumerateArray())
        {
            using var owned = fixture.Admit();
            var actor = owned.Actor;
            var method = control.GetProperty("method").GetString()!;
            var hasStatus = control.TryGetProperty("status", out var status);
            SeedWord(actor.Entry.Activation, control.TryGetProperty("before", out var before)
                ? before.GetInt16() : hasStatus ? short.MinValue : method == "GetAutoDisable" ? (short)0 : (short)1024);
            actor.EntityUpdatePolicy = hasStatus ? 1 : 2;
            actor.SetEntityActive(false);
            List<object?> arguments = [new ZoneLuaHandle(actor.AiObject.EntityId)];
            if (method == "AutoDisable") arguments.Add(hasStatus ? NativeStatus(status.GetString()!) : 0L);
            if (control.TryGetProperty("profile", out var profile))
            {
                switch (profile.GetString())
                {
                    case "wideHandle": arguments[0] = new ZoneLuaHandle((1UL << 32) | actor.AiObject.EntityId); break;
                    case "numericID": arguments[0] = (long)actor.AiObject.EntityId; break;
                    //No owned binding is the managed observation of any missing entity/AI/proxy stage.
                    case "noEntity": case "noAI": case "noProxy": arguments[0] = new ZoneLuaHandle(0); break;
                    case "wrongArgCount0": arguments.Clear(); break;
                    case "wrongArgCount1": arguments.RemoveAt(1); break;
                    case "wrongArgCount2": case "wrongArgCount3": arguments.Add(0L); break;
                    case "wrongStatusType": arguments[1] = false; break;
                    default: throw new InvalidDataException("Unknown native binding control profile.");
                }
            }
            using var function = (ZoneLuaReference)ai.Get(method)!;
            using var result = fixture.Lua.Call(function, arguments.ToArray());
            var expected = control.GetProperty("returns");
            await Assert.That(result.Count).IsEqualTo(expected.GetArrayLength());
            if (result.Count == 1) await Assert.That(result[0] is true).IsEqualTo(expected[0].GetBoolean());
            await Assert.That(actor.Entry.Activation.UpdateAlwaysRemaining).IsEqualTo(control.GetProperty("after").GetInt16());
            await Assert.That(actor.EntityUpdatePolicy).IsEqualTo(control.GetProperty("policy").GetInt32());
            await Assert.That(actor.EntityActive).IsEqualTo(control.GetProperty("entityActive").GetBoolean());
            checkedControls++;
        }
        foreach (var control in json.RootElement.GetProperty("policyControls").EnumerateArray())
        {
            using var owned = fixture.Admit();
            owned.Actor.EntityUpdatePolicy = control.GetProperty("priorPolicy").GetInt32();
            owned.Actor.SetEntityActive(control.GetProperty("initiallyActive").GetBoolean());
            fixture.Scene.SetEntityUpdatePolicy(owned.Actor.Entry, control.GetProperty("requestedPolicy").GetInt32());
            await Assert.That(owned.Actor.EntityActive).IsEqualTo(control.GetProperty("entityActive").GetBoolean());
            await Assert.That(owned.Actor.EntityUpdatePolicy).IsEqualTo(control.GetProperty("policy").GetInt32());
            checkedControls++;
        }
        await Assert.That(checkedControls).IsEqualTo(133);
        await Assert.That(json.RootElement.GetProperty("disagreements").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task OriginalBindingsUseEngineHandlesExactArityAndNoActorAlias()
    {
        using var fixture = new Fixture(); using var owned = fixture.Admit();
        var actor = owned.Actor;
        fixture.Lua.SetGlobal("widened", new ZoneLuaHandle((1UL << 32) | actor.AiObject.EntityId));
        using (var values = fixture.Lua.Execute("""
            return AI.GetAutoDisable(entity.id), AI.GetAutoDisable(widened),
                   AI.GetAutoDisable(entity.unit.id), AI.GetAutoDisable(12),
                   select('#',AI.GetAutoDisable()),select('#',AI.GetAutoDisable(entity.id,1)),
                   select('#',AI.AutoDisable(entity.id)),select('#',AI.AutoDisable(entity.id,0,1))
            """))
        {
            for (var index = 0; index < 4; index++) await Assert.That(values[index] is false).IsTrue();
            for (var index = 4; index < 8; index++) await Assert.That(values[index]).IsEqualTo(0L);
        }
        await Assert.That(actor.Entry.Activation.UpdateAlwaysRemaining).IsEqualTo((short)1024);
        using var aliases = fixture.Lua.Execute("return rawget(entity.actor,'AutoDisable')==nil,rawget(entity.actor,'SetAutoDisable')==nil");
        await Assert.That(aliases[0] is true).IsTrue();
        await Assert.That(aliases[1] is true).IsTrue();
    }

    [Test]
    public async Task EnablingCannotCancelStartupGraceAndDisablingUsesThePersistentSignedWord()
    {
        using var fixture = new Fixture(); using var owned = fixture.Admit();
        var actor = owned.Actor;
        Run(fixture, "AI.AutoDisable(entity.id,1)");
        await Assert.That(actor.Entry.Activation.UpdateAlwaysRemaining).IsEqualTo((short)1024);
        await Assert.That(ZoneNpcAutoDisable.Get(actor)).IsFalse();
        Run(fixture, "AI.AutoDisable(entity.id,0)");
        await Assert.That(actor.Entry.Activation.UpdateAlwaysRemaining).IsEqualTo(short.MinValue);
        await Assert.That(actor.EntityActive).IsTrue();
        for (var index = 0; index < 10; index++) actor.UpdateProxyActivationPolicy();
        await Assert.That(actor.Entry.Activation.UpdateAlwaysRemaining).IsEqualTo(short.MinValue);
        Run(fixture, "AI.AutoDisable(entity.id,1)");
        await Assert.That(actor.Entry.Activation.UpdateAlwaysRemaining).IsEqualTo((short)0);
        await Assert.That(ZoneNpcAutoDisable.Get(actor)).IsTrue();
        await Assert.That(actor.EntityUpdatePolicy).IsEqualTo(2);
    }

    [Test]
    [Arguments(0d, short.MinValue)]
    [Arguments(.999d, short.MinValue)]
    [Arguments(-.999d, short.MinValue)]
    [Arguments(1d, (short)0)]
    [Arguments(-1d, (short)0)]
    [Arguments(2147483648d, (short)0)]
    [Arguments(double.NaN, (short)0)]
    [Arguments(double.PositiveInfinity, (short)0)]
    public async Task NumericStatusCrossesFloatThenTheOriginalIntegerConversion(double status, short expected)
    {
        using var fixture = new Fixture(); using var owned = fixture.Admit();
        ZoneNpcAutoDisable.Set(owned.Actor, 0); //Remove the positive-countdown guard from this numeric control.
        fixture.Lua.SetGlobal("status", status);
        using var values = fixture.Lua.Execute("return select('#',AI.AutoDisable(entity.id,status))");
        await Assert.That(values[0]).IsEqualTo(0L);
        await Assert.That(owned.Actor.Entry.Activation.UpdateAlwaysRemaining).IsEqualTo(expected);
    }

    [Test]
    public async Task WrongNumericTypesDoNotChangeTheProxyAndHandleNarrowingStillResolvesIt()
    {
        using var fixture = new Fixture(); using var owned = fixture.Admit();
        fixture.Lua.SetGlobal("widened", new ZoneLuaHandle((1UL << 32) | owned.Actor.AiObject.EntityId));
        Run(fixture, "AI.AutoDisable(entity.id,false);AI.AutoDisable(entity.id,'0');AI.AutoDisable(entity.id,{})");
        await Assert.That(owned.Actor.Entry.Activation.UpdateAlwaysRemaining).IsEqualTo((short)1024);
        Run(fixture, "AI.AutoDisable(widened,0)");
        await Assert.That(owned.Actor.Entry.Activation.UpdateAlwaysRemaining).IsEqualTo(short.MinValue);
    }

    [Test]
    [Arguments(false, true, (short)1024)]
    [Arguments(true, false, short.MinValue)]
    public async Task RealTemplateProducerRunsAfterOnInitBeforeCharacterAndOverwritesTheInstanceProperty(
        bool activateAlways, bool property, short expected)
    {
        using var fixture = new Fixture(); using var owned = fixture.Admit(activateAlways: activateAlways);
        Run(fixture, """
            local previous=entity.Server.OnInit
            entity.Server.OnInit=function(e)
              e.initialAutoDisable=e.PropertiesInstance.bAutoDisable
              previous(e)
              e.PropertiesInstance.bAutoDisable='OnInit mutation'
            end
            AICharacter.hold_position.Constructor=function(self,e)
              e.characterAutoDisable=e.PropertiesInstance.bAutoDisable
              function e.AI:SetParamX2(p)end
            end
            """);
        var readiness = fixture.Host.Scripts.Initialize(owned.Prepared, () => owned.Actor.HasActor,
            afterEntityInitialization: () => ZoneNpcAutoDisable.ApplyInitialParameters(owned.Binding));
        if (!readiness.SignalReady) throw new InvalidOperationException(readiness.Refusal);
        using var values = fixture.Lua.Execute("return entity.initialAutoDisable,entity.PropertiesInstance.bAutoDisable,entity.characterAutoDisable");
        await Assert.That(values[0]).IsEqualTo(1L); //The inherited original script value is numeric during OnInit.
        await Assert.That(values[1]).IsEqualTo(property);
        await Assert.That(values[2]).IsEqualTo(property);
        await Assert.That(owned.Actor.Entry.Activation.UpdateAlwaysRemaining).IsEqualTo(expected);
        ZoneNpcAutoDisable.ApplyInitialParameters(owned.Binding); //Same registration does not reset a proxy.
        await Assert.That(owned.Actor.Entry.Activation.UpdateAlwaysRemaining).IsEqualTo(expected);
    }

    [Test]
    public async Task MissingOriginalInstanceTableRefusesRegistrationInsteadOfInventingAPropertyOwner()
    {
        using var fixture = new Fixture(); using var owned = fixture.Admit();
        Run(fixture, "entity.PropertiesInstance=nil");
        await Assert.That(() => ZoneNpcAutoDisable.ApplyInitialParameters(owned.Binding)).Throws<NotSupportedException>();
        await Assert.That(owned.Actor.Entry.Activation.UpdateAlwaysRemaining).IsEqualTo((short)1024);
    }

    [Test]
    public async Task RegistrationUsesTheOriginalTypedNumericBooleanReadAfterAnAuthoredPropertyWriteInterceptor()
    {
        using var fixture = new Fixture(); using var owned = fixture.Admit();
        Run(fixture, "entity.PropertiesInstance=setmetatable({},{__newindex=function(t,k,v)rawset(t,k,1)end})");
        ZoneNpcAutoDisable.ApplyInitialParameters(owned.Binding);
        using var result = fixture.Lua.Execute("return type(entity.PropertiesInstance.bAutoDisable),entity.PropertiesInstance.bAutoDisable");
        await Assert.That(result[0]).IsEqualTo("number");
        await Assert.That(result[1]).IsEqualTo(1L);
        await Assert.That(owned.Actor.Entry.Activation.UpdateAlwaysRemaining).IsEqualTo((short)1024);
        await Assert.That(owned.Actor.EntityUpdatePolicy).IsEqualTo(2);
    }

    [Test]
    public async Task ConcretePolicyChangesActivateButUnchangedRequestsPreserveActualEntityActivity()
    {
        using var fixture = new Fixture(); using var owned = fixture.Admit();
        var actor = owned.Actor;
        await Assert.That(actor.EntityActive).IsFalse();
        actor.UpdateProxyActivationPolicy();
        await Assert.That(actor.Entry.Activation.UpdateAlwaysRemaining).IsEqualTo((short)1023);
        await Assert.That(actor.EntityActive).IsTrue();
        await Assert.That(actor.EntityUpdatePolicy).IsEqualTo(1);
        actor.SetEntityActive(false);
        await Assert.That(actor.UpdateProxyActivationPolicy()).IsFalse();
        await Assert.That(actor.Entry.Activation.UpdateAlwaysRemaining).IsEqualTo((short)1022);
        actor.Entry.SetUpdateAlways(1); //Accepted positive value becomes0 and changes policy to2.
        await Assert.That(actor.EntityActive).IsTrue();
        await Assert.That(ZoneNpcAutoDisable.Get(actor)).IsTrue();
        fixture.Scene.GlobalAiUpdateAlways = true;
        actor.UpdateProxyActivationPolicy();
        await Assert.That(actor.EntityUpdatePolicy).IsEqualTo(1);
        await Assert.That(ZoneNpcAutoDisable.Get(actor)).IsTrue(); //Getter reads the proxy word, not the global override.
    }

    [Test]
    public async Task UnknownConditionalVisibilityAndHiddenExtensionProfilesRemainExplicitRefusals()
    {
        using var fixture = new Fixture(); using var owned = fixture.Admit();
        var actor = owned.Actor;
        fixture.Scene.GameObjectActivationProfile = ZoneManagedNpcGameObjectActivationProfile.Unowned;
        actor.UpdateProxyActivationPolicy(); //Nonhidden unconditional policy1 needs no visibility profile.
        await Assert.That(() => actor.Entry.SetUpdateAlways(1)).Throws<NotSupportedException>();
        await Assert.That(actor.EntityUpdatePolicy).IsEqualTo(1);
        await Assert.That(actor.Entry.Activation.UpdateAlwaysRemaining).IsEqualTo((short)0);
        fixture.Scene.GameObjectActivationProfile = ZoneManagedNpcGameObjectActivationProfile.MultiplayerWorldServer;
        actor.UpdateProxyActivationPolicy();
        actor.SetEntityHidden(true);
        await Assert.That(() => ZoneNpcAutoDisable.Set(actor, 0)).Throws<NotSupportedException>();
        await Assert.That(actor.EntityUpdatePolicy).IsEqualTo(2);
        await Assert.That(actor.Entry.Activation.UpdateAlwaysRemaining).IsEqualTo(short.MinValue);
    }

    [Test]
    public async Task ScheduledProxyPhaseDecrementsExactlyOnceAndGatesTheActualSignalDrain()
    {
        using var fixture = new Fixture(); using var owned = fixture.Admit();
        var actor = owned.Actor;
        Run(fixture, "AIBehaviour.signal_control={Name='signal_control',OnOwned=function(self,e)e.received=(e.received or 0)+1 end}");
        actor.Behavior!.SetBehavior("signal_control", mode: ZoneBehaviorConstructorMode.Restore);
        actor.Npc.AiSignals.Send("OnOwned");
        actor.Update(1000, .02f);
        await Assert.That(actor.Entry.Activation.UpdateAlwaysRemaining).IsEqualTo((short)1023);
        await Assert.That(owned.Prepared.EntityScript.Get("received")).IsEqualTo(1L);
        actor.SetEntityActive(false);
        actor.Npc.AiSignals.Send("OnOwned");
        actor.Update(1020, .02f);
        await Assert.That(actor.Entry.Activation.UpdateAlwaysRemaining).IsEqualTo((short)1022);
        await Assert.That(owned.Prepared.EntityScript.Get("received")).IsEqualTo(1L);
        await Assert.That(actor.Npc.AiSignals.Pending.Count).IsEqualTo(1);
        actor.SetEntityActive(true);
        actor.Update(1040, .02f);
        await Assert.That(owned.Prepared.EntityScript.Get("received")).IsEqualTo(2L);
    }

    [Test]
    public async Task ExistingWorldActivationConsumesTheSameProxyStateOutsidePlayerSight()
    {
        using var fixture = new ManagedNpcRuntimeTests.Fixture(withMovement: true);
        fixture.Runtime.Actors.GameObjectActivationProfile = ZoneManagedNpcGameObjectActivationProfile.MultiplayerWorldServer;
        var npc = fixture.Admission.CreateFromWire(fixture.Model.Id, new(1100, 1100, 10.1f));
        var actor = fixture.Runtime.Actors.Find(fixture.Admission.Catalog.TryGet(npc)!.Value)!;
        var services = fixture.Admission.World.GetActivationServices(npc);
        npc.Activation.UpdateProximity(services, fixture.Admission.Registry.Spatial!);
        await Assert.That(npc.Activation.IsNearPlayer).IsFalse();
        npc.Activation.UpdateActivation(services); //Startup grace keeps the real NPC active despite no nearby character.
        await Assert.That(actor.EntityActive).IsTrue();
        await Assert.That(actor.EntityHidden).IsFalse();
        actor.Entry.SetUpdateAlways(1);
        npc.Activation.UpdateActivation(services);
        await Assert.That(actor.EntityActive).IsFalse();
        await Assert.That(actor.AiObject.Enabled).IsFalse();
        //The original hidden GameObject branch needs extension inventory; do not fabricate it here.
        npc.Activation.SetProximity(0, 120, services);
        npc.Activation.UpdateActivation(services);
        ZoneNpcAutoDisable.Set(actor, 0);
        npc.Activation.SetProximity(float.MaxValue, 120, services);
        npc.Activation.UpdateActivation(services);
        await Assert.That(actor.EntityActive).IsTrue();
        await Assert.That(actor.AiObject.Enabled).IsTrue();
        await Assert.That(fixture.Admission.World.HasAiActor(npc)).IsFalse();
    }

    [Test]
    public async Task RetiredProxyAndRegistrationOperationsCannotTouchAReplacementWithTheSameEntityId()
    {
        using var fixture = new Fixture(); using var old = fixture.Admit(entityId: 72);
        var oldActor = old.Actor;
        old.Binding.Dispose(); oldActor.Dispose();
        using var replacement = fixture.Admit(entityId: 72);
        Run(fixture, "AI.AutoDisable(entity.id,0)");
        await Assert.That(replacement.Actor.Entry.Activation.UpdateAlwaysRemaining).IsEqualTo(short.MinValue);
        await Assert.That(() => ZoneNpcAutoDisable.Get(oldActor)).Throws<ObjectDisposedException>();
        await Assert.That(() => ZoneNpcAutoDisable.Set(oldActor, 1)).Throws<ObjectDisposedException>();
        await Assert.That(() => ZoneNpcAutoDisable.ApplyInitialParameters(old.Binding)).Throws<ObjectDisposedException>();
        old.Binding.Dispose();
        await Assert.That(ZoneNpcAutoDisable.Get(replacement.Actor)).IsFalse();
    }

    private static void Run(Fixture fixture, string script) => fixture.Lua.Execute(script).Dispose();

    //The offline oracle begins from raw native memory, including arbitrary signed word values.
    //This only arranges the same fixture state; production has no added countdown-reset API.
    private static void SeedWord(ZoneActorActivationState state, short value) =>
        typeof(ZoneActorActivationState).GetProperty(nameof(ZoneActorActivationState.UpdateAlwaysRemaining))!.SetValue(state, value);

    private static double NativeStatus(string text) => text switch
    {
        "nan" => double.NaN, "inf" => double.PositiveInfinity, "-inf" => double.NegativeInfinity,
        _ => double.Parse(text, CultureInfo.InvariantCulture),
    };

    private sealed class Fixture : IDisposable
    {
        private static int _nextUnit = 1_300_000, _nextEntity = 17_000_000;
        private ulong _nextProxy;
        private readonly ManagedNpcScriptEffectsTests.Fixture _base = new();
        public ZoneManagedLuaRuntime Lua => _base.Lua;
        public ZoneManagedNpcActorScene Scene => _base.Scene;
        public ZoneManagedNpcScriptHost Host => _base.Host;
        public Fixture() => Scene.GameObjectActivationProfile = ZoneManagedNpcGameObjectActivationProfile.MultiplayerWorldServer;
        public Admitted Admit(bool activateAlways = false, uint entityId = 0)
        {
            var original = _base.Admission.Npc((uint)Interlocked.Increment(ref _nextUnit));
            var npc = new ZoneNpc(original.Unit, original.Spawn, 0, _base.Admission.Simulation,
                new NpcTemplateDefinition { Id = 11, ActivateAiAlways = activateAlways }, original.Model, 0,
                _base.Admission.World, _ => null, _ => { }, ZoneNpcSignalContent.Empty, _base.Admission.Simulation.Diagnostics);
            _base.Admission.Publish(npc);
            var handle = _base.Admission.Catalog.Register(npc);
            var actor = Scene.Prepare(handle, new() { EntityId = entityId == 0 ? (uint)Interlocked.Increment(ref _nextEntity) : entityId },
                ZoneManagedNpcActorScene.CreatePipeUserControls(), true);
            using var template = Host.Scripts.RetainGenericActorTemplate();
            var prepared = Host.Scripts.Prepare(11, new(actor.AiObject.EntityId), template, actor);
            var binding = Host.BindEntity(prepared, actor, "auto-disable-control");
            Scene.Adopt(actor, 5, ++_nextProxy, prepared.EntityScript);
            Scene.RegisterBehavior(actor, prepared.DetachBehavior());
            var effects = _base.Effects.BindEntity(binding, new());
            Lua.SetGlobal("entity", prepared.EntityScript);
            Lua.Execute("entity.PropertiesInstance={bAutoDisable=1}").Dispose();
            return new(actor, prepared, binding, effects);
        }
        public void Dispose() => _base.Dispose();
    }

    private sealed record Admitted(ZoneManagedNpcActor Actor, ZoneNpcAuthoredEntity Prepared,
        ZoneManagedNpcScriptBinding Binding, ZoneManagedNpcScriptEffectsLease Effects) : IDisposable
    {
        public void Dispose()
        {
            Effects.Dispose(); Binding.Dispose(); Actor.Dispose(); Prepared.Dispose();
            Actor.Npc.Activation.Dispose(); Actor.Npc.AiSignals.Dispose(); Actor.Npc.Signals.Dispose(); Actor.Npc.Talks.Dispose();
        }
    }
}
