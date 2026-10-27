using System.Numerics;

using AAEmu.Zone.Content;
using AAEmu.Zone.Native.Engine;
using AAEmu.Zone.Native.Entities;
using AAEmu.Zone.Native.Physics;
using AAEmu.Zone.Native.StaticObjects;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Scene;

/// <summary>
/// One required native cell pass could not be admitted. This is not a native skip and not an
/// empty result: the cell must not be reported as loaded, and no fixed or empty provider may stand in
/// for the missing capability. The exception carries the ordered-trace phase so a check can name the
/// first gap exactly instead of failing on a generic message.
/// </summary>
public sealed class ZoneCellPassUnavailableException : NotSupportedException
{
    public ZoneCellPassUnavailableException(string phase, ZoneWorldMapCellPlacement placement, string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Phase = phase;
        Placement = placement;
    }

    /// <summary>The ordered-trace phase of the refused pass, for example CellObjects:DeferredBlockingWork.</summary>
    public string Phase { get; }
    public ZoneWorldMapCellPlacement Placement { get; }
    public ZoneMapCoordinate Cell => Placement.Cell.Coordinate;
}

/// <summary>What one connected pass produced for one cell. Counts are copied, never native pointers.</summary>
public sealed record ZoneCellObjectPassReport(string Pass, string Detail);

/// <summary>
/// One cell's ordered pass results for the native 30163AC0 chain. <paramref name="Refusal"/> is the
/// exact refused-pass exception when a required pass has no owner; a refused cell is never loaded.
/// </summary>
public sealed record ZoneCellObjectReport(ZoneWorldMapCellPlacement Placement, bool Loaded,
    IReadOnlyList<ZoneCellObjectPassReport> Passes, ZoneCellPassUnavailableException? Refusal);

/// <summary>
/// The production cell-object pipeline for one dedicated scene. It follows the real dedicated chain
/// 301637C0 -> 3015E930 -> 30161EF0 -> 3015EEC0 -> 301638E0 -> 3017D480 in order: client/object.dat,
/// client/vegetation.dat, client/visareas.dat, client/entities.xml, client/big_object.dat and finally
/// the deferred blocking cell pass. Every connected pass reuses the existing managed owner for its
/// family (ZoneCellObjectResources, ZoneCellVegetationResources, the native visibility cell, the real
/// XML entity batch, the big-object file owner); no second asset corpus or parallel decoder is added.
/// Native results are preserved: a configured skip stays a skip, and only an actual native result is
/// counted. The final deferred pass (3017CC30/3017D480) is owned by ZoneCellDeferredBrushResources:
/// it decodes statobjs.dat, materials.dat and brush.dat, evaluates the validated position/radius
/// gate (30172560), creates and physicalizes each active record's CBrush through the existing
/// static-object factories, probes one authored obstruction against the created geometry, and
/// retires first in UnloadCellObjects. Its gate inputs are bound from evidence, never from
/// unverified native memory: the active position and radius come from the recorded dedicated
/// activation inputs (proved initial (+0.0,+0.0,+0.0), FIRST_ENTRY_NOTES.md:824-829, and the
/// cry3dengine_dedicate R^2=1e9 constant, FIRST_ENTRY_NOTES.md:336-338) and the World name/origin
/// from the scene-resolved ZoneNativeWorldLayout (FIRST_ENTRY_NOTES.md:316-322); an input whose
/// evidence is missing refuses by name rather than manufacturing an empty success, so the
/// enclosing scene must refuse readiness. Load and unload are single-use per cell and must run on the engine's
/// owning thread. This owner holds managed references only; the per-cell owners retire their own
/// native resources. The pass also runs the connected zero-time static completion at its native
/// position first, because the blocking pass is the point where a cell's queued construction work is
/// consumed before the next cell's native callbacks.
/// </summary>
public sealed class ZoneSceneCellObjectLoader : IZoneTerrainCellObjectLoader
{
    /// <summary>client/object.dat through 30166870/30229200, with per-node gates and callbacks.</summary>
    public const string MainObjectsPass = "CellObjects:MainObjects";
    /// <summary>client/vegetation.dat sectors through 302253C0.</summary>
    public const string SectorVegetationPass = "CellObjects:SectorVegetation";
    /// <summary>client/visareas.dat through the real CVisAreaManager loader 30198BF0.</summary>
    public const string VisibilityPass = "CellObjects:Visibility";
    /// <summary>client/entities.xml through EntitySystem+120/3201C750 and the real class scripts.</summary>
    public const string XmlEntitiesPass = "CellObjects:XmlEntities";
    /// <summary>client/big_object.dat through 30163530/30225050.</summary>
    public const string BigObjectsPass = "CellObjects:BigObjects";
    /// <summary>The final blocking cell pass 3017D480/3017CC30, owned by ZoneCellDeferredBrushResources.</summary>
    public const string DeferredBlockingWorkPass = "CellObjects:DeferredBlockingWork";

    private readonly ZoneNativeEngine _engine;
    private readonly ZoneNativeStaticObjectServices _statics;
    private readonly ZoneNativeEntityServices _entities;
    private readonly ZoneTerrainAssetFiles _assets;
    private readonly ZoneNativeStaticObjectServices.VegetationGroups _groups;
    private readonly ZoneWorldMapContent _world;
    private readonly ZoneNativeWorldLayout? _selection;
    private readonly ZoneNativeTerrainPhysicsWorld? _physics;
    private readonly Action<string>? _trace;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly Dictionary<int, CellPipeline> _cells = [];
    private bool _operating;

    public ZoneSceneCellObjectLoader(ZoneNativeEngine engine, ZoneNativeStaticObjectServices statics,
        ZoneNativeEntityServices entities, ZoneTerrainAssetFiles assets,
        ZoneNativeStaticObjectServices.VegetationGroups groups, Action<string>? phaseTrace,
        ZoneNativeWorldLayout? selection = null, ZoneNativeTerrainPhysicsWorld? physics = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(statics);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(groups);
        if (!ReferenceEquals(statics.Engine, engine) || !ReferenceEquals(entities.Engine, engine))
            throw new ArgumentException("The cell-object pipeline requires static and entity services from the same engine.");
        if (!ReferenceEquals(groups.Owner, statics) || groups.IsDisposed || !groups.Loaded)
            throw new ArgumentException("Cell objects require this service's loaded world vegetation catalog.", nameof(groups));
        if (selection is not null && !ReferenceEquals(selection.Owner, engine))
            throw new ArgumentException("The deferred gate requires the resolved World layout from the same engine.", nameof(selection));
        _engine = engine;
        _statics = statics;
        _entities = entities;
        _assets = assets;
        _groups = groups;
        _world = assets.World;
        _selection = selection;
        _physics = physics;
        _trace = phaseTrace;
    }

    /// <summary>
    /// Reports every attempted cell in layout order, including the cells whose load threw. The report
    /// survives a refusal so the enclosing check can print what each connected pass actually produced.
    /// </summary>
    public IReadOnlyList<ZoneCellObjectReport> Reports => _cells.Values
        .Select(pipeline => new ZoneCellObjectReport(pipeline.Placement, pipeline.Loaded,
            pipeline.Passes.AsReadOnly(), pipeline.Refusal)).ToArray();

    public bool LoadCellObjects(ZoneWorldMapCellPlacement placement)
    {
        Enter();
        try
        {
            var pipeline = RequirePipeline(placement);
            if (pipeline.Attempted)
                throw new InvalidOperationException("Retire this cell's failed object attempt before loading it again.");
            pipeline.Attempted = true; // A failed attempt owns partial resources and cannot be replayed.
            LoadMainObjects(pipeline);
            LoadSectorVegetation(pipeline);
            LoadVisibility(pipeline);
            LoadXmlEntities(pipeline);
            LoadBigObjects(pipeline);
            var loaded = LoadDeferredBlockingWork(pipeline);
            pipeline.Loaded = loaded;
            return loaded;
        }
        finally { _operating = false; }
    }

    /// <summary>
    /// Native 30156F10 order for the dedicated object mask0x18: deferred cancellation
    /// (30175950 over the per-cell deferred manager) first, then EntitySystem cell removal,
    /// visibility removal, then outdoor retirement - so this cell's deferred brush owner is the
    /// first resource disposed here, before XML/visibility/outdoor. A refusal of the deferred
    /// gate records nothing to cancel; its recorded refusal is never rewritten into a successful
    /// unload. A cell whose attempt never ran owns nothing and returns without inventing resources.
    /// </summary>
    public void UnloadCellObjects(ZoneWorldMapCellPlacement placement)
    {
        Enter();
        try
        {
            ArgumentNullException.ThrowIfNull(placement);
            if (!_cells.TryGetValue(CellKey(placement), out var pipeline)) return;
            // Every family retires even when an earlier one fails: one uncertain deferred brush
            // must not abandon this cell's entity, visibility and outdoor resources to the
            // engine-generation sweep. A failed step keeps its owner reference for retry; the
            // failures are reported together after the full recorded order ran.
            List<Exception>? failures = null;
            void Retire(Action action)
            {
                try { action(); }
                catch (Exception error) { (failures ??= []).Add(error); }
            }
            Retire(() => { pipeline.Deferred?.Dispose(); pipeline.Deferred = null; });
            Retire(() => { pipeline.Entities?.Dispose(); pipeline.Entities = null; });
            // TEMPORARY retirement-diagnostic sample: attribute the queue entries that appear
            // between the pre-unload sample and the visibility prepare.
            Retire(() => _statics.SampleVisibilityQueues($"cell({placement.EngineCell.X},{placement.EngineCell.Y})-after-entities-dispose"));
            Retire(() => { pipeline.Visibility?.Dispose(); pipeline.Visibility = null; });
            Retire(() => { pipeline.BigObjects?.Dispose(); pipeline.BigObjects = null; });
            Retire(() => { pipeline.Vegetation?.Dispose(); pipeline.Vegetation = null; });
            Retire(() => { pipeline.MainObjects?.Dispose(); pipeline.MainObjects = null; });
            if (failures is not null)
                throw new AggregateException(
                    $"Cell object retirement failed for engine cell ({placement.EngineCell.X},{placement.EngineCell.Y}).",
                    failures);
        }
        finally { _operating = false; }
    }

    /// <summary>30166870/30229200: main objects, including their real queued roads, water and decals.</summary>
    private void LoadMainObjects(CellPipeline pipeline)
    {
        Trace(MainObjectsPass);
        var content = ReadObjectFile(pipeline, ZoneCellObjectAsset.Objects, octree: true);
        // Direct field assignment makes the ownership transfer explicit for analysis and readers:
        // the per-cell pipeline retires this owner through UnloadCellObjects.
        pipeline.MainObjects = new ZoneCellObjectResources(_statics, _groups, pipeline.Placement);
        pipeline.Retained.Add(pipeline.MainObjects);
        var owner = pipeline.MainObjects;
        owner.Load(content);
        var created = owner.Instances.Count(instance => instance.State == ZoneCellObjectCreationState.Created);
        var skipped = owner.Instances.Count - created;
        pipeline.Passes.Add(new(MainObjectsPass,
            $"records={content.Objects.Count}, nodes={content.OctreeNodes.Count}, callbacks={owner.CompletedNodeCount}, " +
            $"treeResult={owner.MainTreeResult?.ToString() ?? "none"}, created={created}, skipped={skipped}, " +
            $"tables={owner.WereTablesLoaded}, complete={owner.IsFileLoadComplete}"));
    }

    /// <summary>3015E930/302253C0: the cell's sixteen authored sector descriptors.</summary>
    private void LoadSectorVegetation(CellPipeline pipeline)
    {
        Trace(SectorVegetationPass);
        using var stream = OpenCellAsset(pipeline.Placement, ZoneCellObjectAsset.Vegetation);
        var content = ZoneCellVegetationContent.Read(stream);
        pipeline.Vegetation = new ZoneCellVegetationResources(_statics, _groups, pipeline.Placement);
        pipeline.Retained.Add(pipeline.Vegetation);
        var owner = pipeline.Vegetation;
        owner.LoadSectors(content);
        var nonempty = content.Sectors.Count(sector => sector.ByteLength != 0);
        pipeline.Passes.Add(new(SectorVegetationPass,
            $"rows={content.Instances.Count}, bytes={content.FileLength}, nonemptySectors={nonempty}, attempts={owner.Instances.Count}"));
    }

    /// <summary>30161EF0: the cell's compiled visibility areas and their imported brushes.</summary>
    private void LoadVisibility(CellPipeline pipeline)
    {
        Trace(VisibilityPass);
        using var stream = OpenCellAsset(pipeline.Placement, ZoneCellObjectAsset.VisibilityAreas);
        var content = ZoneCellVisibilityContent.Read(stream);
        var cell = pipeline.Placement.EngineCell;
        var visibility = Retain(pipeline, _statics.LoadCellVisibility(cell.X, cell.Y, content));
        pipeline.Visibility = visibility;
        pipeline.Passes.Add(new(VisibilityPass,
            $"areas={visibility.CreatedAreaCount}/{content.AreaCount}, portals={content.PortalCount}, " +
            $"occlusion={content.OcclusionAreaCount}, importedBrushes={visibility.ImportedBrushCount}, " +
            $"physicalBodies={visibility.PhysicalBodyCount}, enabled={visibility.NativeLoadingEnabled}"));
    }

    /// <summary>3015EEC0/3201C750: the authored entity XML through the real class and proxy path.</summary>
    private void LoadXmlEntities(CellPipeline pipeline)
    {
        Trace(XmlEntitiesPass);
        using var stream = OpenCellAsset(pipeline.Placement, ZoneCellObjectAsset.Entities);
        var content = ZoneCellEntityContent.Load(stream);
        var batch = Retain(pipeline, _entities.CreateCellEntityBatch(_world, pipeline.Placement));
        pipeline.Entities = batch;
        var result = batch.Load(content);
        // Native loading completion is one explicit zero-time operation per retained batch; the
        // surrounding engine completion covers the static families only.
        var completed = batch.Status == ZoneCellEntityBatchStatus.Loaded ? batch.CompletePendingPhysics() : 0;
        pipeline.Passes.Add(new(XmlEntitiesPass,
            $"status={batch.Status}, authored={result.AuthoredEntityCount}, loaded={result.NativeLoadedCount}, " +
            $"skipped={result.NativeSkippedCount}, owned={result.OwnedEntityCount}, directProxyBodies={result.DirectProxyBodyCount}, " +
            $"completedRequests={completed}"));
    }

    /// <summary>30163530/30225050: the cell's type-tagged big-object records.</summary>
    private void LoadBigObjects(CellPipeline pipeline)
    {
        Trace(BigObjectsPass);
        var content = ReadObjectFile(pipeline, ZoneCellObjectAsset.BigObjects, octree: false);
        pipeline.BigObjects = new ZoneCellObjectResources(_statics, _groups, pipeline.Placement);
        pipeline.Retained.Add(pipeline.BigObjects);
        var owner = pipeline.BigObjects;
        owner.Load(content);
        var created = owner.Instances.Count(instance => instance.State == ZoneCellObjectCreationState.Created);
        pipeline.Passes.Add(new(BigObjectsPass,
            $"records={content.Objects.Count}, created={created}, skipped={owner.Instances.Count - created}, " +
            $"tables={owner.WereTablesLoaded}, complete={owner.IsFileLoadComplete}"));
    }

    /// <summary>
    /// The native final blocking pass 3017D480/3017CC30 is a deferred worker that consumes
    /// client/statobjs.dat, client/materials.dat and client/brush.dat. ZoneCellDeferredContent
    /// decodes those three inputs, the static-object service binds every native factory this pass
    /// needs (LoadModel/LoadMaterial/CreateBrush/CompleteCellBrush plus their retire catalogs), and
    /// ZoneCellDeferredBrushResources owns the pass per cell: load names through the bound
    /// factories, evaluate the region gate 30172560 with the evidence-bound active position
    /// (the proved dedicated initial (+0.0,+0.0,+0.0), FIRST_ENTRY_NOTES.md:824-829/:915-916) and
    /// radius (the recorded cry3dengine_dedicate constant R^2=1e9, FIRST_ENTRY_NOTES.md:336-338),
    /// then CreateBrush plus
    /// CompleteCellBrush for every record of every active region with physicalization through
    /// _LoadBrush, then one authored obstruction ray against the created static geometry.
    ///
    /// Capability deviations are named, not hidden: the native DeferredLoadingManager
    /// precache-task and CellStreamer lifetime are replaced by this managed owner, and per-frame
    /// re-gating during streaming (native re-runs the gate with budget 3.0) is load-time-only.
    /// A gate input whose evidence is missing refuses by name - never a speculative native
    /// dereference and never a constant standing in for an unevidenced runtime value - so an
    /// unbindable position or radius cannot silently empty the pass. (The earlier Seamless
    /// vtbl+0x128 accessor seam faulted with 0xC0000005 inside the callee in runner batch
    /// b3-20260922 despite passing every structural check, so no native position call remains.)
    ///
    /// The connected replacement still runs the explicit zero-time static completion at this
    /// exact position (World+100/3508BD10) first, because the native blocking pass is the last
    /// owner of this cell's queued construction requests: leaving them pending across the next
    /// cell's native entity callbacks is what the recorded multi-cell run showed to be unsafe.
    /// </summary>
    private bool LoadDeferredBlockingWork(CellPipeline pipeline)
    {
        Trace(DeferredBlockingWorkPass);
        var completed = _engine.CompletePendingStaticPhysics();

        ZoneCellDeferredGate gate;
        try
        {
            gate = BindDeferredGate();
        }
        catch (Exception error)
        {
            throw new ZoneCellPassUnavailableException(DeferredBlockingWorkPass, pipeline.Placement,
                "The native final blocking cell pass 3017D480/3017CC30 could not bind its region-gate " +
                $"inputs from recorded evidence: {error.Message} The World name, Zone origin, decoded inputs and bound " +
                "factories are available; a gate input without recorded evidence refuses here by name and is " +
                "never dereferenced speculatively or replaced by a constant. " +
                $"The connected zero-time static completion consumed {completed} queued requests at this " +
                "position. This cell is not loaded.", error);
        }

        ZoneCellDeferredContent content;
        using (var models = OpenCellAsset(pipeline.Placement, ZoneCellObjectAsset.DeferredModels))
        using (var materials = OpenCellAsset(pipeline.Placement, ZoneCellObjectAsset.DeferredMaterials))
        using (var brushes = OpenCellAsset(pipeline.Placement, ZoneCellObjectAsset.DeferredBrushes))
            content = ZoneCellDeferredContent.Read(models, materials, brushes);

        // The owner is rooted before its first native call so a failed attempt still retires
        // through UnloadCellObjects; Load additionally rolls its own partial resources back.
        var owner = new ZoneCellDeferredBrushResources(_statics, pipeline.Placement, _physics);
        pipeline.Deferred = owner;
        pipeline.Retained.Add(owner);
        try
        {
            owner.Load(content, gate);
        }
        catch (Exception error)
        {
            pipeline.Passes.Add(new(DeferredBlockingWorkPass,
                $"status=FAILED after {completed} queued completion(s): {error.Message}"));
            throw;
        }
        pipeline.Passes.Add(new(DeferredBlockingWorkPass, owner.DescribeLoad(completed)));
        return true;
    }

    private ZoneCellDeferredGate BindDeferredGate()
    {
        var selection = _selection ??
            throw new NotSupportedException(
                "The cell pipeline was constructed without the scene-resolved world layout; missing evidence is the " +
                "ZoneNativeWorldLayout that owns the deferred gate's World name/origin binding " +
                "(ZoneNativeEngine.ResolveWorldLayout, ZoneNativeEngine.WorldLayout.cs:14-41; FIRST_ENTRY_NOTES.md:316-322).");
        var context = _statics.ReadDeferredGateContext(selection);
        return ZoneCellDeferredGate.Create(context);
    }

    private ZoneCellObjectContent ReadObjectFile(CellPipeline pipeline, ZoneCellObjectAsset kind, bool octree)
    {
        using var stream = OpenCellAsset(pipeline.Placement, kind);
        return octree ? ZoneCellObjectContent.ReadOctree(stream) : ZoneCellObjectContent.ReadBigObjects(stream);
    }

    /// <summary>
    /// A missing authored input is a hard failure, not an empty family: the six configured cells
    /// contain all five files, so absence means the asset source or the layout is wrong.
    /// </summary>
    private Stream OpenCellAsset(ZoneWorldMapCellPlacement placement, ZoneCellObjectAsset kind) =>
        _assets.OpenCellObjectAsset(placement.Cell, kind) ??
            throw new FileNotFoundException("A required authored cell-object input is missing.",
                _assets.GetCellObjectPath(placement.Cell, kind));

    private CellPipeline RequirePipeline(ZoneWorldMapCellPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(placement.Cell);
        if (!ReferenceEquals(_world.GetCell(placement.Cell.Coordinate.X, placement.Cell.Coordinate.Y), placement.Cell))
            throw new ArgumentException("The cell-object pass belongs to another world.", nameof(placement));
        if ((uint)placement.EngineCell.X >= 8 || (uint)placement.EngineCell.Y >= 8)
            throw new ArgumentOutOfRangeException(nameof(placement), "Cell objects require a resolved cell in the native eight-by-eight window.");
        var key = CellKey(placement);
        if (!_cells.TryGetValue(key, out var pipeline))
            _cells.Add(key, pipeline = new(placement));
        return pipeline;
    }

    private static int CellKey(ZoneWorldMapCellPlacement placement) =>
        placement.EngineCell.X * 8 + placement.EngineCell.Y;

    /// <summary>Retains managed ownership before the first native call of that owner's pass.</summary>
    private static T Retain<T>(CellPipeline pipeline, T owner) where T : class
    {
        if (owner is null)
            throw new InvalidOperationException("A required cell-object owner did not return a resource.");
        pipeline.Retained.Add(owner);
        return owner;
    }

    private void Trace(string phase) => _trace?.Invoke(phase);

    private void Enter()
    {
        if (Environment.CurrentManagedThreadId != _thread)
            throw new InvalidOperationException("Cell-object passes must run on their engine's owning thread.");
        if (_operating) throw new InvalidOperationException("Cell-object passes cannot reenter their owner.");
        _operating = true;
    }

    /// <summary>One cell's retained owners and copied pass results. Managed references only.</summary>
    private sealed class CellPipeline
    {
        internal readonly List<object> Retained = [];
        internal CellPipeline(ZoneWorldMapCellPlacement placement) => Placement = placement;
        internal ZoneWorldMapCellPlacement Placement { get; }
        internal readonly List<ZoneCellObjectPassReport> Passes = [];
        internal bool Attempted;
        internal bool Loaded;
        internal ZoneCellPassUnavailableException? Refusal;
        internal ZoneCellObjectResources? MainObjects, BigObjects;
        internal ZoneCellVegetationResources? Vegetation;
        internal ZoneCellDeferredBrushResources? Deferred;
        internal ZoneNativeStaticObjectServices.VisibilityCell? Visibility;
        internal ZoneNativeEntityServices.CellEntityBatch? Entities;
    }
}

/// <summary>
/// The validated region gate of the deferred blocking pass (native 30172560 over the 16x16
/// authored regions of one cell, index X*16+Y, 64-unit regions, centre offset +0.5). The recovered
/// dedicated evaluation is d^2 = (sx*64 + (cellX-originX)*1024 - posX)^2 +
/// (sy*64 + (cellY-originY)*1024 - posY)^2 - 90.496, active while d^2 &lt; fVar23 * R^2 with
/// fVar23 = 1.0 (cry3dengine_dedicate.dll constants, R^2 = 1e9, activation radius near 31,623
/// units - FIRST_ENTRY_NOTES "Deferred blocking pass - recovered scope"). EngineCell already is
/// authored-minus-origin (ZoneWorldMapLayout), so the cell term is EngineCell*1024. The radius
/// input is bound from evidence, never read from unverified native memory: the recorded
/// cry3dengine_dedicate constant R^2 = 1e9 arrives in squared form (the unit-radius form near
/// 31,623 remains admitted as the same recorded constant expressed in units), and both resolve
/// to the same gate; any other magnitude refuses instead of substituting a value. For zone 129's
/// six cells the admitted radius exceeds every region distance from the evidence-bound active
/// position, so every authored region activates; the gate still runs per cell from its own
/// validated inputs.
/// </summary>
public sealed record ZoneCellDeferredGate(Vector3 ActivePosition, float ActivationRadiusSquared,
    string PositionSource, float RawRadiusValue)
{
    public const float RegionSize = 64f;
    public const float CellSize = 1024f;
    private const float CentreOffset = RegionSize * 0.5f;
    private const float RadicandOffset = 90.496f;
    /// <summary>A gate below one authored cell cannot admit that cell's own regions.</summary>
    private const float MinimumRadius = CellSize;
    private const float MaximumRadius = 1_000_000f;

    public float ActivationRadius => MathF.Sqrt(ActivationRadiusSquared);

    public bool IsRegionActive(int engineCellX, int engineCellY, ZoneCellDeferredSector region)
    {
        var dx = region.LocalX * RegionSize + CentreOffset + engineCellX * CellSize - ActivePosition.X;
        var dy = region.LocalY * RegionSize + CentreOffset + engineCellY * CellSize - ActivePosition.Y;
        return dx * dx + dy * dy - RadicandOffset < ActivationRadiusSquared;
    }

    internal static ZoneCellDeferredGate Create(ZoneNativeStaticObjectServices.ZoneCellDeferredGateContext context)
    {
        var raw = context.RawRadiusValue;
        if (!float.IsFinite(raw) || raw <= 0f)
            throw new InvalidOperationException(
                $"The bound activation-radius input yielded the implausible value {raw} (source: {context.PositionSource}).");
        // Dedicated R^2 = 1e9 arrives squared; the general radius form arrives near 31,623.
        var squared = raw >= 1e7f ? raw : raw * raw;
        var radius = MathF.Sqrt(squared);
        if (radius < MinimumRadius || radius > MaximumRadius)
            throw new InvalidOperationException(
                $"The activation radius {radius} units resolved from the bound radius value {raw} " +
                $"(source: {context.PositionSource}) is outside the admitted range [{MinimumRadius}, {MaximumRadius}].");
        var position = context.ActivePosition;
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z) ||
            MathF.Abs(position.X) > 1e7f || MathF.Abs(position.Y) > 1e7f || MathF.Abs(position.Z) > 1e7f)
            throw new InvalidOperationException(
                $"The active position ({position.X}, {position.Y}, {position.Z}) from {context.PositionSource} is outside the admitted range.");
        return new(position, squared, context.PositionSource, raw);
    }
}

/// <summary>
/// Owns one cell's deferred blocking pass resources - the replacement for native 3017D480/3017CC30
/// over statobjs.dat, materials.dat and brush.dat. Load resolves every referenced model and
/// material name once per cell through the static-object service's bound factories
/// (LoadModel/LoadMaterial), evaluates the region gate, creates each active record's CBrush at the
/// engine-cell translation (3021E840 adds it without transposing the matrix) and completes its
/// serialized-cell publication metadata (CompleteCellBrush). Physicalization is the native
/// _LoadBrush -> CBrush::Physicalize result, verified per brush by the service; one authored
/// obstruction ray against the largest physicalized bounds then proves the created geometry
/// answers scene queries. A failed Load rolls every resource it created back through the proven
/// retire catalogs before rethrowing. Dispose runs first in
/// ZoneSceneCellObjectLoader.UnloadCellObjects, matching native 30156F10 -> 30175950 before
/// XML/visibility/outdoor retirement, and retires brushes, then materials, then models - the same
/// family order as the static-object service's own final sweep. This owner holds managed
/// references only; native ownership stays rooted in the static-object service, whose retire
/// catalogs remain the final backstop.
/// </summary>
public sealed class ZoneCellDeferredBrushResources : IDisposable
{
    private const int MaximumProbeCandidates = 12;
    private const int ProbesPerCandidate = 5;

    private readonly ZoneNativeStaticObjectServices _services;
    private readonly ZoneNativeTerrainPhysicsWorld? _physics;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly Dictionary<string, ZoneNativeStaticObjectServices.Model> _models = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ZoneNativeStaticObjectServices.Material> _materials = new(StringComparer.Ordinal);
    private readonly List<ZoneNativeStaticObjectServices.Brush> _brushes = [];
    private readonly List<(Vector3 Min, Vector3 Max, int AuthoredOrder)> _physicalizedBounds = [];
    private bool _operating, _retiring, _disposed;

    public ZoneWorldMapCellPlacement Placement { get; }
    public ZoneCellDeferredContent? Content { get; private set; }
    public ZoneCellDeferredGate? Gate { get; private set; }
    public bool IsLoadComplete { get; private set; }
    public int LoadedModelCount => _models.Count;
    public int LoadedMaterialCount => _materials.Count;
    public int CreatedBrushCount { get; private set; }
    public int SkippedBrushCount { get; private set; }
    public int PhysicalizedBrushCount { get; private set; }
    public int ActiveRegionCount { get; private set; }
    public int GatedRecordCount { get; private set; }
    public int InvalidIndexCount { get; private set; }
    public int UnnamedReferenceCount { get; private set; }
    public int ModelLoadFailureCount { get; private set; }
    public int MaterialLoadFailureCount { get; private set; }
    public string? ObstructionResult { get; private set; }
    public bool IsDisposed => _disposed;

    internal ZoneCellDeferredBrushResources(ZoneNativeStaticObjectServices services,
        ZoneWorldMapCellPlacement placement, ZoneNativeTerrainPhysicsWorld? physics)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(placement);
        services.VerifyResourceOwner();
        if ((uint)placement.EngineCell.X >= 8 || (uint)placement.EngineCell.Y >= 8)
            throw new ArgumentOutOfRangeException(nameof(placement),
                "Deferred cell brushes require a resolved cell in the native eight-by-eight window.");
        _services = services;
        _physics = physics;
        Placement = placement;
    }

    public void Load(ZoneCellDeferredContent content, ZoneCellDeferredGate gate)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(gate);
        Enter();
        try
        {
            if (Content is not null)
                throw new InvalidOperationException("Retire this deferred attempt before loading it again.");
            Content = content; // Retain the failed attempt as well as every successful factory.
            Gate = gate;
            LoadResources(content);
            CreateActiveRecords(content, gate);
            ObstructionResult = ProbeObstruction();
            IsLoadComplete = true;
        }
        catch (Exception error)
        {
            // Failed-load rollback: every resource this attempt created retires through the proven
            // catalogs before the original failure leaves the pass. Uncertain cleanup aggregates
            // instead of hiding the first exception.
            try
            {
                var failures = RetireResources();
                if (failures.Count != 0)
                    throw new AggregateException("Deferred pass rollback did not retire every partial resource.", failures);
            }
            catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
            throw;
        }
        finally { _operating = false; }
    }

    private void LoadResources(ZoneCellDeferredContent content)
    {
        foreach (var slot in content.ModelNames)
        {
            if (slot.Name.Length == 0 || _models.ContainsKey(slot.Name)) continue;
            var model = _services.LoadModel(slot.Name);
            if (model is null) ModelLoadFailureCount++;
            else _models.Add(slot.Name, model);
        }
        foreach (var slot in content.MaterialNames)
        {
            if (slot.Name.Length == 0 || _materials.ContainsKey(slot.Name)) continue;
            var material = _services.LoadMaterial(slot.Name);
            if (material is null) MaterialLoadFailureCount++;
            else _materials.Add(slot.Name, material);
        }
    }

    private void CreateActiveRecords(ZoneCellDeferredContent content, ZoneCellDeferredGate gate)
    {
        var cellX = Placement.EngineCell.X;
        var cellY = Placement.EngineCell.Y;
        var translation = new Vector3(cellX << 10, cellY << 10, 0);
        foreach (var region in content.Sectors)
        {
            if (!gate.IsRegionActive(cellX, cellY, region)) continue;
            ActiveRegionCount++;
            foreach (var record in region.Instances)
            {
                GatedRecordCount++;
                var model = ResolveModel(content, record.ModelIndex);
                var material = ResolveMaterial(content, record.MaterialIndex);
                var brush = _services.CreateBrush(translation, record.RawPayload, model, material);
                if (brush is null)
                {
                    SkippedBrushCount++; // Native admission skipped this record; a skip stays a skip.
                    continue;
                }
                _brushes.Add(brush); // Rooted before the publication metadata call.
                _services.CompleteCellBrush(brush);
                CreatedBrushCount++;
                if (brush.HasPhysicalEntity)
                {
                    PhysicalizedBrushCount++;
                    _physicalizedBounds.Add((record.BoundsMin, record.BoundsMax, record.SourceOrder));
                }
            }
        }
    }

    private ZoneNativeStaticObjectServices.Model? ResolveModel(ZoneCellDeferredContent content, int index)
    {
        if (index < 0 || index >= content.ModelNames.Count) { InvalidIndexCount++; return null; }
        var name = content.ModelNames[index].Name;
        if (name.Length == 0) { UnnamedReferenceCount++; return null; }
        return _models.TryGetValue(name, out var model) ? model : null; // A native load failure nulls this record's reference.
    }

    private ZoneNativeStaticObjectServices.Material? ResolveMaterial(ZoneCellDeferredContent content, int index)
    {
        if (index < 0 || index >= content.MaterialNames.Count) { InvalidIndexCount++; return null; }
        var name = content.MaterialNames[index].Name;
        if (name.Length == 0) { UnnamedReferenceCount++; return null; }
        return _materials.TryGetValue(name, out var material) ? material : null;
    }

    /// <summary>
    /// One authored obstruction query against the created static geometry: vertical rays through
    /// the largest physicalized bounds at their engine-window position (the same translation
    /// 3021E840 applied). The first blocking hit on the world's real static collision geometry
    /// answers with distance, position and authored part; a miss stays a miss and reports its
    /// probe count instead of claiming an obstruction.
    /// </summary>
    private string ProbeObstruction()
    {
        if (_physics is null)
            return "not-run(no physics world bound)";
        if (_physicalizedBounds.Count == 0)
            return $"miss(no physicalized brush among {CreatedBrushCount} created)";
        var translation = new Vector3(Placement.EngineCell.X << 10, Placement.EngineCell.Y << 10, 0);
        var candidates = _physicalizedBounds
            .OrderByDescending(box => BoxVolume(box.Min, box.Max))
            .Take(MaximumProbeCandidates)
            .ToArray();
        var probes = 0;
        foreach (var box in candidates)
        {
            var min = box.Min + translation;
            var max = box.Max + translation;
            if (max.X < min.X) (min.X, max.X) = (max.X, min.X);
            if (max.Y < min.Y) (min.Y, max.Y) = (max.Y, min.Y);
            if (max.Z < min.Z) (min.Z, max.Z) = (max.Z, min.Z);
            var size = max - min;
            var centre = min + size * 0.5f;
            var reachX = MathF.Max(size.X * 0.25f, 1f);
            var reachY = MathF.Max(size.Y * 0.25f, 1f);
            var offsets = new (float X, float Y)[]
            {
                (0f, 0f), (reachX, 0f), (-reachX, 0f), (0f, reachY), (0f, -reachY)
            };
            var origin = new Vector3(centre.X, centre.Y, max.Z + 50f);
            var drop = MathF.Max(size.Z, 8f) + 100f;
            foreach (var (offsetX, offsetY) in offsets)
            {
                probes++;
                if (_physics.RaycastStaticScene(origin + new Vector3(offsetX, offsetY, 0f),
                        new Vector3(0f, 0f, -drop), out var hit))
                    return $"hit(distance={hit.Distance}, position=({hit.Position.X},{hit.Position.Y},{hit.Position.Z}), " +
                        $"partId={hit.PartId}, authoredRecord={box.AuthoredOrder}, " +
                        $"bounds=({min.X},{min.Y},{min.Z})-({max.X},{max.Y},{max.Z}), probes={probes})";
            }
        }
        return $"miss(probes={probes}, candidates={candidates.Length}, physicalized={PhysicalizedBrushCount})";
    }

    private static float BoxVolume(Vector3 min, Vector3 max) =>
        MathF.Abs(max.X - min.X) * MathF.Abs(max.Y - min.Y) * MathF.Abs(max.Z - min.Z);

    internal string DescribeLoad(int completedRequests)
    {
        var content = Content!;
        var gate = Gate!;
        var unnamedModelSlots = content.ModelNames.Count(slot => slot.Name.Length == 0);
        var unnamedMaterialSlots = content.MaterialNames.Count(slot => slot.Name.Length == 0);
        var nonemptyRegions = content.Sectors.Count(sector => sector.ByteLength != 0);
        return $"files(statobjs={content.ModelTable.FileLength}B/{content.ModelTable.AuthoredCount} model names, " +
            $"materials={content.MaterialTable.FileLength}B/{content.MaterialTable.AuthoredCount} names, " +
            $"brush={content.BrushFileLength}B/{content.Brushes.Count} records in {nonemptyRegions} regions), " +
            $"gate(position=({gate.ActivePosition.X},{gate.ActivePosition.Y},{gate.ActivePosition.Z}) via {gate.PositionSource}, " +
            $"radius={gate.ActivationRadius} (raw {gate.RawRadiusValue})), " +
            $"activeRegions={ActiveRegionCount}/256, gatedRecords={GatedRecordCount}, created={CreatedBrushCount}, " +
            $"skipped={SkippedBrushCount}, invalidIndices={InvalidIndexCount}, unnamedRefs={UnnamedReferenceCount}, " +
            $"models={LoadedModelCount} loaded ({ModelLoadFailureCount} failed, {unnamedModelSlots} empty slots), " +
            $"materials={LoadedMaterialCount} loaded ({MaterialLoadFailureCount} failed, {unnamedMaterialSlots} empty slots), " +
            $"physicalized={PhysicalizedBrushCount}, obstruction={ObstructionResult}, " +
            $"queuedCompleted={completedRequests}, complete={IsLoadComplete}";
    }

    /// <summary>
    /// Brushes newest first, then materials, then models - the static-object service's own family
    /// order. Every resource is attempted even when an earlier one fails; the failures are returned
    /// for aggregation so one uncertain retire never abandons the remaining live references.
    /// </summary>
    private List<Exception> RetireResources()
    {
        List<Exception>? failures = null;
        void Retire(Action action)
        {
            try { action(); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        for (var index = _brushes.Count - 1; index >= 0; index--)
        {
            var brush = _brushes[index];
            Retire(() => { brush.Dispose(); _brushes.Remove(brush); });
        }
        foreach (var pair in _materials.ToArray())
            Retire(() => { pair.Value.Dispose(); _materials.Remove(pair.Key); });
        foreach (var pair in _models.ToArray())
            Retire(() => { pair.Value.Dispose(); _models.Remove(pair.Key); });
        return failures ?? [];
    }

    public void Dispose()
    {
        if (_disposed) return;
        Enter(retirement: true);
        _retiring = true;
        try
        {
            var failures = RetireResources();
            // Retain ownership on uncertainty: the static-object service's retire catalogs stay the
            // final backstop and its lease is released only after every step succeeds.
            if (failures.Count == 0) _disposed = true;
            else throw new AggregateException("Deferred cell brush retirement failed.", failures);
        }
        finally { _operating = false; }
    }

    private void Enter(bool retirement = false)
    {
        if (Environment.CurrentManagedThreadId != _thread)
            throw new InvalidOperationException("Deferred cell brushes require their native engine's owning thread.");
        ObjectDisposedException.ThrowIf(_disposed || (_retiring && !retirement), this);
        if (_operating) throw new InvalidOperationException("Deferred cell brush operations cannot reenter their owner.");
        if (!retirement) _services.VerifyResourceOwner();
        _operating = true;
    }
}
