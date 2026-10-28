using System.Numerics;

namespace AAEmu.Zone.Physics;

/// <summary>
/// Copied overlap unprojection. Interior face/solid solutions reach touch at Normal * Depth;
/// a terrain boundary constraint can conservatively pass its finite-edge exit. No controller
/// clearance is included. Normal is the chosen query unprojection direction;
/// it need not equal the target's authored surface normal. General convex numerical solutions
/// use the kernel's documented depth tolerance.
/// </summary>
/// <remarks>
/// Surface contacts describe a two-sided triangle/verified coplanar patch, or the terrain's
/// explicitly positive authored-face-normal constraint. Terrain boundary constraints are
/// conservative and never stand in for a whole-terrain/finite-edge MTD. Internal patch edges are consolidated only when an
/// authored face contains the resulting touch point. A solid contact describes a whole closed convex volume's minimum
/// translation. IsAmbiguous identifies nonunique/symmetric direction choices, resolved
/// deterministically by the kernel. The collection owner returns immutable copied snapshots.
/// </remarks>
public readonly record struct ZonePhysicsPenetration
{
    public ZonePhysicsPenetration(float depth, Vector3 normal, Vector3 position, ushort physicalMaterialId,
        bool isSurfaceContact, bool isAmbiguous = false)
    {
        if (!float.IsFinite(depth) || depth <= 0) throw new ArgumentOutOfRangeException(nameof(depth));
        ZoneManagedTerrainGeometry.ValidateFinite(normal, nameof(normal));
        ZoneManagedTerrainGeometry.ValidateFinite(position, nameof(position));
        var length = normal.LengthSquared();
        if (!float.IsFinite(length) || MathF.Abs(length - 1) > .001f)
            throw new ArgumentOutOfRangeException(nameof(normal), "Unprojection direction must be a unit vector.");
        Depth = depth; Normal = Vector3.Normalize(normal); Position = position;
        PhysicalMaterialId = physicalMaterialId; IsSurfaceContact = isSurfaceContact; IsAmbiguous = isAmbiguous;
    }
    public float Depth { get; }
    public Vector3 Normal { get; }
    public Vector3 Position { get; }
    public ushort PhysicalMaterialId { get; }
    public bool IsSurfaceContact { get; }
    public bool IsAmbiguous { get; }
}
