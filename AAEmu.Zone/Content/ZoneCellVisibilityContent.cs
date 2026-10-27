using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace AAEmu.Zone.Content;

public enum ZoneCellVisibilityKind { Area, Portal, OcclusionArea }

/// <summary>One native CVisArea compiled record, before engine-cell translation or resource creation.</summary>
public sealed class ZoneCellVisibilityRecord
{
    private readonly byte[] _data;
    public ZoneCellVisibilityKind Kind { get; }
    public int SourceOrder { get; }
    public int SourceOffset { get; }
    public int Version { get; }
    public string NativeName { get; }
    public Vector3 BoundsMin => ZoneCellObjectValues.Vector(_data, 4);
    public Vector3 BoundsMax => ZoneCellObjectValues.Vector(_data, 16);
    public Vector3 StaticBoundsMin => ZoneCellObjectValues.Vector(_data, 28);
    public Vector3 StaticBoundsMax => ZoneCellObjectValues.Vector(_data, 40);
    public int AuthoredObjectBlockBytes => ZoneCellObjectValues.Int(_data, 0x54);
    public IReadOnlyList<Vector3> Points { get; }
    public ZoneCellObjectContent? EmbeddedObjects { get; }
    /// <summary>The v3/v4 twelve-byte value copied to native CVisArea+C8; zero for v2.</summary>
    public Vector3 NativeVectorC8 => Version >= 3 ? ZoneCellObjectValues.Vector(_data, 0x108) : Vector3.Zero;
    /// <summary>The v4 final float copied to CVisArea+F0; native defaults earlier records to one.</summary>
    public float NativeFloatF0 => Version >= 4 ? ZoneCellObjectValues.Float(_data, _data.Length - 4) : 1f;
    public ReadOnlySpan<byte> RawRecord => _data;

    internal ZoneCellVisibilityRecord(ZoneCellVisibilityKind kind, int order, int offset, int version,
        string name, byte[] data, Vector3[] points, ZoneCellObjectContent? objects)
    {
        Kind = kind; SourceOrder = order; SourceOffset = offset; Version = version; NativeName = name;
        _data = data; Points = Array.AsReadOnly(points); EmbeddedObjects = objects;
    }
}

/// <summary>
/// Bounded visareas.dat decoding for native 30161EF0/30198BF0/3018C290. Preserves both resource
/// tables and the complete native compiled block. This is content admission, not a visibility
/// manager, render-node owner, physicalization operation or scene-readiness claim.
/// </summary>
public sealed class ZoneCellVisibilityContent
{
    private readonly byte[] _compiled;
    public const int MaximumFileBytes = ZoneCellObjectContent.MaximumFileBytes;
    internal ZoneCellObjectContent ResourceTables { get; }
    public IReadOnlyList<ZoneCellObjectResource> ModelResources { get; }
    public IReadOnlyList<ZoneCellObjectResource> MaterialResources { get; }
    public IReadOnlyList<ZoneCellVisibilityRecord> Records { get; }
    public int ResourceTableBytes { get; }
    public int AreaCount { get; }
    public int PortalCount { get; }
    public int OcclusionAreaCount { get; }
    public ReadOnlySpan<byte> RawCompiledData => _compiled;

    private ZoneCellVisibilityContent(ZoneCellObjectContent tables, int tableBytes, byte[] compiled,
        ZoneCellVisibilityRecord[] records, int areas, int portals, int occlusion)
    {
        ResourceTables = tables;
        ModelResources = tables.ModelResources; MaterialResources = tables.MaterialResources;
        ResourceTableBytes = tableBytes; _compiled = compiled; Records = Array.AsReadOnly(records);
        AreaCount = areas; PortalCount = portals; OcclusionAreaCount = occlusion;
    }

    public static ZoneCellVisibilityContent LoadFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    /// <summary>Reads without taking ownership of the supplied stream.</summary>
    public static ZoneCellVisibilityContent Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        var scratch = new byte[81920];
        int count;
        while ((count = stream.Read(scratch)) != 0)
        {
            if (buffer.Length + count > MaximumFileBytes)
                throw new InvalidDataException("Visibility file exceeds the managed allocation bound.");
            buffer.Write(scratch, 0, count);
        }
        return Read(buffer.ToArray());
    }

    public static ZoneCellVisibilityContent Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumFileBytes)
            throw new InvalidDataException("Visibility file exceeds the managed allocation bound.");
        var data = bytes.ToArray();
        var reader = new Reader(data);
        reader.SkipResourceTable(); reader.SkipResourceTable();
        var tableBytes = reader.Position;
        // Reuse the established 260-byte table/path/index decoder; this input has no object rows.
        var tables = ZoneCellObjectContent.Read(data.AsSpan(0, tableBytes), ZoneCellObjectFileKind.BigObjects);
        var header = reader.Take(20);
        if (BinaryPrimitives.ReadInt32LittleEndian(header) != 4)
            throw reader.Error("Unsupported visibility block version");
        var size = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        if (size != bytes.Length - tableBytes)
            throw reader.Error("Visibility block length differs from its complete native chunk");
        var areas = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        var portals = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        var occlusion = BinaryPrimitives.ReadInt32LittleEndian(header[16..]);
        if (areas < 0 || portals < 0 || occlusion < 0 ||
            (long)areas + portals + occlusion > (bytes.Length - reader.Position) / 268)
            throw reader.Error("Invalid visibility record counts");
        var records = new List<ZoneCellVisibilityRecord>();
        foreach (var (kind, count) in new[] { (ZoneCellVisibilityKind.Area, areas),
            (ZoneCellVisibilityKind.Portal, portals), (ZoneCellVisibilityKind.OcclusionArea, occlusion) })
        {
            for (var i = 0; i < count; i++) records.Add(reader.Record(kind, records.Count, tableBytes));
        }
        if (reader.Position != bytes.Length) throw reader.Error("Trailing visibility bytes");
        return new(tables, tableBytes, data.AsSpan(tableBytes).ToArray(), records.ToArray(), areas, portals, occlusion);
    }

    private sealed class Reader(byte[] data)
    {
        internal int Position;
        private int _embeddedDecodeBytes;
        internal InvalidDataException Error(string message) => new($"{message} at visibility offset 0x{Position:X}.");
        internal ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || Position > data.Length - count) throw Error("Truncated visibility record");
            var result = data.AsSpan(Position, count); Position += count; return result;
        }
        private int Int() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        internal void SkipResourceTable()
        {
            var count = Int();
            if (count < 0 || count > (data.Length - Position) / 260) throw Error("Invalid resource table length");
            Take(count * 260);
        }
        internal ZoneCellVisibilityRecord Record(ZoneCellVisibilityKind kind, int order, int tableBytes)
        {
            var start = Position;
            var header = Take(0x108);
            var version = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (version is not (2 or 3 or 4)) throw Error("Unsupported visibility record version");
            // Native strncpy_s reads at most 64 bytes beginning at +34, despite the nominal 32-byte
            // name field. Preserve that actual consumption without exposing an unterminated string.
            var nameStorage = header.Slice(0x34, 64);
            var terminator = nameStorage.IndexOf((byte)0);
            if (terminator < 0) throw Error("Unterminated native visibility name");
            var nameBytes = nameStorage[..terminator];
            foreach (var character in nameBytes)
                if (character > 127) throw Error("Unsupported non-ASCII visibility name");
            var name = Encoding.ASCII.GetString(nameBytes);
            var objectBytes = BinaryPrimitives.ReadInt32LittleEndian(header[0x54..]);
            if (version >= 3) Take(12);
            var pointCount = Int();
            if (pointCount < 0 || pointCount > (data.Length - Position) / 12) throw Error("Invalid visibility point count");
            var points = new Vector3[pointCount];
            for (var i = 0; i < points.Length; i++) points[i] = ZoneCellObjectValues.Vector(Take(12), 0);
            ZoneCellObjectContent? embedded = null;
            if (objectBytes > 4)
            {
                // Native 3018C290 uses this word only as a >4 gate, then consumes 30229B20's whole
                // recursive tree. Admission also checks the authored length, avoiding an unchecked
                // native read. The six configured chunks agree exactly with this boundary.
                var block = Take(objectBytes);
                var combinedLength = checked(tableBytes + objectBytes);
                if (combinedLength > MaximumFileBytes - _embeddedDecodeBytes)
                    throw Error("Embedded visibility decoding exceeds the managed allocation bound");
                _embeddedDecodeBytes += combinedLength;
                var combined = new byte[combinedLength];
                data.AsSpan(0, tableBytes).CopyTo(combined);
                block.CopyTo(combined.AsSpan(tableBytes));
                embedded = ZoneCellObjectContent.Read(combined, ZoneCellObjectFileKind.Octree);
            }
            if (version >= 4) Take(4);
            return new(kind, order, start, version, name, data.AsSpan(start, Position - start).ToArray(), points, embedded);
        }
    }
}
