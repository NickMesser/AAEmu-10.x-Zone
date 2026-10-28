using System.Numerics;

namespace AAEmu.Zone.Physics;

internal readonly record struct ZoneManagedBounds(Vector3 Minimum, Vector3 Maximum)
{
    public Vector3 Center => (Minimum + Maximum) * .5f;
    public bool Overlaps(ZoneManagedBounds other) =>
        Minimum.X <= other.Maximum.X && Maximum.X >= other.Minimum.X &&
        Minimum.Y <= other.Maximum.Y && Maximum.Y >= other.Minimum.Y &&
        Minimum.Z <= other.Maximum.Z && Maximum.Z >= other.Minimum.Z;

    public static ZoneManagedBounds FromTriangle(Vector3 a, Vector3 b, Vector3 c) =>
        new(Vector3.Min(a, Vector3.Min(b, c)), Vector3.Max(a, Vector3.Max(b, c)));

    public static ZoneManagedBounds Union(ZoneManagedBounds first, ZoneManagedBounds second) =>
        new(Vector3.Min(first.Minimum, second.Minimum), Vector3.Max(first.Maximum, second.Maximum));

    public ZoneManagedBounds Transform(Matrix4x4 matrix)
    {
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        for (var corner = 0; corner < 8; corner++)
        {
            var point = Vector3.Transform(new Vector3(
                (corner & 1) == 0 ? Minimum.X : Maximum.X,
                (corner & 2) == 0 ? Minimum.Y : Maximum.Y,
                (corner & 4) == 0 ? Minimum.Z : Maximum.Z), matrix);
            minimum = Vector3.Min(minimum, point);
            maximum = Vector3.Max(maximum, point);
        }
        return new(minimum, maximum);
    }

    public bool IntersectsSegment(Vector3 origin, Vector3 displacement)
    {
        var begin = 0f;
        var end = 1f;
        for (var axis = 0; axis < 3; axis++)
        {
            var position = ZoneManagedTriangleQueries.Component(origin, axis);
            var speed = ZoneManagedTriangleQueries.Component(displacement, axis);
            var minimum = ZoneManagedTriangleQueries.Component(Minimum, axis);
            var maximum = ZoneManagedTriangleQueries.Component(Maximum, axis);
            if (speed == 0)
            {
                if (position < minimum || position > maximum) return false;
                continue;
            }
            var first = (minimum - position) / speed;
            var last = (maximum - position) / speed;
            if (first > last) (first, last) = (last, first);
            begin = MathF.Max(begin, first);
            end = MathF.Min(end, last);
            if (begin > end) return false;
        }
        return true;
    }
}

/// <summary>World-space triangle tests shared by immutable static meshes and their instances.</summary>
internal static class ZoneManagedTriangleQueries
{
    private const float Epsilon = .00001f;

    public static bool Overlaps(ZonePhysicsShape shape, Vector3 center, Vector3 a, Vector3 b, Vector3 c)
    {
        if (shape.Kind == ZonePhysicsShapeKind.Box) return Overlaps(center, shape.HalfExtents, a, b, c);
        if (Vector3.Cross(b - a, c - a).LengthSquared() < 1e-18f) return false;
        return ZoneManagedConvexQueries.Overlaps(ZoneManagedConvexTarget.ForTriangle(a, b, c), center, shape);
    }

    public static bool Sweep(ZonePhysicsShape shape, Vector3 origin, Vector3 displacement,
        Vector3 a, Vector3 b, Vector3 c, out float fraction, out Vector3 normal, out Vector3 position)
    {
        if (shape.Kind == ZonePhysicsShapeKind.Box) return Sweep(origin, shape.HalfExtents, displacement, a, b, c, out fraction, out normal, out position);
        var face = Vector3.Cross(b - a, c - a);
        if (face.LengthSquared() < 1e-18f)
        { fraction = 0; normal = position = default; return false; }
        var faceNormal = Vector3.Normalize(face);
        var signed = Vector3.Dot(origin - a, faceNormal);
        var radius = Vector3.Dot(ZoneManagedConvexTarget.ShapeSupport(shape, faceNormal), faceNormal);
        var speed = Vector3.Dot(displacement, faceNormal);
        // The face plane already separates these shapes for the entire segment. Approaching
        // an internal edge within the convex distance tolerance cannot create a tangent wall.
        if (signed >= radius - Epsilon && speed >= -Epsilon || signed <= -radius + Epsilon && speed <= Epsilon)
        { fraction = 0; normal = position = default; return false; }
        return ZoneManagedConvexQueries.Sweep(ZoneManagedConvexTarget.ForTriangle(a, b, c), origin, shape, displacement, out fraction, out normal, out position);
    }

    public static bool Raycast(Vector3 origin, Vector3 displacement, Vector3 a, Vector3 b, Vector3 c,
        out float fraction, out Vector3 normal)
    {
        fraction = 0;
        normal = Vector3.Zero;
        var edge1 = b - a;
        var edge2 = c - a;
        var cross = Vector3.Cross(edge1, edge2);
        if (cross.LengthSquared() < 1e-18f) return false;
        var p = Vector3.Cross(displacement, edge2);
        var determinant = Vector3.Dot(edge1, p);
        if (MathF.Abs(determinant) < 1e-12f) return false;
        var inverse = 1f / determinant;
        var relative = origin - a;
        var u = Vector3.Dot(relative, p) * inverse;
        if (u < -Epsilon || u > 1 + Epsilon) return false;
        var q = Vector3.Cross(relative, edge1);
        var v = Vector3.Dot(displacement, q) * inverse;
        if (v < -Epsilon || u + v > 1 + Epsilon) return false;
        fraction = Vector3.Dot(edge2, q) * inverse;
        if (fraction < 0 || fraction > 1) return false;
        normal = Vector3.Normalize(cross);
        return true;
    }

    public static bool Overlaps(Vector3 center, Vector3 extents, Vector3 a, Vector3 b, Vector3 c)
    {
        Span<Vector3> axes = stackalloc Vector3[13];
        if (!CreateAxes(a, b, c, axes)) return false;
        foreach (var rawAxis in axes)
        {
            if (rawAxis.LengthSquared() < 1e-18f) continue;
            var axis = Vector3.Normalize(rawAxis);
            var radius = Vector3.Dot(Vector3.Abs(axis), extents);
            var p0 = Vector3.Dot(a - center, axis);
            var p1 = Vector3.Dot(b - center, axis);
            var p2 = Vector3.Dot(c - center, axis);
            if (MathF.Min(p0, MathF.Min(p1, p2)) > radius + Epsilon ||
                MathF.Max(p0, MathF.Max(p1, p2)) < -radius - Epsilon) return false;
        }
        return true;
    }

    public static bool Sweep(Vector3 origin, Vector3 extents, Vector3 displacement,
        Vector3 a, Vector3 b, Vector3 c, out float fraction, out Vector3 normal, out Vector3 position)
    {
        fraction = 0;
        normal = Vector3.Zero;
        position = Vector3.Zero;
        Span<Vector3> axes = stackalloc Vector3[13];
        if (!CreateAxes(a, b, c, axes)) return false;
        var faceNormal = Vector3.Normalize(axes[3]);
        var signedDistance = Vector3.Dot(origin - a, faceNormal);
        var faceRadius = Vector3.Dot(Vector3.Abs(faceNormal), extents);
        var faceSpeed = Vector3.Dot(displacement, faceNormal);
        // Crossing an internal mesh edge while resting exactly on its face is tangential travel,
        // not a wall. Both sides are collidable; movement out of an initial contact stays clear.
        if (signedDistance >= faceRadius - Epsilon && faceSpeed >= -Epsilon ||
            signedDistance <= -faceRadius + Epsilon && faceSpeed <= Epsilon) return false;

        normal = signedDistance > 0 ? faceNormal : signedDistance < 0 ? -faceNormal : faceSpeed > 0 ? -faceNormal : faceNormal;
        var begin = 0f;
        var end = 1f;
        var startsOverlapping = true;
        foreach (var rawAxis in axes)
        {
            if (rawAxis.LengthSquared() < 1e-18f) continue;
            var axis = Vector3.Normalize(rawAxis);
            var radius = Vector3.Dot(Vector3.Abs(axis), extents);
            var p0 = Vector3.Dot(a - origin, axis);
            var p1 = Vector3.Dot(b - origin, axis);
            var p2 = Vector3.Dot(c - origin, axis);
            var minimum = MathF.Min(p0, MathF.Min(p1, p2)) - radius;
            var maximum = MathF.Max(p0, MathF.Max(p1, p2)) + radius;
            if (minimum > Epsilon || maximum < -Epsilon) startsOverlapping = false;
            var speed = Vector3.Dot(displacement, axis);
            if (MathF.Abs(speed) < 1e-12f)
            {
                if (minimum > Epsilon || maximum < -Epsilon) return false;
                continue;
            }
            var first = minimum / speed;
            var last = maximum / speed;
            var enteringNormal = -axis;
            if (first > last) { (first, last) = (last, first); enteringNormal = axis; }
            if (first > begin) { begin = first; normal = enteringNormal; }
            end = MathF.Min(end, last);
            if (begin > end + Epsilon) return false;
        }
        if (begin > 1 || end < 0) return false;
        if (startsOverlapping)
            normal = signedDistance > 0 ? faceNormal : signedDistance < 0 ? -faceNormal : faceSpeed > 0 ? -faceNormal : faceNormal;
        if (Vector3.Dot(displacement, normal) >= -Epsilon) return false;
        fraction = Math.Clamp(begin, 0, 1);
        var center = origin + displacement * fraction;
        position = ClosestPoint(center - normal * Vector3.Dot(Vector3.Abs(normal), extents), a, b, c);
        return true;
    }

    private static bool CreateAxes(Vector3 a, Vector3 b, Vector3 c, Span<Vector3> axes)
    {
        axes[0] = Vector3.UnitX;
        axes[1] = Vector3.UnitY;
        axes[2] = Vector3.UnitZ;
        axes[3] = Vector3.Cross(b - a, c - a);
        if (axes[3].LengthSquared() < 1e-18f) return false;
        ReadOnlySpan<Vector3> edges = [b - a, c - b, a - c];
        for (var edge = 0; edge < 3; edge++)
        {
            axes[4 + edge * 3] = Vector3.Cross(edges[edge], Vector3.UnitX);
            axes[5 + edge * 3] = Vector3.Cross(edges[edge], Vector3.UnitY);
            axes[6 + edge * 3] = Vector3.Cross(edges[edge], Vector3.UnitZ);
        }
        return true;
    }

    private static Vector3 ClosestPoint(Vector3 point, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a; var ac = c - a; var ap = point - a;
        var d1 = Vector3.Dot(ab, ap); var d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        var bp = point - b;
        var d3 = Vector3.Dot(ab, bp); var d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;
        var vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
        var cp = point - c;
        var d5 = Vector3.Dot(ab, cp); var d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;
        var vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
        var va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
            return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
        var inverse = 1 / (va + vb + vc);
        return a + ab * (vb * inverse) + ac * (vc * inverse);
    }

    public static float Component(Vector3 vector, int axis) => axis == 0 ? vector.X : axis == 1 ? vector.Y : vector.Z;
}
