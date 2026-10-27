using System.Xml.Linq;

namespace AAEmu.Zone.Content;

public sealed record ZonePhysicalSurface(int Id, string Name);

/// <summary>
/// Native name-to-ID registration from Libs/MaterialEffects/SurfaceTypes.xml
/// (301CCE50/301CC130/301CBF40). The constructor's mat_default is ID 0; new names receive IDs
/// 100..255 in source order. Names are case-sensitive, duplicates reuse their registered ID,
/// and rows beyond the native ID capacity are ignored. This catalog does not install the XML's
/// friction, elasticity, breakage or other parameters into a physical world.
/// </summary>
public sealed class ZonePhysicalSurfaceCatalog
{
    private readonly Dictionary<string, ZonePhysicalSurface> _byName;
    public IReadOnlyList<ZonePhysicalSurface> Surfaces { get; }
    public bool HasSourceDocument { get; }
    public static ZonePhysicalSurfaceCatalog DefaultOnly { get; } = new(null);

    private ZonePhysicalSurfaceCatalog(XElement? root)
    {
        HasSourceDocument = root is not null;
        _byName = new(StringComparer.Ordinal) { ["mat_default"] = new(0, "mat_default") };
        var surfaces = new List<ZonePhysicalSurface> { _byName["mat_default"] };
        var nextId = 100;
        foreach (var row in root?.Elements() ?? [])
        {
            if (!ZoneTerrainSurfaceXml.NameEquals(row.Name.ToString(), "SurfaceType")) continue;
            var name = ZoneTerrainSurfaceXml.Attribute(row, "name") ?? "";
            if (_byName.ContainsKey(name) || nextId >= 256) continue;
            var surface = new ZonePhysicalSurface(nextId++, name);
            _byName.Add(name, surface);
            surfaces.Add(surface);
        }
        Surfaces = surfaces.AsReadOnly();
    }

    /// <summary>301CB9F0: empty or unknown names resolve to the constructor's default surface.</summary>
    public ZonePhysicalSurface Resolve(string? name) => !string.IsNullOrEmpty(name) &&
        _byName.TryGetValue(name, out var surface) ? surface : _byName["mat_default"];

    public static ZonePhysicalSurfaceCatalog Read(string xml) => Parse(ZoneTerrainSurfaceXml.Read(xml));
    public static ZonePhysicalSurfaceCatalog Load(Stream stream) => Parse(ZoneTerrainSurfaceXml.Load(stream));
    public static ZonePhysicalSurfaceCatalog LoadFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }
    private static ZonePhysicalSurfaceCatalog Parse(XDocument document) =>
        new(document.Root ?? throw new InvalidDataException("Physical surface XML has no root."));
}
