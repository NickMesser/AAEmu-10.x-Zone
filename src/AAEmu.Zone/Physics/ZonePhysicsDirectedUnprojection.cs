using System.Numerics;

namespace AAEmu.Zone.Physics;

/// <summary>An optional copied candidate-admission box, independent of the query shape's actual outer bounds.</summary>
public readonly record struct ZonePhysicsQueryBounds
{
    public ZonePhysicsQueryBounds(Vector3 minimum, Vector3 maximum)
    {
        ZoneManagedTerrainGeometry.ValidateFinite(minimum, nameof(minimum));
        ZoneManagedTerrainGeometry.ValidateFinite(maximum, nameof(maximum));
        if (minimum.X > maximum.X || minimum.Y > maximum.Y || minimum.Z > maximum.Z)
            throw new ArgumentException("Candidate bounds must be ordered.");
        Minimum = minimum; Maximum = maximum;
    }
    public Vector3 Minimum { get; }
    public Vector3 Maximum { get; }
    internal ZoneManagedBounds Managed => new(Minimum, Maximum);
}

/// <summary>
/// Copied exit along fixed +Z for an initially intersecting individual target. Depth is not an
/// unconstrained MTD, and includes no controller clearance or 1.01 iteration multiplier.
/// NormalFallback is the same target's independent positive normal/MTD solution, never its
/// projection onto +Z. This geometry record does not infer native contact order or fallback gates.
/// </summary>
/// <remarks>
/// Zero SeparationTolerance identifies an analytic exit. Convex numerical exits are certified on
/// the clear side within a world normal-separation band; that band is not a travel-distance-error
/// bound near grazing contact. Unreliable numerical progress throws rather than inventing a depth.
/// </remarks>
public readonly record struct ZonePhysicsDirectedUnprojection
{
    public ZonePhysicsDirectedUnprojection(float depth, Vector3 position, ushort physicalMaterialId,
        bool isSurfaceContact, float separationTolerance, ZonePhysicsPenetration normalFallback)
    {
        if (!float.IsFinite(depth) || depth <= 0) throw new ArgumentOutOfRangeException(nameof(depth));
        ZoneManagedTerrainGeometry.ValidateFinite(position, nameof(position));
        if (!float.IsFinite(separationTolerance) || separationTolerance < 0)
            throw new ArgumentOutOfRangeException(nameof(separationTolerance));
        if (!float.IsFinite(normalFallback.Depth) || normalFallback.Depth <= 0)
            throw new ArgumentException("Directed exits require the same initial target's positive normal solution.", nameof(normalFallback));
        Depth = depth; Position = position; PhysicalMaterialId = physicalMaterialId;
        IsSurfaceContact = isSurfaceContact; SeparationTolerance = separationTolerance; NormalFallback = normalFallback;
    }
    public float Depth { get; }
    public Vector3 Direction => Vector3.UnitZ;
    public Vector3 Position { get; }
    public ushort PhysicalMaterialId { get; }
    public bool IsSurfaceContact { get; }
    public float SeparationTolerance { get; }
    public ZonePhysicsPenetration NormalFallback { get; }
    public float NormalFallbackDepth => NormalFallback.Depth;
    public Vector3 NormalFallbackDirection => NormalFallback.Normal;
}
