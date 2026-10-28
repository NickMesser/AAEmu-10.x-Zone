using System.Numerics;
using System.Text.Json;
using AAEmu.Zone.Ai;
using AAEmu.Zone.Scene;

namespace AAEmu.Zone.Tests;

public sealed class ManagedNpcPointObjectsTests
{
    [Test]
    public async Task DirectionAndSimilarityMatchExecutedOriginalInstructionsIncludingTinyAndBoundaryInputs()
    {
        using var input = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("AAEmu.Zone.Tests.Fixtures.npc-reference-native-controls.json")!;
        using var corpus = await JsonDocument.ParseAsync(input);
        using var fixture = new ManagedNpcActorSceneTests.Fixture(); var actor = fixture.Admit();
        var checkedCount = 0;
        foreach (var control in corpus.RootElement.GetProperty("controls").EnumerateArray())
        {
            if (control.GetProperty("kind").GetString() == "direction")
            {
                fixture.Scene.Points.SetReferencePosition(actor, new(7, 8, 9));
                var before = actor.Points!.Reference.State.MoveDirection;
                fixture.Scene.Points.SetReferenceDirection(actor, Vector(control.GetProperty("direction")));
                var calls = control.GetProperty("calls").EnumerateArray().ToArray();
                var expected = calls.Length == 0 ? before : Vector(calls[0].GetProperty("direction"));
                var actual = actor.Points.Reference.State.MoveDirection;
                await Assert.That(BitConverter.SingleToInt32Bits(actual.X)).IsEqualTo(BitConverter.SingleToInt32Bits(expected.X));
                await Assert.That(BitConverter.SingleToInt32Bits(actual.Y)).IsEqualTo(BitConverter.SingleToInt32Bits(expected.Y));
                await Assert.That(BitConverter.SingleToInt32Bits(actual.Z)).IsEqualTo(BitConverter.SingleToInt32Bits(expected.Z));
                await Assert.That(actor.Points.Reference.State.Position).IsEqualTo(new Vector3(7, 8, 9));
            }
            else
            {
                actor.Points!.Reference.State.MoveDirection = Vector(control.GetProperty("reference"));
                actor.AiObject.BodyDirection = Vector(control.GetProperty("view")); //Oracle slot128 is actual body direction144.
                actor.AiObject.ViewDirection = -actor.AiObject.BodyDirection;
                await Assert.That(fixture.Scene.Points.IsSimilarReferenceDirection(actor)).IsEqualTo(control.GetProperty("result").GetBoolean());
            }
            checkedCount++;
        }
        await Assert.That(checkedCount).IsEqualTo(18);
        static Vector3 Vector(JsonElement vector) => new(vector[0].GetSingle(), vector[1].GetSingle(), vector[2].GetSingle());
    }

    [Test]
    public async Task RealDummyInventoryIsSeparateFromNearestObjectTypesAndRetiresWithItsActor()
    {
        using var fixture = new ManagedNpcActorSceneTests.Fixture();
        var actor = fixture.Admit(); var points = actor.Points!;
        await Assert.That(fixture.Scene.Points.DummyCount).IsEqualTo(2);
        await Assert.That(fixture.Scene.Objects.Count).IsEqualTo(1);
        await Assert.That(fixture.Scene.Objects.FindNearest(Vector3.Zero, 0, 100)).IsNull();
        await Assert.That(points.Reference.Name).IsEqualTo(actor.Npc.Unit.Snapshot.Name + "_RefPoint");
        await Assert.That(points.Look.Name).IsEqualTo(actor.Npc.Unit.Snapshot.Name + "_LookAt");
        await Assert.That(points.Reference.SubType).IsEqualTo(2);
        await Assert.That(points.Look.SubType).IsEqualTo(3);
        await Assert.That(points.Reference.State.EntityId).IsEqualTo(0u);
        await Assert.That(points.Reference.State.Position).IsEqualTo(Vector3.Zero);
        await Assert.That(points.Reference.State.BodyDirection).IsEqualTo(Vector3.Zero);
        await Assert.That(points.Look.State.BodyDirection).IsEqualTo(Vector3.UnitX);
        fixture.Scene.Remove(actor.Handle);
        await Assert.That(points.IsDisposed).IsTrue();
        await Assert.That(points.Reference.State.Enabled).IsFalse();
        await Assert.That(fixture.Scene.Points.DummyCount).IsEqualTo(0);
        await Assert.That(() => fixture.Scene.Points.SetReferencePosition(actor, Vector3.One)).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task ReferencePositionCopiesActualMoveDirectionAndNotifiesOnlyTheInnermostPipe()
    {
        using var fixture = new ManagedNpcActorSceneTests.Fixture(); var actor = fixture.Admit();
        var pipe = fixture.Scene.GoalPipes.Create("point_listener")!;
        actor.Entry.Phase.Pipes.Select(ZoneGoalPipeFlags.RunOnce, pipe.Name, eventId: 42);
        var events = new List<(ZoneGoalPipeEvent Kind, int Id)>();
        using var listener = new ZoneGoalPipeListener((_, kind, id) => events.Add((kind, id)));
        actor.Entry.Phase.Pipes.RegisterListener(listener, 42);
        actor.AiObject.BodyDirection = -Vector3.UnitX;
        actor.AiObject.MoveDirection = new(2, 3, 4);
        fixture.Scene.Points.SetReferencePosition(actor, new(.0005f, 0, 0));
        await Assert.That(events.Count).IsEqualTo(0);
        await Assert.That(actor.Points!.Reference.State.BodyDirection).IsEqualTo(new Vector3(2, 3, 4));
        await Assert.That(actor.Points.Reference.State.MoveDirection).IsEqualTo(new Vector3(2, 3, 4));
        fixture.Scene.Points.SetReferencePosition(actor, new(1, 2, 3));
        await Assert.That(events.Single()).IsEqualTo((ZoneGoalPipeEvent.ReferencePointMoved, 42));
        await Assert.That(actor.Points.Reference.PreviousPosition).IsEqualTo(new Vector3(.0005f, 0, 0));
        await Assert.That(actor.Points.Reference.PositionChanged).IsTrue();
        await Assert.That(fixture.Scene.RecentEvents.Count(e => e.Kind == 19)).IsEqualTo(2);
    }

    [Test]
    public async Task DirectionUsesTheOwnedReferenceAndZeroDoesNotEraseIt()
    {
        using var fixture = new ManagedNpcActorSceneTests.Fixture(); var actor = fixture.Admit();
        fixture.Scene.Points.SetReferencePosition(actor, new(7, 8, 9));
        fixture.Scene.Points.SetReferenceDirection(actor, new(4, 0, 3));
        var direction = actor.Points!.Reference.State.ViewDirection;
        await Assert.That(direction).IsEqualTo(new Vector3(.8f, 0, .6f));
        await Assert.That(actor.Points.Reference.State.Position).IsEqualTo(new Vector3(7, 8, 9));
        actor.AiObject.BodyDirection = new(8, 0, 99);
        actor.AiObject.ViewDirection = Vector3.UnitY;
        await Assert.That(fixture.Scene.Points.IsSimilarReferenceDirection(actor)).IsTrue();
        fixture.Scene.Points.SetReferenceDirection(actor, Vector3.Zero);
        await Assert.That(actor.Points.Reference.State.ViewDirection).IsEqualTo(direction);
        actor.AiObject.BodyDirection = Vector3.UnitY;
        await Assert.That(fixture.Scene.Points.IsSimilarReferenceDirection(actor)).IsFalse();
    }

    [Test]
    public async Task LocateAndLookGoalsConsumeRealPointsWithoutMovingTheUnit()
    {
        using var fixture = new ManagedNpcActorSceneTests.Fixture(); var actor = fixture.Admit();
        var original = actor.Npc.Unit.Pose;
        actor.AiObject.MoveDirection = Vector3.UnitY;
        actor.AiObject.BodyDirection = Vector3.UnitY;
        fixture.Scene.Points.SetReferenceDirection(actor, Vector3.UnitX);
        var pipe = fixture.Scene.GoalPipes.Create("point_look")!;
        Append(pipe, ZoneGoalOperation.Locate, new() { Text = "refpoint" });
        Append(pipe, ZoneGoalOperation.LookAt, new() { Text = "refpointdir" }, true);
        actor.Entry.Phase.Pipes.Select(ZoneGoalPipeFlags.RunOnce, pipe.Name);
        actor.Update(1000, .02f);
        await Assert.That(ReferenceEquals(actor.Argument, actor.Points!.Reference.State)).IsTrue();
        await Assert.That(actor.Points.Reference.State.BindingEntityId).IsEqualTo(actor.Entry.EntityId);
        await Assert.That(actor.LookState.LooseAttentionTarget).IsEqualTo(actor.Points.Look.State);
        await Assert.That(actor.Points.Look.State.Position).IsEqualTo(Vector3.UnitX);
        await Assert.That(actor.LookState.UseBodyDirection).IsTrue();
        actor.AiObject.BodyDirection = Vector3.UnitX;
        actor.Update(1020, .02f);
        await Assert.That(actor.LookState.LooseAttentionTarget).IsNull();
        await Assert.That(actor.LookState.UseBodyDirection).IsFalse();
        await Assert.That(actor.Npc.Unit.Pose).IsEqualTo(original);
    }

    [Test]
    public async Task ForcedLookAndRetirementDoNotAliasAnotherActorsOrAReplacementPoint()
    {
        using var fixture = new ManagedNpcActorSceneTests.Fixture(); var actor = fixture.Admit(); var other = fixture.Admit();
        var points = fixture.Scene.Points;
        points.SetLookAtPoint(actor, new(5, 6, 7), true);
        points.SetLookAtPoint(actor, Vector3.Zero);
        await Assert.That(actor.Points!.Look.State.Position).IsEqualTo(new Vector3(5, 6, 7));
        await Assert.That(points.ResolveSpecial(other, "lookat_target")).IsNull();
        other.Services.SetArgument(actor.Points.Reference.State);
        other.LookState.SetLooseAttentionTarget(actor.Points.Look.State);
        fixture.Scene.Remove(actor.Handle);
        await Assert.That(other.Argument).IsNull();
        await Assert.That(other.LookState.LooseAttentionTarget).IsNull();
        await Assert.That(other.HasActor).IsTrue();
        await Assert.That(points.DummyCount).IsEqualTo(2);
    }

    [Test]
    public async Task NotificationRetirementCannotContinueWritingTheDeadPoint()
    {
        using var fixture = new ManagedNpcActorSceneTests.Fixture(); var actor = fixture.Admit();
        var pipe = fixture.Scene.GoalPipes.Create("retiring_point_listener")!;
        actor.Entry.Phase.Pipes.Select(ZoneGoalPipeFlags.RunOnce, pipe.Name, eventId: 43);
        using var listener = new ZoneGoalPipeListener((_, _, _) => fixture.Scene.Remove(actor.Handle));
        actor.Entry.Phase.Pipes.RegisterListener(listener, 43);
        await Assert.That(() => fixture.Scene.Points.SetReferencePosition(actor, Vector3.One)).Throws<ObjectDisposedException>();
        await Assert.That(fixture.Scene.Points.DummyCount).IsEqualTo(0);
        await Assert.That(actor.Points!.IsDisposed).IsTrue();
    }

    private static void Append(ZoneGoalPipeDefinition pipe, ZoneGoalOperation op, ZoneGoalParameters parameters, bool blocking = false) =>
        pipe.Append(op, blocking, ZoneGoalGrouping.None, parameters, text => throw new InvalidOperationException(text));
}
