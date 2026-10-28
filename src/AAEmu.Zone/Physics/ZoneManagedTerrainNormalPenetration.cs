using System.Numerics;
using D = AAEmu.Zone.Physics.ZoneManagedDouble3;

namespace AAEmu.Zone.Physics;

/// <summary>
/// Front-facing terrain recovery constraint. A real positive finite-face intersection is
/// required; the correction then follows the authored upward normal, including when the
/// nearer two-sided exit would lower the body beneath the heightfield. Interior face depth is
/// exact. At finite boundaries this is a conservative face constraint, not a global edge MTD.
/// </summary>
internal static class ZoneManagedTerrainNormalPenetration
{
    public static bool Try(ZonePhysicsShape shape, Vector3 center, Vector3 a, Vector3 b, Vector3 c,
        ushort material, out ZonePhysicsPenetration penetration)
    {
        penetration = default;
        if (!ZoneManagedPenetrationQueries.Try(ZoneManagedConvexTarget.ForTriangle(a, b, c), shape,
            center, material, true, out var intersection)) return false;
        var normal = D.Cross(D.From(b) - D.From(a), D.From(c) - D.From(a)).Unit;
        if (normal.Z <= 0) throw ZoneManagedPenetrationMath.Failure("terrain recovery requires an upward authored face");
        double support;
        if (shape.Kind == ZonePhysicsShapeKind.Box)
            support = Math.Abs(normal.X) * shape.HalfExtents.X + Math.Abs(normal.Y) * shape.HalfExtents.Y + Math.Abs(normal.Z) * shape.HalfExtents.Z;
        else if (shape.UseCapsule) support = shape.Radius + shape.SegmentHalfHeight * normal.Z;
        else support = shape.Radius * Math.Sqrt(normal.X * normal.X + normal.Y * normal.Y) + shape.SegmentHalfHeight * normal.Z;
        var signed = D.Dot(D.From(center) - D.From(a), normal);
        var depth = support - signed;
        if (depth <= 0) return false;
        if (!double.IsFinite(depth) || depth > float.MaxValue)
            throw ZoneManagedPenetrationMath.Failure("terrain normal constraint has invalid positive depth");
        var direction = normal.Float;
        var faceTouch = D.From(center) + normal * depth + D.From(ZoneManagedConvexTarget.ShapeSupport(shape, -direction));
        ZoneManagedPenetrationMath.ClosestTriangle(faceTouch, D.From(a), D.From(b), D.From(c), out var wa, out var wb, out var wc);
        var witness = D.From(a) * wa + D.From(b) * wb + D.From(c) * wc;
        var boundary = (witness - faceTouch).Squared > (double)ZoneManagedPenetrationQueries.Tolerance * ZoneManagedPenetrationQueries.Tolerance;
        penetration = new((float)depth, direction, witness.Float, material, true, intersection.IsAmbiguous || boundary);
        return true;
    }
}
