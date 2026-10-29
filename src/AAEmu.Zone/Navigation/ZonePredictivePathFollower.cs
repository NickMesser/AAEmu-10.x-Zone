using System.Numerics;

namespace AAEmu.Zone.Navigation;

/// <summary>3114D028's copied PathFollowerParams, refreshed by the actual COPTrace owner.</summary>
public readonly record struct ZonePredictivePathFollowerParameters(float NormalSpeed, float PathRadius, float LookAhead,
    float MaximumAcceleration, float MaximumDeceleration, float MinimumSpeed, float MaximumSpeed,
    float EndDistance, bool StopAtEnd, bool Use2D)
{
    /// <summary>Native image byte3120BF04 defaults to zero; no runtime writer has been recovered.</summary>
    public bool CurvatureSlowDown { get; init; }
}
public readonly record struct ZonePredictivePathNode(uint NavigationType, Vector3 Position, Vector3 OffsetDirection, float OffsetScale);
public readonly record struct ZonePredictivePathSample(Vector3 Position, Vector3 Velocity);
public sealed record ZonePredictivePathResult(Vector3 Velocity, bool ReachedEnd, float RemainingDistance,
    int PathVersion, int TargetIndex, Vector3 TargetPosition, float RetainedSpeed,
    IReadOnlyList<ZonePredictivePathSample> Predictions);

/// <summary>
/// Native CPathFollower3114DD78 and its trajectory/target/velocity helpers. This actor-lifetime owner borrows
/// CurrentPath and rebuilds on its source version. Prediction is copied math; no physics Step or pose write occurs.
/// </summary>
public sealed class ZonePredictivePathFollower(ZoneNavigationPath path) : IDisposable
{
    private const float NormalizeThreshold = 1.1920928955078125e-7f;
    private readonly List<ZonePredictivePathNode> _nodes = [];
    private int _pathVersion = -2, _targetIndex;
    private Vector3 _target;
    private float _speed;
    public ZoneNavigationPath Path { get; } = path ?? throw new ArgumentNullException(nameof(path));
    public bool IsDisposed { get; private set; }
    public ZonePredictivePathFollowerParameters Parameters { get; set; }
    public IReadOnlyList<ZonePredictivePathNode> CopyNodes() => Array.AsReadOnly(_nodes.ToArray());

    /// <summary>3114C0D8 through3114BEF8: sample along effective nodes from the current follower target.</summary>
    public Vector3 GetPointAtDistance(float distance, out float actualDistance)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (!float.IsFinite(distance)) throw new ArgumentOutOfRangeException(nameof(distance));
        return SampleEffective(_targetIndex, _target, distance, out actualDistance);
    }

    public ZonePredictivePathResult Update(Vector3 position, Vector3 actualVelocity, float seconds, bool generatePredictions = true)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        Validate(position, actualVelocity, seconds);
        var actualSpeed = Distance(default, actualVelocity);
        if (_pathVersion != Path.Version) { Rebuild(); Initialize(position, actualVelocity); }
        if (_speed > .5f + actualSpeed) _speed = .5f + actualSpeed;
        var velocity = Vector3.Zero; var reached = true;
        var predictions = new List<ZonePredictivePathSample>();
        if (_nodes.Count >= 2)
        {
            AdvanceTarget(ref _targetIndex, ref _target, position, seconds);
            velocity = CalculateVelocity(_targetIndex, _target, position, seconds, ref _speed, out reached);
            if (reached) velocity = default;
            else if (generatePredictions)
            {
                var index = _targetIndex; var target = _target; var speed = _speed;
                var predictedPosition = position; var predictedVelocity = actualVelocity;
                for (var sample = 0; sample < 10; sample++)
                {
                    //3114DD78's .1-second sample interval contains two .05-second forward steps.
                    for (var step = 0; step < 2; step++)
                    {
                        AdvanceTarget(ref index, ref target, predictedPosition, .05f);
                        var nextVelocity = CalculateVelocity(index, target, predictedPosition, .05f, ref speed, out var predictedEnd);
                        if (predictedEnd && Parameters.StopAtEnd)
                        { predictedPosition = Effective(_nodes[^2]); predictedVelocity = default; }
                        else
                        {
                            predictedPosition = new(predictedPosition.X + predictedVelocity.X * .05f,
                                predictedPosition.Y + predictedVelocity.Y * .05f, predictedPosition.Z + predictedVelocity.Z * .05f);
                            predictedVelocity = nextVelocity;
                        }
                    }
                    predictions.Add(new(predictedPosition, predictedVelocity));
                }
            }
        }
        var remaining = RemainingDistance(_targetIndex, _target, position);
        if (!Finite(velocity) || !Finite(_target) || !float.IsFinite(_speed) || !float.IsFinite(remaining) ||
            predictions.Any(sample => !Finite(sample.Position) || !Finite(sample.Velocity)))
            throw new InvalidOperationException("Predictive following produced nonfinite scalar geometry or movement; no body output was published.");
        return new(velocity, reached, remaining, _pathVersion,
            _targetIndex, _target, _speed, predictions.AsReadOnly());
    }

    private void Validate(Vector3 position, Vector3 velocity, float seconds)
    {
        var p = Parameters;
        if (!Finite(position) || !Finite(velocity) || !float.IsFinite(seconds) || seconds < 0 ||
            !float.IsFinite(p.NormalSpeed) || !float.IsFinite(p.PathRadius) || p.PathRadius < 0 ||
            !float.IsFinite(p.LookAhead) || p.LookAhead <= 0 || !float.IsFinite(p.MaximumAcceleration) || p.MaximumAcceleration < 0 ||
            !float.IsFinite(p.MaximumDeceleration) || p.MaximumDeceleration < 0 || !float.IsFinite(p.MinimumSpeed) ||
            !float.IsFinite(p.MaximumSpeed) || p.MaximumSpeed < p.MinimumSpeed || !float.IsFinite(p.EndDistance))
            throw new NotSupportedException("Predictive following requires finite authored movement inputs, positive look-ahead, nonnegative acceleration/deceleration and an ordered speed range.");
        foreach (var point in Path.Points)
            if (!Finite(point.Position)) throw new InvalidDataException("Predictive route contains a nonfinite authored position.");
    }

    //3114D2F8: copy base points and source curvature-dependent lateral offsets.
    private void Rebuild()
    {
        _pathVersion = Path.Version; _nodes.Clear();
        if (Path.Count < 2) return;
        var remaining = 0f; var previous = Path.Points[0].Position;
        foreach (var point in Path.Points) { remaining += Distance(point.Position, previous); previous = point.Position; }
        previous = Path.Points[0].Position;
        var travelled = 0f;
        for (var index = 0; index < Path.Count; index++)
        {
            var point = Path.Points[index]; var position = point.Position;
            var delta = Subtract(position, previous);
            var squared = Parameters.Use2D ? delta.Y * delta.Y + delta.X * delta.X :
                (delta.X * delta.X + delta.Y * delta.Y) + delta.Z * delta.Z;
            var length = MathF.Sqrt(squared); travelled += length; remaining -= length;
            if (travelled > 1 && remaining > 1 && (point.NavigationType & 0x26) != 0)
            {
                var before = SampleFromPoint(index, -2); var after = SampleFromPoint(index, 2);
                var curvature = new Vector3(-((before.X + after.X) - position.X * 2) * (1 / (2f * 2)),
                    -((after.Y + before.Y) - position.Y * 2) * (1 / (2f * 2)),
                    -((after.Z + before.Z) - position.Z * 2) * (1 / (2f * 2)));
                curvature = Normalize(curvature, Vector3.Zero, out var curvatureLength);
                var scale = curvatureLength;
                if (scale >= 0) { if (scale > 1) scale = 1; } else scale = 0;
                var incoming = Subtract(position, previous);
                var outgoing = Subtract(index + 1 < Path.Count ? Path.Points[index + 1].Position : position, position);
                if (Parameters.Use2D) { incoming.Z = 0; outgoing.Z = 0; }
                incoming = Normalize(incoming, default, out _); outgoing = Normalize(outgoing, default, out _);
                var first = new Vector3(incoming.Y, -incoming.X, 0);
                var second = new Vector3(outgoing.Y, -outgoing.X, 0);
                if ((curvature.X * first.X + first.Y * curvature.Y) + curvature.Z * 0 < 0) first = Scale(first, -1);
                if ((curvature.X * second.X + second.Y * curvature.Y) + curvature.Z * 0 < 0) second = Scale(second, -1);
                if ((outgoing.Y * incoming.Y + outgoing.X * incoming.X) + outgoing.Z * incoming.Z <= .9f)
                { _nodes.Add(new(point.NavigationType, position, first, scale)); _nodes.Add(new(point.NavigationType, position, second, scale)); }
                else _nodes.Add(new(point.NavigationType, position, Normalize(Add(second, first), Vector3.UnitX, out _), scale));
            }
            else _nodes.Add(new(point.NavigationType, position, default, 0));
            previous = position;
        }
        if (Parameters.EndDistance > 0)
        {
            var removed = 0f;
            while (_nodes.Count >= 2)
            {
                var start = Effective(_nodes[^2]); var end = Effective(_nodes[^1]); var length = Distance(start, end);
                if (_nodes.Count <= 2 || length >= .01f && Parameters.EndDistance <= length + removed)
                {
                    var fraction = 1 - (Parameters.EndDistance - removed) / length;
                    var upper = ((fraction + 1) - MathF.Abs(fraction - 1)) * .5f;
                    var clamped = (MathF.Abs(upper - .001f) + (upper + .001f)) * .5f;
                    _nodes[^1] = _nodes[^1] with { Position = new((end.X - start.X) * clamped + start.X,
                        (end.Y - start.Y) * clamped + start.Y, (end.Z - start.Z) * clamped + start.Z) };
                    break;
                }
                _nodes.RemoveAt(_nodes.Count - 1); removed += length;
            }
        }
        var last = _nodes[^1]; var prior = _nodes[0].Position;
        for (var index = _nodes.Count - 1; index >= 0; index--)
            if (Distance(_nodes[index].Position, last.Position) > .01f) { prior = _nodes[index].Position; break; }
        var final = last.Position;
        if (Parameters.Use2D) final.Z = prior.Z;
        _nodes.Add(last with { OffsetDirection = Normalize(Subtract(final, prior), Vector3.UnitX, out _),
            OffsetScale = Parameters.StopAtEnd ? .1f : 10 });
        _targetIndex = 0; _target = Effective(_nodes[0]);
    }

    //3114D088: signed full-three-dimensional sample around one source point, independently of Use2D.
    private Vector3 SampleFromPoint(int start, float distance)
    {
        var direction = distance < 0 ? -1 : 1; distance = MathF.Abs(distance);
        var current = Path.Points[start].Position; var travelled = 0f;
        for (var index = start + direction; index >= 0 && index < Path.Count; index += direction)
        {
            var next = Path.Points[index].Position; var delta = Subtract(current, next);
            var length = MathF.Sqrt((delta.Y * delta.Y + delta.X * delta.X) + delta.Z * delta.Z);
            if (length + travelled > distance && length > 0)
            {
                var fraction = (distance - travelled) / length;
                return new(next.X * fraction + current.X * (1 - fraction), next.Y * fraction + current.Y * (1 - fraction),
                    next.Z * fraction + current.Z * (1 - fraction));
            }
            travelled += length; current = next;
        }
        return current;
    }

    //3114C298: closest effective segment, then source look-ahead and projected retained speed.
    private void Initialize(Vector3 position, Vector3 velocity)
    {
        var closest = float.MaxValue;
        if (Path.CreationParameters.ClosedLoop)
        {
            for (var index = _nodes.Count - 4; index >= 0; index--) Consider(index, true);
        }
        else for (var index = 0; index < _nodes.Count - 1; index++) Consider(index, false);
        void Consider(int index, bool acceptTie)
        {
            var start = Effective(_nodes[index]); var end = Effective(_nodes[index + 1]);
            var squared = SegmentDistanceSquared(position, start, end, out var fraction);
            if (!(acceptTie ? closest >= squared : closest > squared)) return;
            closest = squared; _targetIndex = index;
            _target = new(end.X * fraction + (1 - fraction) * start.X, end.Y * fraction + start.Y * (1 - fraction),
                end.Z * fraction + start.Z * (1 - fraction));
        }
        AdvanceTarget(ref _targetIndex, ref _target, position, 100);
        var offset = Subtract(_target, position);
        if (Parameters.Use2D) { velocity.Z = 0; offset.Z = 0; }
        var direction = Normalize(velocity, default, out _); offset = Normalize(offset, default, out _);
        var alignment = (direction.X * offset.X + direction.Y * offset.Y) + offset.Z * direction.Z;
        if (alignment >= 0) { if (alignment > 1) alignment = 1; } else alignment = 0;
        _speed = Distance(default, velocity) * alignment;
    }

    //3114B678: advance the target at most3*dt*maximumSpeed while seeking the configured look-ahead distance.
    private void AdvanceTarget(ref int index, ref Vector3 target, Vector3 position, float seconds)
    {
        if (_nodes.Count < 2) return;
        var lookAhead = Parameters.LookAhead; var minimumStep = lookAhead * .01f;
        var budget = (3 * seconds) * Parameters.MaximumSpeed; var advanced = 0f;
        var start = Effective(_nodes[index]); var nextIndex = index + 1;
        if (nextIndex == _nodes.Count) { target = start; return; }
        var end = Effective(_nodes[nextIndex]); var length = Distance(start, end);
        if (length <= .00001f) { index = Math.Min(nextIndex, _nodes.Count - 2); return; }
        var toEnd = Distance(target, end); var fromActor = Distance(target, position);
        var iterations = 0;
        while (lookAhead > fromActor)
        {
            if (++iterations > 100_000) throw new InvalidOperationException("Predictive target advancement exceeded its explicit numerical work bound.");
            var step = lookAhead - fromActor;
            if (step <= minimumStep) step = minimumStep;
            var limited = false;
            if (step + advanced > budget) { limited = true; step = budget - advanced; }
            if (step <= toEnd)
            {
                toEnd -= step; advanced += step;
                var fraction = toEnd / length; var inverse = 1 - fraction;
                target = new(start.X * fraction + end.X * inverse, start.Y * fraction + end.Y * inverse,
                    start.Z * fraction + end.Z * inverse);
                if (limited) return;
            }
            else
            {
                target = end; advanced += toEnd; index++;
                if (index > _nodes.Count - 2) { index = _nodes.Count - 2; return; }
                start = Effective(_nodes[index]);
                if (index + 1 == _nodes.Count) { target = start; return; }
                end = Effective(_nodes[index + 1]); length = Distance(start, end);
                if (length <= .00001f) { index = Math.Min(index + 1, _nodes.Count - 2); return; }
                toEnd = Distance(target, end);
            }
            fromActor = Distance(target, position); lookAhead = Parameters.LookAhead;
        }
    }

    //3114C748: predictive speed, acceleration/deceleration bounds and the terminal-plane test.
    private Vector3 CalculateVelocity(int index, Vector3 target, Vector3 position, float seconds, ref float retainedSpeed, out bool reached)
    {
        var offset = Subtract(target, position); if (Parameters.Use2D) offset.Z = 0;
        var direction = Normalize(offset, default, out var length);
        var speed = length * Parameters.NormalSpeed / Parameters.LookAhead;
        if (Parameters.CurvatureSlowDown)
        {
            var forward = SampleEffective(index, target, 3, out var along);
            if (along > .1f)
            {
                var scale = (Distance(forward, target) / along - 1) * 3 + 1;
                if (.5f <= scale) { if (scale > 1) scale = 1; } else scale = .5f;
                speed *= scale;
            }
        }
        var deceleration = Parameters.MaximumDeceleration;
        if (deceleration <= 1000)
        {
            var terminalDistance = Distance(position, Effective(_nodes[^1]));
            var stoppingDistance = (speed * speed) * .5f / deceleration;
            if (stoppingDistance > terminalDistance)
            {
                var remaining = RemainingDistance(index, target, position);
                var limit = MathF.Sqrt(remaining * (deceleration * 2));
                if (speed > limit) speed = limit;
            }
        }
        var upper = seconds * Parameters.MaximumAcceleration + retainedSpeed;
        if (!(upper <= Parameters.MaximumSpeed)) upper = Parameters.MaximumSpeed;
        var lower = retainedSpeed - deceleration * seconds;
        if (!(Parameters.MinimumSpeed <= lower)) lower = Parameters.MinimumSpeed;
        if (lower <= speed) { if (speed > upper) speed = upper; }
        else speed = lower;
        retainedSpeed = speed;
        var velocity = Scale(direction, speed);
        reached = false;
        if (index >= _nodes.Count - 2)
        {
            var end = Effective(_nodes[^2]); var terminal = Effective(_nodes[^1]);
            var tangent = Subtract(terminal, end); if (Parameters.Use2D) tangent.Z = 0;
            if (MathF.Abs(tangent.X) <= 0 && MathF.Abs(tangent.Y) <= 0 && MathF.Abs(tangent.Z) <= 0) tangent = Vector3.UnitX;
            tangent = Normalize(tangent, default, out _);
            var relative = Normalize(Subtract(position, end), default, out _);
            reached = !((relative.X * tangent.X + relative.Y * tangent.Y) + relative.Z * tangent.Z <= 0);
        }
        return velocity;
    }

    //3114BB58 excludes the dummy terminal segment until the target itself occupies it.
    private float RemainingDistance(int index, Vector3 target, Vector3? position)
    {
        if (_nodes.Count == 0) return 0;
        var remaining = 0f;
        for (var segment = index + 1; segment < _nodes.Count - 2; segment++)
            remaining += Distance(Effective(_nodes[segment]), Effective(_nodes[segment + 1]));
        remaining += Distance(target, Effective(_nodes[Math.Min(index + 1, _nodes.Count - 1)]));
        if (position is { } current) remaining += Distance(current, target);
        return remaining;
    }

    private Vector3 SampleEffective(int index, Vector3 start, float distance, out float travelled)
    {
        travelled = 0; var current = start;
        for (var next = index + 1; next < _nodes.Count; next++)
        {
            var end = Effective(_nodes[next]); var total = Distance(current, end) + travelled;
            if (total > distance && total - travelled > 0)
            {
                var fraction = (distance - travelled) / (total - travelled);
                travelled = distance;
                return new(end.X * fraction + (1 - fraction) * current.X,
                    end.Y * fraction + (1 - fraction) * current.Y, end.Z * fraction + (1 - fraction) * current.Z);
            }
            travelled = total; current = end;
        }
        return current;
    }
    private Vector3 Effective(ZonePredictivePathNode node)
    { var scale = Parameters.PathRadius * node.OffsetScale; return new(node.OffsetDirection.X * scale + node.Position.X,
        node.OffsetDirection.Y * scale + node.Position.Y, node.OffsetDirection.Z * scale + node.Position.Z); }
    private float Distance(Vector3 a, Vector3 b)
    { var d = Subtract(a, b); return MathF.Sqrt(Parameters.Use2D ? d.X * d.X + d.Y * d.Y : (d.Y * d.Y + d.X * d.X) + d.Z * d.Z); }
    private static Vector3 Normalize(Vector3 vector, Vector3 fallback, out float length)
    {
        var squared = (vector.X * vector.X + vector.Y * vector.Y) + vector.Z * vector.Z;
        if (squared <= NormalizeThreshold) { length = squared; return fallback; }
        length = MathF.Sqrt(squared); var inverse = 1 / length;
        return Scale(vector, inverse);
    }
    private static float SegmentDistanceSquared(Vector3 position, Vector3 start, Vector3 end, out float fraction)
    {
        var relative = Subtract(position, start); var delta = Subtract(end, start);
        fraction = (delta.Y * relative.Y + delta.X * relative.X) + delta.Z * relative.Z;
        if (fraction > 0)
        {
            var length = (delta.Y * delta.Y + delta.X * delta.X) + delta.Z * delta.Z;
            if (fraction < length) { fraction /= length; delta = Scale(delta, fraction); } else fraction = 1;
            relative = Subtract(relative, delta);
        }
        else fraction = 0;
        return (relative.X * relative.X + relative.Y * relative.Y) + relative.Z * relative.Z;
    }
    private static Vector3 Add(Vector3 a, Vector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    private static Vector3 Subtract(Vector3 a, Vector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static Vector3 Scale(Vector3 value, float scale) => new(value.X * scale, value.Y * scale, value.Z * scale);
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    public void Dispose() { IsDisposed = true; _nodes.Clear(); }
}
