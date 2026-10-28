using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Content;
using AAEmu.Zone.Npcs;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Protocol.Units;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.Tests;

public sealed class ManagedNpcPsychokinesisTests
{
    [Test]
    public async Task ConstructorAndNpcInitializationOwnSeparateModelFlagAndActorLatchFields()
    {
        using var world = new ManagedNpcActorMotionTests.World();
        await Assert.That(world.Motion.CopyMovementEligibilityState(world.Actor)).IsEqualTo(new ZoneManagedNpcMovementEligibilityState(0, false));
        await Assert.That(world.Motion.CopyPsychokinesisState(world.Actor)).IsEqualTo(new ZoneManagedNpcPsychokinesisState(false, 0, 0));
        world.Motion.CompleteNpcInitialization(world.Actor);
        await Assert.That(world.Motion.CopyMovementEligibilityState(world.Actor)).IsEqualTo(new ZoneManagedNpcMovementEligibilityState(1, false));
        world.Actor.SetEntityActive(false);
        world.Actor.SetActive(false);
        await Assert.That(world.Motion.CopyMovementEligibilityState(world.Actor)).IsEqualTo(new ZoneManagedNpcMovementEligibilityState(1, false));
    }

    [Test]
    public async Task ActualBuffHandlersPreserveOverlappingRemovalRefreshAndUpdateOrdering()
    {
        using var world = new World();
        var originalBody = world.Body.CopyState();
        await Assert.That(world.Create(1, 101)).IsTrue();
        await Assert.That(world.Create(2, 202)).IsTrue();
        await Assert.That(world.Motion.CopyPsychokinesisState(world.Actor)).IsEqualTo(new ZoneManagedNpcPsychokinesisState(true, 2300, 2));
        await Assert.That(world.Motion.CopyMovementEligibilityState(world.Actor)).IsEqualTo(new ZoneManagedNpcMovementEligibilityState(0, true));
        await Assert.That(world.Remove(101)).IsTrue();
        await Assert.That(world.Actor.Npc.Unit.Buffs.Good.Single().TemplateId).IsEqualTo(2u);
        //The second buff remains; native removal still clears the latch and restores flag1.
        await Assert.That(world.Motion.CopyPsychokinesisState(world.Actor)).IsEqualTo(new ZoneManagedNpcPsychokinesisState(false, 0, 3));
        await Assert.That(world.Motion.CopyMovementEligibilityState(world.Actor)).IsEqualTo(new ZoneManagedNpcMovementEligibilityState(1, false));
        var update = new BuffUpdated(world.Actor.Npc.ObjectId, 202, 9, 3, 100, 0);
        await Assert.That(world.Fixture.Admission.Registry.UpdateBuff(update.ToFrame().Body.Span)).IsTrue();
        await Assert.That(world.Motion.CopyPsychokinesisState(world.Actor)).IsEqualTo(new ZoneManagedNpcPsychokinesisState(false, 0, 3));
        await Assert.That(world.Create(2, 303)).IsTrue(); //Refresh invokes the model effect again.
        await Assert.That(world.Actor.Npc.Unit.Buffs.Good.Count).IsEqualTo(1);
        await Assert.That(world.Motion.CopyPsychokinesisState(world.Actor)).IsEqualTo(new ZoneManagedNpcPsychokinesisState(true, 2300, 4));
        await Assert.That(world.Remove(303)).IsTrue();
        await Assert.That(world.Motion.CopyPsychokinesisState(world.Actor)).IsEqualTo(new ZoneManagedNpcPsychokinesisState(false, 0, 5));
        await Assert.That(world.Motion.CopyMovementEligibilityState(world.Actor)).IsEqualTo(new ZoneManagedNpcMovementEligibilityState(1, false));
        await Assert.That(world.Body.CopyState()).IsEqualTo(originalBody);
    }

    [Test]
    public async Task CachedModelResetPreservesActualFallingVelocityAndPhysicalPose()
    {
        using var world = new World();
        var destination = world.Body.CopyState().Status.Position + new Vector3(0, 0, 2);
        new ZoneManagedNpcRelocation(world.Fixture.Bodies!).Relocate(world.Actor, destination, result =>
        {
            world.Actor.Npc.Unit.CommitPose(new(ZoneMovementPoseMath.ToUnitWorldPosition(result.Current.Status.Position),
                result.Current.Status.Orientation));
            world.Fixture.Admission.Registry.UpdateSpatialMembership(world.Actor.Npc.Unit);
            if (world.Fixture.Components.PublishPose(world.Actor.Handle).Count != 0)
                throw new InvalidOperationException("The relocated fixture lost its real model publication.");
        });
        world.Fixture.Backend!.Step(TimeSpan.FromMilliseconds(20));
        var physical = world.Body.CopyState();
        await Assert.That(physical.Status.Velocity.Z).IsLessThan(0f);
        await Assert.That(physical.Control.RequestedVelocity).IsEqualTo(Vector3.Zero);
        await Assert.That(world.Create(1, 101)).IsTrue();
        await Assert.That(world.Body.CopyState()).IsEqualTo(physical);
        await Assert.That(world.Motion.CopyPsychokinesisState(world.Actor)).IsEqualTo(new ZoneManagedNpcPsychokinesisState(true, 1250, 1));
    }

    [Test]
    [Arguments("actor-output")]
    [Arguments("look-target")]
    [Arguments("queued-wish")]
    [Arguments("nonfinite-speed")]
    public async Task UnownedEffectCommitsWorldBuffButRetiresOnlyItsPoisonedGenerationAtTheNextSafeBoundary(string input)
    {
        using var world = new World(input == "nonfinite-speed" ? float.NaN : 1250);
        var healthy = world.AddOtherActor();
        var pose = world.Fixture.Poses!.Find(world.Actor.Handle)!;
        await Assert.That(pose.Last!.ActorMovementOutput).IsEqualTo(Vector3.Zero);
        await Assert.That(pose.Last.ControllerLookTarget).IsEqualTo(Vector3.Zero);
        if (input == "actor-output") pose.CopyActorMovementOutput(new(1, 2, 3));
        if (input == "look-target") pose.CopyControllerLookTarget(new(1100, 1110, 12));
        if (input == "queued-wish") world.Body.RequestVelocity(new(1, 0, 0));
        //Last deliberately still holds the previous sample: the validator must read live fields.
        await Assert.That(pose.Last.ActorMovementOutput).IsEqualTo(Vector3.Zero);
        await Assert.That(pose.Last.ControllerLookTarget).IsEqualTo(Vector3.Zero);
        await Assert.That(world.Create(1, 101)).IsTrue();
        await Assert.That(world.Actor.Npc.Unit.Buffs.Good.Single().TemplateId).IsEqualTo(1u);
        await Assert.That(world.Fixture.Runtime.Count).IsEqualTo(2);
        await Assert.That(() => world.Motion.CopyMovementEligibilityState(world.Actor)).Throws<NotSupportedException>();
        var faults = world.Fixture.Runtime.PrePhysics(Frame(11), world.Fixture.Admission.Provider.RemoveNpc);
        await Assert.That(faults.Count).IsEqualTo(1);
        await Assert.That(faults[0].Actor).IsEqualTo(world.Actor.Handle);
        await Assert.That(faults[0].Callback).IsEqualTo("model psychokinesis buff effect");
        await Assert.That(world.Fixture.Runtime.Actors.Find(world.Actor.Handle)).IsNull();
        await Assert.That(world.Body.IsDisposed).IsTrue();
        await Assert.That(world.Fixture.Runtime.Actors.Find(healthy.Handle)).IsEqualTo(healthy);
        var healthyBody = world.Fixture.Bodies!.Find(healthy.Handle)!;
        healthyBody.RequestStanceSlot(3);
        world.Fixture.Bodies.PrepareFrame();
        world.Fixture.Backend!.Step(TimeSpan.FromMilliseconds(20));
        await Assert.That(healthyBody.CurrentStanceSlot).IsEqualTo(3);
        await Assert.That(healthyBody.IsCurrent).IsTrue();
        await Assert.That(world.Motion.DrainFaults().Count).IsEqualTo(0);
    }

    [Test]
    public async Task RetirementDetachesTheOldUnitEffectReceiverBeforeSameNumericIdentityReentry()
    {
        using var world = new World();
        await Assert.That(world.Create(1, 101)).IsTrue();
        var previous = world.Actor;
        var oldUnit = previous.Npc.Unit;
        var effect = oldUnit.CopyBuffModelEffectJournal().Single();
        var remove = new ZoneWriter(); remove.WriteBc(previous.Npc.ObjectId);
        await Assert.That(world.Fixture.Admission.Registry.Remove(remove.ToArray())).IsTrue();
        var replacement = world.Fixture.Admission.CreateFromWire(world.Fixture.Model.Id, new(1100, 1100, 10.1f));
        var handle = world.Fixture.Admission.Catalog.TryGet(replacement)!.Value;
        var actor = world.Fixture.Runtime.Actors.Find(handle)!;
        await Assert.That(handle.Generation).IsGreaterThan(previous.Handle.Generation);
        oldUnit.ApplyBuffEffect(effect with { Kind = ZoneUnitBuffEffectKind.Remove });
        await Assert.That(world.Motion.CopyMovementEligibilityState(actor)).IsEqualTo(new ZoneManagedNpcMovementEligibilityState(1, false));
        await Assert.That(world.Motion.CopyPsychokinesisState(actor)).IsEqualTo(new ZoneManagedNpcPsychokinesisState(false, 0, 0));
        await Assert.That(() => world.Motion.CopyMovementEligibilityState(previous)).Throws<ObjectDisposedException>();
        await Assert.That(world.Motion.DrainFaults().Count).IsEqualTo(0);
    }

    private static ZoneMovementFrame Frame(uint number) => new(number, TimeSpan.FromMilliseconds(20), 1000 + number, number * .02f)
        { LocalGameMilliseconds = number * 20, UnpausedMilliseconds = 7000 + number };

    private sealed class World : IDisposable
    {
        public ManagedNpcRuntimeTests.Fixture Fixture { get; }
        public ZoneManagedNpcActor Actor { get; }
        public ZoneManagedNpcBodyLease Body { get; }
        public ZoneManagedNpcActorMotion Motion => Fixture.Motion!;
        public World(float speed = 1250)
        {
            var buffs = new ZoneBuffContent([
                new(1, 1, 1, 100, 100, 0, false, false, Psychokinesis: true, PsychokinesisSpeed: speed),
                new(2, 1, 1, 100, 100, 0, false, false, Psychokinesis: true, PsychokinesisSpeed: 2300)
            ], [], [], [], [], []);
            Fixture = new(withMovement: true, movementBuffContent: buffs);
            var npc = Fixture.Admission.CreateFromWire(Fixture.Model.Id, new(1100, 1100, 10.1f));
            var handle = Fixture.Admission.Catalog.TryGet(npc)!.Value;
            Actor = Fixture.Runtime.Actors.Find(handle) ?? throw new InvalidOperationException(Fixture.Admission.Catalog.GetAdmission(handle)?.Refusal);
            Body = Fixture.Bodies!.Find(handle)!;
        }
        public bool Create(uint template, uint index) => Fixture.Admission.Registry.CreateBuff(new BuffCreated(
            new(ZoneCasterKind.Unit, Actor.Npc.ObjectId), 0, Actor.Npc.ObjectId, index, template, 1, 1, 0, 1, 0, 0, 0, 0).ToFrame().Body.Span);
        public bool Remove(uint index) => Fixture.Admission.Registry.RemoveBuff(new BuffRemoved(Actor.Npc.ObjectId, index).ToFrame().Body.Span);
        public ZoneManagedNpcActor AddOtherActor()
        {
            var id = ManagedNpcAdmissionTests.Fixture.FreshObjectId();
            var npc = Fixture.Admission.Npc(id, id + 1_000_000, Fixture.Model, heading: new(), cleanSkillState: true);
            npc.Unit.CommitPose(new(ZoneMovementPoseMath.ToUnitWorldPosition(new(1110, 1100, 10.1f)), Quaternion.Identity));
            Fixture.Admission.Publish(npc);
            Fixture.Admission.World.Initialize(npc, null);
            var handle = Fixture.Admission.Catalog.TryGet(npc)!.Value;
            return Fixture.Runtime.Actors.Find(handle) ?? throw new InvalidOperationException(Fixture.Admission.Catalog.GetAdmission(handle)?.Refusal);
        }
        public void Dispose() => Fixture.Dispose();
    }
}
