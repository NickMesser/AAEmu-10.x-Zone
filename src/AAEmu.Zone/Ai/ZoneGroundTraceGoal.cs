using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Navigation;

namespace AAEmu.Zone.Ai;

/// <summary>Actual MovementAbility fields consumed by310EC518; no replacement defaults or actor speed.</summary>
public readonly record struct ZoneGroundTraceMovementAbility(float LookAhead, float PathRadius, float SpeedLookAhead,
    float CornerSlowDown, float SlopeSlowDown, float MaximumAcceleration, float MaximumDeceleration, bool AdaptiveLookAhead);
public readonly record struct ZoneGroundTraceSpeeds(float Normal, float Minimum, float Maximum);
public readonly record struct ZoneGroundTraceLivingStatus(bool Flying, Vector3 GroundNormal);
public readonly record struct ZoneGroundTraceTurning(float ManeuverThreshold, bool Obstructed, Vector3 BodyDirection);
public readonly record struct ZoneGroundTraceFrame(Vector3 EntityPosition, Vector3 ActualVelocity,
    ZoneGroundTraceMovementAbility Ability, uint NavigationType, ZoneGroundTraceLivingStatus? LivingStatus,
    ZoneGroundTraceTurning? Turning = null);

/// <summary>Required scene authorities for the ordinary ground COPTrace leaf.</summary>
public interface IZoneGroundTraceGoalServices
{
    bool IsCurrent { get; }
    ZoneActorControlState Controls { get; }
    ZoneActorNavigationState Navigation { get; }
    ZoneApproachPathDecision PathDecision { get; }
    /// <summary>
    /// Admit only the actual ordinary ground branch: no vehicle, 3D/path-follower alternative, actor-target/exact
    /// positioning, smart-object route, active path regeneration, or unowned trace-stop state. Refuse absent owners.
    /// </summary>
    void ValidateOrdinaryGroundTrace(ZoneTraceGoalParameters parameters);
    ZoneGroundTraceFrame ReadFrame();
    /// <summary>CPuppet31169B4C using actual current urgency and AllowStrafing; these are not authored stance maxima.</summary>
    ZoneGroundTraceSpeeds GetMovementSpeeds();
    /// <summary>CAISystem.OnMove event. Return true when a registered handler vetoes this trace update.</summary>
    bool BeginMove();
    /// <summary>Allocate/update the actual trace-owned type2 positional AI object and actor+2376 reference.</summary>
    void SetTraceTarget(Vector3 position);
    void ReleaseTraceTarget();
    /// <summary>310EBE18 reclassifies the actual entity position with the actor's navigation capabilities.</summary>
    void RefreshNavigationType();
    /// <summary>
    ///3116E80C actual nearby-object/actor avoidance, including its control and obstruction outputs.
    ///An empty-candidate certificate must come from the real scene; unsupported candidates must not be ignored.
    /// </summary>
    void UpdateAvoidance(bool fullUpdate, bool ignoreTarget);
    /// <summary>310DC0B8 through3115D474: cancel request/exact-position state and clear both paths plus target2840.</summary>
    void ResetTrace();
    void SetMovementContext(int context);
}

public readonly record struct ZoneGroundTraceMotion(bool EndReached, Vector3 Direction, float DesiredSpeed,
    float DistanceToPathEnd, Vector3 SteeringPosition, ZoneNavigationSteeringResult Steering);

/// <summary>310EC518 ordinary ground speed/steering leaf, independent of goal scheduling and pose publication.</summary>
public static class ZoneGroundTraceMath
{
    /// <summary>310E13D8's certified stateless branch. The active turn/reverse state requires its own entity owner.</summary>
    public static bool NeedsTurnInPlace(ZoneGroundTraceFrame frame, Vector3 direction, float urgency, float priorSpeed)
    {
        if (MathF.Abs(priorSpeed) < .001f) return false;
        var turning = frame.Turning ?? throw new NotSupportedException("Ground trace requires the actual maneuver threshold/body direction for turn-in-place admission.");
        if (turning.ManeuverThreshold >= 1 || turning.Obstructed) return false;
        var body = turning.BodyDirection;
        if (urgency < 0) body = new(body.X * -1, body.Y * -1, body.Z);
        body = Normalize(new(body.X, body.Y, 0)); direction = Normalize(new(direction.X, direction.Y, 0));
        //The original COMISS/JBE enters the turn branch for unordered comparisons too.
        return !((direction.Y * body.Y + direction.X * body.X) + direction.Z * body.Z > turning.ManeuverThreshold);
    }

    public static ZoneGroundTraceMotion Update(ZoneNavigationPath path, ZoneGroundTraceFrame frame,
        ZoneGroundTraceSpeeds speeds, float urgency, float priorSpeed, float seconds, float endAccuracy)
    {
        ArgumentNullException.ThrowIfNull(path);
        var ability = frame.Ability;
        var steering = ZoneNavigationPathSteering.Update(path, frame.EntityPosition, frame.ActualVelocity,
            ability.LookAhead, ability.PathRadius, seconds, ability.AdaptiveLookAhead, true);
        var distanceToEnd = steering.RemainingDistance <= 0 ? 0 : steering.RemainingDistance;
        var remaining = steering.RemainingDistance - -path.DiscardedPathLength;
        if (!steering.HasPath || !(remaining > .1f))
            return new(true, default, priorSpeed, distanceToEnd, default, steering);
        var direction = Normalize(new(steering.Direction.X, steering.Direction.Y, 0));
        var segmentDirection = Normalize(new(steering.SegmentDirection.X, steering.SegmentDirection.Y, 0));
        var cornerScale = 1f; var accuracyScale = 1f; var slopeScale = 1f;
        float speedLookAhead;
        if (ability.SpeedLookAhead >= 0)
        {
            var velocity = frame.ActualVelocity;
            speedLookAhead = MathF.Sqrt((velocity.X * velocity.X + velocity.Y * velocity.Y) + velocity.Z * velocity.Z) * ability.SpeedLookAhead;
        }
        else speedLookAhead = -((ability.SpeedLookAhead * ability.LookAhead) * urgency);
        if (speedLookAhead > 0)
        {
            var curve = ZoneNavigationPathSteering.GetLookAheadCurvature(path, speedLookAhead, true);
            var dot = curve.MinimumDot;
            var currentDot = (segmentDirection.X * direction.X + segmentDirection.Y * direction.Y) + segmentDirection.Z * direction.Z;
            if (dot > currentDot) dot = currentDot;
            if (curve.HasPath)
                cornerScale = (1 - (1 - ability.CornerSlowDown * 2)) * dot + (1 - ability.CornerSlowDown * 2);
            if (!(endAccuracy < 0))
            {
                var denominator = speedLookAhead * 2;
                var numerator = (endAccuracy + remaining) - speedLookAhead * .2f;
                if (!(denominator <= .1f || denominator <= numerator))
                {
                    accuracyScale = numerator / denominator;
                    if (.1f <= accuracyScale)
                    { if (!(accuracyScale <= 1)) accuracyScale = 1; }
                    else accuracyScale = .1f;
                }
            }
        }
        if (ability.SlopeSlowDown > 0)
        {
            if (frame.NavigationType == 4)
            {
                var horizontal = MathF.Sqrt(steering.Direction.Y * steering.Direction.Y + steering.Direction.X * steering.Direction.X);
                if (horizontal > 0 && steering.Direction.Z < 0)
                {
                    slopeScale = 1 - (-((steering.Direction.Z / horizontal) * ability.SlopeSlowDown) / .5f);
                    if (.5f > slopeScale) slopeScale = .5f;
                    else if (slopeScale > 1) slopeScale = 1;
                }
            }
            if (frame.LivingStatus is { Flying: false } living)
            {
                var normal = living.GroundNormal;
                var dot = (normal.Y * direction.Y + normal.X * direction.X) + normal.Z * direction.Z;
                if (dot < -.99f) dot = -.99f;
                else if (dot > .99f) dot = .99f;
                var scale = (dot / (1 - dot * dot) + 1) * ability.SlopeSlowDown;
                if (.5f > scale) scale = .5f;
                if (scale < 1 && slopeScale > scale) slopeScale = scale;
            }
        }
        var combined = cornerScale;
        if (combined > accuracyScale) combined = accuracyScale;
        if (combined > slopeScale) combined = slopeScale;
        if (combined > 1) combined = 1;
        if (combined >= 0) { if (combined > 1) combined = 1; }
        else combined = 0;
        var desired = (1 - combined) * speeds.Minimum + combined * speeds.Normal;
        float speed;
        if (urgency == 0 && desired == 0)
        {
            var magnitude = MathF.Abs(priorSpeed);
            var sign = priorSpeed > 0 ? 1 : priorSpeed < 0 ? -1 : 0;
            if (magnitude > 2) speed = sign * 2;
            else if (priorSpeed == 0) speed = priorSpeed;
            else
            { var amount = sign * seconds; speed = magnitude <= MathF.Abs(amount) ? 0 : priorSpeed - amount; }
        }
        else
        {
            var difference = desired - priorSpeed;
            if (difference <= seconds * ability.MaximumAcceleration)
            { if (-(seconds * ability.MaximumDeceleration) > difference) difference = -(seconds * ability.MaximumDeceleration); }
            else difference = seconds * ability.MaximumAcceleration;
            speed = priorSpeed + difference;
        }
        if (urgency < 0) direction = new(direction.X * -1, direction.Y * -1, direction.Z * -1);
        return new(false, direction, speed, distanceToEnd, steering.SteeringPosition, steering);
    }
    private static Vector3 Normalize(Vector3 vector)
    {
        var squared = (vector.X * vector.X + vector.Y * vector.Y) + vector.Z * vector.Z;
        if (squared <= .00000011920928955078125f) return default;
        var inverse = 1 / MathF.Sqrt(squared);
        return new(vector.X * inverse, vector.Y * inverse, vector.Z * inverse);
    }
}

/// <summary>
/// COPTrace's admitted ordinary ground execution. Scene services own collision/avoidance/AI identity;
/// all movement is output to the actor controls for the single scene physics owner.
/// </summary>
public sealed class ZoneGroundTraceGoal : IZoneGoalOperation
{
    private readonly IZoneGroundTraceGoalServices _services;
    private readonly ZoneTraceGoalParameters _parameters;
    private readonly Func<long> _frameMicroseconds;
    private long _lastFrame = -1_000_000;
    private ulong _epoch;
    private bool _disposed, _executing, _targetCreated;
    public bool WaitingForSpeed { get; private set; }

    public ZoneGroundTraceGoal(ZoneTraceGoalParameters parameters, IZoneGroundTraceGoalServices services, Func<long> frameMicroseconds)
    {
        _parameters = parameters; _services = services ?? throw new ArgumentNullException(nameof(services));
        _frameMicroseconds = frameMicroseconds ?? throw new ArgumentNullException(nameof(frameMicroseconds));
    }
    private bool Active(ulong epoch) => !_disposed && _epoch == epoch && _services.IsCurrent;
    public int Execute()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_executing) throw new InvalidOperationException("A trace operation cannot execute recursively.");
        if (!_services.IsCurrent) return 1;
        _executing = true; var epoch = _epoch;
        try
        {
            _services.ValidateOrdinaryGroundTrace(_parameters);
            if (!Active(epoch)) return 1;
            if (_lastFrame <= 0)
            {
                var vetoed = _services.BeginMove();
                if (!Active(epoch) || vetoed) return 1;
            }
            var now = _frameMicroseconds();
            var seconds = _lastFrame <= 0 ? .1f : MathF.Max((float)(now - _lastFrame) * .000001f, 0);
            _lastFrame = now;
            if (!_targetCreated && _services.Navigation.CurrentPath.Count < 2)
            {
                _services.Controls.DesiredSpeed = 0;
                return Complete();
            }
            var frame = _services.ReadFrame();
            if (!Active(epoch)) return 1;
            // Trace owns a positional object even before producing the first steering point.
            if (!_targetCreated)
            {
                _services.SetTraceTarget(frame.EntityPosition);
                if (Active(epoch)) _targetCreated = true;
            }
            if (!Active(epoch)) return 1;
            var speeds = _services.GetMovementSpeeds();
            if (!Active(epoch)) return 1;
            var controls = _services.Controls;
            var motion = ZoneGroundTraceMath.Update(_services.Navigation.CurrentPath, frame, speeds,
                controls.MovementUrgency, controls.DesiredSpeed, seconds, _parameters.EndAccuracy);
            controls.DistanceToPathEnd = motion.DistanceToPathEnd;
            _services.SetMovementContext(1);
            if (!Active(epoch)) return 1;
            if (!motion.EndReached)
            {
                //The trace marker is the ordinary path look-ahead sample, not the radius-constrained steering point.
                var target = default(Vector3);
                if (_services.Navigation.CurrentPath.TryGetPosition(frame.Ability.LookAhead, true, true, ref target))
                    _services.SetTraceTarget(target);
                if (!Active(epoch)) return 1;
                if (ZoneGroundTraceMath.NeedsTurnInPlace(frame, motion.Steering.Direction, controls.MovementUrgency, controls.DesiredSpeed))
                    throw new NotSupportedException("Ground trace reached the original multi-frame turn-in-place/entity rotation branch.");
                if (MathF.Abs(controls.DesiredSpeed) >= .001f) _services.SetMovementContext(3);
                if (!Active(epoch)) return 1;
                controls.DesiredSpeed = motion.DesiredSpeed; controls.MovementDirection = motion.Direction;
            }
            var reached = motion.EndReached;
            WaitingForSpeed = false;
            if (reached)
            {
                if (_parameters.EndAccuracy >= 0)
                {
                    var velocity = frame.ActualVelocity; var direction = controls.MovementDirection;
                    var forwardSpeed = (velocity.Y * direction.Y + velocity.X * direction.X) + direction.Z * 0;
                    if (forwardSpeed > .01f) { reached = false; WaitingForSpeed = true; }
                }
                controls.DesiredSpeed = 0;
            }
            if (reached) return Complete();
            _services.RefreshNavigationType();
            if (!Active(epoch)) return 1;
            _services.UpdateAvoidance(true, false);
            if (!Active(epoch)) return 1;
            return 1;
        }
        finally { _executing = false; }
    }
    private void ResetCore(bool resetActor)
    {
        _epoch++; _lastFrame = -1_000_000; WaitingForSpeed = false;
        if (resetActor && _services.IsCurrent) { _services.Controls.DesiredSpeed = 0; _services.ResetTrace(); }
        _services.ReleaseTraceTarget();
        _targetCreated = false;
    }
    private int Complete()
    {
        if (_services.PathDecision == ZoneApproachPathDecision.Pending) return 1;
        var failed = _services.PathDecision == ZoneApproachPathDecision.NoPath;
        ResetCore(true);
        return failed ? 3 : 2;
    }
    public void Reset() { ObjectDisposedException.ThrowIf(_disposed, this); ResetCore(true); }
    public void Dispose() { if (_disposed) return; _disposed = true; ResetCore(false); }
}
