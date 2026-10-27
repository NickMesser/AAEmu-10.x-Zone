using AAEmu.Zone.Content;

namespace AAEmu.Zone.Physics;

/// <summary>
/// The scene's one immutable surface table, built from the parsed SurfaceTypes.xml content
/// (Agent 7). It is a VIEW, not a copy of the physics state: it answers "which authored surface is
/// material id N", and the answer is the same for the owned player physics and for the native-AI
/// compatibility route, so the two cannot drift apart on friction or footstep radii.
///
/// Duplicate native rows are already resolved by the content snapshot
/// (<c>ZonePhysicalSurfaceContent</c> retains native duplicate-row mutations), so the last row for
/// an id wins here exactly as the native table does - the table mirrors the content, it does not
/// second-guess it.
/// </summary>
public sealed class ZoneCollisionSurfaceTable : IZoneCollisionSurfaces
{
    private readonly Dictionary<ushort, ZonePhysicalSurfaceDefinition> _byId = [];
    private readonly ZonePhysicalSurfaceDefinition _default;

    /// <param name="content">The scene's parsed surface content; null only for a scene with no materials.</param>
    /// <exception cref="InvalidOperationException">The content defines no `mat_default` row.</exception>
    public ZoneCollisionSurfaceTable(ZonePhysicalSurfaceContent? content)
    {
        if (content is null)
            throw new ArgumentNullException(nameof(content),
                "A collision surface table is built from parsed surface content; a scene with none never reaches this point.");
        foreach (var surface in content.Surfaces)
            _byId[(ushort)surface.Id] = surface;
        _default = content.Surfaces.FirstOrDefault(surface =>
            string.Equals(surface.Name, "mat_default", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "SurfaceTypes.xml defines no mat_default row; the collision surface table refuses to invent a default.");
    }

    /// <inheritdoc/>
    public int Count => _byId.Count;

    /// <inheritdoc/>
    public ZonePhysicalSurfaceDefinition Default => _default;

    /// <inheritdoc/>
    public ZonePhysicalSurfaceDefinition? Find(ushort physicalMaterialId) =>
        _byId.TryGetValue(physicalMaterialId, out var surface) ? surface : null;

    /// <inheritdoc/>
    public ZonePhysicalSurfaceDefinition Resolve(ushort physicalMaterialId) =>
        _byId.TryGetValue(physicalMaterialId, out var surface) ? surface : _default;

    /// <summary>
    /// Whether a surface is walkable for a living actor: authored `no_collide` and
    /// vehicle-only surfaces block footfalls. Kept here so the owned physics and the AI route apply
    /// the SAME rule from the SAME row rather than each re-reading the flags.
    /// </summary>
    public bool IsWalkable(ZonePhysicalSurfaceDefinition surface) =>
        !surface.Flags.HasFlag(ZonePhysicalSurfaceFlags.NoCollide)
        && !surface.Flags.HasFlag(ZonePhysicalSurfaceFlags.VehicleOnlyCollisions);
}
