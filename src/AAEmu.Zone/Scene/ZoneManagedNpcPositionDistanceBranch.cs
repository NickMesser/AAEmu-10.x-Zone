using System.Numerics;

namespace AAEmu.Zone.Scene;

/// <summary>3115AAA4 branches45/46: original float distance and ordered comparisons.</summary>
internal static class ZoneManagedNpcPositionDistanceBranch
{
    internal static bool Evaluate(Vector3 actualPosition, Vector3 goalPosition, float threshold, bool greater)
    {
        var x = goalPosition.X - actualPosition.X;
        var y = goalPosition.Y - actualPosition.Y;
        var z = goalPosition.Z - actualPosition.Z;
        var squared = (y * y + x * x) + z * z;
        var distance = MathF.Sqrt(squared);
        //The PE uses ordered comparisons: both predicates are false for NaN.
        //Comparing squared distances changes negative thresholds and overflow.
        return greater ? distance > threshold : distance <= threshold;
    }
}
