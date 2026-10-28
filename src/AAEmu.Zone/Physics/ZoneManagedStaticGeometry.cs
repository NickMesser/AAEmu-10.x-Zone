using System.Numerics;

namespace AAEmu.Zone.Physics;

/// <summary>An instance identity that is never reused within its collision scene.</summary>
public readonly record struct ZoneManagedStaticInstanceHandle(ulong Value);

public readonly record struct ZoneManagedStaticQueryStatistics(int InstanceBoundsTests, int CandidateInstances, int TriangleTests);

/// <summary>
/// A managed collision scene of shared triangle meshes, analytic convex primitives and affine
/// instances. Spatial buckets select instances; mesh BVHs select authored triangles, while curved
/// proxies retain analytic rays and support maps. Queries account for rotation, shear and
/// nonuniform/mirrored scale without replacing curved targets with bounding boxes.
/// </summary>
/// <remarks>
/// General triangles are two-sided surfaces: space below a bridge or inside a building stays
/// empty. Explicitly flagged closed convex meshes and analytic primitives contain their interior
/// volume. Query boxes are axis aligned; capsules and cylinders retain their actual upright
/// support surfaces. Bounding extents are used only for broadphase candidate selection.
/// This supplies static collision queries, not rigid-body, vehicle, animation or breakage dynamics.
/// </remarks>
public sealed partial class ZoneManagedStaticGeometry
{
    /// <summary>
    /// World-space separation tolerance for curved convex overlap/sweep contacts, in meters,
    /// including capsule/cylinder queries against triangles and analytic targets. This is
    /// not a bound on distance traveled before a grazing contact: a small normal separation can
    /// span a longer interval along a nearly tangent trajectory. Raw ray solutions do not use it.
    /// </summary>
    public const float AnalyticContactTolerance = ZoneManagedPrimitiveQueries.ContactTolerance;
    /// <summary>Numerical depth convergence/residual threshold, excluding controller clearance.</summary>
    public const float PenetrationTolerance = ZoneManagedPenetrationQueries.Tolerance;
    private const float BucketSize = 64;
    private const int MaximumInstanceBuckets = 512;
    private const int MaximumQueryBuckets = 4096;
    private readonly object _gate = new();
    private readonly Dictionary<ZoneManagedStaticInstanceHandle, Instance> _instances = [];
    private readonly Dictionary<Bucket, HashSet<ZoneManagedStaticInstanceHandle>> _buckets = [];
    private readonly HashSet<ZoneManagedStaticInstanceHandle> _oversized = [];
    private readonly HashSet<ZoneManagedStaticInstanceHandle> _queryIds = [];
    private readonly List<Instance> _queryInstances = [];
    private readonly List<int> _queryTriangles = [];
    private ulong _nextIdentity;
    private ZoneManagedStaticQueryStatistics _lastQuery;

    public bool CapsulesUseBoundingBoxes => false;
    public int InstanceCount { get { lock (_gate) return _instances.Count; } }
    public ZoneManagedStaticQueryStatistics LastQueryStatistics { get { lock (_gate) return _lastQuery; } }

    public ZoneManagedStaticInstanceHandle AddInstance(ZoneManagedTriangleMesh mesh, Matrix4x4 localToWorld,
        ZonePhysicsQueryBounds? admissionBounds = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        return AddInstanceCore(mesh, null, localToWorld, admissionBounds);
    }

    public ZoneManagedStaticInstanceHandle AddInstance(ZoneManagedPrimitiveGeometry primitive, Matrix4x4 localToWorld,
        ZonePhysicsQueryBounds? admissionBounds = null)
    {
        ArgumentNullException.ThrowIfNull(primitive);
        return AddInstanceCore(null, primitive, localToWorld, admissionBounds);
    }

    private ZoneManagedStaticInstanceHandle AddInstanceCore(ZoneManagedTriangleMesh? mesh,
        ZoneManagedPrimitiveGeometry? primitive, Matrix4x4 localToWorld, ZonePhysicsQueryBounds? admissionBounds)
    {
        ValidateTransform(localToWorld, out var worldToLocal);
        var bounds = (mesh is not null ? new ZoneManagedBounds(mesh.BoundsMinimum, mesh.BoundsMaximum) : primitive!.Bounds).Transform(localToWorld);
        ZoneManagedTerrainGeometry.ValidateFinite(bounds.Minimum, nameof(localToWorld));
        ZoneManagedTerrainGeometry.ValidateFinite(bounds.Maximum, nameof(localToWorld));
        var admitted = admissionBounds?.Managed ?? bounds;
        if (admitted.Minimum.X > bounds.Minimum.X || admitted.Minimum.Y > bounds.Minimum.Y || admitted.Minimum.Z > bounds.Minimum.Z ||
            admitted.Maximum.X < bounds.Maximum.X || admitted.Maximum.Y < bounds.Maximum.Y || admitted.Maximum.Z < bounds.Maximum.Z)
            throw new ArgumentException("Entity admission bounds must contain the part's collision bounds.", nameof(admissionBounds));
        var (minimum, maximum, bucketCount) = BucketRange(bounds);
        var keys = bucketCount <= MaximumInstanceBuckets ? EnumerateBuckets(minimum, maximum).ToArray() : [];
        lock (_gate)
        {
            if (_nextIdentity == ulong.MaxValue) throw new InvalidOperationException("Static instance identities are exhausted.");
            var handle = new ZoneManagedStaticInstanceHandle(++_nextIdentity);
            _instances.Add(handle, new(mesh, primitive, localToWorld, worldToLocal, bounds, admitted, keys));
            if (keys.Length == 0) _oversized.Add(handle);
            foreach (var key in keys)
            {
                if (!_buckets.TryGetValue(key, out var entries)) _buckets.Add(key, entries = []);
                entries.Add(handle);
            }
            return handle;
        }
    }

    /// <summary>Removes only this instance. Shared mesh resources remain usable by other instances.</summary>
    public bool RemoveInstance(ZoneManagedStaticInstanceHandle handle)
    {
        lock (_gate)
        {
            if (!_instances.Remove(handle, out var instance)) return false;
            _oversized.Remove(handle);
            foreach (var key in instance.Buckets)
            {
                var entries = _buckets[key];
                entries.Remove(handle);
                if (entries.Count == 0) _buckets.Remove(key);
            }
            // Scratch candidates must not retain an unloaded instance's potentially large shared
            // mesh until another query happens to replace the last query's references.
            _queryInstances.Clear();
            _queryIds.Clear();
            _queryTriangles.Clear();
            return true;
        }
    }

    public bool Contains(ZoneManagedStaticInstanceHandle handle) { lock (_gate) return _instances.ContainsKey(handle); }

    /// <summary>Unloads scene instances without reusing retired handles or mutating shared meshes.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _instances.Clear();
            _buckets.Clear();
            _oversized.Clear();
            _queryIds.Clear();
            _queryInstances.Clear();
            _queryTriangles.Clear();
            _lastQuery = default;
        }
    }

    /// <summary>
    /// Finds the nearest bounded static hit. Mesh normals preserve transformed authored winding
    /// on both sides; analytic primitive normals point outward, including exits from an interior
    /// origin. Normal filters apply to candidate mesh faces and analytic roots before selecting
    /// the nearest eligible hit. Nominal tangent decisions can differ from native float rounding.
    /// </summary>
    public bool Raycast(Vector3 origin, Vector3 displacement, out ZonePhysicsHit hit,
        float minimumNormalZ = -1, float maximumNormalZ = 1)
    {
        ZoneManagedTerrainGeometry.ValidateSegment(origin, displacement);
        ValidateNormalRange(minimumNormalZ, maximumNormalZ);
        lock (_gate)
        {
            hit = default;
            if (displacement == Vector3.Zero) { _lastQuery = default; return false; }
            var end = origin + displacement;
            CollectCandidates(new(Vector3.Min(origin, end), Vector3.Max(origin, end)), out var boundsTests);
            var best = double.PositiveInfinity;
            var triangleTests = 0;
            foreach (var instance in _queryInstances)
            {
                var localOrigin = Vector3.Transform(origin, instance.WorldToLocal);
                var localDisplacement = Vector3.TransformNormal(displacement, instance.WorldToLocal);
                if (instance.Primitive is { } primitive)
                {
                    var normalTransform = Matrix4x4.Transpose(instance.WorldToLocal);
                    if (!primitive.RaycastLocal(localOrigin, localDisplacement, out var fraction, out var position, out var localNormal,
                        normalTransform, minimumNormalZ, maximumNormalZ)) continue;
                    var normal = Vector3.Normalize(Vector3.TransformNormal(localNormal, normalTransform));
                    if (fraction >= best || normal.Z < minimumNormalZ || normal.Z > maximumNormalZ) continue;
                    best = fraction;
                    hit = new((float)(fraction * displacement.Length()), Vector3.Transform(position, instance.LocalToWorld), normal, primitive.PhysicalMaterialId);
                    continue;
                }
                instance.Mesh!.QueryRay(localOrigin, localDisplacement, _queryTriangles);
                foreach (var triangle in _queryTriangles)
                {
                    triangleTests++;
                    WorldTriangle(instance, triangle, out var a, out var b, out var c, out var material);
                    if (!ZoneManagedTriangleQueries.Raycast(origin, displacement, a, b, c, out var fraction, out var normal) ||
                        fraction >= best || normal.Z < minimumNormalZ || normal.Z > maximumNormalZ) continue;
                    best = fraction;
                    hit = new(fraction * displacement.Length(), origin + displacement * fraction, normal, material);
                }
            }
            _lastQuery = new(boundsTests, _queryInstances.Count, triangleTests);
            return double.IsFinite(best);
        }
    }

    public bool TryGround(Vector3 origin, float maxDistance, out ZonePhysicsGroundHit ground,
        float minimumNormalZ = .0001f)
    {
        if (!float.IsFinite(maxDistance) || maxDistance <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxDistance), "Ground distance must be finite and positive.");
        if (Raycast(origin, new(0, 0, -maxDistance), out var hit, minimumNormalZ))
        {
            ground = new(hit.Distance, hit.Position, hit.Normal, hit.PhysicalMaterialId);
            return true;
        }
        ground = default;
        return false;
    }

    /// <summary>
    /// Continuously sweeps a box, upright capsule or cylinder against mesh surfaces and
    /// analytic convex targets. Analytic contacts use the declared separation tolerance; this
    /// does not promise the same tolerance in distance traveled at a grazing contact.
    /// Filtering contact normals before selecting the nearest hit lets callers skip walkable
    /// floors while retaining a farther wall, or select downward ceiling normals.
    /// Tangential/outward travel from an existing face contact is clear; inward travel from an
    /// existing overlap reports distance zero. TryCollide reports overlap independent of motion.
    /// Each analytic convex target has one approaching entry; a rejected entry does not substitute
    /// an exit. Other instances remain candidates. Numerical exhaustion throws a named failure;
    /// penetration recovery and a minimum-translation normal for embedded shapes are not supplied.
    /// </summary>
    public bool TrySweep(ZonePhysicsShape shape, Vector3 origin, Vector3 displacement, out ZonePhysicsHit hit,
        float minimumNormalZ = -1, float maximumNormalZ = 1)
    {
        ZoneManagedTerrainGeometry.ValidateShape(shape);
        ZoneManagedTerrainGeometry.ValidateSegment(origin, displacement);
        ValidateNormalRange(minimumNormalZ, maximumNormalZ);
        var end = origin + displacement;
        var worldBounds = new ZoneManagedBounds(Vector3.Min(origin, end) - shape.BoundingHalfExtents, Vector3.Max(origin, end) + shape.BoundingHalfExtents);
        ZoneManagedTerrainGeometry.ValidateFinite(worldBounds.Minimum, nameof(shape));
        ZoneManagedTerrainGeometry.ValidateFinite(worldBounds.Maximum, nameof(shape));
        lock (_gate)
        {
            hit = default;
            if (displacement == Vector3.Zero) { _lastQuery = default; return false; }
            CollectCandidates(worldBounds, out var boundsTests);
            var best = float.PositiveInfinity;
            var triangleTests = 0;
            foreach (var instance in _queryInstances)
            {
                if (instance.Primitive is { } primitive)
                {
                    if (!ZoneManagedPrimitiveQueries.Sweep(primitive, instance.LocalToWorld, origin, shape, displacement,
                        out var fraction, out var normal, out var position) || fraction >= best ||
                        normal.Z < minimumNormalZ || normal.Z > maximumNormalZ) continue;
                    best = fraction;
                    hit = new(fraction * displacement.Length(), position, normal, primitive.PhysicalMaterialId);
                    continue;
                }
                instance.Mesh!.QueryBounds(worldBounds.Transform(instance.WorldToLocal), _queryTriangles);
                foreach (var triangle in _queryTriangles)
                {
                    triangleTests++;
                    WorldTriangle(instance, triangle, out var a, out var b, out var c, out var material);
                    if (!ZoneManagedTriangleQueries.Sweep(shape, origin, displacement, a, b, c,
                        out var fraction, out var normal, out var position) || fraction >= best ||
                        normal.Z < minimumNormalZ || normal.Z > maximumNormalZ) continue;
                    best = fraction;
                    hit = new(fraction * displacement.Length(), position, normal, material);
                }
            }
            _lastQuery = new(boundsTests, _queryInstances.Count, triangleTests);
            return float.IsFinite(best);
        }
    }

    public bool TryCollide(ZonePhysicsShape shape, Vector3 position)
    {
        ZoneManagedTerrainGeometry.ValidateShape(shape);
        ZoneManagedTerrainGeometry.ValidateFinite(position, nameof(position));
        var bounds = new ZoneManagedBounds(position - shape.BoundingHalfExtents, position + shape.BoundingHalfExtents);
        ZoneManagedTerrainGeometry.ValidateFinite(bounds.Minimum, nameof(shape));
        ZoneManagedTerrainGeometry.ValidateFinite(bounds.Maximum, nameof(shape));
        lock (_gate)
        {
            CollectCandidates(bounds, out var boundsTests);
            var triangleTests = 0;
            var collided = false;
            foreach (var instance in _queryInstances)
            {
                if (instance.Primitive is { } primitive)
                {
                    collided = ZoneManagedPrimitiveQueries.Overlaps(primitive, instance.LocalToWorld, position, shape);
                    if (collided) break;
                    continue;
                }
                if (instance.Mesh!.ContainsSolidPoint(Vector3.Transform(position, instance.WorldToLocal)))
                {
                    collided = true;
                    break;
                }
                instance.Mesh.QueryBounds(bounds.Transform(instance.WorldToLocal), _queryTriangles);
                foreach (var triangle in _queryTriangles)
                {
                    triangleTests++;
                    WorldTriangle(instance, triangle, out var a, out var b, out var c, out _);
                    if (!ZoneManagedTriangleQueries.Overlaps(shape, position, a, b, c)) continue;
                    collided = true;
                    break;
                }
                if (collided) break;
            }
            _lastQuery = new(boundsTests, _queryInstances.Count, triangleTests);
            return collided;
        }
    }

    /// <summary>
    /// Copies positive overlap unprojections. General triangles remain independent two-sided
    /// surface contacts; flagged convex resources and analytic primitives return whole-volume
    /// minimum translations. Depth reaches touch, without a clearance gap. Equal-depth direction
    /// choices are deterministic and flagged; unreliable numerical solutions throw explicitly.
    /// </summary>
    public IReadOnlyList<ZonePhysicsPenetration> GetPenetrations(ZonePhysicsShape shape, Vector3 position)
    {
        ZoneManagedTerrainGeometry.ValidateShape(shape);
        ZoneManagedTerrainGeometry.ValidateFinite(position, nameof(position));
        var bounds = new ZoneManagedBounds(position - shape.BoundingHalfExtents, position + shape.BoundingHalfExtents);
        ZoneManagedTerrainGeometry.ValidateFinite(bounds.Minimum, nameof(shape));
        ZoneManagedTerrainGeometry.ValidateFinite(bounds.Maximum, nameof(shape));
        lock (_gate)
        {
            CollectCandidates(bounds, out var boundsTests);
            var contacts = new List<ZonePhysicsPenetration>();
            var triangleTests = 0;
            foreach (var instance in _queryInstances)
            {
                if (instance.Primitive is { } primitive)
                {
                    if (ZoneManagedPenetrationQueries.Try(ZoneManagedConvexTarget.ForPrimitive(primitive, instance.LocalToWorld),
                        shape, position, primitive.PhysicalMaterialId, false, out var contact)) contacts.Add(contact);
                    continue;
                }
                var mesh = instance.Mesh!;
                if (mesh.SolidConvex)
                {
                    if (ZoneManagedPenetrationQueries.Try(ZoneManagedConvexTarget.ForSolidMesh(mesh, instance.LocalToWorld),
                        shape, position, 0, false, out var contact))
                    {
                        var material = SolidContactMaterial(instance, contact, ref triangleTests);
                        contacts.Add(new(contact.Depth, contact.Normal, contact.Position, material, false, contact.IsAmbiguous));
                    }
                    continue;
                }
                mesh.QueryBounds(bounds.Transform(instance.WorldToLocal), _queryTriangles);
                foreach (var triangle in _queryTriangles)
                {
                    triangleTests++;
                    WorldTriangle(instance, triangle, out var a, out var b, out var c, out var material);
                    if (Vector3.Cross(b - a, c - a).LengthSquared() < 1e-18f) continue;
                    var target = ZoneManagedConvexTarget.ForTriangle(a, b, c) with
                        { Mesh = mesh, Transform = instance.LocalToWorld, SourceTriangle = triangle };
                    if (ZoneManagedPenetrationQueries.Try(target, shape, position, material, true,
                        out var contact)) contacts.Add(contact);
                }
            }
            _lastQuery = new(boundsTests, _queryInstances.Count, triangleTests);
            return ZoneManagedPenetrationCollection.Snapshot(contacts);
        }
    }

    private ushort SolidContactMaterial(Instance instance, ZonePhysicsPenetration contact, ref int triangleTests)
        => SolidContactMaterial(instance, contact.Position, contact.Normal, ref triangleTests);

    private ushort SolidContactMaterial(Instance instance, Vector3 position, Vector3 normal, ref int triangleTests)
    {
        var extent = new Vector3(MathF.Max(.001f, PenetrationTolerance * 4));
        instance.Mesh!.QueryBounds(new ZoneManagedBounds(position - extent, position + extent).Transform(instance.WorldToLocal), _queryTriangles);
        if (_queryTriangles.Count == 0) throw ZoneManagedPenetrationMath.Failure("solid contact witness has no nearby authored face");
        var best = double.PositiveInfinity; var alignment = -1f; ushort material = 0;
        foreach (var triangle in _queryTriangles)
        {
            triangleTests++;
            WorldTriangle(instance, triangle, out var a, out var b, out var c, out var candidate);
            ZoneManagedPenetrationMath.ClosestTriangle(ZoneManagedDouble3.From(position), ZoneManagedDouble3.From(a),
                ZoneManagedDouble3.From(b), ZoneManagedDouble3.From(c), out var wa, out var wb, out var wc);
            var point = ZoneManagedDouble3.From(a) * wa + ZoneManagedDouble3.From(b) * wb + ZoneManagedDouble3.From(c) * wc;
            var squared = (point - ZoneManagedDouble3.From(position)).Squared;
            var n = Vector3.Cross(b - a, c - a);
            var score = n.LengthSquared() > 0 ? MathF.Abs(Vector3.Dot(Vector3.Normalize(n), normal)) : 0;
            if (squared > best + 1e-12 || Math.Abs(squared - best) <= 1e-12 && score <= alignment) continue;
            best = squared; material = candidate; alignment = score;
        }
        return material;
    }

    private void CollectCandidates(ZoneManagedBounds bounds, out int boundsTests)
    {
        _queryIds.Clear();
        _queryInstances.Clear();
        var (minimum, maximum, bucketCount) = BucketRange(bounds);
        if (bucketCount > MaximumQueryBuckets)
        {
            // Unusually large queries fall back to instance bounds, never a full triangle scan.
            foreach (var handle in _instances.Keys) _queryIds.Add(handle);
        }
        else
        {
            foreach (var key in EnumerateBuckets(minimum, maximum))
                if (_buckets.TryGetValue(key, out var entries)) _queryIds.UnionWith(entries);
            _queryIds.UnionWith(_oversized);
        }
        boundsTests = _queryIds.Count;
        foreach (var handle in _queryIds)
        {
            var instance = _instances[handle];
            if (instance.Bounds.Overlaps(bounds)) _queryInstances.Add(instance);
        }
    }

    private static void WorldTriangle(Instance instance, int triangle, out Vector3 a, out Vector3 b, out Vector3 c, out ushort material)
    {
        instance.Mesh!.GetTriangle(triangle, out a, out b, out c, out material);
        a = Vector3.Transform(a, instance.LocalToWorld);
        b = Vector3.Transform(b, instance.LocalToWorld);
        c = Vector3.Transform(c, instance.LocalToWorld);
    }

    private static (Bucket Minimum, Bucket Maximum, double Count) BucketRange(ZoneManagedBounds bounds)
    {
        var minimum = BucketAt(bounds.Minimum);
        var maximum = BucketAt(bounds.Maximum);
        var count = ((double)maximum.X - minimum.X + 1) * ((double)maximum.Y - minimum.Y + 1) * ((double)maximum.Z - minimum.Z + 1);
        return (minimum, maximum, count);
    }

    private static Bucket BucketAt(Vector3 position) => new(BucketCoordinate(position.X), BucketCoordinate(position.Y), BucketCoordinate(position.Z));
    private static int BucketCoordinate(float coordinate) => (int)Math.Clamp(Math.Floor(coordinate / (double)BucketSize), int.MinValue, int.MaxValue);

    private static IEnumerable<Bucket> EnumerateBuckets(Bucket minimum, Bucket maximum)
    {
        for (var x = (long)minimum.X; x <= maximum.X; x++)
            for (var y = (long)minimum.Y; y <= maximum.Y; y++)
                for (var z = (long)minimum.Z; z <= maximum.Z; z++)
                    yield return new((int)x, (int)y, (int)z);
    }

    private static void ValidateNormalRange(float minimum, float maximum)
    {
        if (!float.IsFinite(minimum) || !float.IsFinite(maximum) || minimum < -1 || maximum > 1 || minimum > maximum)
            throw new ArgumentOutOfRangeException(nameof(minimum), "Normal filters must be an ordered range within -1..1.");
    }

    private static void ValidateTransform(Matrix4x4 matrix, out Matrix4x4 inverse)
    {
        ReadOnlySpan<float> values = [matrix.M11, matrix.M12, matrix.M13, matrix.M14, matrix.M21, matrix.M22, matrix.M23, matrix.M24,
            matrix.M31, matrix.M32, matrix.M33, matrix.M34, matrix.M41, matrix.M42, matrix.M43, matrix.M44];
        foreach (var value in values)
            if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(matrix), "Instance transforms must be finite.");
        if (matrix.M14 != 0 || matrix.M24 != 0 || matrix.M34 != 0 || matrix.M44 != 1)
            throw new ArgumentException("Collision instances require an affine transform.", nameof(matrix));
        if (!Matrix4x4.Invert(matrix, out inverse)) throw new ArgumentException("Collision instances require an invertible transform.", nameof(matrix));
        ReadOnlySpan<float> inverseValues = [inverse.M11, inverse.M12, inverse.M13, inverse.M21, inverse.M22, inverse.M23,
            inverse.M31, inverse.M32, inverse.M33, inverse.M41, inverse.M42, inverse.M43];
        foreach (var value in inverseValues)
            if (!float.IsFinite(value)) throw new ArgumentException("The inverse collision transform must be finite.", nameof(matrix));
    }

    private sealed record Instance(ZoneManagedTriangleMesh? Mesh, ZoneManagedPrimitiveGeometry? Primitive, Matrix4x4 LocalToWorld,
        Matrix4x4 WorldToLocal, ZoneManagedBounds Bounds, ZoneManagedBounds AdmissionBounds, Bucket[] Buckets);
    private readonly record struct Bucket(int X, int Y, int Z);
}
