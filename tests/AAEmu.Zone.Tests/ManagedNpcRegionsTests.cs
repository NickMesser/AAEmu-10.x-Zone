using System.Globalization;
using System.Numerics;
using System.Text.Json;
using AAEmu.Zone.Content;
using AAEmu.Zone.Scene;

namespace AAEmu.Zone.Tests;

public sealed class ManagedNpcRegionsTests
{
    [Test]
    public async Task OriginalZone129SectorCentersAndBoundaryInstructionsMatchCanonicalWorldRows()
    {
        using var xml = Resource("npc-region-zone129.xml");
        var world = ZoneWorldMapContent.Load(xml, "main_world");
        using var data = Resource("npc-region-native-controls.json");
        using var native = await JsonDocument.ParseAsync(data);
        var root = native.RootElement;
        var layout = new ZoneWorldMapLayout(world, world.Zones[root.GetProperty("selectedSourceIndex").GetInt32()]);
        using var regions = new ZoneManagedNpcRegions(layout);
        await Assert.That(world.Zones.Count).IsEqualTo(164);
        await Assert.That(layout.Cells.Count).IsEqualTo(6);
        await Assert.That(ReferenceEquals(regions.CurrentRegion, world.Zones[root.GetProperty("nativeCurrentRegionSourceIndex").GetInt32()])).IsTrue();
        var checkedCenters = 0; var currentSectors = 0;
        foreach (var cell in root.GetProperty("cells").EnumerateArray())
        {
            var x = cell.GetProperty("x").GetInt32(); var y = cell.GetProperty("y").GetInt32();
            var expected = cell.GetProperty("nativeOwnerSourceIndices");
            for (var sx = 0; sx < 16; sx++)
                for (var sy = 0; sy < 16; sy++)
                {
                    var actual = regions.GetRegion(new((x - layout.Origin.X) * 1024 + sx * 64 + 32,
                        (y - layout.Origin.Y) * 1024 + sy * 64 + 32, 23));
                    var index = expected[sx * 16 + sy];
                    await Assert.That(ReferenceEquals(actual, index.ValueKind == JsonValueKind.Null ? null : world.Zones[index.GetInt32()])).IsTrue();
                    if (ReferenceEquals(actual, regions.CurrentRegion)) currentSectors++;
                    checkedCenters++;
                }
        }
        var checkedBoundaries = 0;
        foreach (var point in root.GetProperty("points").EnumerateArray())
        {
            var bits = point.GetProperty("positionBits");
            var position = new Vector3(Single(bits[0]), Single(bits[1]), Single(bits[2]));
            var actual = regions.GetRegion(position);
            var index = point.GetProperty("nativeOwnerSourceIndex");
            await Assert.That(ReferenceEquals(actual, index.ValueKind == JsonValueKind.Null ? null : world.Zones[index.GetInt32()])).IsTrue();
            checkedBoundaries++;
        }
        await Assert.That(checkedCenters).IsEqualTo(1536);
        await Assert.That(currentSectors).IsEqualTo(469);
        await Assert.That(checkedBoundaries).IsEqualTo(35);
        //The selected Zone loads six cells. Its neighbor at12,14 is not one of them,
        //but original Seamless queries the full World grid and still resolves that owner.
        await Assert.That(layout.Cells.Any(cell => cell.Cell.Coordinate == new ZoneMapCoordinate(12, 14))).IsFalse();
        await Assert.That(regions.GetRegion(new(3072, 32, 23))).IsNotNull();
        await Assert.That(ReferenceEquals(regions.GetRegion(new(3072, 32, 23)), regions.CurrentRegion)).IsFalse();
    }

    [Test]
    public async Task SparseSectorsAndLastAssignmentsDoNotBecomeBoundingRectanglesOrDefaultOwners()
    {
        var world = ZoneWorldMapContent.Read("""
            <World name='sector_test' resolution='256' unitSize='7' cellXCount='4' cellYCount='4'>
              <ZoneList>
                <Zone id='129' name='same' originX='1' originY='1'><cellList>
                  <Cell x='1' y='1'><sectorList><s x='0' y='0'/><s x='1' y='0'/></sectorList></Cell>
                  <Cell x='2' y='1'><sectorList/></Cell>
                </cellList></Zone>
                <Zone id='129' name='same'><cellList>
                  <Cell x='1' y='1'><sectorList><s x='1' y='0'/><s x='16' y='0'/></sectorList></Cell>
                  <Cell x='3' y='3'><sectorList><s x='0' y='0'/></sectorList></Cell>
                </cellList></Zone>
              </ZoneList>
            </World>
            """, "sector_test");
        using var regions = new ZoneManagedNpcRegions(new(world, world.Zones[0]));
        await Assert.That(ReferenceEquals(regions.GetRegion(new(32, 32, -100000)), world.Zones[0])).IsTrue();
        await Assert.That(ReferenceEquals(regions.GetRegion(new(64, 32, 100000)), world.Zones[1])).IsTrue();
        await Assert.That(ReferenceEquals(regions.CurrentRegion, world.Zones[0])).IsTrue();
        await Assert.That(ReferenceEquals(regions.GetRegion(new(2048, 2048, 0)), world.Zones[1])).IsTrue();
        await Assert.That(regions.GetRegion(new(128, 32, 0))).IsNull(); //Unassigned sector in a loaded cell.
        await Assert.That(regions.GetRegion(new(1024, 0, 0))).IsNull(); //Explicit empty cell reference.
        await Assert.That(regions.GetRegion(new(0, 1024, 0))).IsNull(); //Cell absent from all Zone rows.
        await Assert.That(regions.GetRegion(new(4096, 0, 0))).IsNull(); //Beyond the complete World grid.
        await Assert.That(regions.GetRegion(new(-2048, 0, 0))).IsNull();
    }

    [Test]
    public async Task NativeTruncationBelowTheOriginAndSceneRetirementHaveExplicitBoundaries()
    {
        var world = ZoneWorldMapContent.Read("""
            <World name='negative' resolution='1024' unitSize='2' cellXCount='2' cellYCount='2'>
              <ZoneList><Zone id='1' name='one'><cellList><Cell x='1' y='1'><sectorList>
                <s x='0' y='0'/>
              </sectorList></Cell></cellList></Zone></ZoneList>
            </World>
            """, "negative");
        var regions = new ZoneManagedNpcRegions(new(world, world.Zones[0]));
        await Assert.That(ReferenceEquals(regions.GetRegion(new(-63.999996f, -.01f, float.NaN)), regions.CurrentRegion)).IsTrue();
        await Assert.That(regions.GetRegion(new(-64, 0, 0))).IsNull();
        await Assert.That(regions.GetRegion(new(64, 0, 0))).IsNull();
        await Assert.That(regions.GetRegion(new(float.NaN, 0, 0))).IsNull();
        await Assert.That(regions.GetRegion(new(0, float.NegativeInfinity, 0))).IsNull();
        await Assert.That(regions.GetRegion(new(float.MaxValue, 0, 0))).IsNull();
        regions.Dispose(); regions.Dispose();
        await Assert.That(() => regions.GetRegion(Vector3.Zero)).Throws<ObjectDisposedException>();
        await Assert.That(() => regions.CurrentRegion).Throws<ObjectDisposedException>();
        using var replacement = new ZoneManagedNpcRegions(new(world, world.Zones[0]));
        await Assert.That(ReferenceEquals(replacement.GetRegion(Vector3.Zero), world.Zones[0])).IsTrue();
        await Assert.That(() => new ZoneManagedNpcRegions(null!)).Throws<ArgumentNullException>();
    }

    private static Stream Resource(string filename) => System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(
        "AAEmu.Zone.Tests.Fixtures." + filename) ?? throw new InvalidOperationException("Region fixture was not embedded.");
    private static float Single(JsonElement bits) => BitConverter.Int32BitsToSingle(unchecked((int)uint.Parse(
        bits.GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
}
