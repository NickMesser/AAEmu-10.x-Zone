using System.Buffers.Binary;
using System.Numerics;

namespace AAEmu.Zone.Content;

/// <summary>A complete authored node record staged by native 31103988 before node creation.</summary>
public sealed class ZoneNavigationLegacyGraphNodeRow
{
    private readonly byte[] _bytes;
    public int SourceIndex { get; }
    public ReadOnlySpan<byte> SerializedBytes => _bytes;
    public uint NodeId => BinaryPrimitives.ReadUInt32LittleEndian(_bytes);
    public Vector3 Position => new(BinaryPrimitives.ReadSingleLittleEndian(_bytes.AsSpan(28)),
        BinaryPrimitives.ReadSingleLittleEndian(_bytes.AsSpan(32)), BinaryPrimitives.ReadSingleLittleEndian(_bytes.AsSpan(36)));
    public ushort NavigationType => BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(56));
    public byte Flags => _bytes[58];
    /// <summary>Authored signed obstacle-vertex indices used by native type2 nodes.</summary>
    public IReadOnlyList<int> VertexIndices { get; }

    internal ZoneNavigationLegacyGraphNodeRow(int sourceIndex, ReadOnlySpan<byte> bytes)
    {
        SourceIndex = sourceIndex;
        _bytes = bytes.ToArray();
        VertexIndices = Array.AsReadOnly(new[] { BinaryPrimitives.ReadInt32LittleEndian(bytes[44..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[48..]), BinaryPrimitives.ReadInt32LittleEndian(bytes[52..]) });
    }
}

/// <summary>
/// An authored link. Its IDs address the authored node map, not vertex-list indices or runtime
/// pool IDs. Native 310FB7E8 resolves endpoints before allocating paired runtime links.
/// </summary>
public sealed class ZoneNavigationLegacyGraphLinkRow
{
    private readonly byte[] _bytes;
    public int SourceIndex { get; }
    public ReadOnlySpan<byte> SerializedBytes => _bytes;
    public uint SourceNodeId => BinaryPrimitives.ReadUInt32LittleEndian(_bytes);
    public uint TargetNodeId => BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(4));

    internal ZoneNavigationLegacyGraphLinkRow(int sourceIndex, ReadOnlySpan<byte> bytes)
    { SourceIndex = sourceIndex; _bytes = bytes.ToArray(); }
}

/// <summary>
/// The complete version55 network body shared by dedicated31103988 and streamed311030A8. Authored
/// rows remain separate from node reuse, modifier lookup, runtime pools and link construction.
/// Reading this content alone does not produce a ready navigation graph.
/// </summary>
public sealed class ZoneNavigationLegacyGraphContent
{
    public int Version => 55;
    /// <summary>Native copies these header vectors to CGraph+0x1A8 and +0x1B4 respectively.</summary>
    public Vector3 HeaderFirstVector { get; }
    public Vector3 HeaderSecondVector { get; }
    public int AuthoredNodeCount { get; }
    public int AuthoredLinkCount { get; }
    public IReadOnlyList<ZoneNavigationLegacyGraphNodeRow> Nodes { get; }
    public IReadOnlyList<ZoneNavigationLegacyGraphLinkRow> Links { get; }

    private ZoneNavigationLegacyGraphContent(ref Reader reader)
    {
        HeaderFirstVector = reader.Vector("first header vector");
        HeaderSecondVector = reader.Vector("second header vector");
        AuthoredNodeCount = reader.Count("node count", 60);
        var nodes = new ZoneNavigationLegacyGraphNodeRow[Math.Max(AuthoredNodeCount, 0)];
        for (var i = 0; i < nodes.Length; i++) nodes[i] = new(i, reader.Bytes(60, "node record"));
        AuthoredLinkCount = reader.Count("link count", 44);
        var links = new ZoneNavigationLegacyGraphLinkRow[Math.Max(AuthoredLinkCount, 0)];
        for (var i = 0; i < links.Length; i++) links[i] = new(i, reader.Bytes(44, "link record"));
        reader.RequireEnd();
        Nodes = Array.AsReadOnly(nodes);
        Links = Array.AsReadOnly(links);
    }

    public static ZoneNavigationLegacyGraphContent Load(Stream stream)
        => LoadCore(stream, throwOnMissingFirstNode: true)!;

    /// <summary>311030A8 streamed records; unlike 31103988 this path does not require node key1.</summary>
    public static ZoneNavigationLegacyGraphContent LoadStreaming(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var copy = new MemoryStream(); stream.CopyTo(copy);
        var reader = new Reader(copy.GetBuffer().AsSpan(0, checked((int)copy.Length)));
        var version = reader.UInt32("version");
        if (version != 55)
            throw new NotSupportedException($"Streamed network version {version} is unsupported; native requires version 55.");
        return new(ref reader);
    }

    // The startup caller must distinguish native key1 failure from malformed managed input.
    // Do not publish an incomplete graph or weaken the public reader's validation contract.
    internal static (ZoneNavigationLegacyGraphContent? Graph, string? Diagnostic) LoadForStartup(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var copy = new MemoryStream();
        // Read capabilities are outside the native version-rejection boundary.
        stream.CopyTo(copy);
        try
        {
            var graph = ReadCore(copy.GetBuffer().AsSpan(0, checked((int)copy.Length)), throwOnMissingFirstNode: false);
            return (graph, graph is null ? "First node not found - native navigation requires authored node ID 1." : null);
        }
        catch (NotSupportedException ex) { return (null, ex.Message); }
    }

    private static ZoneNavigationLegacyGraphContent? LoadCore(Stream stream, bool throwOnMissingFirstNode)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return ReadCore(copy.GetBuffer().AsSpan(0, checked((int)copy.Length)), throwOnMissingFirstNode);
    }

    public static ZoneNavigationLegacyGraphContent Read(ReadOnlySpan<byte> document)
        => ReadCore(document, throwOnMissingFirstNode: true)!;

    private static ZoneNavigationLegacyGraphContent? ReadCore(ReadOnlySpan<byte> document, bool throwOnMissingFirstNode)
    {
        var reader = new Reader(document);
        var version = reader.UInt32("version");
        if (version != 55)
            throw new NotSupportedException($"Legacy network version {version} is unsupported; native requires version 55.");
        var content = new ZoneNavigationLegacyGraphContent(ref reader);
        // 31103988 checks authored key1 after staging both complete sections. Runtime node
        // construction remains separate, so this admission alone cannot prove graph readiness.
        if (content.Nodes.Any(node => node.NodeId == 1)) return content;
        if (throwOnMissingFirstNode)
            throw new InvalidDataException("First node not found - native navigation loading fails without authored node ID 1.");
        return null;
    }

    private ref struct Reader(ReadOnlySpan<byte> document)
    {
        private readonly ReadOnlySpan<byte> _document = document;
        private int _position;

        internal ReadOnlySpan<byte> Bytes(int count, string field)
        {
            if (count < 0 || count > _document.Length - _position)
                throw new InvalidDataException($"The legacy network file is truncated at byte {_position} while reading {field}.");
            var bytes = _document.Slice(_position, count);
            _position += count;
            return bytes;
        }

        internal uint UInt32(string field) => BinaryPrimitives.ReadUInt32LittleEndian(Bytes(4, field));

        internal int Count(string field, int rowBytes)
        {
            var count = unchecked((int)UInt32(field));
            if (count > (_document.Length - _position) / rowBytes)
                throw new InvalidDataException($"The legacy network {field} {count} exceeds the remaining document.");
            return count;
        }

        internal Vector3 Vector(string field)
        {
            var bytes = Bytes(12, field);
            return new(BinaryPrimitives.ReadSingleLittleEndian(bytes),
                BinaryPrimitives.ReadSingleLittleEndian(bytes[4..]), BinaryPrimitives.ReadSingleLittleEndian(bytes[8..]));
        }

        internal readonly void RequireEnd()
        {
            if (_position != _document.Length)
                throw new InvalidDataException($"The legacy network file has {_document.Length - _position} unmapped trailing bytes.");
        }
    }
}
