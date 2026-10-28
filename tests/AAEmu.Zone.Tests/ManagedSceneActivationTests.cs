using System.Buffers.Binary;
using System.Numerics;
using System.Text;

using AAEmu.Zone.Content;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Tests;

public sealed class ManagedSceneActivationTests
{
    [Test]
    public async Task AuthoredWorldFilesLoadAtZoneLocalCoordinatesAndKeepDecodedEntityPlacements()
    {
        var fixture = new Fixture((9, 14), (10, 14));
        fixture.Cell(9, 14, Heightmap(24, 100));
        fixture.Cell(10, 14, Heightmap(24, 300));
        fixture.Text("game/worlds/authored/cells/010_014/client/entities.xml",
            "<Entities><Objects><Entity Name='Tree' EntityClass='BasicEntity' Pos='2,3,4' Geometry='game/tree.cgf' /></Objects></Entities>");
        using var scene = new ZoneManagedSceneActivation(fixture.Layout, fixture.Assets);
        scene.Create();
        var report = scene.Load();

        await Assert.That(report.TerrainReady).IsTrue();
        await Assert.That(report.TerrainLoadedCells).IsEqualTo(2);
        await Assert.That(scene.Heights.GetHeight(100, 100)).IsEqualTo(132f);
        await Assert.That(scene.Heights.GetHeight(1124, 100)).IsEqualTo(332f);
        await Assert.That(scene.Heights.GetHeight(1023, 100)).IsEqualTo(228f);
        await Assert.That(scene.Heights.Nodes!.OceanWaterLevel).IsEqualTo(80f);
        await Assert.That(scene.Heights.Nodes.GetCellRoot(1, 0).Bounds.Min.X).IsEqualTo(1024f);
        var row = report.Cells[1].Objects.Entities!.Entities.Single();
        await Assert.That(row.GetNativePosition(report.Cells[1].Placement.EngineCell)).IsEqualTo(new Vector3(1026, 3, 4));
        await Assert.That(row.Geometry).IsEqualTo("game/tree.cgf");
        await Assert.That(report.FullSceneCollisionReady).IsFalse();
        await Assert.That(report.UnsupportedCapabilities.Count).IsGreaterThan(0);
        await Assert.That(fixture.Assets.Opened).IsEqualTo(fixture.Assets.Closed);

        scene.Dispose();
        await Assert.That(scene.Heights.GetHeight(100, 100)).IsEqualTo(0f);
        await Assert.That(scene.Heights.GetHeight(1124, 100)).IsEqualTo(0f);
        // Asset-source ownership stays with the caller after scene retirement.
        using var stillAvailable = fixture.Assets.OpenRead("game/worlds/authored/surface.xml");
        await Assert.That(stillAvailable is not null).IsTrue();
    }

    [Test]
    public async Task FailedCellRetainsItsDiagnosticAndNeverCreatesAFloorWhileOtherCellsStillLoad()
    {
        var fixture = new Fixture((9, 14), (10, 14));
        fixture.Cell(9, 14, [24, 0, 0, 0]);
        fixture.Cell(10, 14, Heightmap(24, 300));
        using var scene = new ZoneManagedSceneActivation(fixture.Layout, fixture.Assets);
        scene.Create();
        var report = scene.Load();

        await Assert.That(report.TerrainReady).IsFalse();
        await Assert.That(report.Cells[0].Terrain.Status).IsEqualTo(ZoneTerrainCellLoadStatus.Failed);
        await Assert.That(string.IsNullOrWhiteSpace(report.Cells[0].Terrain.Error)).IsFalse();
        await Assert.That(report.Cells[1].Terrain.Status).IsEqualTo(ZoneTerrainCellLoadStatus.Loaded);
        await Assert.That(scene.Heights.GetHeight(100, 100)).IsEqualTo(0f);
        await Assert.That(scene.Heights.GetHeight(1124, 100)).IsEqualTo(332f);
        await Assert.That(fixture.Assets.Opened).IsEqualTo(fixture.Assets.Closed);
    }

    [Test]
    public async Task Version24OverridesXmlSurfaceSlotsAndVersion23PreservesThem()
    {
        foreach (var version in new uint[] { 23, 24 })
        {
            var fixture = new Fixture((9, 14));
            fixture.Cell(9, 14, Heightmap(version, 100, worldSurfaceId: 2));
            using var scene = new ZoneManagedSceneActivation(fixture.Layout, fixture.Assets);
            scene.Create();
            var report = scene.Load();
            await Assert.That(report.TerrainReady).IsTrue();
            await Assert.That(scene.Surfaces!.GetBinding(0, 0, 0).Surface.Id).IsEqualTo(version == 23 ? 1 : 2);
            await Assert.That(scene.Surfaces.CreatePhysicalMaterialMap()[0]).IsEqualTo(version == 23 ? 100 : 101);
            await Assert.That(scene.SurfaceContent!.Catalog.Resolve("mat_grass").Id).IsEqualTo(100);
            await Assert.That(scene.SurfaceContent.Registrations.Count).IsEqualTo(2);
        }
    }

    [Test]
    public async Task EmptyChunkIsAnAuthoredEmptyCellAndMalformedOptionalObjectsStayVisible()
    {
        var fixture = new Fixture((9, 14));
        fixture.Cell(9, 14, Heightmap(24, 0, empty: true));
        fixture.Assets.Files["game/worlds/authored/cells/009_014/client/object.dat"] = [1, 2, 3];
        using var scene = new ZoneManagedSceneActivation(fixture.Layout, fixture.Assets);
        scene.Create();
        var report = scene.Load();

        await Assert.That(report.TerrainReady).IsTrue();
        await Assert.That(report.EmptyTerrainCells).IsEqualTo(1);
        await Assert.That(report.TerrainLoadedCells).IsEqualTo(0);
        await Assert.That(scene.Heights.GetHeight(100, 100)).IsEqualTo(0f);
        await Assert.That(report.Cells[0].Objects.Diagnostics.Any(message => message.StartsWith("Objects:", StringComparison.Ordinal))).IsTrue();
        await Assert.That(() => scene.Load()).Throws<InvalidOperationException>();
        scene.Dispose();
        await Assert.That(() => scene.Create()).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task ACellOutsideTheLocalWindowCannotAliasSlotZero()
    {
        var fixture = new Fixture((1, 1), (10, 1));
        fixture.Cell(1, 1, Heightmap(24, 100));
        fixture.Cell(10, 1, Heightmap(24, 300));
        using var scene = new ZoneManagedSceneActivation(fixture.Layout, fixture.Assets);
        scene.Create();
        var report = scene.Load();

        await Assert.That(report.TerrainReady).IsFalse();
        await Assert.That(report.Cells[1].Terrain.Status).IsEqualTo(ZoneTerrainCellLoadStatus.InvalidCell);
        await Assert.That(scene.Heights.GetHeight(100, 100)).IsEqualTo(132f);
    }

    internal static byte[] Heightmap(uint version, float offset, int worldSurfaceId = 1, bool empty = false)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach (var value in new uint[] { version, 0, 4096, 2, 64, 128 }) writer.Write(value);
        writer.Write(.0625f);
        writer.Write(80f);
        if (version == 24)
            for (var slot = 0; slot < 32; slot++) writer.Write(worldSurfaceId);
        if (!empty) WriteNode(0, 0, 1024);
        var bytes = stream.ToArray();
        if (!empty) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)bytes.Length);
        return bytes;

        void WriteNode(int x, int y, int size)
        {
            writer.Write(5u);
            foreach (var value in new float[] { x, y, 0, x + size, y + size, 4096 }) writer.Write(value);
            writer.Write((byte)0);
            writer.Write(offset);
            writer.Write(1f);
            var width = size == 64 ? 33 : 0;
            writer.Write(width);
            writer.Write(0);
            for (var sample = 0; sample < width * width; sample++) writer.Write((ushort)32);
            writer.Write(new byte[56]);
            if (size == 64) return;
            var half = size / 2;
            WriteNode(x, y, half);
            WriteNode(x + half, y, half);
            WriteNode(x, y + half, half);
            WriteNode(x + half, y + half, half);
        }
    }

    internal sealed class Fixture
    {
        public MemoryAssets Assets { get; } = new();
        public ZoneWorldMapLayout Layout { get; }

        public Fixture(params (int X, int Y)[] cells)
        {
            var cellXml = string.Concat(cells.Select(cell => $"<Cell x='{cell.X}' y='{cell.Y}' />"));
            var world = ZoneWorldMapContent.Read("<World name='authored' resolution='1024' unitSize='2' " +
                "cellXCount='20' cellYCount='20' oceanLevel='80'><ZoneList><Zone id='129' name='test'>" +
                $"<cellList>{cellXml}</cellList></Zone></ZoneList></World>", "requested_alias");
            Layout = new(world, world.Zones.Single());
            Text("game/worlds/authored/surface.xml", "<Surface><SurfaceTypes>" +
                "<SurfaceType Id='1' Name='grass' DetailMaterial='game/terrain/grass' />" +
                "<SurfaceType Id='2' Name='stone' DetailMaterial='game/terrain/stone' /></SurfaceTypes></Surface>");
            Text("game/terrain/grass.mtl", "<Material SurfaceType='mat_grass' />");
            Text("game/terrain/stone.mtl", "<Material SurfaceType='mat_stone' />");
            Text("game/libs/materialeffects/surfacetypes.xml", "<SurfaceTypes>" +
                "<SurfaceType name='mat_grass'><Physics friction='0.7' /></SurfaceType>" +
                "<SurfaceType name='mat_stone'><Physics friction='0.9' /></SurfaceType></SurfaceTypes>");
        }

        public void Text(string path, string content) => Assets.Files[path] = Encoding.UTF8.GetBytes(content);

        public void Cell(int x, int y, byte[] heightmap)
        {
            var root = $"game/worlds/authored/cells/{x:000}_{y:000}/client/";
            Assets.Files[root + "terrain/heightmap.dat"] = heightmap;
            Text(root + "leveldata.xml", "<LevelData><SurfaceTypes><SurfaceType Id='1' /></SurfaceTypes></LevelData>");
        }
    }

    internal sealed class MemoryAssets : IZoneAssetSource
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public int Opened { get; private set; }
        public int Closed { get; private set; }

        public Stream? OpenRead(string virtualPath)
        {
            if (!Files.TryGetValue(virtualPath, out var bytes)) return null;
            Opened++;
            return new AssetStream(bytes, () => Closed++);
        }

        private sealed class AssetStream(byte[] bytes, Action closed) : MemoryStream(bytes, false)
        {
            private bool _closed;
            protected override void Dispose(bool disposing)
            {
                if (disposing && !_closed) { _closed = true; closed(); }
                base.Dispose(disposing);
            }
        }
    }
}
