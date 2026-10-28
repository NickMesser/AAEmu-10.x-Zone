using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using AAEmu.Zone.Content;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Tests;

public sealed class ManagedSceneAdmissionBoundsTests
{
    private const string Model = "game/objects/admission.cgf";
    private static readonly ZonePhysicsQueryBounds LowerAdmission = new(new(-1, -1, -.5f), new(1, 1, .5f));
    private static readonly ZonePhysicsShape TallShape = ZonePhysicsShape.Capsule(.4f, 2);

    [Test]
    public async Task RayOnlySiblingAdmitsItsEntityBeforeTheUpperCharacterPartIsTested()
    {
        var fixture = new ManagedSceneActivationTests.Fixture((9, 14));
        fixture.Assets.Files[Model] = Resource(compound: true, lowerSlot: 1);
        using var scene = Scene(fixture, 0);
        await Assert.That(scene.Geometry.InstanceCount).IsEqualTo(1);
        await Assert.That(scene.RayGeometry.InstanceCount).IsEqualTo(2);
        var contacts = scene.Geometry.GetUpwardUnprojections(TallShape, Vector3.Zero, LowerAdmission);
        await Assert.That(contacts.Count).IsGreaterThan(0);
        await Assert.That(contacts.Max(contact => contact.Depth)).IsGreaterThan(4f);
        // The lower sibling contributes admission only; it must not become a character wall.
        await Assert.That(scene.Geometry.TryCollide(ZonePhysicsShape.Box(new(.05f)), Vector3.Zero)).IsFalse();
    }

    [Test]
    public async Task VehicleOnlyChildStillContributesToTheSamePhysicalEntityBounds()
    {
        var fixture = new ManagedSceneActivationTests.Fixture((9, 14));
        fixture.Assets.Files[Model] = Resource(compound: true, lowerSlot: 0, lowerVehicleOnly: true);
        fixture.Text("game/libs/materialeffects/surfacetypes.xml", "<SurfaceTypes>" +
            "<SurfaceType name='mat_stone' /><SurfaceType name='mat_vehicle'><Physics vehicle_only_collisions='1' /></SurfaceType>" +
            "</SurfaceTypes>");
        fixture.Text("game/objects/lower_vehicle.mtl", "<Material SurfaceType='mat_vehicle' />");
        using var scene = Scene(fixture, 0);
        await Assert.That(scene.Geometry.InstanceCount).IsEqualTo(1);
        await Assert.That(scene.RayGeometry.InstanceCount).IsEqualTo(1);
        await Assert.That(scene.Geometry.GetUpwardUnprojections(TallShape, Vector3.Zero, LowerAdmission).Count).IsGreaterThan(0);
        await Assert.That(scene.Geometry.TryCollide(ZonePhysicsShape.Box(new(.05f)), Vector3.Zero)).IsFalse();
    }

    [Test]
    public async Task DirectRootObstructPartCountsButUnpublishedCompoundAndSlotThreeDoNot()
    {
        foreach (var (compound, slot, expected) in new[] { (false, 2, true), (true, 2, false), (false, 3, false) })
        {
            var fixture = new ManagedSceneActivationTests.Fixture((9, 14));
            fixture.Assets.Files[Model] = Resource(compound, slot, sameMesh: true);
            using var scene = Scene(fixture, 0);
            await Assert.That(scene.Geometry.InstanceCount).IsEqualTo(1);
            await Assert.That(scene.RayGeometry.InstanceCount).IsEqualTo(1);
            var contacts = scene.Geometry.GetUpwardUnprojections(TallShape, Vector3.Zero, LowerAdmission);
            await Assert.That(contacts.Count != 0).IsEqualTo(expected);
            await Assert.That(scene.Report.Resources.Single().SkippedNonDefaultParts).IsEqualTo(1);
        }
    }

    [Test]
    public async Task SeparateBrushPlacementsNeverShareTheResourcesAdmissionUnion()
    {
        var fixture = new ManagedSceneActivationTests.Fixture((9, 14));
        fixture.Assets.Files[Model] = CgfTestAssets.BoxCgf(blob: CgfTestAssets.BoxBlob(Vector3.Zero, new(.2f, .2f, .1f), 0));
        using var scene = Scene(fixture, 0, 2);
        await Assert.That(scene.Geometry.InstanceCount).IsEqualTo(2);
        var admitted = scene.Geometry.GetUpwardUnprojections(TallShape, Vector3.Zero, LowerAdmission);
        var all = scene.Geometry.GetUpwardUnprojections(TallShape, Vector3.Zero);
        await Assert.That(admitted.Count).IsEqualTo(1);
        await Assert.That(all.Count).IsEqualTo(2);
        await Assert.That(admitted[0].Depth).IsLessThan(3f);
        await Assert.That(all.Max(contact => contact.Depth)).IsGreaterThan(4f);
    }

    private static byte[] Resource(bool compound, int lowerSlot, bool sameMesh = false, bool lowerVehicleOnly = false)
    {
        var upper = new byte[276];
        BinaryPrimitives.WriteInt32LittleEndian(upper.AsSpan(108), 11);
        if (sameMesh) BinaryPrimitives.WriteInt32LittleEndian(upper.AsSpan(108 + lowerSlot * 4), 21);
        var chunks = new List<(uint Type, uint Version, int Id, byte[] Bytes)>
        {
            (0xcccc0014, 0x800, 1, Material("wall_material")),
            (0xcccc0000, 0x800, 10, upper),
            (0xcccc0018, 0x800, 11, Physics(new(0, 0, 2))),
            (0xcccc000b, 0x823, 12, CgfTestAssets.Node(10, -1, Matrix4x4.Identity, "upper")),
            (0xcccc0018, 0x800, 21, Physics(Vector3.Zero))
        };
        if (compound)
        {
            var lower = new byte[276];
            if (!sameMesh) BinaryPrimitives.WriteInt32LittleEndian(lower.AsSpan(108 + lowerSlot * 4), 21);
            var node = CgfTestAssets.Node(20, -1, Matrix4x4.Identity, "lower");
            if (lowerVehicleOnly)
            {
                chunks.Add((0xcccc0014, 0x800, 2, Material("lower_vehicle")));
                BinaryPrimitives.WriteInt32LittleEndian(node.AsSpan(92), 2);
            }
            chunks.Add((0xcccc0000, 0x800, 20, lower));
            chunks.Add((0xcccc000b, 0x823, 22, node));
            // More than one ordinary mesh with export bit0 clear selects native multipart mode.
            chunks.Add((0xcccc0015, 1, 30, new byte[180]));
        }
        return CgfTestAssets.File(0x745, chunks);
    }

    private static byte[] Material(string name)
    {
        var bytes = new byte[424];
        Encoding.ASCII.GetBytes(name).CopyTo(bytes, 24);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(152), 4096);
        return bytes;
    }

    private static byte[] Physics(Vector3 center)
    {
        var blob = CgfTestAssets.BoxBlob(center, new(.2f, .2f, .1f), 0);
        var bytes = new byte[40 + blob.Length];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), blob.Length);
        blob.CopyTo(bytes, 40);
        return bytes;
    }

    private static ZoneManagedSceneCollision Scene(ManagedSceneActivationTests.Fixture fixture, params float[] heights)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(1); writer.Write(0);
            var name = new byte[256]; Encoding.UTF8.GetBytes(Model).CopyTo(name, 0); writer.Write(name);
            writer.Write(0);
            foreach (var height in heights)
            {
                writer.Write((int)ZoneCellObjectType.Brush);
                var bytes = new byte[128];
                // Authored render bounds are deliberately huge and must never admit these parts.
                CgfTestAssets.Vector(bytes, 0, new(-100)); CgfTestAssets.Vector(bytes, 12, new(100));
                float[] transform = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, height];
                for (var i = 0; i < transform.Length; i++)
                    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x43 + i * 4), BitConverter.SingleToInt32Bits(transform[i]));
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x73), -1);
                writer.Write(bytes);
            }
        }
        var objects = ZoneCellObjectContent.Read(stream.ToArray(), ZoneCellObjectFileKind.BigObjects);
        var cell = new ZoneManagedSceneCellLoadReport(fixture.Layout.Cells[0], new(ZoneTerrainCellLoadStatus.Loaded),
            new(objects, null, null, null, []));
        return new(fixture.Assets, ZoneTerrainMaterialResolver.Load(fixture.Assets), [cell]);
    }
}
