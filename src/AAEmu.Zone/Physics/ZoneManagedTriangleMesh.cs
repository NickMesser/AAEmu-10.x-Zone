using System.Numerics;

namespace AAEmu.Zone.Physics;

/// <summary>
/// An immutable collision resource shared by scene instances. Vertex/index/material inputs are
/// copied once, and one balanced local-space BVH is built for the resource, never per instance.
/// Degenerate authored triangles retain their indices/materials but do not produce contacts.
/// </summary>
public sealed class ZoneManagedTriangleMesh
{
    private readonly Vector3[] _vertices;
    private readonly int[] _indices;
    private readonly ushort[] _materials;
    private readonly ZoneManagedBounds[] _triangleBounds;
    private readonly int[] _triangleOrder;
    private readonly Node[] _nodes;
    private readonly SolidPlane[] _solidPlanes;

    public ZoneManagedTriangleMesh(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> triangleIndices,
        ReadOnlySpan<ushort> perTriangleMaterials = default, bool solidConvex = false)
    {
        if (vertices.IsEmpty) throw new ArgumentException("A collision mesh requires vertices.", nameof(vertices));
        if (triangleIndices.IsEmpty || triangleIndices.Length % 3 != 0)
            throw new ArgumentException("A collision mesh requires complete indexed triangles.", nameof(triangleIndices));
        var triangleCount = triangleIndices.Length / 3;
        if (!perTriangleMaterials.IsEmpty && perTriangleMaterials.Length != triangleCount)
            throw new ArgumentException("Supply one physical material id per triangle, or omit the material array.", nameof(perTriangleMaterials));
        foreach (var vertex in vertices) ZoneManagedTerrainGeometry.ValidateFinite(vertex, nameof(vertices));
        foreach (var index in triangleIndices)
            if ((uint)index >= (uint)vertices.Length)
                throw new ArgumentOutOfRangeException(nameof(triangleIndices), "A triangle index is outside the vertex array.");
        _vertices = vertices.ToArray();
        _indices = triangleIndices.ToArray();
        _materials = perTriangleMaterials.IsEmpty ? new ushort[triangleCount] : perTriangleMaterials.ToArray();
        _triangleBounds = new ZoneManagedBounds[triangleCount];
        _triangleOrder = new int[triangleCount];
        var centroids = new Vector3[triangleCount];
        for (var i = 0; i < triangleCount; i++)
        {
            GetTriangle(i, out var a, out var b, out var c, out _);
            _triangleBounds[i] = ZoneManagedBounds.FromTriangle(a, b, c);
            _triangleOrder[i] = i;
            centroids[i] = a / 3 + b / 3 + c / 3;
        }
        var nodes = new List<Node>(triangleCount);
        Build(0, triangleCount);
        _nodes = nodes.ToArray();
        BoundsMinimum = _nodes[0].Bounds.Minimum;
        BoundsMaximum = _nodes[0].Bounds.Maximum;
        SolidConvex = solidConvex;
        _solidPlanes = solidConvex ? BuildSolidPlanes() : [];

        int Build(int first, int count)
        {
            var bounds = _triangleBounds[_triangleOrder[first]];
            var centroidMinimum = centroids[_triangleOrder[first]];
            var centroidMaximum = centroidMinimum;
            for (var i = first + 1; i < first + count; i++)
            {
                bounds = ZoneManagedBounds.Union(bounds, _triangleBounds[_triangleOrder[i]]);
                centroidMinimum = Vector3.Min(centroidMinimum, centroids[_triangleOrder[i]]);
                centroidMaximum = Vector3.Max(centroidMaximum, centroids[_triangleOrder[i]]);
            }
            var nodeIndex = nodes.Count;
            nodes.Add(default);
            var size = centroidMaximum - centroidMinimum;
            var axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
            if (count <= 8 || ZoneManagedTriangleQueries.Component(size, axis) <= 1e-8f)
            {
                nodes[nodeIndex] = new(bounds, -1, -1, first, count);
                return nodeIndex;
            }
            Array.Sort(_triangleOrder, first, count, Comparer<int>.Create((left, right) =>
                ZoneManagedTriangleQueries.Component(centroids[left], axis).CompareTo(ZoneManagedTriangleQueries.Component(centroids[right], axis))));
            var leftCount = count / 2;
            var leftNode = Build(first, leftCount);
            var rightNode = Build(first + leftCount, count - leftCount);
            nodes[nodeIndex] = new(bounds, leftNode, rightNode, 0, 0);
            return nodeIndex;
        }
    }

    public int VertexCount => _vertices.Length;
    public int TriangleCount => _materials.Length;
    public int BvhNodeCount => _nodes.Length;
    /// <summary>True only when the caller explicitly admits this closed convex proxy as a solid.</summary>
    public bool SolidConvex { get; }
    public Vector3 BoundsMinimum { get; }
    public Vector3 BoundsMaximum { get; }

    internal bool ContainsSolidPoint(Vector3 point)
    {
        if (!SolidConvex) return false;
        foreach (var plane in _solidPlanes)
            if (plane.Distance(point.X, point.Y, point.Z) > plane.Tolerance) return false;
        return true;
    }

    private SolidPlane[] BuildSolidPlanes()
    {
        // A convex combination of referenced vertices is inside a valid convex mesh, independent
        // of the authored face winding and unused vertices outside the collision resource.
        var centerX = 0d; var centerY = 0d; var centerZ = 0d;
        foreach (var index in _indices)
        {
            centerX += _vertices[index].X;
            centerY += _vertices[index].Y;
            centerZ += _vertices[index].Z;
        }
        centerX /= _indices.Length; centerY /= _indices.Length; centerZ /= _indices.Length;
        var extentX = (double)BoundsMaximum.X - BoundsMinimum.X;
        var extentY = (double)BoundsMaximum.Y - BoundsMinimum.Y;
        var extentZ = (double)BoundsMaximum.Z - BoundsMinimum.Z;
        var tolerance = Math.Max(1e-8, Math.Max(extentX, Math.Max(extentY, extentZ)) * 2e-7);
        var planes = new List<SolidPlane>();
        var positionIds = new Dictionary<Vector3, int>();
        var edgeUses = new Dictionary<(int First, int Second), int>();
        for (var triangle = 0; triangle < TriangleCount; triangle++)
        {
            GetTriangle(triangle, out var a, out var b, out var c, out _);
            var abX = (double)b.X - a.X; var abY = (double)b.Y - a.Y; var abZ = (double)b.Z - a.Z;
            var acX = (double)c.X - a.X; var acY = (double)c.Y - a.Y; var acZ = (double)c.Z - a.Z;
            var x = abY * acZ - abZ * acY;
            var y = abZ * acX - abX * acZ;
            var z = abX * acY - abY * acX;
            var length = Math.Sqrt(x * x + y * y + z * z);
            if (length == 0) continue;
            x /= length; y /= length; z /= length;
            var centerDistance = x * (centerX - a.X) + y * (centerY - a.Y) + z * (centerZ - a.Z);
            if (centerDistance == 0) throw InvalidSolid();
            if (centerDistance > 0) { x = -x; y = -y; z = -z; }
            var plane = new SolidPlane(x, y, z, a.X, a.Y, a.Z, tolerance);
            foreach (var index in _indices)
            {
                var vertex = _vertices[index];
                if (plane.Distance(vertex.X, vertex.Y, vertex.Z) > tolerance) throw InvalidSolid();
            }
            planes.Add(plane);
            var first = PositionId(a); var second = PositionId(b); var third = PositionId(c);
            AddEdge(first, second); AddEdge(second, third); AddEdge(third, first);
        }
        // Exact duplicate positions are welded for this topology check. This admits box proxies
        // with separate per-face vertices without giving an open or flat mesh invented volume.
        if (planes.Count < 4 || edgeUses.Values.Any(count => count != 2)) throw InvalidSolid();
        return planes.ToArray();

        int PositionId(Vector3 position)
        {
            if (positionIds.TryGetValue(position, out var id)) return id;
            id = positionIds.Count;
            positionIds.Add(position, id);
            return id;
        }
        void AddEdge(int first, int second)
        {
            var key = first < second ? (first, second) : (second, first);
            edgeUses.TryGetValue(key, out var count);
            edgeUses[key] = count + 1;
        }
        static ArgumentException InvalidSolid() => new("Solid collision proxies require a closed, volumetric convex mesh.", "solidConvex");
    }

    /// <summary>Returns copied values; no writable view of the shared resource is exposed.</summary>
    public void GetTriangle(int triangleIndex, out Vector3 a, out Vector3 b, out Vector3 c, out ushort physicalMaterialId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(triangleIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(triangleIndex, _materials.Length);
        a = _vertices[_indices[triangleIndex * 3]];
        b = _vertices[_indices[triangleIndex * 3 + 1]];
        c = _vertices[_indices[triangleIndex * 3 + 2]];
        physicalMaterialId = _materials[triangleIndex];
    }

    internal void QueryBounds(ZoneManagedBounds bounds, List<int> triangles)
    {
        triangles.Clear();
        Span<int> stack = stackalloc int[64]; // Median splits give depth < 32 for an int-indexed mesh.
        var count = 1;
        stack[0] = 0;
        while (count > 0)
        {
            var node = _nodes[stack[--count]];
            if (!node.Bounds.Overlaps(bounds)) continue;
            if (node.Count == 0)
            {
                stack[count++] = node.Left;
                stack[count++] = node.Right;
                continue;
            }
            for (var i = node.First; i < node.First + node.Count; i++)
            {
                var triangle = _triangleOrder[i];
                if (_triangleBounds[triangle].Overlaps(bounds)) triangles.Add(triangle);
            }
        }
    }

    internal void QueryRay(Vector3 origin, Vector3 displacement, List<int> triangles)
    {
        triangles.Clear();
        Span<int> stack = stackalloc int[64];
        var count = 1;
        stack[0] = 0;
        while (count > 0)
        {
            var node = _nodes[stack[--count]];
            if (!node.Bounds.IntersectsSegment(origin, displacement)) continue;
            if (node.Count == 0)
            {
                stack[count++] = node.Left;
                stack[count++] = node.Right;
                continue;
            }
            for (var i = node.First; i < node.First + node.Count; i++)
            {
                var triangle = _triangleOrder[i];
                if (_triangleBounds[triangle].IntersectsSegment(origin, displacement)) triangles.Add(triangle);
            }
        }
    }

    private readonly record struct Node(ZoneManagedBounds Bounds, int Left, int Right, int First, int Count);
    private readonly record struct SolidPlane(double X, double Y, double Z, double PointX, double PointY, double PointZ, double Tolerance)
    {
        public double Distance(double x, double y, double z) => X * (x - PointX) + Y * (y - PointY) + Z * (z - PointZ);
    }
}
