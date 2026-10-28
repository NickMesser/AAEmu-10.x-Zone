using System.Numerics;
using System.Text;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Content;
using AAEmu.Zone.Hosting;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Scripting;

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
        public ZoneManagedPhysicsBackend? Backend { get; }
        public ZoneManagedNpcPhysics? Bodies { get; }
        public ZoneModelDefinition Model { get; } = new() { Id = 42, Name = "runtime_fixture", Subtype = "ActorModel", SubtypeId = 9 };
        public Fixture(bool withEffects = false, bool admitProfile = true, bool withComponents = false)
        {
            ZoneManagedNpcRuntime? runtime = null;
            Admission = new(handle => runtime!.CreateActor(handle));
            var models = new ZoneModelContent([Model], actorModels: new ZoneActorModelContent([new()
            {
                Id = 9, ModelFile = "objects/fixture.chr", PhysicsLivingMass = 80, PhysicsLivingGravity = 25,
                PhysicsLivingColliderMat = "mat_player_collider",
            }], Enumerable.Range(1, 9).Select(id => new ZoneGameStanceDefinition
            { Id = (uint)id, ActorModelId = 9, StanceId = id, SizeX = .3f, SizeY = .3f, SizeZ = .3f, HeightCollider = 1.2f, HeightPivot = .1f, UseCapsule = true })));
            Vector3 Position(AAEmu.Zone.Units.ZoneUnit unit) => withComponents ? new(1100, 1100, 10.1f) : Vector3.Zero;
            Profiles = new(Admission.Registry, Admission.Catalog, Position, models);
            if (withComponents)
            {
                var heights = new AAEmu.Zone.Terrain.ZoneTerrainHeightField();
                heights.LoadCell(1, 1, TerrainHeightFieldTests.CreateCell(10, 0));
                Backend = new(new(heights)); Bodies = new(Backend, models, _ => 143);
            }
            var content = new ZoneNpcContent([new() { Id = 11, NpcAiParamId = 8 }], [], [], [], [],
                aiParameters: [new(8, "delay=1.5")]);
            Runtime = runtime = new(Admission.Registry, Admission.Catalog, models, content, new Source(),
                Position, () => Clock.GameMicroseconds, () => 1, () => DateTimeOffset.UnixEpoch,
                TimeSpan.FromMilliseconds(20), createProfile: admitProfile ? Profiles.Create : null,
                createScriptServices: withComponents ? lua => new ZoneManagedNpcSceneScriptServices(lua, models, new Assets(),
                    () => Bodies!, _ => new(0, 0), Position, () => Clock.GameMicroseconds, () => false,
                    ZoneCharacterAttachmentPublicationProfile.BoneOnly,
                    combat: new(Admission.Registry, Admission.Catalog, value => Admission.Registry.QueueNotification(value))) :
                    withEffects ? lua => new ZoneManagedNpcScriptEffects(lua, () => Clock.GameMicroseconds) : null);
            if (Runtime.ScriptServices is ZoneManagedNpcSceneScriptServices components)
                Runtime.Actors.SmartObjectStateConsumer = components.ModifySmartObjectStates;
            Admission.World.AttachAiRuntime(Runtime.Actors.Actors.AiRuntime);
            if (!Runtime.Bootstrap().Ready) throw new InvalidOperationException(Runtime.Scripts.BootstrapReport.Refusal);
        }
        public void Dispose()
        { try { Admission.Dispose(); } finally { try { Runtime.Dispose(); } finally { try { Bodies?.Dispose(); } finally { Profiles.Dispose(); } } } }
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
                    npc={ActorTemplate={Properties={soclasses_SmartObjectClass='Npc'},Server={OnTimer=function(e,id,ms)timerCalls=(timerCalls or 0)+1 end,OnUpdate=function(e)
                      if failUpdate and e.failUpdate then System.Error('controlled update refusal')end
                    end,OnInit=function(e)
                      observedEntity=e;BasicAI.OnReset(e)
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

    private sealed class Assets : IZoneAssetSource
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
            var bytes = output.ToArray(); Encoding.UTF8.GetBytes("root").CopyTo(bytes, chunk + 48 + 216);
            return new MemoryStream(bytes, false);
        }
    }
}
