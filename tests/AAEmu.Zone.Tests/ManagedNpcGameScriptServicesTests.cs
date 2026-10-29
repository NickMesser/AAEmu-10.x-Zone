using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Scripting;

namespace AAEmu.Zone.Tests;

public sealed class ManagedNpcGameScriptServicesTests
{
    [Test]
    public async Task SmartObjectRegistrationReadsAuthoredClassesAndHasAnActualFilterConsumer()
    {
        using var fixture = new ManagedNpcScriptEffectsTests.Fixture(); using var actor = fixture.Admit();
        fixture.Lua.Execute("entity.Properties={soclasses_SmartObjectClass='Actor, 12Guard,_Npc,Actor'};entity.PropertiesInstance={soclasses_SmartObjectClass='Guard;Vendor'}").Dispose();
        using var states = new ZoneManagedNpcSmartObjectStates(fixture.Lua);
        using var registration = states.BindEntity(actor.Binding, new());
        await Assert.That(registration.HasSmartObject).IsTrue();
        await Assert.That(string.Join(',', states.CopyClasses(actor.Actor))).IsEqualTo("Actor,Guard,_Npc,Vendor");
        await Assert.That(states.HasState(actor.Actor, "Idle")).IsTrue();
        await Assert.That(states.MatchesStateFilter(actor.Actor, "Idle-Busy")).IsTrue();
        states.AddState(actor.Actor, "Busy");
        await Assert.That(states.MatchesStateFilter(actor.Actor, "Idle-Busy")).IsFalse();
        await Assert.That(states.MatchesStateFilter(actor.Actor, "Combat|Busy")).IsTrue();
        await Assert.That(states.MatchesStateFilter(actor.Actor, "idle")).IsFalse();
        states.SetState(actor.Actor, "idle");
        await Assert.That(states.MatchesStateFilter(actor.Actor, "Idle|idle-Busy")).IsTrue();
        await Assert.That(states.HasState(actor.Actor, "Idle")).IsFalse();
        states.SetState(actor.Actor, "");
        await Assert.That(states.HasState(actor.Actor, "idle")).IsTrue();
    }

    [Test]
    public async Task LiteralUnitAddAndBehaviorExpressionHaveDistinctOriginalSemantics()
    {
        using var fixture = new ManagedNpcScriptEffectsTests.Fixture(); using var actor = fixture.Admit();
        fixture.Lua.Execute("entity.Properties.soclasses_SmartObjectClass='Actor'").Dispose();
        using var states = new ZoneManagedNpcSmartObjectStates(fixture.Lua); using var registration = states.BindEntity(actor.Binding, new());
        states.AddState(actor.Actor, "Busy,Combat");
        await Assert.That(states.HasState(actor.Actor, "Busy,Combat")).IsTrue();
        await Assert.That(states.HasState(actor.Actor, "Busy")).IsFalse();
        states.AddState(actor.Actor, "Busy"); states.AddState(actor.Actor, "Alerted");
        states.ModifyStates(actor.Actor, "Idle,Combat-Alerted,Busy");
        await Assert.That(states.MatchesStateFilter(actor.Actor, "Idle,Combat-Alerted,Busy")).IsTrue();
        states.ModifyStates(actor.Actor, "Busy-Busy");
        await Assert.That(states.HasState(actor.Actor, "Busy")).IsTrue(); //removal precedes addition
        states.StateReplacementEnabled = false;
        states.SetState(actor.Actor, "Off"); states.ModifyStates(actor.Actor, "Off-Busy");
        await Assert.That(states.HasState(actor.Actor, "Off")).IsFalse();
        states.RemoveState(actor.Actor, "Busy");
        await Assert.That(states.HasState(actor.Actor, "Busy")).IsFalse(); //singular leaves ignore that gate
    }

    [Test]
    public async Task BusyPropagationConsumesTheFirstRealLinkAndDoesNotFollowRetiredTargets()
    {
        using var fixture = new ManagedNpcScriptEffectsTests.Fixture();
        using var first = fixture.Admit(); fixture.Lua.Execute("entity.Properties.soclasses_SmartObjectClass='Actor'").Dispose();
        using var second = fixture.Admit(); fixture.Lua.Execute("entity.Properties.soclasses_SmartObjectClass='Actor'").Dispose();
        using var third = fixture.Admit(); fixture.Lua.Execute("entity.Properties.soclasses_SmartObjectClass='Actor'").Dispose();
        var links = new ZoneManagedNpcSmartObjectLinks(); links.Add("Busy", second.Actor); links.Add("Busy", third.Actor);
        var reverse = new ZoneManagedNpcSmartObjectLinks(); reverse.Add("Busy", first.Actor);
        using var states = new ZoneManagedNpcSmartObjectStates(fixture.Lua);
        using var a = states.BindEntity(first.Binding, links); using var b = states.BindEntity(second.Binding, reverse); using var c = states.BindEntity(third.Binding, new());
        states.AddState(first.Actor, "Busy");
        await Assert.That(states.HasState(second.Actor, "Busy")).IsTrue();
        await Assert.That(states.HasState(third.Actor, "Busy")).IsFalse();
        states.RemoveState(first.Actor, "Busy");
        await Assert.That(states.HasState(second.Actor, "Busy")).IsFalse();
        states.AddState(first.Actor, "Busy"); states.SetState(first.Actor, "Idle");
        await Assert.That(states.HasState(second.Actor, "Busy")).IsTrue(); //replace is not linked remove
        fixture.Scene.Remove(second.Actor.Handle);
        states.AddState(first.Actor, "Busy");
        await Assert.That(states.HasState(third.Actor, "Busy")).IsFalse();
        await Assert.That(() => states.AddState(second.Actor, "Busy")).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ClasslessEntitiesHaveKnownAbsentSmartObjectsAndParserUsesTheOriginalByteLimit()
    {
        using var fixture = new ManagedNpcScriptEffectsTests.Fixture(); using var actor = fixture.Admit();
        using var states = new ZoneManagedNpcSmartObjectStates(fixture.Lua); using var absent = states.BindEntity(actor.Binding, new());
        await Assert.That(absent.HasSmartObject).IsFalse(); states.AddState(actor.Actor, "Busy");
        await Assert.That(states.MatchesStateFilter(actor.Actor, "")).IsFalse(); absent.Dispose();
        using (var properties = fixture.Lua.CreateTable())
        { properties.Set("soclasses_SmartObjectClass", new string(' ', 1020) + "Actor"); actor.Prepared.EntityScript.Set("Properties", properties); }
        using var clipped = states.BindEntity(actor.Binding, new());
        await Assert.That(string.Join(',', states.CopyClasses(actor.Actor))).IsEqualTo("Act");
        await Assert.That(states.MatchesStateFilter(actor.Actor, " || ")).IsTrue();
        clipped.Dispose();
        await Assert.That(() => states.SetState(actor.Actor, "Busy")).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task CreateBuffReturnsTheOriginalBooleanAndRequestsWorldWithoutOptimisticLocalEffects()
    {
        var service = new Requests();
        using var fixture = new ManagedNpcScriptEffectsTests.Fixture(_ => service); using var actor = fixture.Admit();
        var frames = new List<ZoneFrame>();
        service.Owner = new(fixture.Admission.Registry, fixture.Admission.Catalog, frames.Add, _ => default);
        var buffs = actor.Actor.Npc.Unit.Buffs;
        var initialCount = buffs.Good.Count + buffs.Bad.Count + buffs.Hidden.Count;
        using var result = fixture.Lua.Execute("return entity.unit:CreateBuff(815)");
        await Assert.That(result[0] is true).IsTrue();
        var frame = frames.Single();
        await Assert.That(frame.Opcode).IsEqualTo(ZwOpcodes.CreateBuff);
        await Assert.That(BuffCreationRequest.Read(frame.Body.Span)).IsEqualTo(new BuffCreationRequest(actor.Actor.Npc.ObjectId, 815));
        await Assert.That(ReferenceEquals(actor.Actor.Npc.Unit.Buffs, buffs)).IsTrue();
        await Assert.That(actor.Actor.Npc.Unit.Buffs.Good.Count + actor.Actor.Npc.Unit.Buffs.Bad.Count + actor.Actor.Npc.Unit.Buffs.Hidden.Count).IsEqualTo(initialCount);
    }

    [Test]
    public async Task RemoveBuffReturnsTheOriginalBooleanAndOnlySendsATemplateRequest()
    {
        var service = new Requests();
        using var fixture = new ManagedNpcScriptEffectsTests.Fixture(_ => service); using var actor = fixture.Admit();
        var frames = new List<ZoneFrame>();
        service.Owner = new(fixture.Admission.Registry, fixture.Admission.Catalog, frames.Add, _ => default);
        var buffs = actor.Actor.Npc.Unit.Buffs;
        using var result = fixture.Lua.Execute("return entity.unit:RemoveBuff(815)");
        await Assert.That(result[0] is true).IsTrue();
        var frame = frames.Single(); var request = BuffRemovalRequest.Read(frame.Body.Span);
        await Assert.That(frame.Opcode).IsEqualTo(ZwOpcodes.RemoveBuff);
        await Assert.That(request).IsEqualTo(new BuffRemovalRequest(actor.Actor.Npc.ObjectId, 0, 815, 10));
        await Assert.That(ReferenceEquals(actor.Actor.Npc.Unit.Buffs, buffs)).IsTrue();
    }

    [Test]
    public async Task StopCastingSendsSkillBeforeTimelineAndNeverMutatesTheCopiedOwner()
    {
        using var fixture = new ManagedNpcScriptEffectsTests.Fixture(); using var actor = fixture.Admit();
        var state = new ZoneNpcCastingRequestState(17, 900, 23); var frames = new List<ZoneFrame>();
        var requests = new ZoneManagedNpcGameScriptRequests(fixture.Admission.Registry, fixture.Admission.Catalog, frames.Add, _ => state);
        requests.StopCasting(actor.Actor);
        await Assert.That(frames.Count).IsEqualTo(2);
        await Assert.That(ReadStop(frames[0])).IsEqualTo((actor.Actor.Npc.ObjectId, 0u, 900u));
        await Assert.That(ReadStop(frames[1])).IsEqualTo((actor.Actor.Npc.ObjectId, 17u, 0u));
        await Assert.That(state).IsEqualTo(new ZoneNpcCastingRequestState(17, 900, 23));
        frames.Clear(); state = new(0, 0, 23); requests.StopCasting(actor.Actor);
        await Assert.That(ReadStop(frames.Single())).IsEqualTo((actor.Actor.Npc.ObjectId, 23u, 0u));
        frames.Clear(); state = default; requests.StopCasting(actor.Actor);
        await Assert.That(frames.Count).IsEqualTo(0);
        fixture.Scene.Remove(actor.Actor.Handle);
        await Assert.That(() => requests.RemoveBuff(actor.Actor, 815)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task UnownedCastFieldsRefuseAndRetirementDuringSendStopsFurtherOldGenerationRequests()
    {
        using var fixture = new ManagedNpcScriptEffectsTests.Fixture(); using var actor = fixture.Admit();
        var frames = new List<ZoneFrame>();
        var missing = new ZoneManagedNpcGameScriptRequests(fixture.Admission.Registry, fixture.Admission.Catalog, frames.Add,
            _ => throw new NotSupportedException("Independent channel timeline is not owned."));
        await Assert.That(() => missing.StopCasting(actor.Actor)).Throws<NotSupportedException>();
        await Assert.That(frames.Count).IsEqualTo(0);
        var requests = new ZoneManagedNpcGameScriptRequests(fixture.Admission.Registry, fixture.Admission.Catalog,
            frame => { frames.Add(frame); fixture.Scene.Remove(actor.Actor.Handle); }, _ => new(17, 900, 23));
        await Assert.That(() => requests.StopCasting(actor.Actor)).Throws<InvalidOperationException>();
        await Assert.That(frames.Count).IsEqualTo(1);
    }

    [Test]
    public async Task RetirementDiscardsOnlyUnsentRequestsForItsExactScriptScope()
    {
        using var fixture = new ManagedNpcScriptEffectsTests.Fixture(); using var actor = fixture.Admit();
        var registry = fixture.Admission.Registry;
        while (registry.TryDequeueNotification(out _)) { }
        var marker = new ZoneFrame(0xfffe, new byte[] { 1, 2, 3 });
        registry.QueueNotification(marker, actor.Actor.Npc.Unit, new object());
        var requests = new ZoneManagedNpcGameScriptRequests(registry, fixture.Admission.Catalog,
            _ => throw new InvalidOperationException("Scoped requests must use the registry's exact owner queue."),
            _ => new(17, 900, 23), queueWithActorScope: true);
        requests.StopCasting(actor.Actor); requests.CreateBuff(actor.Actor, 815); requests.RemoveBuff(actor.Actor, 815);
        fixture.Scene.Remove(actor.Actor.Handle);
        requests.Retire(actor.Actor);
        await Assert.That(registry.TryDequeueNotification(out var retained)).IsTrue();
        await Assert.That(retained).IsEqualTo(marker);
        await Assert.That(registry.TryDequeueNotification(out _)).IsFalse();
    }

    private static (uint ObjectId, uint Timeline, uint Skill) ReadStop(ZoneFrame frame)
    {
        if (frame.Opcode != ZwOpcodes.StopCasting) throw new InvalidDataException("Wrong request opcode.");
        var reader = new ZoneReader(frame.Body.Span); var result = (reader.ReadBc(), reader.ReadUInt32(), reader.ReadUInt32());
        reader.EnsureFullyConsumed(); return result;
    }
    private sealed class Requests : IZoneManagedNpcScriptServices
    {
        public ZoneManagedNpcGameScriptRequests Owner { get; set; } = null!;
        public bool CreateBuff(ZoneManagedNpcActor actor, uint buffType) => Owner.CreateBuff(actor, buffType);
        public bool RemoveBuff(ZoneManagedNpcActor actor, uint buffType) => Owner.RemoveBuff(actor, buffType);
    }
}
