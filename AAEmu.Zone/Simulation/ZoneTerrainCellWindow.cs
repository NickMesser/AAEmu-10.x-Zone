using AAEmu.Zone.Content;

namespace AAEmu.Zone.Simulation;

public sealed class ZoneTerrainCellOccupancy
{
    public ZoneWorldMapCellPlacement Placement { get; }
    /// <summary>Seamless+30: the synchronous dedicated attempt sets this before engine entry.</summary>
    public bool TerrainLoadRequested => true;
    public ZoneTerrainCellLoadResult? Result { get; internal set; }
    internal ZoneTerrainCellOccupancy(ZoneWorldMapCellPlacement placement) => Placement = placement;
}

public sealed record ZoneTerrainZoneLoadReport(IReadOnlyList<ZoneTerrainCellLoadResult> Terrain,
    IReadOnlyList<bool> Objects);

/// <summary>A retained dedicated object attempt, separate from terrain occupancy and native resource ownership.</summary>
public sealed class ZoneCellObjectOccupancy
{
    public ZoneWorldMapCellPlacement Placement { get; }
    /// <summary>Null means loading has not returned; false can still leave partial native resources.</summary>
    public bool? Result { get; internal set; }
    internal ZoneCellObjectOccupancy(ZoneWorldMapCellPlacement placement) => Placement = placement;
}

/// <summary>
/// Dedicated Seamless 3656B5D0/36569570 terrain occupancy. Failed and empty loads retain their slot;
/// a second attempt cannot replace it until engine unload returns. All mutation belongs to one scene
/// dispatcher. Nested mutation, including disposal from a backend callback, is rejected explicitly.
/// Object attempts occupy a separate table and retire before terrain. The required backend owns
/// their actual native resources and physical-world lifetime, including partial failed loads.
/// </summary>
public sealed class ZoneTerrainCellWindow : IDisposable
{
    private readonly ZoneTerrainCellOccupancy?[] _slots = new ZoneTerrainCellOccupancy?[64];
    private readonly ZoneCellObjectOccupancy?[] _objectSlots = new ZoneCellObjectOccupancy?[64];
    private bool _operating;
    public ZoneTerrainCellEngine Engine { get; }
    public bool IsDisposed { get; private set; }

    public ZoneTerrainCellWindow(ZoneTerrainCellEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        engine.Claim(this);
        Engine = engine;
    }

    public ZoneTerrainCellOccupancy? GetOccupancy(int x, int y) =>
        (uint)x < 8 && (uint)y < 8 ? _slots[x * 8 + y] : null;

    public ZoneCellObjectOccupancy? GetObjectOccupancy(int x, int y) =>
        (uint)x < 8 && (uint)y < 8 ? _objectSlots[x * 8 + y] : null;

    public ZoneTerrainCellLoadResult TryLoadCell(ZoneWorldMapCellPlacement placement)
    {
        Enter();
        try { return LoadCore(placement); }
        finally { _operating = false; }
    }

    /// <summary>
    /// 365704D0's dedicated passes: every terrain attempt, then every object attempt, in layout order.
    /// Native booleans do not short-circuit either pass. This report does not assert scene readiness.
    /// </summary>
    public ZoneTerrainZoneLoadReport LoadZone(ZoneWorldMapLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!ReferenceEquals(layout.World, Engine.World)) throw new ArgumentException("The layout belongs to another world.", nameof(layout));
        Enter();
        try
        {
            var terrain = new List<ZoneTerrainCellLoadResult>(layout.Cells.Count);
            var objects = new List<bool>(layout.Cells.Count);
            foreach (var placement in layout.Cells) terrain.Add(LoadCore(placement));
            foreach (var placement in layout.Cells) objects.Add(LoadObjectsCore(placement));
            return new(terrain.AsReadOnly(), objects.AsReadOnly());
        }
        finally { _operating = false; }
    }

    /// <summary>
    /// 365704D0's second dedicated pass for one cell. It is exposed separately only so a caller that
    /// owns per-cell failure handling can keep the native order (every terrain attempt, then every
    /// object attempt) while still loading the remaining cells after one cell's required pass is
    /// refused. The gates, occupancy publication and retained attempts are identical to LoadZone,
    /// which remains the one-shot whole-window equivalent. A throw leaves the published attempt in
    /// place, so its partial resources are still unloaded later.
    /// </summary>
    public bool TryLoadCellObjects(ZoneWorldMapCellPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        Enter();
        try { return LoadObjectsCore(placement); }
        finally { _operating = false; }
    }

    public bool UnloadCell(int x, int y, bool refreshPhysics)
    {
        Enter();
        try { return UnloadCore(x, y, refreshPhysics); }
        finally { _operating = false; }
    }

    private ZoneTerrainCellLoadResult LoadCore(ZoneWorldMapCellPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        if (!ReferenceEquals(Engine.World.GetCell(placement.Cell.Coordinate.X, placement.Cell.Coordinate.Y), placement.Cell))
            throw new ArgumentException("The cell belongs to another world.", nameof(placement));
        var cell = placement.EngineCell;
        if ((uint)cell.X >= 8 || (uint)cell.Y >= 8) return new(ZoneTerrainCellLoadStatus.InvalidCell);
        var index = cell.X * 8 + cell.Y;
        if (_slots[index] is not null) return new(ZoneTerrainCellLoadStatus.Occupied);
        var owner = new ZoneTerrainCellOccupancy(placement);
        _slots[index] = owner; // 3656B6D4: publish before opening any asset or calling the engine.
        var result = Engine.LoadCell(placement);
        owner.Result = result;
        return result;
    }

    private bool UnloadCore(int x, int y, bool refreshPhysics)
    {
        var objects = GetObjectOccupancy(x, y);
        if (objects is not null)
        {
            //365697E0 calls Seamless+110(mask0x18,flag0) before +D8 terrain teardown.
            // An object failure must leave both the attempt and its terrain dependencies alive.
            Engine.UnloadCellObjects(objects.Placement);
            _objectSlots[x * 8 + y] = null;
        }
        var owner = GetOccupancy(x, y);
        if (owner is null) return objects is not null;
        Engine.UnloadCell(owner.Placement, refreshPhysics);
        _slots[x * 8 + y] = null; // 36569636: clear only after the engine returns.
        return true;
    }

    private bool LoadObjectsCore(ZoneWorldMapCellPlacement placement)
    {
        var cell = placement.EngineCell;
        if ((uint)cell.X >= 8 || (uint)cell.Y >= 8) return false;
        var index = cell.X * 8 + cell.Y;
        if (_objectSlots[index] is not null) return false;
        var owner = new ZoneCellObjectOccupancy(placement);
        _objectSlots[index] = owner; // Publish before a failing native load can own resources.
        var result = Engine.LoadCellObjects(placement);
        owner.Result = result;
        return result;
    }

    private void Enter()
    {
        if (_operating) throw new InvalidOperationException("Terrain cell operations cannot reenter their owning window.");
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        _operating = true;
    }

    public void Dispose()
    {
        if (_operating) throw new InvalidOperationException("Terrain cell operations cannot reenter their owning window.");
        _operating = true;
        IsDisposed = true;
        List<Exception>? errors = null;
        try
        {
            // Replacement owner cleanup order. Retain any slot whose engine unload throws so a
            // subsequent Dispose can retry it; a successful unload is never repeated.
            for (var x = 0; x < 8; x++)
                for (var y = 0; y < 8; y++)
                    try { UnloadCore(x, y, true); }
                    catch (Exception error) { (errors ??= []).Add(error); }
        }
        finally { _operating = false; }
        if (errors is not null) throw new AggregateException("Terrain cells failed during retirement.", errors);
    }
}
