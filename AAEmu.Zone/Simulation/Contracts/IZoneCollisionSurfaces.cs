using AAEmu.Zone.Content;

namespace AAEmu.Zone.Simulation.Contracts;

/// <summary>
/// Backend-neutral surface resolution for collision results (Agent 7).
///
/// Why this exists: every collision query in the scene already returns a copied
/// <see cref="ZonePhysicsHit"/> / <see cref="ZonePhysicsGroundHit"/> carrying a
/// <c>PhysicalMaterialId</c> - a plain number. That number is useless on its own: friction,
/// elasticity, collision flags and the AI's impact/footstep radii all live in the AUTHORED
/// SurfaceTypes.xml rows, and a consumer that wanted them had to reach for the native world or
/// re-parse content. This is the one route from a hit to what it means, and it is deliberately
/// neutral: <see cref="ZonePhysicalSurfaceDefinition"/> is parsed content, with no native or ABI
/// type on the boundary.
///
/// The same table serves the owned player physics (footfall surface, friction) and the native-AI
/// compatibility route (impact radii, sound radii, movement multipliers), so both read ONE source of
/// truth rather than two that can drift.
/// </summary>
public interface IZoneCollisionSurfaces
{
    /// <summary>How many authored surfaces this table resolves.</summary>
    int Count { get; }

    /// <summary>
    /// The authored default (`mat_default`), used when a hit's material resolves to nothing.
    /// Never null: the content always defines a default, and a scene without one would not have
    /// loaded.
    /// </summary>
    ZonePhysicalSurfaceDefinition Default { get; }

    /// <summary>
    /// Resolves one hit's material id, or null when the id is not an authored surface. Callers that
    /// need a value should use <see cref="Resolve"/> instead, which falls back to the default
    /// rather than making every caller repeat the policy.
    /// </summary>
    ZonePhysicalSurfaceDefinition? Find(ushort physicalMaterialId);

    /// <summary>Resolves a hit's material id, falling back to <see cref="Default"/>.</summary>
    ZonePhysicalSurfaceDefinition Resolve(ushort physicalMaterialId);
}
