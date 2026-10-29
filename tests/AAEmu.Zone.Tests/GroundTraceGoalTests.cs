using System.Numerics;
using System.Text.Json;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Ai;
using AAEmu.Zone.Navigation;

namespace AAEmu.Zone.Tests;

public sealed class GroundTraceGoalTests
{
    [Test]
    public async Task OriginalGroundTraceInstructionsAgreeWithSpeedAndDirectionMath()
    {
        using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "AAEmu.Zone.Tests.Fixtures.npc-ground-trace-native-controls.json")!;
        using var document = JsonDocument.Parse(stream);
        var errors = new List<string>();
        foreach (var control in document.RootElement.GetProperty("controls").EnumerateArray())
        {
            var request = control.GetProperty("request"); var expected = control.GetProperty("result");
            var label = control.GetProperty("name").GetString();
            var path = new ZoneNavigationPath();
            foreach (var point in request.GetProperty("points").EnumerateArray()) path.PushBack(new(Vector(point), 2), true);
            path.Version = 0;
            path.CurrentFraction = request.TryGetProperty("fraction", out var fraction) ? fraction.GetSingle() : 0;
            path.DiscardedPathLength = request.TryGetProperty("discarded", out var discarded) ? discarded.GetSingle() : 0;
            var ability = request.GetProperty("ability");
            var frame = new ZoneGroundTraceFrame(Vector(request.GetProperty("position")), Vector(request.GetProperty("velocity")),
                new(Number(ability, "lookAhead"), Number(ability, "pathRadius"), Number(ability, "speedLookAhead"),
                    Number(ability, "cornerSlowDown"), Number(ability, "slopeSlowDown"), Number(ability, "maximumAcceleration"),
                    Number(ability, "maximumDeceleration"), ability.GetProperty("adaptiveLookAhead").GetBoolean()),
                request.GetProperty("navigationType").GetUInt32(),
                new(request.GetProperty("flying").GetBoolean(), Vector(request.GetProperty("groundNormal"))));
            var speedArray = request.GetProperty("speeds").EnumerateArray().Select(value => value.GetSingle()).ToArray();
            var result = ZoneGroundTraceMath.Update(path, frame, new(speedArray[0], speedArray[1], speedArray[2]),
                Number(request, "urgency"), Number(request, "priorSpeed"), Number(request, "seconds"), Number(request, "endAccuracy"));
            if (result.EndReached != expected.GetProperty("endReached").GetBoolean()) errors.Add(label + " endReached");
            Compare(Number(expected, "desiredSpeed"), result.DesiredSpeed, "desiredSpeed");
            Compare(Number(expected, "distanceToPathEnd"), result.DistanceToPathEnd, "distanceToPathEnd");
            if (!result.EndReached)
            {
                var direction = Vector(expected.GetProperty("direction"));
                Compare(direction.X, result.Direction.X, "direction.x"); Compare(direction.Y, result.Direction.Y, "direction.y"); Compare(direction.Z, result.Direction.Z, "direction.z");
            }
            var expectedPath = expected.GetProperty("path");
            Compare(Number(expectedPath, "fraction"), path.CurrentFraction, "fraction");
            if (path.Count != expectedPath.GetProperty("points").GetArrayLength()) errors.Add(label + " pathCount");
            void Compare(float wanted, float actual, string field)
            { if (!float.IsFinite(actual) || MathF.Abs(wanted - actual) > .000002f) errors.Add($"{label} {field}: {wanted:R} != {actual:R}"); }
        }
        await Assert.That(string.Join(Environment.NewLine, errors)).IsEqualTo("");
    }

    [Test]
    public async Task ActualSceneServicesReceiveControlsAndTraceTargetWithoutWritingAPose()
    {
        var scene = new Services();
        scene.Navigation.CurrentPath.PushBack(new(new(0, 0, 0)), true);
        scene.Navigation.CurrentPath.PushBack(new(new(10, 0, 0)), true);
        scene.Controls.MovementUrgency = 1;
        using var goal = new ZoneGroundTraceGoal(new(-1, false, false), scene, () => scene.Microseconds);
        await Assert.That(goal.Execute()).IsEqualTo(1);
        await Assert.That(scene.Controls.DesiredSpeed).IsEqualTo(1);
        await Assert.That(scene.Controls.MovementDirection).IsEqualTo(Vector3.UnitX);
        await Assert.That(scene.TraceTarget).IsEqualTo(new Vector3(2, 0, 0));
        await Assert.That(scene.Order).IsEquivalentTo(new[] { "validate", "OnMove", "frame", "target", "speeds", "context1", "target", "navtype", "avoidance" });
        scene.Microseconds += 100_000;
        scene.Order.Clear();
        await Assert.That(goal.Execute()).IsEqualTo(1);
        await Assert.That(scene.Controls.DesiredSpeed).IsEqualTo(2);
        await Assert.That(scene.Order.Contains("OnMove")).IsFalse();
        await Assert.That(scene.Context).IsEqualTo(3);
        await Assert.That(scene.Frame.EntityPosition).IsEqualTo(Vector3.Zero);
    }

    [Test]
    public async Task EndAccuracyWaitsForRealForwardVelocityAndReleasesTheExactTrace()
    {
        var scene = new Services();
        scene.Navigation.CurrentPath.PushBack(new(new(0, 0, 0)), true);
        scene.Navigation.CurrentPath.PushBack(new(new(10, 0, 0)), true);
        scene.Controls.MovementUrgency = 1; scene.Controls.MovementDirection = Vector3.UnitX;
        using var goal = new ZoneGroundTraceGoal(new(0, false, false), scene, () => scene.Microseconds);
        goal.Execute();
        scene.Frame = scene.Frame with { EntityPosition = new(10.1f, 0, 0), ActualVelocity = Vector3.UnitX };
        scene.Microseconds += 100_000;
        await Assert.That(goal.Execute()).IsEqualTo(1);
        await Assert.That(goal.WaitingForSpeed).IsTrue();
        await Assert.That(scene.Controls.DesiredSpeed).IsEqualTo(0);
        scene.Frame = scene.Frame with { ActualVelocity = new(.01f, 0, 0) };
        scene.Microseconds += 100_000;
        await Assert.That(goal.Execute()).IsEqualTo(2);
        await Assert.That(scene.Navigation.CurrentPath.Count).IsEqualTo(0);
        await Assert.That(scene.Released).IsEqualTo(1);
    }

    [Test]
    public async Task OnMoveVetoAndRetirementPreventSubsequentFrameOrControlEffects()
    {
        var scene = new Services { Veto = true };
        scene.Navigation.CurrentPath.PushBack(new(new(0, 0, 0)), true); scene.Navigation.CurrentPath.PushBack(new(new(10, 0, 0)), true);
        using var goal = new ZoneGroundTraceGoal(new(-1, false, false), scene, () => scene.Microseconds);
        await Assert.That(goal.Execute()).IsEqualTo(1);
        await Assert.That(scene.Order).IsEquivalentTo(new[] { "validate", "OnMove" });
        scene.Order.Clear(); scene.Veto = false; scene.RetireOnFrame = true;
        await Assert.That(goal.Execute()).IsEqualTo(1);
        await Assert.That(scene.Order).IsEquivalentTo(new[] { "validate", "OnMove", "frame" });
        await Assert.That(scene.Controls.DesiredSpeed).IsEqualTo(0);
    }

    [Test]
    public async Task NativeNoTurnLeavesAreAdmittedAndReachableTurnRequiresItsRealOwner()
    {
        var frame = new Services().Frame with { Turning = new(.8f, false, Vector3.UnitY) };
        await Assert.That(ZoneGroundTraceMath.NeedsTurnInPlace(frame, Vector3.UnitX, 1, 0)).IsFalse();
        await Assert.That(ZoneGroundTraceMath.NeedsTurnInPlace(frame, Vector3.UnitY, 1, 2)).IsFalse();
        await Assert.That(ZoneGroundTraceMath.NeedsTurnInPlace(frame, Vector3.UnitX, 1, 2)).IsTrue();
        await Assert.That(ZoneGroundTraceMath.NeedsTurnInPlace(frame, -Vector3.UnitY, -1, 2)).IsFalse();
        await Assert.That(ZoneGroundTraceMath.NeedsTurnInPlace(frame with { Turning = new(.8f, true, Vector3.UnitY) }, Vector3.UnitX, 1, 2)).IsFalse();
        var scene = new Services { Frame = frame };
        scene.Controls.MovementUrgency = 1; scene.Controls.DesiredSpeed = 2;
        scene.Navigation.CurrentPath.PushBack(new(new(0, 0, 0)), true); scene.Navigation.CurrentPath.PushBack(new(new(10, 0, 0)), true);
        using var goal = new ZoneGroundTraceGoal(new(-1, false, false), scene, () => scene.Microseconds);
        await Assert.That(() => goal.Execute()).Throws<NotSupportedException>();
        await Assert.That(scene.Controls.DesiredSpeed).IsEqualTo(2);
    }

    [Test]
    public async Task OriginalTurnEntryPredicatesAgreeIncludingEqualityAndReverseUrgency()
    {
        using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "AAEmu.Zone.Tests.Fixtures.npc-turn-admission-native-controls.json")!;
        using var document = JsonDocument.Parse(stream);
        foreach (var control in document.RootElement.GetProperty("controls").EnumerateArray())
        {
            var request = control.GetProperty("request");
            var frame = new Services().Frame with { Turning = new(Number(request, "threshold"),
                request.GetProperty("obstructed").GetBoolean(), Vector(request.GetProperty("bodyDirection"))) };
            var result = ZoneGroundTraceMath.NeedsTurnInPlace(frame, Vector(request.GetProperty("direction")), Number(request, "urgency"), Number(request, "priorSpeed"));
            await Assert.That(result).IsEqualTo(control.GetProperty("needsTurn").GetBoolean());
        }
    }

    [Test]
    public async Task StuckComparisonUsesActualAnimatedVelocityAndIndependentUnitAttribute()
    {
        await Assert.That(ZoneApproachStuckMath.IsStuck(Vector3.Zero, 5, 0, Vector3.Zero)).IsFalse();
        await Assert.That(ZoneApproachStuckMath.IsStuck(new(.0001f), 5, 0, Vector3.Zero)).IsFalse();
        await Assert.That(ZoneApproachStuckMath.IsStuck(Vector3.UnitX, .000099f, 0, Vector3.Zero)).IsFalse();
        await Assert.That(ZoneApproachStuckMath.IsStuck(Vector3.UnitX, 5, -1000, Vector3.Zero)).IsFalse();
        await Assert.That(ZoneApproachStuckMath.IsStuck(Vector3.UnitX, 5, 0, new(1.5f, 0, 0))).IsFalse();
        await Assert.That(ZoneApproachStuckMath.IsStuck(Vector3.UnitX, 5, 0, new(MathF.BitDecrement(1.5f), 0, 0))).IsTrue();
        await Assert.That(ZoneApproachStuckMath.IsStuck(Vector3.UnitX, 5, 1000, new(2, 0, 0))).IsTrue();
        await Assert.That(ZoneApproachStuckMath.IsStuck(Vector3.UnitX, 5, 0, new(2, 0, 0))).IsFalse();
        await Assert.That(ZoneApproachStuckMath.IsStuck(Vector3.UnitX, .0001f, 0, Vector3.Zero)).IsTrue();
    }

    private static float Number(JsonElement element, string name) => element.GetProperty(name).GetSingle();
    private static Vector3 Vector(JsonElement element)
    { var values = element.EnumerateArray().Select(value => value.GetSingle()).ToArray(); return new(values[0], values[1], values[2]); }

    private sealed class Services : IZoneGroundTraceGoalServices, IZoneActorNavigationScene
    {
        public bool IsCurrent { get; set; } = true;
        public ZoneActorControlState Controls { get; } = new();
        public ZoneActorNavigationState Navigation { get; }
        public ZoneApproachPathDecision PathDecision { get; set; } = ZoneApproachPathDecision.Found;
        public List<string> Order { get; } = [];
        public long Microseconds { get; set; } = 1_000_000;
        public bool Veto { get; set; }
        public bool RetireOnFrame { get; set; }
        public Vector3? TraceTarget { get; private set; }
        public int Context { get; private set; }
        public int Released { get; private set; }
        public ZoneGroundTraceFrame Frame { get; set; } = new(Vector3.Zero, Vector3.Zero,
            new(2, .5f, 0, .5f, 0, 10, 8, true), 2, new(false, Vector3.UnitZ), new(1, false, Vector3.UnitY));
        public Services() => Navigation = new(this);
        public void ValidateOrdinaryGroundTrace(ZoneTraceGoalParameters parameters) => Order.Add("validate");
        public ZoneGroundTraceFrame ReadFrame() { Order.Add("frame"); if (RetireOnFrame) IsCurrent = false; return Frame; }
        public ZoneGroundTraceSpeeds GetMovementSpeeds() { Order.Add("speeds"); return new(4, 1, 6); }
        public bool BeginMove() { Order.Add("OnMove"); return Veto; }
        public void SetTraceTarget(Vector3 position) { Order.Add("target"); TraceTarget = position; }
        public void ReleaseTraceTarget() { Released++; TraceTarget = null; }
        public void RefreshNavigationType() => Order.Add("navtype");
        public void UpdateAvoidance(bool fullUpdate, bool ignoreTarget) => Order.Add("avoidance");
        public void ResetTrace() { Navigation.CurrentPath.Clear(); Navigation.OriginalPath.Clear(); }
        public void SetMovementContext(int context) { Context = context; Order.Add("context" + context); }
        public bool CanStrafe() => false;
        public Vector3 GetPosition() => Frame.EntityPosition;
        public object? GetAttentionTarget() => null;
        public Vector3 GetTargetPosition(object target) => throw new NotSupportedException();
    }
}
