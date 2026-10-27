using System.Numerics;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace AAEmu.Zone.Content;

public sealed record ZoneCellEntityXmlAttribute(string Name, string Value);

/// <summary>
/// Immutable authored XML, including ordered attributes and children. Xml retains content that
/// individual entity/proxy readers do not interpret; the owning document also retains its bytes.
/// </summary>
public sealed class ZoneCellEntityXmlElement
{
    private readonly XElement _source;
    public string Name { get; }
    public string Xml => _source.ToString(SaveOptions.DisableFormatting);
    public IReadOnlyList<ZoneCellEntityXmlAttribute> Attributes { get; }
    public IReadOnlyList<ZoneCellEntityXmlElement> Children { get; }

    internal ZoneCellEntityXmlElement(XElement source)
    {
        // The private document is never exposed or mutated. Serialize a requested subtree only
        // on demand instead of retaining every ancestor's duplicate copy during construction.
        _source = source;
        Name = source.Name.LocalName;
        Attributes = Array.AsReadOnly(source.Attributes().Select(attribute =>
            new ZoneCellEntityXmlAttribute(attribute.Name.LocalName, attribute.Value)).ToArray());
        Children = Array.AsReadOnly(source.Elements().Select(child => new ZoneCellEntityXmlElement(child)).ToArray());
    }

    /// <summary>CXmlNode uses the first ASCII case-insensitive match, preserving empty values.</summary>
    public string? Attribute(string name) =>
        Attributes.FirstOrDefault(attribute => ZoneCellEntityValues.NameEquals(attribute.Name, name))?.Value;

    public ZoneCellEntityXmlElement? Child(string name) =>
        Children.FirstOrDefault(child => ZoneCellEntityValues.NameEquals(child.Name, name));

}

/// <summary>One direct EntityLinks child. The native reader does not filter its child tag.</summary>
public sealed class ZoneCellEntityLink
{
    public int SourceIndex { get; }
    public string Name { get; }
    public uint TargetId { get; }
    public ZoneCellEntityXmlElement Xml { get; }
    internal ZoneCellEntityLink(int index, ZoneCellEntityXmlElement xml)
    {
        SourceIndex = index; Xml = xml;
        Name = xml.Attribute("Name") ?? "";
        TargetId = unchecked((uint)ZoneCellEntityValues.Integer(xml, "TargetId", 0));
    }
}

/// <summary>
/// Inputs consumed by CEntityLoadManager32097000, before runtime class/quality/network admission.
/// Authored EntityId/ParentId are preserved, but this native path requests allocator ID zero and
/// resolves parent attachment by GUID after the batch. Properties/proxy XML remains fully owned.
/// </summary>
public sealed class ZoneCellEntityRow
{
    public int SourceChildIndex { get; }
    public ZoneCellEntityXmlElement Xml { get; }
    public string Name { get; }
    public string ClassName { get; }
    public string Layer { get; }
    public uint? AuthoredEntityId { get; }
    public uint? AuthoredParentId { get; }
    public uint NativeRequestedEntityId => 0;
    public ulong EntityGuid { get; }
    public ulong? ParentGuid { get; }
    public Vector3 Position { get; }
    public Quaternion Rotation { get; }
    public Vector3 Scale { get; }
    public int MinimumSpec { get; }
    public uint NativeSpawnFlags { get; }
    public bool SyncHasSingleByteValue { get; }
    public string NetworkOption { get; }
    public string Archetype { get; }
    /// <summary>Null means absent; an authored empty Material remains an empty string.</summary>
    public string? Material { get; }
    public string Geometry { get; }
    public bool HiddenInGame { get; }
    public bool UseOccluders { get; }
    public ZoneCellEntityXmlElement? Properties => Xml.Child("Properties");
    public ZoneCellEntityXmlElement? InstanceProperties => Xml.Child("Properties2");
    public ZoneCellEntityXmlElement? Rope => Xml.Child("Rope");
    public ZoneCellEntityXmlElement? FlowGraph => Xml.Child("FlowGraph");
    public ZoneCellEntityXmlElement? Area => Xml.Child("Area");
    public IReadOnlyList<ZoneCellEntityLink> Links { get; }

    internal ZoneCellEntityRow(int sourceIndex, ZoneCellEntityXmlElement xml)
    {
        SourceChildIndex = sourceIndex; Xml = xml;
        Name = xml.Attribute("Name") ?? "";
        ClassName = xml.Attribute("EntityClass") ?? "";
        Layer = xml.Attribute("Layer") ?? "";
        AuthoredEntityId = ZoneCellEntityValues.OptionalId(xml, "EntityId");
        AuthoredParentId = ZoneCellEntityValues.OptionalId(xml, "ParentId");
        EntityGuid = ZoneCellEntityValues.Hexadecimal(xml.Attribute("EntityGuid") ?? "");
        ParentGuid = xml.Attribute("ParentGUID") is { } parent ? ZoneCellEntityValues.Hexadecimal(parent) : null;
        Position = ZoneCellEntityValues.Vector(xml.Attribute("Pos"), Vector3.Zero);
        Rotation = ZoneCellEntityValues.Rotation(xml.Attribute("Rotate"));
        Scale = ZoneCellEntityValues.Vector(xml.Attribute("Scale"), Vector3.One);
        MinimumSpec = ZoneCellEntityValues.Integer(xml, "MinSpec", -1);
        var flags = 0u;
        if (ZoneCellEntityValues.Boolean(xml, "CastShadow", true)) flags |= 2;
        if (ZoneCellEntityValues.Boolean(xml, "CastShadowExtraRange", false)) flags |= 0x10;
        if (ZoneCellEntityValues.Boolean(xml, "GoodOccluder", false)) flags |= 8;
        if (ZoneCellEntityValues.Boolean(xml, "OutdoorOnly", false)) flags |= 0x08000000;
        // Native strlen(Sync)==1 sets the second spawn ushort byte, independently of its text.
        SyncHasSingleByteValue = Encoding.UTF8.GetByteCount(xml.Attribute("Sync") ?? "") == 1;
        if (!SyncHasSingleByteValue) flags |= 0x00400000;
        NetworkOption = xml.Attribute("NetworkOption") ?? "";
        if (ZoneCellEntityValues.NameEquals(NetworkOption, "ClientOnly")) flags |= 0x100;
        else if (ZoneCellEntityValues.NameEquals(NetworkOption, "ServerOnly")) flags |= 0x200;
        NativeSpawnFlags = flags;
        Archetype = xml.Attribute("Archetype") ?? "";
        Material = xml.Attribute("Material");
        Geometry = xml.Attribute("Geometry") ?? "";
        HiddenInGame = ZoneCellEntityValues.Boolean(xml, "HiddenInGame", false);
        UseOccluders = ZoneCellEntityValues.Boolean(xml, "UseOccluders", false);
        Links = Array.AsReadOnly((xml.Child("EntityLinks")?.Children ?? [])
            .Select((child, index) => new ZoneCellEntityLink(index, child)).ToArray());
    }

    /// <summary>
    /// 3015EEC0 injects the runtime engine cell, not the authored world-cell filename. 32097000
    /// adds its signed cell&lt;&lt;10 translation unless ParentGUID is present, even if its value is zero.
    /// Attached rows keep local coordinates for the deferred GUID-parent phase.
    /// </summary>
    public Vector3 GetNativePosition(ZoneMapCoordinate engineCell) => ParentGuid.HasValue ? Position :
        new(Position.X + (float)unchecked(engineCell.X << 10),
            Position.Y + (float)unchecked(engineCell.Y << 10), Position.Z);

    /// <summary>The recovered MinSpec gate; other runtime admission requirements remain separate.</summary>
    public bool PassesMinimumSpec(int currentSpec) =>
        MinimumSpec <= 0 || currentSpec == 0 || currentSpec >= MinimumSpec;
}

/// <summary>
/// Text entities.xml content for 3015EEC0 -> IEntitySystem+120/32097BC0. Only direct Objects
/// children tagged Entity enter that batch; all other XML and original bytes remain preserved.
/// Decoding creates no native entity, resolves no class/script/material and performs no callbacks.
/// Namespace-bearing XML is not admitted: these target assets use literal, unprefixed native tags.
/// </summary>
public sealed class ZoneCellEntityContent
{
    /// <summary>Managed resource bound, not a native XML format limit.</summary>
    public const int MaximumDocumentBytes = 64 * 1024 * 1024;
    /// <summary>Managed structural bound checked before recursive snapshot construction.</summary>
    public const int MaximumElementDepth = 128;
    /// <summary>The caller's captured native entity-pointer array size; this is not a decode limit.</summary>
    public const int NativeCapturedEntityCapacity = 4096;
    private readonly byte[] _document;
    public ReadOnlySpan<byte> RawDocument => _document;
    public ZoneCellEntityXmlElement Root { get; }
    public ZoneCellEntityXmlElement? Objects { get; }
    public IReadOnlyList<ZoneCellEntityRow> Entities { get; }

    private ZoneCellEntityContent(byte[] document, XElement root)
    {
        _document = document;
        Root = new(root);
        Objects = Root.Child("Objects");
        var rows = new List<ZoneCellEntityRow>();
        if (Objects is not null)
            for (var index = 0; index < Objects.Children.Count; index++)
                if (ZoneCellEntityValues.NameEquals(Objects.Children[index].Name, "Entity"))
                    rows.Add(new(index, Objects.Children[index]));
        Entities = rows.AsReadOnly();
    }

    public static ZoneCellEntityContent LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    /// <summary>Leaves the caller's asset stream open.</summary>
    public static ZoneCellEntityContent Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var copy = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = stream.Read(buffer)) != 0)
        {
            if (copy.Length + count > MaximumDocumentBytes)
                throw new InvalidDataException("Cell entity XML exceeds the managed document bound.");
            copy.Write(buffer, 0, count);
        }
        return Read(copy.GetBuffer().AsSpan(0, checked((int)copy.Length)));
    }

    public static ZoneCellEntityContent Read(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        if (Encoding.UTF8.GetByteCount(xml) > MaximumDocumentBytes)
            throw new InvalidDataException("Cell entity XML exceeds the managed document bound.");
        return Read(Encoding.UTF8.GetBytes(xml));
    }

    public static ZoneCellEntityContent Read(ReadOnlySpan<byte> xml)
    {
        if (xml.IsEmpty || xml.Length > MaximumDocumentBytes)
            throw new InvalidDataException("Cell entity XML is empty or exceeds the managed document bound.");
        var bytes = xml.ToArray();
        using var stream = new MemoryStream(bytes, false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, CloseInput = false,
            MaxCharactersInDocument = MaximumDocumentBytes
        });
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidDataException("Cell entity XML has no root element.");
        ValidateStructure(root);
        return new(bytes, root);
    }

    private static void ValidateStructure(XElement root)
    {
        var pending = new Stack<(XElement Element, int Depth)>();
        pending.Push((root, 1));
        while (pending.TryPop(out var item))
        {
            if (item.Depth > MaximumElementDepth)
                throw new InvalidDataException("Cell entity XML exceeds the managed element-depth bound.");
            // XElement does not retain which lexical prefix named an element when several
            // prefixes share a namespace. Do not let reconstructed names change the native
            // literal tag/attribute gate. Namespace support requires a prefix-preserving reader.
            if (item.Element.Name.NamespaceName.Length != 0 || item.Element.Attributes().Any(attribute =>
                    attribute.IsNamespaceDeclaration || attribute.Name.NamespaceName.Length != 0))
                throw new NotSupportedException("Namespaced cell entity XML requires a native lexical-name preserving reader.");
            foreach (var child in item.Element.Elements()) pending.Push((child, item.Depth + 1));
        }
    }
}

internal static class ZoneCellEntityValues
{
    internal static bool NameEquals(string left, string right)
    {
        if (left.Length != right.Length) return false;
        for (var index = 0; index < left.Length; index++)
        {
            var a = left[index] is >= 'A' and <= 'Z' ? left[index] + 32 : left[index];
            var b = right[index] is >= 'A' and <= 'Z' ? right[index] + 32 : right[index];
            if (a != b) return false;
        }
        return true;
    }

    internal static int Integer(ZoneCellEntityXmlElement node, string name, int missing) =>
        node.Attribute(name) is { } value ? ZoneWorldMapXmlValue.Int32(value) : missing;

    internal static uint? OptionalId(ZoneCellEntityXmlElement node, string name) =>
        node.Attribute(name) is { } value ? unchecked((uint)ZoneWorldMapXmlValue.Int32(value)) : null;

    internal static bool Boolean(ZoneCellEntityXmlElement node, string name, bool missing) =>
        node.Attribute(name) is { } value ? ZoneWorldMapXmlValue.Int32(value) != 0 : missing;

    // CXmlNode+C8 / 365943E0 uses XlAtoUI64(base16), not decimal atoi or Guid.Parse.
    // 3301F990 accepts ASCII whitespace/optional0x, no sign in base16, and wraps overflow.
    internal static ulong Hexadecimal(string text)
    {
        var index = 0;
        SkipWhitespace(text, ref index);
        if (index + 1 < text.Length && text[index] == '0' && text[index + 1] is 'x' or 'X') index += 2;
        ulong value = 0;
        while (index < text.Length)
        {
            var digit = text[index] switch
            {
                >= '0' and <= '9' => text[index] - '0',
                >= 'a' and <= 'f' => text[index] - 'a' + 10,
                >= 'A' and <= 'F' => text[index] - 'A' + 10,
                _ => -1
            };
            if (digit < 0) break;
            value = unchecked(value * 16 + (uint)digit);
            index++;
        }
        return value;
    }

    // 36594260/36594000 commit only after sscanf reads all three/four components. Literal
    // commas do not skip preceding whitespace; each %f does skip leading ASCII whitespace.
    internal static Vector3 Vector(string? text, Vector3 missing)
    {
        Span<float> values = stackalloc float[3];
        return Components(text, values) ? new(values[0], values[1], values[2]) : missing;
    }

    internal static Quaternion Rotation(string? text)
    {
        Span<float> values = stackalloc float[4];
        // Serialized Rotate is W,X,Y,Z; the CXmlNode getter stores native X,Y,Z,W.
        return Components(text, values) ? new(values[1], values[2], values[3], values[0]) : Quaternion.Identity;
    }

    private static bool Components(string? text, Span<float> values)
    {
        if (text is null) return false;
        var index = 0;
        for (var component = 0; component < values.Length; component++)
        {
            SkipWhitespace(text, ref index);
            var start = index;
            if (index < text.Length && text[index] is '+' or '-') index++;
            var digits = 0;
            while (index < text.Length && IsDigit(text[index])) { index++; digits++; }
            if (index < text.Length && text[index] == '.')
            {
                index++;
                while (index < text.Length && IsDigit(text[index])) { index++; digits++; }
            }
            if (digits == 0) return false;
            if (index < text.Length && text[index] is 'e' or 'E')
            {
                index++;
                if (index < text.Length && text[index] is '+' or '-') index++;
                var exponent = index;
                while (index < text.Length && IsDigit(text[index])) index++;
                if (index == exponent) return false;
            }
            values[component] = ZoneWorldMapXmlValue.Single(text[start..index]);
            if (component < values.Length - 1 && (index >= text.Length || text[index++] != ',')) return false;
        }
        return true;
    }

    private static void SkipWhitespace(string text, ref int index)
    {
        while (index < text.Length && text[index] is ' ' or '\t' or '\r' or '\n' or '\v' or '\f') index++;
    }
    private static bool IsDigit(char value) => value is >= '0' and <= '9';
}
