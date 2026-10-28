using System.Numerics;

namespace AAEmu.Zone.Physics;

internal readonly record struct ZoneManagedDouble3(double X, double Y, double Z)
{
    public static ZoneManagedDouble3 From(Vector3 p) => new(p.X, p.Y, p.Z);
    public Vector3 Float => new((float)X, (float)Y, (float)Z);
    public double Squared => Dot(this, this);
    public ZoneManagedDouble3 Unit => Squared > 0 ? this * (1 / Math.Sqrt(Squared)) : default;
    public static double Dot(ZoneManagedDouble3 a, ZoneManagedDouble3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static ZoneManagedDouble3 Cross(ZoneManagedDouble3 a, ZoneManagedDouble3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public static ZoneManagedDouble3 operator +(ZoneManagedDouble3 a, ZoneManagedDouble3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static ZoneManagedDouble3 operator -(ZoneManagedDouble3 a, ZoneManagedDouble3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static ZoneManagedDouble3 operator -(ZoneManagedDouble3 a) => a * -1;
    public static ZoneManagedDouble3 operator *(ZoneManagedDouble3 a, double scale) => new(a.X * scale, a.Y * scale, a.Z * scale);
}

internal readonly record struct ZoneManagedPenetrationSupport(ZoneManagedDouble3 Difference, ZoneManagedDouble3 Target);

internal static class ZoneManagedPenetrationMath
{
    public static ZoneManagedPenetrationSupport Support(ZoneManagedConvexTarget target, ZonePhysicsShape shape,
        Vector3 center, ZoneManagedDouble3 direction)
    {
        var d = direction.Unit.Float;
        var queryOffset = ZoneManagedDouble3.From(ZoneManagedConvexTarget.ShapeSupport(shape, d));
        var targetOffset = ZoneManagedDouble3.From(target.SupportOffset(-d));
        return new(queryOffset - targetOffset - (ZoneManagedDouble3.From(target.Center) - ZoneManagedDouble3.From(center)),
            ZoneManagedDouble3.From(target.Center) + targetOffset);
    }

    // Deterministic direction selection when equal-depth minimum translations are nonunique.
    public static bool Prefer(Vector3 candidate, Vector3 current) => candidate.Z > current.Z + 1e-7f ||
        MathF.Abs(candidate.Z - current.Z) <= 1e-7f && (candidate.X > current.X + 1e-7f ||
            MathF.Abs(candidate.X - current.X) <= 1e-7f && candidate.Y > current.Y);

    public static void ClosestTriangle(ZoneManagedDouble3 p, ZoneManagedDouble3 a, ZoneManagedDouble3 b, ZoneManagedDouble3 c,
        out double wa, out double wb, out double wc)
    {
        var ab = b - a; var ac = c - a; var ap = p - a;
        var d1 = ZoneManagedDouble3.Dot(ab, ap); var d2 = ZoneManagedDouble3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) { wa = 1; wb = wc = 0; return; }
        var bp = p - b; var d3 = ZoneManagedDouble3.Dot(ab, bp); var d4 = ZoneManagedDouble3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) { wb = 1; wa = wc = 0; return; }
        var vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) { wb = d1 / (d1 - d3); wa = 1 - wb; wc = 0; return; }
        var cp = p - c; var d5 = ZoneManagedDouble3.Dot(ab, cp); var d6 = ZoneManagedDouble3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) { wc = 1; wa = wb = 0; return; }
        var vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) { wc = d2 / (d2 - d6); wa = 1 - wc; wb = 0; return; }
        var va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
        { wc = (d4 - d3) / ((d4 - d3) + (d5 - d6)); wb = 1 - wc; wa = 0; return; }
        var inverse = 1 / (va + vb + vc); wb = vb * inverse; wc = vc * inverse; wa = 1 - wb - wc;
    }

    public static InvalidOperationException Failure(string reason) =>
        new($"Managed penetration query failed: {reason}; no depth or clear-space result is inferred.");
}
