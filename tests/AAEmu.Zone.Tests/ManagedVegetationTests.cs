using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using AAEmu.Zone.Content;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Tests;

public sealed class ManagedVegetationTests
{
    private const string Model = "game/objects/vegetation.cgf";

    [Test]
    public async Task CatalogPreservesAuthoredRowsAndLastIdReplacementBeforeSortedPublication()
    {
        var catalog = Catalog(Group(9, "old"), Group(2, "second"), Group(9, "replacement"));
        await Assert.That(catalog.Rows.Select(row => row.Id).ToArray()).IsEquivalentTo(new[] { 9, 2, 9 });
        await Assert.That(catalog.Rows.Count).IsEqualTo(3);
        await Assert.That(catalog.Groups.Count).IsEqualTo(2);
        await Assert.That(catalog.Groups[9].ModelFileName).IsEqualTo("replacement");
        await Assert.That(catalog.PublicationOrder[0].Id).IsEqualTo(2);
        await Assert.That(catalog.PublicationOrder[1].Id).IsEqualTo(9);
        await Assert.That(catalog.Groups[9].IntegerAttributes["nMaterialLayers"]).IsEqualTo(16777217);
        await Assert.That(catalog.Groups[9].FloatAttributes["fDensity"]).IsEqualTo(.125f);
        await Assert.That(catalog.Groups[9].Attributes["editorOnlyUnknown"]).IsEqualTo("kept");
    }

    [Test]
    public async Task OriginalTransformAndExclusionInstructionsMatchWithoutEngineLoading()
    {
        using var stream = typeof(ManagedVegetationTests).Assembly.GetManifestResourceStream(
            "AAEmu.Zone.Tests.Fixtures.vegetation-native-controls.json") ?? throw new InvalidOperationException("Missing original controls.");
        using var controls = JsonDocument.Parse(stream);
        var components = 0; var exclusions = 0; var overlapCount = 0;
        foreach (var row in controls.RootElement.GetProperty("transforms").EnumerateArray())
        {
            var angles = row.GetProperty("angles").EnumerateArray().Select(value => value.GetByte()).ToArray();
            var payload = Payload(0, V(row.GetProperty("position")), row.GetProperty("scale").GetSingle());
            payload[0x39] = angles[0]; payload[0x3a] = angles[1]; payload[0x3c] = angles[2];
            var m = ZoneManagedVegetationPlacementMath.Transform(payload, Vector3.Zero, false);
            float[] actual = [m.M11, m.M21, m.M31, m.M41, m.M12, m.M22, m.M32, m.M42, m.M13, m.M23, m.M33, m.M43];
            var expected = row.GetProperty("bits").EnumerateArray().Select(value => value.GetUInt32()).ToArray();
            for (var i = 0; i < expected.Length; i++)
            { await Assert.That(unchecked((uint)BitConverter.SingleToInt32Bits(actual[i]))).IsEqualTo(expected[i]); components++; }
        }
        foreach (var row in controls.RootElement.GetProperty("exclusions").EnumerateArray())
        {
            var v = row.GetProperty("basis").EnumerateArray().Select(value => value.GetSingle()).ToArray();
            var basis = new Matrix4x4(v[0], v[3], v[6], 0, v[1], v[4], v[7], 0, v[2], v[5], v[8], 0, 0, 0, 0, 1);
            var owner = new ZoneManagedVegetationExclusions(Vector3.Zero,
                [new(1, basis, V(row.GetProperty("halfExtents")), V(row.GetProperty("localCenter")), V(row.GetProperty("position")))]);
            var actual = owner.Excludes(new(V(row.GetProperty("minimum")), V(row.GetProperty("maximum"))));
            await Assert.That(actual).IsEqualTo(row.GetProperty("intersects").GetBoolean());
            exclusions++; if (actual) overlapCount++;
        }
        await Assert.That(components).IsEqualTo(192);
        await Assert.That(exclusions).IsEqualTo(140);
        await Assert.That(overlapCount).IsGreaterThan(70);
    }

    [Test]
    public async Task SectorAndCellTranslationsUseActualProxyInsteadOfAuthoredBoundsOrCgfNodeTransform()
    {
        var fixture = new Fixture((9, 14), (10, 15));
        fixture.Assets.Files[Model] = CgfTestAssets.BoxCgf(child: true);
        using var owner = fixture.Owner(Catalog(Group(3, Model)));
        using var cell = owner.BeginCell(fixture.Layout.Cells[1]);
        var payload = Payload(3, new(10, 20, 100), 2); payload[0x3c] = 64;
        cell.LoadSectors(Sectors(3, 2, payload));
        var report = cell.Reports.Single();
        await Assert.That(report.Status).IsEqualTo(ZoneManagedVegetationPlacementStatus.Published);
        await Assert.That(report.Transform!.Value.Translation).IsEqualTo(new Vector3(1802, 1556, 100));
        await Assert.That(report.GeometryFlags).IsEqualTo(0x04018fffu);
        await Assert.That(report.ColliderFlags).IsEqualTo(1u);
        await Assert.That(fixture.Characters.Raycast(new(1802, 1556, 110), new(0, 0, -20), out var hit)).IsTrue();
        await Assert.That(MathF.Abs(hit.Position.Z - 104)).IsLessThan(.001f);
        await Assert.That(hit.PhysicalMaterialId).IsEqualTo((ushort)101);
        await Assert.That(fixture.Rays.Raycast(new(1802, 1556, 110), new(0, 0, -20), out _)).IsTrue();
        await Assert.That(fixture.Characters.TryCollide(ZonePhysicsShape.Capsule(.1f, .1f), new(1802, 1556, 102))).IsTrue();
        await Assert.That(fixture.Characters.Raycast(new(1702, 1556, 110), new(0, 0, -20), out _)).IsFalse();
        await Assert.That(fixture.Assets.Opened).IsEqualTo(fixture.Assets.Closed);
    }

    [Test]
    public async Task ReplacementRetiresExactCellHandlesWithoutRemovingNewOrForeignInstances()
    {
        var fixture = new Fixture((9, 14));
        var foreign = new ZoneManagedPrimitiveGeometry(ZoneManagedPrimitiveKind.Sphere, Vector3.Zero, Vector3.UnitZ, 1, 0, 99);
        fixture.Characters.AddInstance(foreign, Matrix4x4.CreateTranslation(100, 100, 100));
        using var owner = fixture.Owner(Catalog(Group(1, Model)));
        var old = owner.BeginCell(fixture.Layout.Cells[0]);
        old.LoadTaggedObjects(Tagged(Payload(1, Vector3.Zero), Payload(1, new(20, 0, 0))));
        await Assert.That(owner.SharedResourceCount).IsEqualTo(1);
        await Assert.That(fixture.Characters.InstanceCount).IsEqualTo(3);
        var current = owner.BeginCell(fixture.Layout.Cells[0]);
        await Assert.That(old.IsCurrent).IsFalse();
        await Assert.That(fixture.Characters.InstanceCount).IsEqualTo(1);
        current.LoadTaggedObjects(Tagged(Payload(1, new(40, 0, 0))));
        old.Dispose();
        await Assert.That(fixture.Characters.Raycast(new(40, 0, 10), new(0, 0, -20), out _)).IsTrue();
        await Assert.That(fixture.Characters.Raycast(new(0, 0, 10), new(0, 0, -20), out _)).IsFalse();
        owner.Dispose();
        await Assert.That(current.IsCurrent).IsFalse();
        await Assert.That(fixture.Characters.InstanceCount).IsEqualTo(1);
        await Assert.That(fixture.Rays.InstanceCount).IsEqualTo(0);
        await Assert.That(fixture.Characters.Raycast(new(100, 100, 103), new(0, 0, -6), out var untouched)).IsTrue();
        await Assert.That(untouched.PhysicalMaterialId).IsEqualTo((ushort)99);
        await Assert.That(() => old.LoadTaggedObjects(Tagged(Payload(1, Vector3.Zero)))).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task UnsupportedDependenciesNeverPublishPartialBoundsOrBrushPickingProxies()
    {
        foreach (var profile in new[] { "missing", "special", "foliage", "compound", "helper", "material", "alignment" })
        {
            var fixture = new Fixture((9, 14)); var group = Group(1, Model);
            if (profile == "missing") fixture.Assets.Files.Remove(Model);
            if (profile == "material") group.SetAttributeValue("matName", "objects/unowned_override");
            if (profile == "alignment") group.SetAttributeValue("bAlignToTerrain", 1);
            if (profile == "special")
            {
                var bytes = fixture.Assets.Files[Model]; var mesh = ZoneCgfContent.Read(bytes).Chunks.Single(chunk => chunk.Type == 0xcccc0000);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(mesh.Offset + 112), 11);
            }
            if (profile == "foliage")
            {
                var cgf = ZoneCgfContent.Read(fixture.Assets.Files[Model]);
                var chunks = cgf.Chunks.Select(chunk => (chunk.Type, chunk.Version, chunk.Id, chunk.Data.ToArray())).ToList();
                chunks.Add((0xaafc0005, 1, 1234, new byte[32])); fixture.Assets.Files[Model] = CgfTestAssets.File(0x745, chunks);
            }
            if (profile == "compound") fixture.Assets.Files[Model] = CgfTestAssets.BoxCgf(child: true, parentHasMesh: true, exportFlags: 0);
            if (profile == "helper") fixture.Assets.Files[Model] = CgfTestAssets.BoxCgf(child: true, parentHasMesh: true, nodeName: "$picking");
            using var owner = fixture.Owner(Catalog(group));
            using var cell = owner.BeginCell(fixture.Layout.Cells[0]); cell.LoadTaggedObjects(Tagged(Payload(1, Vector3.Zero)));
            if (cell.Reports.Single().Status != ZoneManagedVegetationPlacementStatus.Unsupported)
                throw new InvalidOperationException($"Unexpected admission for {profile}: {cell.Reports.Single().Diagnostic}");
            await Assert.That(cell.Reports.Single().Status).IsEqualTo(ZoneManagedVegetationPlacementStatus.Unsupported);
            await Assert.That(string.IsNullOrWhiteSpace(cell.Reports.Single().Diagnostic)).IsFalse();
            await Assert.That(fixture.Characters.InstanceCount).IsEqualTo(0);
            await Assert.That(fixture.Rays.InstanceCount).IsEqualTo(0);
        }
    }

    [Test]
    public async Task NativeQualitySkipsDoNotRequireMissingModelsAndUnknownSizeGateRemainsUnsupported()
    {
        var fixture = new Fixture((9, 14)); var group = Group(1, ""); group.SetAttributeValue("minConfigSpec", 4);
        using (var owner = fixture.Owner(Catalog(group), new(1, 2, 0, 0)))
        {
            using var cell = owner.BeginCell(fixture.Layout.Cells[0]); cell.LoadTaggedObjects(Tagged(Payload(1, Vector3.Zero)));
            await Assert.That(cell.Reports.Single().Status).IsEqualTo(ZoneManagedVegetationPlacementStatus.SkippedByConfiguration);
        }
        group = Group(1, Model); group.SetAttributeValue("minConfigSpec", 7);
        using (var owner = fixture.Owner(Catalog(group)))
        {
            using var cell = owner.BeginCell(fixture.Layout.Cells[0]); cell.LoadTaggedObjects(Tagged(Payload(1, Vector3.Zero)));
            await Assert.That(cell.Reports.Single().Status).IsEqualTo(ZoneManagedVegetationPlacementStatus.Unsupported);
        }
        using (var owner = fixture.Owner(Catalog(group), new(1, 0, 0, 1, 0)))
        {
            using var cell = owner.BeginCell(fixture.Layout.Cells[0]); cell.LoadTaggedObjects(Tagged(Payload(1, Vector3.Zero)));
            await Assert.That(cell.Reports.Single().Status).IsEqualTo(ZoneManagedVegetationPlacementStatus.SkippedByConfiguration);
        }
        using (var owner = fixture.Owner(Catalog(Group(1, Model)), new(1, 0, .01f, 0)))
        {
            using var cell = owner.BeginCell(fixture.Layout.Cells[0]); cell.LoadTaggedObjects(Tagged(Payload(1, Vector3.Zero)));
            await Assert.That(cell.Reports.Single().Status).IsEqualTo(ZoneManagedVegetationPlacementStatus.Unsupported);
        }
    }

    [Test]
    public async Task ExclusionUsesTranslatedAuthoredBoundsAndCurrentEngineOrigin()
    {
        var fixture = new Fixture((9, 14), (10, 14));
        var snapshot = new ZoneManagedVegetationExclusions(new(8000, 0, 0),
            [new(11, Matrix4x4.Identity, Vector3.One, Vector3.Zero, new(9124, 100, 100))]);
        using var owner = fixture.Owner(Catalog(Group(1, Model)), exclusions: snapshot);
        using var cell = owner.BeginCell(fixture.Layout.Cells[1]); var payload = Payload(1, Vector3.Zero);
        CgfTestAssets.Vector(payload, 0, new(99, 99, 99)); CgfTestAssets.Vector(payload, 12, new(101, 101, 101));
        cell.LoadTaggedObjects(Tagged(payload));
        await Assert.That(cell.Reports.Single().Status).IsEqualTo(ZoneManagedVegetationPlacementStatus.SkippedByExclusion);
        await Assert.That(fixture.Characters.InstanceCount).IsEqualTo(0);
        await Assert.That(fixture.Rays.InstanceCount).IsEqualTo(0);
    }

    [Test]
    public async Task SignedTriangleMaterialsAndBreakEnergyFlagsKeepTheirSourceMeanings()
    {
        var fixture = new Fixture((9, 14));
        fixture.Assets.Files[Model] = CgfTestAssets.BoxCgf(blob: CgfTestAssets.TriangleBlob(material: 255));
        fixture.Text("game/objects/wall_material.mtl", "<Material MtlFlags='256'><SubMaterials>" +
            "<Material SurfaceType='mat_grass'/><Material SurfaceType='mat_stone'/></SubMaterials></Material>");
        fixture.Text("game/libs/materialeffects/surfacetypes.xml", "<SurfaceTypes>" +
            "<SurfaceType name='mat_grass'><Physics break_energy='5'/></SurfaceType>" +
            "<SurfaceType name='mat_stone'><Physics/></SurfaceType></SurfaceTypes>");
        using var owner = fixture.Owner(Catalog(Group(1, Model))); using var cell = owner.BeginCell(fixture.Layout.Cells[0]);
        var suppressed = Payload(1, new(10, 0, 0)); BinaryPrimitives.WriteUInt32LittleEndian(suppressed.AsSpan(0x20), 2);
        cell.LoadTaggedObjects(Tagged(Payload(1, Vector3.Zero), suppressed));
        await Assert.That(cell.Reports[0].GeometryFlags).IsEqualTo(0x04818fffu);
        await Assert.That(cell.Reports[1].GeometryFlags).IsEqualTo(0x04018fffu);
        await Assert.That(cell.Reports[0].ColliderFlags).IsEqualTo(1u);
        await Assert.That(fixture.Characters.Raycast(new(.5f, .5f, 3), new(0, 0, -5), out var hit)).IsTrue();
        await Assert.That(hit.PhysicalMaterialId).IsEqualTo((ushort)100);
    }

    [Test]
    public async Task AnalyticDefaultsRetainTheirCurvedSurfaceAndScaleClamp()
    {
        var fixture = new Fixture((9, 14));
        fixture.Assets.Files[Model] = CgfTestAssets.BoxCgf(blob: CgfTestAssets.PrimitiveBlob(
            ZoneCgfPhysicsGeometryKind.Sphere, Vector3.Zero, Vector3.Zero, 1, 0));
        using var owner = fixture.Owner(Catalog(Group(1, Model))); using var cell = owner.BeginCell(fixture.Layout.Cells[0]);
        cell.LoadTaggedObjects(Tagged(Payload(1, new(0, 0, 100), 0), Payload(1, new(20, 0, 100), 6)));
        await Assert.That(cell.Reports.Count(report => report.Status == ZoneManagedVegetationPlacementStatus.Published)).IsEqualTo(2);
        await Assert.That(fixture.Characters.Raycast(new(0, 0, 101), new(0, 0, -2), out var tiny)).IsTrue();
        await Assert.That(MathF.Abs(tiny.Position.Z - 100.1f)).IsLessThan(.001f);
        await Assert.That(fixture.Characters.Raycast(new(.09f, .09f, 101), new(0, 0, -2), out _)).IsFalse();
        await Assert.That(fixture.Characters.Raycast(new(20, 0, 110), new(0, 0, -20), out var large)).IsTrue();
        await Assert.That(MathF.Abs(large.Position.Z - 105)).IsLessThan(.001f);
    }

    [Test]
    public async Task NativeBoxCompilationWithNegativeWrapperSlotDoesNotInheritMeshBreakFlags()
    {
        foreach (var material in new[] { -1, 0 })
        {
            var fixture = new Fixture((9, 14));
            fixture.Assets.Files[Model] = CgfTestAssets.BoxCgf(blob: CgfTestAssets.BoxBlob(new(0, 0, 1), new(2, 3, 1), material));
            fixture.Text("game/objects/wall_material.mtl", "<Material SurfaceType='mat_grass'/>");
            fixture.Text("game/libs/materialeffects/surfacetypes.xml", "<SurfaceTypes>" +
                "<SurfaceType name='mat_grass'><Physics break_energy='5'/></SurfaceType></SurfaceTypes>");
            using var owner = fixture.Owner(Catalog(Group(1, Model))); using var cell = owner.BeginCell(fixture.Layout.Cells[0]);
            cell.LoadTaggedObjects(Tagged(Payload(1, Vector3.Zero)));
            await Assert.That(cell.Reports.Single().Status).IsEqualTo(ZoneManagedVegetationPlacementStatus.Published);
            await Assert.That(cell.Reports.Single().GeometryFlags).IsEqualTo(material < 0 ? 0x04018fffu : 0x04818fffu);
            await Assert.That(fixture.Characters.Raycast(new(0, 0, 10), new(0, 0, -20), out var hit)).IsTrue();
            await Assert.That(hit.PhysicalMaterialId).IsEqualTo((ushort)100);
        }
    }

    [Test]
    public async Task DirectRootVehicleOnlyMaterialDoesNotAcquireTheBrushOrChildMaskPolicy()
    {
        var fixture = new Fixture((9, 14));
        fixture.Text("game/objects/wall_material.mtl", "<Material SurfaceType='mat_vehicle'/>");
        fixture.Text("game/libs/materialeffects/surfacetypes.xml", "<SurfaceTypes>" +
            "<SurfaceType name='mat_vehicle'><Physics vehicle_only_collisions='1'/></SurfaceType></SurfaceTypes>");
        using var owner = fixture.Owner(Catalog(Group(1, Model))); using var cell = owner.BeginCell(fixture.Layout.Cells[0]);
        cell.LoadTaggedObjects(Tagged(Payload(1, Vector3.Zero)));
        await Assert.That(cell.Reports.Single().Status).IsEqualTo(ZoneManagedVegetationPlacementStatus.Published);
        await Assert.That(cell.Reports.Single().GeometryFlags).IsEqualTo(0x04018fffu);
        await Assert.That(cell.Reports.Single().ColliderFlags).IsEqualTo(1u);
        await Assert.That(fixture.Characters.Raycast(new(0, 0, 10), new(0, 0, -20), out var hit)).IsTrue();
        await Assert.That(fixture.Rays.Raycast(new(0, 0, 10), new(0, 0, -20), out _)).IsTrue();
        await Assert.That(hit.PhysicalMaterialId).IsEqualTo((ushort)100);
    }

    [Test]
    public async Task AssetCallbackRetirementCannotPublishOrCacheAStaleCellResource()
    {
        var fixture = new Fixture((9, 14)); ZoneManagedVegetationCellLease? cell = null;
        var source = new CallbackAssets(fixture.Assets, path => { if (path == Model) cell!.Dispose(); });
        using var owner = fixture.Owner(Catalog(Group(1, Model)), source: source);
        cell = owner.BeginCell(fixture.Layout.Cells[0]);
        await Assert.That(() => cell.LoadTaggedObjects(Tagged(Payload(1, Vector3.Zero)))).Throws<ObjectDisposedException>();
        await Assert.That(cell.IsCurrent).IsFalse();
        await Assert.That(owner.SharedResourceCount).IsEqualTo(0);
        await Assert.That(fixture.Characters.InstanceCount).IsEqualTo(0);
        await Assert.That(fixture.Rays.InstanceCount).IsEqualTo(0);
        await Assert.That(fixture.Assets.Opened).IsEqualTo(fixture.Assets.Closed);
    }

    [Test]
    public async Task AssetCallbackCannotReenterPublicationBeforeTheOuterResourceCompletes()
    {
        var fixture = new Fixture((9, 14)); ZoneManagedVegetationCellLease? cell = null;
        var source = new CallbackAssets(fixture.Assets, path =>
        {
            if (path == Model) cell!.LoadSectors(Sectors(0, 0, Payload(1, new(20, 0, 0))));
        });
        using var owner = fixture.Owner(Catalog(Group(1, Model)), source: source);
        cell = owner.BeginCell(fixture.Layout.Cells[0]);
        await Assert.That(() => cell.LoadTaggedObjects(Tagged(Payload(1, Vector3.Zero)))).Throws<InvalidOperationException>();
        await Assert.That(cell.IsCurrent).IsTrue();
        await Assert.That(cell.Reports.Count).IsEqualTo(0);
        await Assert.That(owner.SharedResourceCount).IsEqualTo(0);
        await Assert.That(fixture.Characters.InstanceCount).IsEqualTo(0);
        await Assert.That(fixture.Rays.InstanceCount).IsEqualTo(0);
    }

    private static Vector3 V(JsonElement v) => new(v[0].GetSingle(), v[1].GetSingle(), v[2].GetSingle());
    private static ZoneWorldVegetationContent Catalog(params XElement[] groups)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(new XElement("Vegetation", new XElement("groupList", groups)).ToString()));
        return ZoneWorldVegetationContent.Read(input, "authored");
    }
    private static XElement Group(int id, string model)
    {
        var row = new XElement("group", new XAttribute("id", id), new XAttribute("modelFileName", model));
        foreach (var name in new[] { "bHideability", "bHideabilitySecondary", "bPickable", "bComplexBending", "bCastShadow", "bCastShadowExtraRange", "bCastShadowTerrain",
            "bRecvShadow", "bPrecShadow", "bUseAlphaBlending", "bUseSprites", "bSpriteCastShadow", "bRandomRotation", "bAlignToTerrain", "bUseTerrainColor", "bAffectedByVoxels", "minConfigSpec", "doodadType" })
            row.SetAttributeValue(name, 0);
        foreach (var name in new[] { "fBending", "fSpriteDistRatio", "fShadowDistRatio", "fMaxViewDistRatio", "fBrightness", "fDensity", "fElevationMax", "fElevationMin",
            "fSize", "fSizeVar", "fSlopeMax", "fSlopeMin", "minDistSameObject", "minDistOtherObject", "aiRadius" }) row.SetAttributeValue(name, ".125");
        row.SetAttributeValue("nMaterialLayers", 16777217); row.SetAttributeValue("editorOnlyUnknown", "kept"); return row;
    }
    private static byte[] Payload(short group, Vector3 position, float scale = 1)
    {
        var bytes = new byte[64]; CgfTestAssets.Vector(bytes, 0, new(-10000)); CgfTestAssets.Vector(bytes, 12, new(10000));
        CgfTestAssets.Vector(bytes, 0x27, position); BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0x33), scale);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(0x37), group); return bytes;
    }
    private static ZoneCellObjectContent Tagged(params byte[][] payloads)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(0); writer.Write(0);
        foreach (var payload in payloads) { writer.Write((int)ZoneCellObjectType.Vegetation); writer.Write(payload); }
        return ZoneCellObjectContent.Read(stream.ToArray(), ZoneCellObjectFileKind.BigObjects);
    }
    private static ZoneCellVegetationContent Sectors(int x, int y, params byte[][] payloads)
    {
        var bytes = new byte[132 + payloads.Length * 64]; BinaryPrimitives.WriteInt32LittleEndian(bytes, 1);
        var index = x * 4 + y; BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4 + index * 4), 132);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(68 + index * 4), payloads.Length * 64);
        for (var i = 0; i < payloads.Length; i++) payloads[i].CopyTo(bytes, 132 + i * 64);
        return ZoneCellVegetationContent.Read(bytes);
    }
    private sealed class Fixture
    {
        internal readonly ManagedSceneActivationTests.Fixture Base;
        internal Fixture(params (int X, int Y)[] cells)
        {
            Base = new(cells); Assets.Files[Model] = CgfTestAssets.BoxCgf();
            Text("game/objects/wall_material.mtl", "<Material SurfaceType='mat_stone'/>");
        }
        internal ManagedSceneActivationTests.MemoryAssets Assets => Base.Assets;
        internal ZoneWorldMapLayout Layout => Base.Layout;
        internal readonly ZoneManagedStaticGeometry Characters = new(), Rays = new();
        internal void Text(string path, string text) => Base.Text(path, text);
        internal ZoneManagedVegetationResources Owner(ZoneWorldVegetationContent catalog, ZoneManagedVegetationPolicy? policy = null,
            ZoneManagedVegetationExclusions? exclusions = null, IZoneAssetSource? source = null)
        {
            return new(source ?? Assets, ZoneTerrainMaterialResolver.Load(source ?? Assets), Layout, catalog, Characters, Rays,
                policy ?? new(1, 0, 0, 0), exclusions ?? new(Vector3.Zero, []));
        }
    }
    private sealed class CallbackAssets(IZoneAssetSource source, Action<string> onRead) : IZoneAssetSource
    { public Stream? OpenRead(string path) { onRead(path); return source.OpenRead(path); } }
}
