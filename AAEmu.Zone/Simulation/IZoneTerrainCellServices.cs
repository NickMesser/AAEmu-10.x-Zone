using AAEmu.Zone.Content;

namespace AAEmu.Zone.Simulation;

/// <summary>
/// Actual asset access, corresponding to CryPak and the XML loader. Returned streams belong to
/// the caller; null means the requested asset could not be opened. Providers resolve the native
/// world surface.xml and cell client/terrain/heightmap.dat and client/leveldata.xml paths.
/// </summary>
public interface IZoneTerrainCellAssets
{
    Stream? OpenWorldSurfaces(ZoneWorldMapContent world);
    Stream? OpenHeightmap(ZoneWorldMapCell cell);
    Stream? OpenLevelData(ZoneWorldMapCell cell);
}

/// <summary>
/// Required engine leaves. No renderer, terrain collision, object loader, or physical callback is
/// supplied by this interface. Implementations own external node resources and register the exact
/// supplied heightfield. Its persistent tree owns data, bounds, seams and material edge maps.
/// Unsupported leaves must throw, rather than report that a playable scene has loaded.
/// The supplied backend retains ownership of its world resources, including failed world setup.
/// </summary>
public interface IZoneTerrainCellServices
{
    /// <summary>
    /// 30157300: create the world's terrain before reading its surface catalog. Bind physical
    /// callbacks to the engine's supplied heightfield and initialize its node tree; do not allocate
    /// a separate sample owner. Resolve the native ocean-level setup before initializing the tree.
    /// </summary>
    bool CreateWorldTerrain(ZoneWorldMapContent world, ZoneTerrainHeightField heights);
    /// <summary>300E9600: install external node resources before the engine publishes shared payloads.</summary>
    void LoadNodeResources(ZoneWorldMapCellPlacement placement, ZoneTerrainHeightmapContent content);
    /// <summary>300E3580(1) followed by 300D9F20, before version 24's surface overrides.</summary>
    void PrepareNodeTextures(ZoneWorldMapCellPlacement placement);
    /// <summary>
    /// 300D9CC0: install the physical heightfield with real 300D5EF0/300D5FF0-equivalent height and
    /// surface/hole callbacks, the 2048-entry material map, and the subsequent entity parameters.
    /// </summary>
    void RegisterPhysicalHeightfield(IReadOnlyList<int> physicalMaterials);
    /// <summary>300D9960: update the existing physical world's terrain material mapping.</summary>
    void RefreshPhysicalMaterialMap(IReadOnlyList<int> physicalMaterials);
    /// <summary>3015DBFA: refresh affected type 13 octree objects after successful terrain or empty load.</summary>
    void FinishTerrainLoad(ZoneWorldMapCellPlacement placement);
    /// <summary>30156FC0's 30174BB0 call, before cell node retirement.</summary>
    void BeginTerrainUnload(ZoneWorldMapCellPlacement placement);
    /// <summary>300E8C20: retire actual node resources before the managed sample projection is cleared.</summary>
    void UnloadNodeResources(ZoneWorldMapCellPlacement placement);
    /// <summary>
    /// Seamless+108's dedicated object admission and C3DEngine 30163AC0. The backend owns the
    /// separate object occupancy table and complete object lifecycle; terrain occupancy is unrelated.
    /// </summary>
    bool LoadCellObjects(ZoneWorldMapCellPlacement placement);
    /// <summary>
    /// Dedicated Seamless teardown calls object removal before terrain removal. The backend
    /// retires deferred work, XML entities, visibility and outdoor objects in native30156F10
    /// order, using the teardown's native object mask0x18 and flag0. A failed load can still own
    /// resources; only a successful return permits clearing this window's object attempt.
    /// </summary>
    void UnloadCellObjects(ZoneWorldMapCellPlacement placement);
}
