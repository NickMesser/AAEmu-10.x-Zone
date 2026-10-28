using System.Numerics;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Tests;

public sealed class ManagedSupportContactTests
{
    [Test]
    public async Task ContactQueriesSelectTheInteriorFloorWithoutAdoptingTerrainOverhead()
    {
        var terrain = new ZoneTerrainHeightField();
        terrain.LoadCell(0, 0, TerrainHeightFieldTests.CreateCell(10, 0));
        var statics = new ZoneManagedStaticGeometry();
        var floor = statics.AddInstance(Floor(0), Matrix4x4.Identity);
        var support = new ZoneManagedSceneGroundQuery(new(terrain), statics);
        await Assert.That(support.TrySupportContact(new(20, 30, 0), out var hit, out var fromStatic)).IsTrue();
        await Assert.That(fromStatic).IsTrue();
        await Assert.That(MathF.Abs(hit.Position.Z)).IsLessThan(.0001f);
        await Assert.That(hit.Normal).IsEqualTo(Vector3.UnitZ);
        await Assert.That(hit.PhysicalMaterialId).IsEqualTo((ushort)37);
        await Assert.That(support.TrySupportContact(new(20, 30, 1), out _, out _)).IsFalse();
        statics.RemoveInstance(floor);
        await Assert.That(support.TrySupportContact(new(20, 30, 0), out _, out _)).IsFalse();
    }

    [Test]
    public async Task TerrainSupportIsCopiedAndDisappearsWithItsLoadedCell()
    {
        var terrain = new ZoneTerrainHeightField();
        terrain.LoadCell(0, 0, TerrainHeightFieldTests.CreateCell(0, 0));
        var support = new ZoneManagedSceneGroundQuery(new(terrain));
        await Assert.That(support.TrySupportContact(new(20, 30, 0), out var hit, out var fromStatic)).IsTrue();
        await Assert.That(fromStatic).IsFalse();
        await Assert.That(hit.Position).IsEqualTo(new Vector3(20, 30, 0));
        terrain.UnloadCell(0, 0);
        await Assert.That(support.TrySupportContact(new(20, 30, 0), out _, out _)).IsFalse();
        await Assert.That(hit.Position).IsEqualTo(new Vector3(20, 30, 0));
    }

    private static ZoneManagedTriangleMesh Floor(float height) => new(
        [new(0, 0, height), new(100, 0, height), new(0, 100, height)], [0, 1, 2], [37]);
}
