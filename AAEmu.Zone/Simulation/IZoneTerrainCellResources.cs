using AAEmu.Zone.Content;

namespace AAEmu.Zone.Simulation;

/// <summary>
/// Required external terrain resources. Implementations own textures, render/procedural node
/// resources and cell objects; unsupported operations must throw. A service composer borrows
/// these resources, including resources retained after failed world creation.
/// </summary>
public interface IZoneTerrainCellResources
{
    /// <summary>
    /// 30157300/30137CC0: create one world's terrain. Invoke registerConstructedHeightfield once
    /// at 300DAF10's 300D9CC0 boundary, after constructing the 2048 empty surface records and
    /// before the remaining constructor allocations. Then 300CFEC0 resets the native cache and
    /// creates the complete node tree with heights.InitializeNodeTree(resolvedOceanLevel)
    /// (300D7910); invoke registerInitializedHeightfield once at
    /// its 300D9CC0 boundary before the remaining initialization. Both calls must finish on the
    /// calling thread before returning true. Do not retain either callback or step physics during
    /// creation. The caller reads world surface.xml only after this operation succeeds.
    /// </summary>
    bool CreateWorldTerrain(ZoneWorldMapContent world, ZoneTerrainHeightField heights, Action registerConstructedHeightfield,
        Action registerInitializedHeightfield);
    /// <summary>
    /// Remaining render/procedural resources. The cell engine already imports the node's
    /// zero-mesh surface-record vectors through Nodes.SurfaceResources before this call.
    /// </summary>
    void LoadNodeResources(ZoneWorldMapCellPlacement placement, ZoneTerrainHeightmapContent content);
    /// <summary>
    /// Use the supplied heightfield's Nodes.Textures for dedicated cover metadata/index/pool setup.
    /// Preserve opener diagnostics without treating a missing cover as terrain-load failure.
    /// The resource owner must still implement node streaming and retire/dispose its resources.
    /// </summary>
    void PrepareNodeTextures(ZoneWorldMapCellPlacement placement);
    void FinishTerrainLoad(ZoneWorldMapCellPlacement placement);
    void BeginTerrainUnload(ZoneWorldMapCellPlacement placement);
    /// <summary>
    /// Retire external node resources. The cell engine then retires its imported zero-mesh
    /// surface vectors before sample clearing. Full per-node interleaving must precede admission
    /// of nonzero native mesh/procedural producers; the vector owner cannot release those yet.
    /// </summary>
    void UnloadNodeResources(ZoneWorldMapCellPlacement placement);
    bool LoadCellObjects(ZoneWorldMapCellPlacement placement);
    /// <summary>Retire the dedicated cell's object resources before its terrain; retain partial cleanup on failure.</summary>
    void UnloadCellObjects(ZoneWorldMapCellPlacement placement);
}
