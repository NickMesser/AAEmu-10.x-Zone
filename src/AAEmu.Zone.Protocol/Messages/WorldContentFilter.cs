using System.Collections.Frozen;

namespace AAEmu.Zone.Protocol.Messages;

public sealed record WorldContentFilterCategory(WorldContentName Name, IReadOnlyList<WorldContentName> Filters);

/// <summary>
/// Native 39C5B580 join filter buffer: u16 category count, then s8-length binary names
/// and u16 filter counts. Duplicate categories replace their set; duplicate filters collapse.
/// The original buffer is retained, including trailing bytes ignored by the native parser.
/// </summary>
public sealed class WorldContentFilter
{
    private readonly byte[] _body;
    public static WorldContentFilter Empty { get; } = Read([]);
    public IReadOnlyDictionary<WorldContentName, IReadOnlySet<WorldContentName>> Categories { get; }
    public WorldContentNameComparer NameComparer { get; }
    public int ParsedLength { get; }
    public int TrailingLength => _body.Length - ParsedLength;

    private WorldContentFilter(byte[] body, int parsedLength,
        Dictionary<WorldContentName, IReadOnlySet<WorldContentName>> categories, WorldContentNameComparer comparer)
    {
        _body = body;
        ParsedLength = parsedLength;
        NameComparer = comparer;
        Categories = categories.ToFrozenDictionary(comparer);
    }

    public bool Contains(WorldContentName category, WorldContentName filter) =>
        Categories.TryGetValue(category, out var filters) && filters.Contains(filter);

    public byte[] ToArray() => _body.ToArray();

    public static WorldContentFilter Read(ReadOnlySpan<byte> body, WorldContentNameComparer? comparer = null)
    {
        comparer ??= WorldContentNameComparer.C;
        var categories = new Dictionary<WorldContentName, IReadOnlySet<WorldContentName>>(comparer);
        if (body.IsEmpty) return new([], 0, categories, comparer);
        var reader = new ZoneReader(body);
        var categoryCount = reader.ReadUInt16();
        if (categoryCount > reader.Remaining / 3)
            throw new InvalidDataException("World content category count exceeds the filter buffer.");
        for (var i = 0; i < categoryCount; i++)
        {
            var name = ReadName(ref reader);
            var filterCount = reader.ReadUInt16();
            if (filterCount > reader.Remaining)
                throw new InvalidDataException("World content filter count exceeds the filter buffer.");
            var filters = new HashSet<WorldContentName>(comparer);
            for (var j = 0; j < filterCount; j++) filters.Add(ReadName(ref reader));
            categories[name] = filters.ToFrozenSet(comparer);
        }
        return new(body.ToArray(), body.Length - reader.Remaining, categories, comparer);
    }

    public static WorldContentFilter Create(IReadOnlyList<WorldContentFilterCategory> categories,
        ReadOnlySpan<byte> trailingBytes = default, WorldContentNameComparer? comparer = null)
    {
        if (categories.Count > ushort.MaxValue)
            throw new InvalidDataException("Too many World content filter categories.");
        var writer = new ZoneWriter();
        writer.WriteUInt16((ushort)categories.Count);
        foreach (var category in categories)
        {
            WriteName(writer, category.Name);
            if (category.Filters.Count > ushort.MaxValue)
                throw new InvalidDataException("Too many filters in a World content category.");
            writer.WriteUInt16((ushort)category.Filters.Count);
            foreach (var filter in category.Filters) WriteName(writer, filter);
        }
        writer.WriteBytes(trailingBytes);
        return Read(writer.ToArray(), comparer);
    }

    private static WorldContentName ReadName(ref ZoneReader reader)
    {
        var length = reader.ReadSByte();
        // Negative native lengths can underflow the cursor and reach an unsafe string allocation.
        if (length < 0) throw new InvalidDataException("World content filter name has a negative byte length.");
        return new(reader.ReadBytes(length));
    }

    private static void WriteName(ZoneWriter writer, WorldContentName name)
    {
        if (name.Bytes.Length > sbyte.MaxValue)
            throw new InvalidDataException("World content filter names must be at most 127 bytes.");
        writer.WriteByte((byte)name.Bytes.Length);
        writer.WriteBytes(name.Bytes);
    }
}
