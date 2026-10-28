using System.Numerics;

namespace AAEmu.Zone.Physics;

/// <summary>
/// Affine convex support queries. Curved targets retain their analytic support surfaces; query
/// capsules use the scene's documented axis-aligned bounds. World separation tolerance is one mm;
/// this does not bound travel-distance error at grazing contact. GJK uses a 0.05 mm support-gap
/// distance convergence target; advancement targets 0.5 mm separation before accepting contact.
/// Iteration exhaustion is a named numerical failure, never a clear-space result.
/// </summary>
internal static class ZoneManagedPrimitiveQueries
{
    public const float ContactTolerance = .001f;
    private const int IterationLimit = 96;

    public static bool Overlaps(ZoneManagedPrimitiveGeometry primitive, Matrix4x4 transform, Vector3 center, Vector3 extents)
        => Distance(primitive, transform, center, extents).Distance <= ContactTolerance;

    public static bool Sweep(ZoneManagedPrimitiveGeometry primitive, Matrix4x4 transform, Vector3 origin, Vector3 extents,
        Vector3 displacement, out float fraction, out Vector3 normal, out Vector3 position)
    {
        fraction = 0; normal = Vector3.Zero; position = Vector3.Zero;
        var time = 0d;
        var lastNormal = Vector3.Zero;
        Matrix4x4.Invert(transform, out var inverse);
        var normalTransform = Matrix4x4.Transpose(inverse);
        for (var iteration = 0; iteration < IterationLimit; iteration++)
        {
            var center = At(origin, displacement, time);
            var distance = Distance(primitive, transform, center, extents);
            if (distance.Distance <= ContactTolerance)
            {
                normal = Vector3.TransformNormal(primitive.OutwardNormal(Vector3.Transform(distance.Point, inverse)), normalTransform);
                if (normal.LengthSquared() > 1e-12f) normal = Vector3.Normalize(normal);
                if (time > 0 && primitive.Kind == ZoneManagedPrimitiveKind.Cylinder &&
                    Vector3.Dot(normal, displacement) >= -1e-7f) normal = distance.Normal;
                if (normal.LengthSquared() < .5f) normal = -Vector3.Normalize(displacement);
                if (time == 0 && Vector3.Dot(displacement, normal) >= -1e-7f) return false;
                if (Vector3.Dot(displacement, normal) > 0 && lastNormal.LengthSquared() > .5f) normal = lastNormal;
                fraction = (float)time;
                position = distance.Point;
                return true;
            }
            var closing = -Dot(distance.Normal, displacement);
            if (closing <= 0) return false; // This separating plane cannot close along the segment.
            var advance = (distance.Distance - ContactTolerance * .5) / closing;
            if (!double.IsFinite(advance) || advance <= 0) throw Failure("non-finite or stalled conservative advancement");
            time += advance;
            if (time > 1) return false;
            if (At(origin, displacement, time) == center) throw Failure("displacement is below coordinate precision");
            lastNormal = distance.Normal;
        }
        throw Failure("conservative advancement exceeded its iteration limit");
    }

    private static DistanceResult Distance(ZoneManagedPrimitiveGeometry primitive, Matrix4x4 transform, Vector3 center, Vector3 extents)
    {
        Span<SupportPoint> simplex = stackalloc SupportPoint[4];
        var transpose = Matrix4x4.Transpose(transform);
        var direction = center - Vector3.Transform(primitive.Center, transform);
        if (direction.LengthSquared() < 1e-12f) direction = Vector3.UnitX;
        simplex[0] = Support(primitive, transform, transpose, center, extents, direction);
        var count = 1;
        var closest = simplex[0].Difference;
        var point = simplex[0].Target;
        var lastNormal = Vector3.Normalize(direction);
        for (var iteration = 0; iteration < IterationLimit; iteration++)
        {
            var squared = DVector.Dot(closest, closest);
            if (squared <= (double)ContactTolerance * ContactTolerance * .01)
                return new(Math.Sqrt(squared), squared > 1e-18 ? closest.UnitFloat : lastNormal, point);
            lastNormal = closest.UnitFloat;
            var next = Support(primitive, transform, transpose, center, extents, -closest.UnitFloat);
            var gap = squared - DVector.Dot(closest, next.Difference);
            var duplicate = false;
            for (var i = 0; i < count; i++) duplicate |= (next.Difference - simplex[i].Difference).LengthSquared < 1e-20;
            // Support gap / current distance bounds the remaining distance error. Requiring
            // float-arithmetic convergence below that bound can cycle on smooth affine surfaces.
            if (gap <= Math.Sqrt(squared) * ContactTolerance * .05 || duplicate)
                return new(Math.Sqrt(squared), closest.UnitFloat, point);
            if (count == 4) throw Failure("unreduced GJK simplex");
            simplex[count++] = next;
            Reduce(simplex, ref count, out closest, out point);
        }
        throw Failure("GJK distance exceeded its iteration limit");
    }

    private static SupportPoint Support(ZoneManagedPrimitiveGeometry primitive, Matrix4x4 transform, Matrix4x4 transpose,
        Vector3 center, Vector3 extents, Vector3 direction)
    {
        var box = new Vector3(direction.X >= 0 ? extents.X : -extents.X,
            direction.Y >= 0 ? extents.Y : -extents.Y, direction.Z >= 0 ? extents.Z : -extents.Z);
        var targetCenter = Vector3.Transform(primitive.Center, transform);
        var targetOffset = Vector3.TransformNormal(primitive.SupportOffset(Vector3.TransformNormal(-direction, transpose)), transform);
        // Evaluate Minkowski supports relative to the query center, avoiding cancellation from
        // separately rounded world support points at zone-local coordinates around 1000..8000m.
        return new(DVector.From(box) - (DVector.From(targetOffset) + DVector.From(targetCenter) - DVector.From(center)), targetCenter + targetOffset);
    }

    // Enumerating simplex faces (at most 15) avoids brittle tetrahedron winding branches.
    // Every retained candidate is a convex combination; the best is the closest hull point.
    private static void Reduce(Span<SupportPoint> simplex, ref int count, out DVector closest, out Vector3 target)
    {
        Span<double> bestWeights = stackalloc double[4];
        Span<double> weights = stackalloc double[4];
        Span<int> indices = stackalloc int[4];
        var best = double.PositiveInfinity;
        for (var mask = 1; mask < 1 << count; mask++)
        {
            var selected = 0;
            for (var i = 0; i < count; i++) if ((mask & 1 << i) != 0) indices[selected++] = i;
            weights.Clear();
            if (!AffineWeights(simplex, indices[..selected], weights)) continue;
            var valid = true;
            var sum = 0d;
            for (var i = 0; i < count; i++)
            {
                if (weights[i] < -1e-8) valid = false;
                weights[i] = Math.Max(0, weights[i]);
                sum += weights[i];
            }
            if (!valid || sum == 0) continue;
            var x = 0d; var y = 0d; var z = 0d;
            for (var i = 0; i < count; i++)
            {
                weights[i] /= sum;
                x += simplex[i].Difference.X * weights[i];
                y += simplex[i].Difference.Y * weights[i];
                z += simplex[i].Difference.Z * weights[i];
            }
            var squared = x * x + y * y + z * z;
            if (squared >= best) continue;
            best = squared;
            weights.CopyTo(bestWeights);
        }
        if (!double.IsFinite(best)) throw Failure("degenerate simplex has no closest face");
        var cx = 0d; var cy = 0d; var cz = 0d;
        var tx = 0d; var ty = 0d; var tz = 0d;
        var remaining = 0;
        for (var i = 0; i < count; i++)
        {
            var weight = bestWeights[i];
            cx += simplex[i].Difference.X * weight; cy += simplex[i].Difference.Y * weight; cz += simplex[i].Difference.Z * weight;
            tx += simplex[i].Target.X * weight; ty += simplex[i].Target.Y * weight; tz += simplex[i].Target.Z * weight;
            if (weight > 1e-10) simplex[remaining++] = simplex[i];
        }
        count = remaining;
        closest = new(cx, cy, cz);
        target = new((float)tx, (float)ty, (float)tz);
    }

    private static bool AffineWeights(ReadOnlySpan<SupportPoint> simplex, ReadOnlySpan<int> indices, Span<double> weights)
    {
        var dimensions = indices.Length - 1;
        if (dimensions == 0) { weights[indices[0]] = 1; return true; }
        Span<DVector> edges = stackalloc DVector[3];
        Span<double> matrix = stackalloc double[12];
        var first = simplex[indices[0]].Difference;
        for (var i = 0; i < dimensions; i++) edges[i] = simplex[indices[i + 1]].Difference - first;
        var scale = 0d;
        for (var row = 0; row < dimensions; row++)
        {
            for (var column = 0; column < dimensions; column++)
            {
                matrix[row * 4 + column] = DVector.Dot(edges[row], edges[column]);
                scale = Math.Max(scale, Math.Abs(matrix[row * 4 + column]));
            }
            matrix[row * 4 + 3] = -DVector.Dot(edges[row], first);
        }
        for (var column = 0; column < dimensions; column++)
        {
            var pivot = column;
            for (var row = column + 1; row < dimensions; row++)
                if (Math.Abs(matrix[row * 4 + column]) > Math.Abs(matrix[pivot * 4 + column])) pivot = row;
            if (Math.Abs(matrix[pivot * 4 + column]) <= Math.Max(1e-24, scale * 1e-14)) return false;
            for (var j = column; j < 4; j++) (matrix[column * 4 + j], matrix[pivot * 4 + j]) = (matrix[pivot * 4 + j], matrix[column * 4 + j]);
            var divisor = matrix[column * 4 + column];
            for (var j = column; j < 4; j++) matrix[column * 4 + j] /= divisor;
            for (var row = 0; row < dimensions; row++)
            {
                if (row == column) continue;
                var multiplier = matrix[row * 4 + column];
                for (var j = column; j < 4; j++) matrix[row * 4 + j] -= multiplier * matrix[column * 4 + j];
            }
        }
        var total = 0d;
        for (var i = 0; i < dimensions; i++) { weights[indices[i + 1]] = matrix[i * 4 + 3]; total += weights[indices[i + 1]]; }
        weights[indices[0]] = 1 - total;
        return true;
    }

    private static Vector3 At(Vector3 origin, Vector3 displacement, double time) => new(
        (float)(origin.X + displacement.X * time), (float)(origin.Y + displacement.Y * time), (float)(origin.Z + displacement.Z * time));
    private static double Dot(Vector3 a, Vector3 b) => (double)a.X * b.X + (double)a.Y * b.Y + (double)a.Z * b.Z;
    private static InvalidOperationException Failure(string reason) => new($"Managed analytic convex query failed: {reason}; no clear-space result is inferred.");
    private readonly record struct SupportPoint(DVector Difference, Vector3 Target);
    private readonly record struct DistanceResult(double Distance, Vector3 Normal, Vector3 Point);
    private readonly record struct DVector(double X, double Y, double Z)
    {
        public static DVector From(Vector3 vector) => new(vector.X, vector.Y, vector.Z);
        public double LengthSquared => Dot(this, this);
        public Vector3 UnitFloat
        {
            get { var length = Math.Sqrt(LengthSquared); return new((float)(X / length), (float)(Y / length), (float)(Z / length)); }
        }
        public static double Dot(DVector a, DVector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static DVector operator +(DVector a, DVector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static DVector operator -(DVector a, DVector b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    }
}
