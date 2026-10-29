using System.Numerics;

namespace AAEmu.Zone.Ai;

/// <summary>
/// ServerAIUnitSystem39213180's final motion comparison. The caller must first resolve the exact live Unit,
/// CanMove, model v320 null, actor/animated-character and movement-state owners as the original does.
/// </summary>
public static class ZoneApproachStuckMath
{
    public static bool IsStuck(Vector3 requestedDirection, float requestedSpeed, int speedAttribute10,
        Vector3 animatedActualVelocity)
    {
        if (.0001f >= MathF.Abs(requestedDirection.X) && .0001f >= MathF.Abs(requestedDirection.Y) &&
            .0001f >= MathF.Abs(requestedDirection.Z)) return false;
        if (requestedSpeed < .0001f) return false;
        var factor = (float)speedAttribute10 * .001f + 1;
        if (factor < .1f) return false;
        var adjusted = factor * requestedSpeed;
        var threshold = (adjusted * adjusted) * .09f;
        if (.01f > threshold) threshold = .01f;
        var velocitySquared = (animatedActualVelocity.X * animatedActualVelocity.X + animatedActualVelocity.Y * animatedActualVelocity.Y) +
            animatedActualVelocity.Z * animatedActualVelocity.Z;
        return threshold > velocitySquared;
    }
}
