using System.Numerics;
using AAEmu.Zone.Navigation;

namespace AAEmu.Zone.Ai;

public readonly record struct ZonePredictiveTraceFrame(Vector3 EntityPosition, Vector3 ActualVelocity,
    ZoneGroundTraceMovementAbility Ability, bool Use2D, bool GeneratePredictions);

/// <summary>The actual predictive branch selected by UsePredictiveFollowing and ai_PredictivePathFollowing.</summary>
public interface IZonePredictiveTraceGoalServices : IZoneGroundTraceGoalServices
{
    /// <summary>Borrowed actor-lifetime follower at native actor3984, bound to this actor's stable CurrentPath.</summary>
    ZonePredictivePathFollower PredictiveFollower { get; }
    void ValidatePredictiveTrace(ZoneTraceGoalParameters parameters);
    ZonePredictiveTraceFrame ReadPredictiveFrame();
    /// <summary>Actual actor2840 target type, absent when that target pointer is null.</summary>
    int? CurrentPathTargetType { get; }
    /// <summary>Copy actual prediction positions/velocities to proxy output, with source times(index+1)*.1f.</summary>
    void SetMovementPredictions(IReadOnlyList<ZonePredictivePathSample> predictions);
}

/// <summary>
/// 310EC118's predictive ground trace. It borrows the real actor follower, publishes only copied control/prediction
/// output, and delegates marker, navigation and avoidance effects to the same exact-generation scene authorities.
/// </summary>
public sealed class ZonePredictiveTraceGoal : IZoneGoalOperation
{
    private readonly IZonePredictiveTraceGoalServices _services;
    private readonly ZoneTraceGoalParameters _parameters;
    private readonly Func<long> _frameMicroseconds;
    private long _lastFrame = -1_000_000;
    private bool _disposed, _executing, _targetCreated;
    private ulong _epoch;

    public ZonePredictiveTraceGoal(ZoneTraceGoalParameters parameters, IZonePredictiveTraceGoalServices services,
        Func<long> frameMicroseconds)
    { _parameters = parameters; _services = services ?? throw new ArgumentNullException(nameof(services));
        _frameMicroseconds = frameMicroseconds ?? throw new ArgumentNullException(nameof(frameMicroseconds)); }
    private bool Active(ulong epoch) => !_disposed && epoch == _epoch && _services.IsCurrent;

    public int Execute()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_executing) throw new InvalidOperationException("A predictive trace cannot execute recursively.");
        if (!_services.IsCurrent) return 1;
        _executing = true; var epoch = _epoch;
        try
        {
            _services.ValidatePredictiveTrace(_parameters);
            if (!Active(epoch)) return 1;
            if (_lastFrame <= 0)
            {
                var vetoed = _services.BeginMove();
                if (!Active(epoch) || vetoed) return 1;
            }
            var now = _frameMicroseconds();
            var seconds = _lastFrame <= 0 ? .1f : (float)(now - _lastFrame) * .000001f;
            _lastFrame = now;
            if (seconds <= 0) return 1; //310EC118's early return, before physical/follower reads.
            if (!_targetCreated && _services.Navigation.CurrentPath.Count < 2)
            { _services.Controls.DesiredSpeed = 0; return Complete(); }
            var frame = _services.ReadPredictiveFrame();
            if (!Active(epoch)) return 1;
            if (!_targetCreated)
            { _services.SetTraceTarget(frame.EntityPosition); if (Active(epoch)) _targetCreated = true; }
            if (!Active(epoch)) return 1;
            var follower = _services.PredictiveFollower;
            if (!Active(epoch)) return 1;
            if (follower.IsDisposed || !ReferenceEquals(follower.Path, _services.Navigation.CurrentPath))
                throw new InvalidOperationException("Predictive trace requires this exact actor's live CurrentPath follower.");
            var speeds = _services.GetMovementSpeeds();
            if (!Active(epoch)) return 1;
            var lowerNormal = ((speeds.Normal + speeds.Maximum) - MathF.Abs(speeds.Normal - speeds.Maximum)) * .5f;
            var normal = (MathF.Abs(lowerNormal - speeds.Minimum) + (lowerNormal + speeds.Minimum)) * .5f;
            var creation = _services.Navigation.CurrentPath.CreationParameters;
            var endDistance = 0f;
            if (creation.ContinueMovingAtEnd)
            {
                var targetType = _services.CurrentPathTargetType;
                if (!Active(epoch)) return 1;
                if (targetType == 1) endDistance = 1;
            }
            follower.Parameters = new(normal, frame.Ability.PathRadius, frame.Ability.LookAhead,
                frame.Ability.MaximumAcceleration, frame.Ability.MaximumDeceleration, speeds.Minimum, speeds.Maximum,
                endDistance, !creation.ContinueMovingAtEnd && !creation.ClosedLoop, frame.Use2D);
            var output = follower.Update(frame.EntityPosition, frame.ActualVelocity, seconds, frame.GeneratePredictions);
            if (!Active(epoch)) return 1;
            var velocity = output.Velocity;
            var squared = (velocity.X * velocity.X + velocity.Y * velocity.Y) + velocity.Z * velocity.Z;
            var desiredSpeed = squared;
            var direction = Vector3.Zero;
            if (!(squared <= 1.1920928955078125e-7f))
            { desiredSpeed = MathF.Sqrt(squared); var inverse = 1 / desiredSpeed;
                direction = new(velocity.X * inverse, velocity.Y * inverse, velocity.Z * inverse); }
            var controls = _services.Controls;
            controls.DesiredSpeed = desiredSpeed; controls.MovementDirection = direction;
            controls.DistanceToPathEnd = output.RemainingDistance;
            _services.SetMovementPredictions(output.Predictions);
            if (!Active(epoch)) return 1;
            //Predictive trace does not call310E13D8 or write an ordinary movement-context1/3.
            if (output.ReachedEnd)
            {
                //310EBE18 sees the zero velocity/direction written by the follower, then finishes.
                controls.DesiredSpeed = 0;
                if (_services.PathDecision != ZoneApproachPathDecision.Pending) controls.DistanceToPathEnd = 0;
                return Complete();
            }
            _services.RefreshNavigationType();
            if (!Active(epoch)) return 1;
            _services.UpdateAvoidance(true, false);
            return 1;
        }
        finally { _executing = false; }
    }
    private int Complete()
    {
        if (_services.PathDecision == ZoneApproachPathDecision.Pending) return 1;
        var failed = _services.PathDecision == ZoneApproachPathDecision.NoPath;
        ResetCore(true); return failed ? 3 : 2;
    }
    private void ResetCore(bool resetActor)
    {
        _epoch++; _lastFrame = -1_000_000;
        try { if (resetActor && _services.IsCurrent) { _services.Controls.DesiredSpeed = 0; _services.ResetTrace(); } }
        finally { _services.ReleaseTraceTarget(); _targetCreated = false; }
        //The actor owns its follower. A completed/reset goal must not dispose that persistent owner.
    }
    public void Reset() { ObjectDisposedException.ThrowIf(_disposed, this); ResetCore(true); }
    public void Dispose() { if (_disposed) return; _disposed = true; ResetCore(false); }
}
