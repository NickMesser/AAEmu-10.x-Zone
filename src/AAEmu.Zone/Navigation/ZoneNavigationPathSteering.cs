using System.Numerics;

namespace AAEmu.Zone.Navigation;

/// <summary>Copied CNavPath311410D0 outputs; these are steering data, not an actor pose or a physical velocity.</summary>
public readonly record struct ZoneNavigationSteeringResult(bool HasPath, Vector3 Direction,
    float RemainingDistance, float DistanceToPath, bool IsStuck, Vector3 SegmentDirection,
    Vector3 InflectionDirection, Vector3 SteeringPosition);

public readonly record struct ZoneNavigationCurvatureResult(bool HasPath, Vector3 Position,
    Vector3 Direction, float Curvature, float MinimumDot);

/// <summary>
/// CNavPath3113F5F0/311409D0/311410D0 scalar path advancement and look-ahead steering.
/// Collision admission and path publication belong to the scene; this helper never searches or moves a body.
/// </summary>
public static class ZoneNavigationPathSteering
{
    private const float NormalizeThreshold = 0.00000011920928955078125f;

    /// <summary>Advance at segment bisectors, then monotonically project onto the admitted first segment.</summary>
    public static float Advance(ZoneNavigationPath path, Vector3 position, float lookAhead,
        bool flattenToXY, bool allowFinish = true)
    {
        ArgumentNullException.ThrowIfNull(path);
        var remaining = path.GetLength(flattenToXY);
        if (flattenToXY) position.Z = 0;
        while (path.Count >= 2)
        {
            var first = path.Points[0].Position;
            var second = path.Points[1].Position;
            if (Distance(first, second, flattenToXY) > .1f)
            {
                var third = path.Count > 2 ? path.Points[2].Position :
                    new Vector3((second.X - first.X) + second.X,
                        (second.Y - first.Y) + second.Y, (second.Z - first.Z) + second.Z);
                if (flattenToXY) { first.Z = 0; second.Z = 0; third.Z = 0; }
                var incoming = Normalize(Subtract(second, first), Vector3.UnitX);
                if (lookAhead * -.6f > Dot(incoming, position) - Dot(incoming, second)) break;
                var outgoing = Normalize(Subtract(third, second), incoming);
                var bisector = Normalize(Add(outgoing, incoming), incoming);
                if (Dot(bisector, position) - Dot(bisector, second) <= 0) break;
            }
            if (!allowFinish && path.Count == 2) { path.CurrentFraction = .99f; return remaining; }
            var next = default(ZoneNavigationPoint);
            path.TryAdvance(ref next);
            path.CurrentFraction = 0;
        }
        if (path.Count < 2) { path.CurrentFraction = 0; return remaining; }
        var start = path.Points[0].Position; var end = path.Points[1].Position;
        if (flattenToXY) { start.Z = 0; end.Z = 0; }
        _ = SegmentDistanceSquared(position, start, end, out var fraction);
        if (fraction > 1) path.CurrentFraction = 1;
        else if (fraction > path.CurrentFraction) path.CurrentFraction = fraction;
        return remaining;
    }

    /// <summary>311409D0: choose a look-ahead point, including its original eight radius-search refinements.</summary>
    public static Vector3 GetSteeringPosition(ZoneNavigationPath path, Vector3 position,
        float lookAhead, float minimumLookAhead, float passRadius, bool flattenToXY)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Count == 0) return position;
        if (path.Count == 1) return path.Points[0].Position;
        var current = SampleCurrent(path);
        var distanceFromPath = Distance(current, position, flattenToXY);
        var second = path.Points[1].Position;
        var delta = Subtract(second, current);
        var segmentLength = Length(delta, flattenToXY);
        var amount = lookAhead - distanceFromPath;
        if (minimumLookAhead > amount) amount = minimumLookAhead;
        if (segmentLength > amount)
        {
            var inverse = 1 / segmentLength;
            return new((delta.X * amount) * inverse + current.X,
                (delta.Y * amount) * inverse + current.Y, (delta.Z * amount) * inverse + current.Z);
        }
        if (path.Count == 2)
        {
            var remainder = amount - segmentLength;
            var tangent = Subtract(second, path.Points[0].Position);
            if (flattenToXY) tangent.Z = 0;
            var squared = (tangent.X * tangent.X + tangent.Y * tangent.Y) + tangent.Z * tangent.Z;
            var length = squared <= NormalizeThreshold ? squared : MathF.Sqrt(squared);
            tangent = Normalize(tangent, Vector3.Zero);
            if (length < .01f) return second;
            return new(tangent.X * remainder + second.X, tangent.Y * remainder + second.Y,
                tangent.Z * remainder + second.Z);
        }
        var candidate = default(Vector3);
        if (path.TryGetPosition(amount, flattenToXY, true, ref candidate))
        {
            var first = path.Points[0].Position; var lineEnd = second;
            var third = path.Points[2].Position;
            var projectedPosition = position;
            var projectedSecond = second; var projectedThird = third;
            if (flattenToXY) { projectedPosition.Z = 0; projectedSecond.Z = 0; projectedThird.Z = 0; }
            if (passRadius * passRadius > SegmentDistanceSquared(projectedPosition, projectedSecond, projectedThird, out _))
            { first = second; lineEnd = third; }
            if (!OutsideLineRadius(candidate, first, lineEnd, passRadius, flattenToXY)) return candidate;
            var distance = (amount - minimumLookAhead) * .5f + minimumLookAhead;
            var adjustment = distance * .5f;
            for (var index = 0; index < 8 && path.TryGetPosition(distance, flattenToXY, true, ref candidate); index++)
            {
                if (OutsideLineRadius(candidate, first, lineEnd, passRadius, flattenToXY)) distance -= adjustment;
                else distance += adjustment;
                adjustment *= .5f;
                if (index == 7) return candidate;
            }
        }
        return second;
    }

    /// <summary>
    /// 311410D0: update path progress and steering. Adaptive look-ahead uses actual velocity and elapsed frame seconds.
    /// A false result preserves the native early return after the route has fewer than two points.
    /// </summary>
    public static ZoneNavigationSteeringResult Update(ZoneNavigationPath path, Vector3 position, Vector3 actualVelocity,
        float lookAhead, float passRadius, float seconds, bool adaptiveLookAhead, bool flattenToXY)
    {
        ArgumentNullException.ThrowIfNull(path);
        path.ClearDebugGeometry();
        if (path.Count < 2) return default;
        var previousCurrent = SampleCurrent(path);
        var remaining = Advance(path, position, lookAhead, flattenToXY);
        if (path.Count < 2) return new(false, default, remaining, 0, false, default, default, default);
        var stuck = false; var effectiveLookAhead = lookAhead;
        var current = SampleCurrent(path);
        var distanceToPath = Distance(position, current, flattenToXY);
        if (adaptiveLookAhead && remaining > lookAhead)
        {
            var progress = Distance(current, previousCurrent, false);
            var expected = Length(actualVelocity, flattenToXY) * seconds;
            if (.1f * expected <= progress || distanceToPath <= passRadius * .5f)
                path.StuckTime -= path.StuckTime <= seconds ? path.StuckTime : seconds;
            else path.StuckTime = seconds + path.StuckTime;
            if (path.StuckTime > .2f)
            {
                stuck = true;
                effectiveLookAhead = (1 / ((path.StuckTime - .2f) * 1 + 1)) * lookAhead;
            }
            if (path.StuckTime > 1) path.StuckTime = 1;
        }
        remaining += distanceToPath;
        var steering = GetSteeringPosition(path, position, effectiveLookAhead, .1f * lookAhead, passRadius, flattenToXY);
        var direction = Normalize(Subtract(steering, position), Vector3.UnitX);
        var segmentDirection = Normalize(Subtract(path.Points[1].Position, path.Points[0].Position), Vector3.UnitX);
        var projectedTravel = Subtract(steering, previousCurrent);
        if (Dot(Subtract(position, steering), projectedTravel) > 0)
        {
            var distance = Length(projectedTravel, flattenToXY);
            var sample = default(Vector3);
            if (path.TryGetPosition(distance, true, false, ref sample)) Advance(path, sample, 100, true, false);
        }
        return new(true, direction, remaining, distanceToPath, stuck, segmentDirection, segmentDirection, steering);
    }

    /// <summary>3113E5D0: weighted angular slowdown and curvature within the actual path look-ahead distance.</summary>
    public static ZoneNavigationCurvatureResult GetLookAheadCurvature(ZoneNavigationPath path, float lookAhead,
        bool flattenToXY, bool weightByRemainingDistance = true)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Count < 2) return new(false, default, default, 0, 1);
        var original = SampleCurrent(path); var current = original;
        var first = Subtract(path.Points[1].Position, current);
        if (flattenToXY) first.Z = 0;
        first = Normalize(first, Vector3.UnitY);
        var direction = first; var remaining = lookAhead; var minimumDot = 1f;
        var result = path.Points[1].Position;
        for (var index = 1; index < path.Count; index++)
        {
            var next = path.Points[index].Position; var delta = Subtract(next, current);
            var length = MathF.Sqrt((delta.Y * delta.Y + delta.X * delta.X) + (flattenToXY ? 0 : delta.Z * delta.Z));
            if (flattenToXY) delta.Z = 0;
            var normalizedSquared = (delta.X * delta.X + delta.Y * delta.Y) + delta.Z * delta.Z;
            var normalizedLength = normalizedSquared <= NormalizeThreshold ? normalizedSquared : MathF.Sqrt(normalizedSquared);
            delta = Normalize(delta, Vector3.Zero);
            if (normalizedLength < .001f)
            { if (index == path.Count - 1) result = next; continue; }
            direction = delta;
            var dot = Dot(direction, first);
            if (weightByRemainingDistance) dot = (remaining / lookAhead) * dot + (1 - remaining / lookAhead);
            if (minimumDot > dot) minimumDot = dot;
            var fraction = length <= 0 ? 1 : remaining / length;
            result = new(next.X * fraction + current.X * (1 - fraction),
                next.Y * fraction + current.Y * (1 - fraction), next.Z * fraction + current.Z * (1 - fraction));
            if (length >= remaining) break;
            current = next; remaining -= length;
            if (index == path.Count - 1) result = next;
        }
        var distance = Distance(result, original, flattenToXY);
        var curvature = 0f;
        if (!(lookAhead <= distance))
        {
            var halfChordSquared = (distance * .5f) * (distance * .5f);
            var sagitta = MathF.Sqrt((lookAhead * .5f) * (lookAhead * .5f) - halfChordSquared);
            var scaled = sagitta * .5f;
            curvature = 1 / (((scaled * scaled + halfChordSquared) * .5f) / scaled);
        }
        return new(true, result, direction, curvature, minimumDot);
    }

    private static Vector3 SampleCurrent(ZoneNavigationPath path)
    {
        var first = path.Points[0].Position; var second = path.Points[1].Position;
        var fraction = path.CurrentFraction;
        return new(first.X * (1 - fraction) + second.X * fraction,
            first.Y * (1 - fraction) + second.Y * fraction, first.Z * (1 - fraction) + second.Z * fraction);
    }
    private static Vector3 Add(Vector3 a, Vector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    private static Vector3 Subtract(Vector3 a, Vector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static float Dot(Vector3 a, Vector3 b) => (a.Y * b.Y + a.X * b.X) + a.Z * b.Z;
    private static float Length(Vector3 delta, bool flat) => MathF.Sqrt(flat
        ? delta.X * delta.X + delta.Y * delta.Y : (delta.Y * delta.Y + delta.X * delta.X) + delta.Z * delta.Z);
    private static float Distance(Vector3 a, Vector3 b, bool flat) => Length(Subtract(a, b), flat);
    private static Vector3 Normalize(Vector3 vector, Vector3 fallback)
    {
        var squared = (vector.X * vector.X + vector.Y * vector.Y) + vector.Z * vector.Z;
        if (squared <= NormalizeThreshold) return fallback;
        var inverse = 1 / MathF.Sqrt(squared);
        return new(vector.X * inverse, vector.Y * inverse, vector.Z * inverse);
    }
    private static float SegmentDistanceSquared(Vector3 point, Vector3 first, Vector3 second, out float fraction)
    {
        var offset = Subtract(point, first); var segment = Subtract(second, first);
        fraction = Dot(segment, offset);
        if (fraction > 0)
        {
            var squared = Dot(segment, segment);
            if (fraction < squared)
            {
                fraction /= squared;
                segment = new(segment.X * fraction, segment.Y * fraction, segment.Z * fraction);
            }
            else fraction = 1;
            offset = Subtract(offset, segment);
        }
        else fraction = 0;
        return (offset.X * offset.X + offset.Y * offset.Y) + offset.Z * offset.Z;
    }
    private static bool OutsideLineRadius(Vector3 point, Vector3 first, Vector3 second, float radius, bool flat)
    {
        var segment = Subtract(second, first);
        if (flat) segment.Z = 0;
        var direction = Normalize(segment, flat ? Vector3.Zero : Vector3.UnitX);
        var offset = Subtract(point, first);
        if (flat) offset.Z = 0;
        var along = Dot(direction, offset);
        var x = offset.X - direction.X * along; var y = offset.Y - direction.Y * along;
        var squared = y * y + x * x;
        if (!flat) { var z = offset.Z - direction.Z * along; squared += z * z; }
        return !(squared <= radius * radius);
    }
}
