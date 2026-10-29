using System.Numerics;
using System.Text.Json;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Ai;

namespace AAEmu.Zone.Tests;

public sealed class ApproachGoalTests
{
    [Test]
    public async Task OriginalApproachAndPathFindInstructionsAgreeWithControlledSceneDecisions()
    {
        using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "AAEmu.Zone.Tests.Fixtures.npc-approach-native-controls.json")!;
        using var document = JsonDocument.Parse(stream);
        var errors = new List<string>();
        foreach (var control in document.RootElement.GetProperty("controls").EnumerateArray())
        {
            var request = control.GetProperty("request"); var expected = control.GetProperty("result");
            var label = control.GetProperty("name").GetString();
            var target = new Target(Vector(request, "targetPosition"))
            { Enabled = Flag(request, "enabled"), Type = request.GetProperty("targetType").GetInt32() };
            var scene = new Services
            {
                AttentionTarget = Flag(request, "hasTarget") ? target : null,
                Argument = Flag(request, "useArgument") ? target : null,
                UsesPathfinder = Flag(request, "usePathfinder"), UsesThreeDimensionalNavigation = Flag(request, "use3D"),
                AiPosition = Vector(request, "aiPosition"), EntityPosition = Vector(request, "entityPosition"),
                MovementSpeed = Number(request, "movementSpeed"), EndpointOffset = request.TryGetProperty("endpointOffset", out var offset) ? offset.GetSingle() : 2,
                RequestDecision = (ZoneApproachPathDecision)request.GetProperty("requestDecision").GetInt32(),
                TraceResult = request.TryGetProperty("traceResult", out var trace) ? trace.GetInt32() : 1
            };
            var settings = new ZoneApproachGoalSettings(Number(request, "endDistance"), Number(request, "endAccuracy"), Number(request, "duration"),
                Flag(request, "useArgument"), Flag(request, "lookArgument"), Flag(request, "forcePartial"), Flag(request, "stopAnimation"))
            { Percent = Flag(request, "percent") };
            using var goal = new ZoneApproachGoal(settings, scene, () => scene.Microseconds);
            var frames = expected.GetProperty("frames").EnumerateArray().ToArray();
            for (var index = 0; index < frames.Length; index++)
            {
                if (index == 1 && Flag(request, "asyncFound")) { scene.PublishPath(); scene.PathDecision = ZoneApproachPathDecision.Found; }
                if (index == 2 && Flag(request, "finish")) scene.TraceResult = 2;
                var frame = frames[index];
                if (goal.Execute() != frame.GetProperty("result").GetInt32()) errors.Add($"{label}/{index} result");
                if (goal.PathFound != Flag(frame, "pathFound")) errors.Add($"{label}/{index} pathFound");
                Compare(Number(frame, "initialDistance"), goal.InitialDistance, "initialDistance");
                Compare(Number(frame, "lastDistance"), goal.LastDistance, "lastDistance");
                var direction = Vector(frame, "direction");
                Compare(direction.X, scene.Controls.MovementDirection.X, "direction.x");
                Compare(direction.Y, scene.Controls.MovementDirection.Y, "direction.y");
                Compare(direction.Z, scene.Controls.MovementDirection.Z, "direction.z");
                if (scene.Context != frame.GetProperty("context").GetInt32()) errors.Add(label + " context");
                if (scene.Navigation.CurrentPath.Count != frame.GetProperty("currentCount").GetInt32()) errors.Add(label + " currentCount");
                if (scene.Navigation.OriginalPath.Count != frame.GetProperty("originalCount").GetInt32()) errors.Add(label + " originalCount");
                if (scene.TraceExecutions != frame.GetProperty("traceCalls").GetInt32()) errors.Add(label + " traceCalls");
                if (scene.Signals.Count != frame.GetProperty("signalCount").GetInt32()) errors.Add(label + " signalCount");
                if (scene.Submissions.Count != frame.GetProperty("requestCount").GetInt32()) errors.Add(label + " requestCount");
            }
            var signals = expected.GetProperty("signals").EnumerateArray().ToArray();
            for (var index = 0; index < Math.Min(signals.Length, scene.Signals.Count); index++)
            {
                var signal = signals[index];
                if (scene.Signals[index].Name != signal.GetProperty("name").GetString()) errors.Add(label + " signalName");
                if (scene.Signals[index].SenderIsActor != Flag(signal, "senderIsActor")) errors.Add(label + " sender");
                if (signal.GetProperty("floatValue").ValueKind != JsonValueKind.Null)
                    Compare(Number(signal, "floatValue"), scene.Signals[index].Data?.FloatValue ?? float.NaN, "signalData");
            }
            var submissions = expected.GetProperty("requests").EnumerateArray().ToArray();
            for (var index = 0; index < Math.Min(submissions.Length, scene.Submissions.Count); index++)
            {
                var native = submissions[index]; var managed = scene.Submissions[index];
                Compare(Number(native, "endTolerance"), managed.EndTolerance, "endTolerance");
                Compare(Number(native, "endDistance"), managed.EndDistance, "endDistance");
                if (managed.Position != Vector(native, "position") || managed.Direction != Vector(native, "direction")) errors.Add(label + " submission vectors");
                if (managed.AllowDangerousDestination != Flag(native, "allowDangerous")) errors.Add(label + " dangerous");
                if (managed.ForceBuildingId != native.GetProperty("buildingId").GetInt32()) errors.Add(label + " building");
                if (!ReferenceEquals(managed.Target, target) || !Flag(native, "targetIsActual")) errors.Add(label + " target identity");
            }
            void Compare(float wanted, float actual, string field)
            { if (!float.IsFinite(actual) || MathF.Abs(wanted - actual) > .000001f) errors.Add($"{label} {field}: {wanted:R} != {actual:R}"); }
        }
        await Assert.That(string.Join(Environment.NewLine, errors)).IsEqualTo("");
    }

    [Test]
    public async Task ConstructorUsesNativeFlagsAndConsumesOnlyTheAuthoredVariation()
    {
        var draws = 0;
        uint Random() { draws++; return 9; }
        var settings = ZoneApproachGoalSettings.FromParameters(new()
        { First = 2, Second = -1, Integer = 1 | 2 | 0x400 | 0x8000, Text = "not-copied-by-native-factory" }, Random);
        await Assert.That(draws).IsEqualTo(0);
        await Assert.That(settings).IsEqualTo(new ZoneApproachGoalSettings(2, -1, 0, true, true, true, true));
        await Assert.That(settings.NoPathSignal).IsEqualTo("");
        settings = ZoneApproachGoalSettings.FromParameters(new() { First = -2, Second = 1, Vector = new(1, 0, 0) }, Random);
        await Assert.That(settings.EndDistance).IsEqualTo(-1);
        await Assert.That(draws).IsEqualTo(1);
        settings = ZoneApproachGoalSettings.FromParameters(new() { First = -3, Integer = 0x4000, Vector = new(1, 0, 0) }, Random);
        await Assert.That(settings.EndDistance).IsEqualTo(0);
        await Assert.That(settings.Duration).IsEqualTo(3);
    }

    [Test]
    public async Task PathDecisionWaitsThenRunsTheActualTraceAndReleasesChildrenOnCompletion()
    {
        var scene = new Services { AttentionTarget = new Target(new(20, 0, 0)) };
        var settings = new ZoneApproachGoalSettings(2, -1, 0, false, false, false, false);
        using var goal = new ZoneApproachGoal(settings, scene, () => scene.Microseconds);
        await Assert.That(goal.Execute()).IsEqualTo(1);
        await Assert.That(goal.PathFound).IsFalse();
        await Assert.That(scene.Submissions.Count).IsEqualTo(1);
        await Assert.That(scene.Submissions[0].EndDistance).IsEqualTo(2);
        await Assert.That(scene.Submissions[0].EndTolerance).IsEqualTo(float.MaxValue);
        await Assert.That(scene.TraceExecutions).IsEqualTo(0);
        await Assert.That(goal.Execute()).IsEqualTo(1);
        await Assert.That(goal.PathFound).IsTrue();
        await Assert.That(scene.TraceExecutions).IsEqualTo(1);
        await Assert.That(scene.Signals[0].Name).IsEqualTo("OnPathFound");
        scene.TraceResult = 2;
        await Assert.That(goal.Execute()).IsEqualTo(2);
        await Assert.That(scene.TraceDisposals).IsEqualTo(1);
        await Assert.That(scene.Navigation.CurrentPath.Count).IsEqualTo(0);
        await Assert.That(scene.Navigation.OriginalPath.Count).IsEqualTo(0);
        await Assert.That(goal.PathFound).IsFalse();
    }

    [Test]
    public async Task ExplicitTargetPathFinderCopiesRealDirectionIdentityAndPendingDecision()
    {
        var attention = new Target(new(9, 0, 0));
        var argument = new Target(new(30, 4, 6)) { Type = 1, Direction = new(0, -1, 0) };
        var scene = new Services { AttentionTarget = attention, Argument = argument, RequestDecision = ZoneApproachPathDecision.Pending };
        using var goal = new ZoneApproachGoal(new(2, .3f, 0, true, false, false, false), scene, () => scene.Microseconds);
        await Assert.That(goal.Execute()).IsEqualTo(1);
        await Assert.That(scene.Submissions[0].Target).IsSameReferenceAs(argument);
        await Assert.That(scene.Submissions[0].Position).IsEqualTo(argument.Position);
        await Assert.That(scene.Submissions[0].Direction).IsEqualTo(argument.Direction);
        await Assert.That(scene.Submissions[0].AllowDangerousDestination).IsFalse();
        await Assert.That(scene.Submissions[0].ForceBuildingId).IsEqualTo(-1);
        await Assert.That(goal.Execute()).IsEqualTo(1);
        await Assert.That(scene.Submissions.Count).IsEqualTo(1);
        scene.PublishPath(); scene.PathDecision = ZoneApproachPathDecision.Found;
        await Assert.That(goal.Execute()).IsEqualTo(1);
        await Assert.That(scene.TraceExecutions).IsEqualTo(1);
    }

    [Test]
    public async Task NoPathAndDisabledTargetsFailThroughTheirRealOwners()
    {
        var scene = new Services { AttentionTarget = new Target(new(10, 0, 0)), RequestDecision = ZoneApproachPathDecision.NoPath };
        using var goal = new ZoneApproachGoal(new(2, -1, 0, false, false, false, false), scene, () => scene.Microseconds);
        await Assert.That(goal.Execute()).IsEqualTo(3);
        await Assert.That(scene.Signals.Single().Name).IsEqualTo("OnNoPathFound");
        await Assert.That(scene.Navigation.CurrentPath.Count).IsEqualTo(0);
        await Assert.That(scene.TraceExecutions).IsEqualTo(0);
        scene.Signals.Clear(); ((Target)scene.AttentionTarget).Enabled = false;
        await Assert.That(goal.Execute()).IsEqualTo(3);
        await Assert.That(scene.Submissions.Count).IsEqualTo(1);
        await Assert.That(scene.Signals.Count).IsEqualTo(0);
    }

    [Test]
    public async Task NativeNoPathfinderLeafUsesStrictAiDistanceAndEntityBasedDirection()
    {
        var scene = new Services { UsesPathfinder = false, AttentionTarget = new Target(new(2, 0, 0)), EntityPosition = new(1, 0, 0) };
        using var goal = new ZoneApproachGoal(new(2, -1, 0, false, false, false, false), scene, () => scene.Microseconds);
        await Assert.That(goal.Execute()).IsEqualTo(1);
        await Assert.That(scene.Controls.MovementDirection).IsEqualTo(Vector3.UnitX);
        await Assert.That(goal.LastDistance).IsEqualTo(2);
        await Assert.That(scene.Context).IsEqualTo(4);
        scene.AiPosition = new(1, 0, 0);
        await Assert.That(goal.Execute()).IsEqualTo(2);
        await Assert.That(scene.Submissions.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DurationAndPercentFeedNativeEndDistanceInsteadOfInventingActorSpeed()
    {
        var scene = new Services { AttentionTarget = new Target(new(3, 4, 12)), MovementSpeed = 4 };
        using (var goal = new ZoneApproachGoal(new(2, -1, 3, false, false, false, false), scene, () => scene.Microseconds))
        {
            goal.Execute();
            await Assert.That(scene.Submissions[0].EndDistance).IsEqualTo(-12);
        }
        scene.Submissions.Clear();
        using (var goal = new ZoneApproachGoal(new(.5f, -1, 0, false, false, false, false) { Percent = true }, scene, () => scene.Microseconds))
        {
            goal.Execute();
            await Assert.That(goal.InitialDistance).IsEqualTo(6.5f);
            await Assert.That(scene.Submissions[0].EndDistance).IsEqualTo(6.5f);
        }
    }

    [Test]
    public async Task EndOffsetAndStuckSignalsUseCopiedDataAndStrictNativeBoundaries()
    {
        var scene = new Services { AttentionTarget = new Target(new(10, 0, 0)), EndpointOffset = 5, Stuck = new(true, false, true) };
        scene.Controls.DesiredSpeed = 1;
        using var goal = new ZoneApproachGoal(new(2, -1, 0, false, false, false, false), scene, () => scene.Microseconds);
        goal.Execute(); goal.Execute();
        await Assert.That(scene.Signals[0].Name).IsEqualTo("OnEndPathOffset");
        await Assert.That(scene.Signals[0].SenderIsActor).IsTrue();
        await Assert.That(scene.Signals[0].Data!.FloatValue).IsEqualTo(3);
        scene.Microseconds += 1_500_000;
        goal.Execute();
        await Assert.That(scene.Signals.Count).IsEqualTo(1);
        scene.Microseconds += 1;
        goal.Execute();
        await Assert.That(scene.Signals[^1].Name).IsEqualTo("OnPathStuck");
        await Assert.That(goal.StuckTime).IsEqualTo(0);
    }

    [Test]
    public async Task RetirementAndResetInsideCallbacksPreventLateTraceOrControlWrites()
    {
        var scene = new Services { AttentionTarget = new Target(new(10, 0, 0)) };
        using var goal = new ZoneApproachGoal(new(2, -1, 0, false, false, false, false), scene, () => scene.Microseconds);
        goal.Execute(); scene.OnSignal = () => scene.IsCurrent = false;
        await Assert.That(goal.Execute()).IsEqualTo(1);
        await Assert.That(scene.TraceExecutions).IsEqualTo(0);
        scene.IsCurrent = true; scene.OnSignal = null;
        goal.Reset(); goal.Execute();
        scene.OnTrace = goal.Reset;
        await Assert.That(goal.Execute()).IsEqualTo(1);
        await Assert.That(scene.TraceDisposals).IsEqualTo(1);
        await Assert.That(scene.Navigation.CurrentPath.Count).IsEqualTo(0);
    }

    private sealed class Target(Vector3 position)
    {
        public Vector3 Position { get; } = position;
        public Vector3 Direction { get; init; } = Vector3.UnitY;
        public bool Enabled { get; set; } = true;
        public int Type { get; init; } = 2;
    }
    private sealed class Services : IZoneApproachGoalServices, IZoneApproachPathFindServices, IZoneActorNavigationScene
    {
        public bool IsCurrent { get; set; } = true;
        public ZoneActorControlState Controls { get; } = new();
        public ZoneActorNavigationState Navigation { get; }
        public ZoneActorLookState LookState { get; }
        public object? Argument { get; set; }
        public object? AttentionTarget { get; set; }
        public Vector3 AiPosition { get; set; }
        public Vector3 EntityPosition { get; set; }
        public bool UsesPathfinder { get; set; } = true;
        public bool UsesThreeDimensionalNavigation { get; set; }
        public ZoneApproachPathDecision PathDecision { get; set; }
        public ZoneApproachPathDecision RequestDecision { get; set; } = ZoneApproachPathDecision.Found;
        public List<ZoneApproachPathSubmission> Submissions { get; } = [];
        public List<(string Name, bool SenderIsActor, ZoneAiSignalData? Data)> Signals { get; } = [];
        public int Context { get; set; }
        public int TraceExecutions { get; private set; }
        public int TraceDisposals { get; set; }
        public int TraceResult { get; set; } = 1;
        public long Microseconds { get; set; } = 1_000_000;
        public float MovementSpeed { get; set; }
        public float EndpointOffset { get; set; } = 2;
        public ZoneApproachStuckInput Stuck { get; set; }
        public Action? OnSignal { get; set; }
        public Action? OnTrace { get; set; }
        public Services() { Navigation = new(this); LookState = new(Controls); }
        public object? GetAttentionTarget() => AttentionTarget;
        public Vector3 GetPosition() => AiPosition;
        public Vector3 GetEntityPosition() => EntityPosition;
        public Vector3 GetEntityPosition(object target) => ((Target)target).Position;
        public bool IsTargetEnabled(object target) => ((Target)target).Enabled;
        public int GetTargetType(object target) => ((Target)target).Type;
        public Vector3 GetTargetDirection(object target) => ((Target)target).Direction;
        public void PrepareApproach(bool pathFound) { }
        public bool AdmitPathDestination(Vector3 position) => true;
        public IZoneGoalOperation CreatePathFind(ZoneApproachPathRequest request) => new ZoneApproachPathFindGoal(request, this);
        public IZoneGoalOperation CreateTrace(ZoneTraceGoalParameters parameters) => new Trace(this);
        public void ResetPath() { Navigation.CurrentPath.Clear(); Navigation.OriginalPath.Clear(); }
        public float GetMovementSpeed() => MovementSpeed;
        public void SetMovementContext(int context) => Context = context;
        public void SubmitSignal(string name, bool senderIsActor, ZoneAiSignalData? data = null)
        { Signals.Add((name, senderIsActor, data)); OnSignal?.Invoke(); }
        public ZoneApproachStuckInput ReadStuckInput() => Stuck;
        public void SubmitNoPathSignal() => SubmitSignal("OnNoPathFound", false);
        public void RequestPath(ZoneApproachPathSubmission submission)
        {
            Submissions.Add(submission); Navigation.CurrentPath.Clear(); Navigation.OriginalPath.Clear();
            PathDecision = RequestDecision;
            if (RequestDecision == ZoneApproachPathDecision.Found) PublishPath();
        }
        public void PublishPath()
        {
            var target = Submissions[^1].Position;
            Navigation.CurrentPath.PushBack(new(EntityPosition), true);
            Navigation.CurrentPath.PushBack(new(new(target.X - EndpointOffset, target.Y, target.Z)), true);
            Navigation.CurrentPath.CreationParameters = new() { End = target };
            Navigation.CopyCurrentToOriginal();
        }
        public bool CanStrafe() => false;
        public Vector3 GetTargetPosition(object target) => GetEntityPosition(target);
        private sealed class Trace(Services owner) : IZoneGoalOperation
        {
            private bool _disposed;
            public int Execute() { owner.TraceExecutions++; owner.OnTrace?.Invoke(); return owner.TraceResult; }
            public void Reset() { }
            public void Dispose() { if (_disposed) return; _disposed = true; owner.TraceDisposals++; }
        }
    }
    private static bool Flag(JsonElement value, string field) => value.TryGetProperty(field, out var found) && found.GetBoolean();
    private static float Number(JsonElement value, string field) => value.TryGetProperty(field, out var found) ? found.GetSingle() : 0;
    private static Vector3 Vector(JsonElement value, string field)
    { var values = value.GetProperty(field).EnumerateArray().Select(number => number.GetSingle()).ToArray(); return new(values[0], values[1], values[2]); }
}
