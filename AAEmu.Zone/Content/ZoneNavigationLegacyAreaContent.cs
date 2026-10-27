using System.Buffers.Binary;
using System.Numerics;

namespace AAEmu.Zone.Content;

/// <summary>An authored legacy region, retaining its native parameter bytes and point order.</summary>
public sealed class ZoneNavigationLegacyRegionRow
{
    private readonly byte[] _parameters;
    public int SourceIndex { get; }
    public ZoneNavigationAreaName Name { get; }
    public ReadOnlySpan<byte> ParameterBytes => _parameters;
    public IReadOnlyList<Vector3> Points { get; }

    internal ZoneNavigationLegacyRegionRow(int sourceIndex, ZoneNavigationAreaName name,
        ReadOnlySpan<byte> parameters, Vector3[] points)
    {
        SourceIndex = sourceIndex;
        Name = name;
        _parameters = parameters.ToArray();
        Points = Array.AsReadOnly(points);
    }
}

/// <summary>
/// The ten-section areas*.bai body read by native 3107AB58 in dedicated Zone mode (2).
/// These are authored rows, before runtime region construction and admission. In particular,
/// modifier IDs come from the native manager's reusable pool, not from the authored ID field.
/// </summary>
public sealed class ZoneNavigationLegacyAreaContent
{
    public int Version { get; }
    public IReadOnlyList<ZoneNavigationAreaRow> ForbiddenAreaRows { get; }
    public IReadOnlyList<ZoneNavigationAreaModifierRow> NavigationModifierRows { get; }
    public IReadOnlyList<ZoneNavigationAreaRow> DesignerForbiddenAreaRows { get; }
    public IReadOnlyList<ZoneNavigationAreaRow> ForbiddenBoundaryRows { get; }
    public IReadOnlyList<ZoneNavigationAreaRow> NpcRoamingAreaRows { get; }
    public IReadOnlyList<ZoneNavigationLegacyRegionRow> ExtraLinkCostModifierRows { get; }
    public IReadOnlyList<ZoneNavigationLegacyRegionRow> DynamicTriangulationAreaRows { get; }
    public IReadOnlyList<ZoneNavigationLegacyRegionRow> DesignerPathRows { get; }
    public IReadOnlyList<ZoneNavigationLegacyRegionRow> ShapeRows { get; }
    public IReadOnlyList<ZoneNavigationAreaRow> NpcCombatAreaRows { get; }

    private ZoneNavigationLegacyAreaContent(ref Reader reader, int version)
    {
        Version = version;
        ForbiddenAreaRows = Array.AsReadOnly(reader.Areas("forbidden areas"));
        NavigationModifierRows = Array.AsReadOnly(reader.Modifiers());
        DesignerForbiddenAreaRows = Array.AsReadOnly(reader.Areas("designer forbidden areas"));
        ForbiddenBoundaryRows = Array.AsReadOnly(reader.Areas("forbidden boundaries"));
        NpcRoamingAreaRows = Array.AsReadOnly(reader.Areas("NPC roaming areas"));
        // 31057CE8 reads a four-byte cost and two authored vectors before the points.
        ExtraLinkCostModifierRows = Array.AsReadOnly(reader.Regions("extra link cost modifiers", 28, 0));
        // 31057B98 reads name/points; 3107AB58 then reads each section's fixed parameters.
        DynamicTriangulationAreaRows = Array.AsReadOnly(reader.Regions("dynamic triangulation areas", 0, 4));
        DesignerPathRows = Array.AsReadOnly(reader.Regions("designer paths", 0, 16));
        ShapeRows = Array.AsReadOnly(reader.Regions("shapes", 0, 16));
        // 3105B718 initializes its count to zero before reading. At exact EOF the final read
        // leaves it zero; all 18 archived version-21 files use this nine-section form. There
        // is no version branch here in native code. A partial count or row remains invalid.
        NpcCombatAreaRows = Array.AsReadOnly(reader.IsAtEnd ? [] : reader.Areas("NPC combat areas"));
        reader.RequireEnd();
    }

    public static ZoneNavigationLegacyAreaContent LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static ZoneNavigationLegacyAreaContent Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return Read(copy.GetBuffer().AsSpan(0, checked((int)copy.Length)));
    }

    public static ZoneNavigationLegacyAreaContent Read(ReadOnlySpan<byte> document)
    {
        var reader = new Reader(document);
        var version = unchecked((int)reader.UInt32("version"));
        if (version < 21)
            throw new NotSupportedException($"Navigation area version {version} is unsupported; the dedicated legacy format requires version 21 or later.");
        return new(ref reader, version);
    }

    private ref struct Reader(ReadOnlySpan<byte> document)
    {
        private readonly ReadOnlySpan<byte> _document = document;
        private int _position;
        internal readonly bool IsAtEnd => _position == _document.Length;

        private ReadOnlySpan<byte> Bytes(int count, string field)
        {
            if (count < 0 || count > _document.Length - _position)
                throw new InvalidDataException($"The legacy navigation area file is truncated at byte {_position} while reading {field}.");
            var bytes = _document.Slice(_position, count);
            _position += count;
            return bytes;
        }

        internal uint UInt32(string field) => BinaryPrimitives.ReadUInt32LittleEndian(Bytes(4, field));

        private int Count(string field, int minimumRowBytes)
        {
            var count = UInt32(field);
            if (count > (_document.Length - _position) / minimumRowBytes)
                throw new InvalidDataException($"The legacy navigation {field} count {count} exceeds the remaining document at byte {_position}.");
            return (int)count;
        }

        private ZoneNavigationAreaName Name(string section)
        {
            var length = UInt32($"{section} name length");
            // Native polygon and general point-list helpers enforce this bound. Applying it
            // to the unchecked modifier/cost stack buffers is an explicit managed input guard.
            if (length >= 1024)
                throw new InvalidDataException($"The legacy navigation {section} name length {length} exceeds 1023 bytes.");
            return new(Bytes((int)length, $"{section} name"));
        }

        private Vector3[] Points(string section, bool bounded, bool addZero = false)
        {
            var count = UInt32($"{section} point count");
            if (bounded && count > 10000)
                throw new InvalidDataException($"The legacy navigation {section} has {count} points; the native maximum is 10000.");
            if (count > (_document.Length - _position) / 12)
                throw new InvalidDataException($"The legacy navigation {section} point count {count} exceeds the remaining document at byte {_position}.");
            var points = new Vector3[(int)count];
            for (var i = 0; i < points.Length; i++)
            {
                var bytes = Bytes(12, $"{section} point");
                var x = BinaryPrimitives.ReadSingleLittleEndian(bytes);
                var y = BinaryPrimitives.ReadSingleLittleEndian(bytes[4..]);
                // 3105B558 and 31057B98 copy coordinates. Only 31060AB8 performs ADDSS with
                // its explicit zero offsets, which changes negative zero and quiets NaNs.
                if (addZero) { x += 0f; y += 0f; }
                points[i] = new(x, y, BinaryPrimitives.ReadSingleLittleEndian(bytes[8..]));
            }
            return points;
        }

        internal ZoneNavigationAreaRow[] Areas(string section)
        {
            var rows = new ZoneNavigationAreaRow[Count($"{section} row", 8)];
            for (var i = 0; i < rows.Length; i++)
                rows[i] = new(i, Name(section), Points(section, true));
            return rows;
        }

        internal ZoneNavigationAreaModifierRow[] Modifiers()
        {
            const string section = "navigation modifiers";
            var rows = new ZoneNavigationAreaModifierRow[Count($"{section} row", 46)];
            for (var i = 0; i < rows.Length; i++)
            {
                var name = Name(section);
                // 31060AB8's older-version branches cannot run under the outer >=21 gate.
                var parameters = Bytes(38, $"{section} parameters");
                rows[i] = new(i, name, parameters, Points(section, false, true));
            }
            return rows;
        }

        internal ZoneNavigationLegacyRegionRow[] Regions(string section, int prefixBytes, int suffixBytes)
        {
            var rows = new ZoneNavigationLegacyRegionRow[Count($"{section} row", 8 + prefixBytes + suffixBytes)];
            for (var i = 0; i < rows.Length; i++)
            {
                var name = Name(section);
                var parameters = Bytes(prefixBytes, $"{section} parameters");
                var points = Points(section, false);
                if (suffixBytes != 0) parameters = Bytes(suffixBytes, $"{section} parameters");
                rows[i] = new(i, name, parameters, points);
            }
            return rows;
        }

        internal void RequireEnd()
        {
            // Native ignores unread bytes. Reject unmapped tails after the final section;
            // only the precisely proven absent final count is accepted above.
            if (_position != _document.Length)
                throw new InvalidDataException($"The legacy navigation area file has {_document.Length - _position} unmapped trailing bytes after its ten sections.");
        }
    }
}
