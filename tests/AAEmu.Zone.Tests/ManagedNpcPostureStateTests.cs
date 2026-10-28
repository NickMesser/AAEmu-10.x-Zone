using AAEmu.Zone.Content;
using AAEmu.Zone.Npcs;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Units;

namespace AAEmu.Zone.Tests;

public sealed class ManagedNpcPostureStateTests
{
    private static ZoneModelDefinition Model() => new() { Id = 27, Name = "posture_actor", Subtype = "ActorModel", SubtypeId = 7 };
    private static ZoneNpcPostureContent Content() => new(
        [new(11, 50, 0, 0, ""), new(12, 60, 0, 20, "")], [new(0, 0)]);

    [Test]
    public async Task DefaultPostureUsesStrictTimeSelectionAndTheRealCurrentAnimationConsumer()
    {
        using var fixture = new ManagedNpcScriptEffectsTests.Fixture(); using var actor = fixture.Admit(model: Model());
        var frames = new List<ZoneFrame>(); float hour = 0;
        using var state = new ZoneManagedNpcPostureState(Content(), id => new(id, 2), () => hour, frames.Add);
        using var owner = state.Bind(actor.Actor, new(Model(), true, [], new(0, false)));
        IZoneNpcPostureModel consumer = owner;
        await Assert.That(consumer.CurrentAnimationId).IsEqualTo(2u);
        state.SetDefaultPosture(actor.Actor, true);
        await Assert.That(frames.Count).IsEqualTo(0); //start must be strictly less than hour
        hour = .5f; state.SetDefaultPosture(actor.Actor, true);
        await Assert.That(consumer.CurrentAnimationId).IsEqualTo(50u);
        await Assert.That(ReadPosture(frames.Single())).IsEqualTo((actor.Actor.Npc.ObjectId, false, 50u, true));
        state.SetDefaultPosture(actor.Actor, true);
        await Assert.That(frames.Count).IsEqualTo(1);
        state.SetDefaultPosture(actor.Actor, false);
        await Assert.That(consumer.CurrentAnimationId).IsEqualTo(2u);
        await Assert.That(ReadPosture(frames[1])).IsEqualTo((actor.Actor.Npc.ObjectId, false, 50u, false));
        state.SetDefaultPosture(actor.Actor, false);
        await Assert.That(frames.Count).IsEqualTo(3); //server notification still occurs for disabled repeats
        hour = 20; state.SetDefaultPosture(actor.Actor, true);
        await Assert.That(consumer.CurrentAnimationId).IsEqualTo(50u);
        hour = 20.5f; state.SetDefaultPosture(actor.Actor, true);
        await Assert.That(consumer.CurrentAnimationId).IsEqualTo(60u);
    }

    [Test]
    public async Task WorldPostureConsumesNativeCachePriorityWithoutEchoingNotifications()
    {
        using var fixture = new ManagedNpcScriptEffectsTests.Fixture(); using var actor = fixture.Admit(model: Model());
        var frames = new List<ZoneFrame>();
        using var state = new ZoneManagedNpcPostureState(Content(), id => new(id, id switch { 31 => 3, 41 => 4, _ => 2 }), () => 1, frames.Add);
        using var owner = state.Bind(actor.Actor, new(Model(), true, [], new(50, true)));
        state.ApplyFromWorld(actor.Actor, new(41, true), true);
        await Assert.That(owner.CurrentAnimationId).IsEqualTo(41u);
        state.ApplyFromWorld(actor.Actor, new(31, true), false);
        await Assert.That(owner.CurrentAnimationId).IsEqualTo(31u);
        state.ApplyFromWorld(actor.Actor, new(31, false), false);
        await Assert.That(owner.CurrentAnimationId).IsEqualTo(41u);
        state.ApplyFromWorld(actor.Actor, new(41, false), false);
        await Assert.That(owner.CurrentAnimationId).IsEqualTo(50u);
        await Assert.That(frames.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ExplicitAbsentDriverAndOwnedOverrideInventoryKeepTheirDifferentNativeEffects()
    {
        using var fixture = new ManagedNpcScriptEffectsTests.Fixture(); using var actor = fixture.Admit(model: Model());
        var frames = new List<ZoneFrame>(); var clockReads = 0;
        using var state = new ZoneManagedNpcPostureState(Content(), _ => throw new InvalidOperationException("Absent driver does not resolve animation actions."),
            () => { clockReads++; return 1; }, frames.Add);
        using (var absent = state.Bind(actor.Actor, new(Model(), false, [], new(0, false))))
        {
            state.SetDefaultPosture(actor.Actor, true); state.SetDefaultPosture(actor.Actor, true);
            await Assert.That(absent.CurrentAnimationId).IsEqualTo(2u);
            await Assert.That(absent.Posture).IsEqualTo(new ActorPosture(50, true));
            await Assert.That(frames.Count).IsEqualTo(2);
        }
        frames.Clear();
        using var overridden = state.Bind(actor.Actor, new(Model(), false, [7, 1, 9], new(0, false)));
        state.SetDefaultPosture(actor.Actor, true);
        await Assert.That(frames.Count).IsEqualTo(0);
        await Assert.That(clockReads).IsEqualTo(3); //selection precedes the native override check
    }

    [Test]
    public async Task SignalItemActionsAndRetiredGenerationsRefuseWithoutPublishingUnbackedPostures()
    {
        using var fixture = new ManagedNpcScriptEffectsTests.Fixture(); using var actor = fixture.Admit(model: Model());
        var frames = new List<ZoneFrame>();
        using var state = new ZoneManagedNpcPostureState(Content(), id => new(id, 1), () => 1, frames.Add);
        using var owner = state.Bind(actor.Actor, new(Model(), true, [], new(0, false)));
        await Assert.That(() => state.SetDefaultPosture(actor.Actor, true)).Throws<NotSupportedException>();
        await Assert.That(owner.Posture).IsEqualTo(new ActorPosture(0, false));
        await Assert.That(frames.Count).IsEqualTo(0);
        fixture.Scene.Remove(actor.Actor.Handle);
        await Assert.That(state.Find(actor.Actor) is null).IsTrue();
        await Assert.That(() => state.SetDefaultPosture(actor.Actor, true)).Throws<InvalidOperationException>();
        await Assert.That(() => owner.CurrentAnimationId).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ConstructionUsesTheActualWireModelInsteadOfCachedTemplateModelMetadata()
    {
        using var fixture = new ManagedNpcScriptEffectsTests.Fixture();
        using var actor = fixture.Admit(model: new() { Id = 99, Subtype = "PrefabModel" }, unitModelId: Model().Id);
        using var state = new ZoneManagedNpcPostureState(Content(), id => new(id, 2), () => 1, _ => { });
        await Assert.That(() => state.Bind(actor.Actor, new(actor.Actor.Npc.Model, false, [], new(0, false))))
            .Throws<InvalidOperationException>();
        using var actual = state.Bind(actor.Actor, new(Model(), true, [], new(0, false)));
        state.ApplyFromWorld(actor.Actor, new(50, true), false);
        await Assert.That(actual.CurrentAnimationId).IsEqualTo(50u);
    }

    private static (uint ObjectId, bool Looted, uint Animation, bool Activate) ReadPosture(ZoneFrame frame)
    {
        if (frame.Opcode != ZwOpcodes.UnitModelPostureChanged) throw new InvalidDataException("Wrong posture opcode.");
        var reader = new ZoneReader(frame.Body.Span); var objectId = reader.ReadBc();
        if (reader.ReadByte() != 4) throw new InvalidDataException("Wrong model posture type.");
        var result = (objectId, reader.ReadBoolean(), reader.ReadUInt32(), reader.ReadBoolean()); reader.EnsureFullyConsumed(); return result;
    }
}
