using System.Numerics;
using D = AAEmu.Zone.Physics.ZoneManagedDouble3;

namespace AAEmu.Zone.Physics;

internal static class ZoneManagedDirectedUnprojectionQueries
{
    public static bool Try(ZoneManagedConvexTarget target, ZonePhysicsShape query, Vector3 center,
        ushort material, bool surface, out ZonePhysicsDirectedUnprojection result)
    {
        result = default;
        if (!ZoneManagedPenetrationQueries.Try(target, query, center, material, surface, out var fallback)) return false;
        if (target.Kind == ZoneManagedConvexTargetKind.Shape)
        {
            var depth = UprightPairExit(query, center, target.Shape, target.Center);
            var moved = new Vector3(center.X, center.Y, (float)(center.Z + depth));
            var witness = ZoneManagedConvexQueries.Distance(target, moved, query);
            if (!double.IsFinite(witness.Distance) || witness.Distance > ZoneManagedConvexQueries.ContactTolerance * 2)
                throw Failure("analytic actor exit has no verified touching witness");
            result = new((float)depth, witness.Point, material, false, 0, fallback);
            return true;
        }
        if (query.Kind == ZonePhysicsShapeKind.Box &&
            (target.Kind == ZoneManagedConvexTargetKind.Triangle || target.Kind == ZoneManagedConvexTargetKind.SolidMesh && target.Mesh!.TriangleCount <= 64))
        {
            var depth = PolytopeExit(target, query, center);
            var witness = ZoneManagedConvexQueries.Distance(target, new(center.X, center.Y, (float)(center.Z + depth)), query);
            if (!double.IsFinite(witness.Distance) || witness.Distance > ZoneManagedConvexQueries.ContactTolerance * 2)
                throw Failure("analytic polytope exit has no verified touching witness");
            result = new((float)depth, witness.Point, material, surface, 0, fallback);
            return true;
        }
        if (target.Kind == ZoneManagedConvexTargetKind.Triangle)
        {
            var cross = D.Cross(D.From(target.B) - D.From(target.A), D.From(target.C) - D.From(target.A));
            if (cross.Z != 0)
            {
                var b = cross.Z > 0 ? target.B : target.C;
                var c = cross.Z > 0 ? target.C : target.B;
                if (!ZoneManagedTerrainPenetration.Try(query, center, target.A, b, c, material, out var upward))
                    throw Failure("an initial triangle intersection lost its analytic upward footprint");
                result = new(upward.Depth, upward.Position, material, true, 0, fallback);
                return true;
            }
        }
        return ReverseAdvance(target, query, center, material, surface, fallback, out result);
    }

    private static double PolytopeExit(ZoneManagedConvexTarget target, ZonePhysicsShape query, Vector3 center)
    {
        var exit = double.PositiveInfinity;
        var delta = D.From(target.Center) - D.From(center);
        foreach (var raw in Axes())
        {
            var axis = D.From(raw).Unit;
            if (axis.Z == 0) continue;
            if (axis.Z < 0) axis = -axis;
            var n = axis.Float;
            var high = D.Dot(D.From(target.SupportOffset(n)) + delta, axis);
            var radius = D.Dot(D.From(ZoneManagedConvexTarget.ShapeSupport(query, n)), axis);
            exit = Math.Min(exit, (high + radius) / axis.Z);
        }
        if (!double.IsFinite(exit) || exit <= 0 || exit > float.MaxValue)
            throw Failure("polytope has no finite positive upward exit");
        return exit;

        IEnumerable<Vector3> Axes()
        {
            yield return Vector3.UnitX; yield return Vector3.UnitY; yield return Vector3.UnitZ;
            var count = target.Kind == ZoneManagedConvexTargetKind.Triangle ? 1 : target.Mesh!.TriangleCount;
            for (var triangle = 0; triangle < count; triangle++)
            {
                var a = target.A; var b = target.B; var c = target.C;
                if (target.Kind == ZoneManagedConvexTargetKind.SolidMesh)
                {
                    target.Mesh!.GetTriangle(triangle, out a, out b, out c, out _);
                    a = Vector3.TransformNormal(a, target.Transform); b = Vector3.TransformNormal(b, target.Transform); c = Vector3.TransformNormal(c, target.Transform);
                }
                yield return Vector3.Cross(b - a, c - a);
                foreach (var edge in new[] { b - a, c - b, a - c })
                {
                    yield return Vector3.Cross(edge, Vector3.UnitX);
                    yield return Vector3.Cross(edge, Vector3.UnitY);
                    yield return Vector3.Cross(edge, Vector3.UnitZ);
                }
            }
        }
    }

    // The upright Minkowski sums are a box, capsule, rounded box, cylinder, rounded cylinder,
    // or a rounded XY rectangle times an axial interval. Their upper boundary at fixed XY is
    // analytic. This is an exit along +Z even when the shorter normal solution points downward.
    private static double UprightPairExit(ZonePhysicsShape query, Vector3 center, ZonePhysicsShape target, Vector3 targetCenter)
    {
        var delta = D.From(center) - D.From(targetCenter);
        var qBox = query.Kind == ZonePhysicsShapeKind.Box; var tBox = target.Kind == ZonePhysicsShapeKind.Box;
        double top;
        if (qBox || tBox)
        {
            if (qBox && tBox) top = (double)query.HalfExtents.Z + target.HalfExtents.Z;
            else
            {
                var box = qBox ? query : target; var curve = qBox ? target : query;
                top = (double)box.HalfExtents.Z + curve.SegmentHalfHeight;
                if (curve.UseCapsule)
                {
                    var dx = Math.Max(0, Math.Abs(delta.X) - box.HalfExtents.X);
                    var dy = Math.Max(0, Math.Abs(delta.Y) - box.HalfExtents.Y);
                    top += Root((double)curve.Radius * curve.Radius - dx * dx - dy * dy);
                }
            }
        }
        else
        {
            top = (double)query.SegmentHalfHeight + target.SegmentHalfHeight;
            if (query.UseCapsule && target.UseCapsule)
            {
                var r = (double)query.Radius + target.Radius;
                top += Root(r * r - delta.X * delta.X - delta.Y * delta.Y);
            }
            else if (query.UseCapsule || target.UseCapsule)
            {
                var capsule = query.UseCapsule ? query : target;
                var cylinder = query.UseCapsule ? target : query;
                var outside = Math.Max(0, Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y) - cylinder.Radius);
                top += Root((double)capsule.Radius * capsule.Radius - outside * outside);
            }
        }
        var depth = top - delta.Z;
        if (!double.IsFinite(depth) || depth <= 0 || depth > float.MaxValue)
            throw Failure("analytic actor exit is not finite and positive");
        return depth;

        static double Root(double squared)
        {
            if (squared < 0) throw Failure("initial actor intersection contradicts its analytic footprint");
            return Math.Sqrt(squared);
        }
    }

    private static bool ReverseAdvance(ZoneManagedConvexTarget target, ZonePhysicsShape query, Vector3 center,
        ushort material, bool surface, ZonePhysicsPenetration fallback, out ZonePhysicsDirectedUnprojection result)
    {
        result = default;
        var required = -Gap(center, Vector3.UnitZ);
        var upper = new Vector3(center.X, center.Y, (float)(center.Z + required + .01));
        ZoneManagedTerrainGeometry.ValidateFinite(upper, nameof(center));
        if (upper.Z <= center.Z || Gap(upper, Vector3.UnitZ) <= ZoneManagedConvexQueries.ContactTolerance * 2)
            throw Failure("no support-plane-certified separated upper point");
        var position = upper;
        for (var iteration = 0; iteration < 96; iteration++)
        {
            var distance = ZoneManagedConvexQueries.Distance(target, position, query);
            if (!double.IsFinite(distance.Distance) || !float.IsFinite(distance.Normal.LengthSquared()) || distance.Normal.LengthSquared() < .5f)
                throw Failure("non-finite reverse convex distance");
            var gap = Gap(position, distance.Normal);
            if (!double.IsFinite(gap) || gap <= 0)
                throw Failure("reverse advancement lost its clear-side support certificate");
            if (distance.Distance <= ZoneManagedConvexQueries.ContactTolerance)
            {
                var depth = (double)position.Z - center.Z;
                if (depth <= 0 || depth > float.MaxValue) throw Failure("reverse exit is not positive");
                result = new((float)depth, distance.Point, material, surface, ZoneManagedConvexQueries.ContactTolerance, fallback);
                return true;
            }
            if (distance.Normal.Z <= 0) throw Failure("initial target cannot be approached from the certified upper point");
            // Move only to a plane that still strictly separates this individual target. A
            // scene-wide cast could select an unrelated overhead obstacle and is never used.
            var clearGap = Math.Min(ZoneManagedConvexQueries.ContactTolerance * .5, gap * .5);
            var advance = (gap - clearGap) / distance.Normal.Z;
            var next = new Vector3(center.X, center.Y, (float)(position.Z - advance));
            if (next.Z <= center.Z || next.Z >= position.Z || !float.IsFinite(next.Z))
                throw Failure("reverse advancement exceeded its initial intersection or coordinate precision");
            position = next;
        }
        throw Failure("reverse advancement exceeded its iteration bound");

        double Gap(Vector3 at, Vector3 normal)
        {
            var support = ZoneManagedPenetrationMath.Support(target, query, at, -D.From(normal));
            return D.Dot(D.From(normal), support.Difference);
        }
    }

    private static InvalidOperationException Failure(string reason) =>
        new($"Managed directed unprojection failed: {reason}; no directed depth is inferred.");
}
