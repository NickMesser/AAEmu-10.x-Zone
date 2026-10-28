using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace AAEmu.Zone.Content;

/// <summary>One bounded authored CGF chunk, including its local header where the format supplies one.</summary>
public sealed class ZoneCgfChunk
{
    private readonly byte[] _file;
    public uint Type { get; }
    public uint Version { get; }
    public int Id { get; }
    public int Offset { get; }
    public int Length { get; }
    public ReadOnlySpan<byte> Data => _file.AsSpan(Offset, Length);
    internal ZoneCgfChunk(byte[] file, uint type, uint version, int id, int offset, int length)
    { _file = file; Type = type; Version = version; Id = id; Offset = offset; Length = length; }
}

public sealed record ZoneCgfMaterial(int ChunkId, string Name, int PhysicalizeType,
    IReadOnlyList<int> SubMaterialChunkIds)
{
    /// <summary>CMatMan 301C4750: names containing '/' are paths; simple names are CGF siblings.</summary>
    public string ResolveAssetName(string cgfAssetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cgfAssetName);
        if (Name.Contains('/')) return Name;
        var separator = cgfAssetName.LastIndexOfAny(['/', '\\', ':']);
        return separator < 0 ? Name : cgfAssetName[..(separator + 1)] + Name;
    }
}

/// <summary>
/// Slot0 is native default geometry (type4096); slot1 is nocollide, slot2 obstruct and slot3 retains
/// type4099. Unsupported parts retain their exact reference and refusal rather than render bounds.
/// </summary>
public sealed record ZoneCgfPhysicsPart(int PhysicsSlot, int PhysicsChunkId,
    ZoneCgfPhysicsGeometry? Geometry, string? Diagnostic);

public sealed class ZoneCgfNode
{
    public int ChunkId { get; }
    public string Name { get; }
    public int ParentId { get; }
    public int ObjectChunkId { get; }
    public int MaterialChunkId { get; }
    /// <summary>301DB9A0's helper classification applied to an authored mesh object.</summary>
    public bool IsHelperMesh { get; }
    public bool IsOrdinaryMesh { get; }
    /// <summary>Raw node TM converted to System.Numerics row-vector meters; rotation/scale are retained.</summary>
    public Matrix4x4 LocalTransform { get; }
    /// <summary>Local * parent.Local * ancestor.Local, as native301D1B70 composes the authored hierarchy.</summary>
    public Matrix4x4 WorldTransform { get; internal set; }
    public IReadOnlyList<ZoneCgfPhysicsPart> PhysicsParts { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    /// <summary>Whether native per-node helper/main admission permits ordinary brush physics publication.</summary>
    public bool IsCollisionSource => CollisionExclusionReason is null;
    public string? CollisionExclusionReason { get; internal set; }
    /// <summary>Completeness of default blocking geometry only; nonblocking slots remain individually reported.</summary>
    public bool IsCollisionComplete => !IsCollisionSource || (Diagnostics.Count == 0 &&
        PhysicsParts.All(part => part.PhysicsSlot != 0 || part.Geometry is not null));

    internal ZoneCgfNode(int id, string name, int parent, int objectId, int materialId,
        Matrix4x4 local, List<ZoneCgfPhysicsPart> parts, List<string> diagnostics, bool isMesh)
    {
        ChunkId = id; Name = name; ParentId = parent; ObjectChunkId = objectId; MaterialChunkId = materialId;
        LocalTransform = WorldTransform = local;
        PhysicsParts = parts.AsReadOnly(); Diagnostics = diagnostics.AsReadOnly();
        IsHelperMesh = isMesh && (name.StartsWith('$') ||
            name.Contains("PhysicsProxy", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("$collision", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("$physics_proxy", StringComparison.OrdinalIgnoreCase));
        IsOrdinaryMesh = isMesh && !IsHelperMesh;
        // CStatObj::LoadCGF30030D90 publishes these helper meshes to renderer/LOD owners,
        // bypassing their own ordinary subobject/physical-geometry publication chain. Helper
        // search301DD1E0 is case-insensitive substring matching, not a prefix comparison.
        if (IsHelperMesh && name.Contains("$occlusion", StringComparison.OrdinalIgnoreCase))
            CollisionExclusionReason = "Authored occlusion helper geometry is renderer-only.";
        else if (IsHelperMesh && name.Contains("$lod", StringComparison.OrdinalIgnoreCase))
            CollisionExclusionReason = "Authored LOD helper geometry belongs to the visual LOD array.";
    }
}

/// <summary>
/// Managed CGF0x744/0x745 container, Node0823, Mesh0800, MtlName0800 and MeshPhysicsData0800 readers.
/// Physical parts come exclusively from serialized physical chunks. Render streams, AABBs and
/// display meshes are never substituted for missing collision. Unknown versions remain explicit.
/// </summary>
public sealed class ZoneCgfContent
{
    public const int MaximumFileBytes = 256 * 1024 * 1024;
    public const int MaximumChunkCount = 262144;
    public const int MaximumHierarchyDepth = 256;
    private const uint MeshType = 0xcccc0000, NodeType = 0xcccc000b, MaterialType = 0xcccc0014,
        PhysicsType = 0xcccc0018;
    public uint FileVersion { get; }
    public IReadOnlyList<ZoneCgfChunk> Chunks { get; }
    public IReadOnlyList<ZoneCgfNode> Nodes { get; }
    public IReadOnlyList<ZoneCgfMaterial> Materials { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    public bool IsCollisionComplete => Diagnostics.Count == 0 && Nodes.All(node => node.IsCollisionComplete);

    private ZoneCgfContent(byte[] file)
    {
        if (file.Length < 20 || !file.AsSpan(0, 8).SequenceEqual("CryTek\0\0"u8))
            throw new InvalidDataException("The CGF file does not contain the verified CryTek header.");
        if (UInt(file, 8) != 0xffff0000) throw new NotSupportedException("The CGF file type is not the verified static-geometry profile.");
        FileVersion = UInt(file, 12);
        if (FileVersion is not (0x744 or 0x745))
            throw new NotSupportedException($"CGF container version 0x{FileVersion:X} is not implemented.");
        var table = PositiveOffset(UInt(file, 16), file.Length - 4, "chunk table");
        if (table < 20) throw new InvalidDataException("The CGF chunk table overlaps the file header.");
        var countValue = UInt(file, table);
        if (countValue > MaximumChunkCount) throw new InvalidDataException("CGF chunk count exceeds the managed bound.");
        var count = (int)countValue;
        var stride = FileVersion == 0x744 ? 16 : 20;
        var tableEnd = (long)table + 4 + (long)count * stride;
        if (tableEnd > file.Length) throw new InvalidDataException("The CGF chunk table is truncated.");
        var entries = new (uint Type, uint Version, int Offset, int Id, int Length)[count];
        var identities = new HashSet<int>();
        for (var index = 0; index < count; index++)
        {
            var at = table + 4 + index * stride;
            var offset = PositiveOffset(UInt(file, at + 8), file.Length, "chunk");
            var id = Int(file, at + 12);
            if (!identities.Add(id)) throw new InvalidDataException($"CGF contains duplicate chunk ID {id}.");
            if (offset < 20 || (offset >= table && offset < tableEnd))
                throw new InvalidDataException("A CGF chunk overlaps the file header or chunk table.");
            var length = stride == 20 ? PositiveOffset(UInt(file, at + 16), file.Length, "chunk length") : 0;
            entries[index] = (UInt(file, at), UInt(file, at + 4), offset, id, length);
        }
        var ordered = Enumerable.Range(0, count).OrderBy(index => entries[index].Offset).ToArray();
        for (var ordinal = 0; ordinal < ordered.Length; ordinal++)
        {
            var index = ordered[ordinal];
            var entry = entries[index];
            var next = ordinal + 1 < ordered.Length ? entries[ordered[ordinal + 1]].Offset : file.Length;
            if (entry.Offset < table) next = Math.Min(next, table);
            if (stride == 16) entry.Length = next - entry.Offset;
            if (entry.Length < 0 || (long)entry.Offset + entry.Length > next ||
                (ordinal + 1 < ordered.Length && next == entry.Offset))
                throw new InvalidDataException("CGF chunk ranges overlap or exceed their file bounds.");
            entries[index] = entry;
        }
        var chunks = entries.Select(entry => new ZoneCgfChunk(file, entry.Type, entry.Version,
            entry.Id, entry.Offset, entry.Length)).ToArray();
        Chunks = Array.AsReadOnly(chunks);
        var byId = chunks.ToDictionary(chunk => chunk.Id);
        var diagnostics = new List<string>();
        var materials = new List<ZoneCgfMaterial>();
        foreach (var chunk in chunks.Where(chunk => chunk.Type == MaterialType))
        {
            if (chunk.Version != 0x800)
            { diagnostics.Add($"Material chunk {chunk.Id} version 0x{chunk.Version:X} is not implemented."); continue; }
            ValidateHeader(chunk, 424);
            var data = chunk.Data;
            var subCount = Int(data, 156);
            if ((uint)subCount > 32) throw new InvalidDataException("CGF material exceeds its 32 authored submaterial references.");
            var subs = new int[subCount];
            for (var index = 0; index < subCount; index++) subs[index] = Int(data, 160 + index * 4);
            materials.Add(new(chunk.Id, Text(data.Slice(24, 128)), Int(data, 152), Array.AsReadOnly(subs)));
        }
        Materials = materials.AsReadOnly();

        var geometry = new Dictionary<int, (ZoneCgfPhysicsGeometry? Value, string? Diagnostic)>();
        var nodes = new List<ZoneCgfNode>();
        foreach (var chunk in chunks.Where(chunk => chunk.Type == NodeType))
        {
            if (chunk.Version != 0x823)
            { diagnostics.Add($"Node chunk {chunk.Id} version 0x{chunk.Version:X} is not implemented."); continue; }
            ValidateHeader(chunk, 220);
            var data = chunk.Data;
            var propertyLength = Int(data, 216);
            if (propertyLength < 0 || propertyLength > data.Length - 220)
                throw new InvalidDataException("CGF node property string exceeds its chunk.");
            var parts = new List<ZoneCgfPhysicsPart>();
            var nodeNotes = new List<string>();
            var objectId = Int(data, 80);
            var isMesh = false;
            if (objectId > 0 && byId.TryGetValue(objectId, out var mesh))
            {
                isMesh = mesh.Type == MeshType;
                if (mesh.Type == MeshType && mesh.Version == 0x800)
                {
                    ValidateHeader(mesh, 276);
                    for (var slot = 0; slot < 4; slot++)
                    {
                        var physicsId = Int(mesh.Data, 108 + slot * 4);
                        if (physicsId <= 0) continue; // Native301D3A30 loads only positive references.
                        if (!geometry.TryGetValue(physicsId, out var parsed))
                        {
                            try
                            {
                                if (!byId.TryGetValue(physicsId, out var physics))
                                    throw new InvalidDataException($"Physical chunk reference {physicsId} is absent.");
                                if (physics.Type != PhysicsType || physics.Version != 0x800)
                                    throw new NotSupportedException($"Physical chunk {physicsId} has unsupported type/version 0x{physics.Type:X}/0x{physics.Version:X}.");
                                ValidateHeader(physics, 40);
                                var size = Int(physics.Data, 16);
                                var tetraSize = Int(physics.Data, 24);
                                if (size < 0 || tetraSize < 0 || (long)40 + size + tetraSize > physics.Length)
                                    throw new InvalidDataException("CGF physical data size exceeds its chunk.");
                                if (tetraSize != 0)
                                    throw new NotSupportedException("CGF tetrahedral physical geometry is not implemented.");
                                parsed = (ZoneCgfPhysicsGeometry.Read(physics.Data.Slice(40, size)), null);
                            }
                            catch (Exception error) when (error is InvalidDataException or NotSupportedException or OverflowException)
                            { parsed = (null, error.Message); }
                            geometry.Add(physicsId, parsed);
                        }
                        parts.Add(new(slot, physicsId, parsed.Value, parsed.Diagnostic));
                    }
                }
                else if (mesh.Type == MeshType)
                    nodeNotes.Add($"Mesh chunk {objectId} version 0x{mesh.Version:X} is not implemented.");
                else if (mesh.Type != 0xcccc0001 && objectId != 0)
                    nodeNotes.Add($"Node object chunk {objectId} type 0x{mesh.Type:X} is not implemented.");
                // Authored Helper chunks contain no mesh and supply no blocking geometry.
            }
            else if (objectId > 0)
                nodeNotes.Add($"Node object chunk {objectId} is absent.");
            var raw = data[100..164];
            var matrix = new Matrix4x4(Float(raw, 0), Float(raw, 4), Float(raw, 8), 0,
                Float(raw, 16), Float(raw, 20), Float(raw, 24), 0,
                Float(raw, 32), Float(raw, 36), Float(raw, 40), 0,
                Float(raw, 48) * .01f, Float(raw, 52) * .01f, Float(raw, 56) * .01f, 1);
            nodes.Add(new(chunk.Id, Text(data.Slice(16, 64)), Int(data, 84), objectId,
                Int(data, 92), matrix, parts, nodeNotes, isMesh));
        }
        // LoadCGF30030D90 sets subobject flag2 on non-main ordinary meshes when any ordinary
        // mesh has the literal case-insensitive name "main". Physicalize300442F0 skips those
        // ordinary meshes, while helper meshes bypass this flag test. This is per node, not
        // inherited by descendants; decoded geometry and ancestor transforms remain retained.
        if (nodes.Any(node => node.IsOrdinaryMesh && node.Name.Equals("main", StringComparison.OrdinalIgnoreCase)))
            foreach (var node in nodes.Where(node => node.IsOrdinaryMesh &&
                !node.Name.Equals("main", StringComparison.OrdinalIgnoreCase)))
                node.CollisionExclusionReason = "An ordinary mesh named main suppresses this non-main ordinary mesh (native subobject flag2).";
        var nodeById = nodes.ToDictionary(node => node.ChunkId);
        foreach (var node in nodes)
        {
            var visited = new HashSet<int> { node.ChunkId };
            var parentId = node.ParentId;
            var transform = node.LocalTransform;
            while (parentId != -1)
            {
                if (visited.Count >= MaximumHierarchyDepth || !visited.Add(parentId))
                    throw new InvalidDataException("CGF node hierarchy is cyclic or exceeds the managed depth bound.");
                if (!nodeById.TryGetValue(parentId, out var parent))
                    throw new InvalidDataException($"CGF parent node {parentId} is absent or unsupported.");
                transform *= parent.LocalTransform;
                parentId = parent.ParentId;
            }
            if (!Finite(transform)) throw new InvalidDataException("CGF composed node transform is non-finite.");
            node.WorldTransform = transform;
        }
        Nodes = nodes.AsReadOnly();
        Diagnostics = diagnostics.AsReadOnly();
    }

    public static ZoneCgfContent Read(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var copy = new MemoryStream();
        var scratch = new byte[81920];
        int count;
        while ((count = input.Read(scratch)) != 0)
        {
            if (copy.Length + count > MaximumFileBytes) throw new InvalidDataException("CGF exceeds the managed file-size bound.");
            copy.Write(scratch, 0, count);
        }
        return new(copy.ToArray());
    }

    public static ZoneCgfContent Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("CGF exceeds the managed file-size bound.");
        return new(bytes.ToArray());
    }

    private static void ValidateHeader(ZoneCgfChunk chunk, int minimum)
    {
        var data = chunk.Data;
        if (data.Length < minimum || UInt(data, 0) != chunk.Type || UInt(data, 4) != chunk.Version ||
            Int(data, 8) != chunk.Offset || Int(data, 12) != chunk.Id)
            throw new InvalidDataException($"CGF chunk {chunk.Id} has a truncated or inconsistent local header.");
    }

    private static int PositiveOffset(uint value, int maximum, string description) => value <= maximum
        ? (int)value : throw new InvalidDataException($"CGF {description} exceeds its file bounds.");
    private static uint UInt(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(at, 4));
    private static int Int(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(at, 4));
    private static float Float(ReadOnlySpan<byte> bytes, int at)
    {
        var value = BitConverter.Int32BitsToSingle(Int(bytes, at));
        if (!float.IsFinite(value)) throw new InvalidDataException("CGF node transform contains a non-finite coordinate.");
        return value;
    }
    private static string Text(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        if (end < 0) throw new InvalidDataException("CGF fixed-width name is unterminated.");
        if (bytes[..end].ContainsAnyExceptInRange((byte)0, (byte)127))
            throw new NotSupportedException("CGF non-ASCII fixed-width names require a verified native encoding.");
        return Encoding.ASCII.GetString(bytes[..end]);
    }
    private static bool Finite(Matrix4x4 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) &&
        float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) &&
        float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) &&
        float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43);
}
