using System.Collections.ObjectModel;
using System.Numerics;

namespace AAEmu.Zone.Navigation;

/// <summary>
/// Shared smart-object endpoints. These are opaque AI-object references, not entity or content IDs.
/// Path points retain this immutable descriptor independently of the path which supplied it.
/// </summary>
public sealed class ZoneSmartObjectNavigationData(object? from, object? to)
{
    public object? From { get; } = from;
    public object? To { get; } = to;
}

public readonly record struct ZoneNavigationPoint(
    Vector3 Position, uint NavigationType = 1, ZoneSmartObjectNavigationData? SmartObjectData = null, int SmartObjectMethod = 0)
{
    // 31143A1C default point construction. CLR default(ZoneNavigationPoint) remains a zeroed value.
    public ZoneNavigationPoint() : this(Vector3.Zero) { }
}

public readonly record struct ZoneNavigationMovementPoint(Vector3 Position, Vector3 Direction);

/// <summary>
/// Owned native CNavPath point data and traversal operations. Scene navigation and physical movement remain separate services.
/// </summary>
public sealed partial class ZoneNavigationPath
{
    // DAT_311CEE04. Point insertion tests each component independently, not the vector's length.
    private const float DuplicateTolerance = 0.05f;
    private readonly List<ZoneNavigationPoint> _points = [];
    private readonly List<ZoneNavigationDebugLine> _debugLines = [];
    private readonly List<ZoneNavigationDebugSphere> _debugSpheres = [];
    private readonly List<object?> _objectsAdjustedFor = [];

    // 31142EA0: independent empty collections, default creation parameters, a valid requested end, and version -1.
    public ZoneNavigationPath()
    {
        Points = _points.AsReadOnly(); DebugLines = _debugLines.AsReadOnly(); DebugSpheres = _debugSpheres.AsReadOnly();
        ObjectsAdjustedFor = _objectsAdjustedFor.AsReadOnly();
    }

    public ReadOnlyCollection<ZoneNavigationPoint> Points { get; }
    public ReadOnlyCollection<ZoneNavigationDebugLine> DebugLines { get; }
    public ReadOnlyCollection<ZoneNavigationDebugSphere> DebugSpheres { get; }
    // Native vector<CAIObject*> at +A8. These references identify scene-owned objects, not entity/content IDs.
    public ReadOnlyCollection<object?> ObjectsAdjustedFor { get; }
    public ZoneNavigationPathCreationParameters CreationParameters { get; set; } = new();
    public int Count => _points.Count;
    // 3113CA60: one point does not form a navigable segment.
    public bool HasPath => Count >= 2;
    public Vector3 EndDirection { get; set; }
    public float CurrentFraction { get; set; }
    public float StuckTime { get; set; }
    public bool PathEndIsAsRequested { get; set; } = true;
    public float DiscardedPathLength { get; set; }
    public int Version { get; set; } = -1;

    /// <summary>3113F280: prepend a point; even a rejected duplicate advances the version.</summary>
    public void PushFront(ZoneNavigationPoint point, bool force = false)
    {
        Version = unchecked(Version + 1);
        if (!force && Count != 0 && IsDuplicate(_points[0], point)) return;
        _points.Insert(0, point);
    }

    /// <summary>3113F390: append a point; either endpoint's navigation type can permit vertical duplicates.</summary>
    public void PushBack(ZoneNavigationPoint point, bool force = false)
    {
        Version = unchecked(Version + 1);
        if (!force && Count != 0 && IsDuplicate(_points[^1], point)) return;
        _points.Add(point);
    }

    private static bool IsDuplicate(ZoneNavigationPoint existing, ZoneNavigationPoint incoming)
    {
        var a = existing.Position; var b = incoming.Position;
        // Native COMISS/JB also inserts unordered (NaN) coordinates.
        if (!(MathF.Abs(b.X - a.X) <= DuplicateTolerance) || !(MathF.Abs(b.Y - a.Y) <= DuplicateTolerance)) return false;
        if (((existing.NavigationType | incoming.NavigationType) & 6) == 0 && !(MathF.Abs(b.Z - a.Z) <= DuplicateTolerance)) return false;
        return existing.NavigationType != 0x80 && incoming.NavigationType != 0x80;
    }

    /// <summary>3113D400: replacing an absent front point has no effect, including on the version.</summary>
    public void ReplaceFront(ZoneNavigationPoint point)
    {
        if (Count == 0) return;
        Version = unchecked(Version + 1);
        _points[0] = point;
    }

    /// <summary>
    /// 3113F1D0: remove the current segment's first point and return the following segment's destination.
    /// Reaching the final point returns false and leaves the caller's destination unchanged.
    /// </summary>
    public bool TryAdvance(ref ZoneNavigationPoint next)
    {
        if (Count < 2) return false;
        Version = unchecked(Version + 1);
        _points.RemoveAt(0);
        if (Count == 1) { CurrentFraction = 0; return false; }
        next = _points[1];
        return true;
    }

    /// <summary>3113F4B0: clear points, debug geometry and progress, retaining creation parameters, end direction and adjusted objects.</summary>
    public void Clear()
    {
        Version = unchecked(Version + 1);
        _points.Clear();
        ClearDebugGeometry();
        CurrentFraction = 0; StuckTime = 0; DiscardedPathLength = 0; PathEndIsAsRequested = true;
    }

    /// <summary>311407F0: append diagnostic geometry only when the enclosing engine's debug CVar is enabled.</summary>
    public void AddDebugLine(ZoneNavigationDebugLine line, bool debugDrawingEnabled)
    {
        if (debugDrawingEnabled) _debugLines.Add(line);
    }

    /// <summary>311408F0: diagnostic sphere radii and float colors are stored without clamping.</summary>
    public void AddDebugSphere(ZoneNavigationDebugSphere sphere, bool debugDrawingEnabled)
    {
        if (debugDrawingEnabled) _debugSpheres.Add(sphere);
    }

    /// <summary>311410D0 clears these collections at the start of path following, independently of path points or version.</summary>
    public void ClearDebugGeometry() { _debugLines.Clear(); _debugSpheres.Clear(); }

    /// <summary>31119898: preserve each scene-object reference and insertion order, including repeated or null references.</summary>
    public void AddObjectAdjustedFor(object? adjustedObject) => _objectsAdjustedFor.Add(adjustedObject);

    /// <summary>3117050C explicitly clears both paths' adjustment records before rebuilding the current path's records.</summary>
    public void ClearObjectsAdjustedFor() => _objectsAdjustedFor.Clear();

    /// <summary>
    /// 3113E320: sample distance from the current fraction of the first segment.
    /// Flattening changes distance measurement only; the sampled height still interpolates.
    /// </summary>
    public bool TryGetPosition(float distance, bool flattenToXY, bool extrapolate, ref Vector3 result)
    {
        if (Count == 0) return false;
        if (Count == 1) { result = _points[0].Position; return true; }
        var next = _points[1].Position;
        var current = Interpolate(_points[0].Position, next, CurrentFraction);
        for (var index = 1; index < Count; index++)
        {
            next = _points[index].Position;
            var length = SegmentLength(current, next, flattenToXY);
            if (distance <= length)
            {
                var fraction = !(length > 0) ? 0 : distance / length;
                result = Interpolate(current, next, fraction);
                return true;
            }
            distance -= length;
            current = next;
        }
        if (!extrapolate) { result = next; return true; }
        // The native loop already assigned current = next. Keep the resulting zero-vector extrapolation,
        // including the arithmetic for nonfinite coordinates, instead of inventing a terminal tangent.
        var x = next.X - current.X; var y = next.Y - current.Y; var z = next.Z - current.Z;
        var squared = y * y + x * x;
        if (!flattenToXY) squared += z * z;
        var remainingLength = MathF.Sqrt(squared);
        var scale = !(remainingLength > 0) ? 0 : distance / remainingLength;
        result = new(x * scale + next.X, y * scale + next.Y, z * scale + next.Z);
        return true;
    }

    // Scalar operations preserve the original floating-point order; do not substitute SIMD/FMA interpolation.
    private static Vector3 Interpolate(Vector3 first, Vector3 second, float fraction)
    {
        var inverse = 1 - fraction;
        return new(second.X * fraction + inverse * first.X,
            second.Y * fraction + inverse * first.Y, second.Z * fraction + inverse * first.Z);
    }

    private static float SegmentLength(Vector3 from, Vector3 to, bool flattenToXY)
    {
        var x = to.X - from.X; var y = to.Y - from.Y; var z = to.Z - from.Z;
        var squared = y * y + x * x;
        if (!flattenToXY) squared += z * z;
        return MathF.Sqrt(squared);
    }

    /// <summary>
    /// 3113EE60: length from the current sampled position through the second and subsequent stored points.
    /// </summary>
    public float GetLength(bool flattenToXY = false)
    {
        var current = default(Vector3);
        if (!TryGetPosition(0, false, false, ref current) || Count < 2) return 0;
        var total = 0f;
        for (var index = 1; index < Count; index++)
        {
            var next = _points[index].Position;
            var x = next.X - current.X; var y = next.Y - current.Y; var z = next.Z - current.Z;
            var squared = flattenToXY ? x * x + y * y : (y * y + x * x) + z * z;
            total += MathF.Sqrt(squared);
            current = next;
        }
        return total;
    }

    /// <summary>31142FF0: own a reversed snapshot excluding the first stored point, with the end direction on its first entry.</summary>
    public IReadOnlyList<ZoneNavigationMovementPoint> GetMovementOutput()
    {
        var output = new ZoneNavigationMovementPoint[Math.Max(0, Count - 1)];
        for (var index = 0; index < output.Length; index++)
            output[index] = new(_points[Count - index - 1].Position, index == 0 ? EndDirection : Vector3.Zero);
        return Array.AsReadOnly(output);
    }

    /// <summary>3113EE00: obtain the shared smart-object endpoints for a qualifying final path point.</summary>
    public ZoneSmartObjectNavigationData? GetEndSmartObjectData()
    {
        if (Count == 0) return null;
        var end = _points[^1];
        return end.NavigationType == 0x80 && end.SmartObjectMethod is 4 or 5 ? end.SmartObjectData : null;
    }

    /// <summary>31044208: independently own all path collections and copy creation/progress state with a source-version increment.</summary>
    public void CopyFrom(ZoneNavigationPath source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!ReferenceEquals(this, source))
        {
            _points.Clear(); _points.AddRange(source._points);
            _debugLines.Clear(); _debugLines.AddRange(source._debugLines);
            _debugSpheres.Clear(); _debugSpheres.AddRange(source._debugSpheres);
            _objectsAdjustedFor.Clear(); _objectsAdjustedFor.AddRange(source._objectsAdjustedFor);
        }
        CreationParameters = source.CreationParameters;
        EndDirection = source.EndDirection; CurrentFraction = source.CurrentFraction; StuckTime = source.StuckTime;
        PathEndIsAsRequested = source.PathEndIsAsRequested; DiscardedPathLength = source.DiscardedPathLength;
        Version = unchecked(source.Version + 1);
    }

    public ZoneNavigationPath Clone()
    {
        var copy = new ZoneNavigationPath(); copy.CopyFrom(this); return copy;
    }
}
