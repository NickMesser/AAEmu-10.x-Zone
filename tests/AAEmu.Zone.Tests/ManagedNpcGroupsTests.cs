using System.Numerics;
using AAEmu.Zone.Content;
using AAEmu.Zone.Npcs;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Scripting;

namespace AAEmu.Zone.Tests;

public sealed class ManagedNpcGroupsTests
{
    [Test]
    public async Task FreshConstructorHasActualZeroRecordWithoutAnInventedGroupAllocation()
    {
        using var fixture = new Fixture(); using var actor = fixture.Admit();
        using var registration = fixture.Groups.BindEntity(actor.Binding);
        var state = fixture.Groups.CopyState(actor.Actor);
        await Assert.That(state.Record).IsEqualTo(new ZoneManagedNpcGroupRecord(0, 0, 0));
        await Assert.That(state.InitialParametersApplied).IsFalse();
        await Assert.That(state.MembershipIndexed).IsFalse();
        await Assert.That(fixture.Groups.CopyGroup(0)).IsNull();
        await Assert.That(fixture.Groups.GetGroupLeader(0)).IsNull();
        await Assert.That(fixture.Groups.GetGroupLeader(12345)).IsNull();
        await Assert.That(fixture.Groups.GroupCount).IsEqualTo(0);
        fixture.Groups.ApplyInitialParameters(actor.Actor); fixture.Groups.InstallAuthoredGroup(actor.Actor);
        await Assert.That(fixture.Groups.GetGroupId(actor.Actor)).IsEqualTo(0);
        await Assert.That(fixture.Groups.CopyState(actor.Actor).AuthoredGroupInstalled).IsTrue();
        await Assert.That(fixture.Groups.GroupCount).IsEqualTo(0);
    }

    [Test]
    public async Task ParametersReadActualTablesInNativeOrderAndConvertThroughFloat()
    {
        using var fixture = new Fixture();
        foreach (var (script, expected) in new[]
        {
            ("entity.Properties={groupid=7.9};entity.PropertiesInstance={groupid=99}", 7),
            ("entity.Properties={groupid=-4.9};entity.PropertiesInstance={groupid=99}", 0),
            ("entity.Properties={groupid='57'};entity.PropertiesInstance={groupid=8.9}", 8),
            ("entity.Properties={groupid=false};entity.PropertiesInstance={groupid=13}", 13),
            ("entity.Properties=nil;entity.PropertiesInstance={groupid=17}", 17),
            ("entity.Properties={groupid=16777217};entity.PropertiesInstance={}", 16777216),
            ("entity.Properties={groupid=2147483647};entity.PropertiesInstance={groupid=19}", 0)
        })
        {
            using var actor = fixture.Admit(); using var registration = fixture.Groups.BindEntity(actor.Binding);
            fixture.Lua.Execute(script).Dispose();
            fixture.Groups.ApplyInitialParameters(actor.Actor);
            await Assert.That(fixture.Groups.GetGroupId(actor.Actor)).IsEqualTo(expected);
            await Assert.That(fixture.Groups.CopyState(actor.Actor).InitialParametersApplied).IsTrue();
            await Assert.That(() => fixture.Groups.ApplyInitialParameters(actor.Actor)).Throws<InvalidOperationException>();
        }
    }

    [Test]
    public async Task LaterParameterApplicationObservesOnInitPropertyWrites()
    {
        using var fixture = new Fixture(); using var actor = fixture.Admit();
        using var registration = fixture.Groups.BindEntity(actor.Binding);
        fixture.Lua.Execute("entity.Properties={groupid=3};entity.Server.OnInit=function(e)e.Properties.groupid=29 end").Dispose();
        using var server = (ZoneLuaReference)fixture.Lua.Get(actor.Prepared.EntityScript, "Server", 5)!;
        using var initialize = (ZoneLuaReference)fixture.Lua.Get(server, "OnInit", 6)!;
        fixture.Lua.Invoke(initialize, actor.Prepared.EntityScript);
        await Assert.That(fixture.Groups.GetGroupId(actor.Actor)).IsEqualTo(0);
        fixture.Groups.ApplyInitialParameters(actor.Actor);
        await Assert.That(fixture.Groups.GetGroupId(actor.Actor)).IsEqualTo(29);
        await Assert.That(fixture.Groups.CopyGroup(29)!.Members.Single()).IsEqualTo(actor.Actor.Handle);
    }

    [Test]
    public async Task AuthoredGroupOverridesRuntimeParametersAndPublishesOriginalScriptInputs()
    {
        using var fixture = new Fixture();
        using var first = fixture.Admit(new(5, 888, 0)); using var a = fixture.Groups.BindEntity(first.Binding);
        fixture.Lua.Execute("entity.Properties={groupid=7};entity.AI={}").Dispose();
        fixture.Groups.ApplyInitialParameters(first.Actor); fixture.Groups.InstallAuthoredGroup(first.Actor);
        await Assert.That(fixture.Groups.CopyState(first.Actor).Record).IsEqualTo(new ZoneManagedNpcGroupRecord(5, 888, 0));
        using (var values = fixture.Lua.Execute("return entity.Properties.groupid,entity.AI.formationTension"))
        {
            await Assert.That(Convert.ToDouble(values[0])).IsEqualTo(888d);
            await Assert.That(Convert.ToSingle(values[1])).IsEqualTo(.25f);
        }
        using var second = fixture.Admit(new(5, 888, 1)); using var b = fixture.Groups.BindEntity(second.Binding);
        fixture.Lua.Execute("entity.Properties={};entity.AI={}").Dispose();
        fixture.Groups.ApplyInitialParameters(second.Actor); fixture.Groups.InstallAuthoredGroup(second.Actor);
        var group = fixture.Groups.CopyGroup(888)!;
        await Assert.That(group.Members.Count).IsEqualTo(2);
        await Assert.That(group.Members[0]).IsEqualTo(first.Actor.Handle);
        await Assert.That(group.Members[1]).IsEqualTo(second.Actor.Handle);
        await Assert.That(group.HasLeaderObject).IsFalse();
        await Assert.That(fixture.Groups.GetGroupLeader(888)).IsNull(); //IsLeader/bit1 does not select a leader.
        await Assert.That(fixture.Groups.CopyGroup(7)!.Members.Count).IsEqualTo(0);
    }

    [Test]
    public async Task MissingGroupContentPreservesTheActualInitialGroupInsteadOfGuessingSpawnId()
    {
        using var fixture = new Fixture(); using var actor = fixture.Admit(new(999, 400, 0));
        using var registration = fixture.Groups.BindEntity(actor.Binding);
        fixture.Lua.Execute("entity.Properties={groupid=31};entity.AI={formationTension=9}").Dispose();
        fixture.Groups.ApplyInitialParameters(actor.Actor); fixture.Groups.InstallAuthoredGroup(actor.Actor);
        await Assert.That(fixture.Groups.GetGroupId(actor.Actor)).IsEqualTo(31);
        await Assert.That(fixture.Groups.CopyState(actor.Actor).Record.Type).IsEqualTo(0u);
        await Assert.That(fixture.Groups.CopyGroup(400)).IsNull();
        using var values = fixture.Lua.Execute("return entity.Properties.groupid,entity.AI.formationTension");
        await Assert.That(values[0]).IsEqualTo(31L);
        await Assert.That(values[1]).IsEqualTo(9L);
    }

    [Test]
    public async Task SameIdStillCopiesTypeMemberAndTensionWithoutDuplicatingMembership()
    {
        using var fixture = new Fixture(); using var actor = fixture.Admit(new(5, 50000, 1));
        using var registration = fixture.Groups.BindEntity(actor.Binding);
        fixture.Lua.Execute("entity.Properties={groupid=50000};entity.AI={}").Dispose();
        fixture.Groups.ApplyInitialParameters(actor.Actor); fixture.Groups.InstallAuthoredGroup(actor.Actor);
        await Assert.That(fixture.Groups.CopyState(actor.Actor).Record).IsEqualTo(new ZoneManagedNpcGroupRecord(5, 50000, 1));
        await Assert.That(fixture.Groups.CopyGroup(50000)!.Members.Count).IsEqualTo(1);
        await Assert.That(fixture.Groups.GetGroupLeader(50000)).IsNull();
        using var values = fixture.Lua.Execute("return entity.AI.formationTension");
        await Assert.That(Convert.ToSingle(values[0])).IsEqualTo(.75f);
    }

    [Test]
    public async Task MovementLeaderRefusesBeforePartialGroupAndScriptPublication()
    {
        using var fixture = new Fixture(); using var actor = fixture.Admit(new(6, 600, 1));
        using var registration = fixture.Groups.BindEntity(actor.Binding);
        fixture.Lua.Execute("entity.Properties={groupid=41};entity.AI={formationTension=2}").Dispose();
        fixture.Groups.ApplyInitialParameters(actor.Actor);
        await Assert.That(() => fixture.Groups.InstallAuthoredGroup(actor.Actor)).Throws<NotSupportedException>();
        await Assert.That(fixture.Groups.CopyState(actor.Actor).Record).IsEqualTo(new ZoneManagedNpcGroupRecord(0, 41, 0));
        await Assert.That(fixture.Groups.CopyState(actor.Actor).AuthoredGroupInstalled).IsFalse();
        await Assert.That(fixture.Groups.CopyGroup(600)).IsNull();
        using var values = fixture.Lua.Execute("return entity.Properties.groupid,entity.AI.formationTension");
        await Assert.That(values[0]).IsEqualTo(41L);
        await Assert.That(values[1]).IsEqualTo(2L);
    }

    [Test]
    public async Task OnlyTheFirstAuthoredMovementLeaderTriggersTheFormationProducer()
    {
        using var fixture = new Fixture(); using var actor = fixture.Admit(new(6, 600, 2));
        using var registration = fixture.Groups.BindEntity(actor.Binding);
        fixture.Lua.Execute("entity.Properties={};entity.AI={}").Dispose();
        fixture.Groups.ApplyInitialParameters(actor.Actor); fixture.Groups.InstallAuthoredGroup(actor.Actor);
        await Assert.That(fixture.Groups.GetGroupId(actor.Actor)).IsEqualTo(600);
        await Assert.That(fixture.Groups.GetGroupLeader(600)).IsNull();
    }

    [Test]
    public async Task RejectedInputsDoNotChangeGroupOrClaimInstallation()
    {
        using var fixture = new Fixture(); using var actor = fixture.Admit(new(5, 600, 9));
        using var registration = fixture.Groups.BindEntity(actor.Binding);
        await Assert.That(() => fixture.Groups.InstallAuthoredGroup(actor.Actor)).Throws<InvalidOperationException>();
        fixture.Lua.Execute("entity.Properties={groupid=11};entity.AI={}").Dispose();
        fixture.Groups.ApplyInitialParameters(actor.Actor);
        await Assert.That(() => fixture.Groups.InstallAuthoredGroup(actor.Actor)).Throws<InvalidDataException>();
        await Assert.That(fixture.Groups.GetGroupId(actor.Actor)).IsEqualTo(11);
        await Assert.That(fixture.Groups.CopyGroup(600)).IsNull();
    }

    [Test]
    public async Task RetiredBindingsCannotRemainMembersAndOldLeasesCannotRemoveNewMembers()
    {
        using var fixture = new Fixture(); using var first = fixture.Admit();
        var old = fixture.Groups.BindEntity(first.Binding);
        fixture.Lua.Execute("entity.Properties={groupid=37}").Dispose(); fixture.Groups.ApplyInitialParameters(first.Actor);
        first.Binding.Dispose();
        await Assert.That(fixture.Groups.CopyGroup(37)!.Members.Count).IsEqualTo(0);
        await Assert.That(old.IsDisposed).IsTrue();
        await Assert.That(() => fixture.Groups.GetGroupId(first.Actor)).Throws<ObjectDisposedException>();
        using var replacement = fixture.Admit(); using var current = fixture.Groups.BindEntity(replacement.Binding);
        fixture.Lua.Execute("entity.Properties={groupid=37}").Dispose(); fixture.Groups.ApplyInitialParameters(replacement.Actor);
        old.Dispose();
        await Assert.That(fixture.Groups.CopyGroup(37)!.Members.Single()).IsEqualTo(replacement.Actor.Handle);
        fixture.Scene.Remove(replacement.Actor.Handle);
        await Assert.That(fixture.Groups.CopyGroup(37)!.Members.Count).IsEqualTo(0);
        await Assert.That(fixture.Groups.GroupCount).IsEqualTo(1); //Native group map outlives last member.
    }

    [Test]
    public async Task SpawnerDetachClearsTheActualGroupRecordAndMembershipWithoutRewritingLua()
    {
        using var fixture = new Fixture(); using var actor = fixture.Admit(new(5, 600, 0));
        using var registration = fixture.Groups.BindEntity(actor.Binding);
        fixture.Lua.Execute("entity.Properties={groupid=17};entity.AI={}").Dispose();
        fixture.Groups.ApplyInitialParameters(actor.Actor); fixture.Groups.InstallAuthoredGroup(actor.Actor);
        fixture.Groups.ClearActorGroup(actor.Actor);
        var state = fixture.Groups.CopyState(actor.Actor);
        await Assert.That(state.Record).IsEqualTo(new ZoneManagedNpcGroupRecord(0, 0, 0));
        await Assert.That(state.MembershipIndexed).IsTrue(); //Native object index retains the zero-ID object; AIGroup0 is absent.
        await Assert.That(state.InitialParametersApplied).IsTrue();
        await Assert.That(state.AuthoredGroupInstalled).IsTrue();
        await Assert.That(fixture.Groups.CopyGroup(17)!.Members.Count).IsEqualTo(0);
        await Assert.That(fixture.Groups.CopyGroup(600)!.Members.Count).IsEqualTo(0);
        using var values = fixture.Lua.Execute("return entity.Properties.groupid,entity.AI.formationTension");
        await Assert.That(Convert.ToDouble(values[0])).IsEqualTo(600d);
        await Assert.That(Convert.ToSingle(values[1])).IsEqualTo(.25f);
        fixture.Groups.ClearActorGroup(actor.Actor);
        await Assert.That(fixture.Groups.CopyGroup(0)).IsNull();
        await Assert.That(fixture.Groups.GroupCount).IsEqualTo(2);
    }

    [Test]
    public async Task ClearingARetiredGroupCannotChangeAnotherExactActorMembership()
    {
        using var fixture = new Fixture(); using var old = fixture.Admit();
        using var oldRegistration = fixture.Groups.BindEntity(old.Binding);
        fixture.Lua.Execute("entity.Properties={groupid=37}").Dispose(); fixture.Groups.ApplyInitialParameters(old.Actor);
        old.Binding.Dispose();
        using var current = fixture.Admit(); using var registration = fixture.Groups.BindEntity(current.Binding);
        fixture.Lua.Execute("entity.Properties={groupid=37}").Dispose(); fixture.Groups.ApplyInitialParameters(current.Actor);
        await Assert.That(() => fixture.Groups.ClearActorGroup(old.Actor)).Throws<ObjectDisposedException>();
        await Assert.That(fixture.Groups.CopyGroup(37)!.Members.Single()).IsEqualTo(current.Actor.Handle);
        fixture.Groups.ClearActorGroup(current.Actor);
        await Assert.That(fixture.Groups.GetGroupId(current.Actor)).IsEqualTo(0);
        await Assert.That(fixture.Groups.CopyGroup(37)!.Members.Count).IsEqualTo(0);
    }

    [Test]
    public async Task OwnerDisposalRetiresLeasesAndBorrowsTheSceneLuaRuntime()
    {
        using var fixture = new Fixture(); using var actor = fixture.Admit();
        using var registration = fixture.Groups.BindEntity(actor.Binding);
        await Assert.That(() => fixture.Groups.BindEntity(actor.Binding)).Throws<InvalidOperationException>();
        fixture.Groups.Dispose();
        await Assert.That(registration.IsDisposed).IsTrue();
        await Assert.That(() => fixture.Groups.GetGroupLeader(0)).Throws<ObjectDisposedException>();
        using var values = fixture.Lua.Execute("return 3");
        await Assert.That(values[0]).IsEqualTo(3L);
    }

    private sealed class Fixture : IDisposable
    {
        private static int _nextUnit = 1_100_000, _nextEntity = 16_000_000;
        private ulong _proxy;
        private readonly ManagedNpcScriptEffectsTests.Fixture _base = new();
        private readonly List<Admitted> _actors = [];
        public IZoneLuaRuntime Lua => _base.Lua;
        public ZoneManagedNpcActorScene Scene => _base.Scene;
        public ZoneManagedNpcGroups Groups { get; }
        public Fixture()
        {
            var content = new ZoneNpcContent([new() { Id = 11 }],
                [new(5, 0, false, "no movement leader"), new(6, 0, false, "explicit movement leader")],
                [new(1, 5, 11, Vector3.Zero, .25f, true, false), new(2, 5, 11, Vector3.One, .75f, false, false),
                 new(3, 6, 11, Vector3.Zero, .1f, true, false), new(4, 6, 11, Vector3.One, .2f, false, true),
                 new(5, 6, 11, Vector3.One, .3f, false, true)], [], []);
            Groups = new(Lua, content);
        }
        public Admitted Admit(NpcSpawnGroup group = default)
        {
            var source = _base.Admission.Npc((uint)Interlocked.Increment(ref _nextUnit));
            var npc = new ZoneNpc(source.Unit, source.Spawn with { Group = group }, 0, _base.Admission.Simulation,
                source.Template, source.Model, 0, _base.Admission.World, _ => null, _ => { },
                ZoneNpcSignalContent.Empty, _base.Admission.Simulation.Diagnostics);
            _base.Admission.Publish(npc);
            var handle = _base.Admission.Catalog.Register(npc);
            var actor = Scene.Prepare(handle, new() { EntityId = (uint)Interlocked.Increment(ref _nextEntity) },
                ZoneManagedNpcActorScene.CreatePipeUserControls(), true);
            using var template = _base.Host.Scripts.RetainGenericActorTemplate();
            var prepared = _base.Host.Scripts.Prepare(11, new(actor.AiObject.EntityId), template, actor);
            var binding = _base.Host.BindEntity(prepared, actor, "group-control");
            Scene.Adopt(actor, 5, ++_proxy, prepared.EntityScript); Scene.RegisterBehavior(actor, prepared.DetachBehavior());
            Lua.SetGlobal("entity", prepared.EntityScript);
            var result = new Admitted(actor, prepared, binding); _actors.Add(result); return result;
        }
        public void Dispose()
        { Groups.Dispose(); foreach (var actor in _actors) actor.Dispose(); _base.Dispose(); }
    }
    private sealed record Admitted(ZoneManagedNpcActor Actor, ZoneNpcAuthoredEntity Prepared,
        ZoneManagedNpcScriptBinding Binding) : IDisposable
    {
        public void Dispose()
        {
            Binding.Dispose(); Actor.Dispose(); Prepared.Dispose();
            Actor.Npc.Activation.Dispose(); Actor.Npc.AiSignals.Dispose(); Actor.Npc.Signals.Dispose(); Actor.Npc.Talks.Dispose();
        }
    }
}
