using AAEmu.Zone.Actors;

namespace AAEmu.Zone.Physics;

/// <summary>
/// Immutable values copied from a fresh living body's dimensions/dynamics. Angles retain their
/// authored degrees and the original double-cosine conversion. This record supplies verified
/// thresholds and dimensions; it does not itself implement suspension, stance changes, moving
/// supports, air control or the living controller's state/flag ordering.
/// </summary>
public sealed class ZonePhysicsLivingMovementSettings
{
    // 35025530 stores these exact degree bits, including the nonintegral-looking 54-degree value.
    public const float DefaultMinimumSlideAngleDegrees = 36;
    public const float DefaultMaximumClimbAngleDegrees = 54.000003814697265625f;
    public const float DefaultMaximumJumpAngleDegrees = DefaultMaximumClimbAngleDegrees;
    public const float DefaultMinimumFallAngleDegrees = 70.1999969482421875f;
    public const float DefaultMaximumSupportVelocity = 10;

    private ZonePhysicsLivingMovementSettings(ZoneActorPhysicalizationStance stance,
        float minimumSlideAngle, float maximumClimbAngle, float maximumJumpAngle, float minimumFallAngle,
        float maximumSupportVelocity, float inertia, float inertiaAcceleration, float airControl, float airResistance,
        float impulseRecoveryTime, float maximumUnprojectionDistance, float minimumLandingAcceleration = 60)
    {
        ArgumentNullException.ThrowIfNull(stance);
        RequireFinite(stance.HeightCollider, nameof(stance.HeightCollider));
        RequireFinite(stance.HeightPivot, nameof(stance.HeightPivot));
        RequireFinite(stance.Size.X, nameof(stance.Size)); RequireFinite(stance.Size.Z, nameof(stance.Size));
        RequireFinite(stance.MaximumSpeed, nameof(stance.MaximumSpeed));
        if (stance.Size.X <= 0 || stance.Size.Z < 0)
            throw new NotSupportedException("Managed living movement requires a positive radius and nonnegative raw segment half-height.");
        RequireFinite(maximumSupportVelocity, nameof(maximumSupportVelocity));
        RequireFinite(inertia, nameof(inertia)); RequireFinite(inertiaAcceleration, nameof(inertiaAcceleration));
        RequireFinite(airControl, nameof(airControl)); RequireFinite(airResistance, nameof(airResistance));
        RequireFinite(impulseRecoveryTime, nameof(impulseRecoveryTime));
        RequireFinite(maximumUnprojectionDistance, nameof(maximumUnprojectionDistance));
        RequireFinite(minimumLandingAcceleration, nameof(minimumLandingAcceleration));

        MinimumSlideAngleDegrees = minimumSlideAngle;
        MaximumClimbAngleDegrees = maximumClimbAngle;
        MaximumJumpAngleDegrees = maximumJumpAngle;
        MinimumFallAngleDegrees = minimumFallAngle;
        MinimumSlideCosine = ZoneLivingMovementMath.DegreesToCosine(minimumSlideAngle);
        MaximumClimbCosine = ZoneLivingMovementMath.DegreesToCosine(maximumClimbAngle);
        MaximumJumpCosine = ZoneLivingMovementMath.DegreesToCosine(maximumJumpAngle);
        MinimumFallCosine = ZoneLivingMovementMath.DegreesToCosine(minimumFallAngle);
        MaximumSupportVelocity = maximumSupportVelocity;
        Inertia = inertia; InertiaAcceleration = inertiaAcceleration;
        AirControl = airControl; AirResistance = airResistance; ImpulseRecoveryTime = impulseRecoveryTime;
        HeightCollider = stance.HeightCollider; HeightPivot = stance.HeightPivot;
        Radius = stance.Size.X; SegmentHalfHeight = stance.Size.Z; UseCapsule = stance.UseCapsule;
        StanceMaximumSpeed = stance.MaximumSpeed;
        MaximumUnprojectionDistance = maximumUnprojectionDistance;
        MinimumLandingAcceleration = minimumLandingAcceleration;
        // 35028BF0 first passes RAW Z to the primitive setter, then separately stores body552.
        // Preserve its expression rather than changing the actual collider to this support size.
        var upper = (float)((double)SegmentHalfHeight + .001);
        var sum = (float)(Math.Abs((double)SegmentHalfHeight - .001) + upper);
        SupportHalfHeight = sum * .5f;
        SupportHeight = HeightCollider - SupportHalfHeight;
        SupportRayStartOffsetZ = SupportHeight - HeightPivot;
        var rayDisplacement = (float)((double)SupportHeight * -1.5) - 2f;
        SupportRayLength = -rayDisplacement;
        RequireFinite(SupportHalfHeight, nameof(SupportHalfHeight));
        RequireFinite(SupportHeight, nameof(SupportHeight));
        RequireFinite(SupportRayStartOffsetZ, nameof(SupportRayStartOffsetZ));
        RequireFinite(SupportRayLength, nameof(SupportRayLength));
    }

    public float MinimumSlideAngleDegrees { get; }
    public float MaximumClimbAngleDegrees { get; }
    /// <summary>Retained dynamics metadata; these helpers do not implement the jump action gate.</summary>
    public float MaximumJumpAngleDegrees { get; }
    public float MinimumFallAngleDegrees { get; }
    public float MinimumSlideCosine { get; }
    public float MaximumClimbCosine { get; }
    public float MaximumJumpCosine { get; }
    public float MinimumFallCosine { get; }
    /// <summary>3502B3F0 caps retained support-platform velocity, not the actor's requested speed.</summary>
    public float MaximumSupportVelocity { get; }
    /// <summary>Copied dynamics coefficients. Complete velocity relaxation/state gates are separate.</summary>
    public float Inertia { get; }
    public float InertiaAcceleration { get; }
    public float AirControl { get; }
    public float AirResistance { get; }
    /// <summary>Copied duration only; impulse/fall recovery timers require explicit controller state.</summary>
    public float ImpulseRecoveryTime { get; }
    public float HeightCollider { get; }
    public float HeightPivot { get; }
    public float Radius { get; }
    /// <summary>Raw primitive axial half-length. Capsule hemispheres extend beyond this segment.</summary>
    public float SegmentHalfHeight { get; }
    public bool UseCapsule { get; }
    /// <summary>Separate game-stance metadata; MaximumSupportVelocity never replaces this value.</summary>
    public float StanceMaximumSpeed { get; }
    /// <summary>Body552 support size after its scalar clamp; the primitive retains raw SegmentHalfHeight.</summary>
    public float SupportHalfHeight { get; }
    /// <summary>hc-h used by the original support probe/rise constraints, not an unconditional stair allowance.</summary>
    public float SupportHeight { get; }
    public float SupportRayStartOffsetZ { get; }
    /// <summary>Signed downward distance 1.5*(hc-h)+2; unusual dimensions can make it nonpositive.</summary>
    public float SupportRayLength { get; }
    /// <summary>Dimension-update unprojection bound; not a walking step height.</summary>
    public float MaximumUnprojectionDistance { get; }
    /// <summary>Body576 minimum landing-recovery acceleration; fresh constructor value is 60.</summary>
    public float MinimumLandingAcceleration { get; }

    /// <summary>Supplied stance dimensions with original 35025530 dynamics constructor defaults.</summary>
    public static ZonePhysicsLivingMovementSettings FromStance(ZoneActorPhysicalizationStance stance)
    {
        ArgumentNullException.ThrowIfNull(stance);
        return new(stance, DefaultMinimumSlideAngleDegrees, DefaultMaximumClimbAngleDegrees,
            DefaultMaximumJumpAngleDegrees, DefaultMinimumFallAngleDegrees, DefaultMaximumSupportVelocity,
            8, 0, .1f, 0, 5, stance.HeightPivot <= 1 ? 1 : stance.HeightPivot);
    }

    /// <summary>Copies a type1 stance update while retaining this body's resolved type4 dynamics.</summary>
    public ZonePhysicsLivingMovementSettings WithStance(ZoneActorPhysicalizationStance stance)
    {
        ArgumentNullException.ThrowIfNull(stance);
        return new(stance, MinimumSlideAngleDegrees, MaximumClimbAngleDegrees, MaximumJumpAngleDegrees,
            MinimumFallAngleDegrees, MaximumSupportVelocity, Inertia, InertiaAcceleration, AirControl,
            AirResistance, ImpulseRecoveryTime, stance.HeightPivot <= 1 ? 1 : stance.HeightPivot,
            MinimumLandingAcceleration);
    }

    /// <summary>
    /// Snapshots the defined initial request. The original unused-float mask retains fresh-body
    /// constructor values; unrelated NaNs/infinities refuse explicitly. Finite angles are neither
    /// clamped nor reordered. No mutable request block or content object is retained.
    /// </summary>
    public static ZonePhysicsLivingMovementSettings FromRequest(ZoneActorPhysicalizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var dimensions = request.Dimensions;
        var stance = request.Stance with
        {
            Size = new(Required(dimensions, 0x0C, "radius"), request.Stance.Size.Y,
                Required(dimensions, 0x14, "raw segment half-height")),
            HeightCollider = Required(dimensions, 0x18, "collider height"),
            HeightPivot = Required(dimensions, 0x04, "pivot height"),
            UseCapsule = (dimensions.ReadUInt32(0x34) ?? throw Missing("capsule selector")) != 0,
        };
        var dynamics = request.Dynamics;
        return new(stance,
            Dynamic(dynamics, 0x30, DefaultMinimumSlideAngleDegrees, "minimum slide angle"),
            Dynamic(dynamics, 0x34, DefaultMaximumClimbAngleDegrees, "maximum climb angle"),
            Dynamic(dynamics, 0x38, DefaultMaximumJumpAngleDegrees, "maximum jump angle"),
            Dynamic(dynamics, 0x3C, DefaultMinimumFallAngleDegrees, "minimum fall angle"),
            Dynamic(dynamics, 0x40, DefaultMaximumSupportVelocity, "maximum support velocity"),
            Dynamic(dynamics, 0x04, 8, "inertia"), Dynamic(dynamics, 0x08, 0, "inertia acceleration"),
            Dynamic(dynamics, 0x0C, .1f, "air control"), Dynamic(dynamics, 0x10, 0, "air resistance"),
            Dynamic(dynamics, 0x44, 5, "impulse recovery time"),
            Required(dimensions, 0x30, "maximum unprojection distance"),
            Dynamic(dynamics, 0x20, 60, "minimum landing acceleration"));
    }

    /// <summary>Type4 inertia-only update; all dimensions, thresholds and other dynamics stay copied.</summary>
    public ZonePhysicsLivingMovementSettings WithInertia(float inertia) => WithInertia(inertia, InertiaAcceleration);

    /// <summary>Original type4 independent inertia/ground-acceleration writes; other coefficients stay copied.</summary>
    public ZonePhysicsLivingMovementSettings WithInertia(float inertia, float inertiaAcceleration) => new(
        new(new(Radius, Radius, SegmentHalfHeight), default, default, HeightCollider, HeightPivot,
            StanceMaximumSpeed, UseCapsule, false),
        MinimumSlideAngleDegrees, MaximumClimbAngleDegrees, MaximumJumpAngleDegrees,
        MinimumFallAngleDegrees, MaximumSupportVelocity, inertia, inertiaAcceleration,
        AirControl, AirResistance, ImpulseRecoveryTime, MaximumUnprojectionDistance, MinimumLandingAcceleration);

    private static float Dynamic(ZoneActorPhysicsParameterFields fields, int offset, float fallback, string name)
    {
        var bits = fields.ReadUInt32(offset) ?? throw Missing(name);
        if ((bits & 0xFFA00000) == 0xFFA00000) return fallback;
        var value = BitConverter.UInt32BitsToSingle(bits); RequireFinite(value, name); return value;
    }
    private static float Required(ZoneActorPhysicsParameterFields fields, int offset, string name)
    {
        var value = fields.ReadSingle(offset) ?? throw Missing(name); RequireFinite(value, name); return value;
    }
    private static NotSupportedException Missing(string name) =>
        new($"Managed living movement requires the initial request's defined {name}.");
    private static void RequireFinite(float value, string name)
    {
        if (!float.IsFinite(value)) throw new NotSupportedException($"Managed living movement does not support a non-finite {name}.");
    }
}
