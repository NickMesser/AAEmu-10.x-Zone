using System.Xml;
using System.Xml.Linq;

namespace AAEmu.Zone.Content;

public readonly record struct ZoneMapCoordinate(int X, int Y);

public sealed record ZoneWorldMapHeader(string Name, int Resolution, int UnitSize, int CellXCount,
    int CellYCount, bool IsInstance, uint NextLayerId, uint NextSurfaceId, float OceanLevel,
    float MaxTerrainHeight, bool IsReleaseBranch);

/// <summary>An authored cell reference, including duplicate references and out-of-range sector rows.</summary>
public sealed class ZoneWorldMapCellRow
{
    public ZoneMapCoordinate Coordinate { get; }
    public IReadOnlyList<ZoneMapCoordinate> Sectors { get; }

    internal ZoneWorldMapCellRow(ZoneMapCoordinate coordinate, ZoneMapCoordinate[] sectors)
    {
        Coordinate = coordinate;
        Sectors = Array.AsReadOnly(sectors);
    }
}

/// <summary>One physical X2Zone row; equal IDs or names do not merge different rows.</summary>
public sealed class ZoneWorldMapZone
{
    public int SourceIndex { get; }
    public int Id { get; }
    public string Name { get; }
    public ZoneMapCoordinate AuthoredOrigin { get; }
    public IReadOnlyList<ZoneWorldMapCellRow> CellRows { get; }
    /// <summary>36565950: unique cell pointers in native front/back insertion order.</summary>
    public IReadOnlyList<ZoneMapCoordinate> Cells { get; }
    public ZoneMapCoordinate Origin { get; }
    /// <summary>Inclusive maximum cell coordinates. The native constructor initializes these to zero.</summary>
    public ZoneMapCoordinate LastCell { get; }

    internal ZoneWorldMapZone(int index, int id, string name, ZoneMapCoordinate origin,
        ZoneWorldMapCellRow[] rows)
    {
        SourceIndex = index;
        Id = id;
        Name = name;
        AuthoredOrigin = origin;
        CellRows = Array.AsReadOnly(rows);
        var unique = new HashSet<ZoneMapCoordinate>();
        var cells = new List<ZoneMapCoordinate>();
        var maximum = new ZoneMapCoordinate(0, 0);
        foreach (var row in rows)
        {
            var cell = row.Coordinate;
            if (!unique.Add(cell)) continue;
            origin = cells.Count == 0 ? cell : new(Math.Min(origin.X, cell.X), Math.Min(origin.Y, cell.Y));
            maximum = new(Math.Max(maximum.X, cell.X), Math.Max(maximum.Y, cell.Y));
            if (cell == origin) cells.Insert(0, cell);
            else cells.Add(cell);
        }
        Origin = origin;
        LastCell = maximum;
        Cells = cells.AsReadOnly();
    }
}

/// <summary>A world grid cell and the final sector ownership after all physical Zone rows load.</summary>
public sealed class ZoneWorldMapCell
{
    private readonly ZoneWorldMapZone?[] _sectors;
    public ZoneMapCoordinate Coordinate { get; }
    public IReadOnlyList<ZoneWorldMapZone> Zones { get; }

    internal ZoneWorldMapCell(ZoneMapCoordinate coordinate, List<ZoneWorldMapZone> zones,
        ZoneWorldMapZone?[] sectors)
    {
        Coordinate = coordinate;
        Zones = zones.AsReadOnly();
        _sectors = sectors;
    }

    /// <summary>36563B60: 16 by 16 X-major ownership, with later assignments replacing earlier ones.</summary>
    public ZoneWorldMapZone? GetSectorOwner(int x, int y) =>
        (uint)x < 16 && (uint)y < 16 ? _sectors[x * 16 + y] : null;
}

/// <summary>
/// Authored world.xml metadata and X2World/X2Zone cell admission (365676F0/36565C30).
/// Empty cell references still affect bounds and loading; sector ownership is a separate relation.
/// Missing grid dimensions and cell references outside the world grid are rejected before allocation or use.
/// This is content admission, not terrain loading, origin streaming or simulation readiness.
/// </summary>
public sealed class ZoneWorldMapContent
{
    private readonly ZoneWorldMapCell[] _cells;
    public ZoneWorldMapHeader Header { get; }
    public string RequestedWorldName { get; }
    public IReadOnlyList<ZoneWorldMapZone> Zones { get; }
    /// <summary>36566AC0: the complete world grid, X outermost and Y innermost.</summary>
    public IReadOnlyList<ZoneWorldMapCell> Cells { get; }

    private ZoneWorldMapContent(ZoneWorldMapHeader header, string requestedWorldName, ZoneWorldMapZone[] zones)
    {
        Header = header;
        RequestedWorldName = requestedWorldName;
        Zones = Array.AsReadOnly(zones);
        var count = checked(header.CellXCount * header.CellYCount);
        var memberships = new List<ZoneWorldMapZone>[count];
        var sectors = new ZoneWorldMapZone?[count][];
        for (var i = 0; i < count; i++)
        {
            memberships[i] = [];
            sectors[i] = new ZoneWorldMapZone?[256];
        }
        foreach (var zone in zones)
        {
            foreach (var row in zone.CellRows)
            {
                var cellIndex = row.Coordinate.X * header.CellYCount + row.Coordinate.Y;
                // 36565C30 writes every source sector, even when AddCell later ignores a duplicate.
                foreach (var sector in row.Sectors)
                    if ((uint)sector.X < 16 && (uint)sector.Y < 16)
                        sectors[cellIndex][sector.X * 16 + sector.Y] = zone;
                if (!memberships[cellIndex].Contains(zone)) memberships[cellIndex].Add(zone);
            }
        }
        _cells = new ZoneWorldMapCell[count];
        for (var x = 0; x < header.CellXCount; x++)
            for (var y = 0; y < header.CellYCount; y++)
            {
                var index = x * header.CellYCount + y;
                _cells[index] = new(new(x, y), memberships[index], sectors[index]);
            }
        Cells = Array.AsReadOnly(_cells);
    }

    public ZoneWorldMapCell? GetCell(int x, int y) =>
        (uint)x < Header.CellXCount && (uint)y < Header.CellYCount ? _cells[x * Header.CellYCount + y] : null;

    public static ZoneWorldMapContent LoadFile(string path, string requestedWorldName)
    {
        using var stream = File.OpenRead(path);
        return Load(stream, requestedWorldName);
    }

    /// <summary>Leaves the caller's stream open. The requested name is the native world loader argument.</summary>
    public static ZoneWorldMapContent Load(Stream stream, string requestedWorldName)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = XmlReader.Create(stream, ReaderSettings());
        return ReadDocument(XDocument.Load(reader), requestedWorldName);
    }

    public static ZoneWorldMapContent Read(string xml, string requestedWorldName)
    {
        using var input = new StringReader(xml);
        using var reader = XmlReader.Create(input, ReaderSettings());
        return ReadDocument(XDocument.Load(reader), requestedWorldName);
    }

    private static XmlReaderSettings ReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        CloseInput = false
    };

    private static ZoneWorldMapContent ReadDocument(XDocument document, string requestedWorldName)
    {
        ArgumentNullException.ThrowIfNull(requestedWorldName);
        var world = document.Root ?? throw new InvalidDataException("World XML has no root element.");
        var header = new ZoneWorldMapHeader(Attribute(world, "name")?.Value ?? "", Int32(world, "resolution"),
            Int32(world, "unitSize"), Int32(world, "cellXCount"), Int32(world, "cellYCount"),
            Boolean(world, "isInstance") || requestedWorldName.Contains("instance_", StringComparison.Ordinal),
            LowUInt32(world, "nextLayerId"), LowUInt32(world, "nextSurfaceId"), Single(world, "oceanLevel", 100),
            Single(world, "maxTerrainHeight", 4096), Boolean(world, "isReleaseBranch"));
        if (header.Resolution <= 0 || header.UnitSize <= 0 || header.CellXCount < 0 || header.CellYCount < 0 ||
            (long)header.CellXCount * header.CellYCount > int.MaxValue)
            throw new InvalidDataException("World grid dimensions are invalid.");
        var zones = new List<ZoneWorldMapZone>();
        // Native loaders enumerate all children; the child tag names do not filter rows.
        foreach (var zone in Child(world, "ZoneList")?.Elements() ?? [])
        {
            var rows = new List<ZoneWorldMapCellRow>();
            foreach (var cell in Child(zone, "cellList")?.Elements() ?? [])
            {
                var coordinate = Coordinate(cell);
                if ((uint)coordinate.X >= header.CellXCount || (uint)coordinate.Y >= header.CellYCount)
                    throw new InvalidDataException($"Cell ({coordinate.X}, {coordinate.Y}) is outside the world grid.");
                rows.Add(new(coordinate, (Child(cell, "sectorList")?.Elements() ?? []).Select(Coordinate).ToArray()));
            }
            zones.Add(new(zones.Count, Int32(zone, "id", 0), Attribute(zone, "name")?.Value ?? "",
                new(Int32(zone, "originX", 0), Int32(zone, "originY", 0)), rows.ToArray()));
        }
        return new(header, requestedWorldName, zones.ToArray());
    }

    private static ZoneMapCoordinate Coordinate(XElement element) => new(Int32(element, "x"), Int32(element, "y"));
    // CXmlNode36524670/36593050 use the default ASCII-folding comparator36592E60 and
    // stop at the first match. OrdinalIgnoreCase would also fold non-ASCII characters.
    private static XAttribute? Attribute(XElement element, string name) =>
        element.Attributes().FirstOrDefault(attribute => NameEquals(attribute.Name.ToString(), name));
    private static XElement? Child(XElement element, string name) =>
        element.Elements().FirstOrDefault(child => NameEquals(child.Name.ToString(), name));
    private static bool NameEquals(string left, string right)
    {
        if (left.Length != right.Length) return false;
        for (var i = 0; i < left.Length; i++)
        {
            var a = left[i] is >= 'A' and <= 'Z' ? left[i] + 32 : left[i];
            var b = right[i] is >= 'A' and <= 'Z' ? right[i] + 32 : right[i];
            if (a != b) return false;
        }
        return true;
    }
    private static string Required(XElement element, string name) => Attribute(element, name)?.Value ??
        throw new InvalidDataException($"World XML element {element.Name} is missing {name}.");
    private static int Int32(XElement element, string name, int? defaultValue = null)
    {
        if (Attribute(element, name) is null && defaultValue.HasValue) return defaultValue.Value;
        return ZoneWorldMapXmlValue.Int32(Required(element, name));
    }
    private static uint LowUInt32(XElement element, string name)
    {
        var text = Attribute(element, name)?.Value;
        if (text is null) return 0;
        // Text CXmlNode slot D8 also writes atoi's signed 32-bit result, despite the caller's
        // eight-byte scratch location. This is different from BinaryXml's unsigned converter.
        return unchecked((uint)ZoneWorldMapXmlValue.Int32(text));
    }
    private static bool Boolean(XElement element, string name) => Int32(element, name, 0) != 0;
    private static float Single(XElement element, string name, float defaultValue)
    {
        if (Attribute(element, name) is null) return defaultValue;
        return ZoneWorldMapXmlValue.Single(Required(element, name));
    }
}
