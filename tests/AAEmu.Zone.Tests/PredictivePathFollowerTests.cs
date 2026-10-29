using System.Numerics;
using System.Text.Json;
using AAEmu.Zone.Navigation;

namespace AAEmu.Zone.Tests;

public sealed class PredictivePathFollowerTests
{
    [Test]
    public async Task ActualRoutePublicationRebuildsTheActorFollowerWithoutChangingTheSourcePath()
    {
        var path = new ZoneNavigationPath(); path.PushBack(new(new(0, 0, 0), 2), true); path.PushBack(new(new(10, 0, 0), 2), true);
        path.CurrentFraction = .25f;
        using var follower = new ZonePredictivePathFollower(path) { Parameters = Parameters() };
        var first = follower.Update(Vector3.Zero, Vector3.Zero, .1f);
        await Assert.That(first.Velocity).IsEqualTo(Vector3.UnitX);
        await Assert.That(first.Predictions.Count).IsEqualTo(10);
        await Assert.That(path.CurrentFraction).IsEqualTo(.25f);
        await Assert.That(path.Points[1].Position).IsEqualTo(new Vector3(10, 0, 0));
        var snapshot = first.Predictions.ToArray();
        path.Clear(); path.PushBack(new(Vector3.Zero, 2), true); path.PushBack(new(new(0, 10, 0), 2), true);
        var second = follower.Update(Vector3.Zero, Vector3.Zero, .1f);
        await Assert.That(second.PathVersion).IsEqualTo(path.Version);
        await Assert.That(second.PathVersion).IsNotEqualTo(first.PathVersion);
        await Assert.That(second.Velocity).IsEqualTo(Vector3.UnitY);
        await Assert.That(first.Predictions).IsEquivalentTo(snapshot);
        path.Clear();
        var empty = follower.Update(Vector3.Zero, Vector3.Zero, .1f);
        await Assert.That(empty.ReachedEnd).IsTrue();
        await Assert.That(empty.Velocity).IsEqualTo(Vector3.Zero);
        await Assert.That(empty.Predictions.Count).IsEqualTo(0);
    }

    [Test]
    public async Task PredictionIsCopiedMathAndRetirementRejectsFurtherActorUpdates()
    {
        var path = new ZoneNavigationPath(); path.PushBack(new(Vector3.Zero, 2), true); path.PushBack(new(new(10, 0, 0), 2), true);
        using var follower = new ZonePredictivePathFollower(path) { Parameters = Parameters() with { MaximumAcceleration = 0 } };
        var stopped = follower.Update(Vector3.Zero, Vector3.Zero, .1f);
        await Assert.That(stopped.ReachedEnd).IsFalse();
        await Assert.That(stopped.Velocity).IsEqualTo(Vector3.Zero);
        await Assert.That(stopped.Predictions.All(sample => sample.Position == Vector3.Zero && sample.Velocity == Vector3.Zero)).IsTrue();
        var withoutPredictions = follower.Update(Vector3.Zero, Vector3.Zero, .1f, generatePredictions: false);
        await Assert.That(withoutPredictions.Predictions.Count).IsEqualTo(0);
        await Assert.That(path.Count).IsEqualTo(2);
        follower.Dispose();
        await Assert.That(() => follower.Update(Vector3.Zero, Vector3.Zero, .1f)).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task OriginalInstructionsAgreeWithOffsetTrajectoryAndEveryPredictedFrame()
    {
        using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "AAEmu.Zone.Tests.Fixtures.npc-predictive-native-controls.json")!;
        using var document = JsonDocument.Parse(stream);
        var errors = new List<string>();
        foreach (var control in document.RootElement.GetProperty("controls").EnumerateArray())
        {
            var request = control.GetProperty("request"); var label = control.GetProperty("name").GetString();
            var path = new ZoneNavigationPath();
            var types = request.TryGetProperty("navigationTypes", out var typeArray) ? typeArray.EnumerateArray().Select(value => value.GetUInt32()).ToArray() : null;
            var pointIndex = 0;
            foreach (var point in request.GetProperty("points").EnumerateArray()) path.PushBack(new(Vector(point), types?[pointIndex++] ?? 2u), true);
            path.Version = 0;
            path.CreationParameters = new() { ClosedLoop = request.TryGetProperty("closedLoop", out var loop) && loop.GetBoolean() };
            var p = request.GetProperty("parameters");
            using var follower = new ZonePredictivePathFollower(path)
            {
                Parameters = new(Number(p, "normalSpeed"), Number(p, "pathRadius"), Number(p, "lookAhead"),
                    Number(p, "maximumAcceleration"), Number(p, "maximumDeceleration"), Number(p, "minimumSpeed"),
                    Number(p, "maximumSpeed"), Number(p, "endDistance"), Flag(p, "stopAtEnd"), Flag(p, "use2D"))
                { CurvatureSlowDown = Flag(p, "curvatureSlowDown") }
            };
            var expectedFrames = control.GetProperty("results").EnumerateArray().ToArray(); var index = 0;
            foreach (var frame in request.GetProperty("frames").EnumerateArray())
            {
                if (Number(p, "lookAhead") == 0)
                {
                    await Assert.That(() => follower.Update(Vector(frame.GetProperty("position")), Vector(frame.GetProperty("velocity")), Number(frame, "seconds")))
                        .Throws<NotSupportedException>();
                    break; //The original produces NaNs here; the admitted managed profile explicitly refuses them.
                }
                var result = follower.Update(Vector(frame.GetProperty("position")), Vector(frame.GetProperty("velocity")), Number(frame, "seconds"),
                    !frame.TryGetProperty("generatePredictions", out var generate) || generate.GetBoolean());
                var expected = expectedFrames[index];
                CompareVector(Vector(expected.GetProperty("velocity")), result.Velocity, "velocity");
                Compare(Number(expected, "remainingDistance"), result.RemainingDistance, "remaining");
                CompareVector(Vector(expected.GetProperty("target")), result.TargetPosition, "target");
                Compare(Number(expected, "retainedSpeed"), result.RetainedSpeed, "retainedSpeed");
                if (result.ReachedEnd != Flag(expected, "reachedEnd")) errors.Add(label + " reachedEnd");
                if (result.TargetIndex != expected.GetProperty("targetIndex").GetInt32()) errors.Add(label + " targetIndex");
                var nodes = follower.CopyNodes(); var expectedNodes = expected.GetProperty("nodes").EnumerateArray().ToArray();
                if (nodes.Count != expectedNodes.Length) errors.Add(label + $" nodes {nodes.Count}!={expectedNodes.Length}");
                for (var node = 0; node < Math.Min(nodes.Count, expectedNodes.Length); node++)
                {
                    CompareVector(Vector(expectedNodes[node].GetProperty("position")), nodes[node].Position, "nodePosition");
                    CompareVector(Vector(expectedNodes[node].GetProperty("offset")), nodes[node].OffsetDirection, "nodeOffset");
                    Compare(Number(expectedNodes[node], "scale"), nodes[node].OffsetScale, "nodeScale");
                }
                var predictions = expected.GetProperty("predictions").EnumerateArray().ToArray();
                if (result.Predictions.Count != predictions.Length) errors.Add(label + " predictionCount");
                for (var sample = 0; sample < Math.Min(result.Predictions.Count, predictions.Length); sample++)
                {
                    CompareVector(Vector(predictions[sample].GetProperty("position")), result.Predictions[sample].Position, "predictedPosition" + sample);
                    CompareVector(Vector(predictions[sample].GetProperty("velocity")), result.Predictions[sample].Velocity, "predictedVelocity" + sample);
                }
                foreach (var sample in expected.GetProperty("samples").EnumerateArray())
                {
                    var point = follower.GetPointAtDistance(Number(sample, "distance"), out var actualDistance);
                    CompareVector(Vector(sample.GetProperty("position")), point, "samplePosition");
                    Compare(Number(sample, "actualDistance"), actualDistance, "sampleActualDistance");
                }
                index++;
            }
            void Compare(float wanted, float actual, string field)
            {
                var tolerance = MathF.Abs(wanted) > 1000 ? .001f : .00001f;
                if (!float.IsFinite(actual) || MathF.Abs(wanted - actual) > tolerance) errors.Add($"{label}/{index} {field}: {wanted:R}!={actual:R}");
            }
            void CompareVector(Vector3 wanted, Vector3 actual, string field)
            { Compare(wanted.X, actual.X, field + ".x"); Compare(wanted.Y, actual.Y, field + ".y"); Compare(wanted.Z, actual.Z, field + ".z"); }
        }
        await Assert.That(string.Join(Environment.NewLine, errors)).IsEqualTo("");
    }
    private static float Number(JsonElement value, string name) => value.GetProperty(name).GetSingle();
    private static bool Flag(JsonElement value, string name) => value.TryGetProperty(name, out var flag) && flag.GetBoolean();
    private static Vector3 Vector(JsonElement value)
    { var numbers = value.EnumerateArray().Select(element => element.GetSingle()).ToArray(); return new(numbers[0], numbers[1], numbers[2]); }
    private static ZonePredictivePathFollowerParameters Parameters() => new(4, .5f, 2, 10, 8, 0, 6, 0, true, true);
}
