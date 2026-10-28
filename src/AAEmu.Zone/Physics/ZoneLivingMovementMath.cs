using System.Numerics;

namespace AAEmu.Zone.Physics;

/// <summary>Independent strict cosine comparisons; these are thresholds, not complete controller states.</summary>
public readonly record struct ZonePhysicsLivingSlopeThresholds(
    bool BelowSlideCosine, bool BelowClimbCosine, bool BelowFallCosine, bool AboveFallCosine);

/// <summary>A copied original support-probe segment, based on the entity origin rather than collider center.</summary>
public readonly record struct ZonePhysicsLivingSupportRay(Vector3 Origin, Vector3 Displacement);

/// <summary>Copied output of the original post-contact airborne gravity/resistance subroutine.</summary>
public readonly record struct ZonePhysicsLivingAirVelocity(Vector3 Velocity,
    bool InheritedSupportVelocity, bool StoppedByResistance);

/// <summary>
/// Verified scalar/vector portions of 35028BF0, 3502CA20 and 3502DED0. Controller flags,
/// requested-versus-actual velocity relaxation, collision ordering and suspension stay with
/// their explicit owners; these small helpers do not infer those missing inputs.
/// </summary>
public static partial class ZoneLivingMovementMath
{
    public static float DegreesToCosine(float degrees)
    {
        Finite(degrees, nameof(degrees));
        return (float)Math.Cos((double)degrees * .0174532925199433);
    }

    /// <summary>Equality triggers none of the strict comparisons; support requires AboveFallCosine.</summary>
    public static ZonePhysicsLivingSlopeThresholds CompareSlope(float normalUpDot, ZonePhysicsLivingMovementSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings); Finite(normalUpDot, nameof(normalUpDot));
        return new(normalUpDot < settings.MinimumSlideCosine, normalUpDot < settings.MaximumClimbCosine,
            normalUpDot < settings.MinimumFallCosine, normalUpDot > settings.MinimumFallCosine);
    }

    /// <summary>v-n*dot(n,v), preserving the original X/Y then Z scalar dot-product order.</summary>
    public static Vector3 ProjectOntoPlane(Vector3 value, Vector3 unitNormal)
    {
        ZoneManagedTerrainGeometry.ValidateFinite(value, nameof(value));
        ZoneManagedTerrainGeometry.ValidateFinite(unitNormal, nameof(unitNormal));
        var squared = Dot(unitNormal, unitNormal);
        if (!float.IsFinite(squared) || MathF.Abs(squared - 1) > .001f)
            throw new ArgumentOutOfRangeException(nameof(unitNormal), "Living ground projection requires a unit normal; it is not silently normalized.");
        var dot = Dot(unitNormal, value);
        var result = new Vector3(value.X - unitNormal.X * dot, value.Y - unitNormal.Y * dot, value.Z - unitNormal.Z * dot);
        ZoneManagedTerrainGeometry.ValidateFinite(result, nameof(value));
        return result;
    }

    /// <summary>
    /// Strict final foot-rise window from 3502DED0. The floor-minus-rise subtraction is kept in
    /// source order so an exact boundary near large zone coordinates cannot be admitted by rounding.
    /// Normal/flag/support ownership gates are separate from this height-only predicate.
    /// </summary>
    public static bool CanRaiseToSupport(float footHeight, float supportHeight, ZonePhysicsLivingMovementSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Finite(footHeight, nameof(footHeight)); Finite(supportHeight, nameof(supportHeight));
        return footHeight < supportHeight && footHeight > supportHeight - settings.SupportHeight;
    }

    /// <summary>3502CA20: entityOrigin+(hc-h-hp)*up, displaced by -(1.5*(hc-h)+2)*up.</summary>
    public static ZonePhysicsLivingSupportRay CreateSupportRay(Vector3 entityOrigin, ZonePhysicsLivingMovementSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ZoneManagedTerrainGeometry.ValidateFinite(entityOrigin, nameof(entityOrigin));
        var origin = entityOrigin + new Vector3(0, 0, settings.SupportRayStartOffsetZ);
        var displacement = new Vector3(0, 0, -settings.SupportRayLength);
        ZoneManagedTerrainGeometry.ValidateSegment(origin, displacement);
        return new(origin, displacement);
    }

    /// <summary>
    /// 35027E10 entity-admission envelope. Its upper bound uses raw segment height without the
    /// capsule hemisphere, and adds world Z only after the local height/pivot arithmetic.
    /// This admits whole entity bounds; it is never a clip box for their individual triangles.
    /// </summary>
    public static ZonePhysicsQueryBounds CreateStanceCandidateBounds(Vector3 entityOrigin, ZonePhysicsLivingMovementSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return CreateStanceCandidateBounds(entityOrigin, settings.Radius, settings.SegmentHalfHeight,
            settings.HeightCollider, settings.HeightPivot);
    }

    public static ZonePhysicsQueryBounds CreateStanceCandidateBounds(Vector3 entityOrigin, float rawRadius,
        float rawH, float newHC, float pivotForThisPass)
    {
        ZoneManagedTerrainGeometry.ValidateFinite(entityOrigin, nameof(entityOrigin));
        Finite(rawRadius, nameof(rawRadius)); Finite(rawH, nameof(rawH));
        Finite(newHC, nameof(newHC)); Finite(pivotForThisPass, nameof(pivotForThisPass));
        if (rawRadius <= 0 || rawH < 0) throw new ArgumentOutOfRangeException(nameof(rawRadius), "Stance geometry requires a positive radius and nonnegative raw half-height.");
        var upperOffset = (newHC + rawH) - pivotForThisPass;
        return new(new(entityOrigin.X - rawRadius, entityOrigin.Y - rawRadius, entityOrigin.Z - pivotForThisPass),
            new(entityOrigin.X + rawRadius, entityOrigin.Y + rawRadius, upperOffset + entityOrigin.Z));
    }

    /// <summary>
    /// Exact 35027BB0 portion only. Caller supplies the selected inertia and the original prior
    /// flying/current support-entity/flag4 inputs; no controller state or flag defaults are invented.
    /// Air-control preparation and collision/position integration occur elsewhere in the step.
    /// </summary>
    public static ZonePhysicsLivingAirVelocity ApplyAirGravityAndResistance(Vector3 actualVelocity,
        Vector3 requestedVelocity, Vector3 gravity, Vector3 departedSupportVelocity, float effectiveInertia,
        float seconds, ZonePhysicsLivingMovementSettings settings, bool wasFlying, bool hasSupportEntity,
        bool livingFlag4)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ZoneManagedTerrainGeometry.ValidateFinite(actualVelocity, nameof(actualVelocity));
        ZoneManagedTerrainGeometry.ValidateFinite(requestedVelocity, nameof(requestedVelocity));
        ZoneManagedTerrainGeometry.ValidateFinite(gravity, nameof(gravity));
        ZoneManagedTerrainGeometry.ValidateFinite(departedSupportVelocity, nameof(departedSupportVelocity));
        Finite(effectiveInertia, nameof(effectiveInertia)); Finite(seconds, nameof(seconds));
        if (seconds <= 0) throw new ArgumentOutOfRangeException(nameof(seconds), "Living air response requires a positive explicit period.");
        var inherited = !wasFlying && !hasSupportEntity;
        var velocity = inherited ? actualVelocity + departedSupportVelocity : actualVelocity;
        var acceleration = gravity;
        if (livingFlag4)
            acceleration += new Vector3((requestedVelocity.X - velocity.X) * effectiveInertia,
                (requestedVelocity.Y - velocity.Y) * effectiveInertia, (requestedVelocity.Z - velocity.Z) * effectiveInertia);
        velocity = new(acceleration.X * seconds + velocity.X, acceleration.Y * seconds + velocity.Y, acceleration.Z * seconds + velocity.Z);
        var resistance = settings.AirResistance;
        var drag = new Vector3((-velocity.X * resistance) * seconds, (-velocity.Y * resistance) * seconds, (-velocity.Z * resistance) * seconds);
        var velocitySquared = Dot(velocity, velocity); var dragSquared = Dot(drag, drag);
        if (!float.IsFinite(velocitySquared) || !float.IsFinite(dragSquared))
            throw new InvalidOperationException("Managed living air response exceeded finite velocity precision.");
        var stopped = false;
        if (dragSquared >= (double)velocitySquared * 4)
        {
            if (Dot(drag, velocity) < 0) { velocity = Vector3.Zero; stopped = true; }
        }
        else velocity += drag;
        var finalSquared = Dot(velocity, velocity);
        if (!float.IsFinite(finalSquared)) throw new InvalidOperationException("Managed living air response produced a non-finite velocity.");
        if (finalSquared < 1e-10) velocity = Vector3.Zero;
        return new(velocity, inherited, stopped);
    }

    internal static float Dot(Vector3 a, Vector3 b) => (a.X * b.X + a.Y * b.Y) + a.Z * b.Z;
    private static void Finite(float value, string name)
    {
        if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(name, "Living movement inputs must be finite.");
    }
}
