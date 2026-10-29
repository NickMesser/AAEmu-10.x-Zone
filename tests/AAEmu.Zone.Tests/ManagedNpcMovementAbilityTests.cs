using System.Numerics;
using System.Text.Json;
using AAEmu.Zone.Content;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Units;
using AAEmu.Zone.Scripting;

namespace AAEmu.Zone.Tests;

public sealed class ManagedNpcMovementAbilityTests
{
    [Test]
    public async Task OriginalCommonRegistrationTailCopiesActualWorldModelCrowdFlagsAfterNpcParameters()
    {
        using var stream = typeof(ManagedNpcMovementAbilityTests).Assembly.GetManifestResourceStream(
            "AAEmu.Zone.Tests.Fixtures.npc-model-common-tail-native-controls.json")!;
        using var controls = JsonDocument.Parse(stream);
        var tested = 0;
        foreach (var value in controls.RootElement.GetProperty("controls").EnumerateArray()
            .Where(value => value.GetProperty("unitKind").GetByte() == (byte)ZoneUnitKind.Npc))
        {
            var flags = value.GetProperty("modelFlags144").GetByte();
            var actualModel = new ZoneModelDefinition
            {
                Id = 42, Name = "runtime_fixture", Subtype = "ActorModel", SubtypeId = 9,
                Big = (flags & 2) != 0, ForceNavigateAroundObject = (flags & 8) != 0,
            };
            using var fixture = new ManagedNpcRuntimeTests.Fixture(withMovementAbility: true, model: actualModel);
            InstallTables(fixture);
            //These model bytes survive the NPC-specific parameter setup before the
            //common tail publishes them. Lua movement-table defaults are not their producer.
            var npc = fixture.Admission.CreateFromWire(actualModel.Id, new(1100, 1100, 10.1f));
            var handle = fixture.Admission.Catalog.TryGet(npc)!.Value;
            var actor = fixture.Runtime.Actors.Find(handle) ?? throw new InvalidOperationException(
                fixture.Admission.Catalog.GetAdmission(handle)?.Refusal ?? "The common-tail control did not admit its actual model.");
            var state = fixture.MovementAbility!.CopyState(actor);
            var native = value.GetProperty("native");
            await Assert.That(state.CrowdFlag177).IsEqualTo(native.GetProperty("flag177").GetBoolean());
            await Assert.That(state.CrowdFlag178).IsEqualTo(native.GetProperty("flag178").GetBoolean());
            tested++;
        }
        await Assert.That(tested).IsEqualTo(4);
        await Assert.That(controls.RootElement.GetProperty("controlCount").GetInt32()).IsEqualTo(32);
    }

    [Test]
    public async Task ActualPostInitTablesAreCopiedBeforeTheCharacterConstructorAndRemainInstalled()
    {
        using var fixture = new ManagedNpcRuntimeTests.Fixture(withMovementAbility: true);
        Run(fixture, """
            local speeds={}
            for s,name in ipairs({'Relaxed','Combat','Stealth','Crouch','Prone','Swim','Fly'})do
              speeds[name]={}
              for u,urgency in ipairs({'Slow','Walk','Run','Sprint'})do
                speeds[name][urgency]={s*10+u,s*10+u-0.5,s*10+u+0.5}
              end
            end
            npc.ActorTemplate.AIMovementAbility={AIMovementSpeeds=speeds,pathLookAhead=99}
            local init=npc.ActorTemplate.Server.OnInit
            npc.ActorTemplate.Server.OnInit=function(e)
              init(e)
              e.AIMovementAbility.pathLookAhead=-1
              e.AIMovementAbility.pathRadius=0.375
              e.AIMovementAbility.passRadius=0.375
              e.AIMovementAbility.usePredictiveFollowing=1
              e.AIMovementAbility.maxAccel=7
              e.AIMovementAbility.maxDecel=9
              e.AIMovementAbility.directionalScaleRefSpeedMin=1
              e.AIMovementAbility.directionalScaleRefSpeedMax=8
              e.AIMovementAbility.pathRegenIntervalDuringTrace=4
            end
            """);
        var npc = fixture.Admission.CreateFromWire(fixture.Model.Id, new(1100, 1100, 10.1f));
        var actor = fixture.Runtime.Actors.Find(fixture.Admission.Catalog.TryGet(npc)!.Value)!;
        var state = fixture.MovementAbility!.CopyState(actor);
        await Assert.That(state.PassRadius).IsEqualTo(.375f);
        await Assert.That(actor.AiObject.Radius).IsEqualTo(.375f);
        await Assert.That(state.GroundTrace.LookAhead).IsEqualTo(74f * .1f + .375f);
        await Assert.That(state.GroundTrace.PathRadius).IsEqualTo(.375f);
        await Assert.That(state.GroundTrace.MaximumAcceleration).IsEqualTo(7f);
        await Assert.That(state.GroundTrace.MaximumDeceleration).IsEqualTo(9f);
        await Assert.That(state.PredictiveFollowing).IsTrue();
        await Assert.That(state.NavigationCapabilities).IsEqualTo(134ul);
        await Assert.That(state.PathRegenInterval).IsEqualTo(4f);
        await Assert.That(state.Speeds.Select(1, 1).Desired).IsEqualTo(13f);
        await Assert.That(state.Speeds.Select(1.5f, 8).Desired).IsEqualTo(74f);
        using (var script = actor.Entry.RetainScript()!)
        using (var ability = (ZoneLuaReference)script.Get("AIMovementAbility")!)
            ability.Set("pathLookAhead", 800);
        await Assert.That(ReferenceEquals(fixture.MovementAbility.CopyState(actor), state)).IsTrue();
        var writer = new ZoneWriter(); writer.WriteBc(npc.ObjectId);
        await Assert.That(fixture.Admission.Registry.Remove(writer.ToArray())).IsTrue();
        await Assert.That(fixture.MovementAbility.Count).IsEqualTo(0);
        await Assert.That(fixture.MovementAbility.Find(actor.Handle)).IsNull();
        await Assert.That(() => fixture.MovementAbility.CopyState(actor)).Throws<ObjectDisposedException>();
    }

    [Test]
    [Arguments("1", true)]
    [Arguments("0", false)]
    [Arguments("0/0", false)]
    [Arguments("'1'", false)]
    public async Task PredictiveRegistrationUsesTheOriginalTypedBooleanRead(string value, bool expected)
    {
        using var fixture = new ManagedNpcRuntimeTests.Fixture(withMovementAbility: true);
        InstallTables(fixture);
        Run(fixture, $"npc.ActorTemplate.AIMovementAbility.usePredictiveFollowing={value}");
        var npc = fixture.Admission.CreateFromWire(fixture.Model.Id, new Vector3(1100, 1100, 10.1f));
        var actor = fixture.Runtime.Actors.Find(fixture.Admission.Catalog.TryGet(npc)!.Value)!;
        var state = fixture.MovementAbility!.CopyState(actor);
        await Assert.That(state.PredictiveFollowing).IsEqualTo(expected);
        await Assert.That(state.UsesPathfinder).IsTrue();
        await Assert.That(state.GroundTrace.LookAhead).IsEqualTo(3f);
        await Assert.That(state.GroundTrace.PathRadius).IsEqualTo(1f);
        await Assert.That(state.GroundTrace.MaximumAcceleration).IsEqualTo(float.MaxValue);
    }

    internal static void InstallTables(ManagedNpcRuntimeTests.Fixture fixture) => Run(fixture, """
        local speeds={}
        for _,name in ipairs({'Relaxed','Combat','Stealth','Crouch','Prone','Swim','Fly'})do
          speeds[name]={Slow={1,0.8,1.2},Walk={2,1.6,2.4},Run={6,4.8,6},Sprint={6,4.8,6}}
        end
        npc.ActorTemplate.AIMovementAbility={AIMovementSpeeds=speeds}
        """);

    [Test]
    public async Task ActualModelTurnDescriptorOverridesLuaAndAvoidanceRadiusRemainsDistinctFromAgentPassRadius()
    {
        using var fixture = new ManagedNpcRuntimeTests.Fixture(withMovementAbility: true);
        InstallTables(fixture);
        Run(fixture, """
            npc.ActorTemplate.AIMovementAbility.lookIdleTurnSpeed=360
            npc.ActorTemplate.AIMovementAbility.lookCombatTurnSpeed=720
            npc.ActorTemplate.AIMovementAbility.faceTargetInstantlyOnStrafing=1
            npc.ActorTemplate.AIMovementAbility.pathRadius=0.7
            npc.ActorTemplate.AIMovementAbility.avoidanceRadius=0.4
            npc.ActorTemplate.AIMovementAbility.passRadius=0.2
            """);
        var npc = fixture.Admission.CreateFromWire(fixture.Model.Id, new(1100, 1100, 10.1f));
        var actor = fixture.Runtime.Actors.Find(fixture.Admission.Catalog.TryGet(npc)!.Value)!;
        var state = fixture.MovementAbility!.CopyState(actor);
        await Assert.That(state.Look!.IdleTurnSpeed).IsEqualTo(0f);
        await Assert.That(state.Look.CombatTurnSpeed).IsEqualTo(0f);
        await Assert.That(state.Look.FaceTargetInstantlyOnStrafing).IsFalse();
        await Assert.That(state.AvoidanceRadius).IsEqualTo(.4f + .7f);
        await Assert.That(state.PassRadius).IsEqualTo(.2f);
        await Assert.That(actor.AiObject.Radius).IsEqualTo(.2f);
    }

    [Test]
    public async Task OriginalNegativeSentinelsResolveTheActualModelAndRawSourceStanceBeforePathLookAhead()
    {
        using var fixture = new ManagedNpcRuntimeTests.Fixture(withMovementAbility: true);
        InstallTables(fixture);
        Run(fixture, """
            npc.ActorTemplate.AIMovementAbility.passRadius=-1
            npc.ActorTemplate.AIMovementAbility.pathRadius=-1
            npc.ActorTemplate.AIMovementAbility.pathLookAhead=-1
            """);
        var npc = fixture.Admission.CreateFromWire(fixture.Model.Id, new(1100, 1100, 10.1f));
        var actor = fixture.Runtime.Actors.Find(fixture.Admission.Catalog.TryGet(npc)!.Value)!;
        var state = fixture.MovementAbility!.CopyState(actor);
        //The real first content ID2 row is a capsule with(.3,.3) raw horizontal size.
        await Assert.That(state.PassRadius).IsEqualTo(.3f);
        await Assert.That(actor.AiObject.Radius).IsEqualTo(.3f);
        await Assert.That(state.GroundTrace.PathRadius).IsEqualTo(.3f);
        await Assert.That(state.GroundTrace.LookAhead).IsEqualTo(6f * .1f + .3f);
        await Assert.That(state.AvoidanceRadius).IsEqualTo(.3f);
    }

    [Test]
    public async Task WrongTypedScalarValuesUseTypedFallbackWithoutLeakingOwnedLuaReferences()
    {
        using var clean = new ManagedNpcRuntimeTests.Fixture(withMovementAbility: true);
        using var unusual = new ManagedNpcRuntimeTests.Fixture(withMovementAbility: true);
        InstallTables(clean); InstallTables(unusual);
        Run(unusual, """
            local original=npc.ActorTemplate.Server.OnInit
            npc.ActorTemplate.Server.OnInit=function(e)
              original(e)
              e.AIMovementAbility.pathRadius={}
              e.AIMovementAbility.maxAccel={}
              e.AIMovementAbility.avoidanceRadius={}
              e.AIMovementAbility.usePredictiveFollowing=function()end
            end
            """);
        Retire(clean); Retire(unusual);
        await Assert.That(((ZoneManagedLuaRuntime)unusual.Runtime.Lua).RetainedReferenceCount)
            .IsEqualTo(((ZoneManagedLuaRuntime)clean.Runtime.Lua).RetainedReferenceCount);

        static void Retire(ManagedNpcRuntimeTests.Fixture fixture)
        {
            var npc = fixture.Admission.CreateFromWire(fixture.Model.Id, new(1100, 1100, 10.1f));
            if (fixture.Runtime.Actors.Find(fixture.Admission.Catalog.TryGet(npc)!.Value) is null)
                throw new InvalidOperationException("The typed fallback profile unexpectedly refused admission.");
            var writer = new ZoneWriter(); writer.WriteBc(npc.ObjectId);
            if (!fixture.Admission.Registry.Remove(writer.ToArray())) throw new InvalidOperationException("The owned test NPC did not retire.");
        }
    }
    private static void Run(ManagedNpcRuntimeTests.Fixture fixture, string source)
    { using var result = fixture.Runtime.Lua.Execute(source); }
}
