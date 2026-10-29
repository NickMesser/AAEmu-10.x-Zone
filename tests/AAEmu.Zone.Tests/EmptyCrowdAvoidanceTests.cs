using System.Numerics;
using System.Text.Json;
using AAEmu.Zone.Ai;

namespace AAEmu.Zone.Tests;

public sealed class EmptyCrowdAvoidanceTests
{
    [Test]
    public async Task OriginalInstructionsAgreeWithEmptySolverBasisClocksAndPersistentState()
    {
        using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "AAEmu.Zone.Tests.Fixtures.npc-empty-crowd-native-controls.json")!;
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement; var defaults = root.GetProperty("defaults");
        var initial = root.GetProperty("initialState"); var errors = new List<string>();
        foreach (var control in root.GetProperty("controls").EnumerateArray())
        {
            var request = control.GetProperty("request"); var label = control.GetProperty("name").GetString();
            var seed = request.TryGetProperty("initialState", out var copiedState) ? copiedState : initial;
            using var owner = new ZoneEmptyCrowdAvoidance(new(),
                request.TryGetProperty("objectType", out var type) ? type.GetUInt16() : (ushort)5, ReadState(seed, initial));
            var expected = control.GetProperty("results").EnumerateArray().ToArray(); var index = 0;
            foreach (var frame in request.GetProperty("frames").EnumerateArray())
            {
                JsonElement Value(string name) => Get(frame, name, defaults);
                var input = new ZoneEmptyCrowdAvoidanceFrame(Vector(Value("entityPosition")), Vector(Value("viewDirection")),
                    Vector(Value("movementDirection")), Value("desiredSpeed").GetSingle(), Value("avoidanceRadius").GetSingle(),
                    Value("alertness").GetInt32(), Value("priority").GetUInt32(), Value("uses3D").GetBoolean(),
                    Value("fullUpdate").GetBoolean(), Value("microseconds").GetInt64(), Value("seconds").GetSingle())
                { Actor1593 = Value("actor1593").GetBoolean(), Actor1594 = Value("actor1594").GetBoolean() };
                var output = owner.Update(input, []); var wanted = expected[index];
                var state = ReadState(wanted, initial);
                if (output.Modified != wanted.GetProperty("modified").GetBoolean()) Error("modified");
                if (output.CensusRefreshed != wanted.GetProperty("events").TryGetProperty("grid0", out _)) Error("censusRefreshed");
                if (output.SolverRebuilt != wanted.GetProperty("events").TryGetProperty("entityBounds", out _)) Error("solverRebuilt");
                if (state.LastCensusMicroseconds != output.State.LastCensusMicroseconds) Error("lastCensus");
                if (state.WasMoving != output.State.WasMoving) Error("wasMoving");
                CompareVector(Vector(wanted.GetProperty("movementDirection")), output.MovementDirection, "movementDirection");
                Compare(wanted.GetProperty("desiredSpeed").GetSingle(), output.DesiredSpeed, "desiredSpeed");
                Compare(state.SteeringSide, output.State.SteeringSide, "side");
                Compare(state.ObstructionTimer, output.State.ObstructionTimer, "timer");
                Compare(state.SolverRadius, output.State.SolverRadius, "radius");
                CompareVector(state.SolverOrigin, output.State.SolverOrigin, "origin");
                CompareVector(state.SolverForward, output.State.SolverForward, "forward");
                CompareVector(state.SolverSide, output.State.SolverSide, "basisSide");
                index++;
                void Error(string field) => errors.Add($"{label}/{index} {field}");
                void Compare(float wantedValue, float actual, string field)
                { if (MathF.Abs(wantedValue - actual) > .00000012f) errors.Add($"{label}/{index} {field}: {wantedValue:R}!={actual:R}"); }
                void CompareVector(Vector3 wantedValue, Vector3 actual, string field)
                { Compare(wantedValue.X, actual.X, field + ".x"); Compare(wantedValue.Y, actual.Y, field + ".y"); Compare(wantedValue.Z, actual.Z, field + ".z"); }
            }
        }
        await Assert.That(string.Join(Environment.NewLine, errors)).IsEqualTo("");
    }

    [Test]
    public async Task ExactSelfAndTheStrictFullThreeDimensionalThirtyMeterBoundaryArePreserved()
    {
        var self = new ZoneAiObjectState { EntityId = 7 };
        using var owner = new ZoneEmptyCrowdAvoidance(self);
        var frame = Frame();
        var output = owner.Update(frame, [new(self, default), new(new(), new(0, 0, 30)), new(new(), new(30, 0, 0))]);
        await Assert.That(output.CensusRefreshed).IsTrue();
        await Assert.That(output.MovementDirection).IsEqualTo(frame.MovementDirection);
        await Assert.That(output.DesiredSpeed).IsEqualTo(frame.DesiredSpeed);
        await Assert.That(output.Modified).IsFalse();
        var previous = owner.State;
        await Assert.That(() => owner.Update(frame with { FrameMicroseconds = 1_500_001 },
            [new(new(), new(0, 0, 29.999f))])).Throws<NotSupportedException>();
        await Assert.That(owner.State).IsEqualTo(previous);
    }

    [Test]
    public async Task NewGridEntriesAreConsumedAtTheSourceRefreshAndARefusalDoesNotCommitTheClock()
    {
        using var owner = new ZoneEmptyCrowdAvoidance(new());
        owner.Update(Frame(), []);
        ZoneEmptyCrowdCandidate[] entering = [new(new(), new(1, 0, 0))];
        var retained = owner.Update(Frame() with { FrameMicroseconds = 1_500_000 }, entering);
        await Assert.That(retained.CensusRefreshed).IsFalse();
        await Assert.That(owner.State.LastCensusMicroseconds).IsEqualTo(1_000_000L);
        var before = owner.State;
        await Assert.That(() => owner.Update(Frame() with { FrameMicroseconds = 1_500_001 }, entering)).Throws<NotSupportedException>();
        await Assert.That(owner.State).IsEqualTo(before);
        var refreshed = owner.Update(Frame() with { FrameMicroseconds = 1_500_001 }, []);
        await Assert.That(refreshed.CensusRefreshed).IsTrue();
    }

    [Test]
    public async Task ActualAlertnessFlagsAreRequiredOnlyWhenTheNativeEntryPredicateDependsOnThem()
    {
        using var owner = new ZoneEmptyCrowdAvoidance(new());
        await Assert.That(() => owner.Update(Frame() with { Alertness = 2 }, [])).Throws<NotSupportedException>();
        await Assert.That(owner.State.WasMoving).IsFalse();
        var skipped = owner.Update(Frame() with { Alertness = 2, Actor1593 = true, Actor1594 = false }, []);
        await Assert.That(skipped.SolverRebuilt).IsFalse();
        await Assert.That(owner.State.WasMoving).IsFalse();
        var radiusSkip = owner.Update(Frame() with { Alertness = 2, AvoidanceRadius = 16 }, []);
        await Assert.That(radiusSkip.CensusRefreshed).IsFalse();
        var allowed = owner.Update(Frame() with { Alertness = 2, Actor1594 = true }, []);
        await Assert.That(allowed.SolverRebuilt).IsTrue();
    }

    [Test]
    public async Task PartialUpdatesApplyTheExistingEmptySolverWhileLowPriorityForcesTheRealBasisReset()
    {
        using var owner = new ZoneEmptyCrowdAvoidance(new(), 5,
            new(1_000_000, true, .75f, 2, new(10, 20, 30), Vector3.UnitX, Vector3.UnitY, 4));
        var partial = owner.Update(Frame() with { FullUpdate = false }, []);
        await Assert.That(partial.SolverRebuilt).IsFalse();
        await Assert.That(partial.State.ObstructionTimer).IsEqualTo(1.9f);
        await Assert.That(partial.State.SteeringSide).IsEqualTo(0f);
        await Assert.That(partial.State.SolverOrigin).IsEqualTo(new Vector3(10, 20, 30));
        var full = owner.Update(Frame() with { FullUpdate = false, UpdatePriority = 1, ViewDirection = new(3, 4, 7) }, []);
        await Assert.That(full.SolverRebuilt).IsTrue();
        await Assert.That(full.State.ObstructionTimer).IsEqualTo(1.8f);
        await Assert.That(full.State.SolverOrigin).IsEqualTo(Vector3.Zero);
        await Assert.That(full.State.SolverForward).IsEqualTo(new Vector3(.6f, .8f, 0));
        await Assert.That(full.State.SolverSide).IsEqualTo(new Vector3(.8f, -.6f, 0));
        await Assert.That(full.State.SteeringSide).IsEqualTo(0f);
        await Assert.That(partial.State.ObstructionTimer).IsEqualTo(1.9f);
    }

    [Test]
    public async Task RetirementRejectsTheOldOwnerAndReentryDoesNotAliasAnOldObjectWithTheSameEntityId()
    {
        var old = new ZoneAiObjectState { EntityId = 9 };
        using var retired = new ZoneEmptyCrowdAvoidance(old);
        retired.Update(Frame(), []); retired.Dispose();
        await Assert.That(() => retired.Update(Frame(), [])).Throws<ObjectDisposedException>();
        using var entered = new ZoneEmptyCrowdAvoidance(new() { EntityId = 9 });
        await Assert.That(entered.State.LastCensusMicroseconds).IsEqualTo(0L);
        await Assert.That(() => entered.Update(Frame(), [new(old, Vector3.Zero)])).Throws<NotSupportedException>();
        await Assert.That(entered.State.WasMoving).IsFalse();
    }

    private static ZoneEmptyCrowdAvoidanceFrame Frame() => new(Vector3.Zero, Vector3.UnitX, Vector3.UnitY,
        2, .5f, 0, 3, false, true, 1_000_000, .1f);
    private static JsonElement Get(JsonElement value, string name, JsonElement defaults) =>
        value.TryGetProperty(name, out var property) ? property : defaults.GetProperty(name);
    private static Vector3 Vector(JsonElement value)
    { var array = value.EnumerateArray().Select(item => item.GetSingle()).ToArray(); return new(array[0], array[1], array[2]); }
    private static ZoneEmptyCrowdAvoidanceState ReadState(JsonElement value, JsonElement defaults) => new(
        Get(value, "lastCensusMicroseconds", defaults).GetInt64(), Get(value, "wasMoving", defaults).GetBoolean(),
        Get(value, "steeringSide", defaults).GetSingle(), Get(value, "obstructionTimer", defaults).GetSingle(),
        Vector(Get(value, "solverOrigin", defaults)), Vector(Get(value, "solverForward", defaults)),
        Vector(Get(value, "solverSide", defaults)), Get(value, "solverRadius", defaults).GetSingle());
}
