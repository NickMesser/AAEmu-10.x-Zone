using System.Numerics;

namespace AAEmu.Zone.Physics;

public enum ZoneManagedPrimitiveKind { Sphere, Cylinder, Capsule }

/// <summary>
/// Immutable analytic convex proxy. Cylinder half-height measures its axis segment; capsule
/// half-height measures the segment between the hemisphere centers, excluding the radius.
/// </summary>
public sealed class ZoneManagedPrimitiveGeometry
{
    public ZoneManagedPrimitiveGeometry(ZoneManagedPrimitiveKind kind, Vector3 center, Vector3 axis,
        float radius, float halfHeight, ushort physicalMaterialId)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        ZoneManagedTerrainGeometry.ValidateFinite(center, nameof(center));
        ZoneManagedTerrainGeometry.ValidateFinite(axis, nameof(axis));
        if (!float.IsFinite(radius) || radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius));
        if (!float.IsFinite(halfHeight) || halfHeight < 0 || kind == ZoneManagedPrimitiveKind.Cylinder && halfHeight == 0 ||
            kind == ZoneManagedPrimitiveKind.Sphere && halfHeight != 0) throw new ArgumentOutOfRangeException(nameof(halfHeight));
        if (kind != ZoneManagedPrimitiveKind.Sphere && axis == Vector3.Zero) throw new ArgumentOutOfRangeException(nameof(axis));
        Kind = kind; Center = center; Radius = radius; HalfHeight = halfHeight; PhysicalMaterialId = physicalMaterialId;
        Axis = kind == ZoneManagedPrimitiveKind.Sphere ? Vector3.Zero : DVector.From(axis).Unit().Float;
        var preciseAxis = DVector.From(Axis).Unit();
        var absolute = Vector3.Abs(preciseAxis.Float);
        var extent = kind == ZoneManagedPrimitiveKind.Cylinder
            ? absolute * halfHeight + new Vector3((float)Math.Sqrt(Math.Max(0, 1 - preciseAxis.X * preciseAxis.X)),
                (float)Math.Sqrt(Math.Max(0, 1 - preciseAxis.Y * preciseAxis.Y)), (float)Math.Sqrt(Math.Max(0, 1 - preciseAxis.Z * preciseAxis.Z))) * radius
            : absolute * halfHeight + new Vector3(radius);
        var minimum = center - extent;
        var maximum = center + extent;
        Bounds = new(new(MathF.BitDecrement(minimum.X), MathF.BitDecrement(minimum.Y), MathF.BitDecrement(minimum.Z)),
            new(MathF.BitIncrement(maximum.X), MathF.BitIncrement(maximum.Y), MathF.BitIncrement(maximum.Z)));
        ZoneManagedTerrainGeometry.ValidateFinite(Bounds.Minimum, nameof(radius));
        ZoneManagedTerrainGeometry.ValidateFinite(Bounds.Maximum, nameof(radius));
    }

    public ZoneManagedPrimitiveKind Kind { get; }
    public Vector3 Center { get; }
    public Vector3 Axis { get; }
    public float Radius { get; }
    public float HalfHeight { get; }
    public ushort PhysicalMaterialId { get; }
    internal ZoneManagedBounds Bounds { get; }

    internal Vector3 SupportOffset(Vector3 direction)
    {
        var d = DVector.From(direction);
        var axis = DVector.From(Axis).Unit();
        var axial = DVector.Dot(d, axis);
        var end = Kind == ZoneManagedPrimitiveKind.Sphere ? DVector.Zero : axis * (axial >= 0 ? HalfHeight : -HalfHeight);
        var radial = Kind == ZoneManagedPrimitiveKind.Cylinder ? d - axis * axial : d;
        return (end + radial.Unit() * Radius).Float;
    }

    internal Vector3 OutwardNormal(Vector3 point)
    {
        var relative = DVector.From(point) - DVector.From(Center);
        if (Kind == ZoneManagedPrimitiveKind.Sphere) return relative.Unit().Float;
        var axis = DVector.From(Axis).Unit();
        var z = DVector.Dot(relative, axis);
        if (Kind == ZoneManagedPrimitiveKind.Capsule)
            return (relative - axis * Math.Clamp(z, -HalfHeight, HalfHeight)).Unit().Float;
        var radial = relative - axis * z;
        return HalfHeight - Math.Abs(z) <= Radius - Math.Sqrt(radial.LengthSquared)
            ? (axis * (z >= 0 ? 1 : -1)).Float : radial.Unit().Float;
    }

    internal bool RaycastLocal(Vector3 origin, Vector3 displacement, out double fraction, out Vector3 position, out Vector3 normal,
        Matrix4x4 normalTransform, float minimumNormalZ, float maximumNormalZ)
    {
        var from = DVector.From(origin) - DVector.From(Center);
        var direction = DVector.From(displacement);
        var axis = DVector.From(Axis).Unit();
        var best = double.PositiveInfinity;
        var bestNormal = DVector.Zero;
        if (Kind == ZoneManagedPrimitiveKind.Sphere)
            Sphere(from, direction, DVector.Zero, 0);
        else
        {
            var z = DVector.Dot(from, axis);
            var speed = DVector.Dot(direction, axis);
            var radial = from - axis * z;
            var radialSpeed = direction - axis * speed;
            Roots(radial, radialSpeed, Radius, out var first, out var last);
            Side(first); Side(last);
            if (Kind == ZoneManagedPrimitiveKind.Cylinder)
            {
                if (speed != 0) { Cap((-HalfHeight - z) / speed, -1); Cap((HalfHeight - z) / speed, 1); }
            }
            else
            {
                Sphere(from, direction, axis * HalfHeight, 1);
                Sphere(from, direction, axis * -HalfHeight, -1);
            }

            void Side(double t)
            {
                if (!Within(t) || Math.Abs(z + speed * t) > HalfHeight + 1e-7) return;
                Accept(t, (radial + radialSpeed * t).Unit());
            }
            void Cap(double t, int sign)
            {
                if (!Within(t) || (radial + radialSpeed * t).LengthSquared > (double)Radius * Radius + 1e-8) return;
                Accept(t, axis * sign);
            }
        }
        fraction = best;
        position = double.IsFinite(best) ? (DVector.From(origin) + direction * best).Float : default;
        normal = bestNormal.Float;
        return double.IsFinite(best);

        bool Within(double t) => double.IsFinite(t) && t >= 0 && t <= 1 && t < best;
        void Accept(double t, DVector n)
        {
            if (!Within(t)) return;
            if (minimumNormalZ > -1 || maximumNormalZ < 1)
            {
                var worldNormal = Vector3.Normalize(Vector3.TransformNormal(n.Float, normalTransform));
                if (worldNormal.Z < minimumNormalZ || worldNormal.Z > maximumNormalZ) return;
            }
            best = t; bestNormal = n;
        }
        void Sphere(DVector start, DVector move, DVector center, int hemisphere)
        {
            Roots(start - center, move, Radius, out var first, out var last);
            Check(first); Check(last);
            void Check(double t)
            {
                if (!Within(t)) return;
                var point = start + move * t;
                var axial = DVector.Dot(point, axis);
                if (hemisphere > 0 && axial < HalfHeight - 1e-7 || hemisphere < 0 && axial > -HalfHeight + 1e-7) return;
                Accept(t, (point - center).Unit());
            }
        }
    }

    // Closest-point form avoids subtracting nearly equal quadratic coefficients on long rays.
    private static void Roots(DVector origin, DVector displacement, double radius, out double first, out double last)
    {
        first = last = double.NaN;
        var lengthSquared = displacement.LengthSquared;
        if (lengthSquared == 0) return;
        var middle = -DVector.Dot(origin, displacement) / lengthSquared;
        var closest = origin + displacement * middle;
        var clearance = radius * radius - closest.LengthSquared;
        if (clearance < -1e-12 * Math.Max(1, radius * radius)) return;
        var half = Math.Sqrt(Math.Max(0, clearance) / lengthSquared);
        first = middle - half; last = middle + half;
    }

    private readonly record struct DVector(double X, double Y, double Z)
    {
        public static DVector Zero => default;
        public static DVector From(Vector3 value) => new(value.X, value.Y, value.Z);
        public Vector3 Float => new((float)X, (float)Y, (float)Z);
        public double LengthSquared => Dot(this, this);
        public DVector Unit() => LengthSquared > 0 ? this * (1 / Math.Sqrt(LengthSquared)) : Zero;
        public static double Dot(DVector a, DVector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static DVector operator +(DVector a, DVector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static DVector operator -(DVector a, DVector b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static DVector operator *(DVector a, double scale) => new(a.X * scale, a.Y * scale, a.Z * scale);
    }
}
