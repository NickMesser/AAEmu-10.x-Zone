using System.Buffers.Binary;
using System.Xml;

using AAEmu.Zone.Content;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Scene;

/// <summary>
/// Authored cell objects retained for a managed scene. These are decoded placements and resource
/// references, not physicalized meshes or running engine entities.
/// </summary>
public sealed record ZoneManagedCellObjects(ZoneCellObjectContent? Objects,
    ZoneCellVegetationContent? Vegetation, ZoneCellEntityContent? Entities,
    ZoneCellObjectContent? BigObjects, IReadOnlyList<string> Diagnostics);

public sealed record ZoneManagedSceneCellLoadReport(ZoneWorldMapCellPlacement Placement,
    ZoneTerrainCellLoadResult Terrain, ZoneManagedCellObjects Objects);

/// <summary>
/// Readiness is deliberately limited to authored terrain. Object decoding does not establish
/// static-mesh collision, entity proxy behavior, flow graphs or vehicle simulation.
/// </summary>
public sealed record ZoneManagedSceneLoadReport(IReadOnlyList<ZoneManagedSceneCellLoadReport> Cells,
    bool TerrainReady, int TerrainLoadedCells, int EmptyTerrainCells,
    IReadOnlyList<string> UnsupportedCapabilities)
{
    public bool FullSceneCollisionReady => false;
}

/// <summary>
/// A scene owner backed only by managed asset readers. Absolute world-cell names select the source
/// files; EngineCell selects the 1024-meter destination in the zone's eight-by-eight local window.
/// It owns mutable height samples and resolved terrain surfaces, and borrows the asset source.
/// No CryEngine module, engine service, Lua library or native handle is created by this owner.
/// </summary>
public sealed class ZoneManagedSceneActivation : IDisposable
{
    private const int MaximumTerrainChunkBytes = 64 * 1024 * 1024;
    private static readonly IReadOnlyList<string> Limitations = Array.AsReadOnly(new[]
    {
        "Static brush collision uses supported authored default CGF proxies; unsupported resources and vegetation remain in the coverage report.",
        "Engine entity proxies, flow graphs, visibility areas and procedural object behavior are not implemented.",
        "Vehicle, boat, destructible mesh and dynamic water-volume physics is not implemented."
    });
    private readonly object _gate = new();
    private readonly ZoneWorldMapLayout _layout;
    private readonly IZoneAssetSource _assets;
    private readonly ZoneTerrainAssetFiles _files;
    private readonly Action<string>? _trace;
    private readonly List<ZoneMapCoordinate> _loadedCells = [];
    private bool _created, _loadAttempted, _disposed;

    public ZoneTerrainHeightField Heights { get; } = new();
    public ZoneTerrainSurfaceBindings? Surfaces { get; private set; }
    public ZonePhysicalSurfaceContent? SurfaceContent { get; private set; }
    public ZoneTerrainMaterialResolver? Materials { get; private set; }
    public ZoneManagedSceneCollision? Collision { get; private set; }
    public ZoneManagedSceneLoadReport? Report { get; private set; }
    public bool IsCreated { get { lock (_gate) return _created && !_disposed; } }

    public ZoneManagedSceneActivation(ZoneWorldMapLayout layout, IZoneAssetSource assets,
        Action<string>? trace = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(assets);
        _layout = layout;
        _assets = assets;
        _files = new(assets, layout.World);
        _trace = trace;
    }

    public void Create()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_created) throw new InvalidOperationException("The managed scene has already been created.");
            // Resolve every material before publishing the tree, so malformed world setup leaves
            // no partially initialized terrain tree that a subsequent Create could reuse.
            var materials = ZoneTerrainMaterialResolver.Load(_assets);
            using var input = _files.OpenWorldSurfaces(_layout.World) ??
                throw new FileNotFoundException("Managed terrain setup requires the world's surface.xml.");
            var surfaces = new ZoneTerrainSurfaceBindings(ZoneTerrainSurfaceContent.Load(input), materials);
            var nodes = Heights.InitializeNodeTree(ZoneTerrainWorldInitialization.ResolveOceanLevel(_layout.World));
            nodes.BindSurfaces(surfaces);
            Surfaces = surfaces;
            Materials = materials;
            SurfaceContent = materials.SurfaceContent;
            _created = true;
            _trace?.Invoke("CreateManagedSceneServices");
        }
    }

    /// <summary>
    /// Attempts all terrain cells before decoding optional object content. Expected asset failures
    /// remain in the report, allowing the host to refuse terrain readiness with exact diagnostics.
    /// A second load requires a new scene owner; samples cannot be silently replaced after startup.
    /// </summary>
    public ZoneManagedSceneLoadReport Load()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_created) throw new InvalidOperationException("Create the managed scene before loading cells.");
            if (_loadAttempted) throw new InvalidOperationException("The managed scene has already attempted its cell load.");
            _loadAttempted = true;
            var terrain = new List<ZoneTerrainCellLoadResult>(_layout.Cells.Count);
            var occupied = new HashSet<ZoneMapCoordinate>();
            foreach (var placement in _layout.Cells)
            {
                if (placement.InvalidLocalX || placement.InvalidLocalY ||
                    (uint)placement.EngineCell.X >= 8 || (uint)placement.EngineCell.Y >= 8)
                    terrain.Add(new(ZoneTerrainCellLoadStatus.InvalidCell,
                        "The authored cell lies outside the zone's eight-by-eight local terrain window."));
                else if (!occupied.Add(placement.EngineCell))
                    terrain.Add(new(ZoneTerrainCellLoadStatus.Occupied,
                        "Two authored cells resolve to the same local terrain slot."));
                else
                    terrain.Add(LoadTerrain(placement));
            }

            var cells = new List<ZoneManagedSceneCellLoadReport>(_layout.Cells.Count);
            for (var index = 0; index < _layout.Cells.Count; index++)
            {
                var placement = _layout.Cells[index];
                cells.Add(new(placement, terrain[index], LoadObjects(placement)));
                _trace?.Invoke($"ManagedTerrain cell=({placement.Cell.Coordinate.X},{placement.Cell.Coordinate.Y}) " +
                    $"local=({placement.EngineCell.X},{placement.EngineCell.Y}) status={terrain[index].Status}" +
                    (terrain[index].Error is { } error ? $" error={error}" : ""));
            }
            Collision = new(_assets, Materials!, cells, _trace);
            return Report = new(cells.AsReadOnly(), terrain.All(result => result.Succeeded),
                terrain.Count(result => result.Status == ZoneTerrainCellLoadStatus.Loaded),
                terrain.Count(result => result.Status == ZoneTerrainCellLoadStatus.Empty), Limitations);
        }
    }

    private ZoneTerrainCellLoadResult LoadTerrain(ZoneWorldMapCellPlacement placement)
    {
        try
        {
            using var input = _files.OpenHeightmap(placement.Cell) ??
                throw new FileNotFoundException("The cell heightmap.dat could not be opened.");
            var content = ReadHeightmap(input);
            using var xml = _files.OpenLevelData(placement.Cell) ??
                throw new FileNotFoundException("The cell leveldata.xml could not be opened.");
            var cellSurfaces = ZoneTerrainCellSurfaceContent.Load(xml);
            var cell = placement.EngineCell;
            Surfaces!.ApplyCellSurfaceTypes(cell.X, cell.Y, cellSurfaces);
            if (content.Header.ChunkSize == 0) return new(ZoneTerrainCellLoadStatus.Empty);

            // LoadCell validates/copies the complete sample payload before installing it, updates
            // node bounds, and applies the recovered X-, X+, Y-, Y+ seam-copy order.
            Heights.LoadCell(cell.X, cell.Y, content);
            _loadedCells.Add(cell);
            Surfaces.ApplyHeightmapSurfaceIds(cell.X, cell.Y, content);
            Heights.Nodes!.RefreshEdgeMaterials(cell.X, cell.Y, true, true);
            if (cell.X != 0) Heights.Nodes.RefreshEdgeMaterials(cell.X - 1, cell.Y, false, true);
            if (cell.Y != 0) Heights.Nodes.RefreshEdgeMaterials(cell.X, cell.Y - 1, true, false);
            return new(ZoneTerrainCellLoadStatus.Loaded);
        }
        catch (Exception error) when (IsAssetFailure(error))
        {
            return new(ZoneTerrainCellLoadStatus.Failed, error.Message);
        }
    }

    private ZoneManagedCellObjects LoadObjects(ZoneWorldMapCellPlacement placement)
    {
        var diagnostics = new List<string>();
        var objects = ReadOptional(ZoneCellObjectAsset.Objects, ZoneCellObjectContent.ReadOctree);
        var vegetation = ReadOptional(ZoneCellObjectAsset.Vegetation, ZoneCellVegetationContent.Read);
        var entities = ReadOptional(ZoneCellObjectAsset.Entities, ZoneCellEntityContent.Load);
        var bigObjects = ReadOptional(ZoneCellObjectAsset.BigObjects, ZoneCellObjectContent.ReadBigObjects);
        return new(objects, vegetation, entities, bigObjects, diagnostics.AsReadOnly());

        T? ReadOptional<T>(ZoneCellObjectAsset kind, Func<Stream, T> read) where T : class
        {
            try
            {
                using var input = _files.OpenCellObjectAsset(placement.Cell, kind);
                if (input is not null) return read(input);
                diagnostics.Add($"{kind}: authored asset is absent.");
            }
            catch (Exception error) when (IsAssetFailure(error) || error is NotSupportedException)
            {
                diagnostics.Add($"{kind}: {error.Message}");
            }
            return null;
        }
    }

    private static ZoneTerrainHeightmapContent ReadHeightmap(Stream input)
    {
        Span<byte> versionBytes = stackalloc byte[4];
        input.ReadExactly(versionBytes);
        var version = BinaryPrimitives.ReadUInt32LittleEndian(versionBytes);
        if (version is not (23 or 24)) throw new InvalidDataException($"Unsupported terrain header version {version}.");
        var header = new byte[version == 23 ? 32 : 160];
        versionBytes.CopyTo(header);
        input.ReadExactly(header.AsSpan(4));
        var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        if (chunkSize == 0) return ZoneTerrainHeightmapContent.Read(header);
        if (chunkSize < header.Length || chunkSize > MaximumTerrainChunkBytes)
            throw new InvalidDataException("The terrain chunk is outside the supported allocation bounds.");
        var bytes = new byte[(int)chunkSize];
        header.CopyTo(bytes, 0);
        input.ReadExactly(bytes.AsSpan(header.Length));
        return ZoneTerrainHeightmapContent.Read(bytes);
    }

    private static bool IsAssetFailure(Exception error) =>
        error is IOException or InvalidDataException or XmlException or UnauthorizedAccessException;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Collision?.Dispose();
            var retiredCells = _loadedCells.Count;
            for (var index = _loadedCells.Count - 1; index >= 0; index--)
            {
                var cell = _loadedCells[index];
                Heights.UnloadCell(cell.X, cell.Y);
            }
            _loadedCells.Clear();
            _disposed = true;
            _trace?.Invoke($"Managed scene terrain retired cells={retiredCells}");
        }
    }
}
