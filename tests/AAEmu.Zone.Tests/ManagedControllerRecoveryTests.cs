using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Tests;

public sealed class ManagedControllerRecoveryTests
{
    [Test]
    public async Task ExplicitTeleportClearsOldGroundAndMaterialBeforeTheNextStep()
    {
        var backend = GroundedCharacter(out var actor);
        backend.CopyStatus(actor, out var grounded);
        await Assert.That(grounded.IsGrounded).IsTrue();
        await Assert.That(backend.GroundMaterialFor(actor)).IsNotNull();
        await Assert.That(backend.OwnedPhysics.TryReadGroundHeight(actor, out _)).IsTrue();
        backend.ApplyVelocity(actor, new(1, 2, 3));

        var destination = new Vector3(40, 30, 10);
        var orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .5f);
        backend.Teleport(actor, destination, orientation);
        backend.CopyStatus(actor, out var teleported);
        await Assert.That(teleported.Position).IsEqualTo(destination);
        await Assert.That(teleported.Orientation).IsEqualTo(orientation);
        await Assert.That(teleported.Velocity).IsEqualTo(Vector3.Zero);
        await Assert.That(teleported.IsGrounded).IsFalse();
        await Assert.That(backend.GroundMaterialFor(actor)).IsNull();
        await Assert.That(backend.OwnedPhysics.TryReadGroundHeight(actor, out _)).IsFalse();

        backend.Step(TimeSpan.FromMilliseconds(10));
        backend.CopyStatus(actor, out var falling);
        await Assert.That(falling.IsGrounded).IsFalse();
        await Assert.That(falling.Position.Z).IsLessThan(destination.Z);
    }

    [Test]
    public async Task RejectedTeleportPreservesThePreviousPoseVelocityAndSupport()
    {
        var backend = GroundedCharacter(out var actor);
        backend.CopyStatus(actor, out var previous);
        backend.ApplyVelocity(actor, new(1, 2, 0));
        await Assert.That(() => backend.Teleport(actor, new(float.NaN, 30, 10), Quaternion.Identity))
            .Throws<ArgumentException>();
        backend.CopyStatus(actor, out var unchanged);
        await Assert.That(unchanged.Position).IsEqualTo(previous.Position);
        await Assert.That(unchanged.IsGrounded).IsTrue();
        await Assert.That(unchanged.Velocity).IsEqualTo(new Vector3(1, 2, 0));
        await Assert.That(backend.GroundMaterialFor(actor)).IsNotNull();
    }

    [Test]
    public async Task InitialWallOverlapRecoversBeforeMotionAndPreservesTangentialVelocity()
    {
        var (backend, actor, _, _) = OverlappingWallCharacter();
        backend.ApplyVelocity(actor, new(1, 2, 0));
        backend.Step(TimeSpan.FromMilliseconds(20));
        backend.CopyStatus(actor, out var recovered);
        await Assert.That(recovered.Position.X).IsLessThan(-.4f);
        await Assert.That(recovered.Position.X).IsGreaterThan(-.6f);
        await Assert.That(MathF.Abs(recovered.Position.Y - .04f)).IsLessThan(.0001f);
        await Assert.That(MathF.Abs(recovered.Position.Z - .15f)).IsLessThan(.0001f);
        await Assert.That(MathF.Abs(recovered.Velocity.Y - 2)).IsLessThan(.00001f);
        await Assert.That(MathF.Abs(recovered.Velocity.Z)).IsLessThan(.00001f);
        await Assert.That(recovered.Velocity.X).IsGreaterThanOrEqualTo(0f);
        await Assert.That(recovered.Velocity.X).IsLessThan(1f);
        await Assert.That(recovered.Orientation).IsEqualTo(Quaternion.Identity);
        await Assert.That(backend.TryReadLastRecovery(actor, out var report)).IsTrue();
        await Assert.That(report.Translation.X).IsLessThan(-.1f);
        await Assert.That(report.Passes).IsGreaterThan(0);
        await Assert.That(backend.TryCollide(ZonePhysicsShape.Capsule(.4f, .32f),
            recovered.Position + new Vector3(0, 0, 1.05f), actor)).IsFalse();
    }

    [Test]
    public async Task InitialWallRecoveryDoesNotCancelOutwardVelocity()
    {
        var (backend, actor, _, _) = OverlappingWallCharacter();
        var velocity = new Vector3(-.5f, 2, 0);
        backend.ApplyVelocity(actor, velocity);
        backend.Step(TimeSpan.FromMilliseconds(20));
        backend.CopyStatus(actor, out var recovered);
        await Assert.That(recovered.Position.X).IsLessThan(-.41f);
        await Assert.That(recovered.Position.X).IsGreaterThan(-.6f);
        await Assert.That(recovered.Velocity).IsEqualTo(velocity);
        await Assert.That(MathF.Abs(recovered.Position.Y - .04f)).IsLessThan(.0001f);
    }

    [Test]
    public async Task OrdinaryCorrectionFromAnOverlapRecoversBeforeCastingAcrossTheWall()
    {
        var (backend, actor, _, _) = OverlappingWallCharacter();
        backend.ApplyPose(actor, new(2, .05f, .15f), Quaternion.Identity);
        backend.CopyStatus(actor, out var corrected);
        await Assert.That(corrected.Position.X).IsLessThanOrEqualTo(-.399f);
        await Assert.That(MathF.Abs(corrected.Position.Y - .05f)).IsLessThan(.0001f);
        await Assert.That(MathF.Abs(corrected.Position.Z - .15f)).IsLessThan(.0001f);
        // A scene teleport is explicitly allowed to cross that same wall.
        backend.Teleport(actor, new(2, .05f, .15f), Quaternion.Identity);
        backend.CopyStatus(actor, out var teleported);
        await Assert.That(teleported.Position).IsEqualTo(new Vector3(2, .05f, .15f));
        await Assert.That(teleported.IsGrounded).IsFalse();
        await Assert.That(backend.TryReadLastRecovery(actor, out _)).IsFalse();
    }

    [Test]
    public async Task RemovedGeometryAndRetiredGenerationsLeaveNoPendingRecovery()
    {
        var (backend, actor, statics, wall) = OverlappingWallCharacter();
        statics.RemoveInstance(wall);
        backend.ApplyVelocity(actor, new(1, 2, 0));
        backend.Step(TimeSpan.FromMilliseconds(20));
        backend.CopyStatus(actor, out var unobstructed);
        await Assert.That(MathF.Abs(unobstructed.Position.X + .28f)).IsLessThan(.0001f);
        backend.RemoveActor(actor);
        await Assert.That(() => backend.CopyStatus(actor, out _)).Throws<InvalidOperationException>();
        var next = new ZoneActorHandle(actor.UnitObjectId, actor.Generation + 1);
        backend.AddActor(next, Description());
        backend.Teleport(next, new(20, 30, .15f), Quaternion.Identity);
        backend.Step(TimeSpan.FromMilliseconds(20));
        backend.CopyStatus(next, out var reentered);
        await Assert.That(reentered.Position).IsEqualTo(new Vector3(20, 30, .15f));
        await Assert.That(reentered.Velocity).IsEqualTo(Vector3.Zero);
    }

    [Test]
    public async Task CertifiedSymmetricSolidExitIsUsedAndItsTieChoiceIsReported()
    {
        var statics = new ZoneManagedStaticGeometry();
        statics.AddInstance(Cube(.1f), Matrix4x4.CreateTranslation(0, 0, 1.2f));
        var backend = new ZoneManagedPhysicsBackend(new(new ZoneTerrainHeightField()), staticGeometry: statics);
        var actor = new ZoneActorHandle(1003, 1);
        backend.AddActor(actor, Description());
        backend.Teleport(actor, new(0, 0, .15f), Quaternion.Identity);
        backend.ApplyVelocity(actor, new(0, 0, .01f));
        backend.Step(TimeSpan.FromMilliseconds(20));
        backend.CopyStatus(actor, out var recovered);
        await Assert.That(backend.TryReadLastRecovery(actor, out var report)).IsTrue();
        await Assert.That(report.AmbiguousContactCount).IsGreaterThan(0);
        await Assert.That(report.Translation.Length()).IsGreaterThan(.5f);
        await Assert.That(backend.TryCollide(Description().Shape,
            recovered.Position + new Vector3(0, 0, Description().CenterOffsetZ), actor)).IsFalse();
    }

    [Test]
    public async Task DepthBeyondTheNativePerGeometryRadiusBoundFailsWithoutCommittingStagedState()
    {
        var statics = new ZoneManagedStaticGeometry();
        statics.AddInstance(Cube(5), Matrix4x4.CreateTranslation(0, 0, 1.2f));
        var backend = new ZoneManagedPhysicsBackend(new(new ZoneTerrainHeightField()), staticGeometry: statics);
        var actor = new ZoneActorHandle(1004, 1);
        backend.AddActor(actor, Description());
        var position = new Vector3(0, 0, .15f);
        var velocity = new Vector3(1, 2, 0);
        backend.Teleport(actor, position, Quaternion.Identity);
        backend.ApplyVelocity(actor, velocity);
        InvalidOperationException? failure = null;
        try { backend.Step(TimeSpan.FromMilliseconds(20)); }
        catch (InvalidOperationException error) { failure = error; }
        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.Message.Contains("2.5-radius", StringComparison.Ordinal)).IsTrue();
        backend.CopyStatus(actor, out var unchanged);
        await Assert.That(unchanged.Position).IsEqualTo(position);
        await Assert.That(unchanged.Velocity).IsEqualTo(velocity);
        await Assert.That(backend.TryReadLastRecovery(actor, out _)).IsFalse();
    }

    [Test]
    public async Task OrdinaryLivingRecoveryExcludesOtherLivingBodiesButBodyQueriesStillSeeThem()
    {
        var backend = new ZoneManagedPhysicsBackend(new(new ZoneTerrainHeightField()));
        var actor = new ZoneActorHandle(1005, 1);
        var other = new ZoneActorHandle(1006, 1);
        backend.AddActor(actor, Description());
        backend.AddActor(other, Description());
        var position = new Vector3(-.3f, 0, .15f);
        backend.Teleport(actor, position, Quaternion.Identity);
        backend.Teleport(other, new(0, 0, .15f), Quaternion.Identity);
        backend.Step(TimeSpan.FromMilliseconds(20));
        backend.CopyStatus(actor, out var unchanged);
        await Assert.That(unchanged.Position).IsEqualTo(position);
        await Assert.That(backend.TryReadLastRecovery(actor, out _)).IsFalse();
        await Assert.That(backend.TryCollide(Description().Shape,
            position + new Vector3(0, 0, Description().CenterOffsetZ), actor)).IsTrue();
        backend.ApplyVelocity(actor, new(1, 0, 0));
        backend.Step(TimeSpan.FromMilliseconds(20));
        backend.CopyStatus(actor, out var blocked);
        await Assert.That(blocked.Position).IsEqualTo(position);
        await Assert.That(blocked.Velocity).IsEqualTo(Vector3.Zero);
    }

    [Test]
    public async Task ExplicitBoxActorKeepsItsGenericCertifiedRecoveryWithoutALivingRadiusLimit()
    {
        var statics = new ZoneManagedStaticGeometry();
        statics.AddInstance(Cube(.4f), Matrix4x4.CreateTranslation(0, 0, 1.2f));
        var backend = new ZoneManagedPhysicsBackend(new(new ZoneTerrainHeightField()), staticGeometry: statics);
        var actor = new ZoneActorHandle(1007, 1);
        var shape = ZonePhysicsShape.Box(new(.2f, .3f, .4f));
        backend.AddActor(actor, new(shape, 80)
        { ColliderCenterOffsetZ = 1.05f, GroundPivotHeight = .15f, Gravity = 0 });
        backend.Teleport(actor, new(0, 0, .15f), Quaternion.Identity);
        backend.Step(TimeSpan.FromMilliseconds(20));
        backend.CopyStatus(actor, out var recovered);
        await Assert.That(backend.TryReadLastRecovery(actor, out var report)).IsTrue();
        // .6m of geometric separation exceeds 2.5 * .2; the native living radius rule
        // is not a valid constraint for this explicitly requested kinematic box profile.
        await Assert.That(report.Translation.Length()).IsGreaterThan(.6f);
        await Assert.That(backend.TryCollide(shape,
            recovered.Position + new Vector3(0, 0, 1.05f), actor)).IsFalse();
    }

    [Test]
    public async Task FinalClearanceUsesTheCommittedAuthoredCenterAtLargeCoordinates()
    {
        var statics = new ZoneManagedStaticGeometry();
        statics.AddInstance(new ZoneManagedTriangleMesh(
            [new(-10, -10, 0), new(10, -10, 0), new(10, 10, 0), new(-10, 10, 0)],
            [0, 1, 2, 0, 2, 3]), Matrix4x4.CreateTranslation(8000, 8000, 8001.2f));
        var backend = new ZoneManagedPhysicsBackend(new(new ZoneTerrainHeightField()), staticGeometry: statics);
        var actor = new ZoneActorHandle(1008, 1);
        var description = Description();
        backend.AddActor(actor, description);
        backend.Teleport(actor, new(8000, 8000, 8000.15f), Quaternion.Identity);
        backend.ApplyVelocity(actor, new(1, 0, 0));
        backend.Step(TimeSpan.FromMilliseconds(20));
        backend.CopyStatus(actor, out var recovered);
        await Assert.That(backend.TryReadLastRecovery(actor, out var report)).IsTrue();
        await Assert.That(report.Translation.Length()).IsGreaterThan(.7f);
        var committedCenter = recovered.Position + new Vector3(0, 0, description.CenterOffsetZ);
        await Assert.That(backend.TryCollide(description.Shape, committedCenter, actor)).IsFalse();
        await Assert.That(statics.GetPenetrations(description.Shape, committedCenter).Count).IsEqualTo(0);
        await Assert.That(recovered.Velocity).IsEqualTo(new Vector3(1, 0, 0));
    }

    [Test]
    public async Task CyclicLocalConstraintsFailWithinTheWorkBoundWithoutMovingTheActor()
    {
        var statics = new ZoneManagedStaticGeometry();
        statics.AddInstance(Cube(1), Matrix4x4.CreateScale(5.05f, 10, 10) * Matrix4x4.CreateTranslation(-4.95f, 0, 1.2f));
        statics.AddInstance(Cube(1), Matrix4x4.CreateScale(5.05f, 10, 10) * Matrix4x4.CreateTranslation(4.95f, 0, 1.2f));
        var backend = new ZoneManagedPhysicsBackend(new(new ZoneTerrainHeightField()), staticGeometry: statics);
        var actor = new ZoneActorHandle(1009, 1);
        backend.AddActor(actor, new(ZonePhysicsShape.Box(new(.2f)), 80)
        { ColliderCenterOffsetZ = 1.05f, GroundPivotHeight = .15f, Gravity = 0 });
        var position = new Vector3(0, 0, .15f);
        var velocity = new Vector3(1, 2, 3);
        backend.Teleport(actor, position, Quaternion.Identity);
        backend.ApplyVelocity(actor, velocity);
        InvalidOperationException? failure = null;
        try { backend.Step(TimeSpan.FromMilliseconds(20)); }
        catch (InvalidOperationException error) { failure = error; }
        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.Message.Contains("recovery", StringComparison.Ordinal)).IsTrue();
        await Assert.That(failure.Message.Contains("cycle", StringComparison.Ordinal) ||
            failure.Message.Contains("bounded", StringComparison.Ordinal)).IsTrue();
        backend.CopyStatus(actor, out var unchanged);
        await Assert.That(unchanged.Position).IsEqualTo(position);
        await Assert.That(unchanged.Velocity).IsEqualTo(velocity);
        await Assert.That(backend.TryReadLastRecovery(actor, out _)).IsFalse();
    }

    [Test]
    public async Task AuthoredGroundRecoveryRaisesThePivotWhenTheColliderWasAlreadyClear()
    {
        var terrain = new ZoneTerrainHeightField();
        terrain.LoadCell(0, 0, TerrainHeightFieldTests.CreateCell(0, 0));
        var backend = new ZoneManagedPhysicsBackend(new(terrain));
        var actor = new ZoneActorHandle(1010, 1);
        var description = ZonePhysicsActorDescription.FromLivingStance(
            new(new(.4f, .4f, .32f), default, default, 1.2f, .15f, 5, true, false), 80, gravity: -25);
        backend.AddActor(actor, description);
        var position = new Vector3(20, 30, -.25f);
        backend.Teleport(actor, position, Quaternion.Identity);
        await Assert.That(backend.TryCollide(description.Shape,
            position + new Vector3(0, 0, description.CenterOffsetZ), actor)).IsFalse();
        backend.Step(TimeSpan.FromMilliseconds(20));
        backend.CopyStatus(actor, out var recovered);
        await Assert.That(recovered.IsGrounded).IsTrue();
        await Assert.That(MathF.Abs(recovered.Position.Z - .15f)).IsLessThan(.0001f);
        await Assert.That(backend.OwnedPhysics.TryReadGroundHeight(actor, out var ground)).IsTrue();
        await Assert.That(ground).IsEqualTo(0f);
        // This movement belongs to bounded pivot support, not body-surface unprojection.
        await Assert.That(backend.TryReadLastRecovery(actor, out _)).IsFalse();
    }

    [Test]
    public async Task LowAuthoredColliderCanHoverWithoutBeingRecenteredOnItsPivot()
    {
        var terrain = new ZoneTerrainHeightField();
        terrain.LoadCell(0, 0, TerrainHeightFieldTests.CreateCell(0, 0));
        var backend = new ZoneManagedPhysicsBackend(new(terrain));
        var actor = new ZoneActorHandle(1011, 1);
        var description = ZonePhysicsActorDescription.FromLivingStance(
            new(new(.08f, .08f, .16f), default, default, .2f, 0, 5, true, false), 80, gravity: 0);
        backend.AddActor(actor, description);
        backend.Teleport(actor, new(20, 30, 0), Quaternion.Identity);
        backend.ApplyVelocity(actor, new(2, 0, 0));
        backend.Step(TimeSpan.FromMilliseconds(20));
        backend.CopyStatus(actor, out var hovering);
        await Assert.That(hovering.Position.Z).IsGreaterThan(.04f);
        await Assert.That(hovering.Position.Z).IsLessThan(.07f);
        await Assert.That(hovering.IsGrounded).IsFalse();
        await Assert.That(hovering.Velocity).IsEqualTo(new Vector3(2, 0, 0));
        var center = hovering.Position + new Vector3(0, 0, description.CenterOffsetZ);
        await Assert.That(backend.TryCollide(description.Shape, center, actor)).IsFalse();
        await Assert.That(description.CenterOffsetZ).IsEqualTo(.2f);
    }

    private static ZoneManagedPhysicsBackend GroundedCharacter(out ZoneActorHandle actor)
    {
        var terrain = new ZoneTerrainHeightField();
        terrain.LoadCell(0, 0, TerrainHeightFieldTests.CreateCell(0, 0));
        var backend = new ZoneManagedPhysicsBackend(new(terrain));
        actor = new(1001, 1);
        backend.AddActor(actor, new(ZonePhysicsShape.Capsule(.4f, .32f), 80)
        { ColliderCenterOffsetZ = 1.2f, Gravity = -25 });
        backend.Teleport(actor, new(20, 30, 1), Quaternion.Identity);
        backend.Step(TimeSpan.FromSeconds(1));
        return backend;
    }

    private static (ZoneManagedPhysicsBackend Backend, ZoneActorHandle Actor,
        ZoneManagedStaticGeometry Statics, ZoneManagedStaticInstanceHandle Wall) OverlappingWallCharacter()
    {
        var statics = new ZoneManagedStaticGeometry();
        var wall = statics.AddInstance(new ZoneManagedTriangleMesh(
            [new(0, -10, -10), new(0, 10, -10), new(0, 10, 10), new(0, -10, 10)],
            [0, 1, 2, 0, 2, 3]), Matrix4x4.Identity);
        var backend = new ZoneManagedPhysicsBackend(new(new ZoneTerrainHeightField()), staticGeometry: statics);
        var actor = new ZoneActorHandle(1002, 1);
        backend.AddActor(actor, Description());
        // Seed a per-Step overlap through the explicit teleport seam. Initial admission and
        // native dimension validation are separate operations with their own recovered limits.
        backend.Teleport(actor, new(-.3f, 0, .15f), Quaternion.Identity);
        return (backend, actor, statics, wall);
    }

    private static ZonePhysicsActorDescription Description() => ZonePhysicsActorDescription.FromLivingStance(
        new(new(.4f, .4f, .32f), default, default, 1.2f, .15f, 5, true, false), 80, gravity: 0);

    private static ZoneManagedTriangleMesh Cube(float h) => new(
        [new(-h, -h, -h), new(h, -h, -h), new(h, h, -h), new(-h, h, -h),
         new(-h, -h, h), new(h, -h, h), new(h, h, h), new(-h, h, h)],
        [0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4,
         1, 2, 6, 1, 6, 5, 2, 3, 7, 2, 7, 6, 3, 0, 4, 3, 4, 7], solidConvex: true);
}
