using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Content;
using AAEmu.Zone.Navigation;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Scene;

/// <summary>
/// Explicit bounded graph/walk policy. Original path following and path adjustment are
/// separate source-backed owners; this policy does not claim native route/walkability parity.
/// </summary>
public sealed record ZoneManagedNpcNavigationPolicy(ZoneManagedNavigationQueryPolicy Walking,
    float MaximumEndpointDistance, int MaximumExpandedNodes);

internal readonly record struct ZoneManagedNpcNavigationCoverage(ulong Terrain, ulong Characters, ulong Rays, ulong Water);

/// <summary>
/// Real terrain, water and registered physical inventories for path admission. Authored
/// vegetation, entity and road collision must be classified before their cell can be
/// certified; brush coverage alone never establishes an empty scene query.
/// </summary>
public sealed class ZoneManagedNpcNavigationQueries
{
    private readonly ZoneManagedPhysicsBackend _physics;
    private readonly ZoneTerrainHeightField _heights;
    private readonly ZoneManagedSceneLoadReport _scene;
    private readonly ZoneManagedSceneCollision _collision;
    private readonly ZoneManagedWaterQueries _water;
    public ZoneManagedNpcNavigationPolicy Policy { get; }

    public ZoneManagedNpcNavigationQueries(ZoneManagedPhysicsBackend physics, ZoneTerrainHeightField heights,
        ZoneManagedSceneLoadReport scene, ZoneManagedSceneCollision collision, ZoneManagedWaterQueries water,
        ZoneManagedNpcNavigationPolicy policy)
    {
        _physics = physics ?? throw new ArgumentNullException(nameof(physics));
        _heights = heights ?? throw new ArgumentNullException(nameof(heights));
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        _collision = collision ?? throw new ArgumentNullException(nameof(collision));
        _water = water ?? throw new ArgumentNullException(nameof(water));
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        if (!(policy.MaximumEndpointDistance > 0) || !float.IsFinite(policy.MaximumEndpointDistance) || policy.MaximumExpandedNodes <= 0)
            throw new ArgumentOutOfRangeException(nameof(policy));
        if (!ReferenceEquals(physics.StaticGeometry, collision.Geometry))
            throw new ArgumentException("Navigation requires the scene's existing physical brush inventory.", nameof(collision));
        if (!ReferenceEquals(physics.Geometry.HeightField, heights))
            throw new ArgumentException("Navigation coverage requires the physical backend's actual installed terrain owner.", nameof(heights));
    }

    public IZoneNavigationSceneQueries ForActor(ZoneActorHandle actor) => new ZoneManagedNavigationQueries(
        _physics, actor, Policy.Walking, CheckTerrainCoverage, CheckCollisionCoverage, _collision.RayGeometry);

    internal IReadOnlyList<ZoneManagedNavigationPhysicalEntity> CopyPhysicalInventory() => _physics.CopyNavigationPhysicalEntities();
    internal ZoneManagedNpcNavigationCoverage CaptureCoverage()
    {
        if (_collision.IsDisposed || _water.IsDisposed)
            throw new NotSupportedException("The NPC navigation collision/water publication has retired.");
        return new(_heights.PublicationRevision, _collision.Geometry.PublicationRevision,
            _collision.RayGeometry.PublicationRevision, _water.PublicationRevision);
    }
    internal bool IsCurrent(ZoneManagedNpcNavigationCoverage publication) => !_collision.IsDisposed && !_water.IsDisposed &&
        publication == CaptureCoverage();

    public ZoneNavigationSegmentAdmission CheckTerrainCoverage(ZonePhysicsQueryBounds bounds)
    {
        if (bounds.Minimum.X < 0 || bounds.Minimum.Y < 0 || bounds.Maximum.X >= 8192 || bounds.Maximum.Y >= 8192)
            return new(ZoneNavigationSegmentOutcome.MissingData, "Navigation query extends outside the engine-local terrain window.");
        for (var x = (int)MathF.Floor(bounds.Minimum.X / 1024); x <= (int)MathF.Floor(bounds.Maximum.X / 1024); x++)
            for (var y = (int)MathF.Floor(bounds.Minimum.Y / 1024); y <= (int)MathF.Floor(bounds.Maximum.Y / 1024); y++)
                if (!_heights.HasLoadedCell(x, y))
                    return new(ZoneNavigationSegmentOutcome.MissingData, $"Navigation query has no installed terrain samples in cell({x},{y}).");
        return new(ZoneNavigationSegmentOutcome.Clear);
    }

    public ZoneNavigationSegmentAdmission CheckCollisionCoverage(ZonePhysicsQueryBounds bounds)
    {
        var terrain = CheckTerrainCoverage(bounds);
        if (terrain.Outcome != ZoneNavigationSegmentOutcome.Clear) return terrain;
        if (!_collision.Report.BrushCoverageComplete)
            return new(ZoneNavigationSegmentOutcome.Unsupported, "Navigation requires complete admitted authored brush collision.");
        for (var x = (int)MathF.Floor(bounds.Minimum.X / 1024); x <= (int)MathF.Floor(bounds.Maximum.X / 1024); x++)
            for (var y = (int)MathF.Floor(bounds.Minimum.Y / 1024); y <= (int)MathF.Floor(bounds.Maximum.Y / 1024); y++)
            {
                var matches = _scene.Cells.Where(cell => cell.Placement.EngineCell == new ZoneMapCoordinate(x, y)).ToArray();
                if (matches.Length != 1) return new(ZoneNavigationSegmentOutcome.MissingData, "Navigation requires one current cell inventory per query cell.");
                var cell = matches[0];
                foreach (var kind in new[] { ZoneCellObjectAsset.Objects, ZoneCellObjectAsset.BigObjects,
                    ZoneCellObjectAsset.Vegetation, ZoneCellObjectAsset.Entities })
                {
                    var present = kind switch
                    {
                        ZoneCellObjectAsset.Objects => cell.Objects.Objects is not null,
                        ZoneCellObjectAsset.BigObjects => cell.Objects.BigObjects is not null,
                        ZoneCellObjectAsset.Vegetation => cell.Objects.Vegetation is not null,
                        ZoneCellObjectAsset.Entities => cell.Objects.Entities is not null,
                        _ => false,
                    };
                    if (!present && !cell.Objects.Diagnostics.Contains($"{kind}: authored asset is absent."))
                        return new(ZoneNavigationSegmentOutcome.MissingData, $"Navigation cannot classify the unread {kind} inventory in cell({x},{y}).");
                }
                if (cell.Objects.Vegetation is { Instances.Count: > 0 } || cell.Objects.Entities is { Entities.Count: > 0 })
                    return new(ZoneNavigationSegmentOutcome.Unsupported,
                        $"Navigation collision still requires authored vegetation/entity classification in cell({x},{y}).");
                if (HasUnknown(cell.Objects.Objects) || HasUnknown(cell.Objects.BigObjects))
                    return new(ZoneNavigationSegmentOutcome.Unsupported,
                        $"Navigation collision still requires authored non-brush object classification in cell({x},{y}).");
            }
        return new(ZoneNavigationSegmentOutcome.Clear);

        static bool HasUnknown(ZoneCellObjectContent? content) => content is not null &&
            content.Objects.Any(row => row is not ZoneCellBrushObject && row.Type != ZoneCellObjectType.Decal);
    }

    internal IReadOnlyList<ZoneNavigationWaterProbe> MeasureWater(ZoneManagedNpcActor actor,
        ZoneManagedNpcBodySnapshot body, ZoneManagedNpcActorMotion motion, ZoneNavigationPath candidate)
    {
        var threshold = motion.CopyNavigationSwimThreshold(actor);
        var stance = body.CurrentStance;
        var upper = stance.HeightCollider + stance.Size.Z;
        if (stance.UseCapsule) upper += MathF.Max(stance.Size.X, stance.Size.Y);
        var bodyMaxZ = MathF.Max(upper, 0) - stance.HeightPivot;
        List<ZoneNavigationWaterProbe> values = [];
        foreach (var point in ZoneNavigationPathDecision.GetOrdinaryWaterProbePoints(candidate))
        {
            var level = _water.GetWaterLevel(point, 0);
            var up = ZoneNavigationPathDecision.CreateUpperClearanceRay(point, bodyMaxZ);
            var afterUp = ZoneNavigationPathDecision.CompleteUpperClearance(point, threshold, bodyMaxZ, level, Cast(up));
            var span = ZoneNavigationPathDecision.CompleteVerticalSpan(afterUp, Cast(afterUp.DownwardRay), _heights.GetHeight(point.X, point.Y));
            values.Add(new(point, level, span));
        }
        return values.AsReadOnly();
    }

    private ZoneNavigationClearanceHit? Cast(ZoneNavigationClearanceRay ray)
    {
        var end = ray.Origin + ray.Displacement;
        var admission = CheckCollisionCoverage(new(Vector3.Min(ray.Origin, end), Vector3.Max(ray.Origin, end)));
        if (admission.Outcome != ZoneNavigationSegmentOutcome.Clear)
            throw new NotSupportedException(admission.Diagnostic);
        ZonePhysicsHit? result = null;
        if (_physics.Geometry.RaycastTerrain(ray.Origin, ray.Displacement, out var terrain)) result = terrain;
        if (_collision.RayGeometry.Raycast(ray.Origin, ray.Displacement, out var brush) &&
            (result is null || brush.Distance < result.Value.Distance)) result = brush;
        //Mask0x107 excludes the scene's known living bodies. Unclassified kinematic
        //entities cannot be assumed absent from the original classes1/2/4 mask.
        if (_physics.CopyNavigationPhysicalEntities().Any(entity => entity.EntityClass == 0))
            throw new NotSupportedException("Navigation clearance has an unclassified registered physical entity.");
        return result is { } hit ? new(hit.Distance, hit.Position) : null;
    }
}
