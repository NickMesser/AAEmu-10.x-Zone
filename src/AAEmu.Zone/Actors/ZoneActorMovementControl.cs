using System.Numerics;

namespace AAEmu.Zone.Actors;

/// <summary>Read-only engine and path services used by the puppet's strafe update.</summary>
public interface IZoneActorStrafeServices
{
    // 3116B94C: resolve the character system through the engine, then query proxy character-key capability.
    // A missing character system returns false. The enclosing actor update must already have a valid proxy.
    bool CanStrafe();
    // Native original-path emptiness (3113CA60) means fewer than two points.
    bool HasPath();
    // 3113EE60 with its flatten-to-XY flag false: length of the original path in three dimensions.
    float GetPathLength();
    Vector3 GetPosition();
    object? GetAttentionTarget();
    Vector3 GetTargetPosition(object target);
    // First entry of the actor's path-output array, or null when that array is empty.
    Vector3? GetFirstPathOutputPosition();
}

/// <summary>
/// Native movement-control calculations. Call at their respective actor/goal phases, not as a replacement for the full actor update.
/// </summary>
public static class ZoneActorMovementControl
{
    /// <summary>Update the actor's movement urgency after its path decision (31175CB2).</summary>
    public static void AdjustAdaptiveUrgency(ZoneActorControlState state, float pathLength)
    {
        ArgumentNullException.ThrowIfNull(state);
        var urgency = state.MovementUrgency;
        AdjustAdaptiveUrgency(state, ref urgency, pathLength);
        state.MovementUrgency = urgency;
    }

    /// <summary>3116847C with the actor's own maximum-path history.</summary>
    public static void AdjustAdaptiveUrgency(ZoneActorControlState state, ref float urgency, float pathLength)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!(state.AdaptiveUrgencyScaleDownPathLength > 0.0001f) || !(pathLength > state.AdaptiveUrgencyMaxPathLength)) return;
        state.AdaptiveUrgencyMaxPathLength = pathLength;
        urgency = AdaptiveUrgency(state, pathLength);
    }

    /// <summary>
    /// 3116847C with a goal-owned maximum. Native updates that maximum but still reads the actor's history to choose the urgency tier.
    /// </summary>
    public static void AdjustAdaptiveUrgency(ZoneActorControlState state, ref float urgency, float pathLength, ref float maximumPathLength)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!(state.AdaptiveUrgencyScaleDownPathLength > 0.0001f) || !(pathLength > maximumPathLength)) return;
        maximumPathLength = pathLength;
        urgency = AdaptiveUrgency(state, pathLength);
    }

    private static float AdaptiveUrgency(ZoneActorControlState state, float pathLength)
    {
        var threshold = state.AdaptiveUrgencyScaleDownPathLength;
        var reductions = 0;
        while (threshold > state.AdaptiveUrgencyMaxPathLength && reductions < 4)
        { threshold *= 0.5f; reductions++; }
        var level = Math.Max(UrgencyLevel(state.AdaptiveUrgencyMin), UrgencyLevel(state.AdaptiveUrgencyMax) - reductions);
        if (pathLength < 1.2f && level > 2) level = 2;
        var sign = level < 0 ? -1f : 1f;
        return Math.Abs(level) switch { 0 => sign * 0, 1 => sign * 0.21f, 2 => sign * 0.4f, 3 => sign, _ => sign * 1.4f };
    }

    // 310C10DC: unlike script speed conversion, this classifies an already converted urgency.
    private static int UrgencyLevel(float urgency)
    {
        var sign = urgency < 0 ? -1 : 1;
        var magnitude = urgency < 0 ? -urgency : urgency;
        if (magnitude < 0.105f) return 0;
        if (magnitude < 0.305f) return sign;
        if (magnitude < 0.7f) return sign * 2;
        if (magnitude < 1.2f) return sign * 3;
        return sign * 4;
    }

    /// <summary>3116B94C: compute the current strafe permission from path or direct distances.</summary>
    public static void UpdateStrafing(ZoneActorControlState state, IZoneActorStrafeServices services)
    {
        ArgumentNullException.ThrowIfNull(state); ArgumentNullException.ThrowIfNull(services);
        state.AllowStrafing = false;
        if (!services.CanStrafe()) return;
        var distances = state.AllowedStrafeDistances;
        if (!(state.DistanceToPathEnd > 0) || !services.HasPath())
        {
            if (!distances.MeansDirect || !(distances.End > 0.001f)) return;
            var target = services.GetAttentionTarget();
            if (target is null) return;
            var targetPosition = services.GetTargetPosition(target);
            state.AllowStrafing = distances.End * distances.End > DistanceSquared(services.GetPosition(), targetPosition);
            return;
        }
        if (distances.Start > 0.001f)
        {
            if (distances.MeansDirect)
                state.AllowStrafing = distances.Start * distances.Start > DistanceSquared(services.GetPosition(), state.PathStartPosition);
            else
            {
                var progress = services.GetPathLength() - state.DistanceToPathEnd;
                // Preserve the previous value for both smaller and unordered comparisons.
                if (progress > state.StrafeStartDistance) state.StrafeStartDistance = progress;
                state.AllowStrafing = distances.Start > state.StrafeStartDistance;
            }
        }
        if (!(distances.End > 0.001f)) return;
        if (distances.MeansDirect && services.GetFirstPathOutputPosition() is { } endPosition)
        {
            if (distances.End * distances.End > DistanceSquared(services.GetPosition(), endPosition)) state.AllowStrafing = true;
        }
        else if (distances.End > state.DistanceToPathEnd) state.AllowStrafing = true;
    }

    // Preserve the native scalar summation order instead of using vector SIMD/FMA distance helpers.
    private static float DistanceSquared(Vector3 position, Vector3 target)
    {
        var x = position.X - target.X; var y = position.Y - target.Y; var z = position.Z - target.Z;
        return (y * y + x * x) + z * z;
    }

    /// <summary>31173B48–31173BEA: delayed stance requires four consecutive moving updates.</summary>
    public static void UpdateDelayedStance(ZoneActorControlState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.DelayedStance == -1) return;
        var direction = state.MovementDirection;
        // Native unordered comparisons also treat a NaN direction component as nonzero.
        if ((direction.X != 0 || direction.Y != 0 || direction.Z != 0) && state.DesiredSpeed > 0)
        {
            state.DelayedStanceMovementCounter = unchecked(state.DelayedStanceMovementCounter + 1);
            if (state.DelayedStanceMovementCounter < 4) return;
            state.BodyState = state.DelayedStance; state.DelayedStance = -1;
        }
        state.DelayedStanceMovementCounter = 0;
    }
}
