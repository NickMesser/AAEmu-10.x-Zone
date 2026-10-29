using System.Numerics;
using System.Text.Json;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Ai;
using AAEmu.Zone.Navigation;

namespace AAEmu.Zone.Tests;

public sealed class PredictiveTraceGoalTests
{
    [Test]
    public async Task OriginalPredictiveTraceInstructionsAgreeWithActualFollowerAndProxyOutput()
    {
        using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "AAEmu.Zone.Tests.Fixtures.npc-predictive-trace-native-controls.json")!;
        using var document = JsonDocument.Parse(stream); var errors = new List<string>();
        foreach (var control in document.RootElement.GetProperty("controls").EnumerateArray())
        {
            var request = control.GetProperty("request"); var label = control.GetProperty("name").GetString();
            using var scene = new Services();
            foreach (var point in request.GetProperty("points").EnumerateArray()) scene.Navigation.CurrentPath.PushBack(new(Vector(point), 2), true);
            scene.Navigation.CurrentPath.Version = 0;
            scene.Navigation.CurrentPath.CreationParameters = new() { ContinueMovingAtEnd = Flag(request, "continueMoving"), ClosedLoop = Flag(request, "closedLoop") };
            var speed = request.GetProperty("speeds").EnumerateArray().Select(value => value.GetSingle()).ToArray();
            scene.Speeds = new(speed[0], speed[1], speed[2]);
            scene.CurrentPathTargetType = request.TryGetProperty("targetType", out var target) ? target.GetInt32() : null;
            var ability = request.GetProperty("parameters");
            using var goal = new ZonePredictiveTraceGoal(new(-1, false, false), scene, () => scene.Microseconds);
            var expected = control.GetProperty("results").EnumerateArray().ToArray(); var index = 0;
            foreach (var frame in request.GetProperty("frames").EnumerateArray())
            {
                scene.Frame = new(Vector(frame.GetProperty("position")), Vector(frame.GetProperty("velocity")),
                    new(Number(ability, "lookAhead"), Number(ability, "pathRadius"), 0, 0, 0,
                        Number(ability, "maximumAcceleration"), Number(ability, "maximumDeceleration"), false), Flag(ability, "use2D"),
                    (frame.TryGetProperty("priority", out var framePriority) ? framePriority.GetInt32() : request.GetProperty("priority").GetInt32()) < 2);
                if (index > 0) scene.Microseconds += (long)MathF.Round(Number(frame, "seconds") * 1_000_000);
                var native = expected[index];
                var result = goal.Execute();
                if (result != (Flag(native, "reachedEnd") ? 2 : 1)) errors.Add(label + " result");
                Compare(Number(native, "desiredSpeed"), scene.Controls.DesiredSpeed, "desiredSpeed");
                Compare(Number(native, "distanceToPathEnd"), scene.Controls.DistanceToPathEnd, "distanceToPathEnd");
                CompareVector(Vector(native.GetProperty("direction")), scene.Controls.MovementDirection, "direction");
                var predictions = native.GetProperty("predictions").EnumerateArray().ToArray();
                if (scene.Predictions.Length != predictions.Length) errors.Add(label + " predictionCount");
                for (var sample = 0; sample < Math.Min(scene.Predictions.Length, predictions.Length); sample++)
                {
                    CompareVector(Vector(predictions[sample].GetProperty("position")), scene.Predictions[sample].Position, "predictionPosition" + sample);
                    CompareVector(Vector(predictions[sample].GetProperty("velocity")), scene.Predictions[sample].Velocity, "predictionVelocity" + sample);
                    Compare(Number(predictions[sample], "time"), (sample + 1) * .1f, "predictionTime");
                }
                index++;
            }
            if (scene.ContextWrites != 0) errors.Add(label + " predictive branch wrote ordinary context");
            void Compare(float wanted, float actual, string field)
            { if (!float.IsFinite(actual) || MathF.Abs(wanted - actual) > (MathF.Abs(wanted) > 1000 ? .001f : .00001f)) errors.Add($"{label}/{index} {field}: {wanted:R}!={actual:R}"); }
            void CompareVector(Vector3 wanted, Vector3 actual, string field)
            { Compare(wanted.X, actual.X, field + ".x"); Compare(wanted.Y, actual.Y, field + ".y"); Compare(wanted.Z, actual.Z, field + ".z"); }
        }
        await Assert.That(string.Join(Environment.NewLine, errors)).IsEqualTo("");
    }

    [Test]
    public async Task ActualPredictiveBranchOwnsNoBodyPoseOrOrdinaryTurnAndRetainsActorFollower()
    {
        using var scene = Scene();
        var firstPosition = scene.Frame.EntityPosition;
        using (var goal = new ZonePredictiveTraceGoal(new(-1, false, false), scene, () => scene.Microseconds))
        {
            await Assert.That(goal.Execute()).IsEqualTo(1);
            await Assert.That(scene.Controls.DesiredSpeed).IsEqualTo(1);
            await Assert.That(scene.Controls.MovementDirection).IsEqualTo(Vector3.UnitX);
            await Assert.That(scene.Predictions.Length).IsEqualTo(10);
            await Assert.That(scene.Frame.EntityPosition).IsEqualTo(firstPosition);
            await Assert.That(scene.Marker).IsEqualTo(firstPosition);
            scene.Microseconds += 20_000; goal.Execute();
            await Assert.That(scene.MarkerWrites).IsEqualTo(1); //Predictive trace never substitutes ordinary steering-marker updates.
        }
        await Assert.That(scene.PredictiveFollower.IsDisposed).IsFalse();
        await Assert.That(scene.ContextWrites).IsEqualTo(0);
        await Assert.That(scene.OrdinaryCalls).IsEqualTo(0);
    }

    [Test]
    public async Task SameClockVetoAndRetirementStopLaterPredictiveEffects()
    {
        using var scene = Scene();
        using var goal = new ZonePredictiveTraceGoal(new(-1, false, false), scene, () => scene.Microseconds);
        scene.Veto = true;
        await Assert.That(goal.Execute()).IsEqualTo(1);
        await Assert.That(scene.FrameReads).IsEqualTo(0);
        scene.Veto = false; goal.Execute();
        var reads = scene.FrameReads; var speed = scene.Controls.DesiredSpeed;
        await Assert.That(goal.Execute()).IsEqualTo(1);
        await Assert.That(scene.FrameReads).IsEqualTo(reads);
        await Assert.That(scene.Controls.DesiredSpeed).IsEqualTo(speed);
        scene.Microseconds += 20_000; scene.RetireOnPrediction = true;
        var avoidance = scene.AvoidanceCalls;
        await Assert.That(goal.Execute()).IsEqualTo(1);
        await Assert.That(scene.IsCurrent).IsFalse();
        await Assert.That(scene.AvoidanceCalls).IsEqualTo(avoidance);
    }

    [Test]
    public async Task SourceLowPriorityProfileMovesNormallyAndPublishesAnEmptyPredictionOutput()
    {
        using var scene = Scene(); scene.Frame = scene.Frame with { GeneratePredictions = false };
        using var goal = new ZonePredictiveTraceGoal(new(-1, false, false), scene, () => scene.Microseconds);
        await Assert.That(goal.Execute()).IsEqualTo(1);
        await Assert.That(scene.Controls.DesiredSpeed).IsEqualTo(1);
        await Assert.That(scene.Controls.MovementDirection).IsEqualTo(Vector3.UnitX);
        await Assert.That(scene.MarkerWrites).IsEqualTo(1);
        await Assert.That(scene.Predictions.Length).IsEqualTo(0);
        await Assert.That(scene.PredictiveFollower.CopyNodes().Count).IsEqualTo(3);
    }

    private static Services Scene()
    {
        var result = new Services(); result.Navigation.CurrentPath.PushBack(new(new(0, 0, 0), 2), true);
        result.Navigation.CurrentPath.PushBack(new(new(10, 0, 0), 2), true); return result;
    }
    private sealed class Services : IZonePredictiveTraceGoalServices, IZoneActorNavigationScene, IDisposable
    {
        public bool IsCurrent { get; set; } = true;
        public ZoneActorControlState Controls { get; } = new();
        public ZoneActorNavigationState Navigation { get; }
        public ZoneApproachPathDecision PathDecision => ZoneApproachPathDecision.Found;
        public ZonePredictivePathFollower PredictiveFollower { get; }
        public long Microseconds { get; set; } = 1_000_000;
        public ZoneGroundTraceSpeeds Speeds { get; set; } = new(4, 0, 6);
        public ZonePredictiveTraceFrame Frame { get; set; } = new(Vector3.Zero, Vector3.Zero, new(2, .5f, 0, 0, 0, 10, 8, false), true, true);
        public int? CurrentPathTargetType { get; set; }
        public ZonePredictivePathSample[] Predictions { get; private set; } = [];
        public Vector3? Marker { get; private set; }
        public int MarkerWrites { get; private set; }
        public int ContextWrites { get; private set; }
        public int OrdinaryCalls { get; private set; }
        public int FrameReads { get; private set; }
        public int AvoidanceCalls { get; private set; }
        public bool Veto { get; set; }
        public bool RetireOnPrediction { get; set; }
        public Services() { Navigation = new(this); PredictiveFollower = new(Navigation.CurrentPath); }
        public void ValidatePredictiveTrace(ZoneTraceGoalParameters parameters) { }
        public ZonePredictiveTraceFrame ReadPredictiveFrame() { FrameReads++; return Frame; }
        public void SetMovementPredictions(IReadOnlyList<ZonePredictivePathSample> predictions)
        { Predictions = predictions.ToArray(); if (RetireOnPrediction) IsCurrent = false; }
        public void ValidateOrdinaryGroundTrace(ZoneTraceGoalParameters parameters) { OrdinaryCalls++; throw new InvalidOperationException(); }
        public ZoneGroundTraceFrame ReadFrame() { OrdinaryCalls++; throw new InvalidOperationException(); }
        public ZoneGroundTraceSpeeds GetMovementSpeeds() => Speeds;
        public bool BeginMove() => Veto;
        public void SetTraceTarget(Vector3 position) { Marker = position; MarkerWrites++; }
        public void ReleaseTraceTarget() => Marker = null;
        public void RefreshNavigationType() { }
        public void UpdateAvoidance(bool fullUpdate, bool ignoreTarget) => AvoidanceCalls++;
        public void ResetTrace() { Navigation.CurrentPath.Clear(); Navigation.OriginalPath.Clear(); }
        public void SetMovementContext(int context) => ContextWrites++;
        public bool CanStrafe() => false;
        public Vector3 GetPosition() => Frame.EntityPosition;
        public object? GetAttentionTarget() => null;
        public Vector3 GetTargetPosition(object target) => throw new NotSupportedException();
        public void Dispose() => PredictiveFollower.Dispose();
    }
    private static float Number(JsonElement value, string name) => value.GetProperty(name).GetSingle();
    private static bool Flag(JsonElement value, string name) => value.TryGetProperty(name, out var found) && found.GetBoolean();
    private static Vector3 Vector(JsonElement value)
    { var numbers = value.EnumerateArray().Select(element => element.GetSingle()).ToArray(); return new(numbers[0], numbers[1], numbers[2]); }
}
