using System.Numerics;

using AAEmu.Zone.Actors;
using AAEmu.Zone.Content;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Tests;

public sealed class ManagedPhysicsTests
{
    [Test]
    public async Task RaysUseAuthoredTriangleHeightsAndSlopeNormals()
    {
        var terrain = new ZoneTerrainHeightField();
        terrain.LoadCell(1, 1, TerrainHeightFieldTests.CreateCell(100, 1,
            sample: (x, y, sx, sy) => (ushort)(x == 64 && y == 64 ? (sx, sy) switch
            {
                (0, 0) => 32, (1, 0) => 64, (0, 1) => 96, (1, 1) => 640, _ => 32,
            } : 32)));
        var geometry = new ZoneManagedTerrainGeometry(terrain);
        await Assert.That(geometry.TryGround(new(1088.5f, 1088.5f, 200), 100, out var ground)).IsTrue();
        await Assert.That(MathF.Abs(ground.Position.Z - 156)).IsLessThan(.001f);
        await Assert.That(MathF.Abs(ground.Distance - 44)).IsLessThan(.001f);
        await Assert.That(Vector3.Distance(ground.Normal, Vector3.Normalize(new(-16, -32, 1)))).IsLessThan(.0001f);
        await Assert.That(geometry.TryGround(new(1089.5f, 1089.5f, 500), 100, out ground)).IsTrue();
        await Assert.That(MathF.Abs(ground.Position.Z - 460)).IsLessThan(.001f);
        await Assert.That(geometry.TryGround(new(1088.5f, 1088.5f, 200), 43, out _)).IsFalse();
    }

    [Test]
    public async Task RaysTraverseLoadedCellEdgesAndRespectBoundedSegments()
    {
        var geometry = FlatGeometry(out _);
        var origin = new Vector3(1000, 1100, 140);
        var displacement = new Vector3(200, 0, -20);
        await Assert.That(geometry.RaycastTerrain(origin, displacement, out var hit)).IsTrue();
        await Assert.That(Vector3.Distance(hit.Position, new(1080, 1100, 132))).IsLessThan(.001f);
        await Assert.That(MathF.Abs(hit.Distance - displacement.Length() * .4f)).IsLessThan(.001f);
        await Assert.That(geometry.RaycastTerrain(origin, displacement * .39f, out _)).IsFalse();
        await Assert.That(geometry.RaycastTerrain(origin + displacement, -displacement, out hit)).IsTrue();
        await Assert.That(Vector3.Distance(hit.Position, new(1080, 1100, 132))).IsLessThan(.001f);
        await Assert.That(geometry.RaycastTerrain(origin, Vector3.Zero, out _)).IsFalse();
        await Assert.That(geometry.TryGround(new(8200, 1100, 140), 100, out _)).IsFalse();
    }

    [Test]
    public async Task MissingHolesAndUnloadedCellsNeverCreateFloors()
    {
        var terrain = new ZoneTerrainHeightField();
        var geometry = new ZoneManagedTerrainGeometry(terrain);
        var origin = new Vector3(1100, 1100, 200);
        await Assert.That(geometry.TryGround(origin, 300, out _)).IsFalse();
        terrain.LoadCell(1, 1, TerrainHeightFieldTests.CreateCell(100, 1));
        // Warm the compatibility callbacks: native cache entries intentionally survive unload.
        await Assert.That(terrain.GetPhysicsHeight(550, 550)).IsEqualTo(132f);
        terrain.GetPhysicsSurfaceIndex(550, 550);
        await Assert.That(geometry.TryGround(origin, 300, out _)).IsTrue();
        terrain.UnloadCell(1, 1);
        await Assert.That(geometry.TryGround(origin, 300, out _)).IsFalse();
        var shape = new ZonePhysicsShape(new(.5f, .5f, 1), false);
        await Assert.That(geometry.TryCollide(shape, new(1100, 1100, -100))).IsFalse();
        await Assert.That(geometry.TrySweep(shape, origin, new(0, 0, -300), out _)).IsFalse();
        terrain.LoadCell(1, 1, TerrainHeightFieldTests.CreateCell(100, 1, sample: (_, _, _, _) => 63));
        await Assert.That(geometry.TryGround(origin, 300, out _)).IsFalse();
        await Assert.That(geometry.TryCollide(shape, new(1100, 1100, 132))).IsFalse();
    }

    [Test]
    public async Task RealZeroAndNegativeHeightSamplesRemainCollidable()
    {
        var terrain = new ZoneTerrainHeightField();
        var geometry = new ZoneManagedTerrainGeometry(terrain);
        terrain.LoadCell(1, 1, TerrainHeightFieldTests.CreateCell(0, 0));
        await Assert.That(geometry.TryGround(new(1100, 1100, 10), 20, out var hit)).IsTrue();
        await Assert.That(hit.Position.Z).IsEqualTo(0f);
        terrain.LoadCell(1, 1, TerrainHeightFieldTests.CreateCell(-12, 0));
        await Assert.That(geometry.TryGround(new(1100, 1100, 0), 20, out hit)).IsTrue();
        await Assert.That(hit.Position.Z).IsEqualTo(-12f);
    }

    [Test]
    public async Task AuthoredCellSlotsResolveTheirCollisionSubmaterial()
    {
        var terrain = new ZoneTerrainHeightField();
        terrain.LoadCell(1, 1, TerrainHeightFieldTests.CreateCell(100, 1, sample: (_, _, _, _) => 34));
        var material = new Material(4, [new(5), new(6), new(37)]);
        var surfaces = new ZoneTerrainSurfaceBindings(
            ZoneTerrainSurfaceContent.Read("<World><SurfaceTypes><Surface Id='3' DetailMaterial='ground'/></SurfaceTypes></World>"),
            new MaterialResolver(material));
        surfaces.ApplyCellSurfaceTypes(1, 1,
            ZoneTerrainCellSurfaceContent.Read("<Level><SurfaceTypes><Surface Id='0'/><Surface Id='0'/><Surface Id='3'/></SurfaceTypes></Level>"));
        var geometry = new ZoneManagedTerrainGeometry(terrain, surfaces);
        await Assert.That(geometry.TryGround(new(1100, 1100, 200), 100, out var ground)).IsTrue();
        await Assert.That(ground.PhysicalMaterialId).IsEqualTo((ushort)37);
    }

    [Test]
    public async Task TerrainBoxesSweepContinuouslyAndOverlapTheAuthoredSurface()
    {
        var geometry = FlatGeometry(out _);
        var shape = new ZonePhysicsShape(new(.5f, .5f, 1), false);
        await Assert.That(geometry.TrySweep(shape, new(1100, 1100, 140), new(0, 0, -20), out var hit)).IsTrue();
        await Assert.That(MathF.Abs(hit.Distance - 7)).IsLessThan(.001f);
        await Assert.That(MathF.Abs(hit.Position.Z - 132)).IsLessThan(.001f);
        await Assert.That(hit.Normal).IsEqualTo(Vector3.UnitZ);
        await Assert.That(geometry.TryCollide(shape, new(1100, 1100, 134))).IsFalse();
        await Assert.That(geometry.TryCollide(shape, new(1100, 1100, 133))).IsTrue();
        await Assert.That(geometry.TryCollide(shape, new(1100, 1100, 100))).IsFalse();
        await Assert.That(geometry.TrySweep(shape, new(1100, 1100, 133), new(10, 0, 0), out _)).IsFalse();
        await Assert.That(geometry.TrySweep(shape, new(1100, 1100, 133), new(0, 0, 10), out _)).IsFalse();
    }

    [Test]
    public async Task SweptBoxDetectsSteepAuthoredTerrainBetweenItsEndpoints()
    {
        var terrain = new ZoneTerrainHeightField();
        terrain.LoadCell(1, 1, TerrainHeightFieldTests.CreateCell(0, .125f,
            sample: (x, _, sx, _) => (ushort)((x / 2 + sx) * 32)));
        var geometry = new ZoneManagedTerrainGeometry(terrain);
        var shape = new ZonePhysicsShape(new(.25f, .25f, .5f), false);
        await Assert.That(geometry.TrySweep(shape, new(1088.5f, 1088.5f, 140), new(10, 0, 0), out var hit)).IsTrue();
        await Assert.That(MathF.Abs(hit.Distance - 5)).IsLessThan(.001f);
        await Assert.That(Vector3.Distance(hit.Normal, Vector3.Normalize(new(-2, 0, 1)))).IsLessThan(.0001f);
        await Assert.That(geometry.CapsulesUseBoundingBoxes).IsFalse();
        await Assert.That(geometry.HasStaticMeshCollision).IsFalse();
    }

    [Test]
    public async Task BackendIntegratesGravityJumpAndTerrainUnloadWithoutNativeModules()
    {
        var geometry = FlatGeometry(out var terrain);
        var backend = new ZoneManagedPhysicsBackend(geometry);
        var actor = new ZoneActorHandle(1, 1);
        backend.AddActor(actor, Description());
        backend.Teleport(actor, new(1100, 1100, 140), Quaternion.Identity);
        backend.ApplyVelocity(actor, new(2, 0, 0));
        backend.Step(TimeSpan.FromSeconds(2));
        backend.CopyStatus(actor, out var status);
        await Assert.That(MathF.Abs(status.Position.X - 1104)).IsLessThan(.01f);
        await Assert.That(status.Position.Z).IsEqualTo(132f);
        await Assert.That(status.IsGrounded).IsTrue();
        await Assert.That(status.Velocity.Z).IsEqualTo(0f);
        await Assert.That(backend.GroundMaterialFor(actor)).IsEqualTo((ushort?)0);
        backend.ApplyImpulse(actor, new(0, 0, 5));
        backend.Step(TimeSpan.FromSeconds(.1));
        backend.CopyStatus(actor, out status);
        await Assert.That(status.Position.Z).IsGreaterThan(132f);
        await Assert.That(status.Velocity.Z).IsGreaterThan(0f);
        await Assert.That(status.IsGrounded).IsFalse();
        backend.Step(TimeSpan.FromSeconds(2));
        backend.CopyStatus(actor, out status);
        await Assert.That(status.IsGrounded).IsTrue();
        terrain.UnloadCell(1, 1);
        backend.Step(TimeSpan.FromSeconds(.1));
        backend.CopyStatus(actor, out status);
        await Assert.That(status.Position.Z).IsLessThan(132f);
        await Assert.That(status.IsGrounded).IsFalse();
        await Assert.That(backend.GroundMaterialFor(actor)).IsNull();
        await Assert.That(backend.HasStaticMeshCollision).IsFalse();
        await Assert.That(backend.HasVehicleDynamics).IsFalse();
        await Assert.That(backend.Limitations.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task BodyQueriesUseRegisteredFootOriginsAndHonorIgnoredActors()
    {
        var backend = new ZoneManagedPhysicsBackend(new(new ZoneTerrainHeightField()));
        var actor = new ZoneActorHandle(1, 1);
        backend.AddActor(actor, Description());
        backend.Teleport(actor, new(100, 100, 10), Quaternion.Identity);
        var shape = Description().Shape;
        var centerZ = 10 + shape.BoundingHalfExtents.Z;
        await Assert.That(backend.TrySweep(shape, new(95, 100, centerZ), new(10, 0, 0), null, out var hit)).IsTrue();
        await Assert.That(MathF.Abs(hit.Distance - 4f)).IsLessThan(ZoneManagedStaticGeometry.AnalyticContactTolerance);
        await Assert.That(hit.Normal).IsEqualTo(-Vector3.UnitX);
        await Assert.That(backend.TrySweep(shape, new(95, 100, centerZ), new(10, 0, 0), actor, out _)).IsFalse();
        await Assert.That(backend.TryCollide(shape, new(100, 100, centerZ), null)).IsTrue();
        await Assert.That(backend.TryCollide(shape, new(100, 100, centerZ), actor)).IsFalse();
        backend.RemoveActor(actor);
        await Assert.That(backend.TryCollide(shape, new(100, 100, centerZ), null)).IsFalse();
    }

    [Test]
    public async Task ActorMotionStopsBeforeCrossingAnotherRegisteredBody()
    {
        var backend = new ZoneManagedPhysicsBackend(FlatGeometry(out _));
        var actor = new ZoneActorHandle(1, 1);
        var blocker = new ZoneActorHandle(2, 1);
        backend.AddActor(actor, Description());
        backend.AddActor(blocker, Description());
        backend.Teleport(actor, new(1100, 1100, 132), Quaternion.Identity);
        backend.Teleport(blocker, new(1103, 1100, 132), Quaternion.Identity);
        backend.ApplyVelocity(actor, new(100, 0, 0));
        backend.Step(TimeSpan.FromSeconds(.1));
        backend.CopyStatus(actor, out var status);
        await Assert.That(status.Position.X).IsLessThan(1102f);
        await Assert.That(status.Position.X).IsGreaterThan(1101.9f);
        await Assert.That(status.Position.Z).IsEqualTo(132f);
    }

    [Test]
    public async Task WalkingUpAnAuthoredWalkableSlopeKeepsHorizontalSpeed()
    {
        var terrain = new ZoneTerrainHeightField();
        terrain.LoadCell(1, 1, TerrainHeightFieldTests.CreateCell(0, .03125f,
            sample: (x, _, sx, _) => (ushort)((x / 2 + sx) * 32)));
        var backend = new ZoneManagedPhysicsBackend(new(terrain));
        var actor = new ZoneActorHandle(1, 1);
        backend.AddActor(actor, Description());
        backend.Teleport(actor, new(1100, 1100, 38), Quaternion.Identity);
        backend.ApplyVelocity(actor, new(4, 2, 0));
        backend.Step(TimeSpan.FromSeconds(.5));
        backend.CopyStatus(actor, out var status);
        await Assert.That(MathF.Abs(status.Position.X - 1102)).IsLessThan(.01f);
        await Assert.That(MathF.Abs(status.Position.Y - 1101)).IsLessThan(.01f);
        await Assert.That(MathF.Abs(status.Position.Z - 39)).IsLessThan(.01f);
        await Assert.That(status.Velocity.X).IsEqualTo(4f);
        await Assert.That(status.IsGrounded).IsTrue();
    }

    [Test]
    public async Task SlidingMotionRechecksTheSecondBodyAtACorner()
    {
        var backend = new ZoneManagedPhysicsBackend(FlatGeometry(out _));
        var actor = new ZoneActorHandle(1, 1);
        var first = new ZoneActorHandle(2, 1);
        var second = new ZoneActorHandle(3, 1);
        backend.AddActor(actor, Description());
        backend.AddActor(first, new(new(new(.5f, 100, 1), false), 70));
        backend.AddActor(second, new(new(new(100, .5f, 1), false), 70));
        backend.Teleport(actor, new(1100, 1100, 132), Quaternion.Identity);
        backend.Teleport(first, new(1103, 1100, 132), Quaternion.Identity);
        backend.Teleport(second, new(1100, 1103, 132), Quaternion.Identity);
        backend.ApplyVelocity(actor, new(100, 100, 0));
        backend.Step(TimeSpan.FromSeconds(.05));
        backend.CopyStatus(actor, out var status);
        await Assert.That(status.Position.X).IsLessThan(1102f);
        await Assert.That(status.Position.Y).IsLessThan(1102f);
        await Assert.That(status.Position.X).IsGreaterThan(1101.9f);
        await Assert.That(status.Position.Y).IsGreaterThan(1101.9f);
    }

    [Test]
    public async Task RetiredGenerationsNeverBecomeUsableAgain()
    {
        var backend = new ZoneManagedPhysicsBackend(new(new ZoneTerrainHeightField()));
        var first = new ZoneActorHandle(1, 1);
        var next = new ZoneActorHandle(1, 2);
        backend.AddActor(first, Description());
        await Assert.That(() => backend.AddActor(first, Description())).Throws<InvalidOperationException>();
        await Assert.That(() => backend.AddActor(next, Description())).Throws<InvalidOperationException>();
        backend.RemoveActor(first);
        backend.RemoveActor(first);
        await Assert.That(() => backend.AddActor(first, Description())).Throws<InvalidOperationException>();
        await Assert.That(() => backend.ApplyVelocity(first, Vector3.Zero)).Throws<InvalidOperationException>();
        await Assert.That(() => backend.CopyStatus(first, out _)).Throws<InvalidOperationException>();
        backend.AddActor(next, Description());
        backend.ApplyVelocity(next, Vector3.UnitX);
        backend.CopyStatus(next, out var status);
        await Assert.That(status.Velocity).IsEqualTo(Vector3.UnitX);
        backend.Teleport(next, new(1, 2, 3), Quaternion.Identity);
        backend.CopyStatus(next, out status);
        await Assert.That(status.Velocity).IsEqualTo(Vector3.Zero);
    }

    [Test]
    public async Task InvalidShapesSegmentsStepsAndPosesAreRejected()
    {
        var backend = new ZoneManagedPhysicsBackend(new(new ZoneTerrainHeightField()));
        var actor = new ZoneActorHandle(1, 1);
        backend.AddActor(actor, Description());
        var invalid = new ZonePhysicsShape(new(float.NaN, 1, 1), false);
        await Assert.That(() => backend.TrySweep(invalid, Vector3.Zero, Vector3.UnitX, null, out _)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => backend.TryCollide(Description().Shape, new(float.PositiveInfinity, 0, 0), null)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => backend.TryGround(Vector3.Zero, float.NaN, out _)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => backend.Step(TimeSpan.Zero)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => backend.Teleport(actor, Vector3.Zero, default)).Throws<ArgumentException>();
        await Assert.That(backend.TrySweep(Description().Shape, Vector3.Zero, Vector3.Zero, null, out _)).IsFalse();
    }

    private static ZoneManagedTerrainGeometry FlatGeometry(out ZoneTerrainHeightField terrain)
    {
        terrain = new();
        terrain.LoadCell(1, 1, TerrainHeightFieldTests.CreateCell(100, 1));
        return new(terrain);
    }

    private static ZonePhysicsActorDescription Description() => new(new(new(.5f, .5f, 1), true), 70);

    private sealed class Material(int id, Material[]? children = null) : IZoneTerrainMaterial
    {
        public string Name => "ground";
        public int SubMaterialCount => children?.Length ?? 0;
        public IZoneTerrainMaterial? GetSubMaterial(int index) => children?[index];
        public int PhysicalSurfaceId => id;
        public float TerrainScale { get; set; }
        public int TerrainProjectionAxis { get; set; }
    }

    private sealed class MaterialResolver(Material material) : IZoneTerrainMaterialResolver
    {
        public IZoneTerrainMaterial? Load(string name) => material;
        public IZoneTerrainMaterial? ErrorMaterial => null;
        public IZoneTerrainMaterial DefaultMaterial => material;
    }
}
