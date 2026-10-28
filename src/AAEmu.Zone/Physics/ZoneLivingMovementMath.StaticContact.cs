using System.Numerics;

namespace AAEmu.Zone.Physics;

/// <summary>Copied impulse and resulting actual velocity for one eligible immovable contact.</summary>
public readonly record struct ZonePhysicsLivingStaticContactImpulse(Vector3 Velocity, Vector3 Impulse,
    float InwardNormalVelocity, bool WasCapped);

public static partial class ZoneLivingMovementMath
{
    /// <summary>
    /// 3502DED0 static-contact impulse, after the caller has admitted its velocity-response gate.
    /// The target is immovable and the living response matrix is inverseMass times identity.
    /// This does not choose contact order, slide displacement, or apply moving-body impulses.
    /// The strict impulse cap can halve a large normal response; small contacts retain 1.01
    /// restitution of their inward normal speed instead of an unconditional half projection.
    /// </summary>
    public static ZonePhysicsLivingStaticContactImpulse ApplyStaticContactImpulse(Vector3 actualVelocity,
        Vector3 normal, float mass)
    {
        ZoneManagedTerrainGeometry.ValidateFinite(actualVelocity, nameof(actualVelocity));
        Unit(normal, nameof(normal));
        Finite(mass, nameof(mass));
        if (mass <= 0) throw new ArgumentOutOfRangeException(nameof(mass), "A living contact requires positive mass.");
        // 3502985F..3502986B use DIVSS for the copied body584 inverse mass.
        var inverseMass = 1f / mass;
        Finite(inverseMass, nameof(mass));

        var along = Dot(actualVelocity, normal);
        Finite(along, nameof(actualVelocity));
        var inward = (along - MathF.Abs(-along)) * .5f;
        Finite(inward, nameof(actualVelocity));
        // Preserve n^T M n and its separately rounded products. Replacing this with
        // inverseMass loses the copied normal's float-rounding effect at the cap boundary.
        var responseX = normal.X * (normal.X * inverseMass);
        var responseY = (inverseMass * normal.Y) * normal.Y;
        var responseZ = (inverseMass * normal.Z) * normal.Z;
        var response = (responseX + responseY) + responseZ;
        var responseUpper = response + 1e-10f;
        var denominator = (MathF.Abs(1e-10f - response) + responseUpper) * .5f;
        Finite(denominator, nameof(mass));
        if (denominator <= 0) throw new InvalidOperationException("Living static contact has no positive finite response denominator.");
        // The original binary uses MULSS/DIVSS here, despite double-looking literals
        // in the decompilation (3503469F..350346CA).
        var magnitude = (inward * -1.01f) / denominator;
        var impulse = new Vector3(normal.X * magnitude, normal.Y * magnitude, normal.Z * magnitude);
        ZoneManagedTerrainGeometry.ValidateFinite(impulse, nameof(actualVelocity));
        var impulseSquared = Squared(impulse);
        var inverseMassSquared = inverseMass * inverseMass;
        var velocityImpulseSquared = impulseSquared * inverseMassSquared;
        var inwardSquared = inward * inward;
        var maximumUpper = inwardSquared + 1f;
        var maximum = (MathF.Abs(1f - inwardSquared) + maximumUpper) * .5f;
        Finite(velocityImpulseSquared, nameof(actualVelocity));
        Finite(maximum, nameof(actualVelocity));
        var capped = velocityImpulseSquared > maximum;
        if (capped)
        {
            var cappedMagnitude = (MathF.Abs(inward) * mass) * .5f;
            var reciprocalLength = 1f / MathF.Sqrt(impulseSquared);
            impulse = new((impulse.X * reciprocalLength) * cappedMagnitude,
                (impulse.Y * reciprocalLength) * cappedMagnitude, (impulse.Z * reciprocalLength) * cappedMagnitude);
        }
        var velocity = new Vector3(actualVelocity.X + impulse.X * inverseMass,
            actualVelocity.Y + impulse.Y * inverseMass, actualVelocity.Z + impulse.Z * inverseMass);
        ZoneManagedTerrainGeometry.ValidateFinite(impulse, nameof(actualVelocity));
        ZoneManagedTerrainGeometry.ValidateFinite(velocity, nameof(actualVelocity));
        return new(velocity, impulse, inward, capped);
    }
}
