using AAEmu.Zone.Content;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.Scene;

public sealed record ZoneWorldMapCellPlacement(ZoneWorldMapCell Cell, ZoneMapCoordinate EngineCell,
    bool InvalidLocalX, bool InvalidLocalY);

/// <summary>
/// The non-streaming LoadZone365704D0 cell layout. Terrain loads in this order, then objects load
/// in the same order. Client streaming origin changes and their moving cell window are separate.
/// </summary>
public sealed class ZoneWorldMapLayout
{
    public ZoneWorldMapContent World { get; }
    public ZoneWorldMapZone Zone { get; }
    public ZoneMapCoordinate Origin => Zone.Origin;
    public ZoneSpatialRegionBounds SpatialBounds { get; }
    public IReadOnlyList<ZoneWorldMapCellPlacement> Cells { get; }

    public ZoneWorldMapLayout(ZoneWorldMapContent world, ZoneWorldMapZone zone)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(zone);
        if ((uint)zone.SourceIndex >= world.Zones.Count || !ReferenceEquals(world.Zones[zone.SourceIndex], zone))
            throw new ArgumentException("The Zone metadata belongs to a different world.", nameof(zone));
        World = world;
        Zone = zone;
        SpatialBounds = new(zone.Origin.X, zone.Origin.Y, zone.LastCell.X, zone.LastCell.Y);
        var cells = new List<ZoneWorldMapCellPlacement>(zone.Cells.Count);
        foreach (var coordinate in zone.Cells)
        {
            // X2Cell36563A00/365639A0 log an invalid component and return zero for that axis.
            // Preserve both the native result and the diagnostic rather than dropping the cell.
            var x = unchecked(coordinate.X - Origin.X);
            var y = unchecked(coordinate.Y - Origin.Y);
            var invalidX = (uint)x >= 8;
            var invalidY = (uint)y >= 8;
            cells.Add(new(world.GetCell(coordinate.X, coordinate.Y)!,
                new(invalidX ? 0 : x, invalidY ? 0 : y), invalidX, invalidY));
        }
        Cells = cells.AsReadOnly();
    }
}
