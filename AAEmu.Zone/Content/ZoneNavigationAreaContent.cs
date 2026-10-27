using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using AAEmu.Zone.Simulation;

namespace AAEmu.Zone.Content;

/// <summary>A native byte name, including the authored bytes after its first NUL.</summary>
public sealed class ZoneNavigationAreaName
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private readonly byte[] _source;
    private readonly int _length;
    private readonly string? _text;

    public ReadOnlySpan<byte> SourceBytes => _source;
    public ReadOnlySpan<byte> Bytes => _source.AsSpan(0, _length);
    public bool HasUtf8Text => _text is not null;
    public string Text => _text ?? throw new InvalidDataException("The navigation area name is not valid UTF-8; its native bytes remain available.");

    internal ZoneNavigationAreaName(ReadOnlySpan<byte> source)
    {
        _source = source.ToArray();
        var zero = source.IndexOf((byte)0);
        _length = zero < 0 ? source.Length : zero;
        try { _text = Utf8.GetString(Bytes); }
        catch (DecoderFallbackException) { }
    }
}

public sealed class ZoneNavigationAreaRow
{
    public int SourceIndex { get; }
    public ZoneNavigationAreaName Name { get; }
    public ZoneNavigationShape Shape { get; }

    internal ZoneNavigationAreaRow(int sourceIndex, ZoneNavigationAreaName name, Vector3[] points)
    { SourceIndex = sourceIndex; Name = name; Shape = new(points); }
}

/// <summary>
/// The second streaming section. Its 38 authored parameter bytes are retained without assigning
/// unproven gameplay meanings. Native 31149494 consumes two u64 values, one byte, five u32 values,
/// and one byte, in that order, before its points.
/// The full loader then replaces runtime field +0x14 (parameter bytes 33..36) with an allocated
/// manager ID. This content preserves the authored field; assigning runtime IDs is a later phase.
/// </summary>
public sealed class ZoneNavigationAreaModifierRow
{
    private readonly byte[] _parameters;
    public int SourceIndex { get; }
    public ZoneNavigationAreaName Name { get; }
    public ReadOnlySpan<byte> ParameterBytes => _parameters;
    public IReadOnlyList<Vector3> Points { get; }

    internal ZoneNavigationAreaModifierRow(int sourceIndex, ZoneNavigationAreaName name,
        ReadOnlySpan<byte> parameters, Vector3[] points)
    {
        SourceIndex = sourceIndex;
        Name = name;
        _parameters = parameters.ToArray();
        Points = Array.AsReadOnly(points);
    }
}

/// <summary>
/// The four-section streaming areas*.bai format consumed by native 31149764, not the broader
/// legacy 3107AB58 loader. Loading is atomic and validates the complete supplied document.
/// </summary>
public sealed class ZoneNavigationAreaContent
{
    public int Version { get; }
    public int XOffset { get; }
    public int YOffset { get; }
    public IReadOnlyList<ZoneNavigationAreaRow> ForbiddenAreaRows { get; }
    public IReadOnlyList<ZoneNavigationAreaModifierRow> NavigationModifierRows { get; }
    public IReadOnlyList<ZoneNavigationAreaRow> DesignerForbiddenAreaRows { get; }
    public IReadOnlyList<ZoneNavigationAreaRow> ForbiddenBoundaryRows { get; }
    /// <summary>First-name admissions in file order, independently for each native container.</summary>
    public IReadOnlyList<ZoneNavigationAreaRow> ForbiddenAreas { get; }
    public IReadOnlyList<ZoneNavigationAreaModifierRow> NavigationModifiers { get; }
    public IReadOnlyList<ZoneNavigationAreaRow> DesignerForbiddenAreas { get; }
    public IReadOnlyList<ZoneNavigationAreaRow> ForbiddenBoundaries { get; }

    private ZoneNavigationAreaContent(int version, int xOffset, int yOffset,
        ZoneNavigationAreaRow[] forbidden, ZoneNavigationAreaModifierRow[] modifiers,
        ZoneNavigationAreaRow[] designer, ZoneNavigationAreaRow[] boundaries)
    {
        Version = version; XOffset = xOffset; YOffset = yOffset;
        ForbiddenAreaRows = Array.AsReadOnly(forbidden);
        NavigationModifierRows = Array.AsReadOnly(modifiers);
        DesignerForbiddenAreaRows = Array.AsReadOnly(designer);
        ForbiddenBoundaryRows = Array.AsReadOnly(boundaries);
        ForbiddenAreas = FirstNames(forbidden);
        var modifierNames = new HashSet<string>(StringComparer.Ordinal);
        // The modifier map's 3108AB5C lookup also compares the retained bytes case-sensitively.
        NavigationModifiers = Array.AsReadOnly(modifiers.Where(row => modifierNames.Add(Convert.ToHexString(row.Name.Bytes))).ToArray());
        DesignerForbiddenAreas = FirstNames(designer);
        ForbiddenBoundaries = FirstNames(boundaries);
    }

    private static System.Collections.ObjectModel.ReadOnlyCollection<ZoneNavigationAreaRow> FirstNames(ZoneNavigationAreaRow[] rows)
    {
        // 31149764 probes 3117F110 before insertion. The 310F5260 comparison is byte-exact;
        // case variants and invalid UTF-8 remain distinct while an embedded NUL discards the
        // suffix for admission, without losing those source bytes in the authored row.
        var names = new HashSet<string>(StringComparer.Ordinal);
        return Array.AsReadOnly(rows.Where(row => names.Add(Convert.ToHexString(row.Name.Bytes))).ToArray());
    }

    public static ZoneNavigationAreaContent LoadFile(string path, int xOffset, int yOffset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        return Load(stream, xOffset, yOffset);
    }

    public static ZoneNavigationAreaContent Load(Stream stream, int xOffset, int yOffset)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return Read(copy.GetBuffer().AsSpan(0, checked((int)copy.Length)), xOffset, yOffset);
    }

    public static ZoneNavigationAreaContent Read(ReadOnlySpan<byte> document, int xOffset, int yOffset)
    {
        var reader = new Reader(document, xOffset, yOffset);
        var version = unchecked((int)reader.UInt32("version"));
        // 31149764 checks the signed version against 21. Its helper has no additional format
        // branches for accepted versions; the diagnostic's expected version 22 is not a gate.
        if (version < 21)
            throw new NotSupportedException($"Navigation area version {version} is unsupported; the streaming format requires version 21 or later.");
        var forbidden = reader.Areas("forbidden areas");
        var modifiers = reader.Modifiers();
        var designer = reader.Areas("designer forbidden areas");
        var boundaries = reader.Areas("forbidden boundaries");
        // Native streaming loads a prefix and ignores unread bytes. Rejecting a remaining tail
        // prevents a legacy or unmapped format from silently appearing to have been fully loaded.
        reader.RequireEnd();
        return new(version, xOffset, yOffset, forbidden, modifiers, designer, boundaries);
    }

    private ref struct Reader(ReadOnlySpan<byte> document, int xOffset, int yOffset)
    {
        private readonly ReadOnlySpan<byte> _document = document;
        private readonly float _xOffset = (float)xOffset;
        private readonly float _yOffset = (float)yOffset;
        private int _position;

        private ReadOnlySpan<byte> Bytes(int count, string field)
        {
            if (count < 0 || count > _document.Length - _position)
                throw new InvalidDataException($"The navigation area file is truncated at byte {_position} while reading {field}.");
            var result = _document.Slice(_position, count);
            _position += count;
            return result;
        }

        internal uint UInt32(string field) => BinaryPrimitives.ReadUInt32LittleEndian(Bytes(4, field));

        private int Count(string field, int minimumRowBytes)
        {
            var count = UInt32(field);
            if (count > (_document.Length - _position) / minimumRowBytes)
                throw new InvalidDataException($"The navigation area {field} count {count} exceeds the remaining document at byte {_position}.");
            return (int)count;
        }

        private ZoneNavigationAreaName Name(string section)
        {
            var length = UInt32($"{section} name length");
            // 31149264 enforces this bound. The modifier helper uses an unchecked stack buffer;
            // applying the same bound to modifiers is an explicit managed input guard.
            if (length >= 1024)
                throw new InvalidDataException($"The navigation area {section} name length {length} exceeds 1023 bytes.");
            return new(Bytes((int)length, $"{section} name"));
        }

        private Vector3[] Points(string section, bool bounded)
        {
            var count = UInt32($"{section} point count");
            if (bounded && count > 10000)
                throw new InvalidDataException($"The navigation area {section} has {count} points; the native maximum is 10000.");
            if (count > (_document.Length - _position) / 12)
                throw new InvalidDataException($"The navigation area {section} point count {count} exceeds the remaining document at byte {_position}.");
            var result = new Vector3[(int)count];
            for (var i = 0; i < result.Length; i++)
            {
                var value = Bytes(12, $"{section} point");
                // Match CVTSI2SS then ADDSS, including large integer offset rounding. Z is copied.
                result[i] = new(BinaryPrimitives.ReadSingleLittleEndian(value) + _xOffset,
                    BinaryPrimitives.ReadSingleLittleEndian(value[4..]) + _yOffset,
                    BinaryPrimitives.ReadSingleLittleEndian(value[8..]));
            }
            return result;
        }

        internal ZoneNavigationAreaRow[] Areas(string section)
        {
            var result = new ZoneNavigationAreaRow[Count($"{section} row", 8)];
            for (var i = 0; i < result.Length; i++)
            {
                var name = Name(section);
                result[i] = new(i, name, Points(section, true));
            }
            return result;
        }

        internal ZoneNavigationAreaModifierRow[] Modifiers()
        {
            const string section = "navigation modifiers";
            var result = new ZoneNavigationAreaModifierRow[Count($"{section} row", 46)];
            for (var i = 0; i < result.Length; i++)
            {
                var name = Name(section);
                var parameters = Bytes(38, $"{section} parameters");
                result[i] = new(i, name, parameters, Points(section, false));
            }
            return result;
        }

        internal void RequireEnd()
        {
            if (_position != _document.Length)
                throw new InvalidDataException($"The navigation area file contains {_document.Length - _position} unmapped trailing bytes after its four streaming sections.");
        }
    }
}
