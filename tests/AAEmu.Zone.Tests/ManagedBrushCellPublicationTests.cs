using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using AAEmu.Zone.Content;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Tests;

public sealed class ManagedBrushCellPublicationTests
{
    private const string Model = "game/objects/wall.cgf";

    [Test]
    public async Task CurrentMasksFollowOwnedHandlesInsteadOfHistoricalCountsOrForeignGeometry()
    {
        var (fixture, cell) = Cell();
        using var collision = new ZoneManagedSceneCollision(fixture.Assets,
            ZoneTerrainMaterialResolver.Load(fixture.Assets), [cell]);
        var initial = collision.CopyBrushCell(cell);
        await Assert.That(initial.IsCompleteFor(ZoneManagedCollisionQueryMask.CharactersAndRays)).IsTrue();
        await Assert.That(initial.CharacterInstances).IsEqualTo(1);
        await Assert.That(collision.IsCurrent(initial)).IsTrue();
        collision.Geometry.Clear();
        collision.Geometry.AddInstance(new ZoneManagedPrimitiveGeometry(ZoneManagedPrimitiveKind.Sphere,
            Vector3.Zero, Vector3.UnitZ, 1, 0, 99), Matrix4x4.Identity);
        var changed = collision.CopyBrushCell(cell);
        await Assert.That(collision.Report.BrushCoverageComplete).IsTrue(); //Historical resource admission still passed.
        await Assert.That(collision.Geometry.InstanceCount).IsEqualTo(1); //A foreign instance cannot replace the owned one.
        await Assert.That(collision.IsCurrent(initial)).IsFalse();
        await Assert.That(changed.IsCompleteFor(ZoneManagedCollisionQueryMask.Characters)).IsFalse();
        await Assert.That(changed.IsCompleteFor(ZoneManagedCollisionQueryMask.Rays)).IsTrue();
        await Assert.That(changed.CharacterInstances).IsEqualTo(0);
        await Assert.That(changed.RayInstances).IsEqualTo(1);
    }

    [Test]
    public async Task ExactInputAndCollisionOwnerIdentityCannotBeReplacedByEquivalentRecords()
    {
        var (fixture, cell) = Cell();
        var materials = ZoneTerrainMaterialResolver.Load(fixture.Assets);
        using var collision = new ZoneManagedSceneCollision(fixture.Assets, materials, [cell]);
        using var other = new ZoneManagedSceneCollision(fixture.Assets, materials, [cell]);
        var snapshot = collision.CopyBrushCell(cell);
        var equivalent = new ZoneManagedSceneCellLoadReport(cell.Placement, cell.Terrain, cell.Objects);
        await Assert.That(collision.CopyBrushCell(equivalent).IsCompleteFor(ZoneManagedCollisionQueryMask.CharactersAndRays)).IsFalse();
        await Assert.That(other.IsCurrent(snapshot)).IsFalse();
        await Assert.That(collision.IsCurrent(snapshot)).IsTrue();
    }

    [Test]
    public async Task BodylessCellRetirementAdvancesCoverageEvenWhenGeometryDoesNotChange()
    {
        var (fixture, cell) = Cell(empty: true);
        using var collision = new ZoneManagedSceneCollision(fixture.Assets,
            ZoneTerrainMaterialResolver.Load(fixture.Assets), [cell]);
        var snapshot = collision.CopyBrushCell(cell);
        var characters = collision.Geometry.PublicationRevision; var rays = collision.RayGeometry.PublicationRevision;
        await Assert.That(snapshot.IsCompleteFor(ZoneManagedCollisionQueryMask.CharactersAndRays)).IsTrue();
        await Assert.That(collision.RetireBrushCell(cell)).IsTrue();
        await Assert.That(collision.Geometry.PublicationRevision).IsEqualTo(characters);
        await Assert.That(collision.RayGeometry.PublicationRevision).IsEqualTo(rays);
        await Assert.That(collision.IsCurrent(snapshot)).IsFalse();
        await Assert.That(collision.CopyBrushCell(cell).IsCompleteFor(ZoneManagedCollisionQueryMask.CharactersAndRays)).IsFalse();
        await Assert.That(collision.RetireBrushCell(cell)).IsFalse();
    }

    [Test]
    public async Task CellRetirementRemovesItsBothIndicesAndPreservesOtherCellsAndForeignInstances()
    {
        var fixture = new ManagedSceneActivationTests.Fixture((9, 14), (10, 14));
        Install(fixture);
        var first = Input(fixture.Layout.Cells[0]); var second = Input(fixture.Layout.Cells[1]);
        using var collision = new ZoneManagedSceneCollision(fixture.Assets,
            ZoneTerrainMaterialResolver.Load(fixture.Assets), [first, second]);
        var foreign = collision.Geometry.AddInstance(new ZoneManagedPrimitiveGeometry(ZoneManagedPrimitiveKind.Sphere,
            Vector3.Zero, Vector3.UnitZ, 1, 0, 99), Matrix4x4.CreateTranslation(200, 200, 100));
        await Assert.That(collision.RetireBrushCell(first)).IsTrue();
        await Assert.That(collision.Geometry.InstanceCount).IsEqualTo(2);
        await Assert.That(collision.RayGeometry.InstanceCount).IsEqualTo(1);
        await Assert.That(collision.Geometry.Contains(foreign)).IsTrue();
        await Assert.That(collision.CopyBrushCell(second).IsCompleteFor(ZoneManagedCollisionQueryMask.CharactersAndRays)).IsTrue();
        await Assert.That(collision.CopyBrushCell(first).IsCompleteFor(ZoneManagedCollisionQueryMask.CharactersAndRays)).IsFalse();
    }

    [Test]
    public async Task UnreadOptionalInputAndDuplicatedSourceCannotCertifyAnEmptyOrCompleteBrushInventory()
    {
        var (fixture, cell) = Cell();
        var unread = cell with { Objects = cell.Objects with { Diagnostics = [] } };
        var materials = ZoneTerrainMaterialResolver.Load(fixture.Assets);
        using var collision = new ZoneManagedSceneCollision(fixture.Assets, materials, [unread]);
        await Assert.That(collision.CopyBrushCell(unread).IsCompleteFor(ZoneManagedCollisionQueryMask.CharactersAndRays)).IsFalse();
        using var duplicate = new ZoneManagedSceneCollision(fixture.Assets, materials, [cell, cell]);
        await Assert.That(duplicate.CopyBrushCell(cell).IsCompleteFor(ZoneManagedCollisionQueryMask.CharactersAndRays)).IsFalse();
    }

    private static (ManagedSceneActivationTests.Fixture Fixture, ZoneManagedSceneCellLoadReport Cell) Cell(bool empty = false)
    {
        var fixture = new ManagedSceneActivationTests.Fixture((9, 14)); Install(fixture);
        return (fixture, Input(fixture.Layout.Cells[0], empty));
    }
    private static void Install(ManagedSceneActivationTests.Fixture fixture)
    {
        fixture.Assets.Files[Model] = CgfTestAssets.BoxCgf();
        fixture.Text("game/objects/wall_material.mtl", "<Material SurfaceType='mat_stone'/>");
    }
    private static ZoneManagedSceneCellLoadReport Input(ZoneWorldMapCellPlacement placement, bool empty = false)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(1); writer.Write(0); var name = new byte[256]; Encoding.UTF8.GetBytes(Model).CopyTo(name, 0);
            writer.Write(name); writer.Write(0);
            if (!empty)
            {
                writer.Write((uint)ZoneCellObjectType.Brush); var payload = new byte[128];
                CgfTestAssets.Vector(payload, 0, new(-10)); CgfTestAssets.Vector(payload, 12, new(10));
                float[] matrix = [1, 0, 0, 10, 0, 1, 0, 20, 0, 0, 1, 100];
                for (var index = 0; index < matrix.Length; index++)
                    BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(0x43 + index * 4), matrix[index]);
                BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0x73), -1); writer.Write(payload);
            }
        }
        var content = ZoneCellObjectContent.Read(stream.ToArray(), ZoneCellObjectFileKind.BigObjects);
        return new(placement, new(ZoneTerrainCellLoadStatus.Loaded), new(null, null, null, content,
            ["Objects: authored asset is absent.", "Vegetation: authored asset is absent.", "Entities: authored asset is absent."]));
    }
}
