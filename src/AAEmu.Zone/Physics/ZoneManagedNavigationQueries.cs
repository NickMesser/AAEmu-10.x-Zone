using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Navigation;

namespace AAEmu.Zone.Physics;

/// <summary>Explicit sampled-ground replacement policy; these are not native walkability constants.</summary>
public sealed record ZoneManagedNavigationQueryPolicy(float MaximumSegmentLength, float SupportSampleSpacing,
    int MaximumSupportSamples, float MaximumRise, float MaximumDrop, float MinimumSupportNormalZ);

/// <summary>
/// Read-only navigation admission over the scene's existing managed physics owner. Ground samples
/// use the ACTUAL applied living dimensions and support ray, then continuous full-body casts join
/// those samples. No pose, velocity or step is written, and no collision gap is added. The AI pass
/// radius remains graph clearance metadata; it never replaces the actor's physical shape.
/// Ground continuity/rise/drop are sampled under an explicit bounded policy. Full native X2/Cry
/// walkability, stairs and moving supports are not claimed. Visibility tests terrain and supplied
/// static proxies in both directions; other actors are not vision occluders in this profile.
/// Coverage callbacks must certify the entire requested region, including unsupported proxies;
/// a missing/unknown region cannot be inferred clear from a ray or sweep miss.
/// </summary>
public sealed class ZoneManagedNavigationQueries : IZoneNavigationSceneQueries
{
    private readonly ZoneManagedPhysicsBackend _physics;
    private readonly ZoneActorHandle _actor;
    private readonly ZoneManagedNavigationQueryPolicy _policy;
    private readonly Func<ZonePhysicsQueryBounds, ZoneNavigationSegmentAdmission> _terrainCoverage, _collisionCoverage;
    private readonly ZoneManagedStaticGeometry? _visibilityStatics;

    public ZoneManagedNavigationQueries(ZoneManagedPhysicsBackend physics, ZoneActorHandle actor,
        ZoneManagedNavigationQueryPolicy policy,
        Func<ZonePhysicsQueryBounds, ZoneNavigationSegmentAdmission> terrainCoverage,
        Func<ZonePhysicsQueryBounds, ZoneNavigationSegmentAdmission> collisionCoverage,
        ZoneManagedStaticGeometry? visibilityStatics = null)
    {
        ArgumentNullException.ThrowIfNull(physics); ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(terrainCoverage); ArgumentNullException.ThrowIfNull(collisionCoverage);
        if (!float.IsFinite(policy.MaximumSegmentLength) || policy.MaximumSegmentLength <= 0 ||
            !float.IsFinite(policy.SupportSampleSpacing) || policy.SupportSampleSpacing <= 0 || policy.MaximumSupportSamples < 1 ||
            !float.IsFinite(policy.MaximumRise) || policy.MaximumRise < 0 || !float.IsFinite(policy.MaximumDrop) || policy.MaximumDrop < 0 ||
            !float.IsFinite(policy.MinimumSupportNormalZ) || policy.MinimumSupportNormalZ <= 0 || policy.MinimumSupportNormalZ > 1)
            throw new ArgumentOutOfRangeException(nameof(policy), "Navigation query bounds and support thresholds must be finite and representable.");
        _physics = physics; _actor = actor; _policy = policy; _terrainCoverage = terrainCoverage; _collisionCoverage = collisionCoverage;
        _visibilityStatics = visibilityStatics ?? physics.StaticGeometry;
    }

    public ZoneNavigationSegmentAdmission CheckWalkSegment(Vector3 from, Vector3 to, float passRadius)
    {
        ZoneManagedTerrainGeometry.ValidateFinite(from, nameof(from)); ZoneManagedTerrainGeometry.ValidateFinite(to, nameof(to));
        if (!float.IsFinite(passRadius) || passRadius < 0) throw new ArgumentOutOfRangeException(nameof(passRadius));
        if (!Begin(out var description, out var actualPosition)) return Unsupported("The bound actor generation is no longer registered.");
        if (description.Profile != ZonePhysicsActorProfile.Living || description.LivingMovement is not { } movement ||
            !description.CollisionActive || description.LivingFlag4 || description.Gravity > 0)
            return Unsupported("Walking admission requires an active upright living ground profile with copied applied dimensions.");
        var length = Distance(from, to);
        if (length > _policy.MaximumSegmentLength) return Unsupported("The segment exceeds the explicit walking query length.");
        var count = length == 0 ? 1 : Math.Max(2, Math.Ceiling(length / _policy.SupportSampleSpacing) + 1);
        if (count > _policy.MaximumSupportSamples) return Unsupported("The segment exceeds the explicit support-sample budget.");
        try
        {
            var actualCenter = actualPosition + new Vector3(0, 0, description.CenterOffsetZ);
            var coverage = Covered(Bounds(actualCenter, actualCenter, description.Shape.BoundingHalfExtents), description);
            if (coverage.Outcome != ZoneNavigationSegmentOutcome.Clear) return coverage;
            if (_physics.NavigationPoseBlocked(description.Shape, actualCenter, _actor)) return Blocked("The actor's current physical body is penetrating a collider.");
            Vector3? previousCenter = null; float? previousFloor = null;
            for (var i = 0; i < (int)count; i++)
            {
                var t = count == 1 ? 0 : i / (count - 1);
                var sample = new Vector3((float)(from.X + ((double)to.X - from.X) * t),
                    (float)(from.Y + ((double)to.Y - from.Y) * t), (float)(from.Z + ((double)to.Z - from.Z) * t));
                var ray = ZoneLivingMovementMath.CreateSupportRay(sample, movement);
                coverage = Covered(Bounds(ray.Origin, ray.Origin + ray.Displacement,
                    new(description.Shape.BoundingHalfExtents.X, description.Shape.BoundingHalfExtents.Y, 0)), description);
                if (coverage.Outcome != ZoneNavigationSegmentOutcome.Clear) return coverage;
                if (!_physics.NavigationSupport(sample, movement, out var support)) return Missing("No authored ground support exists at a sampled point.");
                if (support.Normal is not { } normal || normal.Z < _policy.MinimumSupportNormalZ)
                    return Blocked("A sampled surface exceeds the explicit support-normal policy.");
                var projection = (double)support.Position.Z - ((double)sample.Z - movement.HeightPivot);
                if (projection > _policy.MaximumRise || -projection > _policy.MaximumDrop)
                    return Blocked("A path point exceeds the explicit support projection rise/drop policy.");
                if (previousFloor is { } floor && ((double)support.Position.Z - floor > _policy.MaximumRise || (double)floor - support.Position.Z > _policy.MaximumDrop))
                    return Blocked("Consecutive support samples exceed the explicit rise/drop policy.");
                var projected = new Vector3(sample.X, sample.Y, support.Position.Z + movement.HeightPivot);
                var center = projected + new Vector3(0, 0, description.CenterOffsetZ);
                coverage = Covered(Bounds(center, previousCenter ?? center, description.Shape.BoundingHalfExtents), description);
                if (coverage.Outcome != ZoneNavigationSegmentOutcome.Clear) return coverage;
                if (_physics.NavigationPoseBlocked(description.Shape, center, _actor)) return Blocked("The applied physical shape has no positive-depth clearance at a support sample.");
                if (previousCenter is { } previous && center != previous &&
                    _physics.TrySweep(description.Shape, previous, center - previous, _actor, out _))
                    return Blocked("The continuous physical body corridor intersects authored geometry or another registered body.");
                previousCenter = center; previousFloor = support.Position.Z;
            }
            return Current(description) ? Clear() : Unsupported("The actor body changed or retired during walking admission.");
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or ArgumentException)
        { return Unsupported("Managed walking query refused: " + error.Message); }
    }

    public ZoneNavigationSegmentAdmission CheckLineOfSight(Vector3 from, Vector3 to)
    {
        ZoneManagedTerrainGeometry.ValidateFinite(from, nameof(from)); ZoneManagedTerrainGeometry.ValidateFinite(to, nameof(to));
        if (!Begin(out var description, out _)) return Unsupported("The bound actor generation is no longer registered.");
        if (Distance(from, to) > _policy.MaximumSegmentLength) return Unsupported("The visibility segment exceeds the explicit query length.");
        try
        {
            var coverage = Covered(Bounds(from, to, Vector3.Zero), description);
            if (coverage.Outcome != ZoneNavigationSegmentOutcome.Clear) return coverage;
            if (_visibilityStatics?.NavigationContainsSolidPoint(from) == true || _visibilityStatics?.NavigationContainsSolidPoint(to) == true)
                return Blocked("A visibility endpoint lies inside an admitted solid proxy.");
            var displacement = to - from;
            if (displacement != Vector3.Zero &&
                (_physics.Geometry.RaycastTerrain(from, displacement, out _) || _physics.Geometry.RaycastTerrain(to, -displacement, out _) ||
                 _visibilityStatics?.Raycast(from, displacement, out _) == true))
                return Blocked("The bounded visibility segment intersects authored terrain or static collision.");
            return Current(description) ? Clear() : Unsupported("The actor body changed or retired during visibility admission.");
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or ArgumentException)
        { return Unsupported("Managed visibility query refused: " + error.Message); }
    }

    private bool Begin(out ZonePhysicsActorDescription description, out Vector3 position) => _physics.TryNavigationActor(_actor, out description, out position);
    private bool Current(ZonePhysicsActorDescription description) => _physics.NavigationActorIsCurrent(_actor, description);
    private ZoneNavigationSegmentAdmission Covered(ZonePhysicsQueryBounds bounds, ZonePhysicsActorDescription description)
    {
        var terrain = _terrainCoverage(bounds);
        if (!Current(description)) return Unsupported("The bound actor body changed or retired in a coverage callback.");
        if (terrain.Outcome != ZoneNavigationSegmentOutcome.Clear) return terrain;
        var collision = _collisionCoverage(bounds);
        return Current(description) ? collision : Unsupported("The bound actor body changed or retired in a coverage callback.");
    }
    private static ZonePhysicsQueryBounds Bounds(Vector3 first, Vector3 last, Vector3 half)
        => new(Vector3.Min(first, last) - half, Vector3.Max(first, last) + half);
    private static double Distance(Vector3 a, Vector3 b)
    { var x = (double)a.X - b.X; var y = (double)a.Y - b.Y; var z = (double)a.Z - b.Z; return Math.Sqrt(x * x + y * y + z * z); }
    private static ZoneNavigationSegmentAdmission Clear() => new(ZoneNavigationSegmentOutcome.Clear);
    private static ZoneNavigationSegmentAdmission Blocked(string detail) => new(ZoneNavigationSegmentOutcome.Blocked, detail);
    private static ZoneNavigationSegmentAdmission Missing(string detail) => new(ZoneNavigationSegmentOutcome.MissingData, detail);
    private static ZoneNavigationSegmentAdmission Unsupported(string detail) => new(ZoneNavigationSegmentOutcome.Unsupported, detail);
}

// Read helpers stay on the same backend; this adapter creates no body or physics owner.
public sealed partial class ZoneManagedPhysicsBackend
{
    internal bool TryNavigationActor(ZoneActorHandle actor, out ZonePhysicsActorDescription description, out Vector3 position)
    {
        if (_actors.TryGetValue(actor, out var current) && OwnedPhysics.TryReadPosition(actor, out position))
        { description = current; return true; }
        description = null!; position = default; return false;
    }
    internal bool NavigationActorIsCurrent(ZoneActorHandle actor, ZonePhysicsActorDescription description)
        => _actors.TryGetValue(actor, out var current) && ReferenceEquals(current, description);
    internal bool NavigationSupport(Vector3 position, ZonePhysicsLivingMovementSettings settings, out ZonePhysicsCharacterSupport support)
        => _groundQuery.TryLivingSupport(position, settings, out support);
    internal bool NavigationPoseBlocked(ZonePhysicsShape shape, Vector3 center, ZoneActorHandle ignore)
    {
        if (Geometry.GetPenetrations(shape, center).Any(contact => contact.Depth > ZoneManagedStaticGeometry.PenetrationTolerance) ||
            StaticGeometry?.GetPenetrations(shape, center).Any(contact => contact.Depth > ZoneManagedStaticGeometry.PenetrationTolerance) == true) return true;
        foreach (var (actor, description) in _actors)
            if (actor != ignore && ZoneManagedShapeQueries.TryPenetration(shape, center, description.Shape, Center(actor, description), out var contact) &&
                contact.Depth > ZoneManagedStaticGeometry.PenetrationTolerance) return true;
        return false;
    }
}

public sealed partial class ZoneManagedStaticGeometry
{
    /// <summary>Zero-radius point containment in explicitly solid targets; no inflated ray shape.</summary>
    internal bool NavigationContainsSolidPoint(Vector3 point)
    {
        lock (_gate)
        {
            CollectCandidates(new(point, point), out _);
            foreach (var instance in _queryInstances)
            {
                var local = Vector3.Transform(point, instance.WorldToLocal);
                ZoneManagedTerrainGeometry.ValidateFinite(local, nameof(point));
                if (instance.Mesh?.ContainsSolidPoint(local) == true) return true;
                if (instance.Primitive is not { } primitive) continue;
                var x = (double)local.X - primitive.Center.X; var y = (double)local.Y - primitive.Center.Y; var z = (double)local.Z - primitive.Center.Z;
                var squared = x * x + y * y + z * z; var radiusSquared = (double)primitive.Radius * primitive.Radius;
                if (primitive.Kind == ZoneManagedPrimitiveKind.Sphere) { if (squared <= radiusSquared) return true; continue; }
                var axial = (x * primitive.Axis.X + y * primitive.Axis.Y) + z * primitive.Axis.Z;
                if (primitive.Kind == ZoneManagedPrimitiveKind.Cylinder)
                { if (Math.Abs(axial) <= primitive.HalfHeight && squared - axial * axial <= radiusSquared) return true; }
                else
                {
                    var closest = Math.Clamp(axial, -primitive.HalfHeight, primitive.HalfHeight);
                    x -= primitive.Axis.X * closest; y -= primitive.Axis.Y * closest; z -= primitive.Axis.Z * closest;
                    if (x * x + y * y + z * z <= radiusSquared) return true;
                }
            }
            return false;
        }
    }
}
