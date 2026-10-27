using System.Numerics;

using AAEmu.Zone.Content;
using AAEmu.Zone.Native.Engine;
using AAEmu.Zone.Native.Entities;
using AAEmu.Zone.Native.Physics;
using AAEmu.Zone.Native.StaticObjects;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Scene;

/// <summary>
/// One real engine-local ground sample for a loaded cell: the shared managed samples and, when the
/// physical query hits, the copied native ray result. No borrowed native pointer is retained.
/// </summary>
public sealed record ZoneSceneGroundSample(ZoneMapCoordinate EngineCell, Vector3 Point, float ManagedHeight,
    ZoneNativeTerrainRayHit? NativeHit);

/// <summary>One scheduled node-read step at the explicit frame period. State/handle are copied.</summary>
public sealed record ZoneSceneNodeReadReport(ZoneMapCoordinate EngineCell, bool Started,
    ZoneTerrainNodeTextureState State, short BaseHandle, string? Diagnostic);

/// <summary>One cell's real outcomes for both dedicated passes of 365704D0.</summary>
public sealed record ZoneSceneCellLoadReport(ZoneWorldMapCellPlacement Placement, ZoneTerrainCellLoadResult Terrain,
    ZoneTerrainTextureLoadResult? Cover, bool ObjectsLoaded, ZoneCellPassUnavailableException? Refusal,
    IReadOnlyList<ZoneCellObjectPassReport> Passes);

/// <summary>
/// The complete result of one isolated six-cell scene load. It is a report, not a claim: readiness
/// requires every cell to load and every required pass to have an owner.
/// </summary>
public sealed record ZoneSceneLoadReport(IReadOnlyList<ZoneSceneCellLoadReport> Cells,
    IReadOnlyList<ZoneSceneNodeReadReport> NodeReads, int PendingStaticRequests, int TerrainLoadedCells,
    int ObjectLoadedCells, bool Ready, IReadOnlyList<ZoneCellPassUnavailableException> Refusals);

/// <summary>
/// Owns the real dedicated scene for one engine generation: the borrowed physical world, the native
/// static-object and entity services, the terrain cell composition and the ordered cell-object
/// pipeline. This is the production replacement for the imported heightfield bootstrap in this mode,
/// so no cell, object file or terrain sample is loaded twice and no native Seamless LoadZone365704D0
/// is invoked over the managed composition. The engine's real World/Seamless registry is resolved and
/// validated by the caller before this owner is created.
/// Retirement follows the recorded order: cell objects and their ground references, then the five
/// captured terrain replicas while the engine is still alive, then the borrowed services and world.
/// This owner never publishes scene readiness; the caller decides from its report.
/// </summary>
public sealed class ZoneSceneActivation : IDisposable
{
    private readonly ZoneNativeEngine _engine;
    private readonly ZoneNativeWorldLayout _selection;
    private readonly IZoneAssetSource _assets;
    private readonly TimeSpan _framePeriod;
    private readonly Action<string>? _trace;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private ZoneNativeTerrainPhysicsWorld? _physics;
    private ZoneNativeStaticObjectServices? _statics;
    private ZoneNativeEntityServices? _entities;
    private ZoneNativeStaticObjectServices.VegetationGroups? _groups;
    private ZoneTerrainAssetFiles? _terrainAssets;
    private ZoneTerrainMaterialResolver? _materials;
    private ZoneSceneCellObjectLoader? _objects;
    private ZoneDedicatedTerrainCellResources? _resources;
    private ZoneTerrainCellServices? _services;
    private ZoneTerrainCellEngine? _terrain;
    private ZoneTerrainCellWindow? _window;
    private bool _operating, _retiring, _disposed;

    /// <param name="framePeriod">
    /// The explicit replacement scheduling period. It is a required caller input: the replacement
    /// does not infer a native frame rate, and the scheduled node-read step below advances by it.
    /// </param>
    public ZoneSceneActivation(ZoneNativeEngine engine, ZoneNativeWorldLayout selection, IZoneAssetSource assets,
        TimeSpan framePeriod, Action<string>? phaseTrace)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(assets);
        if (!ReferenceEquals(selection.Owner, engine))
            throw new ArgumentException("The resolved World layout belongs to another engine generation.", nameof(selection));
        if (framePeriod <= TimeSpan.Zero || framePeriod > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(framePeriod), "The explicit frame period must be between zero and ten seconds.");
        _engine = engine;
        _selection = selection;
        _assets = assets;
        _framePeriod = framePeriod;
        _trace = phaseTrace;
    }

    public ZoneSceneLoadReport? Report { get; private set; }
    public bool IsCreated => _window is not null;
    /// <summary>The initialized engine's effective terrain settings, copied after creation.</summary>
    public ZoneNativeTerrainSettings? Settings => _resources?.Settings;
    public int VegetationGroupCount => _groups?.DefinitionCount ?? 0;
    public TimeSpan FramePeriod => _framePeriod;
    /// <summary>The scene's borrowed physics world (null before Create/after retirement). Orchestrator-cleared one-line accessor for the host frame coordinator; noted in the ownership change-log.</summary>
    public ZoneNativeTerrainPhysicsWorld? PhysicsWorld => _physics;
    /// <summary>
    /// The engine's entity services, published for the scene composition that constructs the player
    /// factory before this activation can Create. The engine admits a single entity borrower and that
    /// lease belongs to this activation; consumers borrow the SAME services instance, never a second
    /// lease (blocked-activation-entities-accessor, orchestrator-cleared one-line accessor).
    /// </summary>
    public ZoneNativeEntityServices? EntityServices => _entities;

    /// <summary>
    /// The authored physical-surface catalog this scene loaded from
    /// <c>game/libs/materialeffects/surfacetypes.xml</c> — the SAME file the native surface manager
    /// registers from (301CB9F0 / 301CC130, installed into this scene's physics world above). It is
    /// the D2 resolver's data source, so a resolved ID is the engine's own registration order rather
    /// than a managed invention. Null only when the asset is absent, which keeps the gate closed.
    /// </summary>
    public ZonePhysicalSurfaceContent? SurfaceContent => _materials?.SurfaceContent;

    /// <summary>
    /// Creates every required scene service before any cell load. The order preserves the recorded
    /// dependencies: the physical world first (terrain registration needs it), then the static and
    /// entity borrowers, the authored surface registrations, the world vegetation catalog that the
    /// object owners borrow, then the terrain resources, services, cell engine and cell window.
    /// A failure here retains the partially created owners for retirement.
    /// </summary>
    public void Create()
    {
        Enter();
        try
        {
            _trace?.Invoke("CreateSceneServices");
            _physics = _engine.CreatePhysicsWorld();
            _statics = _engine.CreateStaticObjectServices();
            _entities = _engine.CreateEntityServices();
            _terrainAssets = new ZoneTerrainAssetFiles(_assets, _selection.Layout.World);
            _materials = ZoneTerrainMaterialResolver.Load(_assets);
            if (_materials.SurfaceContent is { } surfaces) ZonePhysicalSurfaceInstaller.Install(surfaces, _physics);
            _groups = _statics.LoadVegetationGroups(_selection.Layout.World) ??
                throw new InvalidOperationException("The dedicated engine did not load the authored world vegetation catalog.");
            _objects = new ZoneSceneCellObjectLoader(_engine, _statics, _entities, _terrainAssets, _groups, _trace,
                _selection, _physics);
            _resources = new ZoneDedicatedTerrainCellResources(_engine, _physics, _statics, _terrainAssets, _objects);
            _services = new ZoneTerrainCellServices(_resources, _physics);
            _terrain = new ZoneTerrainCellEngine(_selection.Layout.World, _terrainAssets, _materials, _services);
            _window = new ZoneTerrainCellWindow(_terrain);
        }
        finally { _operating = false; }
    }

    /// <summary>
    /// 365704D0's dedicated passes in layout order: every terrain attempt, then every object attempt.
    /// The object pass runs the native six-pass chain per cell, including the owned deferred
    /// blocking pass. A cell whose required pass refuses admission - for example the deferred
    /// region gate when one of its inputs cannot be sourced from recorded evidence - is recorded
    /// and its
    /// remaining connected resources are retained for retirement; the enclosing loop still loads
    /// the other cells so their real resources are exercised, and the returned report marks the
    /// scene not ready. The first refusal is rethrown only after the report
    /// is stored, and a non-refusal failure publishes the partial evidence too, so the exact
    /// capability gap or refused native gate always arrives together with what each pass produced.
    /// </summary>
    public ZoneSceneLoadReport Load()
    {
        Enter();
        var layout = _selection.Layout;
        var terrains = new List<ZoneTerrainCellLoadResult>(layout.Cells.Count);
        var objects = new bool[layout.Cells.Count];
        var refusals = new List<ZoneCellPassUnavailableException>();
        try
        {
            _trace?.Invoke("LoadZoneTerrain");
            // TEMPORARY retirement-diagnostic samples: attribute queue entries to a load phase.
            _statics?.SampleVisibilityQueues("before-LoadZoneTerrain");
            foreach (var placement in layout.Cells) terrains.Add(_window!.TryLoadCell(placement));
            _statics?.SampleVisibilityQueues("after-LoadZoneTerrain");
            for (var index = 0; index < terrains.Count; index++)
                if (!terrains[index].Succeeded)
                    throw new InvalidDataException(
                        $"Terrain cell ({layout.Cells[index].Cell.Coordinate.X},{layout.Cells[index].Cell.Coordinate.Y}) did not load: {terrains[index].Status} {terrains[index].Error}");

            _trace?.Invoke("LoadZoneObjects");
            for (var index = 0; index < layout.Cells.Count; index++)
            {
                var placement = layout.Cells[index];
                try
                {
                    objects[index] = _window!.TryLoadCellObjects(placement);
                    _statics?.SampleVisibilityQueues($"after-objects-cell({placement.EngineCell.X},{placement.EngineCell.Y})");
                }
                catch (ZoneCellPassUnavailableException refusal)
                {
                    // A named capability gap. Native 30163AC0 does not short-circuit its dedicated
                    // cell sequence, and every connected pass already owns real resources that this
                    // check must exercise and retire; the refusal still forbids scene readiness.
                    refusals.Add(refusal);
                    _statics?.SampleVisibilityQueues($"after-refusal-cell({placement.EngineCell.X},{placement.EngineCell.Y})");
                }
            }
        }
        catch
        {
            // A refused native ownership gate poisons the engine, but the pass evidence gathered so
            // far is exactly what the next person needs; publish it before rethrowing.
            Publish(layout, terrains, objects, refusals);
            throw;
        }
        finally { _operating = false; }
        return Publish(layout, terrains, objects, refusals);
    }

    private ZoneSceneLoadReport Publish(ZoneWorldMapLayout layout, List<ZoneTerrainCellLoadResult> terrains,
        bool[] objects, List<ZoneCellPassUnavailableException> refusals)
    {
        var cells = new List<ZoneSceneCellLoadReport>(terrains.Count);
        for (var index = 0; index < terrains.Count; index++)
        {
            var placement = layout.Cells[index];
            var cellObjects = _objects!.Reports.FirstOrDefault(report =>
                report.Placement.EngineCell == placement.EngineCell);
            cells.Add(new(placement, terrains[index],
                _resources is null ? null : _resources.GetTextureLoadResult(placement), objects[index],
                cellObjects?.Refusal, cellObjects?.Passes ?? []));
        }
        var attemptCount = terrains.Count;
        var objectCount = objects.Count(loaded => loaded);
        return Report = new(cells.AsReadOnly(), [], 0, terrains.Count(result => result.Succeeded),
            objectCount, refusals.Count == 0 && objectCount == layout.Cells.Count &&
            attemptCount == layout.Cells.Count, refusals.AsReadOnly());
    }

    /// <summary>
    /// Final explicit zero-time native loading completion after the six cells and before collision
    /// readiness or cell retirement (World+100/3508BD10). The cell pipeline already runs the same
    /// completion at the position of the missing deferred blocking pass, so this call verifies that
    /// no queued construction request of this scene's retained static bodies remains. It is not an
    /// actor simulation frame.
    /// </summary>
    public int CompletePendingStaticWork()
    {
        Enter();
        try { return _engine.CompletePendingStaticPhysics(); }
        finally { _operating = false; }
    }

    /// <summary>
    /// Replacement node-read schedule: one step per explicit frame period, one eligible node per
    /// loaded cell, starting from that cell's root in stable pre-order. The native frame scheduler's
    /// active-node priority membership has no managed owner yet, so this is a deterministic bounded
    /// replacement policy, not native scheduling parity; a node without an authored nonnegative
    /// texture index or without opened cover metadata is reported instead of forced. Reads publish
    /// through the low-level adapter and are drained before retirement.
    /// </summary>
    public IReadOnlyList<ZoneSceneNodeReadReport> ScheduleNodeReads()
    {
        Enter();
        try
        {
            var textures = _terrain!.Nodes.Textures;
            var reads = new List<ZoneSceneNodeReadReport>();
            foreach (var placement in _selection.Layout.Cells)
            {
                Thread.Sleep(_framePeriod);
                var cell = placement.EngineCell;
                var node = FindEligibleNode(_terrain.Nodes.GetCellRoot(cell.X, cell.Y));
                if (node is null)
                {
                    reads.Add(new(cell, false, ZoneTerrainNodeTextureState.Idle, 0,
                        "no authored nonnegative node texture index in this cell"));
                    continue;
                }
                var started = textures.StartNodeRead(node, waitForCompletion: true);
                var snapshot = textures.GetNodeTexture(node);
                reads.Add(new(cell, started, snapshot.State, snapshot.BaseHandle,
                    snapshot.Diagnostics.Count == 0 ? null : string.Join("; ", snapshot.Diagnostics)));
            }
            return reads.AsReadOnly();
        }
        finally { _operating = false; }
    }

    /// <summary>
    /// Engine-local ground queries: the shared managed samples and the real replica-4 terrain ray.
    /// The comparison is the behaviour the acceptance criterion asks for; a miss stays a miss.
    /// </summary>
    public IReadOnlyList<ZoneSceneGroundSample> QueryGround(IReadOnlyList<Vector3> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        Enter();
        try
        {
            var samples = new List<ZoneSceneGroundSample>(points.Count);
            foreach (var point in points)
            {
                var cell = new ZoneMapCoordinate((int)(point.X / 1024), (int)(point.Y / 1024));
                var height = _terrain!.Heights.GetHeight(point.X, point.Y);
                var hit = _physics!.RaycastTerrain(new(point.X, point.Y, height + 10), new(0, 0, -30), out var value)
                    ? value : (ZoneNativeTerrainRayHit?)null;
                samples.Add(new(cell, point, height, hit));
            }
            return samples.AsReadOnly();
        }
        finally { _operating = false; }
    }

    private static ZoneTerrainNode? FindEligibleNode(ZoneTerrainNode node)
    {
        if (node.TextureIndex >= 0) return node;
        foreach (var child in node.Children)
        {
            var found = FindEligibleNode(child);
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>
    /// The recorded retirement order. Cell objects (and therefore every entity, visibility area,
    /// imported brush, road, water volume, decal and vegetation instance, with their ground
    /// references) retire first; then the terrain registration clears the whole heightfield, which
    /// retires the five captured replicas while the engine's CTerrain/CSystem is still alive; then
    /// the node resources, the world catalog, the borrowed services and finally the borrowed world.
    /// The enclosing caller retires the engine after this owner. Every phase runs even when an
    /// earlier one failed, so one failure does not strand the remaining owners, and all failures
    /// are reported together.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        Enter(disposal: true);
        _retiring = true;
        var failures = new List<Exception>();
        void Retire(string phase, Action action)
        {
            _trace?.Invoke(phase);
            try { action(); }
            catch (Exception error) { failures.Add(new InvalidOperationException($"Scene retirement phase {phase} failed.", error)); }
        }
        try
        {
            Retire("RetireCellWindow", () => _window?.Dispose());
            Retire("RetireTerrainRegistration", () => _services?.Dispose());
            Retire("RetireTerrainResources", () => _resources?.Dispose());
            Retire("RetireWorldCatalog", () => _groups?.Dispose());
            Retire("RetireEntityServices", () => _entities?.Dispose());
            Retire("RetireStaticObjectServices", () => _statics?.Dispose());
            Retire("RetirePhysicsWorld", () => _physics?.Dispose());
            _disposed = true;
        }
        finally { _operating = false; }
        if (failures.Count != 0) throw new AggregateException("Scene retirement failed.", failures);
    }

    private void Enter(bool disposal = false)
    {
        if (Environment.CurrentManagedThreadId != _thread)
            throw new InvalidOperationException("Scene operations must run on their engine's owning thread.");
        ObjectDisposedException.ThrowIf(_disposed || (_retiring && !disposal), this);
        if (_operating) throw new InvalidOperationException("Scene operations cannot reenter their owner.");
        _operating = true;
    }
}
