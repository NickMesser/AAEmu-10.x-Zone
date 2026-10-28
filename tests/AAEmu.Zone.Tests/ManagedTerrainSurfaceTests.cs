using System.Numerics;
using AAEmu.Zone.Content;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Tests;

public sealed class ManagedTerrainSurfaceTests
{
    private static readonly ZonePhysicsShape[] Shapes =
    [ZonePhysicsShape.Box(new(.4f, .3f, .7f)), ZonePhysicsShape.Capsule(.4f, .3f), ZonePhysicsShape.Cylinder(.4f, .7f)];

    [Test]
    public async Task AllShapesIntersectTheSurfaceWithoutFillingTheVolumeBelowIt()
    {
        var terrain = Flat(out _);
        foreach (var shape in Shapes)
        {
            var halfHeight = shape.BoundingHalfExtents.Z;
            foreach (var z in new[] { -.1f, 0, .1f, -halfHeight, halfHeight })
                await Assert.That(terrain.TryCollide(shape, new(8000.25f, 8000.5f, z))).IsTrue();
            foreach (var z in new[] { -10f, 10f })
                await Assert.That(terrain.TryCollide(shape, new(8000.25f, 8000.5f, z))).IsFalse();
            await Assert.That(terrain.TrySweep(shape, new(8000.25f, 8000.5f, -10), new(3, 0, 0), out _)).IsFalse();
        }
    }

    [Test]
    public async Task SweepsCrossTheTerrainFromBothSidesWithContactFacingNormals()
    {
        var terrain = Flat(out _);
        foreach (var shape in Shapes)
            foreach (var side in new[] { -1, 1 })
            {
                var origin = new Vector3(8000.25f, 8000.5f, side * 3);
                await Assert.That(terrain.TrySweep(shape, origin, new(0, 0, -side * 6), out var hit)).IsTrue();
                await Near(hit.Distance, 3 - shape.BoundingHalfExtents.Z, .001f);
                await Near(hit.Position.Z, 0, .001f);
                await Near(hit.Normal, Vector3.UnitZ * side);
                await Assert.That(terrain.TrySweep(shape, origin, new(0, 0, side * 6), out _)).IsFalse();
            }
    }

    [Test]
    public async Task TouchingShapesCanLeaveEitherSideAndTravelTangentially()
    {
        var terrain = Flat(out _);
        foreach (var shape in Shapes)
            foreach (var side in new[] { -1, 1 })
            {
                var origin = new Vector3(8000.25f, 8000.5f, side * shape.BoundingHalfExtents.Z);
                await Assert.That(terrain.TrySweep(shape, origin, Vector3.UnitZ * side, out _)).IsFalse();
                await Assert.That(terrain.TrySweep(shape, origin, new(4, 0, 0), out _)).IsFalse();
                await Assert.That(terrain.TrySweep(shape, origin, -Vector3.UnitZ * side, out var hit)).IsTrue();
                await Near(hit.Distance, 0, .001f);
                await Near(hit.Normal, Vector3.UnitZ * side);
            }
    }

    [Test]
    public async Task SurfaceOnlyQueriesKeepHolesAndUnloadAuthoritative()
    {
        var terrain = Flat(out var heights);
        foreach (var shape in Shapes)
            await Assert.That(terrain.TryCollide(shape, new(8000, 8000, 0))).IsTrue();
        heights.UnloadCell(7, 7);
        await AssertClear();
        heights.LoadCell(7, 7, TerrainHeightFieldTests.CreateCell(0, 0, sample: (_, _, _, _) => 31));
        await AssertClear();

        async Task AssertClear()
        {
            foreach (var shape in Shapes)
            {
                await Assert.That(terrain.TryCollide(shape, new(8000, 8000, 0))).IsFalse();
                await Assert.That(terrain.TrySweep(shape, new(8000, 8000, -3), new(0, 0, 6), out _)).IsFalse();
                await Assert.That(terrain.GetPenetrations(shape, new(8000, 8000, 0)).Count).IsEqualTo(0);
            }
        }
    }

    [Test]
    public async Task PhysicsQuadSelectsEachGlobalVertexLeafAndTheLowerLeftMaterial()
    {
        var heights = new ZoneTerrainHeightField();
        heights.LoadCell(1, 1, TerrainHeightFieldTests.CreateCell(0, 1, width: 2,
            sample: (x, y, _, _) => (ushort)((x, y) switch
            {
                (64, 64) => 32 | 7, (128, 64) => 64 | 3, (64, 128) => 96 | 4, (128, 128) => 128 | 5, _ => 32,
            })));
        await Assert.That(heights.TryReadManagedPhysicsQuad(575, 575, out var quad, out var surface)).IsTrue();
        await Assert.That(quad).IsEqualTo(new Vector4(32, 64, 96, 128));
        await Assert.That(surface).IsEqualTo((1 * 8 + 1) * 32 + 7);
        var terrain = new ZoneManagedTerrainGeometry(heights);
        await Assert.That(terrain.RaycastTerrain(new(1150.5f, 1150.5f, 200), new(0, 0, -200), out var hit)).IsTrue();
        await Near(hit.Position.Z, 56, .001f);
        await Near(hit.Normal, Vector3.Normalize(new(-16, -32, 1)));
        // The renderer still expands the selected coarse quad, preserving its independent API.
        await Near(heights.GetHeight(1150.5f, 1150.5f), 32, .001f);
    }

    [Test]
    public async Task PhysicsVerticesUseCurrentNeighborSamplesAndGlobalOuterClamp()
    {
        var heights = new ZoneTerrainHeightField();
        heights.LoadCell(1, 1, TerrainHeightFieldTests.CreateCell(100, 1));
        heights.LoadCell(2, 1, TerrainHeightFieldTests.CreateCell(300, 1));
        await Assert.That(heights.TryReadManagedPhysicsQuad(1023, 550, out var quad, out _)).IsTrue();
        await Assert.That(quad).IsEqualTo(new Vector4(132, 332, 132, 332));
        heights.UnloadCell(2, 1);
        await Assert.That(heights.TryReadManagedPhysicsQuad(1023, 550, out quad, out _)).IsTrue();
        await Assert.That(quad).IsEqualTo(new Vector4(132, 0, 132, 0));
        await Assert.That(heights.TryReadManagedPhysicsQuad(1024, 550, out _, out _)).IsFalse();
        heights.LoadCell(7, 7, TerrainHeightFieldTests.CreateCell(0, 1,
            sample: (_, _, sx, sy) => sx == 32 || sy == 32 ? (ushort)640 : (ushort)32));
        await Assert.That(heights.TryReadManagedPhysicsQuad(4095, 4095, out quad, out _)).IsTrue();
        await Assert.That(quad).IsEqualTo(new Vector4(32));
    }

    [Test]
    public async Task ShapeMaterialsUseSignedTriangleBytesWhileRaysRetainTheFullIndex()
    {
        var heights = new ZoneTerrainHeightField();
        var content = TerrainHeightFieldTests.CreateCell(0, 0, sample: (_, _, _, _) => 2);
        heights.LoadCell(2, 0, content); heights.LoadCell(0, 4, content);
        var bindings = new ZoneTerrainSurfaceBindings(ZoneTerrainSurfaceContent.Read(
            "<World><SurfaceTypes><Surface Id='1' DetailMaterial='full'/><Surface Id='2' DetailMaterial='alias'/><Surface Id='3' DetailMaterial='fallback'/></SurfaceTypes></World>"), new Materials());
        bindings.ApplyCellSurfaceTypes(0, 0, ZoneTerrainCellSurfaceContent.Read(
            "<Level><SurfaceTypes><Surface Id='3'/><Surface Id='0'/><Surface Id='2'/></SurfaceTypes></Level>"));
        var fullBinding = ZoneTerrainCellSurfaceContent.Read(
            "<Level><SurfaceTypes><Surface Id='0'/><Surface Id='0'/><Surface Id='1'/></SurfaceTypes></Level>");
        bindings.ApplyCellSurfaceTypes(2, 0, fullBinding); bindings.ApplyCellSurfaceTypes(0, 4, fullBinding);
        var terrain = new ZoneManagedTerrainGeometry(heights, bindings);
        // 514 truncates to signed byte 2; 130 truncates to -126 and selects default slot 0.
        // The latter branch follows the recovered source; the real 54-case oracle covers 513→1.
        foreach (var (center, shapeMaterial) in new[] { (new Vector3(2560, 512, 0), (ushort)11), (new Vector3(512, 4608, 0), (ushort)23) })
        {
            await Assert.That(terrain.RaycastTerrain(center + Vector3.UnitZ * 3, -Vector3.UnitZ * 6, out var ray)).IsTrue();
            await Assert.That(ray.PhysicalMaterialId).IsEqualTo((ushort)37);
            foreach (var shape in Shapes)
            {
                foreach (var side in new[] { -1, 1 })
                {
                    await Assert.That(terrain.TrySweep(shape, center + Vector3.UnitZ * (side * 3), Vector3.UnitZ * (-side * 6), out var hit)).IsTrue();
                    await Assert.That(hit.PhysicalMaterialId).IsEqualTo(shapeMaterial);
                }
                var contacts = terrain.GetPenetrations(shape, center);
                await Assert.That(contacts.Count).IsGreaterThan(0);
                await Assert.That(contacts.All(contact => contact.PhysicalMaterialId == shapeMaterial)).IsTrue();
            }
        }
    }

    private static ZoneManagedTerrainGeometry Flat(out ZoneTerrainHeightField heights)
    {
        heights = new(); heights.LoadCell(7, 7, TerrainHeightFieldTests.CreateCell(0, 0));
        return new(heights);
    }

    private static async Task Near(float actual, float expected, float tolerance = .0002f) =>
        await Assert.That(MathF.Abs(actual - expected)).IsLessThan(tolerance);
    private static async Task Near(Vector3 actual, Vector3 expected, float tolerance = .0002f) =>
        await Assert.That(Vector3.Distance(actual, expected)).IsLessThan(tolerance);

    private sealed class Materials : IZoneTerrainMaterialResolver
    {
        private readonly Dictionary<string, ZoneTerrainMaterial> _materials = new()
        {
            ["full"] = new("full") { PhysicalSurfaceId = 37 },
            ["alias"] = new("alias") { PhysicalSurfaceId = 11 },
            ["fallback"] = new("fallback") { PhysicalSurfaceId = 23 },
        };
        public IZoneTerrainMaterial? Load(string name) => _materials.GetValueOrDefault(name);
        public IZoneTerrainMaterial? ErrorMaterial => null;
        public IZoneTerrainMaterial DefaultMaterial => _materials["fallback"];
    }
}
