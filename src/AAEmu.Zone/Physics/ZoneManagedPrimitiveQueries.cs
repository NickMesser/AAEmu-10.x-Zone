using System.Numerics;

namespace AAEmu.Zone.Physics;

/// <summary>Analytic target adapter over the shared convex distance/advancement kernel.</summary>
internal static class ZoneManagedPrimitiveQueries
{
    public const float ContactTolerance = ZoneManagedConvexQueries.ContactTolerance;
    public static bool Overlaps(ZoneManagedPrimitiveGeometry primitive, Matrix4x4 transform, Vector3 center, Vector3 extents) =>
        Overlaps(primitive, transform, center, ZonePhysicsShape.Box(extents));
    public static bool Overlaps(ZoneManagedPrimitiveGeometry primitive, Matrix4x4 transform, Vector3 center, ZonePhysicsShape shape) =>
        ZoneManagedConvexQueries.Overlaps(ZoneManagedConvexTarget.ForPrimitive(primitive, transform), center, shape);
    public static bool Sweep(ZoneManagedPrimitiveGeometry primitive, Matrix4x4 transform, Vector3 origin, Vector3 extents,
        Vector3 displacement, out float fraction, out Vector3 normal, out Vector3 position) =>
        Sweep(primitive, transform, origin, ZonePhysicsShape.Box(extents), displacement, out fraction, out normal, out position);
    public static bool Sweep(ZoneManagedPrimitiveGeometry primitive, Matrix4x4 transform, Vector3 origin, ZonePhysicsShape shape,
        Vector3 displacement, out float fraction, out Vector3 normal, out Vector3 position) =>
        ZoneManagedConvexQueries.Sweep(ZoneManagedConvexTarget.ForPrimitive(primitive, transform), origin, shape, displacement,
            out fraction, out normal, out position);
}
