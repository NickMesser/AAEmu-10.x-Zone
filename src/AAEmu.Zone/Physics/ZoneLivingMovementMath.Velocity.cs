using System.Numerics;

namespace AAEmu.Zone.Physics;

/// <summary>Velocity before the original displacement/sweep and world speed-limit stages.</summary>
public readonly record struct ZonePhysicsLivingPreparedVelocity(Vector3 Velocity, float EffectiveInertia,
    bool OverrodeWithRequestedVelocity);

/// <summary>Optional landing-recovery scalar writes; the caller owns their state and later suspension response.</summary>
public readonly record struct ZonePhysicsLivingLandingResponse(float UpVelocity, float Acceleration);

/// <summary>Copied 3502DED0 landing preparation; pose correction remains an explicit caller-owned action.</summary>
public readonly record struct ZonePhysicsLivingGroundContactVelocity(Vector3 Velocity, float? RaiseAlongUp,
    ZonePhysicsLivingLandingResponse? LandingResponse, bool RequestsFlying, bool ZeroedOnLanding);

/// <summary>Copied grounded velocity after relaxation and slope guards, with pre-guard acceleration.</summary>
public readonly record struct ZonePhysicsLivingGroundVelocity(Vector3 Velocity, Vector3 Acceleration,
    bool AppliedSlideGravity, bool ClimbLimited, bool RequestsFlying, bool StoppedByReversal);

public static partial class ZoneLivingMovementMath
{
    /// <summary>3502DED0 selects before decrementing the impulse-recovery timer.</summary>
    public static float SelectEffectiveInertia(Vector3 requestedVelocity, float impulseRecoveryTimer,
        ZonePhysicsLivingMovementSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ZoneManagedTerrainGeometry.ValidateFinite(requestedVelocity, nameof(requestedVelocity));
        Finite(impulseRecoveryTimer, nameof(impulseRecoveryTimer));
        if (impulseRecoveryTimer > .0001f) return 6;
        return settings.InertiaAcceleration != 0 && Squared(requestedVelocity) > .1f
            ? settings.InertiaAcceleration : settings.Inertia;
    }

    /// <summary>
    /// 3502DED0 preparation through the one-shot mode3 override, before the world speed cap.
    /// Flags/idle eligibility/timer mutation are supplied by the controller. In particular the
    /// air increment uses BASE inertia, not the selected impulse/acceleration inertia.
    /// </summary>
    public static ZonePhysicsLivingPreparedVelocity PrepareVelocity(Vector3 actualVelocity,
        Vector3 requestedVelocity, Vector3 gravity, Vector3 cachedGroundNormal, float seconds,
        ZonePhysicsLivingMovementSettings settings, bool wasFlying, bool livingFlag2,
        bool forceRequestedVelocity, float impulseRecoveryTimer)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ValidateVectors(actualVelocity, requestedVelocity, gravity);
        ZoneManagedTerrainGeometry.ValidateFinite(cachedGroundNormal, nameof(cachedGroundNormal));
        Period(seconds);
        var inertia = SelectEffectiveInertia(requestedVelocity, impulseRecoveryTimer, settings);
        var velocity = actualVelocity;
        if (!wasFlying)
        {
            if (inertia == 0 && !livingFlag2) velocity = ProjectOntoPlane(requestedVelocity, cachedGroundNormal);
        }
        else if (settings.AirControl > 0)
        {
            if (settings.Inertia <= 0)
            {
                velocity = requestedVelocity;
                var gravitySquared = Squared(gravity);
                if (gravitySquared > 0)
                {
                    var difference = Dot(actualVelocity, gravity) - Dot(requestedVelocity, gravity);
                    var inverse = (float)(1.0 / gravitySquared);
                    velocity += new Vector3((gravity.X * difference) * inverse,
                        (gravity.Y * difference) * inverse, (gravity.Z * difference) * inverse);
                }
            }
            else
            {
                var amount = (settings.Inertia * seconds) * settings.AirControl;
                var dx = requestedVelocity.X - actualVelocity.X;
                var dy = requestedVelocity.Y - actualVelocity.Y;
                var dz = requestedVelocity.Z - actualVelocity.Z;
                var x = AirIncrement(dx, requestedVelocity.X * amount);
                var y = AirIncrement(dy, requestedVelocity.Y * amount);
                var z = AirIncrement(dz, requestedVelocity.Z * amount);
                if (settings.AirControl - 1 >= 0) { x = dx; y = dy; }
                velocity = new(actualVelocity.X + x, actualVelocity.Y + y, actualVelocity.Z + z);
            }
        }
        if (forceRequestedVelocity) velocity = requestedVelocity;
        ZoneManagedTerrainGeometry.ValidateFinite(velocity, nameof(actualVelocity));
        return new(velocity, inertia, forceRequestedVelocity);
    }

    /// <summary>
    /// 3502DED0 landing branch before grounded relaxation. The caller must already have chosen
    /// the grounded response path. supportOnlyGrounded is the strict contact-band result, not
    /// the separate blocked flag. Suppression maps to living flag0x20; the landing coefficient
    /// is an explicit copied input. No suspension or pose change is applied inside this helper.
    /// </summary>
    public static ZonePhysicsLivingGroundContactVelocity PrepareGroundContact(Vector3 actualVelocity,
        Vector3 requestedVelocity, Vector3 groundNormal, Vector3 up, float entityUpCoordinate,
        float groundCoordinate, ZonePhysicsLivingMovementSettings settings, bool wasFlying,
        bool livingFlag4, bool blocked, bool supportOnlyGrounded, bool suppressLandingResponse,
        float minimumLandingAcceleration)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ValidateVectors(actualVelocity, requestedVelocity, groundNormal); Unit(up, nameof(up)); Unit(groundNormal, nameof(groundNormal));
        Finite(entityUpCoordinate, nameof(entityUpCoordinate)); Finite(groundCoordinate, nameof(groundCoordinate));
        Finite(minimumLandingAcceleration, nameof(minimumLandingAcceleration));
        if (!wasFlying || livingFlag4) return new(actualVelocity, null, null, false, false);
        var velocity = actualVelocity; var upVelocity = Dot(up, velocity);
        ZonePhysicsLivingLandingResponse? landing = null;
        if (upVelocity < -4 && !suppressLandingResponse && minimumLandingAcceleration > 0)
        {
            var acceleration = (upVelocity * upVelocity) / (settings.SupportHalfHeight * 2);
            var sum = acceleration + minimumLandingAcceleration;
            acceleration = (float)(Math.Abs((double)(acceleration - minimumLandingAcceleration)) + sum) * .5f;
            Finite(acceleration, nameof(minimumLandingAcceleration));
            landing = new(upVelocity, acceleration);
        }
        var normalUp = Dot(up, groundNormal);
        var zero = false;
        if (normalUp >= settings.MinimumFallCosine || upVelocity >= -4 || blocked)
        {
            if (normalUp >= settings.MinimumFallCosine &&
                (supportOnlyGrounded && Squared(requestedVelocity) <= 0 || normalUp <= settings.MaximumClimbCosine && upVelocity >= 0))
            { velocity = Vector3.Zero; zero = true; }
            else velocity = ProjectOntoPlane(velocity, groundNormal);
            var foot = entityUpCoordinate - settings.HeightPivot;
            float? raise = CanRaiseToSupport(foot, groundCoordinate, settings)
                ? (groundCoordinate + settings.HeightPivot) - entityUpCoordinate : null;
            return new(velocity, raise, landing, false, zero);
        }
        velocity = ProjectOntoPlane(velocity, groundNormal);
        var upwards = Dot(up, velocity);
        if (upwards > 0) velocity -= up * upwards;
        return new(velocity, null, landing, true, false);
    }

    /// <summary>
    /// 3502DED0 grounded relaxation/slope response after movement and landing preparation.
    /// actualVelocity is already collision/landing constrained; requestedVelocity is the raw
    /// independent wish. poseUpDisplacement is measured after movement against the source pose
    /// anchor. This helper neither changes pose nor chooses the grounded/airborne branch.
    /// </summary>
    public static ZonePhysicsLivingGroundVelocity ApplyGroundVelocityResponse(Vector3 actualVelocity,
        Vector3 requestedVelocity, Vector3 gravity, Vector3 groundNormal, Vector3 up, float seconds,
        float effectiveInertia, ZonePhysicsLivingMovementSettings settings, bool livingFlag4,
        bool blocked, float poseUpDisplacement)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ValidateVectors(actualVelocity, requestedVelocity, gravity); Unit(groundNormal, nameof(groundNormal)); Unit(up, nameof(up));
        Period(seconds); Finite(effectiveInertia, nameof(effectiveInertia)); Finite(poseUpDisplacement, nameof(poseUpDisplacement));
        var target = livingFlag4 ? requestedVelocity : ProjectOntoPlane(requestedVelocity, groundNormal);
        var coefficient = effectiveInertia * seconds <= 1 ? effectiveInertia : (float)(1.0 / seconds);
        var delta = target - actualVelocity;
        var acceleration = delta * coefficient;
        var correction = acceleration * seconds;
        var deltaSquared = Squared(delta);
        if (Dot(delta, correction) > deltaSquared)
        {
            var length = MathF.Sqrt(deltaSquared);
            if (length > 0)
            {
                var unit = delta * (float)(1.0 / length);
                var along = Dot(unit, correction);
                var inversePeriod = (float)(1.0 / seconds);
                acceleration = new((correction.X - along * unit.X + delta.X) * inversePeriod,
                    (correction.Y - along * unit.Y + delta.Y) * inversePeriod,
                    (correction.Z - along * unit.Z + delta.Z) * inversePeriod);
            }
        }
        var normalUp = Dot(up, groundNormal);
        var slide = normalUp < settings.MinimumSlideCosine && !livingFlag4;
        if (slide) acceleration += ProjectOntoPlane(gravity, groundNormal);
        var velocity = new Vector3(acceleration.X * seconds + actualVelocity.X,
            acceleration.Y * seconds + actualVelocity.Y, acceleration.Z * seconds + actualVelocity.Z);
        var stopped = Dot(velocity, actualVelocity) < 0 && Dot(requestedVelocity, velocity) <= 0;
        if (stopped) velocity = Vector3.Zero;
        var climbLimited = false;
        if (normalUp < settings.MaximumClimbCosine)
        {
            var upVelocity = Dot(up, velocity);
            if (upVelocity > 0 && Dot(acceleration, up) > 0)
            { velocity -= up * upVelocity; climbLimited = true; }
            if (poseUpDisplacement > settings.SupportHalfHeight * .001f)
            { velocity -= up * Dot(up, velocity); climbLimited = true; }
        }
        var flying = normalUp < settings.MinimumFallCosine && !blocked;
        if (flying) velocity += groundNormal - up * normalUp;
        var squared = Squared(velocity);
        if (Squared(requestedVelocity) == 0 && squared < .001f || squared < .0001f) velocity = Vector3.Zero;
        ZoneManagedTerrainGeometry.ValidateFinite(velocity, nameof(actualVelocity));
        ZoneManagedTerrainGeometry.ValidateFinite(acceleration, nameof(actualVelocity));
        return new(velocity, acceleration, slide, climbLimited, flying, stopped);
    }

    private static float AirIncrement(float difference, float proposed)
    {
        if (-(difference * proposed) >= 0) proposed = 0;
        var sum = difference + proposed;
        var absolute = Math.Abs((double)(difference - proposed));
        var combined = (float)(difference >= 0 ? sum - absolute : absolute + sum);
        return combined * .5f;
    }
    private static void ValidateVectors(Vector3 first, Vector3 second, Vector3 third)
    {
        ZoneManagedTerrainGeometry.ValidateFinite(first, nameof(first));
        ZoneManagedTerrainGeometry.ValidateFinite(second, nameof(second));
        ZoneManagedTerrainGeometry.ValidateFinite(third, nameof(third));
    }
    private static void Unit(Vector3 value, string name)
    {
        ZoneManagedTerrainGeometry.ValidateFinite(value, name);
        if (MathF.Abs(Squared(value) - 1) > .001f)
            throw new ArgumentOutOfRangeException(name, "Living velocity response requires copied unit directions; it does not normalize them.");
    }
    private static float Squared(Vector3 value)
    {
        var squared = Dot(value, value);
        if (!float.IsFinite(squared)) throw new InvalidOperationException("Managed living velocity response exceeds finite scalar precision.");
        return squared;
    }
    private static void Period(float seconds)
    {
        Finite(seconds, nameof(seconds));
        if (seconds <= 0) throw new ArgumentOutOfRangeException(nameof(seconds), "Living velocity response requires a positive explicit period.");
    }
}
