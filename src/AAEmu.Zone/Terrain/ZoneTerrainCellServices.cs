using AAEmu.Zone.Content;
using AAEmu.Zone.Scene;

namespace AAEmu.Zone.Terrain;

/// <summary>
/// Composes required terrain resources with the physical registration of the cell engine's exact
/// heightfield. The resources and physical world are borrowed and must outlive this owner.
/// Complete cell-window disposal successfully first, then dispose this owner and the borrowed
/// backends. A failed cell unload retains its slot for retry and still needs these services and
/// physics; do not clear them while that retirement remains incomplete. Retain this owner
/// for cleanup even if engine construction fails; it owns the registration from creation onward.
/// Disposal explicitly retires the whole physical heightfield, not the borrowed resource backend.
/// Calls serialize; reentry, including disposal from a backend callback, throws. A failed disposal
/// may be retried, but no further terrain operations are admitted. This is a composition contract,
/// not a physical simulation or an assertion that the scene is ready.
/// </summary>
public sealed class ZoneTerrainCellServices : IZoneTerrainCellServices, IDisposable
{
    private readonly object _gate = new();
    private readonly IZoneTerrainCellResources _resources;
    private readonly IZoneTerrainPhysicsWorld _physicsWorld;
    private ZoneTerrainPhysicsRegistration? _registration;
    private bool _worldCreated;
    private bool _busy;
    private bool _retiring;
    private bool _disposed;

    public ZoneTerrainCellServices(IZoneTerrainCellResources resources, IZoneTerrainPhysicsWorld physicsWorld)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(physicsWorld);
        _resources = resources;
        _physicsWorld = physicsWorld;
    }

    public bool CreateWorldTerrain(ZoneWorldMapContent world, ZoneTerrainHeightField heights)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(heights);
        lock (_gate)
        {
            CheckAvailable();
            if (_registration is not null)
                throw new InvalidOperationException("These terrain services have already been bound to a world.");
            var registration = _registration = new(heights, _physicsWorld);
            _busy = true;
            var threadId = Environment.CurrentManagedThreadId;
            var active = true;
            var nextPhase = 0;
            var violated = 0;
            try
            {
                var result = _resources.CreateWorldTerrain(world, heights, () => RegisterInitial(0), () => RegisterInitial(1));
                if (result && (nextPhase != 2 || Volatile.Read(ref violated) != 0))
                    throw new InvalidOperationException("World terrain creation did not complete both physical registration phases.");
                _worldCreated = result;
                return result;
            }
            finally
            {
                active = false;
                _busy = false;
            }

            void RegisterInitial(int phase)
            {
                // Reject asynchronous use without taking the operation lock and blocking its caller.
                if (Environment.CurrentManagedThreadId != threadId)
                {
                    Interlocked.Exchange(ref violated, 1);
                    throw new InvalidOperationException("World terrain registration must run on the creation thread.");
                }
                if (!active)
                    throw new InvalidOperationException("The world terrain registration callback has expired.");
                if (nextPhase != phase)
                {
                    Interlocked.Exchange(ref violated, 1);
                    throw new InvalidOperationException("World terrain registration phases must run once and in order.");
                }
                nextPhase = -1;
                if ((phase == 0 && heights.Nodes is not null) || (phase == 1 && heights.Nodes is null))
                {
                    Interlocked.Exchange(ref violated, 1);
                    throw new InvalidOperationException("The terrain tree must be initialized between the two physical registrations.");
                }
                // Both native registrations precede world surface.xml and use empty surface records.
                // Native geometry creation retains these callbacks without querying them before the tree exists.
                registration.Register(new int[ZoneTerrainPhysicsDescriptor.MaterialCount]);
                nextPhase = phase + 1;
            }
        }
    }

    public void LoadNodeResources(ZoneWorldMapCellPlacement placement, ZoneTerrainHeightmapContent content) =>
        Execute(() => _resources.LoadNodeResources(placement, content));
    public void PrepareNodeTextures(ZoneWorldMapCellPlacement placement) =>
        Execute(() => _resources.PrepareNodeTextures(placement));
    public void RegisterPhysicalHeightfield(IReadOnlyList<int> physicalMaterials) =>
        Execute(() => _registration!.Register(physicalMaterials));
    public void RefreshPhysicalMaterialMap(IReadOnlyList<int> physicalMaterials) =>
        Execute(() => _registration!.Refresh(physicalMaterials));
    public void FinishTerrainLoad(ZoneWorldMapCellPlacement placement) =>
        Execute(() => _resources.FinishTerrainLoad(placement));
    public void BeginTerrainUnload(ZoneWorldMapCellPlacement placement) =>
        Execute(() => _resources.BeginTerrainUnload(placement));
    public void UnloadNodeResources(ZoneWorldMapCellPlacement placement) =>
        Execute(() => _resources.UnloadNodeResources(placement));
    public void UnloadCellObjects(ZoneWorldMapCellPlacement placement) =>
        Execute(() => _resources.UnloadCellObjects(placement));
    public bool LoadCellObjects(ZoneWorldMapCellPlacement placement)
    {
        var result = false;
        Execute(() => result = _resources.LoadCellObjects(placement));
        return result;
    }

    /// <summary>
    /// Clear any complete or partial physical installation, even after world setup fails. The
    /// backend must complete callback retirement before releasing its owner. Borrowed resources
    /// and physics-world disposal remain the caller's responsibility. Retry if clearing throws.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_busy) throw new InvalidOperationException("Terrain services cannot be disposed during a backend operation.");
            _retiring = true;
            _busy = true;
            try
            {
                _registration?.Clear();
                _disposed = true;
            }
            finally { _busy = false; }
        }
    }

    private void Execute(Action operation)
    {
        lock (_gate)
        {
            CheckAvailable();
            if (!_worldCreated) throw new InvalidOperationException("World terrain creation has not succeeded.");
            _busy = true;
            try { operation(); }
            finally { _busy = false; }
        }
    }

    private void CheckAvailable()
    {
        ObjectDisposedException.ThrowIf(_retiring, this);
        if (_busy) throw new InvalidOperationException("Terrain services cannot be reentered from a backend operation.");
    }
}
