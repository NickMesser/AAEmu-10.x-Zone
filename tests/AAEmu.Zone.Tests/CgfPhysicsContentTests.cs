using System.Buffers.Binary;
using System.Numerics;
using System.Text;

using AAEmu.Zone.Content;

namespace AAEmu.Zone.Tests;

public sealed class CgfPhysicsContentTests
{
    [Test]
    public async Task AuthoredBoxUsesItsPrimitiveCenterAndHalfSizesWithoutApplyingInertialOrigin()
    {
        var blob = CgfTestAssets.BoxBlob(new(1, 2, 3), new(4, 5, 6), 3);
        CgfTestAssets.Vector(blob, 36, new(100, 200, 300)); // Wrapper mass-center, never a placement.
        var geometry = ZoneCgfPhysicsGeometry.Read(blob);
        await Assert.That(geometry.Kind).IsEqualTo(ZoneCgfPhysicsGeometryKind.Box);
        await Assert.That(geometry.Vertices.Count).IsEqualTo(8);
        await Assert.That(geometry.TriangleIndices.Count).IsEqualTo(36);
        await Assert.That(geometry.Vertices.Aggregate(Vector3.Min)).IsEqualTo(new Vector3(-3, -3, -3));
        await Assert.That(geometry.Vertices.Aggregate(Vector3.Max)).IsEqualTo(new Vector3(5, 7, 9));
        await Assert.That(geometry.MaterialSlots.All(slot => slot == 3)).IsTrue();
    }

    [Test]
    public async Task OrientedBoxCornersUseTheVerifiedNativeTransposeConvention()
    {
        var blob = CgfTestAssets.BoxBlob(new(10, 20, 30), new(1, 2, 3), 0);
        // Native3513AC00 emits (x*b00+y*b10+z*b20, x*b01+y*b11+z*b21, ...).
        var basis = new float[] { 0, -1, 0, 1, 0, 0, 0, 0, 1 };
        for (var index = 0; index < basis.Length; index++)
            BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(72 + index * 4), BitConverter.SingleToInt32Bits(basis[index]));
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(72 + 36), 1);
        var geometry = ZoneCgfPhysicsGeometry.Read(blob);
        await Assert.That(geometry.Vertices[7]).IsEqualTo(new Vector3(12, 19, 33));
        await Assert.That(geometry.Vertices[0]).IsEqualTo(new Vector3(8, 21, 27));
    }

    [Test]
    public async Task InlinePhysicalTrianglesRetainRemappingAndSignedMaterialSentinels()
    {
        var geometry = ZoneCgfPhysicsGeometry.Read(CgfTestAssets.TriangleBlob(vertexMap: [2, 0, 1], material: 255));
        await Assert.That(geometry.Kind).IsEqualTo(ZoneCgfPhysicsGeometryKind.TriangleMesh);
        await Assert.That(geometry.Vertices.SequenceEqual(new Vector3[] { new(0, 0, 1), new(2, 0, 1), new(0, 3, 1) })).IsTrue();
        await Assert.That(geometry.TriangleIndices.SequenceEqual(new[] { 2, 0, 1 })).IsTrue();
        await Assert.That(geometry.MaterialSlots.Single()).IsEqualTo(-1);
        await Assert.That(geometry.DefaultMaterialSlot).IsEqualTo(7);
        await Assert.That(geometry.UnreadAccelerationBytes).IsEqualTo(68);
        var fallback = ZoneCgfPhysicsGeometry.Read(CgfTestAssets.TriangleBlob(material: null));
        await Assert.That(fallback.MaterialSlots.Single()).IsEqualTo(7);
    }

    [Test]
    public async Task ExternalVertexAndTriangleBuffersAreRefusedInsteadOfUsingRenderGeometry()
    {
        await Assert.That(() => ZoneCgfPhysicsGeometry.Read(CgfTestAssets.TriangleBlob(flags: 1)))
            .Throws<NotSupportedException>();
        await Assert.That(() => ZoneCgfPhysicsGeometry.Read(CgfTestAssets.TriangleBlob(foreignMap: true)))
            .Throws<NotSupportedException>();
        var inline = ZoneCgfPhysicsGeometry.Read(CgfTestAssets.TriangleBlob(flags: 0x400000, foreignMap: true));
        await Assert.That(inline.TriangleIndices.Count).IsEqualTo(3);
    }

    [Test]
    public async Task BothVerifiedChunkContainersResolvePhysicalPartsAndCentimeterNodeHierarchy()
    {
        foreach (var version in new uint[] { 0x744, 0x745 })
        {
            var content = ZoneCgfContent.Read(CgfTestAssets.BoxCgf(version, child: true));
            await Assert.That(content.FileVersion).IsEqualTo(version);
            await Assert.That(content.IsCollisionComplete).IsTrue();
            var child = content.Nodes.Single(node => node.ChunkId == 12);
            await Assert.That(Vector3.Transform(Vector3.Zero, child.LocalTransform)).IsEqualTo(new Vector3(1, 0, 0));
            await Assert.That(Vector3.Transform(Vector3.Zero, child.WorldTransform)).IsEqualTo(new Vector3(2, 3, 0));
            var part = child.PhysicsParts.Single();
            await Assert.That(part.PhysicsSlot).IsEqualTo(0);
            await Assert.That(part.PhysicsChunkId).IsEqualTo(11);
            await Assert.That(part.Geometry!.Vertices.Count).IsEqualTo(8);
            var material = content.Materials.Single();
            await Assert.That(material.ResolveAssetName("game/objects/wall.cgf")).IsEqualTo("game/objects/wall_material");
            await Assert.That((material with { Name = "game/materials/shared" }).ResolveAssetName("game/objects/wall.cgf"))
                .IsEqualTo("game/materials/shared");
        }
    }

    [Test]
    public async Task VisualHelperAdmissionUsesSubstringsAndEvaluatesDescendantsIndependently()
    {
        foreach (var name in new[] { "$occlusion", "$helper_$oCclusion", "$helper_$LoD1", "prefixPhysicsProxy_$occlusion" })
        {
            var content = ZoneCgfContent.Read(CgfTestAssets.BoxCgf(child: true, parentName: name, parentHasMesh: true));
            var parent = content.Nodes.Single(node => node.ChunkId == 13);
            await Assert.That(parent.PhysicsParts.Single().Geometry is not null).IsTrue(); // Data is retained.
            await Assert.That(parent.IsCollisionSource).IsFalse();
            await Assert.That(parent.IsHelperMesh).IsTrue();
            var child = content.Nodes.Single(node => node.ChunkId == 12);
            await Assert.That(child.CollisionExclusionReason!.Contains("Direct-root", StringComparison.Ordinal)).IsTrue();
            await Assert.That(content.DirectPhysicsNodeId).IsEqualTo((int?)(name.Contains("PhysicsProxy", StringComparison.OrdinalIgnoreCase) ? 13 : 12));
            await Assert.That(Vector3.Transform(Vector3.Zero, child.WorldTransform)).IsEqualTo(new Vector3(2, 3, 0));
            await Assert.That(content.IsCollisionComplete).IsTrue();
        }
        var proxy = ZoneCgfContent.Read(CgfTestAssets.BoxCgf(child: true, nodeName: "$physics_proxy", parentHasMesh: true, parentHasPhysics: false));
        await Assert.That(proxy.Nodes.Single(node => node.ChunkId == 12).IsCollisionSource).IsTrue();
        var ordinary = ZoneCgfContent.Read(CgfTestAssets.BoxCgf(nodeName: "ordinary$occlusion"));
        await Assert.That(ordinary.Nodes.Single().IsOrdinaryMesh).IsTrue();
        await Assert.That(ordinary.DirectPhysicsNodeId).IsEqualTo((int?)12);
    }

    [Test]
    public async Task LiteralMainMeshSuppressesOtherOrdinaryMeshesWhileHelpersKeepTheirOwnAdmission()
    {
        var content = ZoneCgfContent.Read(CgfTestAssets.BoxCgf(child: true, parentName: "mAiN", parentHasMesh: true, exportFlags: 0));
        var main = content.Nodes.Single(node => node.ChunkId == 13);
        var ordinary = content.Nodes.Single(node => node.ChunkId == 12);
        await Assert.That(main.IsOrdinaryMesh).IsTrue();
        await Assert.That(main.IsCollisionSource).IsTrue();
        await Assert.That(ordinary.PhysicsParts.Single().Geometry is not null).IsTrue();
        await Assert.That(ordinary.IsCollisionSource).IsFalse();
        await Assert.That(ordinary.CollisionExclusionReason!.Contains("flag2", StringComparison.Ordinal)).IsTrue();

        var helperChild = ZoneCgfContent.Read(CgfTestAssets.BoxCgf(child: true, parentName: "main",
            parentHasMesh: true, nodeName: "childPhysicsProxy"));
        await Assert.That(helperChild.Nodes.Single(node => node.ChunkId == 12).IsHelperMesh).IsTrue();
        await Assert.That(helperChild.Nodes.Single(node => node.ChunkId == 12).IsCollisionSource).IsTrue();

        var helperParent = ZoneCgfContent.Read(CgfTestAssets.BoxCgf(child: true, parentName: "$main", parentHasMesh: true));
        await Assert.That(helperParent.DirectPhysicsNodeId).IsEqualTo((int?)12);
        var groupParent = ZoneCgfContent.Read(CgfTestAssets.BoxCgf(child: true, parentName: "main", parentHasMesh: false));
        await Assert.That(groupParent.DirectPhysicsNodeId).IsEqualTo((int?)12);
    }

    [Test]
    public async Task UnsupportedPhysicalResourcesRemainPerPartCoverageFailures()
    {
        var blob = CgfTestAssets.BoxBlob(Vector3.Zero, Vector3.One, 0);
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(68), 2);
        var content = ZoneCgfContent.Read(CgfTestAssets.BoxCgf(blob: blob));
        var node = content.Nodes.Single();
        await Assert.That(content.IsCollisionComplete).IsFalse();
        await Assert.That(node.IsPhysicalDataComplete).IsFalse();
        await Assert.That(node.PhysicsParts.Single().Geometry is null).IsTrue();
        await Assert.That(node.PhysicsParts.Single().Diagnostic!.Contains("type 2", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task CurvedPrimitivesRetainExactDescriptorsWithoutPolygonOrBoundsSubstitutes()
    {
        foreach (var kind in new[] { ZoneCgfPhysicsGeometryKind.Sphere, ZoneCgfPhysicsGeometryKind.Cylinder, ZoneCgfPhysicsGeometryKind.Capsule })
        {
            var geometry = ZoneCgfPhysicsGeometry.Read(CgfTestAssets.PrimitiveBlob(kind, new(10, 20, 30), new(0, -1, 0), 2, 3));
            await Assert.That(geometry.Kind).IsEqualTo(kind);
            var primitive = geometry.Primitive!;
            await Assert.That(primitive.Center).IsEqualTo(new Vector3(10, 20, 30));
            await Assert.That(primitive.Radius).IsEqualTo(2f);
            await Assert.That(primitive.Axis).IsEqualTo(kind == ZoneCgfPhysicsGeometryKind.Sphere ? Vector3.Zero : new Vector3(0, -1, 0));
            await Assert.That(primitive.HalfHeight).IsEqualTo(kind == ZoneCgfPhysicsGeometryKind.Sphere ? 0f : 3f);
            await Assert.That(geometry.DefaultMaterialSlot).IsEqualTo(5);
            await Assert.That(geometry.Vertices.Count + geometry.TriangleIndices.Count + geometry.MaterialSlots.Count).IsEqualTo(0);
            await Assert.That(geometry.UnreadAccelerationBytes).IsEqualTo(68);
        }
        await Assert.That(() => ZoneCgfPhysicsGeometry.Read(CgfTestAssets.PrimitiveBlob(
            ZoneCgfPhysicsGeometryKind.Cylinder, Vector3.Zero, Vector3.Zero, 1, 1))).Throws<NotSupportedException>();
        await Assert.That(() => ZoneCgfPhysicsGeometry.Read(CgfTestAssets.PrimitiveBlob(
            ZoneCgfPhysicsGeometryKind.Sphere, Vector3.Zero, Vector3.Zero, -1, 0))).Throws<InvalidDataException>();
    }

    [Test]
    public async Task ReferencedPhysicalFacesUseOnlySerializedForeignTrianglesAndTheirSubsetMaterials()
    {
        var content = ZoneCgfContent.Read(CgfTestAssets.ReferencedCgf());
        var geometry = content.Nodes.Single().PhysicsParts.Single().Geometry!;
        await Assert.That(content.IsCollisionComplete).IsTrue();
        await Assert.That(geometry.UsesReferencedSource).IsTrue();
        await Assert.That(geometry.Vertices[3]).IsEqualTo(new Vector3(4, 0, 2)); // Already meters.
        await Assert.That(geometry.TriangleIndices.SequenceEqual(new[] { 0, 3, 2 })).IsTrue(); // Foreign face1, then vertex map.
        await Assert.That(geometry.TriangleIndices.Count).IsEqualTo(3); // Other source/render face is not collision.
        await Assert.That(geometry.MaterialSlots.Single()).IsEqualTo(-1); // Subset1's low signed byte255.
        await Assert.That(geometry.DefaultMaterialSlot).IsEqualTo(7);
    }

    [Test]
    public async Task ExternalVertexOnlyProfilesDoNotRequireUnusedSourceIndexOrSubsetStreams()
    {
        var content = ZoneCgfContent.Read(CgfTestAssets.ReferencedCgf(verticesOnly: true, includeIndices: false));
        var geometry = content.Nodes.Single().PhysicsParts.Single().Geometry!;
        await Assert.That(content.IsCollisionComplete).IsTrue();
        await Assert.That(geometry.UsesReferencedSource).IsTrue();
        await Assert.That(geometry.TriangleIndices.SequenceEqual(new[] { 2, 1, 0 })).IsTrue();
        await Assert.That(geometry.MaterialSlots.Single()).IsEqualTo(-3); // Inline material byte253.
    }

    [Test]
    public async Task MissingOrMalformedReferencedDataRemainsNamedPerPartCoverageFailure()
    {
        foreach (var bytes in new[]
        {
            CgfTestAssets.ReferencedCgf(foreignTriangle: 2),
            CgfTestAssets.ReferencedCgf(includeSelectedSubset: false),
            CgfTestAssets.ReferencedCgf(positionElementSize: 16),
            CgfTestAssets.ReferencedCgf(includeIndices: false)
        })
        {
            var content = ZoneCgfContent.Read(bytes);
            var part = content.Nodes.Single().PhysicsParts.Single();
            await Assert.That(content.IsCollisionComplete).IsFalse();
            await Assert.That(part.Geometry is null).IsTrue();
            await Assert.That(string.IsNullOrWhiteSpace(part.Diagnostic)).IsFalse();
        }
    }

    [Test]
    public async Task SharedPhysicalChunkKeepsEachReferencedMeshSourceContext()
    {
        var content = ZoneCgfContent.Read(CgfTestAssets.ReferencedCgf(secondMesh: true));
        var first = content.Nodes.Single(node => node.ChunkId == 12).PhysicsParts.Single().Geometry!;
        var second = content.Nodes.Single(node => node.ChunkId == 42).PhysicsParts.Single().Geometry!;
        await Assert.That(first.Vertices[0]).IsEqualTo(new Vector3(0, 0, 1));
        await Assert.That(second.Vertices[0]).IsEqualTo(new Vector3(100, 0, 1));
    }

    [Test]
    public async Task DirectRootPresenceUsesExportFlagsAndRawSelectedPhysicsBeforeVisualAdmission()
    {
        var direct = ZoneCgfContent.Read(CgfTestAssets.BoxCgf());
        await Assert.That(direct.ExportFlags).IsEqualTo(1u); // No chunk: native constructor default.
        await Assert.That(direct.IsCompound).IsFalse();
        await Assert.That(direct.HasDirectPhysicsGeometry).IsTrue();
        await Assert.That(direct.DirectPhysicsNodeId).IsEqualTo((int?)12);
        var compound = ZoneCgfContent.Read(CgfTestAssets.BoxCgf(child: true, parentHasMesh: true, exportFlags: 0));
        await Assert.That(compound.IsCompound).IsTrue();
        await Assert.That(compound.HasDirectPhysicsGeometry).IsFalse();
        var selectedHelper = ZoneCgfContent.Read(CgfTestAssets.BoxCgf(child: true, parentHasMesh: true,
            parentName: "PhysicsProxy$occlusion", exportFlags: 1));
        await Assert.That(selectedHelper.DirectPhysicsNodeId).IsEqualTo((int?)13);
        await Assert.That(selectedHelper.Nodes.Single(node => node.ChunkId == 13).IsCollisionSource).IsFalse();
        await Assert.That(selectedHelper.HasDirectPhysicsGeometry).IsTrue(); // Root registration precedes helper exclusion.
        var helperOnly = ZoneCgfContent.Read(CgfTestAssets.BoxCgf(nodeName: "$physics_proxy"));
        await Assert.That(helperOnly.HasOrdinaryMesh).IsFalse();
        await Assert.That(helperOnly.HasDirectPhysicsGeometry).IsFalse();
        await Assert.That(helperOnly.IsCollisionComplete).IsFalse();
        await Assert.That(helperOnly.Nodes.Single().PhysicsParts.Single().Geometry is not null).IsTrue();
    }

    [Test]
    public async Task AimpointHelpersSkipIndependentPublicationWithoutChangingRawRootSelection()
    {
        var content = ZoneCgfContent.Read(CgfTestAssets.BoxCgf(child: true, parentHasMesh: true,
            parentHasPhysics: false, nodeName: "$AiMpOiNt_PhysicsProxy"));
        var aimpoint = content.Nodes.Single(node => node.ChunkId == 12);
        await Assert.That(aimpoint.IsHelperMesh).IsTrue();
        await Assert.That(aimpoint.IsCollisionSource).IsFalse();
        await Assert.That(aimpoint.PhysicsParts.Single().Geometry is not null).IsTrue();
        await Assert.That(content.DirectPhysicsSource).IsEqualTo(aimpoint);
        await Assert.That(content.HasDirectPhysicsGeometry).IsTrue();
    }

    [Test]
    public async Task MalformedLengthsReferencesCoordinatesAndCyclesAreRejectedBeforeGeometryPublication()
    {
        var blob = CgfTestAssets.TriangleBlob();
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(72), int.MaxValue);
        await Assert.That(() => ZoneCgfPhysicsGeometry.Read(blob)).Throws<InvalidDataException>();
        var nonFinite = CgfTestAssets.BoxBlob(Vector3.Zero, Vector3.One, 0);
        BinaryPrimitives.WriteInt32LittleEndian(nonFinite.AsSpan(72 + 40), BitConverter.SingleToInt32Bits(float.NaN));
        await Assert.That(() => ZoneCgfPhysicsGeometry.Read(nonFinite)).Throws<InvalidDataException>();
        var cgf = CgfTestAssets.BoxCgf(child: true, cycle: true);
        await Assert.That(() => ZoneCgfContent.Read(cgf)).Throws<InvalidDataException>();
        var overlap = CgfTestAssets.BoxCgf();
        BinaryPrimitives.WriteInt32LittleEndian(overlap.AsSpan(24 + 8), 20);
        await Assert.That(() => ZoneCgfContent.Read(overlap)).Throws<InvalidDataException>();
        for (var length = 0; length < 20; length++)
        {
            var truncated = overlap[..length];
            await Assert.That(() => ZoneCgfContent.Read(truncated)).Throws<InvalidDataException>();
        }
    }
}

internal static class CgfTestAssets
{
    internal static byte[] BoxBlob(Vector3 center, Vector3 half, int material)
    {
        var bytes = Header(0, material);
        using var stream = new MemoryStream();
        stream.Write(bytes);
        using var writer = new BinaryWriter(stream);
        foreach (var value in new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }) writer.Write(value);
        writer.Write(0); // Unoriented authored box.
        foreach (var value in new[] { center.X, center.Y, center.Z, half.X, half.Y, half.Z }) writer.Write(value);
        writer.Write(new byte[68]); // Retained native single-box tree, replaced by a managed BVH.
        return stream.ToArray();
    }

    internal static byte[] TriangleBlob(int flags = 0x20, ushort[]? vertexMap = null,
        byte? material = 2, bool foreignMap = false)
    {
        using var stream = new MemoryStream();
        stream.Write(Header(1, 7));
        using var writer = new BinaryWriter(stream);
        writer.Write(3); writer.Write(1); writer.Write(2); writer.Write(flags);
        writer.Write(vertexMap is not null);
        if (vertexMap is not null) foreach (var value in vertexMap) writer.Write(value);
        writer.Write(foreignMap);
        if (foreignMap) writer.Write((ushort)0);
        foreach (var value in new float[] { 0, 0, 1, 2, 0, 1, 0, 3, 1 }) writer.Write(value);
        foreach (var index in new ushort[] { 0, 1, 2 }) writer.Write(index);
        writer.Write(material.HasValue);
        if (material is { } slot) writer.Write(slot);
        writer.Write(new byte[16]); writer.Write(0); writer.Write(new byte[32]); writer.Write(2);
        writer.Write(new byte[68]);
        return stream.ToArray();
    }

    internal static byte[] PrimitiveBlob(ZoneCgfPhysicsGeometryKind kind, Vector3 center, Vector3 axis, float radius, float halfHeight)
    {
        using var stream = new MemoryStream();
        stream.Write(Header((int)kind, 5));
        using var writer = new BinaryWriter(stream);
        foreach (var value in new[] { center.X, center.Y, center.Z }) writer.Write(value);
        if (kind != ZoneCgfPhysicsGeometryKind.Sphere)
            foreach (var value in new[] { axis.X, axis.Y, axis.Z }) writer.Write(value);
        writer.Write(radius);
        if (kind != ZoneCgfPhysicsGeometryKind.Sphere) { writer.Write(halfHeight); writer.Write(18); }
        writer.Write(new byte[68]);
        return stream.ToArray();
    }

    private static byte[] Header(int type, int material)
    {
        var bytes = new byte[72];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(56), material);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(68), type);
        return bytes;
    }

    internal static byte[] BoxCgf(uint version = 0x745, bool child = false, bool cycle = false, byte[]? blob = null,
        string nodeName = "authored_wall", string parentName = "authored_parent", bool parentHasMesh = false,
        uint? exportFlags = null, bool parentHasPhysics = true)
    {
        blob ??= BoxBlob(new(0, 0, 1), new(2, 3, 1), 0);
        var material = new byte[424];
        Encoding.ASCII.GetBytes("wall_material").CopyTo(material, 24);
        BinaryPrimitives.WriteInt32LittleEndian(material.AsSpan(152), 4096);
        var mesh = new byte[276];
        BinaryPrimitives.WriteInt32LittleEndian(mesh.AsSpan(108), 11);
        var physics = new byte[40 + blob.Length];
        BinaryPrimitives.WriteInt32LittleEndian(physics.AsSpan(16), blob.Length);
        blob.CopyTo(physics, 40);
        var node = Node(10, child ? 13 : -1, child ? Matrix4x4.CreateTranslation(100, 0, 0) : Matrix4x4.Identity, nodeName);
        var chunks = new List<(uint Type, uint Version, int Id, byte[] Bytes)>
        { (0xcccc0014, 0x800, 1, material), (0xcccc0000, 0x800, 10, mesh),
            (0xcccc0018, 0x800, 11, physics), (0xcccc000b, 0x823, 12, node) };
        if (child)
        {
            if (parentHasMesh && !parentHasPhysics) chunks.Add((0xcccc0000, 0x800, 14, new byte[276]));
            chunks.Add((0xcccc000b, 0x823, 13, Node(parentHasMesh ? parentHasPhysics ? 10 : 14 : 0,
                cycle ? 12 : -1, Matrix4x4.CreateScale(2) * Matrix4x4.CreateTranslation(0, 300, 0), parentName)));
        }
        if (exportFlags is { } flags)
        {
            var export = new byte[180];
            BinaryPrimitives.WriteUInt32LittleEndian(export.AsSpan(16), flags);
            chunks.Add((0xcccc0015, 1, 20, export));
        }
        return File(version, chunks);
    }

    internal static byte[] ReferencedCgf(ushort foreignTriangle = 1, bool includeSelectedSubset = true,
        int positionElementSize = 12, bool includeIndices = true, bool verticesOnly = false, bool secondMesh = false)
    {
        var mesh = new byte[276];
        foreach (var pair in new[] { (24, 4), (28, 6), (32, includeSelectedSubset ? 2 : 1),
            (36, 32), (44, 30), (64, 31), (108, 11) })
            BinaryPrimitives.WriteInt32LittleEndian(mesh.AsSpan(pair.Item1), pair.Item2);
        using var blob = new MemoryStream();
        blob.Write(Header(1, 7));
        using (var writer = new BinaryWriter(blob, Encoding.UTF8, true))
        {
            writer.Write(4); writer.Write(1); writer.Write(2); writer.Write(verticesOnly ? 1 : 0x20);
            writer.Write(!verticesOnly);
            if (!verticesOnly) foreach (var value in new ushort[] { 1, 0, 2, 3 }) writer.Write(value);
            writer.Write(!verticesOnly);
            if (!verticesOnly) writer.Write(foreignTriangle);
            else
            {
                foreach (var value in new ushort[] { 2, 1, 0 }) writer.Write(value);
                writer.Write(true); writer.Write((byte)253);
            }
            writer.Write(new byte[16]); writer.Write(0); writer.Write(new byte[32]); writer.Write(2); writer.Write(new byte[68]);
        }
        var physical = new byte[40 + blob.Length];
        BinaryPrimitives.WriteInt32LittleEndian(physical.AsSpan(16), (int)blob.Length);
        blob.ToArray().CopyTo(physical, 40);
        var vertices = new byte[40 + 4 * 12];
        BinaryPrimitives.WriteInt32LittleEndian(vertices.AsSpan(20), 0);
        BinaryPrimitives.WriteInt32LittleEndian(vertices.AsSpan(24), 4);
        BinaryPrimitives.WriteInt32LittleEndian(vertices.AsSpan(28), positionElementSize);
        var points = new Vector3[] { new(0, 0, 1), new(2, 0, 1), new(0, 3, 1), new(4, 0, 2) };
        for (var index = 0; index < points.Length; index++) Vector(vertices, 40 + index * 12, points[index]);
        var indices = new byte[40 + 6 * 2];
        BinaryPrimitives.WriteInt32LittleEndian(indices.AsSpan(20), 5);
        BinaryPrimitives.WriteInt32LittleEndian(indices.AsSpan(24), 6);
        BinaryPrimitives.WriteInt32LittleEndian(indices.AsSpan(28), 2);
        ushort[] faces = [0, 1, 2, 1, 3, 2];
        for (var index = 0; index < faces.Length; index++) BinaryPrimitives.WriteUInt16LittleEndian(indices.AsSpan(40 + index * 2), faces[index]);
        var subsets = new byte[32 + (includeSelectedSubset ? 2 : 1) * 36];
        BinaryPrimitives.WriteInt32LittleEndian(subsets.AsSpan(20), includeSelectedSubset ? 2 : 1);
        BinaryPrimitives.WriteInt32LittleEndian(subsets.AsSpan(36), 3);
        BinaryPrimitives.WriteInt32LittleEndian(subsets.AsSpan(48), 4);
        if (includeSelectedSubset)
        {
            BinaryPrimitives.WriteInt32LittleEndian(subsets.AsSpan(68), 3);
            BinaryPrimitives.WriteInt32LittleEndian(subsets.AsSpan(72), 3);
            BinaryPrimitives.WriteInt32LittleEndian(subsets.AsSpan(84), 255);
        }
        var chunks = new List<(uint Type, uint Version, int Id, byte[] Bytes)>
        { (0xcccc0000, 0x800, 10, mesh), (0xcccc0018, 0x800, 11, physical),
            (0xcccc000b, 0x823, 12, Node(10, -1, Matrix4x4.Identity, "referenced_proxy")),
            (0xcccc0016, 0x800, 30, vertices), (0xcccc0017, 0x800, 32, subsets) };
        if (includeIndices) chunks.Add((0xcccc0016, 0x800, 31, indices));
        if (secondMesh)
        {
            var otherMesh = mesh.ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(otherMesh.AsSpan(44), 50);
            var otherVertices = vertices.ToArray();
            for (var index = 0; index < points.Length; index++) Vector(otherVertices, 40 + index * 12, points[index] + new Vector3(100, 0, 0));
            chunks.Add((0xcccc0000, 0x800, 40, otherMesh));
            chunks.Add((0xcccc000b, 0x823, 42, Node(40, -1, Matrix4x4.Identity, "second_proxy")));
            chunks.Add((0xcccc0016, 0x800, 50, otherVertices));
        }
        return File(0x745, chunks);
    }

    internal static byte[] File(uint version, List<(uint Type, uint Version, int Id, byte[] Bytes)> chunks)
    {
        var stride = version == 0x744 ? 16 : 20;
        var offset = 24 + chunks.Count * stride;
        var bytes = new byte[offset + chunks.Sum(chunk => chunk.Bytes.Length)];
        "CryTek\0\0"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 0xffff0000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), version);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 20);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), chunks.Count);
        for (var index = 0; index < chunks.Count; index++)
        {
            var chunk = chunks[index];
            var table = 24 + index * stride;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(table), chunk.Type);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(table + 4), chunk.Version);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(table + 8), offset);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(table + 12), chunk.Id);
            if (stride == 20) BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(table + 16), chunk.Bytes.Length);
            bytes.AsSpan(table, 16).CopyTo(chunk.Bytes);
            chunk.Bytes.CopyTo(bytes, offset);
            offset += chunk.Bytes.Length;
        }
        return bytes;
    }

    internal static byte[] Node(int objectId, int parentId, Matrix4x4 transform, string name)
    {
        var bytes = new byte[220];
        Encoding.ASCII.GetBytes(name).CopyTo(bytes, 16);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(80), objectId);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(84), parentId);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(92), 1);
        var values = new[] { transform.M11, transform.M12, transform.M13, transform.M14,
            transform.M21, transform.M22, transform.M23, transform.M24,
            transform.M31, transform.M32, transform.M33, transform.M34,
            transform.M41, transform.M42, transform.M43, transform.M44 };
        for (var index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(100 + index * 4), BitConverter.SingleToInt32Bits(values[index]));
        return bytes;
    }

    internal static void Vector(byte[] bytes, int offset, Vector3 value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), BitConverter.SingleToInt32Bits(value.X));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 4), BitConverter.SingleToInt32Bits(value.Y));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 8), BitConverter.SingleToInt32Bits(value.Z));
    }
}
