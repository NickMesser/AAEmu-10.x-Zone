using System.Numerics;
using D = AAEmu.Zone.Physics.ZoneManagedDouble3;

namespace AAEmu.Zone.Physics;

internal static class ZoneManagedPenetrationQueries
{
    public const float Tolerance = .0001f;

    public static bool Try(ZoneManagedConvexTarget target, ZonePhysicsShape shape, Vector3 center,
        ushort material, bool surface, out ZonePhysicsPenetration penetration)
    {
        penetration = default;
        ZoneManagedPenetrationResult result;
        bool found;
        if (target.Kind == ZoneManagedConvexTargetKind.Shape)
            found = ActorPair(shape, center, target.Shape, target.Center, out result);
        else if (target.Kind == ZoneManagedConvexTargetKind.Primitive && TryUprightPrimitive(target, out var upright))
            found = ActorPair(shape, center, upright, target.Center, out result);
        else if (ZoneManagedCertifiedSpherePenetration.Try(target, shape, center, out result))
            found = result.Depth > 0;
        else if (target.Kind == ZoneManagedConvexTargetKind.Triangle && shape.Kind == ZonePhysicsShapeKind.Box)
            found = TriangleSat(shape, center, target.A, target.B, target.C, false, out result);
        else if (target.Kind == ZoneManagedConvexTargetKind.Triangle && shape.UseCapsule)
            found = CapsuleTriangle(shape, center, target.A, target.B, target.C, out result);
        else if (target.Kind == ZoneManagedConvexTargetKind.SolidMesh && shape.Kind == ZonePhysicsShapeKind.Box && target.Mesh!.TriangleCount <= 64)
            found = MeshSat(shape, center, target, out result);
        else
        {
            if (!ZoneManagedConvexQueries.Overlaps(target, center, shape)) return false;
            found = ZoneManagedEpaPenetration.Try(target, shape, center, out result);
        }
        if (!found || result.Depth == 0) return false;
        if (surface && target.Kind == ZoneManagedConvexTargetKind.Triangle && target.Mesh is not null)
            result = ConsolidateInternalCoplanarContact(target, shape, center, result);
        if (!double.IsFinite(result.Depth) || result.Depth < 0 || result.Depth > float.MaxValue)
            throw ZoneManagedPenetrationMath.Failure("invalid positive depth");
        penetration = new((float)result.Depth, result.Normal, result.Point, result.MaterialOverride ?? material, surface, result.Ambiguous);
        return true;
    }

    private static ZoneManagedPenetrationResult ConsolidateInternalCoplanarContact(ZoneManagedConvexTarget target,
        ZonePhysicsShape shape, Vector3 center, ZoneManagedPenetrationResult original)
    {
        var face = D.Cross(D.From(target.B) - D.From(target.A), D.From(target.C) - D.From(target.A)).Unit;
        if (Math.Abs(D.Dot(face, D.From(original.Normal))) > .9999) return original;
        var signed = D.Dot(D.From(center) - D.From(target.A), face);
        var direction = signed > 0 ? face.Float : signed < 0 ? -face.Float :
            ZoneManagedPenetrationMath.Prefer(face.Float, -face.Float) ? face.Float : -face.Float;
        var radius = D.Dot(D.From(ZoneManagedConvexTarget.ShapeSupport(shape, direction)), D.From(direction));
        var depth = radius - Math.Abs(signed);
        if (depth <= 0) return original;
        var faceTouch = D.From(center) + D.From(direction) * depth + D.From(ZoneManagedConvexTarget.ShapeSupport(shape, -direction));
        var moved = center + original.Normal * (float)(original.Depth + ZoneManagedConvexQueries.ContactTolerance * 2);
        Matrix4x4.Invert(target.Transform, out var inverse);
        var bounds = new ZoneManagedBounds(moved - shape.BoundingHalfExtents, moved + shape.BoundingHalfExtents).Transform(inverse);
        var neighbors = new List<int>(); target.Mesh!.QueryBounds(bounds, neighbors);
        foreach (var triangle in neighbors)
        {
            if (triangle == target.SourceTriangle) continue;
            target.Mesh.GetTriangle(triangle, out var a, out var b, out var c, out var material);
            a = Vector3.Transform(a, target.Transform); b = Vector3.Transform(b, target.Transform); c = Vector3.Transform(c, target.Transform);
            var shared = new[] { a, b, c }.Count(vertex => vertex == target.A || vertex == target.B || vertex == target.C);
            if (shared < 2) continue; // Never join disjoint coplanar islands or fill a hole.
            var n = D.Cross(D.From(b) - D.From(a), D.From(c) - D.From(a)).Unit;
            if (Math.Abs(D.Dot(n, face)) < .99999 || Math.Abs(D.Dot(D.From(a) - D.From(target.A), face)) > Tolerance) continue;
            if (!ZoneManagedTriangleQueries.Overlaps(shape, moved, a, b, c)) continue;
            if (OnTriangle(faceTouch, target.A, target.B, target.C))
                return new(depth, direction, faceTouch.Float, true);
            if (OnTriangle(faceTouch, a, b, c))
                return new(depth, direction, faceTouch.Float, true, material);
        }
        return original;

        static bool OnTriangle(D point, Vector3 a, Vector3 b, Vector3 c)
        {
            ZoneManagedPenetrationMath.ClosestTriangle(point, D.From(a), D.From(b), D.From(c), out var wa, out var wb, out var wc);
            return (D.From(a) * wa + D.From(b) * wb + D.From(c) * wc - point).Squared <= (double)Tolerance * Tolerance;
        }
    }

    private static bool CapsuleTriangle(ZonePhysicsShape capsule, Vector3 center, Vector3 a, Vector3 b, Vector3 c,
        out ZoneManagedPenetrationResult result)
    {
        var raw = ZoneManagedCapsuleDistance.Triangle(capsule, center, a, b, c, false, false);
        if (!double.IsFinite(raw.Distance)) { result = default; return false; }
        if (raw.Distance > 1e-10)
        {
            var depth = capsule.Radius - raw.Distance;
            result = new(depth, raw.Normal, raw.Point, false); return depth > 0;
        }
        return TriangleSat(capsule, center, a, b, c, true, out result);
    }

    private static bool TriangleSat(ZonePhysicsShape shape, Vector3 center, Vector3 a, Vector3 b, Vector3 c,
        bool capsuleCore, out ZoneManagedPenetrationResult result)
    {
        var face = Vector3.Cross(b - a, c - a);
        if (face.LengthSquared() < 1e-18f) { result = default; return false; }
        var axes = new List<Vector3> { face };
        var edges = new[] { b - a, c - b, a - c };
        if (capsuleCore)
            foreach (var edge in edges) axes.Add(Vector3.Cross(edge, Vector3.UnitZ));
        else
        {
            axes.AddRange([Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ]);
            foreach (var edge in edges)
                axes.AddRange([Vector3.Cross(edge, Vector3.UnitX), Vector3.Cross(edge, Vector3.UnitY), Vector3.Cross(edge, Vector3.UnitZ)]);
        }
        var core = capsuleCore ? ZonePhysicsShape.Capsule(0, shape.SegmentHalfHeight) : shape;
        if (!Sat(ZoneManagedConvexTarget.ForTriangle(a, b, c), core, center, axes, out result)) return false;
        if (capsuleCore)
        {
            var depth = result.Depth + shape.Radius;
            var contact = ZoneManagedCapsuleDistance.Triangle(shape, center + result.Normal * (float)depth, a, b, c, false);
            result = result with { Depth = depth, Point = contact.Point };
        }
        return result.Depth > 0;
    }

    private static bool MeshSat(ZonePhysicsShape shape, Vector3 center, ZoneManagedConvexTarget target,
        out ZoneManagedPenetrationResult result)
    {
        var axes = new List<Vector3> { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
        for (var triangle = 0; triangle < target.Mesh!.TriangleCount; triangle++)
        {
            target.Mesh.GetTriangle(triangle, out var a, out var b, out var c, out _);
            a = Vector3.TransformNormal(a, target.Transform); b = Vector3.TransformNormal(b, target.Transform); c = Vector3.TransformNormal(c, target.Transform);
            axes.Add(Vector3.Cross(b - a, c - a));
            foreach (var edge in new[] { b - a, c - b, a - c })
                axes.AddRange([Vector3.Cross(edge, Vector3.UnitX), Vector3.Cross(edge, Vector3.UnitY), Vector3.Cross(edge, Vector3.UnitZ)]);
        }
        return Sat(target, shape, center, axes, out result);
    }

    private static bool Sat(ZoneManagedConvexTarget target, ZonePhysicsShape shape, Vector3 center,
        IEnumerable<Vector3> axes, out ZoneManagedPenetrationResult result)
    {
        var depth = double.PositiveInfinity;
        var normal = Vector3.Zero;
        var ambiguous = false;
        var delta = D.From(target.Center) - D.From(center);
        foreach (var raw in axes)
        {
            var axis = D.From(raw).Unit;
            if (axis.Squared == 0) continue;
            var n = axis.Float;
            var radius = D.Dot(D.From(ZoneManagedConvexTarget.ShapeSupport(shape, n)), axis);
            var minimum = D.Dot(D.From(target.SupportOffset(-n)) + delta, axis);
            var maximum = D.Dot(D.From(target.SupportOffset(n)) + delta, axis);
            var positive = maximum + radius;
            var negative = radius - minimum;
            if (positive < 0 || negative < 0) { result = default; return false; }
            var candidateDepth = Math.Min(positive, negative);
            var candidate = positive <= negative ? n : -n;
            var tie = Math.Abs(positive - negative) <= Tolerance;
            if (tie && ZoneManagedPenetrationMath.Prefer(-candidate, candidate)) candidate = -candidate;
            if (candidateDepth < depth - Tolerance * .01)
            { depth = candidateDepth; normal = candidate; ambiguous = tie; }
            else if (Math.Abs(candidateDepth - depth) <= Tolerance)
            {
                if (Vector3.Dot(candidate, normal) < .9999f) ambiguous = true;
                if (candidateDepth <= depth + Tolerance * .01 && ZoneManagedPenetrationMath.Prefer(candidate, normal))
                { depth = candidateDepth; normal = candidate; }
            }
        }
        if (!double.IsFinite(depth) || normal == Vector3.Zero) throw ZoneManagedPenetrationMath.Failure("SAT has no stable axis");
        var point = target.Center + target.SupportOffset(normal);
        if (shape.Kind == ZonePhysicsShapeKind.Box)
        {
            var witness = ZoneManagedConvexQueries.Distance(target, center + normal * (float)depth, shape);
            if (!double.IsFinite(witness.Distance) || witness.Distance > ZoneManagedConvexQueries.ContactTolerance * 2)
                throw ZoneManagedPenetrationMath.Failure("SAT translation has no verified touching witness");
            point = witness.Point;
        }
        else if (target.Kind == ZoneManagedConvexTargetKind.Triangle)
        {
            var moved = center + normal * (float)depth;
            var queryPoint = D.From(moved) + D.From(ZoneManagedConvexTarget.ShapeSupport(shape, -normal));
            ZoneManagedPenetrationMath.ClosestTriangle(queryPoint, D.From(target.A), D.From(target.B), D.From(target.C), out var wa, out var wb, out var wc);
            point = (D.From(target.A) * wa + D.From(target.B) * wb + D.From(target.C) * wc).Float;
        }
        result = new(depth, normal, point, ambiguous);
        return true; // A zero-depth segment core still acquires the capsule's positive radius.
    }

    private static bool ActorPair(ZonePhysicsShape query, Vector3 center, ZonePhysicsShape target, Vector3 targetCenter,
        out ZoneManagedPenetrationResult result)
    {
        var delta = D.From(center) - D.From(targetCenter);
        double depth; Vector3 normal; bool ambiguous;
        if (query.Kind == ZonePhysicsShapeKind.Box && target.Kind == ZonePhysicsShapeKind.Box)
            SignedBox(delta, D.From(query.HalfExtents) + D.From(target.HalfExtents), 0, out depth, out normal, out ambiguous);
        else if (query.UseCapsule && target.UseCapsule)
        {
            var z = Math.Sign(delta.Z) * Math.Max(0, Math.Abs(delta.Z) - query.SegmentHalfHeight - target.SegmentHalfHeight);
            var offset = new D(delta.X, delta.Y, z); var length = Math.Sqrt(offset.Squared);
            depth = query.Radius + target.Radius - length;
            ambiguous = length <= 1e-12;
            normal = length > 1e-12 ? offset.Unit.Float : query.SegmentHalfHeight + target.SegmentHalfHeight > 0 ? Vector3.UnitX : Vector3.UnitZ;
        }
        else if (query.Kind == ZonePhysicsShapeKind.Box && target.UseCapsule || query.UseCapsule && target.Kind == ZonePhysicsShapeKind.Box)
        {
            var box = query.Kind == ZonePhysicsShapeKind.Box ? query : target;
            var capsule = query.UseCapsule ? query : target;
            SignedBox(delta, new(box.HalfExtents.X, box.HalfExtents.Y, box.HalfExtents.Z + capsule.SegmentHalfHeight),
                capsule.Radius, out depth, out normal, out ambiguous);
        }
        else if (query.Kind == ZonePhysicsShapeKind.Cylinder && target.Kind == ZonePhysicsShapeKind.Cylinder)
            SignedCylinder(delta, query.Radius + target.Radius, query.SegmentHalfHeight + target.SegmentHalfHeight, 0,
                out depth, out normal, out ambiguous);
        else if (query.Kind != ZonePhysicsShapeKind.Box && target.Kind != ZonePhysicsShapeKind.Box)
        {
            var cylinder = query.Kind == ZonePhysicsShapeKind.Cylinder ? query : target;
            var capsule = query.UseCapsule ? query : target;
            SignedCylinder(delta, cylinder.Radius, cylinder.SegmentHalfHeight + capsule.SegmentHalfHeight, capsule.Radius,
                out depth, out normal, out ambiguous);
        }
        else
        {
            var box = query.Kind == ZonePhysicsShapeKind.Box ? query : target;
            var cylinder = query.Kind == ZonePhysicsShapeKind.Cylinder ? query : target;
            SignedBox(new(delta.X, delta.Y, 0), new(box.HalfExtents.X, box.HalfExtents.Y, double.PositiveInfinity), cylinder.Radius,
                out var radial, out var radialNormal, out var radialAmbiguous);
            var axial = box.HalfExtents.Z + cylinder.SegmentHalfHeight - Math.Abs(delta.Z);
            if (radial <= 0 || axial <= 0) { result = default; return false; }
            var axialNormal = Vector3.UnitZ * (delta.Z >= 0 ? 1 : -1);
            depth = Math.Min(radial, axial); normal = radial <= axial ? radialNormal : axialNormal;
            ambiguous = Math.Abs(radial - axial) <= Tolerance || radial <= axial && radialAmbiguous || axial < radial && delta.Z == 0;
            if (Math.Abs(radial - axial) <= Tolerance && ZoneManagedPenetrationMath.Prefer(axialNormal, normal))
            { normal = axialNormal; depth = axial; }
        }
        if (depth <= 0) { result = default; return false; }
        var moved = center + normal * (float)depth;
        var point = ContactOnShape(target, targetCenter, query, moved, normal);
        result = new(depth, normal, point, ambiguous); return true;
    }

    private static void SignedBox(D delta, D half, double padding, out double depth, out Vector3 normal, out bool ambiguous)
    {
        var outside = new D(Math.Sign(delta.X) * Math.Max(0, Math.Abs(delta.X) - half.X),
            Math.Sign(delta.Y) * Math.Max(0, Math.Abs(delta.Y) - half.Y), Math.Sign(delta.Z) * Math.Max(0, Math.Abs(delta.Z) - half.Z));
        var length = Math.Sqrt(outside.Squared);
        if (length > 0) { depth = padding - length; normal = outside.Unit.Float; ambiguous = false; return; }
        var margins = new[] { half.Z - Math.Abs(delta.Z), half.X - Math.Abs(delta.X), half.Y - Math.Abs(delta.Y) };
        var directions = new[] { Vector3.UnitZ * (delta.Z >= 0 ? 1 : -1), Vector3.UnitX * (delta.X >= 0 ? 1 : -1), Vector3.UnitY * (delta.Y >= 0 ? 1 : -1) };
        var index = 0;
        for (var i = 1; i < 3; i++) if (margins[i] < margins[index]) index = i;
        depth = margins[index] + padding; normal = directions[index];
        ambiguous = margins.Where((_, i) => i != index).Any(value => Math.Abs(value - margins[index]) <= Tolerance) ||
            index == 0 && delta.Z == 0 || index == 1 && delta.X == 0 || index == 2 && delta.Y == 0;
    }

    private static void SignedCylinder(D delta, double radius, double halfHeight, double padding,
        out double depth, out Vector3 normal, out bool ambiguous)
    {
        var radial = Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
        var outsideR = Math.Max(0, radial - radius); var outsideZ = Math.Max(0, Math.Abs(delta.Z) - halfHeight);
        var outside = Math.Sqrt(outsideR * outsideR + outsideZ * outsideZ);
        if (outside > 0)
        {
            depth = padding - outside;
            normal = new((float)(radial > 0 ? delta.X / radial * outsideR / outside : 0),
                (float)(radial > 0 ? delta.Y / radial * outsideR / outside : 0), (float)(Math.Sign(delta.Z) * outsideZ / outside));
            ambiguous = false; return;
        }
        var r = radius - radial; var z = halfHeight - Math.Abs(delta.Z);
        depth = Math.Min(r, z) + padding;
        normal = r < z ? radial > 0 ? new((float)(delta.X / radial), (float)(delta.Y / radial), 0) : Vector3.UnitX
            : Vector3.UnitZ * (delta.Z >= 0 ? 1 : -1);
        ambiguous = Math.Abs(r - z) <= Tolerance || r < z && radial == 0 || z <= r && delta.Z == 0;
    }

    private static Vector3 ContactOnShape(ZonePhysicsShape target, Vector3 targetCenter, ZonePhysicsShape query, Vector3 moved, Vector3 normal)
    {
        if (target.Kind == ZonePhysicsShapeKind.Box)
        {
            var p = Vector3.Clamp(moved, targetCenter - target.HalfExtents, targetCenter + target.HalfExtents);
            var axis = MathF.Abs(normal.Z) > .9999f ? 2 : MathF.Abs(normal.X) > .9999f ? 0 : MathF.Abs(normal.Y) > .9999f ? 1 : -1;
            if (axis == 0) p.X = targetCenter.X + MathF.Sign(normal.X) * target.HalfExtents.X;
            if (axis == 1) p.Y = targetCenter.Y + MathF.Sign(normal.Y) * target.HalfExtents.Y;
            if (axis == 2) p.Z = targetCenter.Z + MathF.Sign(normal.Z) * target.HalfExtents.Z;
            return p;
        }
        if (target.UseCapsule)
            return targetCenter + new Vector3(0, 0, Math.Clamp(moved.Z - targetCenter.Z, -target.SegmentHalfHeight, target.SegmentHalfHeight)) + normal * target.Radius;
        var radial = new Vector3(moved.X - targetCenter.X, moved.Y - targetCenter.Y, 0);
        if (MathF.Abs(normal.Z) > .9999f)
        {
            if (query.Kind == ZonePhysicsShapeKind.Box)
                radial = Vector3.Clamp(targetCenter, moved - query.HalfExtents, moved + query.HalfExtents) - targetCenter;
            radial.Z = 0;
            if (radial.LengthSquared() > target.Radius * target.Radius) radial = Vector3.Normalize(radial) * target.Radius;
            return targetCenter + radial + Vector3.UnitZ * (MathF.Sign(normal.Z) * target.SegmentHalfHeight);
        }
        var xy = new Vector3(normal.X, normal.Y, 0);
        if (xy.LengthSquared() > 0) xy = Vector3.Normalize(xy) * target.Radius;
        return targetCenter + xy + Vector3.UnitZ * Math.Clamp(moved.Z - targetCenter.Z, -target.SegmentHalfHeight, target.SegmentHalfHeight);
    }

    private static bool TryUprightPrimitive(ZoneManagedConvexTarget target, out ZonePhysicsShape shape)
    {
        var primitive = target.Primitive!;
        var x = Vector3.TransformNormal(Vector3.UnitX, target.Transform);
        var y = Vector3.TransformNormal(Vector3.UnitY, target.Transform);
        var z = Vector3.TransformNormal(Vector3.UnitZ, target.Transform);
        var xx = x.LengthSquared(); var yy = y.LengthSquared(); var zz = z.LengthSquared();
        var off = MathF.Abs(Vector3.Dot(x, y)) + MathF.Abs(Vector3.Dot(y, z)) + MathF.Abs(Vector3.Dot(z, x));
        var lower = Math.Max(0, Math.Min(xx, Math.Min(yy, zz)) - off);
        var upper = Math.Max(xx, Math.Max(yy, zz)) + off;
        var lowScale = Math.Sqrt(lower); var scale = Math.Sqrt(upper);
        var error = (scale - lowScale) * (primitive.Radius + primitive.HalfHeight);
        if (error > Tolerance * .1) { shape = default; return false; }
        if (primitive.Kind == ZoneManagedPrimitiveKind.Sphere)
        { shape = ZonePhysicsShape.Capsule((float)(primitive.Radius * scale), 0); return true; }
        var axis = Vector3.TransformNormal(primitive.Axis, target.Transform);
        axis = Vector3.Normalize(axis);
        if (Math.Sqrt((double)axis.X * axis.X + (double)axis.Y * axis.Y) * primitive.HalfHeight * scale > Tolerance * .1)
        { shape = default; return false; }
        shape = primitive.Kind == ZoneManagedPrimitiveKind.Capsule
            ? ZonePhysicsShape.Capsule((float)(primitive.Radius * scale), (float)(primitive.HalfHeight * scale))
            : ZonePhysicsShape.Cylinder((float)(primitive.Radius * scale), (float)(primitive.HalfHeight * scale));
        return true;
    }
}
