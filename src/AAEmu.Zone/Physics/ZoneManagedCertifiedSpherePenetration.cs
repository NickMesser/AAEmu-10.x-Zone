using System.Numerics;
using D = AAEmu.Zone.Physics.ZoneManagedDouble3;

namespace AAEmu.Zone.Physics;

/// <summary>
/// Exact support-bound certificate for a diagonal affine sphere and an axis-aligned offset.
/// No near-diagonal, near-axis or near-equal-radius admission tolerance is used. A case that
/// cannot attain the global lower bound continues through the ordinary convex solver.
/// </summary>
internal static class ZoneManagedCertifiedSpherePenetration
{
    /// <summary>
    /// True means the bound is attained, including a nonpositive depth proving touch/clear.
    /// For unit n, hTarget(n)+hQuery(n)-dot(delta,n) is at least target minimum radius plus
    /// query inscribed radius minus |delta|. An aligned minimum/support axis attains that bound,
    /// certifying the whole-solid MTD without polytope refinement or an added clearance.
    /// </summary>
    public static bool Try(ZoneManagedConvexTarget target, ZonePhysicsShape query, Vector3 center,
        out ZoneManagedPenetrationResult result)
    {
        result = default;
        if (target.Kind != ZoneManagedConvexTargetKind.Primitive || target.Primitive!.Kind != ZoneManagedPrimitiveKind.Sphere)
            return false;
        var matrix = target.Transform;
        if (matrix.M12 != 0 || matrix.M13 != 0 || matrix.M21 != 0 || matrix.M23 != 0 || matrix.M31 != 0 || matrix.M32 != 0)
            return false;
        var radius = (double)target.Primitive.Radius;
        var radii = new D(radius * Math.Abs(matrix.M11), radius * Math.Abs(matrix.M22), radius * Math.Abs(matrix.M33));
        var minimumRadius = Math.Min(radii.X, Math.Min(radii.Y, radii.Z));
        var inscribedRadius = query.Kind == ZonePhysicsShapeKind.Box
            ? Math.Min(query.HalfExtents.X, Math.Min(query.HalfExtents.Y, query.HalfExtents.Z))
            : query.UseCapsule ? query.Radius : Math.Min(query.Radius, query.SegmentHalfHeight);
        var delta = D.From(center) - D.From(target.Center);
        // +Z, +X, +Y matches the general kernel's deterministic symmetric-direction preference.
        foreach (var axis in new[] { 2, 0, 1 })
        {
            var targetRadius = Component(radii, axis);
            if (targetRadius != minimumRadius) continue;
            var supportRadius = query.Kind == ZonePhysicsShapeKind.Box
                ? ZoneManagedTriangleQueries.Component(query.HalfExtents, axis)
                : axis != 2 ? query.Radius : query.SegmentHalfHeight + (query.UseCapsule ? query.Radius : 0d);
            if (supportRadius != inscribedRadius) continue;
            if (axis != 0 && delta.X != 0 || axis != 1 && delta.Y != 0 || axis != 2 && delta.Z != 0) continue;
            var offset = Component(delta, axis);
            var normal = (axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ) * (offset >= 0 ? 1 : -1);
            var depth = minimumRadius + inscribedRadius - Math.Abs(offset);
            var point = (D.From(target.Center) + D.From(normal) * targetRadius).Float;
            result = new(depth, normal, point, delta.Squared == 0);
            return true;
        }
        return false;
    }

    private static double Component(D value, int axis) => axis == 0 ? value.X : axis == 1 ? value.Y : value.Z;
}
