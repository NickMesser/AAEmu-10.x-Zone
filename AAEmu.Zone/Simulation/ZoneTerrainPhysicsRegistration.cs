using System.Collections.ObjectModel;

namespace AAEmu.Zone.Simulation;

/// <summary>The physical world's entity identity returned by its heightfield installation.</summary>
public interface IZoneTerrainPhysicsEntity
{
    /// <summary>
    /// Entity+38/type11: update the foreign-data ID, retaining the foreign pointer and its flags,
    /// and project that metadata to an associated placeholder. Terrain passes zero. The native
    /// call uses threadSafe=0, pointer=-1, flags=INT_MIN, flagsAND=-1 and flagsOR=0.
    /// </summary>
    void SetForeignDataId(short id);
}

/// <summary>
/// Required physical-world backend. Implementations must supply real heightfield geometry,
/// callbacks and ownership; these operations cannot be satisfied by accepting descriptors only.
/// Unsupported capabilities must throw. This interface does not provide a physical simulation.
/// </summary>
public interface IZoneTerrainPhysicsWorld
{
    /// <summary>
    /// IPhysicalWorld+88/3508B860: install geometry and copy the material map into world-owned
    /// storage. The native world owns five terrain entities. Reinstallation retains their identity,
    /// releases their previous geometry/maps and installs replacements. Retain the callback owner
    /// until those geometries retire. Return the first terrain entity; do not apply foreign metadata.
    /// </summary>
    IZoneTerrainPhysicsEntity SetHeightfield(ZoneTerrainPhysicsDescriptor descriptor,
        IReadOnlyList<int> physicalMaterials);
    /// <summary>
    /// IPhysicalWorld+98/35078E20: replace the owned maps of the existing terrain entities, copying
    /// the supplied entries. Keep geometry, callback ownership and entity metadata unchanged.
    /// </summary>
    void SetHeightfieldMaterials(IReadOnlyList<int> physicalMaterials);
    /// <summary>
    /// IPhysicalWorld+88 with a null descriptor: release terrain geometry, map storage and entities.
    /// Repeated clearing is valid. This is whole-heightfield retirement, separate from cell unload.
    /// Stop new callback use before releasing its owner; complete in-flight calls or retain their
    /// owner safely until they complete. The reusable registration itself retains its descriptor.
    /// </summary>
    void ClearHeightfield();
}

/// <summary>
/// Executes the terrain side of physical registration and map refresh (300D9CC0/300D9960).
/// A required backend owns the real physics resources; completing this component's calls does not
/// establish scene readiness. Use one registration owner for one physical world's terrain.
/// </summary>
public sealed class ZoneTerrainPhysicsRegistration
{
    private readonly object _gate = new();
    private readonly IZoneTerrainPhysicsWorld _world;
    private bool _hasHeightfield;
    public ZoneTerrainPhysicsDescriptor Descriptor { get; }

    public ZoneTerrainPhysicsRegistration(ZoneTerrainHeightField heights, IZoneTerrainPhysicsWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        Descriptor = new(heights);
        _world = world;
    }

    public void Register(IReadOnlyList<int> physicalMaterials)
    {
        var materials = Snapshot(physicalMaterials);
        lock (_gate)
        {
            // A failed replacement may leave only some native replicas installed.
            _hasHeightfield = false;
            var entity = _world.SetHeightfield(Descriptor, materials) ??
                throw new InvalidOperationException("The physics backend did not return its terrain entity.");
            // Installation already happened if the subsequent metadata operation fails.
            _hasHeightfield = true;
            entity.SetForeignDataId(0);
        }
    }

    public void Refresh(IReadOnlyList<int> physicalMaterials)
    {
        var materials = Snapshot(physicalMaterials);
        lock (_gate)
        {
            // Native +98 dereferences all five entities; reject this invalid lifecycle explicitly.
            if (!_hasHeightfield) throw new InvalidOperationException("The physical heightfield has not been registered.");
            _world.SetHeightfieldMaterials(materials);
        }
    }

    /// <summary>
    /// Retire the backend's whole heightfield. Also call this after a failed backend installation:
    /// the backend may own partially constructed resources. A later Register can create it again.
    /// This operation does not unload cells or clear their height/surface query caches.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            // A throwing backend may already have retired part of its heightfield.
            _hasHeightfield = false;
            _world.ClearHeightfield();
        }
    }

    private static ReadOnlyCollection<int> Snapshot(IReadOnlyList<int> materials)
    {
        ArgumentNullException.ThrowIfNull(materials);
        if (materials.Count != ZoneTerrainPhysicsDescriptor.MaterialCount)
            throw new ArgumentException("Terrain physics requires exactly 2048 material entries.", nameof(materials));
        var snapshot = new int[ZoneTerrainPhysicsDescriptor.MaterialCount];
        for (var i = 0; i < snapshot.Length; i++) snapshot[i] = materials[i];
        return Array.AsReadOnly(snapshot);
    }
}
