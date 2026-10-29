using System.Numerics;
using System.Text.Json;
using AAEmu.Zone.Navigation;

namespace AAEmu.Zone.Tests;

public sealed class NavigationPathSteeringTests
{
    [Test]
    public async Task OriginalInstructionsAgreeWithAdvancementLookAheadAndSteering()
    {
        using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "AAEmu.Zone.Tests.Fixtures.npc-path-steering-native-controls.json")!;
        using var document = JsonDocument.Parse(stream);
        var errors = new List<string>();
        foreach (var control in document.RootElement.GetProperty("controls").EnumerateArray())
        {
            var request = control.GetProperty("request"); var expected = control.GetProperty("result");
            var label = request.GetProperty("kind").GetString() + "/" + control.GetProperty("name").GetString();
            var path = new ZoneNavigationPath();
            foreach (var point in request.GetProperty("points").EnumerateArray()) path.PushBack(new(Vector(point), 2), true);
            path.Version = 0;
            path.CurrentFraction = Number(request, "fraction"); path.StuckTime = Number(request, "stuck");
            var position = Vector(request.GetProperty("pos")); var flat = request.GetProperty("flat").GetBoolean();
            var look = Number(request, "look"); var radius = Number(request, "radius");
            switch (request.GetProperty("kind").GetString())
            {
                case "advance":
                    Compare(Number(expected, "remaining"), ZoneNavigationPathSteering.Advance(path, position, look, flat), "remaining");
                    break;
                case "steer":
                    CompareVector(Vector(expected.GetProperty("steering")),
                        ZoneNavigationPathSteering.GetSteeringPosition(path, position, look, look * .1f, radius, flat), "steering");
                    break;
                case "curve":
                    var curve = ZoneNavigationPathSteering.GetLookAheadCurvature(path, look, flat);
                    if (curve.HasPath != expected.GetProperty("hasPath").GetBoolean()) errors.Add(label + " hasPath");
                    CompareVector(Vector(expected.GetProperty("position")), curve.Position, "position");
                    CompareVector(Vector(expected.GetProperty("direction")), curve.Direction, "direction");
                    Compare(Number(expected, "curvature"), curve.Curvature, "curvature");
                    Compare(Number(expected, "minimumDot"), curve.MinimumDot, "minimumDot");
                    break;
                default:
                    var result = ZoneNavigationPathSteering.Update(path, position, Vector(request.GetProperty("vel")),
                        look, radius, Number(request, "seconds"), request.GetProperty("adaptive").GetBoolean(), flat);
                    if (result.HasPath != expected.GetProperty("hasPath").GetBoolean()) errors.Add(label + " hasPath");
                    Compare(Number(expected, "remaining"), result.RemainingDistance, "remaining");
                    Compare(Number(expected, "distanceToPath"), result.DistanceToPath, "distanceToPath");
                    if (result.IsStuck != expected.GetProperty("isStuck").GetBoolean()) errors.Add(label + " isStuck");
                    CompareVector(Vector(expected.GetProperty("direction")), result.Direction, "direction");
                    CompareVector(Vector(expected.GetProperty("segment")), result.SegmentDirection, "segment");
                    CompareVector(Vector(expected.GetProperty("inflection")), result.InflectionDirection, "inflection");
                    CompareVector(Vector(expected.GetProperty("steering")), result.SteeringPosition, "steering");
                    break;
            }
            Compare(Number(expected, "fraction"), path.CurrentFraction, "fraction");
            Compare(Number(expected, "stuck"), path.StuckTime, "stuck");
            if (path.Version != expected.GetProperty("version").GetInt32()) errors.Add(label + $" version {path.Version}");
            var expectedPoints = expected.GetProperty("points").EnumerateArray().ToArray();
            if (expectedPoints.Length != path.Count) errors.Add(label + $" count {path.Count}");
            else for (var index = 0; index < path.Count; index++) CompareVector(Vector(expectedPoints[index]), path.Points[index].Position, "point");

            void Compare(float wanted, float actual, string field)
            {
                if (float.IsNaN(wanted)) { if (!float.IsNaN(actual)) errors.Add(label + " " + field + " expected NaN"); return; }
                if (!float.IsFinite(actual)) { errors.Add(label + " " + field + " unexpected nonfinite"); return; }
                // Large-coordinate sampled heights may differ by one float ULP after interpolation.
                if (MathF.Abs(wanted - actual) > MathF.Max(.000001f, MathF.Abs(wanted) * .0000002f))
                    errors.Add($"{label} {field}: {wanted:R} != {actual:R}");
            }
            void CompareVector(Vector3 wanted, Vector3 actual, string field)
            { Compare(wanted.X, actual.X, field + ".x"); Compare(wanted.Y, actual.Y, field + ".y"); Compare(wanted.Z, actual.Z, field + ".z"); }
        }
        await Assert.That(string.Join(Environment.NewLine, errors)).IsEqualTo("");
    }

    [Test]
    public async Task BisectorAdvancementPreservesFinalPointAndMonotonicProgress()
    {
        var path = new ZoneNavigationPath();
        path.PushBack(new(new(0, 0, 0)), true); path.PushBack(new(new(5, 0, 0)), true); path.PushBack(new(new(5, 5, 0)), true);
        ZoneNavigationPathSteering.Advance(path, new(5, 0, 0), 2, true);
        await Assert.That(path.Count).IsEqualTo(3);
        await Assert.That(path.CurrentFraction).IsEqualTo(1);
        ZoneNavigationPathSteering.Advance(path, new(5, .01f, 0), 2, true);
        await Assert.That(path.Count).IsEqualTo(2);
        var fraction = path.CurrentFraction;
        ZoneNavigationPathSteering.Advance(path, new(5, 0, 0), 2, true);
        await Assert.That(path.CurrentFraction).IsEqualTo(fraction);
        ZoneNavigationPathSteering.Advance(path, new(5, 6, 0), 2, true);
        await Assert.That(path.Count).IsEqualTo(1);
        await Assert.That(path.Points[0].Position).IsEqualTo(new Vector3(5, 5, 0));
    }

    private static float Number(JsonElement element, string name) => element.GetProperty(name).GetSingle();
    private static Vector3 Vector(JsonElement element)
    { var values = element.EnumerateArray().Select(value => value.GetSingle()).ToArray(); return new(values[0], values[1], values[2]); }
}
