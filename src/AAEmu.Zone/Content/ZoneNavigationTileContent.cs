using System.Globalization;
using System.Numerics;

namespace AAEmu.Zone.Content;

public enum ZoneNavigationTileLoadStatus { Loaded, MissingAsset, UnsupportedFormat, InvalidContent }

/// <summary>One atomic triplet result. An unavailable tile never supplies partial topology.</summary>
public sealed record ZoneNavigationTileLoadResult(ZoneMapCoordinate Coordinate,
    ZoneNavigationTileLoadStatus Status, string RootPath, ZoneNavigationTileContent? Content, string? Diagnostic);

/// <summary>
/// Immutable streamed navigation input. 31149CE4 loads areas, vertices, then network from
/// paths/%03d_%03d. 311A2AC4 and 311030A8 translate XY by (tile - 4*zoneOrigin)*256;
/// authored IDs and vertex indices remain tile-local until graph import remaps them.
/// The dedicated zone-root loader has a different lifetime and safe-root contract.
/// </summary>
public sealed class ZoneNavigationTileContent
{
    public ZoneMapCoordinate Coordinate { get; }
    public ZoneMapCoordinate ZoneOrigin { get; }
    public ZoneMapCoordinate Offset { get; }
    public string RootPath { get; }
    public ZoneNavigationAreaContent Areas { get; }
    public ZoneNavigationLegacyVertexContent Vertices { get; }
    public ZoneNavigationLegacyGraphContent Network { get; }

    private ZoneNavigationTileContent(ZoneMapCoordinate coordinate, ZoneMapCoordinate origin,
        ZoneMapCoordinate offset, string root, ZoneNavigationAreaContent areas,
        ZoneNavigationLegacyVertexContent vertices, ZoneNavigationLegacyGraphContent network)
    { Coordinate = coordinate; ZoneOrigin = origin; Offset = offset; RootPath = root; Areas = areas; Vertices = vertices; Network = network; }

    public Vector3 Translate(Vector3 position) => new(position.X + Offset.X, position.Y + Offset.Y, position.Z);

    public static ZoneNavigationTileLoadResult Load(IZoneAssetSource assets, string worldName,
        ZoneMapCoordinate zoneOrigin, ZoneMapCoordinate coordinate)
    {
        ArgumentNullException.ThrowIfNull(assets); ArgumentException.ThrowIfNullOrWhiteSpace(worldName);
        if (coordinate.X < 0 || coordinate.Y < 0)
            throw new ArgumentOutOfRangeException(nameof(coordinate), "World navigation tile coordinates must be nonnegative.");
        var path = $"game/worlds/{worldName}/paths/" + coordinate.X.ToString("D3", CultureInfo.InvariantCulture) + "_" + coordinate.Y.ToString("D3", CultureInfo.InvariantCulture);
        var characters = path.ToCharArray();
        for (var i = 0; i < characters.Length; i++) if (characters[i] is >= 'A' and <= 'Z') characters[i] += (char)('a' - 'A');
        var root = ZoneAssetPath.Validate(new string(characters));
        var offset = new ZoneMapCoordinate(checked((coordinate.X - checked(zoneOrigin.X * 4)) * 256),
            checked((coordinate.Y - checked(zoneOrigin.Y * 4)) * 256));
        var current = root + "/areasmission0.bai";
        try
        {
            using var areaStream = assets.OpenRead(current);
            if (areaStream is null) return Missing();
            var areas = ZoneNavigationAreaContent.Load(areaStream, offset.X, offset.Y);
            current = root + "/vertsmission0.bai";
            using var vertexStream = assets.OpenRead(current);
            if (vertexStream is null) return Missing();
            var vertices = ZoneNavigationLegacyVertexContent.Load(vertexStream);
            // 311A2AC4 returns false if its local-to-runtime vertex map is empty.
            if (vertices.Rows.Count == 0)
                return new(coordinate, ZoneNavigationTileLoadStatus.InvalidContent, root, null, "The streamed tile has no obstacle vertices.");
            current = root + "/netmission0.bai";
            using var networkStream = assets.OpenRead(current);
            if (networkStream is null) return Missing();
            var network = ZoneNavigationLegacyGraphContent.LoadStreaming(networkStream);
            return new(coordinate, ZoneNavigationTileLoadStatus.Loaded, root,
                new(coordinate, zoneOrigin, offset, root, areas, vertices, network), null);
        }
        catch (NotSupportedException error)
        { return new(coordinate, ZoneNavigationTileLoadStatus.UnsupportedFormat, root, null, current + ": " + error.Message); }
        catch (Exception error) when (error is InvalidDataException or EndOfStreamException)
        { return new(coordinate, ZoneNavigationTileLoadStatus.InvalidContent, root, null, current + ": " + error.Message); }

        ZoneNavigationTileLoadResult Missing() => new(coordinate, ZoneNavigationTileLoadStatus.MissingAsset, root, null, "Missing " + current);
    }

    public static IReadOnlyList<ZoneNavigationTileLoadResult> LoadZone(IZoneAssetSource assets,
        string worldName, ZoneWorldMapZone zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var results = new List<ZoneNavigationTileLoadResult>(checked(zone.Cells.Count * 16));
        foreach (var cell in zone.Cells)
        for (var x = 0; x < 4; x++)
        for (var y = 0; y < 4; y++)
            results.Add(Load(assets, worldName, zone.Origin, new(checked(cell.X * 4 + x), checked(cell.Y * 4 + y))));
        return results.AsReadOnly();
    }
}
