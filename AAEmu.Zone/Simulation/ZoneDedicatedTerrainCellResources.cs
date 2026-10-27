using AAEmu.Zone.Content;

namespace AAEmu.Zone.Simulation;

/// <summary>
/// Required cell-object loading, separate from terrain samples and cover textures. Implementations
/// supply the native object, vegetation, visibility, XML entity, big-object and deferred-load passes
/// in order. A missing required resource capability must throw; brush-only loading is insufficient.
/// The caller retains this owner and its partial allocations through scene retirement.
/// </summary>
public interface IZoneTerrainCellObjectLoader
{
    bool LoadCellObjects(ZoneWorldMapCellPlacement placement);
    /// <summary>
    /// Retire deferred work, entities, visibility and outdoor resources in native30156F10 order.
    /// Dedicated teardown uses object mask0x18 and flag0. Retain unfinished owners on failure;
    /// do not release terrain dependencies until this method returns successfully.
    /// </summary>
    void UnloadCellObjects(ZoneWorldMapCellPlacement placement);
}

/// <summary>
/// Concrete terrain composition for the current imported zero-mesh node scope. Borrows one
/// engine's physics/static services, the authored asset source and the complete cell-object loader;
/// owns cover metadata, node reads/pools and imported surface vectors through the supplied tree.
/// Create this owner and ZoneTerrainCellServices before constructing ZoneTerrainCellEngine, since
/// failed construction can already own heightfield registrations. Retire the cell window first,
/// then clear its physical registration, then dispose this owner and the borrowed services.
/// Scheduling and nonzero render/procedural producers require their own admitted owners.
/// </summary>
public sealed class ZoneDedicatedTerrainCellResources : IZoneTerrainCellResources, IDisposable
{
    private readonly ZoneNativeEngine _engine;
    private readonly ZoneNativeTerrainPhysicsWorld _physics;
    private readonly ZoneNativeStaticObjectServices _statics;
    private readonly ZoneTerrainAssetFiles _assets;
    private readonly IZoneTerrainCellObjectLoader _objects;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly ZoneTerrainTextureLoadResult?[] _textureResults = new ZoneTerrainTextureLoadResult?[64];
    private ZoneWorldMapContent? _world;
    private ZoneTerrainHeightField? _heights;
    private bool _creationStarted, _created, _operating, _retiring, _disposed;

    public ZoneNativeTerrainSettings Settings { get; }

    public ZoneDedicatedTerrainCellResources(ZoneNativeEngine engine,
        ZoneNativeTerrainPhysicsWorld physics, ZoneNativeStaticObjectServices statics,
        ZoneTerrainAssetFiles assets, IZoneTerrainCellObjectLoader objects)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(physics);
        ArgumentNullException.ThrowIfNull(statics);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(objects);
        if (!ReferenceEquals(physics.Engine, engine) || !ReferenceEquals(statics.Engine, engine))
            throw new ArgumentException("Terrain resources require physics and static services from the same engine.");
        _engine = engine;
        _physics = physics;
        _statics = statics;
        _assets = assets;
        _objects = objects;
        Settings = engine.CaptureTerrainSettings(); // Also verifies the real engine's owning thread.
    }

    public bool CreateWorldTerrain(ZoneWorldMapContent world, ZoneTerrainHeightField heights,
        Action registerConstructedHeightfield, Action registerInitializedHeightfield)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(heights);
        ArgumentNullException.ThrowIfNull(registerConstructedHeightfield);
        ArgumentNullException.ThrowIfNull(registerInitializedHeightfield);
        Enter(requireCreated: false);
        try
        {
            if (!ReferenceEquals(_assets.World, world))
                throw new ArgumentException("The terrain assets belong to another world.", nameof(world));
            if (_creationStarted || heights.Nodes is not null)
                throw new InvalidOperationException("Terrain creation requires a new owner and an uninitialized heightfield.");
            _creationStarted = true;
            _world = world;
            _heights = heights; // Retain partial resources before either native registration.
            registerConstructedHeightfield();
            var nodes = heights.InitializeNodeTree(ZoneTerrainWorldInitialization.ResolveOceanLevel(world));
            registerInitializedHeightfield();
            _physics.ConfigureTerrainEntityGrid();
            nodes.Textures.ConnectStreams(_engine);
            _created = true;
            return true;
        }
        finally { _operating = false; }
    }

    public void LoadNodeResources(ZoneWorldMapCellPlacement placement, ZoneTerrainHeightmapContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Execute(placement, node => RequireImportedScope(node));
        // 300E9600 imports zeroed surface records but creates no mesh/procedural resource.
        // ZoneTerrainCellEngine already imports them and installs samples after this callback.
    }

    public void PrepareNodeTextures(ZoneWorldMapCellPlacement placement) => Execute(placement, node =>
    {
        var textures = node.Owner.Textures;
        textures.RetireNode(node, true); // 300E3580 precedes 300D9F20, even on cover-open failure.
        _textureResults[placement.EngineCell.X * 8 + placement.EngineCell.Y] =
            textures.TryOpenCell(placement, _assets, Settings.TexturePoolSize);
        // Retain diagnostics; the enclosing native terrain loader ignores the cover result.
        // Frame scheduling supplies active-list eligibility and starts reads separately.
    });

    public ZoneTerrainTextureLoadResult? GetTextureLoadResult(ZoneWorldMapCellPlacement placement)
    {
        Enter();
        try
        {
            _ = RequireCell(placement);
            return _textureResults[placement.EngineCell.X * 8 + placement.EngineCell.Y];
        }
        finally { _operating = false; }
    }

    public void FinishTerrainLoad(ZoneWorldMapCellPlacement placement) => Execute(placement, _ =>
        _statics.FinishTerrainLoad(placement.EngineCell.X, placement.EngineCell.Y));

    public void BeginTerrainUnload(ZoneWorldMapCellPlacement placement) => Execute(placement, _ =>
        _statics.BeginTerrainUnload(placement.EngineCell.X, placement.EngineCell.Y));

    public void UnloadNodeResources(ZoneWorldMapCellPlacement placement) => Execute(placement, node =>
    {
        RequireImportedScope(node);
        node.Owner.Textures.RetireNode(node, true);
        // The cell engine subsequently retires surface vectors and clears shared samples.
        // Authored metadata, texture indices and structural nodes survive that cell unload.
    });

    public bool LoadCellObjects(ZoneWorldMapCellPlacement placement)
    {
        Enter();
        try { _ = RequireCell(placement); return _objects.LoadCellObjects(placement); }
        finally { _operating = false; }
    }

    public void UnloadCellObjects(ZoneWorldMapCellPlacement placement)
    {
        Enter();
        // TEMPORARY retirement-diagnostic samples: which half of the unload dirties the queues.
        _statics.SampleVisibilityQueues($"cell({placement.EngineCell.X},{placement.EngineCell.Y})-before-object-unload");
        try
        {
            _ = RequireCell(placement);
            _objects.UnloadCellObjects(placement);
        }
        finally
        {
            _statics.SampleVisibilityQueues($"cell({placement.EngineCell.X},{placement.EngineCell.Y})-after-object-unload");
            _operating = false;
        }
    }

    /// <summary>TEMPORARY diagnostic hook for the terrain half of the same unload.</summary>
    public void SampleQueuesForTerrain(string pathTag) => _statics.SampleVisibilityQueues(pathTag);

    public void Dispose()
    {
        if (_disposed) return;
        Enter(requireCreated: false, disposal: true);
        _retiring = true;
        try
        {
            if (_heights?.Nodes is { } nodes)
            {
                nodes.Textures.Dispose(); // Drain reads before metadata/staging and stream lease retirement.
                nodes.SurfaceResources.Dispose();
            }
            _disposed = true;
        }
        finally { _operating = false; }
    }

    private void Execute(ZoneWorldMapCellPlacement placement, Action<ZoneTerrainNode> action)
    {
        Enter();
        try { action(RequireCell(placement)); }
        finally { _operating = false; }
    }

    private ZoneTerrainNode RequireCell(ZoneWorldMapCellPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        if (!ReferenceEquals(_world!.GetCell(placement.Cell.Coordinate.X, placement.Cell.Coordinate.Y), placement.Cell))
            throw new ArgumentException("The terrain cell belongs to another world.", nameof(placement));
        return _heights!.Nodes!.GetCellRoot(placement.EngineCell.X, placement.EngineCell.Y);
    }

    private static void RequireImportedScope(ZoneTerrainNode node)
    {
        node.Owner.SurfaceResources.ReleaseSurfaceMeshes(node);
        foreach (var child in node.Children) RequireImportedScope(child);
    }

    private void Enter(bool requireCreated = true, bool disposal = false)
    {
        if (Environment.CurrentManagedThreadId != _thread)
            throw new InvalidOperationException("Terrain resources must run on their engine's owning thread.");
        ObjectDisposedException.ThrowIf(_disposed || (_retiring && !disposal), this);
        if (_operating) throw new InvalidOperationException("Terrain resource callbacks cannot reenter their owner.");
        if (requireCreated && !_created) throw new InvalidOperationException("Terrain resource creation has not completed.");
        _operating = true;
    }
}
