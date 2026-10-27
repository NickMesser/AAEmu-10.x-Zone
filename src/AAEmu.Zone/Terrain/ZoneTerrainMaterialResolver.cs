using System.Text;
using System.Xml;
using System.Xml.Linq;

using AAEmu.Zone.Content;

namespace AAEmu.Zone.Terrain;

/// <summary>The material identity, submaterial graph and physical fields used by terrain queries.</summary>
public sealed class ZoneTerrainMaterial : IZoneTerrainMaterial
{
    private ZoneTerrainMaterial?[] _submaterials = [];
    public string Name { get; }
    public int PhysicalSurfaceId { get; internal set; }
    public bool CannotPlaceDoodad { get; internal set; }
    public ZoneTerrainMaterial? DualMaterial { get; internal set; }
    public int SubMaterialCount => _submaterials.Length;
    public float TerrainScale { get; set; } = 1;
    public int TerrainProjectionAxis { get; set; }
    internal ZoneTerrainMaterial(string name) => Name = name;
    public IZoneTerrainMaterial? GetSubMaterial(int index) =>
        (uint)index < (uint)_submaterials.Length ? _submaterials[index] : null;
    internal void AllocateSubmaterials(int count) => _submaterials = new ZoneTerrainMaterial?[count];
    internal void SetSubmaterial(int index, ZoneTerrainMaterial? material) => _submaterials[index] = material;
}

/// <summary>
/// Terrain material provider for the native text .mtl profile (301C8570/301C6200/301C9440).
/// It implements the material graph, cache identity and physical-surface lookup; shader, texture,
/// material-layer and rendering resources remain outside this provider. Actual terrain material
/// assets use this text XML profile. Filesystem mount rebasing and editor material overrides are
/// not enabled. Streams and source priority are supplied by the caller's asset source.
/// Ordinary file access/read failures and XML parse failures use native material fallbacks.
/// Guarded unsupported content and asset-source capability failures remain explicit exceptions.
/// Native XML loading ignores read counts; managed read exceptions use a bounded fallback instead
/// of parsing an incomplete native buffer. Successfully read XML survives ordinary close failures.
/// </summary>
public sealed class ZoneTerrainMaterialResolver : IZoneTerrainMaterialResolver
{
    private const int MaximumDepth = 128;
    private const int MaximumSubmaterials = 65536;
    private readonly object _gate = new();
    private readonly IZoneAssetSource _assets;
    private readonly Dictionary<string, ZoneTerrainMaterial> _cache = new(StringComparer.Ordinal);
    private readonly ZoneTerrainMaterial _error = new("Default");
    private readonly ZoneTerrainMaterial _default = new("DefaultTerrainLayer");
    public ZonePhysicalSurfaceCatalog SurfaceCatalog { get; }
    /// <summary>
    /// The same authored surface snapshot used by Load to resolve material IDs. A missing,
    /// unreadable or malformed source, or an explicitly supplied identity-only catalog, leaves
    /// this null and supplies no physical-world registrations.
    /// </summary>
    public ZonePhysicalSurfaceContent? SurfaceContent { get; }
    public IZoneTerrainMaterial ErrorMaterial => _error;
    public IZoneTerrainMaterial DefaultMaterial => _default;

    public ZoneTerrainMaterialResolver(IZoneAssetSource assets, ZonePhysicalSurfaceCatalog surfaces)
        : this(assets, surfaces, null) { }

    private ZoneTerrainMaterialResolver(IZoneAssetSource assets, ZonePhysicalSurfaceCatalog surfaces,
        ZonePhysicalSurfaceContent? surfaceContent)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(surfaces);
        _assets = assets;
        SurfaceCatalog = surfaces;
        SurfaceContent = surfaceContent;
        // 301C9880 creates these three distinct objects. Only nodraw enters the ordinary cache.
        _cache.Add("game/nodraw", new("nodraw"));
    }

    /// <summary>
    /// Loads the physical surface snapshot once from the selected asset source. Its exact catalog
    /// supplies material IDs and SurfaceContent supplies authored physical registrations. An absent,
    /// unreadable or malformed XML file leaves the native default-only catalog with no registrations.
    /// InvalidDataException and capability/admission failures remain explicit.
    /// </summary>
    public static ZoneTerrainMaterialResolver Load(IZoneAssetSource assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        var content = ReadXmlAsset(assets, "game/libs/materialeffects/surfacetypes.xml", ZonePhysicalSurfaceContent.Load);
        return new(assets, content?.Catalog ?? ZonePhysicalSurfaceCatalog.DefaultOnly, content);
    }

    /// <summary>
    /// Native LoadMaterial(name, 1, 0, 0): absent, unreadable and malformed material XML return the
    /// shared error sentinel without caching failure. Parents retain error children and complete
    /// normally. Guard/capability failures throw and roll back newly cached graphs. Valid private
    /// materials (MtlFlags bit 7) are also not cached.
    /// </summary>
    public IZoneTerrainMaterial? Load(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_gate)
        {
            var additions = new List<string>();
            try { return LoadCore(name, 0, additions); }
            catch
            {
                // A guarded or failed asset read must not publish a partially constructed graph.
                foreach (var key in additions) _cache.Remove(key);
                throw;
            }
        }
    }

    private ZoneTerrainMaterial LoadCore(string name, int depth, List<string> additions)
    {
        var key = GetMaterialKey(name);
        if (_cache.TryGetValue(key, out var cached)) return cached;
        CheckDepth(depth);
        // 301C8570 checks for any dot in the normalized key, including directory components.
        // 301C4E50 has already removed a final filename extension.
        var path = key.Contains('.') ? key : key + ".mtl";
        var document = ReadXmlAsset(_assets, path, ZoneTerrainSurfaceXml.Load);
        return document?.Root is { } root ? Construct(key, root, false, depth, additions) : _error;
    }

    private static T? ReadXmlAsset<T>(IZoneAssetSource assets, string path, Func<Stream, T> read) where T : class
    {
        // 36595110 initializes null before opening/reading. CCryFile's close return is ignored,
        // so an ordinary close failure does not discard XML that was successfully read.
        Stream? stream = null;
        try
        {
            stream = assets.OpenRead(path);
            return stream is null ? null : read(stream);
        }
        catch (XmlException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        finally
        {
            try { stream?.Dispose(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private ZoneTerrainMaterial Construct(string name, XElement root, bool inline, int depth, List<string> additions)
    {
        CheckDepth(depth);
        var flags = unchecked((uint)ZoneWorldMapXmlValue.Int32(ZoneTerrainSurfaceXml.Attribute(root, "MtlFlags") ?? "0"));
        if (inline) flags |= 0x80;
        var material = new ZoneTerrainMaterial(name);
        // 301C9440 installs a shared root before traversing its children. Self-references therefore
        // receive this exact partial object, and later assignments complete the same graph.
        if ((flags & 0x80) == 0)
        {
            _cache.Add(name, material);
            additions.Add(name);
        }
        if ((flags & 0x100) == 0)
        {
            material.PhysicalSurfaceId = SurfaceCatalog.Resolve(ZoneTerrainSurfaceXml.Attribute(root, "SurfaceType")).Id;
            material.CannotPlaceDoodad = ZoneWorldMapXmlValue.Int32(
                ZoneTerrainSurfaceXml.Attribute(root, "CannotPlaceDoodad") ?? "0") != 0;
        }
        if (ZoneTerrainSurfaceXml.Child(root, "SubMaterials") is { } submaterials)
        {
            var rows = submaterials.Elements().ToArray();
            if (rows.Length > MaximumSubmaterials)
                throw new InvalidDataException("Terrain material has too many submaterial slots.");
            material.AllocateSubmaterials(rows.Length);
            for (var i = 0; i < rows.Length; i++)
            {
                var row = rows[i];
                var childName = ZoneTerrainSurfaceXml.Attribute(row, "Name") ?? "";
                if (ZoneTerrainSurfaceXml.NameEquals(row.Name.ToString(), "Material"))
                    material.SetSubmaterial(i, Construct(childName, row, true, depth + 1, additions));
                else if (childName.Length > 0)
                    material.SetSubmaterial(i, LoadCore(childName, depth + 1, additions));
            }
        }
        if (ZoneTerrainSurfaceXml.Child(root, "DualMaterial") is { } dual &&
            ZoneTerrainSurfaceXml.Attribute(dual, "Name") is { Length: > 0 } dualName)
            material.DualMaterial = LoadCore(dualName, depth + 1, additions);
        return material;
    }

    /// <summary>
    /// 3655B510 + XlNormalizePath3301B6E0, then 301C4E50's final-extension removal. The native
    /// 260-byte path buffer, mounted drive paths and unsafe asset-tree segments are rejected here
    /// rather than approximated. Dot characters within a component retain native significance.
    /// </summary>
    public static string GetMaterialKey(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Contains(':') || name.Contains('\0'))
            throw new ArgumentException("Terrain materials require a virtual path without a drive or NUL.", nameof(name));
        var output = new StringBuilder(name.Length + 5);
        var slash = false;
        foreach (var source in name)
        {
            var c = source == '\\' ? '/' : source;
            if (c == '/' && slash) continue;
            output.Append(c);
            slash = c == '/';
        }
        var normalized = output.ToString();
        var prefixes = new[] { "game/", "editor/", "bin32/", "USER/", "UCC/", "profiler/", "addon/", "ScreenShots/" };
        if (!prefixes.Any(prefix => normalized.Length >= prefix.Length &&
                ZoneTerrainSurfaceXml.NameEquals(normalized[..prefix.Length], prefix)))
            normalized = "game/" + normalized.TrimStart('/');
        var chars = normalized.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (chars[i] is >= 'A' and <= 'Z') chars[i] = (char)(chars[i] + 32);
        normalized = new string(chars);
        if (Encoding.UTF8.GetByteCount(normalized) >= 260)
            throw new ArgumentException("Terrain material path exceeds the supported native buffer.", nameof(name));
        var separator = normalized.LastIndexOf('/');
        var dot = normalized.LastIndexOf('.');
        if (dot > separator) normalized = normalized[..dot];
        // Keep native normalization independent from the asset source's filesystem admission rules.
        // An authored "game/" reference is valid here: native code attempts game/.mtl and falls
        // back when absent. Only interior empty components and traversal components are rejected.
        var parts = normalized.Split('/');
        if (parts.Any(part => part is "." or "..") || parts.Take(parts.Length - 1).Any(part => part.Length == 0))
            throw new ArgumentException("Terrain material paths cannot contain interior empty, dot or parent segments.", nameof(name));
        return normalized;
    }

    private static void CheckDepth(int depth)
    {
        if (depth > MaximumDepth) throw new InvalidDataException("Terrain material graph exceeds the supported depth.");
    }
}
