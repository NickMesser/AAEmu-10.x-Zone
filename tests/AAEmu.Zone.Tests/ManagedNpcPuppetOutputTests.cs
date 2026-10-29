using System.Numerics;
using System.Diagnostics.CodeAnalysis;
using AAEmu.Zone.Ai;
using AAEmu.Zone.Combat;
using AAEmu.Zone.Content;
using AAEmu.Zone.Hosting;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Protocol.Units;
using AAEmu.Zone.Scene;

namespace AAEmu.Zone.Tests;

[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "CreateActor returns the exact actor borrowed from the disposed fixture's Runtime; Runtime owns and retires that generation.")]
public sealed class ManagedNpcPuppetOutputTests
{
    [Test]
    public async Task BeforeGoalsRetainsSpeedAndCurrentGoalFieldsReachTheCopiedProxyStateWithoutMovingTheBody()
    {
        using var fixture = CreateFixture();
        var actor = CreateActor(fixture);
        using var output = Output(fixture); using var lease = output.Bind(actor);
        var before = fixture.Bodies!.Find(actor.Handle)!.CopyState().Status;
        actor.Controls.MovementDirection = Vector3.UnitX; actor.Controls.DesiredSpeed = 3;
        actor.Controls.MovementUrgency = .21f; actor.Controls.GoalMovementUrgency = 1;
        output.BeforeGoals(actor);
        await Assert.That(actor.Controls.MovementDirection).IsEqualTo(Vector3.Zero);
        await Assert.That(actor.Controls.DesiredSpeed).IsEqualTo(3f);
        actor.Controls.MovementDirection = Vector3.UnitY;
        actor.Controls.DistanceToPathEnd = 12;
        actor.Controls.SetDelayedStance(5); actor.Controls.DelayedStanceMovementCounter = 3;
        var state = output.BuildBeforeProxy(actor);
        await Assert.That(state.BodyState).IsEqualTo(5);
        await Assert.That(state.DesiredSpeed).IsEqualTo(3f);
        await Assert.That(state.MovementUrgency).IsEqualTo(1f);
        await Assert.That(state.MoveDirection).IsEqualTo(Vector3.UnitY);
        await Assert.That(state.LookTarget).IsEqualTo(actor.AiObject.Position + new Vector3(0, 40, 0));
        await Assert.That(state.BodyTarget).IsEqualTo(Vector3.Zero);
        await Assert.That(state.MovementContext).IsEqualTo(0);
        await Assert.That(output.CopyStuckMovementInput(actor)).IsEqualTo(new ZoneManagedNpcPuppetMovementState(Vector3.UnitY, 3));
        await Assert.That(fixture.Bodies.Find(actor.Handle)!.CopyState().Status).IsEqualTo(before);
        await Assert.That(lease.BuildCount).IsEqualTo(1ul);
        await Assert.That(() => output.BuildBeforeProxy(actor)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task OriginalLookAtGoalOwnsTheLiveEnableFlagAndHandlerRecopyDoesNotRepeatTheLookPhase()
    {
        using var fixture = CreateFixture(); var actor = CreateActor(fixture);
        using var output = Output(fixture); using var lease = output.Bind(actor);
        actor.Points!.Reference.SetTransform(actor.AiObject.Position + new Vector3(5, 0, 0), Vector3.UnitY);
        actor.Services.SetArgument(actor.Points.Reference.State);
        using var goal = new ZoneLookAtGoal(new() { Integer = 2, Boolean = true }, actor.Services);
        output.BeforeGoals(actor);
        await Assert.That(goal.Execute()).IsEqualTo(1);
        var beforeHandler = output.BuildBeforeProxy(actor);
        await Assert.That(beforeHandler.LookTargetEnabled).IsTrue();
        await Assert.That(beforeHandler.LookTarget).IsEqualTo(actor.Points.Reference.State.Position);
        goal.Reset();
        actor.Controls.BodyState = 7; actor.Controls.DesiredSpeed = 9; actor.Controls.MovementDirection = Vector3.UnitX;
        var afterHandler = output.CopyStateForProxy(actor);
        await Assert.That(afterHandler.LookTargetEnabled).IsFalse();
        await Assert.That(afterHandler.LookTarget).IsEqualTo(beforeHandler.LookTarget);
        await Assert.That(afterHandler.BodyState).IsEqualTo(7);
        await Assert.That(afterHandler.MoveDirection).IsEqualTo(Vector3.UnitX);
        await Assert.That(afterHandler.DesiredSpeed).IsEqualTo(9f);
        await Assert.That(lease.BuildCount).IsEqualTo(1ul);
    }

    [Test]
    public async Task ActorIntervalUsesActualSkippedClockTimeAndItsHalfSecondCapIndependentlyOfGlobalLookDelta()
    {
        using var fixture = CreateFixture(); var actor = CreateActor(fixture);
        using var output = Output(fixture); using var lease = output.Bind(actor);
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        output.BeforeGoals(actor); output.BuildBeforeProxy(actor);
        await Assert.That(output.CopyTimePassed(actor)).IsEqualTo(0f);
        await Assert.That(lease.LastUpdateMicroseconds).IsEqualTo(5_000_000L);
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(150));
        output.BeforeGoals(actor); output.BuildBeforeProxy(actor);
        await Assert.That(output.CopyTimePassed(actor)).IsEqualTo(150000f * .000001f);
        await Assert.That(fixture.Runtime.Actors.FrameTimeSeconds).IsEqualTo(.02f);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        output.BeforeGoals(actor); output.BuildBeforeProxy(actor);
        await Assert.That(output.CopyTimePassed(actor)).IsEqualTo(.5f);
    }

    [Test]
    public async Task ProxyToggleKeepsLookCalculationButDoesNotApplyTheProxyOnlyDebugStanceOverride()
    {
        using var fixture = CreateFixture(); var actor = CreateActor(fixture);
        using var output = Output(fixture, ZoneEngineCVars.Parse("ai_UpdateProxy=0\nai_ForceStance=7\nai_ForceAllowStrafing=1"));
        using var lease = output.Bind(actor);
        output.BeforeGoals(actor); actor.Controls.BodyState = 1; actor.Controls.MovementDirection = Vector3.UnitY; actor.Controls.DesiredSpeed = 3;
        var state = output.BuildBeforeProxy(actor);
        await Assert.That(output.ProxyUpdatesEnabled).IsFalse();
        await Assert.That(state.BodyState).IsEqualTo(1);
        await Assert.That(state.AllowStrafing).IsFalse();
        await Assert.That(state.LookTarget).IsNotEqualTo(Vector3.Zero);
    }

    [Test]
    public async Task ActualSourceDebugOverridesHappenAfterBodyTargetSelection()
    {
        using var fixture = CreateFixture(); var actor = CreateActor(fixture);
        using var output = Output(fixture, ZoneEngineCVars.Parse("ai_ForceStance=7\nai_ForceAllowStrafing=1"));
        using var lease = output.Bind(actor);
        output.BeforeGoals(actor); actor.Controls.MovementDirection = Vector3.UnitY; actor.Controls.DesiredSpeed = 3;
        var state = output.BuildBeforeProxy(actor);
        await Assert.That(state.BodyState).IsEqualTo(7);
        await Assert.That(state.AllowStrafing).IsTrue();
        await Assert.That(state.BodyTarget).IsEqualTo(Vector3.Zero);
    }

    [Test]
    public async Task RetiredNavigationCallbackCannotPublishUrgencyOrDelayedStanceToItsOldGeneration()
    {
        using var fixture = CreateFixture(); var actor = CreateActor(fixture);
        using var output = new ZoneManagedNpcPuppetOutput(fixture.Runtime.Actors, fixture.Poses!, fixture.MovementAbility!, fixture.Motion!,
            new(fixture.MovementStats!), current => { current.Dispose(); return null; });
        using var lease = output.Bind(actor);
        output.BeforeGoals(actor);
        actor.Controls.MovementUrgency = .21f; actor.Controls.GoalMovementUrgency = 1;
        actor.Controls.MovementDirection = Vector3.UnitY; actor.Controls.DesiredSpeed = 3;
        actor.Controls.SetDelayedStance(5); actor.Controls.DelayedStanceMovementCounter = 3;
        await Assert.That(() => output.BuildBeforeProxy(actor)).Throws<ObjectDisposedException>();
        await Assert.That(actor.Controls.MovementUrgency).IsEqualTo(.21f);
        await Assert.That(actor.Controls.DelayedStance).IsEqualTo(5);
        await Assert.That(lease.BuildCount).IsEqualTo(0ul);
    }

    [Test]
    public async Task ExactUnitRetirementInvalidatesOutputAndAReplacementStartsWithIndependentTargetHistory()
    {
        using var fixture = CreateFixture(); var actor = CreateActor(fixture);
        using var output = Output(fixture); using var old = output.Bind(actor);
        output.BeforeGoals(actor); actor.Controls.MovementDirection = Vector3.UnitY; actor.Controls.DesiredSpeed = 3;
        output.BuildBeforeProxy(actor);
        var writer = new ZoneWriter(); writer.WriteBc(actor.Npc.ObjectId);
        await Assert.That(fixture.Admission.Registry.Remove(writer.ToArray())).IsTrue();
        await Assert.That(output.Find(actor.Handle)).IsNull();
        await Assert.That(() => output.CopyStateForProxy(actor)).Throws<ObjectDisposedException>();
        old.Dispose();
        var next = CreateActor(fixture); using var replacement = output.Bind(next);
        await Assert.That(next.Handle.Generation).IsGreaterThan(actor.Handle.Generation);
        output.BeforeGoals(next);
        var state = output.BuildBeforeProxy(next);
        await Assert.That(state.LookTarget).IsEqualTo(Vector3.Zero);
        await Assert.That(state.BodyTarget).IsEqualTo(Vector3.Zero);
        await Assert.That(replacement.LastUpdateMicroseconds).IsEqualTo(0L);
    }

    [Test]
    public async Task TimingOnlyCollectionReplacementDoesNotRefuseAnOtherwiseKnownPuppet()
    {
        using var fixture = CreateFixture(); var actor = CreateActor(fixture);
        using var output = Output(fixture); using var lease = output.Bind(actor);
        actor.Npc.Unit.CommitBuffTiming(new([], [], []), actor.Npc.Unit.Stats);
        output.BeforeGoals(actor);
        output.BuildBeforeProxy(actor);
        await Assert.That(lease.BuildCount).IsEqualTo(1ul);
    }

    [Test]
    [Arguments(7987u)]
    [Arguments(8173u)]
    [Arguments(8177u)]
    public async Task ActualHarmlessOriginalBuffDescriptorsAndLaterTimingCopiesRetainTheOwnedTurnPermission(uint recordedTemplate)
    {
        var templates = new uint[] { 1965, 6613, 3590 }.Select(id => new BuffStatTemplate(id, id == 1965 ? (byte)1 : (byte)3,
            1, 1, 0, 0, false, false)).ToArray();
        var content = new ZoneBuffContent(templates, [], [], [], [], []);
        var initial = recordedTemplate == 7987 ? new UnitBuffs([Restored(1965, 1)], [], []) :
            new([], [], [Restored(6613, 1), Restored(3590, 2)]);
        using var fixture = CreateFixture(content); var actor = CreateActor(fixture, initial);
        using var output = Output(fixture); using var lease = output.Bind(actor);
        ConfigureStrafing(actor);
        await Assert.That(actor.Npc.Unit.Buffs.Good.Count + actor.Npc.Unit.Buffs.Hidden.Count).IsEqualTo(recordedTemplate == 7987 ? 1 : 2);
        await Assert.That(fixture.Admission.Registry.UpdateBuff(new BuffUpdated(actor.Npc.ObjectId, 1, 0, 0, 100, 0).ToFrame().Body.Span)).IsTrue();
        var before = actor.Npc.Unit.Buffs;
        actor.Npc.Unit.CommitBuffTiming(ZoneBuffTiming.Advance(before, content, 33).Buffs, actor.Npc.Unit.Stats);
        await Assert.That(ReferenceEquals(before, actor.Npc.Unit.Buffs)).IsFalse();
        for (var index = 0; index < 3; index++)
        {
            output.BeforeGoals(actor);
            await Assert.That(output.BuildBeforeProxy(actor).AllowStrafing).IsTrue();
            fixture.Clock.Advance(TimeSpan.FromMilliseconds(33));
        }
        await Assert.That(lease.BuildCount).IsEqualTo(3ul);
    }

    [Test]
    public async Task ActualImpossibleRotateLifecycleControlsPermissionAndDeathDoesNotEraseTheRetainedCounter()
    {
        var template = new BuffStatTemplate(400, 3, 4, 255, 255, 0, false, false, ImpossibleRotate: true);
        using var fixture = CreateFixture(new([template], [], [], [], [], [])); var actor = CreateActor(fixture);
        using var output = Output(fixture); using var lease = output.Bind(actor);
        var registry = fixture.Admission.Registry; var unit = actor.Npc.Unit;
        ConfigureStrafing(actor);
        await Check(true);
        registry.CreateBuff(Created(actor, 400, 1).ToFrame().Body.Span);
        await Check(false);
        registry.CreateBuff(Created(actor, 400, 2).ToFrame().Body.Span);
        await Assert.That(unit.MovementStatus.CopySnapshot().ImpossibleRotateCounter).IsEqualTo((byte)1);
        registry.UpdateBuff(new BuffUpdated(actor.Npc.ObjectId, 2, 0, 0, 1000, 0).ToFrame().Body.Span);
        await Check(false);
        registry.Kill(new UnitDeath(actor.Npc.ObjectId).ToFrame().Body.Span);
        await Assert.That(unit.MovementStatus.CopySnapshot().ImpossibleRotateCounter).IsEqualTo((byte)1);
        await Check(true);
        registry.UpdateBuff(new BuffUpdated(actor.Npc.ObjectId, 2, 0, 0, 2000, 0).ToFrame().Body.Span);
        await Check(false);
        registry.RemoveBuff(new BuffRemoved(actor.Npc.ObjectId, 2).ToFrame().Body.Span);
        await Check(true);
        async Task Check(bool expected)
        {
            output.BeforeGoals(actor);
            await Assert.That(output.BuildBeforeProxy(actor).AllowStrafing).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task AFailedActualEffectStillRefusesThePuppetInsteadOfPublishingFalseClearFlags()
    {
        var template = new BuffStatTemplate(400, 1, 1, 1, 1, 0, false, false, ImpossibleRotate: true);
        using var fixture = CreateFixture(new([template], [], [], [], [], [])); var actor = CreateActor(fixture);
        using var output = Output(fixture); using var lease = output.Bind(actor);
        actor.Npc.Unit.BuffEffect += (_, _) => throw new NotSupportedException("A controlled unowned model-effect consumer.");
        await Assert.That(() => fixture.Admission.Registry.CreateBuff(Created(actor, 400, 1).ToFrame().Body.Span)).Throws<NotSupportedException>();
        output.BeforeGoals(actor);
        await Assert.That(() => output.BuildBeforeProxy(actor)).Throws<NotSupportedException>();
        await Assert.That(lease.BuildCount).IsEqualTo(0ul);
    }

    private static BuffCreated Created(ZoneManagedNpcActor actor, uint template, uint index) =>
        new(new(ZoneCasterKind.Unit, actor.Npc.ObjectId), 1, actor.Npc.ObjectId, index, template, 1, 1, 0, 1, 1, 100, 0, 10);
    private static UnitBuff Restored(uint template, uint index) =>
        new(index, new(ZoneCasterKind.Unit, 0), 1, 1, 1, 0, 0, 0, 0, template, 0, 0, 0);
    private static void ConfigureStrafing(ZoneManagedNpcActor actor)
    {
        actor.Navigation.OriginalPath.PushBack(new(actor.AiObject.Position));
        actor.Navigation.OriginalPath.PushBack(new(actor.AiObject.Position + Vector3.UnitY));
        actor.Controls.AllowedStrafeDistances = new(0, 2, false); actor.Controls.DistanceToPathEnd = 1;
    }
    private static ManagedNpcRuntimeTests.Fixture CreateFixture(ZoneBuffContent? buffs = null)
    {
        var fixture = new ManagedNpcRuntimeTests.Fixture(withMovementAbility: true, movementBuffContent: buffs);
        ManagedNpcMovementAbilityTests.InstallTables(fixture); return fixture;
    }
    private static ZoneManagedNpcActor CreateActor(ManagedNpcRuntimeTests.Fixture fixture, UnitBuffs? initialBuffs = null)
    {
        var npc = fixture.Admission.CreateFromWire(fixture.Model.Id, new(1100, 1100, 10.1f), initialBuffs);
        return fixture.Runtime.Actors.Find(fixture.Admission.Catalog.TryGet(npc)!.Value)!;
    }
    private static ZoneManagedNpcPuppetOutput Output(ManagedNpcRuntimeTests.Fixture fixture, ZoneEngineCVars? cvars = null) =>
        new(fixture.Runtime.Actors, fixture.Poses!, fixture.MovementAbility!, fixture.Motion!, new ZoneStatCalculator(fixture.MovementStats!), engineCVars: cvars);
}
