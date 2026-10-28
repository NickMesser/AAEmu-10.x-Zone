using System.Numerics;
using AAEmu.Zone.Hosting;
using AAEmu.Zone.Npcs;
using AAEmu.Zone.Scene;

namespace AAEmu.Zone.Tests;

public sealed class ManagedNpcEntityActivityTests
{
    [Test]
    public async Task EntityActivityIsIndependentOfAiAndOnlyEligibleTimeReachesTheOriginalUpdateAccumulator()
    {
        using var fixture = new ManagedNpcScriptEffectsTests.Fixture();
        using var owner = fixture.Admit();
        var actor = owner.Actor;
        await Assert.That(actor.CopyEntityActivity()).IsEqualTo(new ZoneManagedNpcEntityActivityState(false, false, 0x600, false));
        actor.SetActive(true);
        fixture.Effects.SetEntityTimer(actor, 17, 0);
        await Assert.That(fixture.Effects.Tick(TimeSpan.FromSeconds(.25)).Count).IsEqualTo(0);
        await Assert.That(fixture.Effects.CopyState(actor).AccumulatedTime).IsEqualTo(10f);
        using (var values = fixture.Lua.Execute("return #entity.updates,#entity.events,entity.events[1].id"))
        {
            await Assert.That(values[0]).IsEqualTo(0L);
            await Assert.That(values[1]).IsEqualTo(1L);
            await Assert.That(values[2]).IsEqualTo(17L);
        }
        actor.SetEntityActive(true); actor.SetEntityHidden(true);
        fixture.Effects.Tick(TimeSpan.FromSeconds(3));
        await Assert.That(fixture.Effects.CopyState(actor).AccumulatedTime).IsEqualTo(10f);
        actor.SetEntityHidden(false); actor.SetActive(false);
        //AI enablement is not CEntity's active bit. A live entity still receives its callback.
        fixture.Effects.Tick(TimeSpan.FromSeconds(.125));
        using (var values = fixture.Lua.Execute("return #entity.updates,entity.updates[1]"))
        {
            await Assert.That(values[0]).IsEqualTo(1L);
            await Assert.That(values[1]).IsEqualTo(10.125d);
        }
        actor.SetEntityActive(false);
        fixture.Effects.Tick(TimeSpan.FromSeconds(7));
        await Assert.That(fixture.Effects.CopyState(actor).AccumulatedTime).IsEqualTo(0f);
        actor.SetEntityActive(true);
        fixture.Effects.Tick(TimeSpan.FromSeconds(.5), scriptsEnabled: false);
        await Assert.That(fixture.Effects.CopyState(actor).AccumulatedTime).IsEqualTo(0f);
    }

    [Test]
    public async Task RealNpcCompletionDeactivatesBeforeItsModelIsPublishedAndTimersStillRun()
    {
        using var fixture = new ManagedNpcRuntimeTests.Fixture(withMovement: true);
        fixture.Runtime.Lua.SetGlobal("testTimer", true);
        var actor = Admit(fixture);
        var state = actor.CopyEntityActivity();
        await Assert.That(state.NpcInitializationCompleted).IsTrue();
        await Assert.That(state.Active).IsFalse();
        await Assert.That(state.Hidden).IsTrue();
        await Assert.That(actor.Npc.Activation.IsActive).IsFalse();
        await Assert.That(fixture.Motion!.Find(actor.Handle)!.ModelFlags).IsEqualTo(1u);
        await Assert.That(actor.AiObject.Enabled).IsFalse(); //Original AIevent6.
        //A separate AI enable operation does not undo CEntity deactivation.
        actor.SetActive(true);
        await Assert.That(actor.EntityActive).IsFalse();
        using var script = actor.Entry.RetainScript()!;
        script.Set("failUpdate", true); fixture.Runtime.Lua.SetGlobal("failUpdate", true);
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(20));
        await Assert.That(fixture.Runtime.TickScripts(TimeSpan.FromMilliseconds(20), fixture.Admission.Provider.RemoveNpc).Count).IsEqualTo(0);
        await Assert.That(fixture.Runtime.Lua.GetGlobal("timerCalls")).IsEqualTo(1L);
        await Assert.That(fixture.Runtime.Scripts.RecentErrors.Count).IsEqualTo(0);
        await Assert.That(fixture.Runtime.Count).IsEqualTo(1);
        await Assert.That(fixture.Components.Effects.CopyState(actor).AccumulatedTime).IsEqualTo(10f);
    }

    [Test]
    public async Task OriginalEightFrameActivationUsesTheRealProxyCountdownAndUnhidesDedicatedEntities()
    {
        using var fixture = new ManagedNpcRuntimeTests.Fixture(withMovement: true);
        var actor = Admit(fixture);
        await Assert.That(fixture.Admission.World.HasAiActor(actor.Npc)).IsFalse();
        await Assert.That(fixture.Admission.World.HasPartialUpdateStep(actor.Npc, ZoneNpcUpdateStep.Proximity)).IsTrue();
        await Assert.That(fixture.Admission.World.HasPartialUpdateStep(actor.Npc, ZoneNpcUpdateStep.Activation)).IsTrue();
        for (uint frame = 1; frame < 8; frame++) fixture.Admission.Simulation.Update(frame, false);
        await Assert.That(actor.EntityActive).IsFalse();
        fixture.Admission.Simulation.Update(8, false);
        await Assert.That(actor.Npc.Activation.IsNearPlayer).IsFalse();
        await Assert.That(actor.Entry.Activation.AlwaysUpdate).IsTrue(); //Actual constructor1024, no invented leader/player.
        await Assert.That(actor.EntityActive).IsTrue();
        await Assert.That(actor.EntityHidden).IsFalse();
        await Assert.That(actor.Npc.Activation.IsActive).IsTrue();
        await Assert.That(actor.IsEntityScriptUpdateEligible).IsTrue();
        var services = fixture.Admission.World.GetActivationServices(actor.Npc);
        actor.Npc.Activation.SetActive(false, services);
        await Assert.That(actor.EntityHidden).IsTrue();
        await Assert.That(actor.EntityActive).IsFalse();
        await Assert.That(actor.AiObject.Enabled).IsFalse();
        actor.Npc.Activation.SetActive(true, services);
        await Assert.That(actor.EntityHidden).IsFalse();
        await Assert.That(actor.EntityActive).IsTrue();
        await Assert.That(actor.AiObject.Enabled).IsTrue();
    }

    [Test]
    public async Task ConcreteWorldInputsKeepDecimalSightRangesAndTheConfiguredAiOverride()
    {
        using var fixture = new ManagedNpcRuntimeTests.Fixture(withMovement: true,
            movementCVars: ZoneEngineCVars.Parse("player_unit_sight_range=95.25\nplayer_unit_sight_range_big=200.5\nai_UpdateAllAlways=1"));
        var actor = Admit(fixture);
        var services = fixture.Admission.World.GetActivationServices(actor.Npc);
        await Assert.That(services.GetEntity()).IsEqualTo(actor);
        await Assert.That(services.PlayerSightRange).IsEqualTo(95.25f);
        await Assert.That(services.PlayerSightRangeBig).IsEqualTo(200.5f);
        await Assert.That(services.GlobalAiUpdateAlways).IsTrue();
        await Assert.That(services.IsDedicated).IsTrue();
        await Assert.That(services.GetPosition()).IsEqualTo(new Vector3(1100, 1100, actor.Npc.Unit.Pose.Position.Z));
        fixture.Admission.Simulation.Update(8, false);
        await Assert.That(actor.EntityActive).IsTrue();
    }

    [Test]
    public async Task UnsupportedHiddenUpdateFlagsRefuseAtomicallyAndRetiredAdaptersCannotTouchAReusedId()
    {
        using var fixture = new ManagedNpcRuntimeTests.Fixture(withMovement: true);
        var old = Admit(fixture);
        var services = fixture.Admission.World.GetActivationServices(old.Npc);
        var entity = services.GetEntity()!;
        var before = old.CopyEntityActivity();
        await Assert.That(() => old.SetEntityFlags(0x200600)).Throws<NotSupportedException>();
        await Assert.That(old.CopyEntityActivity()).IsEqualTo(before);
        var remove = new AAEmu.Zone.Protocol.ZoneWriter(); remove.WriteBc(old.Npc.ObjectId);
        await Assert.That(fixture.Admission.Registry.Remove(remove.ToArray())).IsTrue();
        var replacement = Admit(fixture);
        await Assert.That(replacement.Handle.Generation).IsGreaterThan(old.Handle.Generation);
        await Assert.That(services.HasUnit).IsFalse();
        await Assert.That(services.GetEntity()).IsNull();
        await Assert.That(() => entity.Activate(true)).Throws<ObjectDisposedException>();
        await Assert.That(() => entity.Hide(false)).Throws<ObjectDisposedException>();
        await Assert.That(replacement.EntityActive).IsFalse();
        await Assert.That(replacement.EntityHidden).IsTrue();
        await Assert.That(fixture.Admission.World.GetActivationServices(replacement.Npc).GetEntity()).IsEqualTo(replacement);
    }

    private static ZoneManagedNpcActor Admit(ManagedNpcRuntimeTests.Fixture fixture)
    {
        var npc = fixture.Admission.CreateFromWire(fixture.Model.Id, new(1100, 1100, 10.1f));
        var handle = fixture.Admission.Catalog.TryGet(npc)!.Value;
        return fixture.Runtime.Actors.Find(handle) ??
            throw new InvalidOperationException(fixture.Admission.Catalog.GetAdmission(handle)?.Refusal ?? "The concrete NPC actor was not admitted.");
    }
}
