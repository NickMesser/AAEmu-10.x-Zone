using System.Numerics;
using AAEmu.Zone.Actors;

namespace AAEmu.Zone.Ai;

public enum ZoneApproachPathDecision { Pending = 0, Found = 3, NoPath = 4 }

/// <summary>The explicit COPPathFind target and native CPipeUser.v688 request parameters.</summary>
public sealed record ZoneApproachPathRequest(object Target, float EndTolerance, float EndDistance);

public readonly record struct ZoneTraceGoalParameters(float EndAccuracy, bool ForceReturnPartialPath, bool StopOnAnimationStart);
/// <summary>3115C414 first admits active goals through31158DA4/v72, then reads v88; independent of physical velocity.</summary>
public readonly record struct ZoneApproachStuckInput(bool CanUpdate, bool HasActiveGoalSuppressingStuck, bool UnitStuck);

/// <summary>
/// Exact actor/path owners required by COPApproach. Implementations must bind these services to one actor generation.
/// Path and trace operations own their requests; a scene must not implement them with a fabricated direct route.
/// </summary>
public interface IZoneApproachGoalServices
{
    bool IsCurrent { get; }
    ZoneActorControlState Controls { get; }
    ZoneActorNavigationState Navigation { get; }
    ZoneActorLookState LookState { get; }
    object? Argument { get; }
    object? GetAttentionTarget();
    Vector3 GetPosition();
    Vector3 GetEntityPosition();
    Vector3 GetEntityPosition(object target);
    bool IsTargetEnabled(object target);
    int GetTargetType(object target);
    bool UsesPathfinder { get; }
    bool UsesThreeDimensionalNavigation { get; }
    ZoneApproachPathDecision PathDecision { get; set; }
    /// <summary>31159A64(false) for any allocated COPStick, and clear the actor+2832 ignore-end-direction flag.</summary>
    void PrepareApproach(bool pathFound);
    /// <summary>The actual AIUnitSystem path-area restriction; absence of a registered restriction is admissible.</summary>
    bool AdmitPathDestination(Vector3 targetPosition);
    IZoneGoalOperation CreatePathFind(ZoneApproachPathRequest request);
    IZoneGoalOperation CreateTrace(ZoneTraceGoalParameters parameters);
    /// <summary>3115D474: cancel request/exact-position state, clear CurrentPath and OriginalPath, and clear target2840.</summary>
    void ResetPath();
    /// <summary>31169B4C normal speed for current MovementUrgency and bSlowForStrafe=false.</summary>
    float GetMovementSpeed();
    void SetMovementContext(int context);
    /// <summary>Queue through the existing actor signal proxy; do not invoke Lua synchronously.</summary>
    void SubmitSignal(string name, bool senderIsActor, ZoneAiSignalData? data = null);
    ZoneApproachStuckInput ReadStuckInput();
}

/// <summary>310F6574/310E06A8 copied constructor inputs, independent of mutable Lua definitions.</summary>
public readonly record struct ZoneApproachGoalSettings(float EndDistance, float EndAccuracy, float Duration,
    bool UseLastOperationResult, bool LookAtLastOperationResult, bool ForceReturnPartialPath, bool StopOnAnimationStart)
{
    // Percent and the optional no-path text are serialized state; 310F6574 does not set them from the goal parameters.
    public bool Percent { get; init; }
    public string NoPathSignal { get; init; } = "";

    public static ZoneApproachGoalSettings FromParameters(ZoneGoalParameters parameters, Func<uint> random)
    {
        ArgumentNullException.ThrowIfNull(parameters); ArgumentNullException.ThrowIfNull(random);
        var duration = (parameters.Integer & 0x4000) != 0 ? MathF.Abs(parameters.First) : 0;
        var distance = (parameters.Integer & 0x4000) != 0 ? 0 : parameters.First;
        if (parameters.Vector.X > .01f)
        {
            var variation = (float)(random() % 10) / 9 * parameters.Vector.X;
            if (distance <= 0)
            { var sum = variation + distance; distance = sum >= 0 ? 0 : sum; }
            else
            { var difference = distance - variation; distance = difference <= 0 ? 0 : difference; }
        }
        return new(distance, parameters.Second, duration, (parameters.Integer & 1) != 0,
            (parameters.Integer & 2) != 0, (parameters.Integer & 0x400) != 0, (parameters.Integer & 0x8000) != 0);
    }
}

/// <summary>310E4F88 COPApproach orchestration over real pathfinding and trace operations.</summary>
public sealed class ZoneApproachGoal : IZoneGoalOperation
{
    private readonly IZoneApproachGoalServices _services;
    private readonly Func<long> _frameMicroseconds;
    private IZoneGoalOperation? _pathFind, _trace;
    private ZoneApproachGoalSettings _settings;
    private bool _pathFound, _disposed, _executing;
    private ulong _epoch;
    private int _looseAttentionId;
    private long _stuckFrame;
    public float LastDistance { get; private set; }
    public float InitialDistance { get; private set; }
    public float StuckTime { get; private set; }
    public bool PathFound => _pathFound;

    public ZoneApproachGoal(ZoneApproachGoalSettings settings, IZoneApproachGoalServices services,
        Func<long> frameMicroseconds)
    {
        _settings = settings with { NoPathSignal = ZoneAiSignalName.CString(settings.NoPathSignal) };
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _frameMicroseconds = frameMicroseconds ?? throw new ArgumentNullException(nameof(frameMicroseconds));
        _stuckFrame = _frameMicroseconds();
    }

    public int Execute()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_executing) throw new InvalidOperationException("An approach operation cannot execute recursively.");
        if (!_services.IsCurrent) return 1;
        _executing = true; var epoch = _epoch;
        try { return ExecuteCore(epoch); }
        finally { _executing = false; }
    }

    private bool Active(ulong epoch) => !_disposed && epoch == _epoch && _services.IsCurrent;
    private int ExecuteCore(ulong epoch)
    {
        // The original recurses once after adopting a found path, re-reading the actual target and actor state.
        while (Active(epoch))
        {
            var attention = _services.GetAttentionTarget();
            if (!Active(epoch)) return 1;
            _services.PrepareApproach(_pathFound);
            if (!Active(epoch)) return 1;
            var target = attention is not null && !_settings.UseLastOperationResult ? attention : _services.Argument;
            if (target is null) return Finish(3);
            var enabled = _services.IsTargetEnabled(target);
            if (!Active(epoch)) return 1;
            if (!enabled) return Finish(3);
            var position = _services.GetPosition();
            if (!Active(epoch)) return 1;
            Vector3 targetPosition;
            if (_services.PathDecision == ZoneApproachPathDecision.Found && _settings.ForceReturnPartialPath)
                targetPosition = _services.Navigation.CurrentPath.Count == 0 ? default : _services.Navigation.CurrentPath.Points[^1].Position;
            else targetPosition = _services.GetEntityPosition(target);
            if (!Active(epoch)) return 1;
            if (_looseAttentionId == 0 && _settings.LookAtLastOperationResult && _services.Argument is { } argument)
                _looseAttentionId = _services.LookState.SetLooseAttentionTarget(argument);
            if (_settings.Percent && InitialDistance == 0)
                InitialDistance = SpatialDistance(targetPosition, position) * _settings.EndDistance;
            if (!_services.UsesPathfinder)
            {
                var delta = Subtract(position, targetPosition);
                var squared = delta.Y * delta.Y + delta.X * delta.X;
                if (_services.UsesThreeDimensionalNavigation) squared += delta.Z * delta.Z;
                var distance = MathF.Sqrt(squared);
                var endDistance = GetEndDistance();
                if (!Active(epoch)) return 1;
                if (endDistance > distance) { InitialDistance = 0; return Finish(2); }
                var entityPosition = _services.GetEntityPosition();
                if (!Active(epoch)) return 1;
                delta = Subtract(targetPosition, entityPosition);
                var inverse = 1 / MathF.Sqrt(((delta.X * delta.X + delta.Y * delta.Y) + delta.Z * delta.Z) + 1.1754943508222875e-38f);
                _services.Controls.MovementDirection = new(delta.X * inverse, delta.Y * inverse, delta.Z * inverse);
                _services.SetMovementContext(4);
                if (!Active(epoch)) return 1;
                LastDistance = distance;
                return 1;
            }
            if (_pathFound && _pathFind is null) return Finish(3);
            if (!_pathFound && _pathFind is null)
            {
                var admitted = _services.AdmitPathDestination(targetPosition);
                if (!Active(epoch)) return 1;
                if (!admitted) return Finish(3);
                var endTolerance = _settings.ForceReturnPartialPath || _settings.EndAccuracy < 0 ? float.MaxValue : _settings.EndAccuracy;
                var distance = GetEndDistance();
                if (!Active(epoch)) return 1;
                var finder = _services.CreatePathFind(new(target, endTolerance, distance));
                if (!Active(epoch)) { finder.Dispose(); return 1; }
                _pathFind = finder;
                _services.PathDecision = ZoneApproachPathDecision.Pending;
                if (!Active(epoch)) return 1;
                var result = finder.Execute();
                if (!Active(epoch)) return 1;
                if (result != 1 && _services.PathDecision == ZoneApproachPathDecision.NoPath) return NoPath();
                return 1;
            }
            if (_pathFound)
            {
                if (_trace is null)
                {
                    var type = _services.GetTargetType(target);
                    if (!Active(epoch)) return 1;
                    if (type == 13 && (_settings.EndDistance > 0 || _settings.Duration > 0))
                        _settings = _settings with { EndDistance = 0, Duration = 0 };
                    if (_services.Navigation.OriginalPath.Count == 0)
                        throw new InvalidOperationException("A found approach path requires its actual OriginalPath endpoint.");
                    var endpoint = _services.Navigation.OriginalPath.Points[^1];
                    var offset = SpatialDistance(endpoint.Position, _services.Navigation.CurrentPath.CreationParameters.End);
                    var distance = GetEndDistance();
                    if (!Active(epoch)) return 1;
                    if (endpoint.NavigationType == 128 || offset <= distance + 2)
                        _services.SubmitSignal("OnPathFound", false);
                    else _services.SubmitSignal("OnEndPathOffset", true, new(FloatValue: offset - distance));
                    if (!Active(epoch)) return 1;
                    var trace = _services.CreateTrace(new(_settings.EndAccuracy, _settings.ForceReturnPartialPath, _settings.StopOnAnimationStart));
                    if (!Active(epoch)) { trace.Dispose(); return 1; }
                    _trace = trace;
                }
                var currentTrace = _trace;
                var result = currentTrace.Execute();
                if (!Active(epoch)) return 1;
                UpdateStuck(epoch);
                if (!Active(epoch) || !ReferenceEquals(currentTrace, _trace)) return 1;
                if (result != 1) return Finish(result);
            }
            switch (_services.PathDecision)
            {
                case ZoneApproachPathDecision.Pending:
                    _pathFind!.Execute();
                    return 1;
                case ZoneApproachPathDecision.Found when !_pathFound:
                    _pathFound = true; StuckTime = 0; _stuckFrame = _frameMicroseconds();
                    continue;
                case ZoneApproachPathDecision.NoPath: return NoPath();
                default: return 1;
            }
        }
        return 1;
    }

    private float GetEndDistance()
    {
        if (_settings.Duration > 0)
        {
            var speed = _services.GetMovementSpeed();
            if (speed > 0) return -(speed * _settings.Duration);
        }
        else if (_settings.Percent) return InitialDistance;
        return _settings.EndDistance;
    }
    private int NoPath()
    {
        var epoch = _epoch;
        if (_settings.NoPathSignal.Length != 0) _services.SubmitSignal(_settings.NoPathSignal, false);
        if (!Active(epoch)) return 1;
        _services.Controls.MovementDirection = default;
        return Finish(3);
    }
    private void UpdateStuck(ulong epoch)
    {
        var now = _frameMicroseconds();
        if (!Active(epoch)) return;
        if (_services.Controls.DesiredSpeed * _services.Controls.DesiredSpeed > .0001f)
        {
            var input = _services.ReadStuckInput();
            if (!Active(epoch)) return;
            if (input.CanUpdate)
            {
                var seconds = (float)(now - _stuckFrame) * .000001f;
                StuckTime = input.HasActiveGoalSuppressingStuck || !input.UnitStuck ? StuckTime - seconds : seconds + StuckTime;
                // Preserve the native fabs-based scalar clamp rather than a replacement timeout policy.
                var lower = ((StuckTime + 3) - MathF.Abs(StuckTime - 3)) * .5f;
                StuckTime = (MathF.Abs(lower - 0) + (lower + 0)) * .5f;
            }
        }
        _stuckFrame = now;
        if (StuckTime > 1.5f)
        {
            StuckTime = 0; _stuckFrame = _frameMicroseconds();
            if (Active(epoch)) _services.SubmitSignal("OnPathStuck", true);
        }
    }
    private int Finish(int result) { ResetCore(true); return result; }
    private void ResetCore(bool resetActor)
    {
        _epoch++;
        var pathFind = _pathFind; var trace = _trace; _pathFind = null; _trace = null;
        pathFind?.Dispose(); trace?.Dispose(); InitialDistance = 0; _pathFound = false;
        if (resetActor && _services.IsCurrent)
        {
            _services.ResetPath();
            if (_services.IsCurrent && _settings.LookAtLastOperationResult)
            { _services.LookState.SetLooseAttentionTarget(null, _looseAttentionId); _looseAttentionId = 0; }
        }
        StuckTime = 0; _stuckFrame = _frameMicroseconds();
    }
    public void Reset() { ObjectDisposedException.ThrowIf(_disposed, this); ResetCore(true); }
    public void Dispose() { if (_disposed) return; _disposed = true; ResetCore(false); }
    private static Vector3 Subtract(Vector3 a, Vector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static float SpatialDistance(Vector3 a, Vector3 b)
    { var d = Subtract(a, b); return MathF.Sqrt((d.Y * d.Y + d.X * d.X) + d.Z * d.Z); }
}
