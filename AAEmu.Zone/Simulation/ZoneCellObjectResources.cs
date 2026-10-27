using System.Numerics;
using AAEmu.Zone.Content;

namespace AAEmu.Zone.Simulation;

public enum ZoneCellObjectCreationState
{
    Pending,
    SkippedOutdoorBrush,
    SkippedNativeFactory,
    Created
}

/// <summary>One ordered factory attempt. Pending also describes an interrupted native call.</summary>
public sealed class ZoneCellObjectInstance
{
    public ZoneCellObjectRecord Authored { get; }
    public IDisposable? Resource { get; internal set; }
    public ZoneCellObjectCreationState State { get; internal set; }
    internal ZoneCellObjectInstance(ZoneCellObjectRecord authored) => Authored = authored;
}

/// <summary>
/// Loads one outdoor main/big object file through the native tree and existing family owners.
/// Shares its temporary resource tables, preserves interleaved source order and per-node gates,
/// and retains partial resources on failure. Construct and retain this owner before calling Load;
/// a failed attempt cannot be replayed. Complete pending static construction before retirement.
/// This is one file pass: sectors, visibility, XML entities and deferred work remain cell-level
/// responsibilities. Queued roads are not compiled meshes; file completion is not scene readiness.
/// </summary>
public sealed class ZoneCellObjectResources : IDisposable
{
    private readonly ZoneNativeStaticObjectServices _services;
    private readonly List<ZoneCellObjectInstance> _instances = [];
    private readonly int _thread;
    private ZoneCellObjectTables? _tables;
    private nint _root;
    private bool _operating, _retiring, _disposed;

    public ZoneWorldMapCellPlacement Placement { get; }
    public ZoneNativeStaticObjectServices.VegetationGroups Groups { get; }
    public ZoneCellObjectContent? Content { get; private set; }
    public IReadOnlyList<ZoneCellObjectInstance> Instances { get; }
    public bool WereTablesLoaded { get; private set; }
    public int CompletedNodeCount { get; private set; }
    public int SkippedObjectBlockCount { get; private set; }
    /// <summary>
    /// Native recursive result for a main file: positive node count, or zero for a stopped tree.
    /// Null for big files or an interrupted call. The native outer wrapper ignores a zero result.
    /// </summary>
    public int? MainTreeResult { get; private set; }
    public bool IsFileLoadComplete { get; private set; }
    public bool IsDisposed => _disposed;

    public ZoneCellObjectResources(ZoneNativeStaticObjectServices services,
        ZoneNativeStaticObjectServices.VegetationGroups groups, ZoneWorldMapCellPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(placement);
        services.VerifyResourceOwner();
        if (!ReferenceEquals(groups.Owner, services) || groups.IsDisposed || !groups.Loaded)
            throw new ArgumentException("Cell objects require this service's loaded world vegetation catalog.", nameof(groups));
        if (!ReferenceEquals(groups.World.GetCell(placement.Cell.Coordinate.X, placement.Cell.Coordinate.Y), placement.Cell))
            throw new ArgumentException("The cell and vegetation catalog belong to different worlds.", nameof(placement));
        if ((uint)placement.EngineCell.X >= 8 || (uint)placement.EngineCell.Y >= 8)
            throw new ArgumentOutOfRangeException(nameof(placement), "Cell objects require a resolved cell in the native eight-by-eight window.");
        _services = services;
        _thread = groups.OwnerThread;
        Placement = placement;
        Groups = groups;
        Instances = _instances.AsReadOnly();
    }

    public void Load(ZoneCellObjectContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Enter();
        try
        {
            if (Content is not null)
                throw new InvalidOperationException("Retire this file attempt before loading another object file.");
            Content = content; // Retain the failed attempt as well as every successful factory.
            _instances.EnsureCapacity(content.Objects.Count);
            _root = _services.EnsureOutdoorCellObjectRoot();
            var loadTables = content.Kind == ZoneCellObjectFileKind.BigObjects ||
                _services.ShouldLoadMainCellObjectResources();
            if (loadTables)
            {
                _tables = ZoneCellObjectTables.Create(_services, content);
                WereTablesLoaded = true;
            }
            ValidateRoot();
            if (content.Kind == ZoneCellObjectFileKind.BigObjects)
            {
                foreach (var record in content.Objects) Create(record);
                //30163530 has no per-file/per-record CSystem loading callback.
            }
            else
            {
                MainTreeResult = LoadNode(content.OctreeNodes[0], _root);
            }
            _tables?.Dispose(); // Native wrappers release materials before models on return.
            IsFileLoadComplete = true;
        }
        finally { _operating = false; }
    }

    private int LoadNode(ZoneCellObjectOctreeNode node, nint nativeNode)
    {
        //30229200 checks this before reading its header. Zero aborts this subtree and siblings;
        // the parent handles the failed child, while the outer main-file wrapper ignores zero.
        if (!_services.ShouldReadMainCellObjectNode()) return 0;
        if (node.ObjectBlockLength != 0)
        {
            if (_services.ShouldDispatchMainCellObjectNode())
            {
                if (_tables is null)
                    throw new InvalidOperationException("Main-file dispatch became enabled after its resource tables were skipped; retain the partial load.");
                foreach (var index in node.ObjectIndices) Create(Content!.Objects[index]);
            }
            else SkippedObjectBlockCount++;
        }
        // Both empty and configuration-skipped valid blocks reach this callback before children.
        _services.CompleteMainCellObjectNodeLoad();
        CompletedNodeCount++;
        ValidateRoot();
        var result = 1;
        foreach (var childIndex in node.ChildIndices)
        {
            var child = Content!.OctreeNodes[childIndex];
            var octant = child.ChildOctant!.Value;
            var nativeChild = _services.EnsureMainCellObjectChild(nativeNode, octant);
            var childResult = LoadNode(child, nativeChild);
            _services.CompleteMainCellObjectChild(nativeNode, octant, nativeChild, childResult);
            if (childResult == 0) return 0;
            result = checked(result + childResult);
        }
        return result;
    }

    private void Create(ZoneCellObjectRecord record)
    {
        ValidateRoot();
        _tables!.ValidateFor(_services, Content!);
        var instance = new ZoneCellObjectInstance(record);
        _instances.Add(instance); // Retain ownership before native creation or metadata callbacks.
        var x = Placement.EngineCell.X;
        var y = Placement.EngineCell.Y;
        switch (record)
        {
            case ZoneCellBrushObject:
                // This file owner admits only actual outdoor roots. The native area gate applies
                // to both file kinds; indoor brushes belong to the real visibility-area loader.
                instance.State = ZoneCellObjectCreationState.SkippedOutdoorBrush;
                return;
            case ZoneCellVegetationObject vegetation:
                var tree = _services.CreateVegetation(Groups, new Vector3(x << 10, y << 10, 0), vegetation.RawPayload);
                instance.Resource = tree;
                if (tree is not null) _services.CompleteCellVegetation(tree);
                break;
            case ZoneCellDecalObject decal:
                var mark = _services.CreateDecal(x, y, decal.RawPayload, _tables.Materials);
                instance.Resource = mark;
                if (mark is not null) _services.CompleteCellDecal(mark);
                break;
            case ZoneCellRoadObject road:
                var path = _services.CreateQueuedRoad(x, y, road.RawPayload, _tables.Materials);
                instance.Resource = path;
                if (path is not null) _services.CompleteCellRoad(path);
                break;
            case ZoneCellWaterVolumeObject water:
                var volume = _services.CreateWaterVolume(x, y, water.RawPayload, _tables.Materials);
                instance.Resource = volume;
                if (volume is not null) _services.CompleteCellWaterVolume(volume);
                break;
            default:
                throw new NotSupportedException($"No native resource owner exists for serialized object type {record.Type}.");
        }
        instance.State = instance.Resource is null
            ? ZoneCellObjectCreationState.SkippedNativeFactory : ZoneCellObjectCreationState.Created;
    }

    private void ValidateRoot()
    {
        if (_services.CaptureOutdoorCellObjectRoot() != _root)
            throw new InvalidOperationException("The engine's object root changed during file loading; retain its resources.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        Enter(retirement: true);
        _retiring = true;
        try
        {
            // Stop at the first failure. Earlier resources and table references remain retained;
            // the engine's native hierarchy outlives all these registered render nodes.
            for (var index = _instances.Count - 1; index >= 0; index--)
                _instances[index].Resource?.Dispose();
            _tables?.Dispose();
            _disposed = true;
        }
        finally { _operating = false; }
    }

    private void Enter(bool retirement = false)
    {
        if (Environment.CurrentManagedThreadId != _thread)
            throw new InvalidOperationException("Cell objects require their native engine's owning thread.");
        ObjectDisposedException.ThrowIf(_disposed || (_retiring && !retirement), this);
        if (_operating) throw new InvalidOperationException("Cell object loading and retirement cannot reenter their owner.");
        if (!retirement)
        {
            _services.VerifyResourceOwner();
            ObjectDisposedException.ThrowIf(Groups.IsDisposed, Groups);
        }
        _operating = true;
    }
}
