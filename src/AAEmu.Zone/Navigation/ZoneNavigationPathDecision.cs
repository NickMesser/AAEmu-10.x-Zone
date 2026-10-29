using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Ai;

namespace AAEmu.Zone.Navigation;

/// <summary>Copied original fields; the scene must supply their actual parameter/controller producers.</summary>
public sealed record ZoneNavigationPathDecisionProfile(
    ulong NavigationCapabilities, bool ProxyIsSwimming, bool FlyingCreature, bool UnderwaterCreature,
    bool ThreeDimensionalMovement, uint CachedNavigationType, bool HasActorTargetRequest,
    bool HasSmartObjectActorTarget, bool AllowSlightBackoff, int SplineMode, int PuppetAlertness,
    bool AdjustAroundDynamicObstacles, bool CrowdControlInPathfind, float PathRegenerationInterval,
    float CombatRunMaximum);

/// <summary>3113CFC0 query profile; the scene owns actual target inventory, coverage and ray callbacks.</summary>
public readonly record struct ZoneNavigationClearanceRay(Vector3 Origin, Vector3 Displacement)
{
    public const uint ObjectMask = 0x107;
    public const uint Flags = 0x2000f;
}

public readonly record struct ZoneNavigationClearanceHit(float Distance, Vector3 Point);
public readonly record struct ZoneNavigationVerticalSpan(float Ceiling, float Floor);
public readonly record struct ZoneNavigationWaterProbe(Vector3 Point, float WaterLevel, ZoneNavigationVerticalSpan Span);
public readonly record struct ZoneNavigationUpperClearance(ZoneNavigationClearanceRay DownwardRay, float Ceiling);
public readonly record struct ZoneNavigationPathDecisionResult(ZoneApproachPathDecision Decision,
    Vector3? RequestedEnd, uint CachedNavigationType, bool ClearArgument);

/// <summary>
///311757BC's ordinary path publication, including actual dry-water and empty-dynamic-census
///admission. Search, generation/revision ownership and the final argument clear belong to the
///scene owner. Unknown adjustment branches refuse before publishing a partially admitted path.
/// </summary>
public static partial class ZoneNavigationPathDecision
{
    /// <summary>31143130: first point, then up to20 three-meter 3D steps per segment, selected by XY distance.</summary>
    public static IReadOnlyList<Vector3> GetOrdinaryWaterProbePoints(ZoneNavigationPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!path.HasPath) return Array.Empty<Vector3>();
        RequireOrdinaryPoints(path);
        List<Vector3> points = [path.Points[0].Position];
        for (var index = 0; index + 1 < path.Count; index++)
        {
            var current = path.Points[index].Position;
            var end = path.Points[index + 1].Position;
            var remaining = 20;
            do
            {
                var x = end.X - current.X; var y = end.Y - current.Y; var z = end.Z - current.Z;
                Vector3 next;
                if (y * y + x * x > 9)
                {
                    Normalize(new(x, y, z), out var direction);
                    next = new(direction.X * 3 + current.X,
                        direction.Y * 3 + current.Y, direction.Z * 3 + current.Z);
                }
                else { next = end; remaining = 0; }
                points.Add(next); current = next;
            }
            while (--remaining > 0);
        }
        return points.AsReadOnly();
    }

    /// <summary>3113CFC0 upward query. Its initial ceiling is water+(bodyMaxZ-swimThreshold).</summary>
    public static ZoneNavigationClearanceRay CreateUpperClearanceRay(Vector3 point, float bodyMaxZ)
    {
        Finite(point); Finite(bodyMaxZ);
        return new(point, new(0, 0, bodyMaxZ > 2 ? bodyMaxZ : 2));
    }

    /// <summary>The original lower ray starts at point+upwardDistance (or hitDistance-.05) and spans-10000.</summary>
    public static ZoneNavigationUpperClearance CompleteUpperClearance(Vector3 point, float swimThreshold,
        float bodyMaxZ, float waterLevel, ZoneNavigationClearanceHit? upwardHit)
    {
        var upward = CreateUpperClearanceRay(point, bodyMaxZ);
        Finite(swimThreshold); Finite(waterLevel);
        var distance = upward.Displacement.Z;
        var ceiling = waterLevel + (bodyMaxZ - swimThreshold);
        if (upwardHit is { } hit)
        {
            Finite(hit.Distance); Finite(hit.Point);
            distance = hit.Distance - .05f; ceiling = hit.Point.Z - .05f;
        }
        return new(new(new(point.X + 0f, point.Y + 0f, point.Z + distance), new(0, 0, -10000)), ceiling);
    }

    public static ZoneNavigationVerticalSpan CompleteVerticalSpan(ZoneNavigationUpperClearance upper,
        ZoneNavigationClearanceHit? downwardHit, float rendererTerrainHeight)
    {
        Finite(rendererTerrainHeight);
        var floor = rendererTerrainHeight;
        if (downwardHit is { } hit) { Finite(hit.Point); floor = hit.Point.Z + .05f; }
        return new(upper.Ceiling, floor);
    }

    /// <summary>
    ///Resolve3115CAA4 from its actual fields/current candidate. The unowned region query is
    ///required only for the mixed-capability/no-point branch with cached type1.
    /// </summary>
    public static bool UsesThreeDimensionalNavigation(ZoneNavigationPathDecisionProfile profile,
        ZoneNavigationPath path, out uint cachedNavigationType)
    {
        ArgumentNullException.ThrowIfNull(profile); ArgumentNullException.ThrowIfNull(path);
        cachedNavigationType = profile.CachedNavigationType;
        if (profile.ProxyIsSwimming) return true;
        var spatial = (profile.NavigationCapabilities & 0x38) != 0;
        var planar = (profile.NavigationCapabilities & 0x106) != 0;
        if (spatial && !planar) return true;
        if (!spatial && planar) return false;
        if (!path.HasPath || profile.ThreeDimensionalMovement)
        {
            if (cachedNavigationType == 1)
                throw new NotSupportedException("Mixed navigation capabilities require the actual region-navigation query for cached type1.");
            return (cachedNavigationType & 0x146) == 0 && cachedNavigationType != 1;
        }
        cachedNavigationType = path.Points[0].NavigationType;
        return (path.Points[0].NavigationType & 0x46) == 0 || (path.Points[1].NavigationType & 0x46) == 0;
    }

    public static ZoneNavigationPathDecisionResult PublishOrdinary(ZoneActorNavigationState navigation,
        ZoneActorControlState controls, ZoneNavigationPath candidate, ZoneNavigationPathDecisionProfile profile,
        ZoneActorHandle actor, Vector3 actorPosition, IReadOnlyList<ZoneNavigationWaterProbe> waterMeasurements,
        ZoneNavigationObstacleCensus? obstacleCensus)
    {
        ArgumentNullException.ThrowIfNull(navigation); ArgumentNullException.ThrowIfNull(controls);
        ArgumentNullException.ThrowIfNull(candidate); ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(waterMeasurements); Finite(actorPosition);
        RequireOrdinaryProfile(profile);
        var staged = candidate.Clone();
        staged.Version = unchecked(navigation.CurrentPath.Version + 1); //311757BC preserves old destination version, not source version.
        var spatial = UsesThreeDimensionalNavigation(profile, staged, out var cachedType);
        if (spatial) throw new NotSupportedException("This path decision requires the actual three-dimensional adjustment consumer.");
        if (staged.HasPath && staged.CreationParameters.IsDirectional && profile.AllowSlightBackoff)
            throw new NotSupportedException("Directional slight-backoff requires the actual forbidden-area adjustment consumer.");
        var requestedEnd = staged.HasPath ? staged.Points[^1].Position : (Vector3?)null;
        if (staged.HasPath)
        {
            var points = GetOrdinaryWaterProbePoints(staged);
            if (points.Count != waterMeasurements.Count)
                throw new NotSupportedException("Ordinary path adjustment requires every original water/vertical-span measurement.");
            for (var index = 0; index < points.Count; index++)
            {
                var measurement = waterMeasurements[index];
                Finite(measurement.Point); Finite(measurement.WaterLevel); Finite(measurement.Span.Floor);
                if (measurement.Point != points[index])
                    throw new InvalidOperationException("Water measurements belong to a different path or sampling order.");
                if (!(measurement.WaterLevel - measurement.Span.Floor <= 0))
                    throw new NotSupportedException("Path water transitions require the original insertion/height adjustment consumer.");
            }
            var distance = staged.CreationParameters.EndDistance;
            if (MathF.Abs(distance) > .0001f) staged.TrimEnd(distance, false);
            RequireOrdinaryPoints(staged); //31141940 encounters no smart-object128 points in this profile.
            if (profile.AdjustAroundDynamicObstacles)
            {
                if (profile.PuppetAlertness >= 2)
                    throw new NotSupportedException("Combat path adjustment requires its actual avoidance-flag exception producers.");
                var query = CreateObstacleQuery(staged, actorPosition, profile.PathRegenerationInterval, profile.CombatRunMaximum);
                if (profile.CrowdControlInPathfind && profile.PathRegenerationInterval > .01f)
                    RequireEmptyObstacleCensus(actor, query, obstacleCensus);
                //31142900 always calls3113EAA0 before visiting even an empty obstacle vector.
                if (staged.Count != 0) staged.Version = unchecked(staged.Version + 1);
            }
        }
        var urgency = controls.MovementUrgency;
        if (staged.HasPath)
        {
            //Actual length is evaluated after water/end/smart processing and before AdjustPath.
            //The admitted empty-obstacle processor changes version/history only, so this length is identical.
            ZoneActorMovementControl.AdjustAdaptiveUrgency(controls, ref urgency, staged.GetLength(true));
        }
        //No external callbacks occur during publication. The scene must recheck generation/revision before this call.
        navigation.CurrentPath.CopyFrom(staged); navigation.CurrentPath.Version = staged.Version;
        if (candidate.HasPath && profile.AdjustAroundDynamicObstacles)
        { navigation.CurrentPath.ClearObjectsAdjustedFor(); navigation.OriginalPath.ClearObjectsAdjustedFor(); }
        navigation.CopyCurrentToOriginal();
        controls.MovementUrgency = urgency;
        return new(ZoneApproachPathDecision.Found, requestedEnd, cachedType, false);
    }

    /// <summary>3115D474 clears both paths. The owning scene must also cancel its request/target state and clear its argument.</summary>
    public static ZoneNavigationPathDecisionResult PublishNoPath(ZoneActorNavigationState navigation,
        ZoneNavigationPathDecisionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(navigation); ArgumentNullException.ThrowIfNull(profile);
        if (profile.HasActorTargetRequest || profile.HasSmartObjectActorTarget)
            throw new NotSupportedException("No-path cancellation requires the actual actor-target/smart-object request owner.");
        navigation.CurrentPath.Clear(); navigation.OriginalPath.Clear();
        return new(ZoneApproachPathDecision.NoPath, null, profile.CachedNavigationType, true);
    }

    private static void RequireOrdinaryProfile(ZoneNavigationPathDecisionProfile profile)
    {
        if (profile.HasActorTargetRequest || profile.HasSmartObjectActorTarget)
            throw new NotSupportedException("Path decisions require the actual pending actor-target/smart-object request consumer.");
        if (profile.FlyingCreature || profile.UnderwaterCreature)
            throw new NotSupportedException("Flying/underwater path adjustment requires its original processor.");
        if (profile.SplineMode > 0)
            throw new NotSupportedException("Positive Puppet spline mode requires the original path-to-spline processor.");
        Finite(profile.PathRegenerationInterval); Finite(profile.CombatRunMaximum);
    }
    private static void RequireOrdinaryPoints(ZoneNavigationPath path)
    {
        foreach (var point in path.Points)
        {
            Finite(point.Position);
            if (point.NavigationType != 2)
                throw new NotSupportedException("Ordinary path decisions require actual ground-triangle navigation type2; smart-object/flying transitions are separate.");
        }
    }
    private static void Finite(float value)
    { if (!float.IsFinite(value)) throw new NotSupportedException("Path adjustment requires finite copied/query values."); }
    private static void Finite(Vector3 value) { Finite(value.X); Finite(value.Y); Finite(value.Z); }
    //3100D534 returns1/(1/sqrt), not the original sqrt; this can differ by one float ULP.
    private static float Normalize(Vector3 value, out Vector3 normalized)
    {
        var squared = (value.X * value.X + value.Y * value.Y) + value.Z * value.Z;
        if (squared <= 1.1920928955078125e-7f) { normalized = Vector3.Zero; return 0; }
        var inverse = 1 / MathF.Sqrt(squared);
        normalized = new(value.X * inverse, value.Y * inverse, value.Z * inverse);
        return 1 / inverse;
    }
}
