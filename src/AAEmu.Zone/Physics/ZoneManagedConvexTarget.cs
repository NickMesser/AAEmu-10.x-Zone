using System.Numerics;

namespace AAEmu.Zone.Physics;

internal enum ZoneManagedConvexTargetKind { Primitive, Shape, Triangle, Terrain }

internal readonly record struct ZoneManagedConvexTarget
{
    public ZoneManagedConvexTargetKind Kind { get; init; }
    public ZoneManagedPrimitiveGeometry? Primitive { get; init; }
    public Matrix4x4 Transform { get; init; }
    public ZonePhysicsShape Shape { get; init; }
    public Vector3 Center { get; init; }
    public Vector3 A { get; init; }
    public Vector3 B { get; init; }
    public Vector3 C { get; init; }
    public float Bottom { get; init; }

    public static ZoneManagedConvexTarget ForPrimitive(ZoneManagedPrimitiveGeometry primitive, Matrix4x4 transform) =>
        new() { Kind = ZoneManagedConvexTargetKind.Primitive, Primitive = primitive, Transform = transform,
            Center = Vector3.Transform(primitive.Center, transform) };
    public static ZoneManagedConvexTarget ForShape(ZonePhysicsShape shape, Vector3 center) =>
        new() { Kind = ZoneManagedConvexTargetKind.Shape, Shape = shape, Center = center };
    public static ZoneManagedConvexTarget ForTriangle(Vector3 a, Vector3 b, Vector3 c) =>
        new() { Kind = ZoneManagedConvexTargetKind.Triangle, Center = a, A = a, B = b, C = c };
    public static ZoneManagedConvexTarget ForTerrain(Vector3 a, Vector3 b, Vector3 c, float bottom) =>
        new() { Kind = ZoneManagedConvexTargetKind.Terrain, Center = a, A = a, B = b, C = c, Bottom = bottom };

    public Vector3 SupportOffset(Vector3 direction)
    {
        if (Kind == ZoneManagedConvexTargetKind.Primitive)
            return Vector3.TransformNormal(Primitive!.SupportOffset(Vector3.TransformNormal(direction, Matrix4x4.Transpose(Transform))), Transform);
        if (Kind == ZoneManagedConvexTargetKind.Shape) return ShapeSupport(Shape, direction);
        var a = A - Center; var b = B - Center; var c = C - Center;
        if (Kind == ZoneManagedConvexTargetKind.Terrain && direction.Z < 0)
            a.Z = b.Z = c.Z = Bottom - Center.Z;
        var best = a;
        if (Dot(b, direction) > Dot(best, direction)) best = b;
        if (Dot(c, direction) > Dot(best, direction)) best = c;
        return best;
    }

    public static Vector3 ShapeSupport(ZonePhysicsShape shape, Vector3 direction)
    {
        if (shape.Kind == ZonePhysicsShapeKind.Box)
            return new(direction.X >= 0 ? shape.HalfExtents.X : -shape.HalfExtents.X,
                direction.Y >= 0 ? shape.HalfExtents.Y : -shape.HalfExtents.Y,
                direction.Z >= 0 ? shape.HalfExtents.Z : -shape.HalfExtents.Z);
        var radial = shape.UseCapsule ? direction : new Vector3(direction.X, direction.Y, 0);
        var squared = Dot(radial, radial);
        var normalized = squared > 0 ? radial / (float)Math.Sqrt(squared) : Vector3.Zero;
        return normalized * shape.Radius + new Vector3(0, 0, direction.Z >= 0 ? shape.SegmentHalfHeight : -shape.SegmentHalfHeight);
    }

    public Vector3 ContactNormal(Vector3 point, Vector3 queryCenter, ZonePhysicsShape query, Vector3 fallback)
    {
        if (Kind == ZoneManagedConvexTargetKind.Primitive)
        {
            Matrix4x4.Invert(Transform, out var inverse);
            var localPoint = Primitive!.Center + Vector3.TransformNormal(point - Center, inverse);
            var normal = Vector3.TransformNormal(Primitive.OutwardNormal(localPoint), Matrix4x4.Transpose(inverse));
            return normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : fallback;
        }
        if (Kind is ZoneManagedConvexTargetKind.Triangle or ZoneManagedConvexTargetKind.Terrain)
        {
            var normal = Vector3.Normalize(Vector3.Cross(B - A, C - A));
            var signed = Vector3.Dot(queryCenter - A, normal);
            var radius = Vector3.Dot(ShapeSupport(query, normal), normal);
            if (MathF.Abs(MathF.Abs(signed) - radius) <= ZoneManagedConvexQueries.ContactTolerance * 2)
                return signed >= 0 ? normal : -normal;
            return fallback;
        }
        if (Shape.Kind != ZonePhysicsShapeKind.Box)
        {
            var relative = point - Center;
            if (Shape.UseCapsule)
            {
                relative.Z -= Math.Clamp(relative.Z, -Shape.SegmentHalfHeight, Shape.SegmentHalfHeight);
                return relative.LengthSquared() > 1e-12f ? Vector3.Normalize(relative) : fallback;
            }
            var radial = new Vector3(relative.X, relative.Y, 0);
            return Shape.SegmentHalfHeight - MathF.Abs(relative.Z) <= Shape.Radius - radial.Length()
                ? Vector3.UnitZ * (relative.Z >= 0 ? 1 : -1) : radial.LengthSquared() > 1e-12f ? Vector3.Normalize(radial) : fallback;
        }
        return fallback;
    }

    private static double Dot(Vector3 a, Vector3 b) => (double)a.X * b.X + (double)a.Y * b.Y + (double)a.Z * b.Z;
}

internal readonly record struct ZoneManagedConvexDistance(double Distance, Vector3 Normal, Vector3 Point);

/// <summary>Registered upright actor-pair queries over the same convex shapes as static queries.</summary>
internal static class ZoneManagedShapeQueries
{
    public static bool Overlaps(ZonePhysicsShape shape, Vector3 center, ZonePhysicsShape otherShape, Vector3 otherCenter)
    {
        ZoneManagedTerrainGeometry.ValidateShape(shape); ZoneManagedTerrainGeometry.ValidateShape(otherShape);
        ZoneManagedTerrainGeometry.ValidateFinite(center, nameof(center)); ZoneManagedTerrainGeometry.ValidateFinite(otherCenter, nameof(otherCenter));
        var delta = Vector3.Abs(center - otherCenter);
        var bounds = shape.BoundingHalfExtents + otherShape.BoundingHalfExtents;
        if (shape.Kind == ZonePhysicsShapeKind.Box && otherShape.Kind == ZonePhysicsShapeKind.Box)
            return delta.X <= bounds.X && delta.Y <= bounds.Y && delta.Z <= bounds.Z;
        bounds += new Vector3(ZoneManagedConvexQueries.ContactTolerance);
        if (delta.X > bounds.X || delta.Y > bounds.Y || delta.Z > bounds.Z) return false;
        return ZoneManagedConvexQueries.Overlaps(ZoneManagedConvexTarget.ForShape(otherShape, otherCenter), center, shape);
    }
    public static bool Sweep(ZonePhysicsShape shape, Vector3 center, Vector3 displacement,
        ZonePhysicsShape targetShape, Vector3 targetCenter, out float fraction, out Vector3 normal, out Vector3 point)
    {
        ZoneManagedTerrainGeometry.ValidateShape(shape); ZoneManagedTerrainGeometry.ValidateShape(targetShape);
        ZoneManagedTerrainGeometry.ValidateSegment(center, displacement); ZoneManagedTerrainGeometry.ValidateFinite(targetCenter, nameof(targetCenter));
        if (displacement == Vector3.Zero) { fraction = 0; normal = point = default; return false; }
        if (shape.Kind == ZonePhysicsShapeKind.Box && targetShape.Kind == ZonePhysicsShapeKind.Box)
            return SweepBoxes(shape, center, displacement, targetShape, targetCenter, out fraction, out normal, out point);
        var bounds = shape.BoundingHalfExtents + targetShape.BoundingHalfExtents + new Vector3(ZoneManagedConvexQueries.ContactTolerance);
        if (!new ZoneManagedBounds(-bounds, bounds).IntersectsSegment(center - targetCenter, displacement))
        { fraction = 0; normal = point = default; return false; }
        return ZoneManagedConvexQueries.Sweep(ZoneManagedConvexTarget.ForShape(targetShape, targetCenter), center, shape, displacement,
            out fraction, out normal, out point);
    }

    // Preserve the existing exact slab result for actor box pairs; curved pairs use their actual
    // convex shapes after this same inexpensive candidate rejection, not a bounding-box answer.
    private static bool SweepBoxes(ZonePhysicsShape shape, Vector3 center, Vector3 displacement,
        ZonePhysicsShape target, Vector3 targetCenter, out float fraction, out Vector3 normal, out Vector3 point)
    {
        fraction = 0; normal = point = default;
        var origin = center - targetCenter;
        var bounds = shape.HalfExtents + target.HalfExtents;
        var end = 1f;
        var initial = true;
        var penetration = float.PositiveInfinity;
        var initialNormal = Vector3.Zero;
        for (var axis = 0; axis < 3; axis++)
        {
            var o = ZoneManagedTriangleQueries.Component(origin, axis);
            var d = ZoneManagedTriangleQueries.Component(displacement, axis);
            var r = ZoneManagedTriangleQueries.Component(bounds, axis);
            var direction = axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
            var depth = r - MathF.Abs(o);
            if (depth < 0) initial = false;
            if (depth < penetration)
            { penetration = depth; initialNormal = direction * (o != 0 ? MathF.Sign(o) : d > 0 ? -1 : 1); }
            if (d == 0) { if (MathF.Abs(o) > r) return false; continue; }
            var first = (-r - o) / d; var last = (r - o) / d;
            var entryNormal = -direction;
            if (first > last) { (first, last) = (last, first); entryNormal = direction; }
            if (first > fraction) { fraction = first; normal = entryNormal; }
            end = MathF.Min(end, last);
            if (fraction > end) return false;
        }
        if (initial) normal = initialNormal;
        if (fraction > 1 || end < 0 || Vector3.Dot(displacement, normal) >= 0) return false;
        point = center + displacement * fraction - normal * Vector3.Dot(Vector3.Abs(normal), shape.HalfExtents);
        return true;
    }
}
