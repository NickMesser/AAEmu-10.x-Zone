using System.Numerics;

namespace AAEmu.Zone.Simulation;

/// <summary>3117CD88 construction, 3117E138 records and the cached 3117C328 query.</summary>
internal sealed class ZoneNavigationShapeStripCache
{
    internal readonly record struct Edge(float Minimum, float Maximum, ushort Index, bool Full);
    internal sealed class Strip(float minimum, float maximum)
    {
        internal float Minimum = minimum;
        internal float Maximum = maximum;
        internal readonly List<Edge> Edges = [];
    }

    internal float Height { get; }
    internal float Inverse { get; }
    internal Strip[] Strips { get; }

    private ZoneNavigationShapeStripCache(float height, Strip[] strips)
    {
        Height = height;
        Inverse = 1f / height;
        Strips = strips;
    }

    internal static ZoneNavigationShapeStripCache? Build(Vector3[] points, Vector3 minimum, Vector3 maximum)
    {
        if (points.Length <= 14) return null;
        var extent = maximum.Y - minimum.Y;
        var requested = MathF.Ceiling(extent / 3f);
        // Native float-to-integer conversions and subsequent pointer indexing are
        // unsafe for these malformed inputs. Do not create fabricated geometry.
        if (!float.IsFinite(requested) || requested < 0 || requested >= 4_294_967_296f)
            throw new NotSupportedException("Navigation strip construction requires a finite, representable Y extent.");
        if (requested <= 1) return null;
        if (!float.IsFinite(minimum.X) || !float.IsFinite(maximum.X) ||
            points.Any(point => !float.IsFinite(point.X) || !float.IsFinite(point.Y)))
            throw new NotSupportedException("Navigation strip construction requires finite XY coordinates.");
        var count = (int)Math.Min((uint)requested, (uint)(points.Length / 2));
        var cache = new ZoneNavigationShapeStripCache(extent / count,
            Enumerable.Range(0, count).Select(_ => new Strip(maximum.X, minimum.X)).ToArray());
        var previous = points[^1];
        var previousIndex = points.Length - 1;
        for (var index = 0; index < points.Length; index++)
        {
            var current = points[index];
            if (previous.Y != current.Y)
            {
                var lower = previous;
                var upper = current;
                if (current.Y < previous.Y) (lower, upper) = (current, previous);
                var lowerFraction = (lower.Y - minimum.Y) * cache.Inverse;
                var upperFraction = (upper.Y - minimum.Y) * cache.Inverse;
                var firstStrip = NativeInt(lowerFraction);
                var lastStrip = NativeInt(upperFraction);
                if (firstStrip < lastStrip && upperFraction == (float)lastStrip) lastStrip--;
                firstStrip = Math.Max(firstStrip, 0);
                lastStrip = Math.Min(lastStrip, count - 1);
                if ((uint)firstStrip >= (uint)count || lastStrip < firstStrip)
                    throw new NotSupportedException("Navigation geometry produced an invalid native strip range.");
                var x = lower.X;
                var slope = ((upper.X - lower.X) * cache.Height) / (upper.Y - lower.Y);
                for (var strip = firstStrip; strip < lastStrip; strip++)
                {
                    var nextX = ((float)(strip + 1) - lowerFraction) * slope + lower.X;
                    Add(cache.Strips[strip], x, nextX, unchecked((ushort)previousIndex), strip != firstStrip);
                    x = nextX;
                }
                Add(cache.Strips[lastStrip], x, upper.X, unchecked((ushort)previousIndex), false);
            }
            previous = current;
            previousIndex = index;
        }
        foreach (var strip in cache.Strips)
            // 3117DD64 uses the same MSVC sort, including equal-key ordering.
            ZoneNpcSpawnerOrdering.Sort(strip.Edges, (first, second) => first.Minimum < second.Minimum);
        return cache;
    }

    private static void Add(Strip strip, float first, float second, ushort index, bool full)
    {
        if (!float.IsFinite(first) || !float.IsFinite(second))
            throw new NotSupportedException("Navigation edge clipping produced a non-finite native strip bound.");
        var minimum = first;
        var maximum = second;
        if (second < first) (minimum, maximum) = (second, first);
        if (minimum < strip.Minimum) strip.Minimum = minimum;
        if (strip.Maximum < maximum) strip.Maximum = maximum;
        strip.Edges.Add(new(minimum, maximum, index, full));
    }

    internal bool Contains(Vector3[] points, float minimumY, Vector3 position)
    {
        var index = NativeInt((position.Y - minimumY) * Inverse);
        if ((uint)index >= (uint)Strips.Length) return false;
        var strip = Strips[index];
        if (!(strip.Minimum <= position.X && position.X <= strip.Maximum)) return false;
        var inside = false;
        for (var at = 0; at < strip.Edges.Count; at++)
        {
            var edge = strip.Edges[at];
            if (position.X < edge.Minimum)
            {
                // All remaining edges lie to the right. Complete strip crossings
                // toggle immediately; the two clipped end strips still test Y.
                for (; at < strip.Edges.Count; at++)
                {
                    edge = strip.Edges[at];
                    if (CrossesY(edge)) inside = !inside;
                }
                return inside;
            }
            if (position.X < edge.Maximum && CrossesY(edge))
            {
                var first = points[edge.Index];
                var second = points[(edge.Index + 1) % points.Length];
                var crossing = first.X - ((second.X - first.X) * (first.Y - position.Y)) /
                    (second.Y - first.Y);
                if (position.X <= crossing) inside = !inside;
            }
        }
        return inside;

        bool CrossesY(Edge edge) => edge.Full ||
            (position.Y <= points[edge.Index].Y) != (position.Y <= points[(edge.Index + 1) % points.Length].Y);
    }

    // CVTTSS2SI produces INT_MIN for NaN, infinities and out-of-range values.
    private static int NativeInt(float value) =>
        !float.IsFinite(value) || value < -2_147_483_648f || value >= 2_147_483_648f ? int.MinValue : (int)value;
}
