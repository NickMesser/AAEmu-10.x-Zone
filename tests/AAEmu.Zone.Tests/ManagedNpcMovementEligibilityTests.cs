using System.Numerics;
using System.Text.Json;
using AAEmu.Zone.Ai;
using AAEmu.Zone.Content;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Protocol.Units;
using AAEmu.Zone.Scene;

namespace AAEmu.Zone.Tests;

public sealed class ManagedNpcMovementEligibilityTests
{
    [Test]
    public async Task OriginalCompleteCanMoveLeafAgreesAcrossEveryStatusAndModelBitCombination()
    {
        using var stream = typeof(ManagedNpcMovementEligibilityTests).Assembly.GetManifestResourceStream(
            "AAEmu.Zone.Tests.Fixtures.npc-can-move-native-controls.json")!;
        using var json = JsonDocument.Parse(stream);
        var checkedModels = 0;
        foreach (var control in json.RootElement.GetProperty("controls").EnumerateArray())
        {
            //Managed lifecycle tests enforce exact generation ownership. The native missing-
            //Unit log+true and absent-model false are retained in the oracle's declared scope.
            if (!control.GetProperty("unitPresent").GetBoolean() || !control.GetProperty("modelPresent").GetBoolean()) continue;
            var result = ZoneManagedNpcMovementEligibility.Evaluate(control.GetProperty("stun").GetByte() != 0,
                control.GetProperty("root").GetByte() != 0, control.GetProperty("sleep").GetByte() != 0,
                control.GetProperty("modelFlags").GetUInt32(), control.GetProperty("psychokinesis").GetBoolean());
            await Assert.That(result).IsEqualTo(control.GetProperty("result").GetBoolean());
            checkedModels++;
        }
        await Assert.That(checkedModels).IsEqualTo(64);
        await Assert.That(json.RootElement.GetProperty("controls").GetArrayLength()).IsEqualTo(67);
        foreach (var module in json.RootElement.GetProperty("engineModulesLoaded").EnumerateObject())
            await Assert.That(module.Value.GetBoolean()).IsFalse();
    }

    [Test]
    public async Task ActualWorldUnitAndInitializedModelAdmitMovementDespiteInactiveEntityAiAndStationaryBody()
    {
        using var fixture = new ManagedNpcRuntimeTests.Fixture(withMovement: true);
        var npc = fixture.Admission.CreateFromWire(fixture.Model.Id, new Vector3(1100, 1100, 10.1f));
        var handle = fixture.Admission.Catalog.TryGet(npc)!.Value;
        var actor = fixture.Runtime.Actors.Find(handle)!;
        var eligibility = fixture.Runtime.Actors.MovementEligibility!;
        await Assert.That(actor.AiObject.EntityId == npc.ObjectId).IsFalse();
        await Assert.That(actor.EntityActive).IsFalse();
        await Assert.That(actor.EntityHidden).IsTrue();
        await Assert.That(actor.AiObject.Enabled).IsFalse();
        await Assert.That(fixture.Bodies!.Find(handle)!.CopyState().Status.Velocity).IsEqualTo(Vector3.Zero);
        await Assert.That(eligibility.CanMove(actor)).IsTrue();
        //Health/death is independent of the native CanMove predicate too.
        npc.Unit.Die(npc.Unit.Stats);
        await Assert.That(npc.Unit.IsDead).IsTrue();
        await Assert.That(eligibility.CanMove(actor)).IsTrue();
        var removed = new ZoneWriter(); removed.WriteBc(npc.ObjectId);
        await Assert.That(fixture.Admission.Registry.Remove(removed.ToArray())).IsTrue();
        await Assert.That(() => eligibility.CanMove(actor)).Throws<ObjectDisposedException>();
        var replacement = fixture.Admission.CreateFromWire(fixture.Model.Id, new Vector3(1100, 1100, 10.1f));
        var next = fixture.Runtime.Actors.Find(fixture.Admission.Catalog.TryGet(replacement)!.Value)!;
        await Assert.That(replacement.ObjectId).IsEqualTo(npc.ObjectId);
        await Assert.That(eligibility.CanMove(next)).IsTrue();
        await Assert.That(() => eligibility.CanMove(actor)).Throws<ObjectDisposedException>();
    }

    [Test]
    [Arguments(601u)]
    [Arguments(602u)]
    [Arguments(603u)]
    [Arguments(604u)]
    public async Task ActualWorldBuffMutationsImmediatelyChangeTheBoundActorPredicate(uint template)
    {
        using var fixture = new ManagedNpcRuntimeTests.Fixture(withMovement: true, movementBuffContent: BuffContent());
        var npc = fixture.Admission.CreateFromWire(fixture.Model.Id, new Vector3(1100, 1100, 10.1f));
        var actor = fixture.Runtime.Actors.Find(fixture.Admission.Catalog.TryGet(npc)!.Value)!;
        var eligibility = fixture.Runtime.Actors.MovementEligibility!;
        await Assert.That(eligibility.CanMove(actor)).IsTrue();
        var created = Created(npc.ObjectId, template);
        await Assert.That(fixture.Admission.Registry.CreateBuff(created.ToFrame().Body.Span)).IsTrue();
        await Assert.That(eligibility.CanMove(actor)).IsFalse();
        await Assert.That(fixture.Admission.Registry.UpdateBuff(new BuffUpdated(npc.ObjectId, 1, 9, 7, 500, 0).ToFrame().Body.Span)).IsTrue();
        await Assert.That(eligibility.CanMove(actor)).IsFalse();
        await Assert.That(fixture.Admission.Registry.RemoveBuff(new BuffRemoved(npc.ObjectId, 1).ToFrame().Body.Span)).IsTrue();
        await Assert.That(eligibility.CanMove(actor)).IsTrue();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task ActualGoalExecutorAppliesNot47OnceToCurrentUnitStatus(bool stunned, bool inverse)
    {
        using var fixture = new ManagedNpcRuntimeTests.Fixture(withMovement: true, movementBuffContent: BuffContent());
        var npc = fixture.Admission.CreateFromWire(fixture.Model.Id, new Vector3(1100, 1100, 10.1f));
        var actor = fixture.Runtime.Actors.Find(fixture.Admission.Catalog.TryGet(npc)!.Value)!;
        fixture.Runtime.Actors.GameObjectActivationProfile = ZoneManagedNpcGameObjectActivationProfile.MultiplayerWorldServer;
        npc.Activation.SetActive(true, fixture.Admission.World.GetActivationServices(npc));
        if (stunned) fixture.Admission.Registry.CreateBuff(Created(npc.ObjectId, 601).ToFrame().Body.Span);
        var pipe = fixture.Runtime.Actors.GoalPipes.Create("can_move47_inversion")!;
        void Append(ZoneGoalOperation operation, ZoneGoalParameters parameters, bool blocking = false) =>
            pipe.Append(operation, blocking, ZoneGoalGrouping.None, parameters, message => throw new InvalidOperationException(message));
        Append(ZoneGoalOperation.Branch, new() { Integer = 47 | (inverse ? 0x100 : 0), Integer2 = 1 });
        Append(ZoneGoalOperation.Run, new() { First = 1 });
        Append(ZoneGoalOperation.Timeout, new() { First = 10, Second = 10 }, true);
        actor.Entry.Phase.Pipes.Select(ZoneGoalPipeFlags.RunOnce, pipe.Name);
        actor.SetActive(true);
        actor.Update(1000, .02f); actor.Update(1020, .02f);
        var branchTaken = (!stunned) != inverse;
        await Assert.That(actor.Controls.MovementUrgency).IsEqualTo(branchTaken ? .4f : 1f);
        await Assert.That(actor.Entry.Phase.Pipes.Executor.ActiveGoalCount).IsEqualTo(1);
        await Assert.That(actor.HasActor).IsTrue();
    }

    [Test]
    public async Task DeferredModelAdmissionReplaysPostEnterEffectsAfterTheActualInitializationFlag()
    {
        using var fixture = new ManagedNpcRuntimeTests.Fixture(withMovement: true, movementBuffContent: BuffContent());
        var npc = fixture.Admission.Npc(model: fixture.Model, heading: new(), cleanSkillState: true);
        fixture.Admission.Publish(npc);
        await Assert.That(fixture.Admission.Registry.CreateBuff(Created(npc.ObjectId, 604).ToFrame().Body.Span)).IsTrue();
        await Assert.That(fixture.Admission.Registry.RemoveBuff(new BuffRemoved(npc.ObjectId, 1).ToFrame().Body.Span)).IsTrue();
        var reapplied = Created(npc.ObjectId, 604) with { Index = 2 };
        await Assert.That(fixture.Admission.Registry.CreateBuff(reapplied.ToFrame().Body.Span)).IsTrue();
        fixture.Admission.World.Initialize(npc, null);
        var actor = fixture.Runtime.Actors.Find(fixture.Admission.Catalog.TryGet(npc)!.Value)!;
        await Assert.That(actor).IsNotNull();
        var model = fixture.Motion!.CopyMovementEligibilityState(actor);
        await Assert.That(model.ModelFlags & 1).IsEqualTo(0u);
        await Assert.That(model.IsPsychokinesisTarget).IsTrue();
        await Assert.That(fixture.Runtime.Actors.MovementEligibility!.CanMove(actor)).IsFalse();
        await Assert.That(fixture.Admission.Registry.RemoveBuff(new BuffRemoved(npc.ObjectId, 2).ToFrame().Body.Span)).IsTrue();
        await Assert.That(fixture.Runtime.Actors.MovementEligibility.CanMove(actor)).IsTrue();
    }

    private static BuffCreated Created(uint unit, uint template) =>
        new(new(ZoneCasterKind.Unit, unit), 0, unit, 1, template, 1, 1, 0, 1, 1, 1000, 0, 0);

    private static ZoneBuffContent BuffContent() => new([
        new(601, 2, 6, 10, 10, 0, false, false, Stun: true),
        new(602, 3, 6, 10, 10, 0, false, false, Sleep: true),
        new(603, 1, 6, 10, 10, 0, false, false, Root: true),
        new(604, 2, 6, 10, 10, 0, false, false, Psychokinesis: true, PsychokinesisSpeed: 2)
    ], [], [], [], [], []);
}
