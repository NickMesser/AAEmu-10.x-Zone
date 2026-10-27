using System.Globalization;
using System.Text;
using AAEmu.Zone.Simulation;

namespace AAEmu.Zone.Content;

public enum ZoneCellObjectAsset
{
    Objects, Vegetation, VisibilityAreas, Entities, BigObjects,
    DeferredModels, DeferredMaterials, DeferredBrushes
}

/// <summary>
/// Native authored-world/cell paths over a caller-owned asset source. Cell roots come from
/// 36566060's World name and absolute coordinates, not the requested world alias or local window.
/// ASCII casing follows the ordinary relative-path CryPak branch; drive mounts are not involved.
/// </summary>
public sealed class ZoneTerrainAssetFiles : IZoneTerrainCellAssets
{
    internal ZoneWorldMapContent World => _world;
    private readonly IZoneAssetSource _source;
    private readonly ZoneWorldMapContent _world;
    private readonly string _worldRoot;

    public ZoneTerrainAssetFiles(IZoneAssetSource source, ZoneWorldMapContent world)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(world);
        _source = source; _world = world;
        _worldRoot = ZoneAssetPath.Validate($"game/worlds/{world.Header.Name}");
    }

    public Stream? OpenWorldSurfaces(ZoneWorldMapContent world)
    {
        if (!ReferenceEquals(world, _world)) throw new ArgumentException("The terrain assets belong to another world.", nameof(world));
        return _source.OpenRead(LowerAscii(_worldRoot + "/surface.xml"));
    }

    public Stream? OpenHeightmap(ZoneWorldMapCell cell) =>
        _source.OpenRead(LowerAscii(CellRoot(cell) + "client/terrain/heightmap.dat"));

    public Stream? OpenLevelData(ZoneWorldMapCell cell) =>
        _source.OpenRead(LowerAscii(CellRoot(cell) + "client/leveldata.xml"));

    public Stream? OpenCoverTexture(ZoneWorldMapCell cell) =>
        _source.OpenRead(GetCoverTexturePath(cell));

    /// <summary>The authored object passes and the final deferred worker's three input files.</summary>
    public Stream? OpenCellObjectAsset(ZoneWorldMapCell cell, ZoneCellObjectAsset kind) =>
        _source.OpenRead(GetCellObjectPath(cell, kind));

    public string GetCellObjectPath(ZoneWorldMapCell cell, ZoneCellObjectAsset kind)
    {
        var file = kind switch
        {
            ZoneCellObjectAsset.Objects => "object.dat",
            ZoneCellObjectAsset.Vegetation => "vegetation.dat",
            ZoneCellObjectAsset.VisibilityAreas => "visareas.dat",
            ZoneCellObjectAsset.Entities => "entities.xml",
            ZoneCellObjectAsset.BigObjects => "big_object.dat",
            ZoneCellObjectAsset.DeferredModels => "statobjs.dat",
            ZoneCellObjectAsset.DeferredMaterials => "materials.dat",
            ZoneCellObjectAsset.DeferredBrushes => "brush.dat",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return LowerAscii(CellRoot(cell) + "client/" + file);
    }

    /// <summary>The same validated authored path used by native terrain streaming.</summary>
    public string GetCoverTexturePath(ZoneWorldMapCell cell) =>
        LowerAscii(CellRoot(cell) + "client/terrain/cover.ctc");

    private string CellRoot(ZoneWorldMapCell cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        var (x, y) = cell.Coordinate;
        if (!ReferenceEquals(_world.GetCell(x, y), cell)) throw new ArgumentException("The cell belongs to another world.", nameof(cell));
        var root = string.Create(CultureInfo.InvariantCulture, $"{_worldRoot}/cells/{x:000}_{y:000}/");
        // Native vsprintf_s uses a 260-byte root buffer and does not silently truncate.
        if (Encoding.UTF8.GetByteCount(root) >= 260)
            throw new InvalidDataException("The authored terrain cell root exceeds the native path capacity.");
        return root;
    }

    private static string LowerAscii(string value) => string.Create(value.Length, value, static (result, input) =>
    {
        for (var i = 0; i < input.Length; i++)
            result[i] = input[i] is >= 'A' and <= 'Z' ? (char)(input[i] + 32) : input[i];
    });
}
