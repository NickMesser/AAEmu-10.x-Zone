using System.Numerics;
using System.Text.Json;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Ai;
using AAEmu.Zone.Navigation;

namespace AAEmu.Zone.Tests;

public sealed class NavigationPathDecisionTests
{
    [Test]
    public async Task AdaptiveUrgencyMatchesOriginalMathIncludingNanAndIndependentGoalHistory()
    {
        using var stream = typeof(NavigationPathDecisionTests).Assembly.GetManifestResourceStream(
            "AAEmu.Zone.Tests.Fixtures.npc-adaptive-urgency-native-controls.json")!;
        using var json = JsonDocument.Parse(stream);
        var checkedControls = 0;
        foreach (var control in json.RootElement.GetProperty("controls").EnumerateArray())
        {
            float Number(string name) => BitConverter.UInt32BitsToSingle(control.GetProperty(name).GetUInt32());
            var state = new ZoneActorControlState();
            state.SetAdaptiveUrgency(Number("minimumBits"), Number("maximumBits"), Number("scaleBits"));
            state.AdaptiveUrgencyMaxPathLength = Number("historyBits");
            var urgency = .4f; var external = Number("initialExternalBits");
            if (control.GetProperty("externalHistory").GetBoolean())
                ZoneActorMovementControl.AdjustAdaptiveUrgency(state, ref urgency, Number("lengthBits"), ref external);
            else ZoneActorMovementControl.AdjustAdaptiveUrgency(state, ref urgency, Number("lengthBits"));
            await SameFloat(urgency, Number("urgencyBits"));
            await SameFloat(state.AdaptiveUrgencyMaxPathLength, Number("actorHistoryBits"));
            await SameFloat(external, Number("externalHistoryBits"));
            checkedControls++;
        }
        await Assert.That(checkedControls).IsEqualTo(128);
    }

    [Test]
    public async Task OriginalCompleteDecisionFlowMatchesDrySamplingTrimmingUrgencyVersionsAndDefaultEmptyObstacleQueries()
    {
        using var stream = typeof(NavigationPathDecisionTests).Assembly.GetManifestResourceStream(
            "AAEmu.Zone.Tests.Fixtures.npc-path-decision-native-controls.json")!;
        using var json = JsonDocument.Parse(stream);
        var checkedControls = 0;
        foreach (var control in json.RootElement.GetProperty("controls").EnumerateArray())
        {
            var settings = control.GetProperty("settings");
            var found = !settings.TryGetProperty("found", out var foundValue) || foundValue.GetBoolean();
            var candidate = Path(control.GetProperty("points").EnumerateArray().Select(Vector).ToArray());
            candidate.Version = 70;
            candidate.CreationParameters = candidate.CreationParameters with
            { EndDistance = settings.TryGetProperty("end_distance", out var trimValue) ? trimValue.GetSingle() : 0 };
            var navigation = new ZoneActorNavigationState(new Scene());
            navigation.CurrentPath.CopyFrom(Path([new(90, 0, 0), new(91, 0, 0)])); navigation.CurrentPath.Version = 10;
            navigation.OriginalPath.CopyFrom(Path([new(80, 0, 0), new(81, 0, 0)])); navigation.OriginalPath.Version = 20;
            var controls = new ZoneActorControlState { MovementUrgency = .4f };
            controls.SetAdaptiveUrgency(.21f, 1.4f, 10);
            var profile = Profile() with { AdjustAroundDynamicObstacles = !settings.TryGetProperty("dyn", out var dynamicValue) || dynamicValue.GetBoolean() };
            var points = found ? ZoneNavigationPathDecision.GetOrdinaryWaterProbePoints(candidate) : Array.Empty<Vector3>();
            var nativeWater = control.GetProperty("waterCalls");
            await Assert.That(nativeWater.GetArrayLength()).IsEqualTo(points.Count * 2);
            List<ZoneNavigationWaterProbe> measurements = [];
            for (var index = 0; index < points.Count; index++)
            {
                await SameVector(points[index], Vector(nativeWater[index * 2]));
                var upper = ZoneNavigationPathDecision.CreateUpperClearanceRay(points[index], 1.8f);
                var originalUpper = control.GetProperty("upperRays")[index];
                await SameVector(upper.Origin, Vector(originalUpper.GetProperty("origin")));
                await SameVector(upper.Displacement, Vector(originalUpper.GetProperty("displacement")));
                var upHit = settings.TryGetProperty("upward_hit", out var up)
                    ? new ZoneNavigationClearanceHit(up.GetSingle(), new(points[index].X, points[index].Y, points[index].Z + up.GetSingle()))
                    : (ZoneNavigationClearanceHit?)null;
                var complete = ZoneNavigationPathDecision.CompleteUpperClearance(points[index], .8f, 1.8f, -100, upHit);
                var originalLower = control.GetProperty("lowerRays")[index];
                await SameVector(complete.DownwardRay.Origin, Vector(originalLower.GetProperty("origin")));
                await SameVector(complete.DownwardRay.Displacement, Vector(originalLower.GetProperty("displacement")));
                var downHit = settings.TryGetProperty("downward_hit", out var down)
                    ? new ZoneNavigationClearanceHit(complete.DownwardRay.Origin.Z - down.GetSingle(), new(points[index].X, points[index].Y, down.GetSingle()))
                    : (ZoneNavigationClearanceHit?)null;
                measurements.Add(new(points[index], -100, ZoneNavigationPathDecision.CompleteVerticalSpan(complete, downHit, 0)));
            }
            ZoneNavigationObstacleCensus? census = null;
            if (found && candidate.HasPath && profile.AdjustAroundDynamicObstacles)
            {
                var adjusted = candidate.Clone();
                if (MathF.Abs(candidate.CreationParameters.EndDistance) > .0001f)
                    adjusted.TrimEnd(candidate.CreationParameters.EndDistance);
                var query = ZoneNavigationPathDecision.CreateObstacleQuery(adjusted, Vector3.Zero, 4, 2);
                var queries = control.GetProperty("queries");
                await SameVector(query.ActorPosition, Vector(queries[0].GetProperty("point")));
                await SameFloat(query.ActorRadius, queries[0].GetProperty("radius").GetSingle());
                await SameVector(query.PhysicalBounds.Minimum, Vector(queries[1].GetProperty("bounds")[0]));
                await SameVector(query.PhysicalBounds.Maximum, Vector(queries[1].GetProperty("bounds")[1]));
                await Assert.That(queries[1].GetProperty("mask").GetUInt32()).IsEqualTo(ZoneNavigationObstacleQuery.PhysicalMask);
                census = new(query, [new(Actor, 5, Vector3.Zero, null)], [], []);
            }
            var result = found ? ZoneNavigationPathDecision.PublishOrdinary(navigation, controls, candidate, profile,
                Actor, Vector3.Zero, measurements, census) : ZoneNavigationPathDecision.PublishNoPath(navigation, profile);
            await Assert.That((int)result.Decision).IsEqualTo(control.GetProperty("decision").GetInt32());
            await Assert.That(result.CachedNavigationType).IsEqualTo(control.GetProperty("cachedNavigationType").GetUInt32());
            if (found && candidate.HasPath) await SameVector(result.RequestedEnd!.Value, Vector(control.GetProperty("storedEnd")));
            await SamePath(navigation.CurrentPath, control.GetProperty("current"));
            await SamePath(navigation.OriginalPath, control.GetProperty("original"));
            await SameFloat(controls.MovementUrgency, control.GetProperty("urgency").GetSingle());
            await SameFloat(controls.AdaptiveUrgencyMaxPathLength, control.GetProperty("maximumPathLength").GetSingle());
            checkedControls++;
        }
        await Assert.That(checkedControls).IsEqualTo(40);
    }

    [Test]
    public async Task DryAndEmptyCensusProofIsRequiredBeforeAnyPathOrControlPublication()
    {
        var navigation = new ZoneActorNavigationState(new Scene());
        navigation.CurrentPath.CopyFrom(Path([Vector3.Zero, Vector3.UnitX]));
        navigation.OriginalPath.CopyFrom(navigation.CurrentPath);
        var oldCurrent = navigation.CurrentPath.Version; var oldOriginal = navigation.OriginalPath.Version;
        var controls = new ZoneActorControlState { MovementUrgency = .4f };
        var candidate = Path([Vector3.Zero, new(5, 0, 0)]);
        var profile = Profile();
        var points = ZoneNavigationPathDecision.GetOrdinaryWaterProbePoints(candidate);
        var dry = points.Select(point => new ZoneNavigationWaterProbe(point, -10, new(20, 0))).ToArray();
        await Assert.That(() => ZoneNavigationPathDecision.PublishOrdinary(navigation, controls, candidate, profile,
            Actor, Vector3.Zero, dry, null)).Throws<NotSupportedException>();
        var query = ZoneNavigationPathDecision.CreateObstacleQuery(candidate, Vector3.Zero, 4, 2);
        var census = new ZoneNavigationObstacleCensus(query, [], [], []);
        var wet = dry.ToArray(); wet[1] = wet[1] with { WaterLevel = 1 };
        await Assert.That(() => ZoneNavigationPathDecision.PublishOrdinary(navigation, controls, candidate, profile,
            Actor, Vector3.Zero, wet, census)).Throws<NotSupportedException>();
        await Assert.That(navigation.CurrentPath.Version).IsEqualTo(oldCurrent);
        await Assert.That(navigation.OriginalPath.Version).IsEqualTo(oldOriginal);
        await Assert.That(controls.MovementUrgency).IsEqualTo(.4f);
        await Assert.That(controls.AdaptiveUrgencyMaxPathLength).IsEqualTo(0f);
    }

    [Test]
    public async Task CensusUsesNativeActorRangeAndPhysicalClassesWithoutInventingStaticDynamicObstacles()
    {
        var candidate = Path([Vector3.Zero, new(5, 0, 0)]);
        var query = ZoneNavigationPathDecision.CreateObstacleQuery(candidate, Vector3.Zero, 4, 2);
        var outside = new ZoneActorHandle(999, 1);
        var bounds = query.PhysicalBounds;
        var census = new ZoneNavigationObstacleCensus(query,
            [new(Actor, 5, Vector3.Zero, null), new(outside, 5, new(query.ActorRadius, 0, 0), null)],
            [new(new object(), 1, bounds), new(new object(), 8, bounds), new(new object(), 0x100, bounds)], []);
        var navigation = new ZoneActorNavigationState(new Scene()); var controls = new ZoneActorControlState();
        ZoneNavigationPathDecision.PublishOrdinary(navigation, controls, candidate, Profile(), Actor,
            Vector3.Zero, Dry(candidate), census);
        await Assert.That(navigation.CurrentPath.Points.Count).IsEqualTo(2);
        var inside = census with { Actors = [new(outside, 5, new(query.ActorRadius - .001f, 0, 0), null)] };
        await Assert.That(() => ZoneNavigationPathDecision.PublishOrdinary(navigation, controls, candidate, Profile(),
            Actor, Vector3.Zero, Dry(candidate), inside)).Throws<NotSupportedException>();
        var rigid = census with { PhysicalEntities = [new(new object(), 4, bounds)] };
        await Assert.That(() => ZoneNavigationPathDecision.PublishOrdinary(navigation, controls, candidate, Profile(),
            Actor, Vector3.Zero, Dry(candidate), rigid)).Throws<NotSupportedException>();
    }

    [Test]
    public async Task EmptyObstacleProcessorClearsBothAdjustmentHistoriesAndKeepsTheRequestedEndBeforeTrimming()
    {
        var navigation = new ZoneActorNavigationState(new Scene());
        navigation.CurrentPath.AddObjectAdjustedFor(new object()); navigation.OriginalPath.AddObjectAdjustedFor(new object());
        var candidate = Path([Vector3.Zero, new(10, 0, 0)]);
        candidate.AddObjectAdjustedFor(new object());
        candidate.CreationParameters = candidate.CreationParameters with { EndDistance = 3 };
        var adjusted = candidate.Clone(); adjusted.TrimEnd(3);
        var query = ZoneNavigationPathDecision.CreateObstacleQuery(adjusted, Vector3.Zero, 4, 2);
        var result = ZoneNavigationPathDecision.PublishOrdinary(navigation, new(), candidate, Profile(), Actor,
            Vector3.Zero, Dry(candidate), new(query, [], [], []));
        await Assert.That(result.RequestedEnd).IsEqualTo((Vector3?)new(10, 0, 0));
        await Assert.That(navigation.CurrentPath.Points[^1].Position).IsEqualTo(new Vector3(7, 0, 0));
        await Assert.That(navigation.CurrentPath.ObjectsAdjustedFor.Count + navigation.OriginalPath.ObjectsAdjustedFor.Count).IsEqualTo(0);
        await Assert.That(candidate.ObjectsAdjustedFor.Count).IsEqualTo(1);
    }

    [Test]
    public async Task NoPathClearsBothContainersAndReportsTheRealArgumentCancellationWorkToItsOwner()
    {
        var navigation = new ZoneActorNavigationState(new Scene());
        navigation.CurrentPath.CopyFrom(Path([Vector3.Zero, Vector3.One])); navigation.CopyCurrentToOriginal();
        var result = ZoneNavigationPathDecision.PublishNoPath(navigation, Profile());
        await Assert.That(result.Decision).IsEqualTo(ZoneApproachPathDecision.NoPath);
        await Assert.That(result.ClearArgument).IsTrue();
        await Assert.That(navigation.CurrentPath.Count + navigation.OriginalPath.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AvoidanceSphereOwnerImplementsTheOriginalReplacementRemovalAndRetirementProducer()
    {
        using var owner = new ZoneNavigationAvoidanceSpheres();
        await Assert.That(owner.Count).IsEqualTo(0);
        owner.Set(7, Vector3.One, 2); owner.Set(7, Vector3.Zero, 3);
        await Assert.That(owner.Copy().Single()).IsEqualTo(new ZoneNavigationAvoidanceSphere(7, Vector3.Zero, 3));
        var candidate = Path([Vector3.Zero, Vector3.One]);
        var query = ZoneNavigationPathDecision.CreateObstacleQuery(candidate, Vector3.Zero, 4, 2);
        await Assert.That(() => ZoneNavigationPathDecision.PublishOrdinary(new(new Scene()), new(), candidate, Profile(),
            Actor, Vector3.Zero, Dry(candidate), new(query, [], [], owner.Copy()))).Throws<NotSupportedException>();
        owner.Set(7, Vector3.One, 0);
        await Assert.That(owner.Count).IsEqualTo(0);
        owner.Dispose();
        await Assert.That(() => owner.Set(7, Vector3.Zero, 2)).Throws<ObjectDisposedException>();
    }

    [Test]
    [Arguments("flying")]
    [Arguments("underwater")]
    [Arguments("spatial")]
    [Arguments("actorTarget")]
    [Arguments("smartObject")]
    [Arguments("spline")]
    [Arguments("backoff")]
    [Arguments("combat")]
    public async Task UnownedSourceBranchesCannotPublishAnOrdinarySuccess(string branch)
    {
        var candidate = Path([Vector3.Zero, Vector3.UnitX]);
        var profile = branch switch
        {
            "flying" => Profile() with { FlyingCreature = true },
            "underwater" => Profile() with { UnderwaterCreature = true },
            "spatial" => Profile() with { ProxyIsSwimming = true },
            "actorTarget" => Profile() with { HasActorTargetRequest = true },
            "smartObject" => Profile() with { HasSmartObjectActorTarget = true },
            "spline" => Profile() with { SplineMode = 1 },
            "backoff" => Profile() with { AllowSlightBackoff = true },
            "combat" => Profile() with { PuppetAlertness = 2 },
            _ => throw new InvalidDataException(),
        };
        if (branch == "backoff") candidate.CreationParameters = candidate.CreationParameters with { IsDirectional = true };
        var navigation = new ZoneActorNavigationState(new Scene());
        var query = ZoneNavigationPathDecision.CreateObstacleQuery(candidate, Vector3.Zero, 4, 2);
        await Assert.That(() => ZoneNavigationPathDecision.PublishOrdinary(navigation, new(), candidate, profile,
            Actor, Vector3.Zero, Dry(candidate), new(query, [], [], []))).Throws<NotSupportedException>();
        await Assert.That(navigation.CurrentPath.Count + navigation.OriginalPath.Count).IsEqualTo(0);
        await Assert.That(navigation.CurrentPath.Version).IsEqualTo(-1);
        await Assert.That(navigation.OriginalPath.Version).IsEqualTo(-1);
    }

    private static ZoneActorHandle Actor => new(1, 1);
    private static ZoneNavigationPathDecisionProfile Profile() => new(134, false, false, false,
        false, 1, false, false, false, 0, 0, true, true, 4, 2);
    private static ZoneNavigationPath Path(Vector3[] points)
    {
        var path = new ZoneNavigationPath();
        foreach (var point in points) path.PushBack(new(point, 2), true);
        return path;
    }
    private static ZoneNavigationWaterProbe[] Dry(ZoneNavigationPath path) =>
        ZoneNavigationPathDecision.GetOrdinaryWaterProbePoints(path).Select(point => new ZoneNavigationWaterProbe(point, -100, new(-99, 0))).ToArray();
    private static Vector3 Vector(JsonElement element) => new(element[0].GetSingle(), element[1].GetSingle(), element[2].GetSingle());
    private static async Task SameFloat(float actual, float expected) =>
        await Assert.That(BitConverter.SingleToUInt32Bits(actual)).IsEqualTo(BitConverter.SingleToUInt32Bits(expected));
    private static async Task SameVector(Vector3 actual, Vector3 expected)
    { await SameFloat(actual.X, expected.X); await SameFloat(actual.Y, expected.Y); await SameFloat(actual.Z, expected.Z); }
    private static async Task SamePath(ZoneNavigationPath actual, JsonElement expected)
    {
        var points = expected.GetProperty("points");
        await Assert.That(actual.Count).IsEqualTo(points.GetArrayLength());
        for (var index = 0; index < actual.Count; index++) await SameVector(actual.Points[index].Position, Vector(points[index]));
        await Assert.That(actual.Version).IsEqualTo(expected.GetProperty("version").GetInt32());
        await SameFloat(actual.CurrentFraction, expected.GetProperty("fraction").GetSingle());
        await SameFloat(actual.DiscardedPathLength, expected.GetProperty("discardedLength").GetSingle());
    }
    private sealed class Scene : IZoneActorNavigationScene
    {
        public bool CanStrafe() => false;
        public Vector3 GetPosition() => Vector3.Zero;
        public object? GetAttentionTarget() => null;
        public Vector3 GetTargetPosition(object target) => throw new NotSupportedException();
    }
}
