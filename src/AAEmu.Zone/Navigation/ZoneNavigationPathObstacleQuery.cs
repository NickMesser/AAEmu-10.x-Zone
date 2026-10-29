using System.Numerics;
using AAEmu.Zone.Actors;

namespace AAEmu.Zone.Navigation;

public readonly record struct ZoneNavigationObstacleBounds(Vector3 Minimum, Vector3 Maximum);
public readonly record struct ZoneNavigationObstacleQuery(Vector3 ActorPosition, float ActorRadius,
    ZoneNavigationObstacleBounds PhysicalBounds)
{
    //3100D4C8 adds0x40000 to31151CCC's0x10006. Classes2/4 are sleeping/rigid;
    //the accompanying query flags do not admit static1, living8 or terrain0x100.
    public const uint PhysicalMask = 0x50006;
}

/// <summary>Only actual actor-grid registrations belong here; point/dummy objects are not that grid.</summary>
public readonly record struct ZoneNavigationAiObstacleCandidate(ZoneActorHandle Actor, int ObjectType,
    Vector3 Position, object? PhysicalEntity);
public readonly record struct ZoneNavigationPhysicalObstacleCandidate(object PhysicalEntity, uint EntityClass,
    ZoneNavigationObstacleBounds Bounds);
public readonly record struct ZoneNavigationAvoidanceSphere(ulong Owner, Vector3 Position, float Radius);

/// <summary>
///Actual copied query inventories for one scene publication. The scene must refuse incomplete,
///retired or unknown inventories; absence of a guessed candidate is not an empty query result.
/// </summary>
public sealed record ZoneNavigationObstacleCensus(ZoneNavigationObstacleQuery Query,
    IReadOnlyList<ZoneNavigationAiObstacleCandidate> Actors,
    IReadOnlyList<ZoneNavigationPhysicalObstacleCandidate> PhysicalEntities,
    IReadOnlyList<ZoneNavigationAvoidanceSphere> AvoidanceSpheres);

/// <summary>CAISystem constructor3107FD08's initially empty map, actual v792 producer3105EBF8.</summary>
public sealed class ZoneNavigationAvoidanceSpheres : IDisposable
{
    private readonly SortedDictionary<ulong, ZoneNavigationAvoidanceSphere> _values = [];
    public bool IsDisposed { get; private set; }
    public int Count => _values.Count;
    public void Set(ulong owner, Vector3 position, float radius)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (!float.IsFinite(radius) || !float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
            throw new NotSupportedException("Navigation avoidance spheres require finite source values.");
        if (radius <= 0) _values.Remove(owner);
        else _values[owner] = new(owner, position, radius);
    }
    public IReadOnlyList<ZoneNavigationAvoidanceSphere> Copy()
    { ObjectDisposedException.ThrowIf(IsDisposed, this); return Array.AsReadOnly(_values.Values.ToArray()); }
    public void Dispose() { if (IsDisposed) return; IsDisposed = true; _values.Clear(); }
}

public static partial class ZoneNavigationPathDecision
{
    /// <summary>
    ///31151CCC: call after31141B30 end trimming.3113EB50 bounds the path prefix from
    ///its current fraction using abs(Combat.Run.Maximum*pathRegenInterval), then expands by15.
    ///The actor-grid query uses the actual actor position and that distance plus15.
    /// </summary>
    public static ZoneNavigationObstacleQuery CreateObstacleQuery(ZoneNavigationPath adjustedPath,
        Vector3 actorPosition, float pathRegenerationInterval, float combatRunMaximum)
    {
        ArgumentNullException.ThrowIfNull(adjustedPath);
        Finite(actorPosition); Finite(pathRegenerationInterval); Finite(combatRunMaximum);
        RequireOrdinaryPoints(adjustedPath);
        if (!adjustedPath.HasPath) throw new InvalidOperationException("Obstacle queries require a real path segment.");
        var product = (1f * combatRunMaximum) * pathRegenerationInterval;
        var remaining = MathF.Sqrt(product * product);
        Finite(remaining);
        var radius = 15 + remaining;
        var point = default(Vector3);
        adjustedPath.TryGetPosition(0, false, false, ref point);
        var minimum = point; var maximum = point;
        for (var index = 1; index < adjustedPath.Count; index++)
        {
            var end = adjustedPath.Points[index].Position;
            var x = end.X - point.X; var y = end.Y - point.Y; var z = end.Z - point.Z;
            var length = Normalize(new(x, y, z), out var direction);
            if (length > remaining)
            {
                var clipped = new Vector3(direction.X * remaining + point.X,
                    direction.Y * remaining + point.Y, direction.Z * remaining + point.Z);
                minimum = Vector3.Min(minimum, clipped); maximum = Vector3.Max(maximum, clipped);
                break;
            }
            minimum = Vector3.Min(minimum, end); maximum = Vector3.Max(maximum, end);
            point = end; remaining -= length;
        }
        var expansion = new Vector3(15);
        return new(actorPosition, radius, new(minimum - expansion, maximum + expansion));
    }

    private static void RequireEmptyObstacleCensus(ZoneActorHandle actor, ZoneNavigationObstacleQuery query,
        ZoneNavigationObstacleCensus? census)
    {
        if (census is null) throw new NotSupportedException("Default path adjustment requires the actual AI/physical/avoidance-sphere query inventories.");
        if (census.Query != query) throw new InvalidOperationException("Obstacle inventory belongs to a different adjusted path/query.");
        ArgumentNullException.ThrowIfNull(census.Actors); ArgumentNullException.ThrowIfNull(census.PhysicalEntities);
        ArgumentNullException.ThrowIfNull(census.AvoidanceSpheres);
        HashSet<object> excludedPhysicalEntities = new(ReferenceEqualityComparer.Instance);
        foreach (var candidate in census.Actors)
        {
            Finite(candidate.Position);
            var delta = candidate.Position - query.ActorPosition;
            var squared = (delta.X * delta.X + delta.Y * delta.Y) + delta.Z * delta.Z;
            if (!(query.ActorRadius * query.ActorRadius > squared)) continue;
            if (candidate.PhysicalEntity is { } physical) excludedPhysicalEntities.Add(physical);
            if (candidate.Actor == actor) continue;
            if (candidate.ObjectType is 5 or 7 or 100)
                throw new NotSupportedException("A real nearby AI actor requires native navigation-interaction/velocity and dynamic obstacle shape processing.");
        }
        foreach (var entity in census.PhysicalEntities)
        {
            ArgumentNullException.ThrowIfNull(entity.PhysicalEntity);
            Finite(entity.Bounds.Minimum); Finite(entity.Bounds.Maximum);
            if (!Overlaps(query.PhysicalBounds, entity.Bounds) || excludedPhysicalEntities.Contains(entity.PhysicalEntity)) continue;
            if (entity.EntityClass == 0)
                throw new NotSupportedException("The queried physical owner has no resolved native entity class.");
            if ((entity.EntityClass & 6) != 0)
                throw new NotSupportedException("A real sleeping/rigid physical candidate requires native part bounds/height and obstacle geometry processing.");
        }
        if (census.AvoidanceSpheres.Count != 0)
            throw new NotSupportedException("Registered navigation avoidance spheres require the original path-distance and sphere adjustment processor.");
        //No AI/physical/sphere obstacle expanded the native +/-9999999 sentinel box.
        //31151CCC therefore bypasses its subsequent navigation-node obstacle gathering.
    }

    private static bool Overlaps(ZoneNavigationObstacleBounds first, ZoneNavigationObstacleBounds second) =>
        first.Minimum.X <= second.Maximum.X && first.Maximum.X >= second.Minimum.X &&
        first.Minimum.Y <= second.Maximum.Y && first.Maximum.Y >= second.Minimum.Y &&
        first.Minimum.Z <= second.Maximum.Z && first.Maximum.Z >= second.Minimum.Z;
}
