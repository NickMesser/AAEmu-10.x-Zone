using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using AAEmu.Zone.Content;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Tests;

public sealed class ManagedSceneCollisionTests
{
    private const string Model = "game/objects/wall.cgf";

    [Test]
    public async Task AuthoredProxyUsesBrushRotationCellOriginMaterialsAndOrderedUnload()
    {
        var fixture = new ManagedSceneActivationTests.Fixture((9, 14), (10, 15));
        fixture.Assets.Files[Model] = CgfTestAssets.BoxCgf();
        fixture.Text("game/objects/wall_material.mtl", "<Material SurfaceType='mat_stone' />");
        var placement = new ZoneCellObjectTransform(0, -1, 0, 10, 1, 0, 0, 20, 0, 0, 1, 100);
        using var scene = Collision(fixture, placement, cellIndex: 1);
        var origin = new Vector3(1034, 1044, 110);
        await Assert.That(scene.Geometry.InstanceCount).IsEqualTo(1);
        await Assert.That(scene.RayGeometry.InstanceCount).IsEqualTo(1);
        await Assert.That(scene.Geometry.Raycast(origin, new(0, 0, -20), out var hit)).IsTrue();
        await Assert.That(MathF.Abs(hit.Position.Z - 102)).IsLessThan(.001f);
        await Assert.That(hit.PhysicalMaterialId).IsEqualTo((ushort)101);
        await Assert.That(scene.Geometry.TryCollide(new ZonePhysicsShape(new(.1f), false), new(1034, 1044, 101))).IsTrue();
        await Assert.That(scene.Geometry.Raycast(new(1036.9f, 1044, 110), new(0, 0, -20), out _)).IsTrue();
        await Assert.That(scene.Geometry.Raycast(new(1037.1f, 1044, 110), new(0, 0, -20), out _)).IsFalse();
        await Assert.That(scene.Report.BrushCoverageComplete).IsTrue();
        scene.Dispose();
        await Assert.That(scene.Geometry.InstanceCount).IsEqualTo(0);
        await Assert.That(scene.RayGeometry.InstanceCount).IsEqualTo(0);
        await Assert.That(scene.Geometry.Raycast(origin, new(0, 0, -20), out _)).IsFalse();
        await Assert.That(fixture.Assets.Opened).IsEqualTo(fixture.Assets.Closed);
    }

    [Test]
    public async Task UnsupportedOrMissingPhysicalDataNeverBecomesABoundingBoxCollider()
    {
        var fixture = new ManagedSceneActivationTests.Fixture((9, 14));
        var blob = CgfTestAssets.BoxBlob(new(0, 0, 1), new(2), 0);
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(68), 42); // Unknown authored proxy; never replace it with bounds.
        fixture.Assets.Files[Model] = CgfTestAssets.BoxCgf(blob: blob);
        using var scene = Collision(fixture, Identity);
        await Assert.That(scene.Geometry.InstanceCount).IsEqualTo(0);
        await Assert.That(scene.Report.UnsupportedBrushCount).IsEqualTo(1);
        await Assert.That(scene.Report.Resources.Single().Diagnostics.Any(line => line.Contains("type 42", StringComparison.Ordinal))).IsTrue();
        await Assert.That(scene.Geometry.Raycast(new(0, 0, 10), new(0, 0, -20), out _)).IsFalse();
        fixture.Assets.Files.Remove(Model);
        using var missing = Collision(fixture, Identity);
        await Assert.That(missing.Report.UnsupportedBrushCount).IsEqualTo(1);
        await Assert.That(missing.Geometry.InstanceCount).IsEqualTo(0);
    }

    [Test]
    public async Task SignedAndOutOfRangeMaterialSlotsUseNativeDefaultAndMappingZeroRules()
    {
        foreach (var material in new byte?[] { 255, null, 1 })
        {
            var fixture = new ManagedSceneActivationTests.Fixture((9, 14));
            fixture.Assets.Files[Model] = CgfTestAssets.BoxCgf(blob: CgfTestAssets.TriangleBlob(material: material));
            fixture.Text("game/objects/wall_material.mtl", "<Material MtlFlags='256'><SubMaterials>" +
                "<Material SurfaceType='mat_grass' /><Material SurfaceType='mat_stone' /></SubMaterials></Material>");
            using var scene = Collision(fixture, Identity);
            await Assert.That(scene.Geometry.Raycast(new(.5f, .5f, 4), new(0, 0, -10), out var hit)).IsTrue();
            await Assert.That(hit.PhysicalMaterialId).IsEqualTo(material == 1 ? (ushort)101 : (ushort)100);
            await Assert.That(scene.Report.UnsupportedBrushCount).IsEqualTo(0);
        }
    }

    [Test]
    public async Task PickingProxyIsARayTargetWithoutBecomingALivingCharacterWall()
    {
        var fixture = new ManagedSceneActivationTests.Fixture((9, 14));
        var bytes = CgfTestAssets.BoxCgf();
        var mesh = ZoneCgfContent.Read(bytes).Chunks.Single(chunk => chunk.Type == 0xcccc0000);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(mesh.Offset + 108), 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(mesh.Offset + 112), 11);
        fixture.Assets.Files[Model] = bytes;
        using var scene = Collision(fixture, Identity);
        await Assert.That(scene.Geometry.InstanceCount).IsEqualTo(0);
        await Assert.That(scene.RayGeometry.InstanceCount).IsEqualTo(1);
        await Assert.That(scene.Geometry.Raycast(new(0, 0, 10), new(0, 0, -20), out _)).IsFalse();
        await Assert.That(scene.RayGeometry.Raycast(new(0, 0, 10), new(0, 0, -20), out _)).IsTrue();
        await Assert.That(scene.Report.Resources.Single().RayInstances).IsEqualTo(1);
        await Assert.That(scene.Report.Resources.Single().SkippedNonDefaultParts).IsEqualTo(0);
        await Assert.That(scene.Report.RayOnlyBrushCount).IsEqualTo(1);
        await Assert.That(scene.Report.RenderOnlyBrushCount).IsEqualTo(0);
    }

    [Test]
    public async Task ASubmaterialGraphWithoutTheNativeMultiFlagKeepsTheRootSurface()
    {
        var fixture = new ManagedSceneActivationTests.Fixture((9, 14));
        fixture.Assets.Files[Model] = CgfTestAssets.BoxCgf();
        fixture.Text("game/objects/wall_material.mtl", "<Material SurfaceType='mat_stone'><SubMaterials>" +
            "<Material SurfaceType='mat_grass' /></SubMaterials></Material>");
        using var scene = Collision(fixture, Identity);
        await Assert.That(scene.Geometry.Raycast(new(0, 0, 5), new(0, 0, -10), out var hit)).IsTrue();
        await Assert.That(hit.PhysicalMaterialId).IsEqualTo((ushort)101);
    }

    [Test]
    public async Task VehicleOnlyOverrideAppliesToTheWholeBrushIncludingUnusedSubmaterials()
    {
        var fixture = new ManagedSceneActivationTests.Fixture((9, 14));
        fixture.Assets.Files[Model] = CgfTestAssets.BoxCgf();
        fixture.Text("game/libs/materialeffects/surfacetypes.xml", "<SurfaceTypes>" +
            "<SurfaceType name='mat_grass'><Physics /></SurfaceType>" +
            "<SurfaceType name='mat_vehicle'><Physics vehicle_only_collisions='1' /></SurfaceType></SurfaceTypes>");
        fixture.Text("game/objects/override.mtl", "<Material MtlFlags='256'><SubMaterials>" +
            "<Material SurfaceType='mat_grass' /><Material SurfaceType='mat_vehicle' /></SubMaterials></Material>");
        using var scene = Collision(fixture, Identity, materialOverride: "game/objects/override");
        await Assert.That(scene.Geometry.InstanceCount).IsEqualTo(0);
        await Assert.That(scene.RayGeometry.InstanceCount).IsEqualTo(0);
        await Assert.That(scene.Report.Resources.Single().NonBlockingReason).IsNotNull();
        await Assert.That(scene.Report.UnsupportedBrushCount).IsEqualTo(0);
    }

    [Test]
    public async Task VisualHelpersKeepTheirAuthoredDataWithoutEnteringCollision()
    {
        var fixture = new ManagedSceneActivationTests.Fixture((9, 14));
        fixture.Assets.Files[Model] = CgfTestAssets.BoxCgf(child: true, parentHasMesh: true,
            parentHasPhysics: false, nodeName: "$lod1");
        using var scene = Collision(fixture, Identity);
        await Assert.That(scene.Geometry.InstanceCount).IsEqualTo(0);
        await Assert.That(scene.RayGeometry.InstanceCount).IsEqualTo(0);
        await Assert.That(scene.Report.RenderOnlyBrushCount).IsEqualTo(1);
    }

    [Test]
    public async Task CurvedAuthoredProxiesKeepAnalyticSurfacesMaterialsAndSharedLifetime()
    {
        foreach (var kind in new[] { 4, 5, 6 })
        {
            var fixture = new ManagedSceneActivationTests.Fixture((9, 14), (10, 15));
            fixture.Assets.Files[Model] = CgfTestAssets.BoxCgf(blob: PrimitiveBlob(kind, 1));
            fixture.Text("game/objects/wall_material.mtl", "<Material MtlFlags='256'><SubMaterials>" +
                "<Material SurfaceType='mat_grass' /><Material SurfaceType='mat_stone' /></SubMaterials></Material>");
            using var scene = Collision(fixture, new(1, 0, 0, 10, 0, 1, 0, 20, 0, 0, 1, 100), cellIndex: 1);
            var center = new Vector3(1034, 1044, 101);
            await Assert.That(scene.Geometry.Raycast(center + new Vector3(0, 0, 10), new(0, 0, -20), out var hit)).IsTrue();
            var top = kind == 6 ? 103f : 102f;
            await Assert.That(MathF.Abs(hit.Position.Z - top)).IsLessThan(.001f);
            await Assert.That(hit.PhysicalMaterialId).IsEqualTo((ushort)101);
            await Assert.That(scene.Geometry.TryCollide(new ZonePhysicsShape(new(.1f), false), center)).IsTrue();
            await Assert.That(scene.Geometry.Raycast(center + new Vector3(1.01f, 0, 10), new(0, 0, -20), out _)).IsFalse();
            await Assert.That(scene.Report.Resources.Single().MeshInstances).IsEqualTo(0);
            await Assert.That(scene.Report.Resources.Single().PrimitiveInstances).IsEqualTo(1);
            await Assert.That(scene.Report.Resources.Single().CollisionInstances).IsEqualTo(1);
            await Assert.That(scene.Report.BrushCoverageComplete).IsTrue();
            scene.Dispose();
            await Assert.That(scene.Geometry.InstanceCount).IsEqualTo(0);
            await Assert.That(scene.RayGeometry.InstanceCount).IsEqualTo(0);
        }
    }

    [Test]
    public async Task AnalyticPickingProxyRetainsRayProfileWithoutBlockingCharacters()
    {
        var fixture = new ManagedSceneActivationTests.Fixture((9, 14));
        var bytes = CgfTestAssets.BoxCgf(blob: PrimitiveBlob(6, 0));
        var mesh = ZoneCgfContent.Read(bytes).Chunks.Single(chunk => chunk.Type == 0xcccc0000);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(mesh.Offset + 108), 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(mesh.Offset + 112), 11);
        fixture.Assets.Files[Model] = bytes;
        using var scene = Collision(fixture, Identity);
        await Assert.That(scene.Geometry.InstanceCount).IsEqualTo(0);
        await Assert.That(scene.RayGeometry.InstanceCount).IsEqualTo(1);
        await Assert.That(scene.RayGeometry.Raycast(new(0, 0, 10), new(0, 0, -20), out _)).IsTrue();
        await Assert.That(scene.Report.Resources.Single().RayInstances).IsEqualTo(1);
        await Assert.That(scene.Report.Resources.Single().SkippedNonDefaultParts).IsEqualTo(0);
        await Assert.That(scene.Report.RayOnlyBrushCount).IsEqualTo(1);
        await Assert.That(scene.Report.RenderOnlyBrushCount).IsEqualTo(0);
    }

    [Test]
    public async Task DirectRootBrushScaleAdmissionMatchesTheOriginalBaselineComparisons()
    {
        foreach (var (x, y, z, admitted) in new[]
        {
            (1f, 1.009f, .991f, true), // Both differ from X by <1cm; Y-Z differs by >1cm.
            (1f, 1.011f, 1f, false),
            (-1f, 1f, 1f, true)
        })
        {
            var fixture = new ManagedSceneActivationTests.Fixture((9, 14));
            fixture.Assets.Files[Model] = CgfTestAssets.BoxCgf();
            using var scene = Collision(fixture, new(x, 0, 0, 0, 0, y, 0, 0, 0, 0, z, 0));
            await Assert.That(scene.Geometry.InstanceCount).IsEqualTo(admitted ? 1 : 0);
            await Assert.That(scene.Report.Resources.Single().SkippedNonUniformBrushes).IsEqualTo(admitted ? 0 : 1);
            await Assert.That(scene.Report.UnsupportedBrushCount).IsEqualTo(0);
            await Assert.That(scene.Report.RenderOnlyBrushCount).IsEqualTo(admitted ? 0 : 1);
        }
        var invalid = new ManagedSceneActivationTests.Fixture((9, 14));
        invalid.Assets.Files[Model] = CgfTestAssets.BoxCgf();
        using var malformed = Collision(invalid, new(0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0));
        await Assert.That(malformed.Report.UnsupportedBrushCount).IsEqualTo(1);
        await Assert.That(malformed.Report.Resources.Single().SkippedNonUniformBrushes).IsEqualTo(0);
    }

    [Test]
    public async Task DirectRootUsesRawGeometryWhileCompoundChildrenUseTheirNodeTransforms()
    {
        var direct = new ManagedSceneActivationTests.Fixture((9, 14));
        direct.Assets.Files[Model] = CgfTestAssets.BoxCgf(child: true);
        using var directScene = Collision(direct, Identity);
        await Assert.That(directScene.Geometry.InstanceCount).IsEqualTo(1);
        await Assert.That(directScene.Geometry.Raycast(new(0, 0, 10), new(0, 0, -20), out var raw)).IsTrue();
        await Assert.That(MathF.Abs(raw.Position.Z - 2f)).IsLessThan(.001f);
        await Assert.That(directScene.Geometry.Raycast(new(5, 3, 10), new(0, 0, -20), out _)).IsFalse();

        var compound = new ManagedSceneActivationTests.Fixture((9, 14));
        compound.Assets.Files[Model] = CgfTestAssets.BoxCgf(child: true, parentHasMesh: true, exportFlags: 0);
        using var compoundScene = Collision(compound, new(1, 0, 0, 0, 0, 2, 0, 0, 0, 0, 1, 0));
        await Assert.That(compoundScene.Geometry.InstanceCount).IsEqualTo(2);
        await Assert.That(compoundScene.Report.Resources.Single().SkippedNonUniformBrushes).IsEqualTo(0);
        // Child world TM: local X translation1m, then parent scale2 and Y translation3m.
        await Assert.That(compoundScene.Geometry.Raycast(new(5, 6, 10), new(0, 0, -20), out var child)).IsTrue();
        await Assert.That(MathF.Abs(child.Position.Z - 4f)).IsLessThan(.001f);
    }

    [Test]
    public async Task SelectedPhysicsHelperPublishesRawRootAndEligibleTransformedChildSeparately()
    {
        foreach (var name in new[] { "$physics_proxy", "$physics_proxy_$lod1", "$AiMpOiNt_$physics_proxy" })
        {
            var fixture = new ManagedSceneActivationTests.Fixture((9, 14));
            fixture.Assets.Files[Model] = CgfTestAssets.BoxCgf(child: true, parentHasMesh: true,
                parentHasPhysics: false, nodeName: name);
            using var scene = Collision(fixture, Identity);
            var child = name == "$physics_proxy";
            await Assert.That(scene.Geometry.InstanceCount).IsEqualTo(child ? 2 : 1);
            // The raw root is registered before visual helper filtering, including LOD/aimpoint.
            await Assert.That(scene.Geometry.Raycast(new(0, 0, 1), new(0, 0, 10), out var root)).IsTrue();
            await Assert.That(MathF.Abs(root.Position.Z - 2f)).IsLessThan(.001f);
            await Assert.That(scene.Geometry.Raycast(new(5, 3, 10), new(0, 0, -20), out _)).IsEqualTo(child);
        }
    }

    [Test]
    public async Task AHelperOnlyResourceRetainsAnExplicitNativeLoadRefusal()
    {
        var fixture = new ManagedSceneActivationTests.Fixture((9, 14));
        fixture.Assets.Files[Model] = CgfTestAssets.BoxCgf(nodeName: "$physics_proxy");
        using var scene = Collision(fixture, Identity);
        await Assert.That(scene.Geometry.InstanceCount).IsEqualTo(0);
        await Assert.That(scene.Report.UnsupportedBrushCount).IsEqualTo(1);
        await Assert.That(scene.Report.Resources.Single().Diagnostics.Any(line =>
            line.Contains("ordinary mesh", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task AFailedPlacementDoesNotMarkLaterValidBrushesOfTheSameResourceUnsupported()
    {
        var fixture = new ManagedSceneActivationTests.Fixture((9, 14));
        fixture.Assets.Files[Model] = CgfTestAssets.BoxCgf();
        using var scene = Collisions(fixture, [new(0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0), Identity]);
        await Assert.That(scene.Report.BrushCount).IsEqualTo(2);
        await Assert.That(scene.Report.LoadedBrushCount).IsEqualTo(1);
        await Assert.That(scene.Report.UnsupportedBrushCount).IsEqualTo(1);
        await Assert.That(scene.Report.Resources.Single().RequestedBrushes).IsEqualTo(2);
        await Assert.That(scene.Report.Resources.Single().LoadedBrushes).IsEqualTo(1);
        await Assert.That(scene.Report.Resources.Single().FailedPlacements).IsEqualTo(1);
        await Assert.That(scene.Geometry.InstanceCount).IsEqualTo(1);
        await Assert.That(scene.Report.Resources.Single().Diagnostics.Count).IsEqualTo(1);
    }

    private static byte[] PrimitiveBlob(int kind, int material)
    {
        var bytes = new byte[72 + (kind == 4 ? 16 : 36) + 68];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(56), material);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(68), kind);
        CgfTestAssets.Vector(bytes, 72, new(0, 0, 1));
        if (kind == 4) BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(84), BitConverter.SingleToInt32Bits(1f));
        else
        {
            CgfTestAssets.Vector(bytes, 84, Vector3.UnitZ);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(96), BitConverter.SingleToInt32Bits(1f));
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(100), BitConverter.SingleToInt32Bits(1f));
        }
        return bytes;
    }

    private static readonly ZoneCellObjectTransform Identity = new(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0);

    private static ZoneManagedSceneCollision Collision(ManagedSceneActivationTests.Fixture fixture,
        ZoneCellObjectTransform transform, int cellIndex = 0, string? materialOverride = null)
        => Collisions(fixture, [transform], cellIndex, materialOverride);

    private static ZoneManagedSceneCollision Collisions(ManagedSceneActivationTests.Fixture fixture,
        ZoneCellObjectTransform[] transforms, int cellIndex = 0, string? materialOverride = null)
    {
        using var file = new MemoryStream();
        using (var writer = new BinaryWriter(file, Encoding.UTF8, true))
        {
            writer.Write(1); Name(Model);
            writer.Write(materialOverride is null ? 0 : 1);
            if (materialOverride is not null) Name(materialOverride);
            foreach (var transform in transforms)
            {
                writer.Write((int)ZoneCellObjectType.Brush);
                var payload = new byte[128];
                CgfTestAssets.Vector(payload, 0, new(-100));
                CgfTestAssets.Vector(payload, 12, new(100)); // Deliberately unrelated render bounds.
                var matrix = new[] { transform.M00, transform.M01, transform.M02, transform.M03,
                    transform.M10, transform.M11, transform.M12, transform.M13,
                    transform.M20, transform.M21, transform.M22, transform.M23 };
                for (var index = 0; index < matrix.Length; index++)
                    BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0x43 + index * 4), BitConverter.SingleToInt32Bits(matrix[index]));
                BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0x73), materialOverride is null ? -1 : 0);
                writer.Write(payload);
            }
            void Name(string value)
            {
                writer.Write(0); var bytes = new byte[256]; Encoding.UTF8.GetBytes(value).CopyTo(bytes, 0); writer.Write(bytes);
            }
        }
        var objects = ZoneCellObjectContent.Read(file.ToArray(), ZoneCellObjectFileKind.BigObjects);
        var cell = new ZoneManagedSceneCellLoadReport(fixture.Layout.Cells[cellIndex], new(ZoneTerrainCellLoadStatus.Loaded),
            new(objects, null, null, null, []));
        return new(fixture.Assets, ZoneTerrainMaterialResolver.Load(fixture.Assets), [cell]);
    }
}
