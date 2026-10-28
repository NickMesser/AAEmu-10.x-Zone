using System.Numerics;

namespace AAEmu.Zone.Physics;

/// <summary>Exact segment distances for upright capsules; no capsule bounding-box substitution.</summary>
internal static class ZoneManagedCapsuleDistance
{
    public static ZoneManagedConvexDistance Triangle(ZonePhysicsShape capsule, Vector3 center,
        Vector3 worldA, Vector3 worldB, Vector3 worldC, bool solidBelow)
    {
        var a = V.From(worldA) - V.From(center); var b = V.From(worldB) - V.From(center); var c = V.From(worldC) - V.From(center);
        var face = V.Cross(b - a, c - a).Unit;
        if (face.Squared == 0) return new(double.PositiveInfinity, Vector3.Zero, default);
        var p = new V(0, 0, -capsule.SegmentHalfHeight); var q = new V(0, 0, capsule.SegmentHalfHeight);
        var best = double.PositiveInfinity;
        var onAxis = p; var onTarget = a;
        Check(p, ClosestTriangle(p, a, b, c)); Check(q, ClosestTriangle(q, a, b, c));
        Edge(a, b); Edge(b, c); Edge(c, a);
        var denominator = V.Dot(face, q - p);
        if (Math.Abs(denominator) > 1e-18)
        {
            var t = V.Dot(face, a - p) / denominator;
            if (t >= 0 && t <= 1)
            {
                var cross = p + (q - p) * t;
                if ((ClosestTriangle(cross, a, b, c) - cross).Squared < 1e-18) Check(cross, cross);
            }
        }
        if (solidBelow)
        {
            var determinant = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
            if (determinant != 0)
            {
                var u = ((b.Y - c.Y) * -c.X + (c.X - b.X) * -c.Y) / determinant;
                var v = ((c.Y - a.Y) * -c.X + (a.X - c.X) * -c.Y) / determinant;
                var height = u * a.Z + v * b.Z + (1 - u - v) * c.Z;
                if (u >= 0 && v >= 0 && u + v <= 1 && p.Z <= height)
                    Check(p, p);
            }
            Side(a, b); Side(b, c); Side(c, a);
        }
        var distance = Math.Sqrt(best);
        var normal = distance > 1e-12 ? (onAxis - onTarget) * (1 / distance)
            : solidBelow || V.Dot(face, -a) >= 0 ? face : face * -1;
        return new(Math.Max(0, distance - capsule.Radius), normal.Float, (V.From(center) + onTarget).Float);

        void Check(V axis, V target)
        {
            var squared = (axis - target).Squared;
            if (squared >= best) return;
            best = squared; onAxis = axis; onTarget = target;
        }
        void Edge(V first, V last)
        {
            SegmentSegment(p, q, first, last, out var axis, out var target); Check(axis, target);
        }
        void Side(V first, V last)
        {
            var dx = last.X - first.X; var dy = last.Y - first.Y;
            var length = dx * dx + dy * dy;
            var t = length > 0 ? Math.Clamp(-(first.X * dx + first.Y * dy) / length, 0, 1) : 0;
            var point = first + (last - first) * t;
            if (p.Z > point.Z) return;
            var z = Math.Clamp(point.Z, p.Z, q.Z);
            Check(new(0, 0, z), new(point.X, point.Y, z));
        }
    }

    public static bool TryShapes(ZonePhysicsShape shape, Vector3 center, ZonePhysicsShape target, Vector3 targetCenter,
        out ZoneManagedConvexDistance distance)
    {
        if (shape.UseCapsule && target.UseCapsule)
        {
            var delta = V.From(targetCenter) - V.From(center);
            var z = Math.Clamp(delta.Z, -shape.SegmentHalfHeight, shape.SegmentHalfHeight);
            var otherZ = Math.Clamp(z, delta.Z - target.SegmentHalfHeight, delta.Z + target.SegmentHalfHeight);
            var targetAxis = new V(delta.X, delta.Y, otherZ);
            var offset = new V(0, 0, z) - targetAxis;
            var length = Math.Sqrt(offset.Squared);
            var normal = length > 1e-12 ? offset * (1 / length) : new V(1, 0, 0);
            distance = new(Math.Max(0, length - shape.Radius - target.Radius), normal.Float,
                (V.From(center) + targetAxis + normal * target.Radius).Float);
            return true;
        }
        if (shape.UseCapsule && target.Kind == ZonePhysicsShapeKind.Box)
        { distance = CapsuleBox(shape, center, target, targetCenter); return true; }
        if (shape.Kind == ZonePhysicsShapeKind.Box && target.UseCapsule)
        {
            var reverse = CapsuleBox(target, targetCenter, shape, center);
            distance = new(reverse.Distance, -reverse.Normal, reverse.Point + reverse.Normal * (float)reverse.Distance);
            return true;
        }
        distance = default; return false;
    }

    private static ZoneManagedConvexDistance CapsuleBox(ZonePhysicsShape capsule, Vector3 center, ZonePhysicsShape box, Vector3 boxCenter)
    {
        var delta = V.From(boxCenter) - V.From(center);
        var z = Math.Clamp(delta.Z, -capsule.SegmentHalfHeight, capsule.SegmentHalfHeight);
        var axis = new V(0, 0, z);
        var target = new V(Math.Clamp(0, delta.X - box.HalfExtents.X, delta.X + box.HalfExtents.X),
            Math.Clamp(0, delta.Y - box.HalfExtents.Y, delta.Y + box.HalfExtents.Y),
            Math.Clamp(z, delta.Z - box.HalfExtents.Z, delta.Z + box.HalfExtents.Z));
        var offset = axis - target;
        var length = Math.Sqrt(offset.Squared);
        var normal = length > 1e-12 ? offset * (1 / length) : new V(0, 0, center.Z >= boxCenter.Z ? 1 : -1);
        return new(Math.Max(0, length - capsule.Radius), normal.Float, (V.From(center) + target).Float);
    }

    private static void SegmentSegment(V p, V q, V a, V b, out V first, out V second)
    {
        var d = q - p; var e = b - a; var r = p - a;
        var dd = V.Dot(d, d); var ee = V.Dot(e, e); var de = V.Dot(d, e);
        var dr = V.Dot(d, r); var er = V.Dot(e, r);
        var determinant = dd * ee - de * de;
        var s = dd > 0 && determinant > 1e-24 ? Math.Clamp((de * er - dr * ee) / determinant, 0, 1) : 0;
        var t = ee > 0 ? (de * s + er) / ee : 0;
        if (t < 0) { t = 0; s = dd > 0 ? Math.Clamp(-dr / dd, 0, 1) : 0; }
        if (t > 1) { t = 1; s = dd > 0 ? Math.Clamp((de - dr) / dd, 0, 1) : 0; }
        first = p + d * s; second = a + e * t;
    }

    private static V ClosestTriangle(V point, V a, V b, V c)
    {
        var ab = b - a; var ac = c - a; var ap = point - a;
        var d1 = V.Dot(ab, ap); var d2 = V.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        var bp = point - b; var d3 = V.Dot(ab, bp); var d4 = V.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;
        var vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
        var cp = point - c; var d5 = V.Dot(ab, cp); var d6 = V.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;
        var vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
        var va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
        var inverse = 1 / (va + vb + vc); return a + ab * (vb * inverse) + ac * (vc * inverse);
    }

    private readonly record struct V(double X, double Y, double Z)
    {
        public static V From(Vector3 value) => new(value.X, value.Y, value.Z);
        public Vector3 Float => new((float)X, (float)Y, (float)Z);
        public double Squared => Dot(this, this);
        public V Unit => Squared > 0 ? this * (1 / Math.Sqrt(Squared)) : default;
        public static double Dot(V a, V b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static V Cross(V a, V b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        public static V operator +(V a, V b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static V operator -(V a, V b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static V operator -(V a) => a * -1;
        public static V operator *(V a, double scale) => new(a.X * scale, a.Y * scale, a.Z * scale);
    }
}
