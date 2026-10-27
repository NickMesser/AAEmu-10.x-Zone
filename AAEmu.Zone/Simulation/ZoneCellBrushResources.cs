using AAEmu.Zone.Content;

namespace AAEmu.Zone.Simulation;

/// <summary>One authored brush and the native factory's result, including configured skips.</summary>
public sealed class ZoneCellBrushInstance
{
    public ZoneCellBrushObject Authored { get; }
    public ZoneNativeStaticObjectServices.Brush? Resource { get; internal set; }
    internal ZoneCellBrushInstance(ZoneCellBrushObject authored) => Authored = authored;
}

/// <summary>
/// Records the outdoor brush subset from one decoded cell-object file, including native skips.
/// Requires the engine's actual outdoor octree root and loads the temporary tables in source
/// order. Native30225050 skips type-one records when that root has no visibility-area association,
/// in both main and big files. Indoor brushes require a separate visibility-area owner.
/// This helper does not implement main-node configuration gates, callbacks, other families or
/// complete file loading; successful construction does not establish cell readiness.
/// </summary>
public sealed class ZoneCellBrushResources : IDisposable
{
    private readonly ZoneNativeStaticObjectServices _services;
    private ZoneCellObjectTables? _tables;
    private readonly List<ZoneCellBrushInstance> _instances = [];
    private bool _disposed;

    public ZoneWorldMapCellPlacement Placement { get; }
    public ZoneCellObjectContent Content { get; }
    public IReadOnlyList<ZoneCellBrushInstance> Instances { get; }
    public int NonBrushObjectCount { get; }

    private ZoneCellBrushResources(ZoneNativeStaticObjectServices services,
        ZoneWorldMapCellPlacement placement, ZoneCellObjectContent content)
    {
        _services = services;
        Placement = placement;
        Content = content;
        Instances = _instances.AsReadOnly();
        NonBrushObjectCount = content.Objects.Count(value => value is not ZoneCellBrushObject);
    }

    public static ZoneCellBrushResources Create(ZoneNativeStaticObjectServices services,
        ZoneWorldMapCellPlacement placement, ZoneCellObjectContent content)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(content);
        if ((uint)placement.EngineCell.X >= 8 || (uint)placement.EngineCell.Y >= 8)
            throw new ArgumentOutOfRangeException(nameof(placement), "Brush placement requires a resolved cell in the native 8 by 8 window.");
        var owner = new ZoneCellBrushResources(services, placement, content);
        try
        {
            owner.Load();
            return owner;
        }
        catch (Exception error)
        {
            // Every allocation also belongs to the engine's strongly rooted service. A failed
            // native cleanup therefore keeps its resources alive even if this factory cannot return.
            try { owner.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
            throw;
        }
    }

    private void Load()
    {
        var root = _services.CaptureOutdoorCellObjectRoot();
        _tables = ZoneCellObjectTables.Create(_services, Content);
        if (_services.CaptureOutdoorCellObjectRoot() != root)
            throw new InvalidOperationException("The engine's outdoor octree root changed while loading cell resource tables.");

        _instances.EnsureCapacity(Content.Objects.Count - NonBrushObjectCount);
        foreach (var record in Content.Objects)
        {
            if (record is not ZoneCellBrushObject brush) continue;
            // The root exists but its CVisArea association is null. This native admission
            // applies to both file kinds; do not force the raw brush factory to bypass it.
            _instances.Add(new ZoneCellBrushInstance(brush));
        }
        _tables.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        // Collection rollback retires owned instances in reverse creation order. Each native
        // brush performs its own proven unregister/physics/reference retirement. Stop and retain
        // the collection on failure; do not release dependencies past an uncertain native call.
        for (var i = _instances.Count - 1; i >= 0; i--)
            _instances[i].Resource?.Dispose();
        _tables?.Dispose();
        _disposed = true;
    }
}
