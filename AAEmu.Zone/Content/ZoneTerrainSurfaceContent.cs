using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace AAEmu.Zone.Content;

/// <summary>The fields authored into a native 0x238-byte world surface record (300D2510).</summary>
public sealed class ZoneTerrainSurfaceRecord
{
    private readonly byte[] _nameBytes;
    public int SourceIndex { get; }
    public int Id { get; }
    public string Name
    {
        get
        {
            var end = Array.IndexOf(_nameBytes, (byte)0);
            return Encoding.UTF8.GetString(_nameBytes.AsSpan(0, end >= 0 ? end : _nameBytes.Length));
        }
    }
    /// <summary>Native strncpy's entire 64-byte field, including a possibly unterminated name.</summary>
    public ReadOnlySpan<byte> NativeNameBytes => _nameBytes;
    public string DetailMaterial { get; }
    public float DetailScaleX { get; }
    public int ProjectionAxis { get; }
    public IReadOnlyList<int> VegetationGroupIds { get; }
    public bool IsAuthored => SourceIndex >= 0;

    internal static ZoneTerrainSurfaceRecord Empty { get; } = new(-1, 0, "", "", 0, 0, []);

    internal ZoneTerrainSurfaceRecord(int sourceIndex, int id, string name, string material,
        float scale, int axis, int[] vegetation)
    {
        SourceIndex = sourceIndex;
        Id = id;
        _nameBytes = new byte[64];
        var bytes = Encoding.UTF8.GetBytes(name.Split('\0')[0]);
        bytes.AsSpan(0, Math.Min(bytes.Length, 64)).CopyTo(_nameBytes);
        DetailMaterial = material;
        DetailScaleX = scale;
        ProjectionAxis = axis;
        VegetationGroupIds = Array.AsReadOnly(vegetation);
    }
}

/// <summary>
/// The runtime world surface.xml catalog. 300D2510 enumerates every SurfaceTypes child, sizes the
/// catalog to max(1, Id) + 1, and writes by Id in source order. Missing records are all zero, including
/// their Id; duplicate Ids replace earlier records. The native secondary material (+50), render data
/// (+70..22F), and local slot byte (+234) remain zero during this load. DetailScaleY is not read.
/// Material objects and renderer-derived shader resources are separate from this authored content.
/// </summary>
public sealed class ZoneTerrainSurfaceContent
{
    public const int MaximumSurfaceCount = 65536;
    public IReadOnlyList<ZoneTerrainSurfaceRecord> Rows { get; }
    public IReadOnlyList<ZoneTerrainSurfaceRecord> Entries { get; }

    private ZoneTerrainSurfaceContent(ZoneTerrainSurfaceRecord[] rows)
    {
        Rows = Array.AsReadOnly(rows);
        var maximum = 1;
        foreach (var row in rows) maximum = Math.Max(maximum, row.Id);
        var entries = new ZoneTerrainSurfaceRecord[maximum + 1];
        Array.Fill(entries, ZoneTerrainSurfaceRecord.Empty);
        foreach (var row in rows) entries[row.Id] = row;
        Entries = Array.AsReadOnly(entries);
    }

    public static ZoneTerrainSurfaceContent LoadFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static ZoneTerrainSurfaceContent Load(Stream stream) => Parse(ZoneTerrainSurfaceXml.Load(stream));
    public static ZoneTerrainSurfaceContent Read(string xml) => Parse(ZoneTerrainSurfaceXml.Read(xml));

    private static ZoneTerrainSurfaceContent Parse(XDocument document)
    {
        var root = document.Root ?? throw new InvalidDataException("World surface XML has no root.");
        var surfaces = ZoneTerrainSurfaceXml.Child(root, "SurfaceTypes") ??
            throw new InvalidDataException("World surface XML is missing SurfaceTypes.");
        var rows = new List<ZoneTerrainSurfaceRecord>();
        foreach (var element in surfaces.Elements())
        {
            var id = ZoneWorldMapXmlValue.Int32(ZoneTerrainSurfaceXml.Attribute(element, "Id") ?? "0");
            // Native negative IDs write before the vector; excessive IDs allocate unbounded memory.
            if ((uint)id >= MaximumSurfaceCount)
                throw new InvalidDataException($"World surface ID {id} is outside the supported catalog bounds.");
            var axis = ZoneTerrainSurfaceXml.Attribute(element, "ProjAxis") ?? "";
            var vegetation = element.Elements()
                .Where(child => ZoneTerrainSurfaceXml.NameEquals(child.Name.ToString(), "VegetationGroup"))
                .Select(child => ZoneTerrainSurfaceXml.Attribute(child, "Id"))
                .Where(value => value is not null).Select(value => ZoneWorldMapXmlValue.Int32(value!)).ToArray();
            rows.Add(new(rows.Count, id, ZoneTerrainSurfaceXml.Attribute(element, "Name") ?? "",
                ZoneTerrainSurfaceXml.Attribute(element, "DetailMaterial") ?? "",
                ZoneWorldMapXmlValue.Single(ZoneTerrainSurfaceXml.Attribute(element, "DetailScaleX") ?? "0"),
                axis.Length == 0 ? 0 : axis[0] switch { 'X' => 1, 'Y' => 2, 'Z' => 3, _ => 0 }, vegetation));
        }
        return new(rows.ToArray());
    }
}

/// <summary>
/// Runtime cell leveldata.xml surface rows (300D1D60). Each child's ordinal is the local slot;
/// only Id is read. An absent SurfaceTypes node has no assignments. Extra rows remain visible in
/// content, although the native binding function ignores ordinals at or above 32.
/// </summary>
public sealed class ZoneTerrainCellSurfaceContent
{
    public IReadOnlyList<int> WorldSurfaceIds { get; }
    private ZoneTerrainCellSurfaceContent(int[] ids) => WorldSurfaceIds = Array.AsReadOnly(ids);
    public static ZoneTerrainCellSurfaceContent LoadFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }
    public static ZoneTerrainCellSurfaceContent Load(Stream stream) => Parse(ZoneTerrainSurfaceXml.Load(stream));
    public static ZoneTerrainCellSurfaceContent Read(string xml) => Parse(ZoneTerrainSurfaceXml.Read(xml));
    private static ZoneTerrainCellSurfaceContent Parse(XDocument document)
    {
        var root = document.Root ?? throw new InvalidDataException("Cell surface XML has no root.");
        var surfaces = ZoneTerrainSurfaceXml.Child(root, "SurfaceTypes");
        return new((surfaces?.Elements() ?? []).Select(element =>
            ZoneWorldMapXmlValue.Int32(ZoneTerrainSurfaceXml.Attribute(element, "Id") ?? "0")).ToArray());
    }
}

internal static class ZoneTerrainSurfaceXml
{
    private static XmlReaderSettings Settings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, CloseInput = false,
        MaxCharactersInDocument = 64 * 1024 * 1024
    };
    internal static XDocument Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = XmlReader.Create(stream, Settings());
        return XDocument.Load(reader);
    }
    internal static XDocument Read(string xml)
    {
        using var input = new StringReader(xml);
        using var reader = XmlReader.Create(input, Settings());
        return XDocument.Load(reader);
    }
    internal static string? Attribute(XElement element, string name) =>
        element.Attributes().FirstOrDefault(attribute => NameEquals(attribute.Name.ToString(), name))?.Value;
    internal static XElement? Child(XElement element, string name) =>
        element.Elements().FirstOrDefault(child => NameEquals(child.Name.ToString(), name));
    internal static bool NameEquals(string left, string right)
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
}
