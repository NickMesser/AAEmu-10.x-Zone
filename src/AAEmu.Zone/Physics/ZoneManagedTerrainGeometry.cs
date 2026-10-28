using System.Numerics;

using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Physics;

/// <summary>
/// Collision geometry over the currently loaded authored heightmap samples. Rays intersect the
/// same two triangles as the heightfield, including negative and zero heights. Missing cells and
/// surface-slot-31 holes have no geometry. This class neither loads nor calls an engine module.
/// </summary>
/// <remarks>
/// Shape positions are centers. Box sweeps use continuous triangle separating axes; overlaps
/// include the solid below the terrain. Upright capsules conservatively use their bounding boxes.
/// This provider contains terrain only: authored static meshes, caves, vegetation, vehicles and
/// water collision need separate geometry providers and are not implied by a terrain miss.
/// </remarks>
public sealed class ZoneManagedTerrainGeometry(
    ZoneTerrainHeightField terrain, ZoneTerrainSurfaceBindings? surfaces = null)
    : ZoneOwnedPlayerPhysics.IGroundQuery
{
    private const float Epsilon = 0.00001f;
    private readonly ZoneTerrainHeightField _terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));

    public bool CapsulesUseBoundingBoxes => true;
    public bool HasStaticMeshCollision => false;

    /// <summary>Nearest intersection along a bounded displacement, with the authored upward normal.</summary>
    public bool RaycastTerrain(Vector3 origin, Vector3 displacement, out ZonePhysicsHit hit)
    {
        ValidateSegment(origin, displacement);
        hit = default;
        if (displacement == Vector3.Zero) return false;
        var begin = 0f;
        var end = 1f;
        if (!ClipAxis(origin.X, displacement.X, 0, 8192, ref begin, ref end) ||
            !ClipAxis(origin.Y, displacement.Y, 0, 8192, ref begin, ref end)) return false;

        var start = origin + displacement * begin;
        var gridX = Math.Clamp((int)MathF.Floor(start.X * .5f), 0, 4095);
        var gridY = Math.Clamp((int)MathF.Floor(start.Y * .5f), 0, 4095);
        var stepX = Math.Sign(displacement.X);
        var stepY = Math.Sign(displacement.Y);
        var nextX = NextBoundary(origin.X, displacement.X, gridX, stepX);
        var nextY = NextBoundary(origin.Y, displacement.Y, gridY, stepY);
        var deltaX = stepX == 0 ? float.PositiveInfinity : 2f / MathF.Abs(displacement.X);
        var deltaY = stepY == 0 ? float.PositiveInfinity : 2f / MathF.Abs(displacement.Y);
        var cellBegin = begin;
        var length = displacement.Length();
        while ((uint)gridX < 4096 && (uint)gridY < 4096 && cellBegin <= end + Epsilon)
        {
            var cellEnd = MathF.Min(end, MathF.Min(nextX, nextY));
            if (_terrain.TryReadManagedPhysicsQuad(gridX, gridY, out var heights, out var surface))
            {
                CreateTriangles(gridX, gridY, heights, out var a, out var b, out var c, out var d);
                var best = float.PositiveInfinity;
                var normal = Vector3.Zero;
                if (RayTriangle(origin, displacement, a, b, c, out var t, out var n) &&
                    t >= cellBegin - Epsilon && t <= cellEnd + Epsilon)
                {
                    best = t;
                    normal = n;
                }
                if (RayTriangle(origin, displacement, b, d, c, out t, out n) &&
                    t >= cellBegin - Epsilon && t <= cellEnd + Epsilon && t < best)
                {
                    best = t;
                    normal = n;
                }
                if (float.IsFinite(best))
                {
                    best = Math.Clamp(best, 0, 1);
                    hit = new(best * length, origin + displacement * best, normal, ResolveMaterial(surface));
                    return true;
                }
            }
            if (cellEnd >= end) break;
            // Advance both coordinates at a grid corner, without a zero-length DDA iteration.
            if (nextX <= cellEnd + Epsilon) { gridX += stepX; nextX += deltaX; }
            if (nextY <= cellEnd + Epsilon) { gridY += stepY; nextY += deltaY; }
            cellBegin = cellEnd;
        }
        return false;
    }

    public bool TryGround(Vector3 origin, float maxDistance, out ZonePhysicsGroundHit ground)
    {
        if (!float.IsFinite(maxDistance) || maxDistance <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxDistance), "Ground distance must be finite and positive.");
        if (RaycastTerrain(origin, new Vector3(0, 0, -maxDistance), out var hit))
        {
            ground = new(hit.Distance, hit.Position, hit.Normal, hit.PhysicalMaterialId);
            return true;
        }
        ground = default;
        return false;
    }

    public bool TryGround(Vector3 from, Vector3 to, out Vector3 position, out ushort physicalMaterialId)
    {
        ValidateFinite(to, nameof(to));
        if (RaycastTerrain(from, to - from, out var hit))
        {
            position = hit.Position;
            physicalMaterialId = hit.PhysicalMaterialId;
            return true;
        }
        position = default;
        physicalMaterialId = 0;
        return false;
    }

    /// <summary>
    /// Continuous axis-aligned-box sweep against loaded terrain triangles. Capsule shapes use
    /// their upright bounding boxes, so a reported contact can precede the rounded capsule.
    /// </summary>
    public bool TrySweep(ZonePhysicsShape shape, Vector3 origin, Vector3 displacement, out ZonePhysicsHit hit)
        => TrySweepCore(shape, origin, displacement, float.PositiveInfinity, out hit);

    internal bool TrySweepSteepTerrain(ZonePhysicsShape shape, Vector3 origin, Vector3 displacement,
        float minimumWalkableNormalZ, out ZonePhysicsHit hit)
        => TrySweepCore(shape, origin, displacement, minimumWalkableNormalZ, out hit);

    private bool TrySweepCore(ZonePhysicsShape shape, Vector3 origin, Vector3 displacement,
        float maximumNormalZ, out ZonePhysicsHit hit)
    {
        ValidateShape(shape);
        ValidateSegment(origin, displacement);
        hit = default;
        if (displacement == Vector3.Zero) return false;
        var nearest = float.PositiveInfinity;
        var bestPosition = Vector3.Zero;
        var bestNormal = Vector3.Zero;
        var bestSurface = 0;
        foreach (var (x, y) in SweepCells(origin, displacement, shape.HalfExtents))
        {
            if (!_terrain.TryReadManagedPhysicsQuad(x, y, out var heights, out var surface)) continue;
            CreateTriangles(x, y, heights, out var a, out var b, out var c, out var d);
            Check(a, b, c, surface);
            Check(b, d, c, surface);
        }
        if (!float.IsFinite(nearest)) return false;
        hit = new(nearest * displacement.Length(), bestPosition, bestNormal, ResolveMaterial(bestSurface));
        return true;

        void Check(Vector3 a, Vector3 b, Vector3 c, int surface)
        {
            if (Vector3.Normalize(Vector3.Cross(b - a, c - a)).Z >= maximumNormalZ) return;
            if (!SweepTriangle(shape.HalfExtents, origin, displacement, a, b, c, out var fraction, out var normal) ||
                fraction >= nearest) return;
            nearest = fraction;
            bestNormal = normal;
            bestSurface = surface;
            var center = origin + displacement * fraction;
            var radius = Vector3.Dot(Vector3.Abs(normal), shape.HalfExtents);
            bestPosition = ClosestPointOnTriangle(center - normal * radius, a, b, c);
        }
    }

    public bool TryCollide(ZonePhysicsShape shape, Vector3 position)
    {
        ValidateShape(shape);
        ValidateFinite(position, nameof(position));
        foreach (var (x, y) in SweepCells(position, Vector3.Zero, shape.HalfExtents))
        {
            if (!_terrain.TryReadManagedPhysicsQuad(x, y, out var heights, out _)) continue;
            CreateTriangles(x, y, heights, out var a, out var b, out var c, out var d);
            if (OverlapsSolidBelow(position, shape.HalfExtents, a, b, c) ||
                OverlapsSolidBelow(position, shape.HalfExtents, b, d, c)) return true;
        }
        return false;
    }

    private ushort ResolveMaterial(int index)
    {
        if (surfaces is null) return 0; // The authored unbound/default physical-material mapping.
        var cell = index / 32;
        var material = surfaces.GetBinding(cell / 8, cell % 8, index % 32).Material;
        if (material is null) return 0;
        if (material.SubMaterialCount > 2)
            material = material.GetSubMaterial(2) ?? throw new InvalidOperationException("Terrain material lost collision submaterial 2.");
        if ((uint)material.PhysicalSurfaceId > ushort.MaxValue)
            throw new InvalidDataException($"Terrain physical surface {material.PhysicalSurfaceId} cannot fit a collision material id.");
        return (ushort)material.PhysicalSurfaceId;
    }

    private static void CreateTriangles(int x, int y, Vector4 h,
        out Vector3 a, out Vector3 b, out Vector3 c, out Vector3 d)
    {
        a = new(x * 2, y * 2, h.X);
        b = new(x * 2 + 2, y * 2, h.Y);
        c = new(x * 2, y * 2 + 2, h.Z);
        d = new(x * 2 + 2, y * 2 + 2, h.W);
    }

    private static bool RayTriangle(Vector3 origin, Vector3 displacement, Vector3 a, Vector3 b, Vector3 c,
        out float fraction, out Vector3 normal)
    {
        fraction = 0;
        var edge1 = b - a;
        var edge2 = c - a;
        normal = Vector3.Normalize(Vector3.Cross(edge1, edge2));
        var p = Vector3.Cross(displacement, edge2);
        var determinant = Vector3.Dot(edge1, p);
        if (MathF.Abs(determinant) < 1e-8f) return false;
        var inverse = 1f / determinant;
        var t = origin - a;
        var u = Vector3.Dot(t, p) * inverse;
        if (u < -Epsilon || u > 1 + Epsilon) return false;
        var q = Vector3.Cross(t, edge1);
        var v = Vector3.Dot(displacement, q) * inverse;
        if (v < -Epsilon || u + v > 1 + Epsilon) return false;
        fraction = Vector3.Dot(edge2, q) * inverse;
        return fraction >= -Epsilon && fraction <= 1 + Epsilon;
    }

    private static bool SweepTriangle(Vector3 extents, Vector3 origin, Vector3 displacement,
        Vector3 a, Vector3 b, Vector3 c, out float fraction, out Vector3 normal)
    {
        var triangleNormal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        fraction = 0;
        normal = triangleNormal;
        // The heightfield is solid below its upward face. Tangential travel across an internal
        // triangle edge is not a wall, and travel out of the solid is not a blocking contact.
        if (Vector3.Dot(displacement, triangleNormal) >= -Epsilon) return false;
        if (OverlapsSolidBelow(origin, extents, a, b, c))
            return true;

        Span<Vector3> axes = stackalloc Vector3[13];
        axes[0] = Vector3.UnitX;
        axes[1] = Vector3.UnitY;
        axes[2] = Vector3.UnitZ;
        axes[3] = triangleNormal;
        ReadOnlySpan<Vector3> edges = [b - a, c - b, a - c];
        for (var i = 0; i < 3; i++)
        {
            axes[4 + i * 3] = Vector3.Cross(edges[i], Vector3.UnitX);
            axes[5 + i * 3] = Vector3.Cross(edges[i], Vector3.UnitY);
            axes[6 + i * 3] = Vector3.Cross(edges[i], Vector3.UnitZ);
        }
        var enter = 0f;
        var exit = 1f;
        foreach (var axisValue in axes)
        {
            if (axisValue.LengthSquared() < 1e-12f) continue;
            var axis = Vector3.Normalize(axisValue);
            var p0 = Vector3.Dot(a - origin, axis);
            var p1 = Vector3.Dot(b - origin, axis);
            var p2 = Vector3.Dot(c - origin, axis);
            var radius = Vector3.Dot(Vector3.Abs(axis), extents);
            var minimum = MathF.Min(p0, MathF.Min(p1, p2)) - radius;
            var maximum = MathF.Max(p0, MathF.Max(p1, p2)) + radius;
            var speed = Vector3.Dot(displacement, axis);
            if (MathF.Abs(speed) < 1e-8f)
            {
                if (minimum > Epsilon || maximum < -Epsilon) return false;
                continue;
            }
            var first = minimum / speed;
            var last = maximum / speed;
            var enteringNormal = -axis;
            if (first > last) { (first, last) = (last, first); enteringNormal = axis; }
            if (first > enter) { enter = first; normal = enteringNormal; }
            exit = MathF.Min(exit, last);
            if (enter > exit + Epsilon) return false;
        }
        if (enter < 0 || enter > 1 || exit < 0) return false;
        fraction = enter;
        return Vector3.Dot(displacement, normal) < -Epsilon;
    }

    // Clip the triangle to the box's XY footprint. Its highest remaining point tells whether
    // any part of the box enters the solid terrain below, even when the entire box is underground.
    private static bool OverlapsSolidBelow(Vector3 center, Vector3 extents, Vector3 a, Vector3 b, Vector3 c)
    {
        Span<Vector3> polygon = stackalloc Vector3[8];
        Span<Vector3> scratch = stackalloc Vector3[8];
        polygon[0] = a; polygon[1] = b; polygon[2] = c;
        var count = 3;
        for (var edge = 0; edge < 4; edge++)
        {
            var axis = edge / 2;
            var lower = edge % 2 == 0;
            var boundary = Component(center, axis) + (lower ? -1 : 1) * Component(extents, axis);
            var resultCount = 0;
            for (var i = 0; i < count; i++)
            {
                var previous = polygon[(i + count - 1) % count];
                var current = polygon[i];
                var previousDistance = Component(previous, axis) - boundary;
                var currentDistance = Component(current, axis) - boundary;
                var previousInside = lower ? previousDistance >= 0 : previousDistance <= 0;
                var currentInside = lower ? currentDistance >= 0 : currentDistance <= 0;
                if (previousInside != currentInside)
                    scratch[resultCount++] = Vector3.Lerp(previous, current, previousDistance / (previousDistance - currentDistance));
                if (currentInside) scratch[resultCount++] = current;
            }
            if (resultCount == 0) return false;
            scratch[..resultCount].CopyTo(polygon);
            count = resultCount;
        }
        var bottom = center.Z - extents.Z;
        for (var i = 0; i < count; i++)
            if (bottom <= polygon[i].Z + Epsilon) return true;
        return false;
    }

    private static Vector3 ClosestPointOnTriangle(Vector3 point, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a; var ac = c - a; var ap = point - a;
        var d1 = Vector3.Dot(ab, ap); var d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        var bp = point - b;
        var d3 = Vector3.Dot(ab, bp); var d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;
        var vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
        var cp = point - c;
        var d5 = Vector3.Dot(ab, cp); var d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;
        var vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
        var va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
            return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
        var inverse = 1 / (va + vb + vc);
        return a + ab * (vb * inverse) + ac * (vc * inverse);
    }

    private static IEnumerable<(int X, int Y)> SweepCells(Vector3 origin, Vector3 displacement, Vector3 extents)
    {
        var begin = 0f;
        var end = 1f;
        if (!ClipAxis(origin.X, displacement.X, -extents.X, 8192 + extents.X, ref begin, ref end) ||
            !ClipAxis(origin.Y, displacement.Y, -extents.Y, 8192 + extents.Y, ref begin, ref end)) yield break;
        var first = origin + displacement * begin;
        var last = origin + displacement * end;
        var steps = (int)Math.Clamp(Math.Ceiling(Math.Max(Math.Abs((double)last.X - first.X), Math.Abs((double)last.Y - first.Y)) / 2), 1, 8194);
        var visited = new HashSet<int>();
        var previous = first;
        for (var step = 1; step <= steps; step++)
        {
            var current = Vector3.Lerp(first, last, (float)step / steps);
            var minimum = Vector3.Min(previous, current) - extents;
            var maximum = Vector3.Max(previous, current) + extents;
            var minX = GridIndex(minimum.X); var maxX = GridIndex(maximum.X);
            var minY = GridIndex(minimum.Y); var maxY = GridIndex(maximum.Y);
            for (var x = minX; x <= maxX; x++)
                for (var y = minY; y <= maxY; y++)
                    if (visited.Add(x * 4096 + y)) yield return (x, y);
            previous = current;
        }
    }

    private static int GridIndex(float value) => (int)Math.Clamp(MathF.Floor(value * .5f), 0, 4095);
    private static float Component(Vector3 vector, int axis) => axis == 0 ? vector.X : vector.Y;
    private static float NextBoundary(float origin, float displacement, int cell, int step) => step == 0
        ? float.PositiveInfinity : ((cell + (step > 0 ? 1 : 0)) * 2 - origin) / displacement;

    private static bool ClipAxis(float origin, float displacement, float minimum, float maximum, ref float begin, ref float end)
    {
        if (displacement == 0) return origin >= minimum && origin <= maximum;
        var first = (minimum - origin) / displacement;
        var last = (maximum - origin) / displacement;
        if (first > last) (first, last) = (last, first);
        begin = MathF.Max(begin, first);
        end = MathF.Min(end, last);
        return begin <= end;
    }

    internal static void ValidateShape(ZonePhysicsShape shape)
    {
        ValidateFinite(shape.HalfExtents, nameof(shape));
        if (shape.HalfExtents.X <= 0 || shape.HalfExtents.Y <= 0 || shape.HalfExtents.Z <= 0)
            throw new ArgumentOutOfRangeException(nameof(shape), "Collision half extents must be positive.");
    }

    internal static void ValidateSegment(Vector3 origin, Vector3 displacement)
    {
        ValidateFinite(origin, nameof(origin));
        ValidateFinite(displacement, nameof(displacement));
        ValidateFinite(origin + displacement, nameof(displacement));
        if (!float.IsFinite(displacement.LengthSquared()))
            throw new ArgumentOutOfRangeException(nameof(displacement), "Collision displacement must have a finite length.");
    }

    internal static void ValidateFinite(Vector3 value, string parameterName)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            throw new ArgumentOutOfRangeException(parameterName, "Collision inputs must be finite.");
    }
}
