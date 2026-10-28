using System.Buffers.Binary;
using System.Numerics;

namespace AAEmu.Zone.Content;

public enum ZoneCgfPhysicsGeometryKind { Box = 0, TriangleMesh = 1, Sphere = 4, Cylinder = 5, Capsule = 6 }

/// <summary>
/// Exact curved physical primitive. Cylinder half-height is its axial half-length; capsule
/// half-height is the center-segment half-length, excluding its spherical caps. A sphere has
/// no axis/segment and publishes zero for those unused fields. No tessellation is supplied.
/// </summary>
public sealed record ZoneCgfPhysicsPrimitive(ZoneCgfPhysicsGeometryKind Kind, Vector3 Center,
    Vector3 Axis, float Radius, float HalfHeight);

// Exact source buffers selected by Mesh0800's stream references. Only serialized foreign
// triangles select faces from them; this context is never substituted for a missing proxy.
internal sealed record ZoneCgfPhysicsSource(Vector3[] Vertices, int[] TriangleIndices, int?[] TriangleMaterials);

/// <summary>
/// Authored physical geometry from a version-1 CryPhysics serialization, not a render mesh or a
/// model's bounding box. Material slots are CGF material ordinals, before scene surface resolution.
/// Inertial properties in the legacy phys_geometry wrapper are not placement transforms.
/// </summary>
public sealed class ZoneCgfPhysicsGeometry
{
    public const int MaximumVertexCount = 1_000_000;
    public const int MaximumTriangleCount = 1_000_000;
    public ZoneCgfPhysicsGeometryKind Kind { get; }
    public IReadOnlyList<Vector3> Vertices { get; }
    public IReadOnlyList<int> TriangleIndices { get; }
    public IReadOnlyList<int> MaterialSlots { get; }
    public int DefaultMaterialSlot { get; }
    public int SerializationFlags { get; }
    public ZoneCgfPhysicsPrimitive? Primitive { get; }
    public bool UsesReferencedSource { get; }
    /// <summary>Serialized native acceleration data is retained by the source chunk, not used for managed queries.</summary>
    public int UnreadAccelerationBytes { get; }

    private ZoneCgfPhysicsGeometry(ZoneCgfPhysicsGeometryKind kind, Vector3[] vertices, int[] indices,
        int[] materials, int defaultMaterial, int flags, int accelerationBytes,
        ZoneCgfPhysicsPrimitive? primitive = null, bool referencedSource = false)
    {
        Kind = kind;
        Vertices = Array.AsReadOnly(vertices);
        TriangleIndices = Array.AsReadOnly(indices);
        MaterialSlots = Array.AsReadOnly(materials);
        DefaultMaterialSlot = defaultMaterial;
        SerializationFlags = flags;
        UnreadAccelerationBytes = accelerationBytes;
        Primitive = primitive;
        UsesReferencedSource = referencedSource;
    }

    /// <summary>
    /// CGeomManager3514B340/3514B070 and CTriMesh351D83B0. Referenced meshes require the exact
    /// CGF source context; missing buffers, unsupported profiles and unknown types are refused.
    /// Native BVHs are not geometry substitutes: triangles feed a new managed spatial tree and
    /// curved shapes retain their analytic descriptors without tessellation.
    /// </summary>
    public static ZoneCgfPhysicsGeometry Read(ReadOnlySpan<byte> bytes) => Read(bytes, null);

    internal static ZoneCgfPhysicsGeometry Read(ReadOnlySpan<byte> bytes,
        Func<bool, ZoneCgfPhysicsSource>? sourceResolver)
    {
        var reader = new Reader(bytes);
        if (reader.Int32() != 1) throw new NotSupportedException("Only version-1 serialized CGF physical geometry is supported.");
        reader.Skip(52); // Reserved word, inertia, inertial quaternion/origin, volume and old reference count.
        var defaultMaterial = reader.Int32(); // blob +56; not a physical-surface ID.
        reader.Skip(8); // Legacy pointer/reserved storage is never interpreted.
        var type = reader.Int32(); // blob +68.
        return type switch
        {
            0 => ReadBox(ref reader, defaultMaterial),
            1 => ReadTriangleMesh(ref reader, defaultMaterial, sourceResolver),
            4 or 5 or 6 => ReadPrimitive(ref reader, (ZoneCgfPhysicsGeometryKind)type, defaultMaterial),
            _ => throw new NotSupportedException($"Serialized CGF physical geometry type {type} is not implemented.")
        };
    }

    private static ZoneCgfPhysicsGeometry ReadBox(ref Reader reader, int material)
    {
        // CBoxGeom's 64-byte box: Basis (world-to-box), bOriented, center, half sizes.
        var basis = new Matrix4x4(reader.Single(), reader.Single(), reader.Single(), 0,
            reader.Single(), reader.Single(), reader.Single(), 0,
            reader.Single(), reader.Single(), reader.Single(), 0, 0, 0, 0, 1);
        var oriented = reader.Int32();
        if (oriented is not (0 or 1)) throw new InvalidDataException("CGF physical box has an invalid orientation flag.");
        var center = reader.Vector3();
        var half = reader.Vector3();
        if (half.X <= 0 || half.Y <= 0 || half.Z <= 0)
            throw new NotSupportedException("Degenerate serialized CGF physical boxes require a separate geometry policy.");
        // Save/Load includes the single-box acceleration tree (64-byte box plus one word).
        if (reader.Remaining < 68) throw new InvalidDataException("CGF physical box acceleration data is truncated.");
        if (oriented != 0)
        {
            var x = new Vector3(basis.M11, basis.M12, basis.M13);
            var y = new Vector3(basis.M21, basis.M22, basis.M23);
            var z = new Vector3(basis.M31, basis.M32, basis.M33);
            if (Math.Abs(x.LengthSquared() - 1) > .001f || Math.Abs(y.LengthSquared() - 1) > .001f ||
                Math.Abs(z.LengthSquared() - 1) > .001f || Math.Abs(Vector3.Dot(x, y)) > .001f ||
                Math.Abs(Vector3.Dot(x, z)) > .001f || Math.Abs(Vector3.Dot(y, z)) > .001f)
                throw new NotSupportedException("A serialized CGF physical box has a non-orthonormal basis.");
        }
        var vertices = new Vector3[8];
        for (var index = 0; index < vertices.Length; index++)
        {
            var local = new Vector3((index & 1) == 0 ? -half.X : half.X,
                (index & 2) == 0 ? -half.Y : half.Y, (index & 4) == 0 ? -half.Z : half.Z);
            // Row-vector multiplication by the stored basis is native Basis.Transpose()*local.
            vertices[index] = center + (oriented != 0 ? Vector3.TransformNormal(local, basis) : local);
            if (!float.IsFinite(vertices[index].X) || !float.IsFinite(vertices[index].Y) || !float.IsFinite(vertices[index].Z))
                throw new InvalidDataException("Serialized CGF physical box corners exceed finite coordinates.");
        }
        int[] indices = [0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6,
            0, 1, 4, 1, 5, 4, 2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5];
        var materials = new int[12];
        Array.Fill(materials, material);
        return new(ZoneCgfPhysicsGeometryKind.Box, vertices, indices, materials, material, 0, reader.Remaining);
    }

    private static ZoneCgfPhysicsGeometry ReadPrimitive(ref Reader reader, ZoneCgfPhysicsGeometryKind kind, int material)
    {
        // CSphereGeom351AFC60 reads center/radius. CCylinderGeom35144D40 (also inherited by
        // CCapsuleGeom) reads center/axis/radius/half-height and one rendering tessellation word.
        var center = reader.Vector3();
        var axis = kind == ZoneCgfPhysicsGeometryKind.Sphere ? Vector3.Zero : reader.Vector3();
        var radius = reader.Single();
        var halfHeight = kind == ZoneCgfPhysicsGeometryKind.Sphere ? 0 : reader.Single();
        if (radius <= 0 || halfHeight < 0)
            throw new InvalidDataException("CGF curved physical primitive requires a positive radius and non-negative half-height.");
        if (kind != ZoneCgfPhysicsGeometryKind.Sphere)
        {
            if (Math.Abs(axis.LengthSquared() - 1f) > .002f)
                throw new NotSupportedException("CGF curved physical primitive has a non-unit authored axis.");
            reader.Skip(4); // Retained tessellation count, not part of the analytic collision shape.
        }
        reader.Require(68); // Native single-box acceleration tree; managed queries build their own.
        var primitive = new ZoneCgfPhysicsPrimitive(kind, center, axis, radius, halfHeight);
        return new(kind, [], [], [], material, 0, reader.Remaining, primitive);
    }

    private static ZoneCgfPhysicsGeometry ReadTriangleMesh(ref Reader reader, int material,
        Func<bool, ZoneCgfPhysicsSource>? sourceResolver)
    {
        var vertexCount = reader.Count(MaximumVertexCount, "vertices");
        var triangleCount = reader.Count(MaximumTriangleCount, "triangles");
        var maximumValency = reader.Int32();
        var flags = reader.Int32();
        if (maximumValency < 0) throw new InvalidDataException("CGF physical mesh has a negative vertex valency.");
        ushort[]? vertexMap = null;
        if (reader.Boolean())
        {
            reader.Require(checked(vertexCount * 2));
            vertexMap = new ushort[vertexCount];
            for (var index = 0; index < vertexCount; index++)
            {
                vertexMap[index] = reader.UInt16();
                if (vertexMap[index] >= vertexCount) throw new InvalidDataException("CGF physical vertex map refers outside its vertex array.");
            }
        }
        var foreignTriangleMap = reader.Boolean();
        ushort[]? foreign = null;
        if (foreignTriangleMap)
        {
            reader.Require(checked(triangleCount * 2));
            foreign = new ushort[triangleCount];
            for (var index = 0; index < foreign.Length; index++) foreign[index] = reader.UInt16();
        }
        var referencedTriangles = foreignTriangleMap && (flags & 0x400000) == 0;
        var referencedVertices = referencedTriangles || (flags & 1) != 0;
        ZoneCgfPhysicsSource? source = null;
        if (referencedVertices)
            source = sourceResolver?.Invoke(referencedTriangles) ?? throw new NotSupportedException(
                "CGF physical mesh requires its exact referenced source buffers; no geometry fallback is allowed.");
        var vertices = new Vector3[vertexCount];
        if (source is not null)
        {
            if (vertexCount > source.Vertices.Length)
                throw new InvalidDataException("CGF physical vertex count exceeds its referenced source vertex stream.");
            source.Vertices.AsSpan(0, vertexCount).CopyTo(vertices);
        }
        else
        {
            reader.Require(checked(vertexCount * 12));
            for (var index = 0; index < vertexCount; index++) vertices[index] = reader.Vector3();
        }
        var indices = new int[checked(triangleCount * 3)];
        var materials = new int[triangleCount];
        if (referencedTriangles)
        {
            // CTriMesh351D83B0's foreign branch has no inline vertices, indices, or material
            // presence byte. Each serialized foreign triangle selects exactly three source
            // indices and its source subset material, then reaches the common vertex remap.
            for (var triangle = 0; triangle < triangleCount; triangle++)
            {
                var selected = foreign![triangle];
                if (selected >= source!.TriangleMaterials.Length ||
                    (long)selected * 3 + 3 > source.TriangleIndices.Length)
                    throw new InvalidDataException("CGF serialized foreign triangle refers outside its source index stream.");
                materials[triangle] = source.TriangleMaterials[selected] ?? throw new NotSupportedException(
                    $"CGF referenced triangle {selected} has no authored subset material; native process-global scratch bytes are not reproducible content.");
                for (var corner = 0; corner < 3; corner++) indices[triangle * 3 + corner] = source.TriangleIndices[selected * 3 + corner];
            }
        }
        else
        {
            reader.Require(checked(triangleCount * 6 + 1));
            for (var index = 0; index < indices.Length; index++) indices[index] = reader.UInt16();
            var hasMaterials = reader.Boolean();
            if (hasMaterials)
                for (var index = 0; index < triangleCount; index++) materials[index] = unchecked((sbyte)reader.Byte());
            else Array.Fill(materials, material);
        }
        for (var index = 0; index < indices.Length; index++)
        {
            var value = indices[index];
            if ((uint)value >= (uint)vertexCount) throw new InvalidDataException("CGF physical triangle refers outside its vertex array.");
            indices[index] = vertexMap is null ? value : vertexMap[value];
        }

        // CTriMesh's unrelated retained bookkeeping and accelerator follow the geometry payload.
        // Validate their fixed header, then leave the native tree opaque: queries build their own.
        reader.Skip(16);
        var emptyMesh = reader.Int32();
        if (emptyMesh == 0)
        {
            reader.Skip(32);
            var treeType = reader.Int32();
            if (treeType is < 0 or > 2)
                throw new NotSupportedException($"CGF physical mesh acceleration type {treeType} is not recognized.");
        }
        return new(ZoneCgfPhysicsGeometryKind.TriangleMesh, vertices, indices, materials, material, flags, reader.Remaining,
            referencedSource: referencedVertices);
    }

    private ref struct Reader(ReadOnlySpan<byte> bytes)
    {
        private ReadOnlySpan<byte> _bytes = bytes;
        public int Remaining => _bytes.Length;
        public void Require(int count)
        {
            if (count < 0 || count > _bytes.Length) throw new InvalidDataException("Serialized CGF physical geometry is truncated.");
        }
        private ReadOnlySpan<byte> Take(int count)
        {
            Require(count);
            var result = _bytes[..count];
            _bytes = _bytes[count..];
            return result;
        }
        public void Skip(int count) => _ = Take(count);
        public byte Byte() => Take(1)[0];
        public bool Boolean()
        {
            var value = Byte();
            if (value > 1) throw new InvalidDataException("Serialized CGF physical geometry contains an invalid presence flag.");
            return value != 0;
        }
        public ushort UInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public float Single()
        {
            var value = BitConverter.Int32BitsToSingle(Int32());
            if (!float.IsFinite(value)) throw new InvalidDataException("Serialized CGF physical geometry contains a non-finite coordinate.");
            return value;
        }
        public Vector3 Vector3() => new(Single(), Single(), Single());
        public int Count(int maximum, string description)
        {
            var value = Int32();
            if (value < 0 || value > maximum) throw new InvalidDataException($"CGF physical mesh {description} exceed the managed allocation bound.");
            return value;
        }
    }
}
