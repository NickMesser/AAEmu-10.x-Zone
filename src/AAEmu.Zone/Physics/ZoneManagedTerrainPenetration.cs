using System.Numerics;
using D = AAEmu.Zone.Physics.ZoneManagedDouble3;

namespace AAEmu.Zone.Physics;

/// <summary>
/// Exact upward unprojection against an authored terrain triangle. Maximizes terrain height
/// minus the true shape's lower surface over their XY intersection, using analytic extrema.
/// It is a selected terrain support constraint, not an invented internal-prism sidewall MTD.
/// </summary>
internal static class ZoneManagedTerrainPenetration
{
    public static bool Try(ZonePhysicsShape shape, Vector3 center, Vector3 worldA, Vector3 worldB, Vector3 worldC,
        ushort material, out ZonePhysicsPenetration penetration)
    {
        penetration = default;
        var a = D.From(worldA) - D.From(center); var b = D.From(worldB) - D.From(center); var c = D.From(worldC) - D.From(center);
        var cross = D.Cross(b - a, c - a);
        if (cross.Z <= 0) throw ZoneManagedPenetrationMath.Failure("terrain has no upward heightfield face");
        var outer = (double)shape.BoundingHalfExtents.Z;
        if (Math.Min(a.Z, Math.Min(b.Z, c.Z)) >= outer || Math.Max(a.Z, Math.Max(b.Z, c.Z)) <= -outer) return false;
        // Original heightfield non-ray recovery delegates to its authored top triangles. A
        // wholly submerged body does not create an early overlap contact with that surface.
        if (!ZoneManagedPenetrationQueries.Try(ZoneManagedConvexTarget.ForTriangle(worldA, worldB, worldC),
            shape, center, material, true, out _)) return false;
        var gx = -cross.X / cross.Z; var gy = -cross.Y / cross.Z;
        var baseHeight = a.Z - gx * a.X - gy * a.Y;
        var maximum = double.NegativeInfinity;
        var contact = D.From(worldA);
        if (shape.Kind == ZonePhysicsShapeKind.Box)
        {
            Span<D> polygon = stackalloc D[8]; Span<D> scratch = stackalloc D[8];
            polygon[0] = a; polygon[1] = b; polygon[2] = c; var count = 3;
            for (var side = 0; side < 4; side++)
            {
                var axis = side / 2; var lower = side % 2 == 0;
                var boundary = (lower ? -1 : 1) * (axis == 0 ? shape.HalfExtents.X : shape.HalfExtents.Y);
                var next = 0;
                for (var i = 0; i < count; i++)
                {
                    var p = polygon[(i + count - 1) % count]; var q = polygon[i];
                    var pd = (axis == 0 ? p.X : p.Y) - boundary; var qd = (axis == 0 ? q.X : q.Y) - boundary;
                    var pi = lower ? pd >= 0 : pd <= 0; var qi = lower ? qd >= 0 : qd <= 0;
                    if (pi != qi) scratch[next++] = p + (q - p) * (pd / (pd - qd));
                    if (qi) scratch[next++] = q;
                }
                if (next == 0) return false;
                scratch[..next].CopyTo(polygon); count = next;
            }
            var area = 0d;
            for (var i = 0; i < count; i++) area += polygon[i].X * polygon[(i + 1) % count].Y - polygon[i].Y * polygon[(i + 1) % count].X;
            if (Math.Abs(area) <= 1e-20) return false; // XY boundary touch has no penetration volume.
            for (var i = 0; i < count; i++) Take(polygon[i], polygon[i].Z + shape.HalfExtents.Z);
        }
        else
        {
            var radius = (double)shape.Radius;
            var r2 = radius * radius;
            ZoneManagedPenetrationMath.ClosestTriangle(default, new(a.X, a.Y, 0), new(b.X, b.Y, 0), new(c.X, c.Y, 0),
                out var wa, out var wb, out var wc);
            var closest = a * wa + b * wb + c * wc;
            if (closest.X * closest.X + closest.Y * closest.Y >= r2) return false;
            var length = Math.Sqrt(gx * gx + gy * gy);
            var scale = shape.UseCapsule ? radius / Math.Sqrt(1 + length * length) : length > 0 ? radius / length : 0;
            var interior = new D(gx * scale, gy * scale, baseHeight + (gx * gx + gy * gy) * scale);
            if (Inside(interior)) Evaluate(interior);
            Edge(a, b); Edge(b, c); Edge(c, a);
            // On a flat cylinder cap, any point in the nonempty footprint is an exact extremum.
            if (!shape.UseCapsule && length == 0) Evaluate(closest);

            void Edge(D first, D last)
            {
                var dx = last.X - first.X; var dy = last.Y - first.Y;
                var edgeLength = Math.Sqrt(dx * dx + dy * dy);
                if (edgeLength == 0) return;
                var middle = -(first.X * dx + first.Y * dy) / (edgeLength * edgeLength);
                var nearest = first + (last - first) * middle;
                var remainder = r2 - nearest.X * nearest.X - nearest.Y * nearest.Y;
                if (remainder < 0) return;
                var width = Math.Sqrt(Math.Max(0, remainder));
                var low = Math.Max(-middle * edgeLength, -width);
                var high = Math.Min((1 - middle) * edgeLength, width);
                if (low > high) return;
                var slope = (last.Z - first.Z) / edgeLength;
                var along = shape.UseCapsule ? width * slope / Math.Sqrt(1 + slope * slope) : slope >= 0 ? high : low;
                along = Math.Clamp(along, low, high);
                Evaluate(first + (last - first) * (middle + along / edgeLength));
            }
            void Evaluate(D point)
            {
                var sphere = shape.UseCapsule ? Math.Sqrt(Math.Max(0, r2 - point.X * point.X - point.Y * point.Y)) : 0;
                Take(point, point.Z + shape.SegmentHalfHeight + sphere);
            }
        }
        if (!double.IsFinite(maximum)) throw ZoneManagedPenetrationMath.Failure("terrain extrema have no finite candidate");
        if (maximum <= 0) return false;
        penetration = new((float)maximum, Vector3.UnitZ, contact.Float, material, true);
        return true;

        void Take(D point, double depth)
        {
            if (depth <= maximum) return;
            maximum = depth; contact = D.From(center) + point;
        }
        bool Inside(D point)
        {
            var denominator = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
            var u = ((b.Y - c.Y) * (point.X - c.X) + (c.X - b.X) * (point.Y - c.Y)) / denominator;
            var v = ((c.Y - a.Y) * (point.X - c.X) + (a.X - c.X) * (point.Y - c.Y)) / denominator;
            return u >= 0 && v >= 0 && u + v <= 1;
        }
    }
}

internal static class ZoneManagedPenetrationCollection
{
    public static IReadOnlyList<ZonePhysicsPenetration> Snapshot(List<ZonePhysicsPenetration> contacts)
    {
        if (contacts.Count == 0) return Array.Empty<ZonePhysicsPenetration>();
        contacts.Sort((a, b) =>
        {
            var result = b.Depth.CompareTo(a.Depth); if (result != 0) return result;
            result = b.Normal.Z.CompareTo(a.Normal.Z); if (result != 0) return result;
            result = b.Normal.X.CompareTo(a.Normal.X); if (result != 0) return result;
            result = b.Normal.Y.CompareTo(a.Normal.Y); if (result != 0) return result;
            result = a.PhysicalMaterialId.CompareTo(b.PhysicalMaterialId); if (result != 0) return result;
            result = a.Position.X.CompareTo(b.Position.X); if (result != 0) return result;
            result = a.Position.Y.CompareTo(b.Position.Y); return result != 0 ? result : a.Position.Z.CompareTo(b.Position.Z);
        });
        var distinct = new List<ZonePhysicsPenetration>(contacts.Count);
        foreach (var contact in contacts)
        {
            if (distinct.Any(previous => previous.Normal == contact.Normal && previous.IsSurfaceContact == contact.IsSurfaceContact &&
                previous.PhysicalMaterialId == contact.PhysicalMaterialId && MathF.Abs(previous.Depth - contact.Depth) <= ZoneManagedPenetrationQueries.Tolerance)) continue;
            distinct.Add(contact);
        }
        return Array.AsReadOnly(distinct.ToArray());
    }
}
