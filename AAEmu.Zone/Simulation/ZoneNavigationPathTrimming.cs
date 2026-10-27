using System.Numerics;

namespace AAEmu.Zone.Simulation;

public sealed partial class ZoneNavigationPath
{
    /// <summary>
    /// 31141B30: a positive distance removes length from the end; a nonpositive distance specifies its negated retained length.
    /// A retained length below 0.01 replaces the path with two copies of its first point. Trimming changes point data and the path version.
    /// </summary>
    public void TrimEnd(float endDistance, bool flattenToXY = false)
    {
        if (Count < 2) return;
        var length = GetLength(flattenToXY);
        // Native JBE negates zero, negative and unordered input; XOR preserves NaN payload and signed zero.
        var retainedLength = endDistance > 0 ? length - endDistance :
            BitConverter.UInt32BitsToSingle(BitConverter.SingleToUInt32Bits(endDistance) ^ 0x80000000);
        // The native JA does not reject an unordered comparison with the path length.
        if (retainedLength > length) return;
        if (retainedLength < 0.01f)
        {
            Version = unchecked(Version + 1);
            var first = _points[0];
            _points.Clear(); _points.Add(first); _points.Add(first);
            return;
        }

        // GetLength uses CurrentFraction, but this scan begins at the first stored point without applying that fraction.
        var accumulated = 0f;
        for (var index = 0; index + 1 < Count; index++)
        {
            var first = _points[index]; var next = _points[index + 1];
            var segmentLength = TrimSegmentLength(first.Position, next.Position, flattenToXY);
            if (accumulated <= retainedLength && retainedLength < accumulated + segmentLength)
            {
                // Native preserves a segment whose two endpoints both use smart-object navigation.
                if (first.NavigationType == 0x80 && next.NavigationType == 0x80) return;
                if (next.NavigationType == 0x80)
                    next = next with { NavigationType = first.NavigationType, SmartObjectData = null, SmartObjectMethod = 0 };
                var fraction = (retainedLength - accumulated) / segmentLength;
                var start = first.Position; var end = next.Position;
                next = next with { Position = new(
                    (end.X - start.X) * fraction + start.X,
                    (end.Y - start.Y) * fraction + start.Y,
                    (end.Z - start.Z) * fraction + start.Z) };
                _points[index + 1] = next;
                if (index + 2 < Count) _points.RemoveRange(index + 2, Count - index - 2);
                Version = unchecked(Version + 1);
                return;
            }
            accumulated += segmentLength;
        }
    }

    private static float TrimSegmentLength(Vector3 first, Vector3 next, bool flattenToXY)
    {
        var x = first.X - next.X; var y = first.Y - next.Y; var z = first.Z - next.Z;
        // 31141C8D / 31141CAF use different scalar summation orders for horizontal and spatial distance.
        var squared = flattenToXY ? y * y + x * x : (x * x + y * y) + z * z;
        return MathF.Sqrt(squared);
    }
}
