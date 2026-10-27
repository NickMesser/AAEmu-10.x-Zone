using System.Buffers.Binary;
using System.Xml;

using AAEmu.Zone.Content;
using AAEmu.Zone.Scene;

namespace AAEmu.Zone.Terrain;

public enum ZoneTerrainCellLoadStatus { Loaded, Empty, Failed, Occupied, InvalidCell }
public sealed record ZoneTerrainCellLoadResult(ZoneTerrainCellLoadStatus Status, string? Error = null)
{
    public bool Succeeded => Status is ZoneTerrainCellLoadStatus.Loaded or ZoneTerrainCellLoadStatus.Empty;
}

/// <summary>
/// Dedicated 3015D4A0/300D0890 ordering around the managed terrain projections and required engine
/// leaves. Constructing this component does not establish gameplay or whole-world readiness.
/// Asset failures return false-equivalent results; missing engine capabilities propagate to callers.
/// </summary>
public sealed class ZoneTerrainCellEngine
{
    // Replacement allocation bound for malformed authored files, not a recovered native limit.
    public const int MaximumChunkBytes = 64 * 1024 * 1024;
    private readonly IZoneTerrainCellAssets _assets;
    private readonly IZoneTerrainCellServices _services;
    private ZoneTerrainCellWindow? _owner;
    public ZoneWorldMapContent World { get; }
    public ZoneTerrainHeightField Heights { get; } = new();
    public ZoneTerrainNodeTree Nodes { get; }
    public ZoneTerrainSurfaceBindings Surfaces { get; }

    public ZoneTerrainCellEngine(ZoneWorldMapContent world, IZoneTerrainCellAssets assets,
        IZoneTerrainMaterialResolver materials, IZoneTerrainCellServices services)
    {
        ArgumentNullException.ThrowIfNull(world); ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(materials); ArgumentNullException.ThrowIfNull(services);
        World = world; _assets = assets; _services = services;
        if (!services.CreateWorldTerrain(world, Heights))
            throw new InvalidDataException("The engine could not create the world's terrain.");
        Nodes = Heights.Nodes ?? throw new InvalidOperationException("World terrain creation did not initialize the supplied heightfield's node tree.");
        using var stream = assets.OpenWorldSurfaces(world) ??
            throw new FileNotFoundException("World terrain setup requires surface.xml.");
        Surfaces = new(ZoneTerrainSurfaceContent.Load(stream), materials);
        Nodes.BindSurfaces(Surfaces);
    }

    internal void Claim(ZoneTerrainCellWindow owner)
    {
        if (_owner is not null) throw new InvalidOperationException("The terrain engine already belongs to a cell window.");
        _owner = owner;
    }

    internal ZoneTerrainCellLoadResult LoadCell(ZoneWorldMapCellPlacement placement)
    {
        Stream? input;
        try { input = _assets.OpenHeightmap(placement.Cell); }
        catch (IOException error) { return Failed(error); }
        catch (InvalidDataException error) { return Failed(error); }
        catch (UnauthorizedAccessException error) { return Failed(error); }
        if (input is null) return new(ZoneTerrainCellLoadStatus.Failed, "The cell heightmap could not be opened.");
        using (input)
        {
            byte[] header;
            try { header = ReadHeader(input); }
            catch (IOException error) { return Failed(error); }
            catch (InvalidDataException error) { return Failed(error); }
            catch (UnauthorizedAccessException error) { return Failed(error); }

            // The native loader requires parsed XML even when ChunkSize is zero. A present but
            // empty SurfaceTypes still invokes +508 and physical registration; an absent child does not.
            ZoneTerrainCellSurfaceContent? cellSurfaces;
            try
            {
                using var xml = _assets.OpenLevelData(placement.Cell);
                if (xml is null) return new(ZoneTerrainCellLoadStatus.Failed, "The cell leveldata.xml could not be opened.");
                var document = ZoneTerrainSurfaceXml.Load(xml);
                var root = document.Root ?? throw new InvalidDataException("Cell leveldata.xml has no root.");
                cellSurfaces = ZoneTerrainSurfaceXml.Child(root, "SurfaceTypes") is null ? null :
                    ZoneTerrainCellSurfaceContent.Read(document.ToString());
            }
            catch (IOException error) { return Failed(error); }
            catch (XmlException error) { return Failed(error); }
            catch (InvalidDataException error) { return Failed(error); }
            catch (UnauthorizedAccessException error) { return Failed(error); }
            var cell = placement.EngineCell;
            if (cellSurfaces is not null)
            {
                Surfaces.ApplyCellSurfaceTypes(cell.X, cell.Y, cellSurfaces);
                _services.RegisterPhysicalHeightfield(Surfaces.CreatePhysicalMaterialMap());
            }
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            if (chunkSize == 0)
            {
                _services.FinishTerrainLoad(placement);
                return new(ZoneTerrainCellLoadStatus.Empty);
            }

            ZoneTerrainHeightmapContent content;
            try
            {
                if (chunkSize < header.Length || chunkSize > MaximumChunkBytes)
                    throw new InvalidDataException("The terrain chunk is outside the supported allocation bounds.");
                var bytes = new byte[(int)chunkSize];
                header.CopyTo(bytes, 0);
                input.ReadExactly(bytes.AsSpan(header.Length));
                content = ZoneTerrainHeightmapContent.Read(bytes);
            }
            catch (IOException error) { return Failed(error); }
            catch (InvalidDataException error) { return Failed(error); }
            catch (UnauthorizedAccessException error) { return Failed(error); }

            Heights.ResetPhysicsCache();
            Nodes.SurfaceResources.LoadCell(placement, content);
            _services.LoadNodeResources(placement, content);
            Heights.InstallCellSamples(cell.X, cell.Y, content);
            Nodes.RefreshBounds(cell.X, cell.Y);
            _services.PrepareNodeTextures(placement);
            Surfaces.ApplyHeightmapSurfaceIds(cell.X, cell.Y, content);
            Heights.CopyCellSeams(cell.X, cell.Y);
            RefreshEdges(cell);
            _services.RefreshPhysicalMaterialMap(Surfaces.CreatePhysicalMaterialMap());
            _services.FinishTerrainLoad(placement);
            return new(ZoneTerrainCellLoadStatus.Loaded);
        }
    }

    internal void UnloadCell(ZoneWorldMapCellPlacement placement, bool refreshPhysics)
    {
        var cell = placement.EngineCell;
        _services.BeginTerrainUnload(placement);
        _services.UnloadNodeResources(placement);
        Nodes.SurfaceResources.RetireCell(placement);
        Heights.UnloadCell(cell.X, cell.Y);
        RefreshEdges(cell);
        if (refreshPhysics) _services.RefreshPhysicalMaterialMap(Surfaces.CreatePhysicalMaterialMap());
        // Native 300D0030 leaves the cell surface records and adjoining copied heights intact.
    }

    internal bool LoadCellObjects(ZoneWorldMapCellPlacement placement) => _services.LoadCellObjects(placement);
    internal void UnloadCellObjects(ZoneWorldMapCellPlacement placement) => _services.UnloadCellObjects(placement);

    private void RefreshEdges(ZoneMapCoordinate cell)
    {
        Nodes.RefreshEdgeMaterials(cell.X, cell.Y, true, true);
        if (cell.X != 0) Nodes.RefreshEdgeMaterials(cell.X - 1, cell.Y, false, true);
        if (cell.Y != 0) Nodes.RefreshEdgeMaterials(cell.X, cell.Y - 1, true, false);
    }

    private static byte[] ReadHeader(Stream stream)
    {
        var versionBytes = new byte[4];
        stream.ReadExactly(versionBytes);
        var version = BinaryPrimitives.ReadUInt32LittleEndian(versionBytes);
        if (version is not (23 or 24)) throw new InvalidDataException($"Unsupported terrain header version {version}.");
        var header = new byte[version == 23 ? 32 : 160];
        versionBytes.CopyTo(header, 0);
        stream.ReadExactly(header.AsSpan(4));
        return header;
    }

    private static ZoneTerrainCellLoadResult Failed(Exception error) => new(ZoneTerrainCellLoadStatus.Failed, error.Message);
}
