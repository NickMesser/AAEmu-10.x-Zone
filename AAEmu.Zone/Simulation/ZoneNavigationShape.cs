using System.Numerics;

namespace AAEmu.Zone.Simulation;

/// <summary>
/// Immutable authored navigation geometry with native 3117C218 bounds and both
/// 3117C328 containment paths. Loading starts without a cache; BuildStripCache
/// performs native 3117CD88's later optimization. The scene's shape-container
/// spatial index remains a separate service.
/// </summary>
public sealed class ZoneNavigationShape
{
    private readonly Vector3[] _points;
    private ZoneNavigationShapeStripCache? _stripCache;
    public IReadOnlyList<Vector3> Points { get; }
    public Vector3 BoundsMinimum { get; }
    public Vector3 BoundsMaximum { get; }
    public bool HasStripCache => _stripCache is not null;

    public ZoneNavigationShape(IEnumerable<Vector3> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        _points = points.ToArray();
        Points = Array.AsReadOnly(_points);
        var minimum = new Vector3(9_999_999f);
        var maximum = new Vector3(-9_999_999f);
        foreach (var point in _points)
        {
            // 310430D0's COMISS/JA selects the incoming value on equality
            // or unordered comparisons, including signed zero and NaN.
            if (!(point.X > minimum.X)) minimum.X = point.X;
            if (!(point.Y > minimum.Y)) minimum.Y = point.Y;
            if (!(point.Z > minimum.Z)) minimum.Z = point.Z;
            if (!(maximum.X > point.X)) maximum.X = point.X;
            if (!(maximum.Y > point.Y)) maximum.Y = point.Y;
            if (!(maximum.Z > point.Z)) maximum.Z = point.Z;
        }
        var paddingX = (maximum.X - minimum.X) * .00001f;
        var paddingY = (maximum.Y - minimum.Y) * .00001f;
        maximum.X += paddingX;
        maximum.Y += paddingY;
        minimum.X -= paddingX;
        minimum.Y -= paddingY;
        BoundsMinimum = minimum;
        BoundsMaximum = maximum;
    }

    /// <summary>
    /// Test XY using the current native cache state. Z is ignored; no tolerance,
    /// nearest-edge projection or polygon validity repair is applied. The optimized
    /// path deliberately retains its different native arithmetic on polygon edges.
    /// </summary>
    public bool Contains(Vector3 position)
    {
        if (_stripCache is { } cache) return cache.Contains(_points, BoundsMinimum.Y, position);
        if (!(BoundsMinimum.X <= position.X && position.X < BoundsMaximum.X &&
              BoundsMinimum.Y <= position.Y && position.Y < BoundsMaximum.Y))
            return false;
        var inside = false;
        for (var index = 0; index < _points.Length; index++)
        {
            var first = _points[index];
            var second = _points[index + 1 == _points.Length ? 0 : index + 1];
            if (!(second.Y <= position.Y && position.Y < first.Y ||
                  first.Y <= position.Y && position.Y < second.Y))
                continue;
            // Preserve the binary's scalar float operation order. In particular,
            // points exactly on a crossing do not toggle the parity.
            var crossing = ((first.X - second.X) * (position.Y - second.Y)) /
                           (first.Y - second.Y) + second.X;
            if (position.X < crossing) inside = !inside;
        }
        return inside;
    }

    /// <summary>
    /// Replaces the prior cache, as 3117CD88 does. Small polygons and Y extents
    /// retain the unoptimized path. Non-finite XY, unrepresentable extents and
    /// clipping results, and invalid strip ranges are rejected at this managed
    /// boundary instead of reproducing native malformed-input behavior.
    /// </summary>
    public void BuildStripCache()
    {
        _stripCache = null;
        _stripCache = ZoneNavigationShapeStripCache.Build(_points, BoundsMinimum, BoundsMaximum);
    }

    /// <summary>Release only optimization storage; authored geometry and bounds are retained.</summary>
    public void ClearStripCache() => _stripCache = null;
}
