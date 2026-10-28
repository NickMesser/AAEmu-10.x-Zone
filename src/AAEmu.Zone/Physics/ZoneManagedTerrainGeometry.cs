using System.Numerics;

using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Physics;

/// <summary>
/// Collision geometry over the currently loaded authored heightmap samples. Rays intersect the
/// same two triangles as the heightfield, including negative and zero heights. Missing cells and
/// surface-slot-31 holes have no geometry. This class neither loads nor calls an engine module.
/// </summary>
/// <remarks>
/// Shape positions are centers. All shapes intersect the authored two-sided top triangles;
/// fully submerged bodies do not collide with an invented solid below the heightfield. Box
/// sweeps use continuous triangle separating axes. Upright capsule/cylinder queries use convex
/// support or exact capsule segment distances; their bounding boxes select candidates only.
/// This provider contains terrain only: authored static meshes, caves, vegetation, vehicles and
/// water collision need separate geometry providers and are not implied by a terrain miss.
/// </remarks>
public sealed partial class ZoneManagedTerrainGeometry(
    ZoneTerrainHeightField terrain, ZoneTerrainSurfaceBindings? surfaces = null)
    : ZoneOwnedPlayerPhysics.IGroundQuery
{
    private const float Epsilon = 0.00001f;
    private readonly ZoneTerrainHeightField _terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));

    public bool CapsulesUseBoundingBoxes => false;
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
    /// Continuous box, upright capsule or cylinder sweep against loaded two-sided terrain
    /// triangles. Curved queries use the shared one-mm separation tolerance, not an enclosing
    /// box substitute. Both downward approaches and upward crossings from below can collide.
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
        foreach (var (x, y) in SweepCells(origin, displacement, shape.BoundingHalfExtents))
        {
            if (!_terrain.TryReadManagedPhysicsQuad(x, y, out var heights, out var surface)) continue;
            CreateTriangles(x, y, heights, out var a, out var b, out var c, out var d);
            Check(a, b, c, surface);
            Check(b, d, c, surface);
        }
        if (!float.IsFinite(nearest)) return false;
        hit = new(nearest * displacement.Length(), bestPosition, bestNormal, ResolveShapeMaterial(bestSurface));
        return true;

        void Check(Vector3 a, Vector3 b, Vector3 c, int surface)
        {
            var faceNormal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            if (faceNormal.Z >= maximumNormalZ) return;
            if (!ZoneManagedTriangleQueries.Sweep(shape, origin, displacement, a, b, c, out var fraction, out var normal, out var position) ||
                fraction >= nearest) return;
            nearest = fraction;
            bestNormal = normal;
            bestSurface = surface;
            bestPosition = position;
        }
    }

    public bool TryCollide(ZonePhysicsShape shape, Vector3 position)
    {
        ValidateShape(shape);
        ValidateFinite(position, nameof(position));
        foreach (var (x, y) in SweepCells(position, Vector3.Zero, shape.BoundingHalfExtents))
        {
            if (!_terrain.TryReadManagedPhysicsQuad(x, y, out var heights, out _)) continue;
            CreateTriangles(x, y, heights, out var a, out var b, out var c, out var d);
            if (ZoneManagedTriangleQueries.Overlaps(shape, position, a, b, c) ||
                ZoneManagedTriangleQueries.Overlaps(shape, position, b, d, c)) return true;
        }
        return false;
    }

    /// <summary>
    /// Copies positive authored-face-normal recovery constraints, gated by real finite-triangle
    /// intersections. Interior face depth is exact; boundary normal/depth selection is a conservative
    /// surface policy, not a whole-terrain/edge MTD. A shorter backside exit never lowers a body
    /// below terrain. Coplanar seam constraints coalesce; internal grid edges do not become walls.
    /// Holes, unloaded cells and fully submerged nonintersecting shapes supply no contacts.
    /// </summary>
    public IReadOnlyList<ZonePhysicsPenetration> GetPenetrations(ZonePhysicsShape shape, Vector3 position)
    {
        ValidateShape(shape); ValidateFinite(position, nameof(position));
        ValidateFinite(position - shape.BoundingHalfExtents, nameof(shape));
        ValidateFinite(position + shape.BoundingHalfExtents, nameof(shape));
        var contacts = new List<ZonePhysicsPenetration>();
        foreach (var (x, y) in SweepCells(position, Vector3.Zero, shape.BoundingHalfExtents))
        {
            if (!_terrain.TryReadManagedPhysicsQuad(x, y, out var heights, out var material)) continue;
            CreateTriangles(x, y, heights, out var a, out var b, out var c, out var d);
            var physical = ResolveShapeMaterial(material);
            if (ZoneManagedTerrainNormalPenetration.Try(shape, position, a, b, c, physical, out var first)) contacts.Add(first);
            if (ZoneManagedTerrainNormalPenetration.Try(shape, position, b, d, c, physical, out var second)) contacts.Add(second);
        }
        return ZoneManagedPenetrationCollection.Snapshot(contacts);
    }

    private ushort ResolveShapeMaterial(int index)
    {
        // 35155180 stores mesh triangle IDs in bytes; 3514E580/35093100 read them as signed.
        // 35037930 substitutes the fresh terrain part's default slot 0 for a negative ID before
        // applying its 2048-entry mapping (3508B860/350212E0). Raw rays retain their full index.
        var triangleId = unchecked((sbyte)(byte)index);
        return ResolveMaterial(triangleId >= 0 ? triangleId : 0);
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
        if (!Enum.IsDefined(shape.Kind)) throw new ArgumentOutOfRangeException(nameof(shape), "Unknown collision shape kind.");
        if (shape.Kind == ZonePhysicsShapeKind.Box)
        {
            ValidateFinite(shape.HalfExtents, nameof(shape));
            if (shape.HalfExtents.X <= 0 || shape.HalfExtents.Y <= 0 || shape.HalfExtents.Z <= 0)
                throw new ArgumentOutOfRangeException(nameof(shape), "Collision box half extents must be positive.");
        }
        else if (!float.IsFinite(shape.Radius) || shape.Radius <= 0 || !float.IsFinite(shape.SegmentHalfHeight) ||
            shape.SegmentHalfHeight < 0 || shape.Kind == ZonePhysicsShapeKind.Cylinder && shape.SegmentHalfHeight == 0)
            throw new ArgumentOutOfRangeException(nameof(shape), "Curved queries require a positive radius and valid axial half-length.");
        ValidateFinite(shape.BoundingHalfExtents, nameof(shape));
    }

    /// <summary>Only the original old-kind/new-dimensions stance preflight can use a cylinder disk.</summary>
    internal static void ValidateStancePreflightShape(ZonePhysicsShape shape)
    {
        if (shape.Kind != ZonePhysicsShapeKind.Cylinder || shape.SegmentHalfHeight != 0) { ValidateShape(shape); return; }
        if (!float.IsFinite(shape.Radius) || shape.Radius <= 0)
            throw new ArgumentOutOfRangeException(nameof(shape), "A stance preflight disk requires a finite positive radius.");
        ValidateFinite(shape.BoundingHalfExtents, nameof(shape));
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
