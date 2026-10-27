using System.Buffers.Binary;

namespace AAEmu.Zone.Content;

/// <summary>A 32-byte authored vertex record from native CVertexList::ReadFromFile (311A2864).</summary>
public sealed class ZoneNavigationLegacyVertexRow
{
    private readonly byte[] _bytes;
    public int SourceIndex { get; }
    public ReadOnlySpan<byte> SerializedBytes => _bytes;
    /// <summary>Native copies seven four-byte values and two bytes; the last two bytes are padding.</summary>
    public ReadOnlySpan<byte> ValueBytes => _bytes.AsSpan(0, 30);

    internal ZoneNavigationLegacyVertexRow(int sourceIndex, ReadOnlySpan<byte> bytes)
    { SourceIndex = sourceIndex; _bytes = bytes.ToArray(); }
}

/// <summary>The dedicated vertex-list body. No field meanings or graph links are synthesized.</summary>
public sealed class ZoneNavigationLegacyVertexContent
{
    public int Version => 2;
    public int AuthoredCount { get; }
    public IReadOnlyList<ZoneNavigationLegacyVertexRow> Rows { get; }

    private ZoneNavigationLegacyVertexContent(int authoredCount, ZoneNavigationLegacyVertexRow[] rows)
    { AuthoredCount = authoredCount; Rows = Array.AsReadOnly(rows); }

    public static ZoneNavigationLegacyVertexContent Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return Read(copy.GetBuffer().AsSpan(0, checked((int)copy.Length)));
    }

    public static ZoneNavigationLegacyVertexContent Read(ReadOnlySpan<byte> document)
    {
        if (document.Length < 4) throw new InvalidDataException("The legacy vertex-list version is truncated.");
        var version = BinaryPrimitives.ReadUInt32LittleEndian(document);
        if (version != 2) throw new NotSupportedException($"Legacy vertex-list version {version} is unsupported; native requires version 2.");
        if (document.Length < 8) throw new InvalidDataException("The legacy vertex-list count is truncated.");
        var count = BinaryPrimitives.ReadInt32LittleEndian(document[4..]);
        // The native signed comparison treats nonpositive counts as an empty successful list.
        var rowCount = Math.Max(count, 0);
        if (rowCount > (document.Length - 8) / 32)
            throw new InvalidDataException($"The legacy vertex-list count {count} exceeds the remaining document.");
        if (document.Length - 8 != rowCount * 32)
            throw new InvalidDataException("The legacy vertex list has unmapped trailing bytes.");
        var rows = new ZoneNavigationLegacyVertexRow[rowCount];
        for (var i = 0; i < rows.Length; i++) rows[i] = new(i, document.Slice(8 + i * 32, 32));
        return new(count, rows);
    }
}
