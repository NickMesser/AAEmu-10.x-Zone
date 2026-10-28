using System.Numerics;

namespace AAEmu.Zone.Physics;

/// <summary>
/// Copied character support. Position/material describe the actual surface. A null normal
/// explicitly denotes a legacy height-only provider; it must not become an invented flat plane
/// when applying authored slope rules. No borrowed geometry or actor state escapes the query.
/// </summary>
public readonly record struct ZonePhysicsCharacterSupport(
    Vector3 Position, Vector3? Normal, ushort PhysicalMaterialId);
