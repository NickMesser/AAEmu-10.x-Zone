using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace AAEmu.Zone.Content;

public enum ZoneCellObjectFileKind { Octree, BigObjects }
public enum ZoneCellObjectType : uint { Brush = 1, Vegetation = 2, Decal = 9, WaterVolume = 11, Road = 13 }

/// <summary>An ordered 260-byte resource-table entry; the authored word is not its array index.</summary>
public sealed class ZoneCellObjectResource
{
    private readonly byte[] _nameStorage;
    public int Index { get; }
    public uint AuthoredValue { get; }
    public string Name { get; }
    public ReadOnlySpan<byte> RawNameStorage => _nameStorage;
    internal ZoneCellObjectResource(int index, uint value, string name, byte[] storage)
    { Index = index; AuthoredValue = value; Name = name; _nameStorage = storage; }
}

/// <summary>Native row-major Matrix34 words; translation is M03/M13/M23.</summary>
public readonly record struct ZoneCellObjectTransform(float M00, float M01, float M02, float M03,
    float M10, float M11, float M12, float M13, float M20, float M21, float M22, float M23)
{
    /// <summary>3021E840 adds the supplied engine-cell translation without transposing the matrix.</summary>
    public ZoneCellObjectTransform Translate(Vector3 translation) => this with
    { M03 = M03 + translation.X, M13 = translation.Y + M13, M23 = translation.Z + M23 };

    internal static ZoneCellObjectTransform Read(ReadOnlySpan<byte> bytes, int offset) => new(
        ZoneCellObjectValues.Float(bytes, offset), ZoneCellObjectValues.Float(bytes, offset + 4),
        ZoneCellObjectValues.Float(bytes, offset + 8), ZoneCellObjectValues.Float(bytes, offset + 12),
        ZoneCellObjectValues.Float(bytes, offset + 16), ZoneCellObjectValues.Float(bytes, offset + 20),
        ZoneCellObjectValues.Float(bytes, offset + 24), ZoneCellObjectValues.Float(bytes, offset + 28),
        ZoneCellObjectValues.Float(bytes, offset + 32), ZoneCellObjectValues.Float(bytes, offset + 36),
        ZoneCellObjectValues.Float(bytes, offset + 40), ZoneCellObjectValues.Float(bytes, offset + 44));
}

/// <summary>
/// One fully bounded native record. RawPayload excludes the four-byte type tag and retains every
/// authored bit, including unnamed fields. Decoding does not apply quality gates, create geometry,
/// project water vertices, register objects or establish collision readiness.
/// </summary>
public abstract class ZoneCellObjectRecord
{
    private readonly byte[] _payload;
    public ZoneCellObjectType Type { get; }
    public int SourceOrder { get; }
    public int SourceOffset { get; }
    public int? OctreeNodeIndex { get; }
    public int SerializedLength => checked(4 + _payload.Length);
    public ReadOnlySpan<byte> RawPayload => _payload;
    public Vector3 BoundsMin => ZoneCellObjectValues.Vector(_payload, 0);
    public Vector3 BoundsMax => ZoneCellObjectValues.Vector(_payload, 12);
    public uint Word18 => ZoneCellObjectValues.UInt(_payload, 0x18);
    public uint Flags1C => ZoneCellObjectValues.UInt(_payload, 0x1c);
    public uint Flags20 => ZoneCellObjectValues.UInt(_payload, 0x20);
    public byte Byte24 => _payload[0x24];
    public byte Byte25 => _payload[0x25];
    public byte Byte26 => _payload[0x26];

    private protected ZoneCellObjectRecord(ZoneCellObjectType type, int order, int offset, int? node, byte[] payload)
    { Type = type; SourceOrder = order; SourceOffset = offset; OctreeNodeIndex = node; _payload = payload; }
}

/// <summary>30225050/3021E840: exactly 128 bytes, including both signed table indices.</summary>
public sealed class ZoneCellBrushObject : ZoneCellObjectRecord
{
    public int ModelIndex => ZoneCellObjectValues.Int(RawPayload, 0x7b);
    public int MaterialIndex => ZoneCellObjectValues.Int(RawPayload, 0x73);
    public uint Word77 => ZoneCellObjectValues.UInt(RawPayload, 0x77);
    public byte Byte7F => RawPayload[0x7f];
    public ZoneCellObjectTransform AuthoredTransform => ZoneCellObjectTransform.Read(RawPayload, 0x43);
    internal ZoneCellBrushObject(int order, int offset, int? node, byte[] payload)
        : base(ZoneCellObjectType.Brush, order, offset, node, payload) { }
}

/// <summary>302221D0: exactly 64 bytes. The signed group index uses a separate world vegetation table.</summary>
public sealed class ZoneCellVegetationObject : ZoneCellObjectRecord
{
    public Vector3 Position => ZoneCellObjectValues.Vector(RawPayload, 0x27);
    public float AuthoredScale => ZoneCellObjectValues.Float(RawPayload, 0x33);
    public short VegetationGroupIndex => BinaryPrimitives.ReadInt16LittleEndian(RawPayload[0x37..]);
    internal ZoneCellVegetationObject(int order, int offset, int? node, byte[] payload)
        : base(ZoneCellObjectType.Vegetation, order, offset, node, payload) { }
}

/// <summary>30222F10: exactly 111 bytes; the material index is the low 24 bits of the final word.</summary>
public sealed class ZoneCellDecalObject : ZoneCellObjectRecord
{
    public byte ProjectionType => RawPayload[0x27];
    public Vector3 Position => ZoneCellObjectValues.Vector(RawPayload, 0x2b);
    public Vector3 Direction => ZoneCellObjectValues.Vector(RawPayload, 0x37);
    public Vector3 OrientationRow0 => ZoneCellObjectValues.Vector(RawPayload, 0x43);
    public Vector3 OrientationRow1 => ZoneCellObjectValues.Vector(RawPayload, 0x4f);
    public Vector3 OrientationRow2 => ZoneCellObjectValues.Vector(RawPayload, 0x5b);
    public float Float67 => ZoneCellObjectValues.Float(RawPayload, 0x67);
    public uint PackedMaterial => ZoneCellObjectValues.UInt(RawPayload, 0x6b);
    public int MaterialIndex => (int)(PackedMaterial & 0xffffff);
    public byte MaterialHighByte => (byte)(PackedMaterial >> 24);
    internal ZoneCellDecalObject(int order, int offset, int? node, byte[] payload)
        : base(ZoneCellObjectType.Decal, order, offset, node, payload) { }
}

/// <summary>30222AA0: 63-byte header followed by count(+27) Vec3 records.</summary>
public sealed class ZoneCellRoadObject : ZoneCellObjectRecord
{
    public IReadOnlyList<Vector3> Vertices { get; }
    public uint PackedMaterial => ZoneCellObjectValues.UInt(RawPayload, 0x3b);
    public int MaterialIndex => (int)(PackedMaterial & 0xffffff);
    public byte MaterialHighByte => (byte)(PackedMaterial >> 24);
    public Vector4 Parameters => ZoneCellObjectValues.Vector4(RawPayload, 0x2b);
    internal ZoneCellRoadObject(int order, int offset, int? node, byte[] payload, int count)
        : base(ZoneCellObjectType.Road, order, offset, node, payload)
    { Vertices = ZoneCellObjectValues.Vectors(payload, 0x3f, count); }
}

/// <summary>
/// 30223480: 119-byte header followed by two distinct Vec3 arrays, counted at+67 and+73.
/// These are authored vertices; native water projection, deduplication and physicalization are separate.
/// </summary>
public sealed class ZoneCellWaterVolumeObject : ZoneCellObjectRecord
{
    public byte VolumeType => RawPayload[0x27];
    public ulong VolumeId => BinaryPrimitives.ReadUInt64LittleEndian(RawPayload[0x2b..]);
    public int MaterialIndex => ZoneCellObjectValues.Int(RawPayload, 0x33);
    public Vector4 AuthoredPlane => ZoneCellObjectValues.Vector4(RawPayload, 0x47);
    public IReadOnlyList<Vector3> Vertices { get; }
    public IReadOnlyList<Vector3> PhysicsVertices { get; }
    internal ZoneCellWaterVolumeObject(int order, int offset, int? node, byte[] payload, int count, int physicsCount)
        : base(ZoneCellObjectType.WaterVolume, order, offset, node, payload)
    {
        Vertices = ZoneCellObjectValues.Vectors(payload, 0x77, count);
        PhysicsVertices = ZoneCellObjectValues.Vectors(payload, checked(0x77 + count * 12), physicsCount);
    }
}

/// <summary>One version-2 octree header and its complete subtree, in serialized preorder.</summary>
public sealed class ZoneCellObjectOctreeNode
{
    private readonly byte[] _headerData;
    public int SourceIndex { get; }
    public int SourceOffset { get; }
    public int? ParentIndex { get; }
    public int? ChildOctant { get; }
    public uint Version => 2;
    /// <summary>The 24 header bytes at+4; native30229200 consumes but does not use them.</summary>
    public ReadOnlySpan<byte> HeaderData => _headerData;
    public int ObjectBlockLength { get; }
    public byte ChildMask { get; }
    public int SerializedLength { get; }
    public IReadOnlyList<int> ObjectIndices { get; }
    public IReadOnlyList<int> ChildIndices { get; }
    internal ZoneCellObjectOctreeNode(int index, int offset, int? parent, int? octant, byte[] header,
        int blockLength, byte mask, int length, int[] objects, int[] children)
    {
        SourceIndex = index; SourceOffset = offset; ParentIndex = parent; ChildOctant = octant;
        _headerData = header; ObjectBlockLength = blockLength; ChildMask = mask; SerializedLength = length;
        ObjectIndices = Array.AsReadOnly(objects); ChildIndices = Array.AsReadOnly(children);
    }
}

/// <summary>
/// Shared resource tables and complete object.dat/big_object.dat decoding for the five native
/// record families present in the six configured Zone129 cells. Unknown types, malformed lengths,
/// invalid resource references and trailing data fail explicitly. No record is silently skipped.
/// Input streams remain open. The original bytes and source order survive native load-gate choices.
/// </summary>
public sealed class ZoneCellObjectContent
{
    // Managed admission bounds, not recovered native allocation limits. Target octrees have depth9.
    public const int MaximumFileBytes = 64 * 1024 * 1024;
    public const int MaximumOctreeDepth = 64;
    public ZoneCellObjectFileKind Kind { get; }
    public IReadOnlyList<ZoneCellObjectResource> ModelResources { get; }
    public IReadOnlyList<ZoneCellObjectResource> MaterialResources { get; }
    public IReadOnlyList<ZoneCellObjectRecord> Objects { get; }
    public IReadOnlyList<ZoneCellObjectOctreeNode> OctreeNodes { get; }
    public int ResourceTableBytes { get; }
    public int ConsumedBytes { get; }

    private ZoneCellObjectContent(ZoneCellObjectFileKind kind, ZoneCellObjectResource[] models,
        ZoneCellObjectResource[] materials, ZoneCellObjectRecord[] objects,
        ZoneCellObjectOctreeNode[] nodes, int tableBytes, int length)
    {
        Kind = kind; ModelResources = Array.AsReadOnly(models); MaterialResources = Array.AsReadOnly(materials);
        Objects = Array.AsReadOnly(objects); OctreeNodes = Array.AsReadOnly(nodes);
        ResourceTableBytes = tableBytes; ConsumedBytes = length;
    }

    public static ZoneCellObjectContent ReadOctree(Stream stream) => Read(stream, ZoneCellObjectFileKind.Octree);
    public static ZoneCellObjectContent ReadBigObjects(Stream stream) => Read(stream, ZoneCellObjectFileKind.BigObjects);
    public static ZoneCellObjectContent Read(Stream stream, ZoneCellObjectFileKind kind)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        var scratch = new byte[81920];
        int count;
        while ((count = stream.Read(scratch)) != 0)
        {
            if (buffer.Length + count > MaximumFileBytes) throw new InvalidDataException("Cell object file exceeds the managed allocation bound.");
            buffer.Write(scratch, 0, count);
        }
        return Read(buffer.ToArray(), kind);
    }

    public static ZoneCellObjectContent Read(ReadOnlySpan<byte> bytes, ZoneCellObjectFileKind kind)
    {
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("Cell object file exceeds the managed allocation bound.");
        if (kind is not (ZoneCellObjectFileKind.Octree or ZoneCellObjectFileKind.BigObjects))
            throw new ArgumentOutOfRangeException(nameof(kind));
        var reader = new Reader(bytes.ToArray());
        var models = reader.Table();
        var materials = reader.Table();
        var tableBytes = reader.Position;
        reader.ModelCount = models.Length; reader.MaterialCount = materials.Length;
        if (kind == ZoneCellObjectFileKind.Octree) reader.Node(null, null, 0);
        else reader.Block(reader.Length, null);
        if (reader.Position != reader.Length) throw reader.Error("Trailing cell object data");
        return new(kind, models, materials, reader.Objects.ToArray(), reader.Nodes.Select(node => node!).ToArray(), tableBytes, reader.Position);
    }

    /// <summary>Any negative native signed index means no reference; the exact index remains in its record.</summary>
    public ZoneCellObjectResource? GetModelResource(int index) => Resolve(ModelResources, index);
    public ZoneCellObjectResource? GetMaterialResource(int index) => Resolve(MaterialResources, index);
    private static ZoneCellObjectResource? Resolve(IReadOnlyList<ZoneCellObjectResource> resources, int index) =>
        index < 0 ? null : index < resources.Count ? resources[index] : throw new ArgumentOutOfRangeException(nameof(index));

    private sealed class Reader(byte[] data)
    {
        internal int Position;
        internal int Length => data.Length;
        internal int ModelCount, MaterialCount;
        internal readonly List<ZoneCellObjectRecord> Objects = [];
        internal readonly List<ZoneCellObjectOctreeNode?> Nodes = [];
        internal InvalidDataException Error(string message) => new($"{message} at cell object offset 0x{Position:X}.");
        private ReadOnlySpan<byte> Peek(int count, int end)
        {
            if (count < 0 || Position > end - count) throw Error("Truncated record");
            return data.AsSpan(Position, count);
        }
        private ReadOnlySpan<byte> Take(int count, int end)
        { var result = Peek(count, end); Position += count; return result; }
        private uint UInt(int end) => BinaryPrimitives.ReadUInt32LittleEndian(Take(4, end));

        internal ZoneCellObjectResource[] Table()
        {
            var count = UInt(Length);
            if (count > (Length - Position) / 260) throw Error("Invalid resource table length");
            var values = new ZoneCellObjectResource[(int)count];
            for (var i = 0; i < values.Length; i++)
            {
                var value = UInt(Length);
                var storage = Take(256, Length).ToArray();
                var terminator = Array.IndexOf(storage, (byte)0);
                if (terminator < 0) throw Error("Unterminated resource name");
                foreach (var character in storage.AsSpan(0, terminator))
                    if (character > 127) throw Error("Unsupported non-ASCII resource name; native encoding is not established");
                values[i] = new(i, value, Encoding.ASCII.GetString(storage, 0, terminator), storage);
            }
            return values;
        }

        internal int Node(int? parent, int? octant, int depth)
        {
            if (depth > MaximumOctreeDepth) throw Error("Octree exceeds the managed depth bound");
            var start = Position;
            if (UInt(Length) != 2) throw Error("Unsupported octree version");
            var header = Take(24, Length).ToArray();
            var size = UInt(Length);
            var mask = Take(1, Length)[0];
            if (size > Length - Position) throw Error("Octree object block exceeds file length");
            var index = Nodes.Count; Nodes.Add(null);
            var firstObject = Objects.Count;
            Block(Position + (int)size, index);
            var objects = Enumerable.Range(firstObject, Objects.Count - firstObject).ToArray();
            var children = new List<int>(8);
            for (var bit = 0; bit < 8; bit++)
                if ((mask & (1 << bit)) != 0) children.Add(Node(index, bit, depth + 1));
            Nodes[index] = new(index, start, parent, octant, header, (int)size, mask, Position - start, objects, children.ToArray());
            return index;
        }

        internal void Block(int end, int? node)
        {
            while (Position < end)
            {
                var start = Position;
                var type = (ZoneCellObjectType)UInt(end);
                int count = 0, physicsCount = 0;
                var length = type switch
                {
                    ZoneCellObjectType.Brush => 0x80,
                    ZoneCellObjectType.Vegetation => 0x40,
                    ZoneCellObjectType.Decal => 0x6f,
                    ZoneCellObjectType.Road => Variable(0x3f, 0x27, -1, end, out count, out physicsCount),
                    ZoneCellObjectType.WaterVolume => Variable(0x77, 0x67, 0x73, end, out count, out physicsCount),
                    _ => throw new InvalidDataException($"Unsupported cell object type {(uint)type} at offset 0x{start:X}.")
                };
                var payload = Take(length, end).ToArray();
                var order = Objects.Count;
                ZoneCellObjectRecord record = type switch
                {
                    ZoneCellObjectType.Brush => new ZoneCellBrushObject(order, start, node, payload),
                    ZoneCellObjectType.Vegetation => new ZoneCellVegetationObject(order, start, node, payload),
                    ZoneCellObjectType.Decal => new ZoneCellDecalObject(order, start, node, payload),
                    ZoneCellObjectType.Road => new ZoneCellRoadObject(order, start, node, payload, count),
                    ZoneCellObjectType.WaterVolume => new ZoneCellWaterVolumeObject(order, start, node, payload, count, physicsCount),
                    _ => throw Error("Unsupported record")
                };
                switch (record)
                {
                    case ZoneCellBrushObject brush:
                        Reference(brush.ModelIndex, ModelCount, "model"); Reference(brush.MaterialIndex, MaterialCount, "material"); break;
                    case ZoneCellDecalObject decal: Reference(decal.MaterialIndex, MaterialCount, "material"); break;
                    case ZoneCellRoadObject road: Reference(road.MaterialIndex, MaterialCount, "material"); break;
                    case ZoneCellWaterVolumeObject water: Reference(water.MaterialIndex, MaterialCount, "material"); break;
                }
                Objects.Add(record);
            }
        }

        private int Variable(int headerSize, int countOffset, int physicsOffset, int end, out int count, out int physicsCount)
        {
            var header = Peek(headerSize, end);
            count = ZoneCellObjectValues.Int(header, countOffset);
            physicsCount = physicsOffset < 0 ? 0 : ZoneCellObjectValues.Int(header, physicsOffset);
            if (count < 0 || physicsCount < 0) throw Error("Negative variable-record vertex count");
            var length = headerSize + 12L * ((long)count + physicsCount);
            if (length > end - Position) throw Error("Variable-record vertices exceed their object block");
            return (int)length;
        }

        private void Reference(int index, int count, string kind)
        { if (index >= count) throw Error($"Invalid {kind} resource index {index}"); }
    }
}

internal static class ZoneCellObjectValues
{
    internal static uint UInt(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    internal static int Int(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
    internal static float Float(ReadOnlySpan<byte> bytes, int offset) => BitConverter.UInt32BitsToSingle(UInt(bytes, offset));
    internal static Vector3 Vector(ReadOnlySpan<byte> bytes, int offset) => new(Float(bytes, offset), Float(bytes, offset + 4), Float(bytes, offset + 8));
    internal static Vector4 Vector4(ReadOnlySpan<byte> bytes, int offset) => new(Float(bytes, offset), Float(bytes, offset + 4), Float(bytes, offset + 8), Float(bytes, offset + 12));
    internal static IReadOnlyList<Vector3> Vectors(ReadOnlySpan<byte> bytes, int offset, int count)
    {
        var values = new Vector3[count];
        for (var i = 0; i < values.Length; i++) values[i] = Vector(bytes, checked(offset + i * 12));
        return Array.AsReadOnly(values);
    }
}
