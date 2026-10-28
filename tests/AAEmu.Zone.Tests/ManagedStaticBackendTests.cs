using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Content;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Tests;

public sealed class ManagedStaticBackendTests
{
    [Test]
    public async Task DeferredBrushSectorTranslationMatchesCapturedRoofPlacement()
    {
        // Captured zone129/cell009_015, sector179: deferred brush coordinates are sector-local;
        // object.dat holds the same roof at cell-local (762.4465,252.9082). Cell-local y adds1024.
        var sector = new ZoneCellDeferredSector([], 179, 0, 0, []);
        var translation = sector.GetEngineTranslation(new(0, 1));
        var placed = new Vector3(58.4465f, 60.9082f, 260) + translation;
        await Assert.That(translation).IsEqualTo(new Vector3(704, 1216, 0));
        await Assert.That(MathF.Abs(placed.X - 762.4465f)).IsLessThan(.001f);
        await Assert.That(MathF.Abs(placed.Y - 1276.9082f)).IsLessThan(.001f);
        await Assert.That(placed.Z).IsEqualTo(260f);
    }

    private static ZoneManagedTriangleMesh Floor(float z, ushort material = 17) => new(
        [new(-100, -100, z), new(100, -100, z), new(100, 100, z), new(-100, 100, z)],
        [0, 1, 2, 0, 2, 3], [material, material]);

    private static (ZoneManagedPhysicsBackend Backend, ZoneActorHandle Actor) Character(
        ZoneManagedStaticGeometry statics, Vector3 position, Vector3? halfExtents = null)
    {
        var backend = new ZoneManagedPhysicsBackend(new(new ZoneTerrainHeightField()), staticGeometry: statics);
        var actor = new ZoneActorHandle(701, 1);
        backend.AddActor(actor, new(new(halfExtents ?? new(.4f, .4f, .8f), true), 80));
        backend.Teleport(actor, position, Quaternion.Identity);
        return (backend, actor);
    }

    [Test]
    public async Task CharacterLandsInsideBuildingWithoutSnappingOntoItsRoof()
    {
        var statics = new ZoneManagedStaticGeometry();
        var floor = statics.AddInstance(Floor(0), Matrix4x4.Identity);
        statics.AddInstance(Floor(4, 29), Matrix4x4.Identity);
        var (backend, actor) = Character(statics, new(1, 2, 1));
        backend.Step(TimeSpan.FromSeconds(1));
        backend.CopyStatus(actor, out var standing);
        await Assert.That(standing.IsGrounded).IsTrue();
        await Assert.That(standing.Position.Z).IsEqualTo(0f);
        await Assert.That(backend.GroundMaterialFor(actor)).IsEqualTo((ushort?)17);
        await Assert.That(backend.HasStaticMeshCollision).IsTrue();
        // General scene rays still see the roof from above; character support is context-aware.
        await Assert.That(backend.TryGround(new(1, 2, 10), 20, out var roof)).IsTrue();
        await Assert.That(MathF.Abs(roof.Position.Z - 4f)).IsLessThan(.001f);
        await Assert.That(roof.PhysicalMaterialId).IsEqualTo((ushort)29);
        statics.RemoveInstance(floor);
        backend.Step(TimeSpan.FromMilliseconds(100));
        backend.CopyStatus(actor, out var falling);
        await Assert.That(falling.IsGrounded).IsFalse();
        await Assert.That(falling.Position.Z).IsLessThan(0f);
        await Assert.That(backend.GroundMaterialFor(actor)).IsNull();
    }

    [Test]
    public async Task PlayerMotionStopsAtAuthoredWallAndKeepsItsSlidingComponent()
    {
        var statics = new ZoneManagedStaticGeometry();
        statics.AddInstance(Floor(0), Matrix4x4.Identity);
        statics.AddInstance(new ZoneManagedTriangleMesh(
            [new(3, -100, 0), new(3, 100, 0), new(3, 100, 10), new(3, -100, 10)],
            [0, 1, 2, 0, 2, 3], [23, 23]), Matrix4x4.Identity);
        var (backend, actor) = Character(statics, new(0, 0, 0));
        backend.ApplyVelocity(actor, new(10, 2, 0));
        backend.Step(TimeSpan.FromSeconds(.5));
        backend.CopyStatus(actor, out var blocked);
        await Assert.That(blocked.Position.X).IsLessThanOrEqualTo(2.601f);
        await Assert.That(blocked.Position.X).IsGreaterThan(2.5f);
        await Assert.That(blocked.Position.Y).IsGreaterThan(.9f);
        await Assert.That(blocked.IsGrounded).IsTrue();
        await Assert.That(MathF.Abs(blocked.Velocity.X)).IsLessThan(.01f);
        await Assert.That(blocked.Velocity.Y).IsEqualTo(2f);
    }

    [Test]
    public async Task UpwardMotionHitsTheCeilingAndThenFallsBackToTheFloor()
    {
        var statics = new ZoneManagedStaticGeometry();
        statics.AddInstance(Floor(0), Matrix4x4.Identity);
        statics.AddInstance(Floor(3), Matrix4x4.Identity);
        var (backend, actor) = Character(statics, new(1, 2, 0), new(.4f, .4f, .5f));
        backend.ApplyImpulse(actor, new(0, 0, 12));
        var maximum = 0f;
        var sawDownward = false;
        for (var frame = 0; frame < 120; frame++)
        {
            backend.Step(TimeSpan.FromMilliseconds(33));
            backend.CopyStatus(actor, out var status);
            maximum = MathF.Max(maximum, status.Position.Z);
            sawDownward |= status.Position.Z > .1f && status.Velocity.Z < 0;
        }
        backend.CopyStatus(actor, out var landed);
        await Assert.That(maximum).IsLessThanOrEqualTo(2.001f); // Feet + full box height cannot cross z=3.
        await Assert.That(maximum).IsGreaterThan(1.9f);
        await Assert.That(sawDownward).IsTrue();
        await Assert.That(landed.IsGrounded).IsTrue();
        await Assert.That(landed.Position.Z).IsEqualTo(0f);
    }

    [Test]
    public async Task WalkableStaticSlopeSuppliesRisingSupportWithoutBlockingForwardMotion()
    {
        var statics = new ZoneManagedStaticGeometry();
        statics.AddInstance(new ZoneManagedTriangleMesh(
            [new(-20, -20, -10), new(20, -20, 10), new(20, 20, 10), new(-20, 20, -10)],
            [0, 1, 2, 0, 2, 3], [31, 31]), Matrix4x4.Identity);
        var (backend, actor) = Character(statics, Vector3.Zero);
        backend.ApplyVelocity(actor, new(2, 0, 0));
        for (var frame = 0; frame < 30; frame++) backend.Step(TimeSpan.FromMilliseconds(33));
        backend.CopyStatus(actor, out var climbed);
        await Assert.That(climbed.Position.X).IsGreaterThan(1.9f);
        await Assert.That(MathF.Abs(climbed.Position.Z - climbed.Position.X * .5f)).IsLessThan(.001f);
        await Assert.That(climbed.IsGrounded).IsTrue();
    }

    [Test]
    public async Task StaticQueriesComposeWithTerrainAndUnloadingRestoresTerrainSupport()
    {
        var terrain = new ZoneTerrainHeightField();
        terrain.LoadCell(0, 0, TerrainHeightFieldTests.CreateCell(0, 0));
        var statics = new ZoneManagedStaticGeometry();
        statics.AddInstance(Floor(5, 29), Matrix4x4.Identity);
        var backend = new ZoneManagedPhysicsBackend(new(terrain), staticGeometry: statics);
        await Assert.That(backend.TryGround(new(1, 2, 10), 20, out var hit)).IsTrue();
        await Assert.That(hit.Position.Z).IsEqualTo(5f);
        var shape = new ZonePhysicsShape(new(.5f), false);
        await Assert.That(backend.TrySweep(shape, new(1, 2, 10), new(0, 0, -20), null, out var sweep)).IsTrue();
        await Assert.That(MathF.Abs(sweep.Distance - 4.5f)).IsLessThan(.001f);
        await Assert.That(backend.TryCollide(shape, new(1, 2, 5), null)).IsTrue();
        statics.Clear();
        await Assert.That(backend.HasStaticMeshCollision).IsFalse();
        await Assert.That(backend.TryGround(new(1, 2, 10), 20, out hit)).IsTrue();
        await Assert.That(hit.Position.Z).IsEqualTo(0f);
        await Assert.That(backend.TryCollide(shape, new(1, 2, 5), null)).IsFalse();
    }

    [Test]
    public async Task DiagonalJumpCannotCrossAnOverhangMissedBySeparateAxisCasts()
    {
        var statics = new ZoneManagedStaticGeometry();
        statics.AddInstance(new ZoneManagedTriangleMesh([new(1, -2, 2), new(3, -2, 2), new(3, 2, 2), new(1, 2, 2)],
            [0, 1, 2, 0, 2, 3]), Matrix4x4.Identity);
        var (backend, actor) = Character(statics, Vector3.Zero, new(.1f, .1f, .5f));
        backend.ApplyVelocity(actor, new(40, 0, 40));
        backend.Step(TimeSpan.FromMilliseconds(50));
        backend.CopyStatus(actor, out var blocked);
        await Assert.That(blocked.Position.X).IsGreaterThan(1.9f);
        await Assert.That(blocked.Position.Z).IsLessThanOrEqualTo(1.001f);
        await Assert.That(blocked.Velocity.Z).IsLessThanOrEqualTo(0f);
    }

    [Test]
    public async Task JumpAboveALowWallDoesNotBlockAtTheOldHorizontalHeight()
    {
        var statics = new ZoneManagedStaticGeometry();
        statics.AddInstance(new ZoneManagedTriangleMesh([new(1, -2, 0), new(1, 2, 0), new(1, 2, .3f), new(1, -2, .3f)],
            [0, 1, 2, 0, 2, 3]), Matrix4x4.Identity);
        var (backend, actor) = Character(statics, Vector3.Zero, new(.1f));
        backend.ApplyVelocity(actor, new(40, 0, 40));
        backend.Step(TimeSpan.FromMilliseconds(50));
        backend.CopyStatus(actor, out var passed);
        await Assert.That(passed.Position.X).IsGreaterThan(1.9f);
        await Assert.That(passed.Position.Z).IsGreaterThan(1.9f);
    }

    [Test]
    public async Task FallingUnderAPlatformCannotSnapUpToItsEndpointColumn()
    {
        var statics = new ZoneManagedStaticGeometry();
        statics.AddInstance(new ZoneManagedTriangleMesh([new(.45f, -2, 1.9f), new(2, -2, 1.9f), new(2, 2, 1.9f), new(.45f, 2, 1.9f)],
            [0, 1, 2, 0, 2, 3]), Matrix4x4.Identity);
        var (backend, actor) = Character(statics, new(0, 0, 2), new(.05f, .05f, .5f));
        backend.ApplyVelocity(actor, new(10, 0, -30));
        backend.Step(TimeSpan.FromMilliseconds(50));
        backend.CopyStatus(actor, out var fallen);
        await Assert.That(fallen.Position.Z).IsLessThan(.5f);
        await Assert.That(fallen.IsGrounded).IsFalse();
        await Assert.That(fallen.Velocity.Z).IsLessThan(-30f);
    }

    [Test]
    public async Task TerrainOverheadCannotReplaceReachableInteriorSupport()
    {
        var terrain = new ZoneTerrainHeightField();
        terrain.LoadCell(0, 0, TerrainHeightFieldTests.CreateCell(10, 0));
        var statics = new ZoneManagedStaticGeometry();
        statics.AddInstance(Floor(0), Matrix4x4.Identity);
        var backend = new ZoneManagedPhysicsBackend(new(terrain), staticGeometry: statics);
        var actor = new ZoneActorHandle(702, 1);
        backend.AddActor(actor, new(new(new(.4f, .4f, .8f), true), 80));
        backend.Teleport(actor, new(1, 2, 1), Quaternion.Identity);
        backend.Step(TimeSpan.FromSeconds(1));
        backend.CopyStatus(actor, out var standing);
        await Assert.That(standing.Position.Z).IsEqualTo(0f);
        await Assert.That(standing.IsGrounded).IsTrue();
    }

    [Test]
    public async Task OrdinaryCorrectionsRespectWallsWhileExplicitTeleportsRemainAuthoritative()
    {
        var statics = new ZoneManagedStaticGeometry();
        statics.AddInstance(new ZoneManagedTriangleMesh([new(3, -10, -10), new(3, 10, -10), new(3, 10, 10), new(3, -10, 10)],
            [0, 1, 2, 0, 2, 3]), Matrix4x4.Identity);
        var (backend, actor) = Character(statics, Vector3.Zero);
        backend.ApplyPose(actor, new(5, 2, 0), Quaternion.Identity);
        backend.CopyStatus(actor, out var corrected);
        await Assert.That(corrected.Position.X).IsLessThan(2.601f);
        await Assert.That(corrected.Position.X).IsGreaterThan(2.5f);
        await Assert.That(corrected.Position.Y).IsEqualTo(2f);
        backend.Teleport(actor, new(5, 2, 0), Quaternion.Identity);
        backend.CopyStatus(actor, out var teleported);
        await Assert.That(teleported.Position).IsEqualTo(new Vector3(5, 2, 0));
    }

    [Test]
    public async Task PlayerLandsOnAnAnalyticCylinderAndJumpStopsAtASphericalCeiling()
    {
        var statics = new ZoneManagedStaticGeometry();
        statics.AddInstance(new ZoneManagedPrimitiveGeometry(ZoneManagedPrimitiveKind.Cylinder,
            Vector3.Zero, Vector3.UnitZ, 5, 1, 41), Matrix4x4.CreateTranslation(4000, 4000, -1));
        statics.AddInstance(new ZoneManagedPrimitiveGeometry(ZoneManagedPrimitiveKind.Sphere,
            Vector3.Zero, Vector3.Zero, 1, 0, 42), Matrix4x4.CreateTranslation(4000, 4000, 5));
        var (backend, actor) = Character(statics, new(4000, 4000, 1), new(.4f, .4f, .5f));
        backend.Step(TimeSpan.FromSeconds(1));
        backend.CopyStatus(actor, out var standing);
        await Assert.That(standing.IsGrounded).IsTrue();
        await Assert.That(MathF.Abs(standing.Position.Z)).IsLessThan(.002f);
        await Assert.That(backend.GroundMaterialFor(actor)).IsEqualTo((ushort?)41);
        backend.ApplyImpulse(actor, new(0, 0, 12));
        var maximum = 0f;
        for (var frame = 0; frame < 120; frame++)
        {
            backend.Step(TimeSpan.FromMilliseconds(33));
            backend.CopyStatus(actor, out var status);
            maximum = MathF.Max(maximum, status.Position.Z);
        }
        await Assert.That(maximum).IsGreaterThan(2.9f);
        await Assert.That(maximum).IsLessThanOrEqualTo(3.002f);
        backend.CopyStatus(actor, out var landed);
        await Assert.That(landed.IsGrounded).IsTrue();
        await Assert.That(MathF.Abs(landed.Position.Z)).IsLessThan(.002f);
    }
}
