using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Content;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Tests;

public sealed class ManagedLivingStanceTests
{
    [Test]
    public async Task InitialLivingDescriptionUsesLastAuthoredStanceAndNativeEntityScaling()
    {
        var models = new ZoneActorModelContent(
            [new() { Id = 1, PhysicsLivingMass = 80, PhysicsLivingGravity = 6 }],
            [new() { Id = 1, ActorModelId = 1, StanceId = 2, SizeX = 9, SizeY = 9, SizeZ = 9 },
             new() { Id = 2, ActorModelId = 1, StanceId = 0, SizeX = 8, SizeY = 8, SizeZ = 8 },
             new() { Id = 3, ActorModelId = 1, StanceId = 2, SizeX = .4f, SizeY = .9f, SizeZ = .32f,
                 HeightCollider = .85f, HeightPivot = .15f, UseCapsule = true }]);
        var request = ZoneActorPhysicalizationRequest.BuildInitial(models.Models[1], 2, false, _ => null);
        var description = ZonePhysicsActorDescription.FromLivingStance(request.Stance,
            request.Dynamics.ReadSingle(0x28)!.Value, -request.Dynamics.ReadSingle(0x1C)!.Value);

        await Assert.That(description.Shape.Kind).IsEqualTo(ZonePhysicsShapeKind.Capsule);
        await Assert.That(description.Shape.Radius).IsEqualTo(.8f);
        await Assert.That(description.Shape.SegmentHalfHeight).IsEqualTo(.64f);
        // Size.Y is retained as authored metadata but the living primitive uses Size.X radially.
        await Assert.That(description.Shape.HalfExtents.Y).IsEqualTo(1.8f);
        await Assert.That(Vector3.Distance(description.Shape.BoundingHalfExtents, new(.8f, .8f, 1.44f))).IsLessThan(.0001f);
        await Assert.That(MathF.Abs(description.CenterOffsetZ - 1.4f)).IsLessThan(.0001f);
        await Assert.That(description.GroundPivotHeight).IsEqualTo(.3f);
        await Assert.That(description.Mass).IsEqualTo(640f);
        await Assert.That(description.Gravity).IsEqualTo(-6f);
    }

    [Test]
    public async Task MissingStanceKeepsNativeConstructorDimensionsWithoutScalingThem()
    {
        var models = new ZoneActorModelContent([new() { Id = 1, PhysicsLivingMass = 10 }], []);
        var request = ZoneActorPhysicalizationRequest.BuildInitial(models.Models[1], 3, false, _ => null);
        var description = ZonePhysicsActorDescription.FromLivingStance(request.Stance, 270);
        await Assert.That(description.Shape.Kind).IsEqualTo(ZonePhysicsShapeKind.Cylinder);
        await Assert.That(description.Shape.BoundingHalfExtents).IsEqualTo(new Vector3(.5f));
        await Assert.That(description.CenterOffsetZ).IsEqualTo(0f);
        await Assert.That(description.GroundPivotHeight).IsEqualTo(0f);
        var explicitBox = new ZonePhysicsShape(new(.2f, .3f, .4f), false);
        await Assert.That(explicitBox.Kind).IsEqualTo(ZonePhysicsShapeKind.Box);
        await Assert.That(explicitBox.BoundingHalfExtents).IsEqualTo(explicitBox.HalfExtents);
        await Assert.That(new ZonePhysicsActorDescription(explicitBox, 10).CenterOffsetZ).IsEqualTo(.4f);
    }

    [Test]
    public async Task AuthoredCapsuleUsesRoundedRadialBoundaryAndColliderHeightInsteadOfFeetRecentering()
    {
        var backend = Backend();
        var actor = new ZoneActorHandle(901, 1);
        var stance = Stance(useCapsule: true);
        var description = ZonePhysicsActorDescription.FromLivingStance(stance, 80);
        backend.AddActor(actor, description);
        var origin = new Vector3(4, 5, 10);
        backend.Teleport(actor, origin, Quaternion.Identity);
        var center = origin + new Vector3(0, 0, stance.HeightCollider - stance.HeightPivot);
        var probe = ZonePhysicsShape.Box(new(.005f));

        await Assert.That(backend.TryCollide(probe, center, null)).IsTrue();
        await Assert.That(backend.TryCollide(probe, origin + new Vector3(0, 0, .2f), null)).IsFalse();
        await Assert.That(backend.TryCollide(probe, center + new Vector3(.35f, .35f, 0), null)).IsFalse();
        await Assert.That(backend.TryCollide(probe, center + new Vector3(.39f, 0, 0), null)).IsTrue();
        await Assert.That(backend.TryCollide(probe, center, actor)).IsFalse();
        await Assert.That(backend.TrySweep(probe, center + new Vector3(2, 0, 0), new(-3, 0, 0), null, out var hit)).IsTrue();
        await Assert.That(MathF.Abs(hit.Distance - 1.595f)).IsLessThan(.003f);
        await Assert.That(Vector3.Distance(hit.Normal, Vector3.UnitX)).IsLessThan(.003f);
        backend.RemoveActor(actor);
        await Assert.That(backend.TryCollide(probe, center, null)).IsFalse();
    }

    [Test]
    public async Task AuthoredNonCapsuleLivingBodyIsACylinderWithFlatCapsAndCircularSides()
    {
        var backend = Backend();
        var actor = new ZoneActorHandle(902, 1);
        var description = ZonePhysicsActorDescription.FromLivingStance(Stance(useCapsule: false), 80);
        backend.AddActor(actor, description);
        backend.Teleport(actor, new(4, 5, 10), Quaternion.Identity);
        var center = new Vector3(4, 5, 10 + description.CenterOffsetZ);
        var probe = ZonePhysicsShape.Box(new(.005f));

        await Assert.That(description.Shape.Kind).IsEqualTo(ZonePhysicsShapeKind.Cylinder);
        await Assert.That(backend.TryCollide(probe, center + new Vector3(.35f, .35f, 0), null)).IsFalse();
        await Assert.That(backend.TryCollide(probe, center + new Vector3(0, 0, .35f), null)).IsFalse();
        await Assert.That(backend.TryCollide(probe, center + new Vector3(0, 0, .31f), null)).IsTrue();
        await Assert.That(backend.TrySweep(probe, center + new Vector3(0, 0, 2), new(0, 0, -3), null, out var hit)).IsTrue();
        await Assert.That(MathF.Abs(hit.Distance - 1.675f)).IsLessThan(.003f);
        await Assert.That(Vector3.Distance(hit.Normal, Vector3.UnitZ)).IsLessThan(.003f);
    }

    [Test]
    public async Task ManagedBackendPassesAuthoredGravityToItsOwnedIntegrator()
    {
        var backend = Backend();
        var actor = new ZoneActorHandle(903, 1);
        backend.AddActor(actor, ZonePhysicsActorDescription.FromLivingStance(Stance(true), 80, gravity: -4));
        backend.Teleport(actor, new(4, 5, 100), Quaternion.Identity);
        backend.Step(TimeSpan.FromSeconds(.1));
        backend.CopyStatus(actor, out var status);
        await Assert.That(MathF.Abs(status.Velocity.Z + .4f)).IsLessThan(.0001f);
        await Assert.That(MathF.Abs(status.Position.Z - 99.97f)).IsLessThan(.0001f);
    }

    [Test]
    public async Task AuthoredColliderCenterControlsCeilingContactWhilePivotControlsLanding()
    {
        var statics = new ZoneManagedStaticGeometry();
        statics.AddInstance(Floor(0, 17), Matrix4x4.Identity);
        statics.AddInstance(Floor(3, 33), Matrix4x4.Identity);
        var backend = new ZoneManagedPhysicsBackend(new(new ZoneTerrainHeightField()), staticGeometry: statics);
        var actor = new ZoneActorHandle(905, 1);
        var stance = new ZoneActorPhysicalizationStance(new(.4f, .9f, .32f), default, default,
            1.2f, .15f, 5, true, false);
        backend.AddActor(actor, ZonePhysicsActorDescription.FromLivingStance(stance, 80, gravity: -25));
        backend.Teleport(actor, new(1, 2, .15f), Quaternion.Identity);
        backend.ApplyImpulse(actor, new(0, 0, 8));
        var maximum = .15f;
        for (var frame = 0; frame < 120; frame++)
        {
            backend.Step(TimeSpan.FromMilliseconds(10));
            backend.CopyStatus(actor, out var current);
            maximum = MathF.Max(maximum, current.Position.Z);
        }
        backend.CopyStatus(actor, out var standing);
        // Ceiling - (heightCollider - heightPivot) - (segment half-height + radius).
        await Assert.That(maximum).IsLessThanOrEqualTo(1.231f);
        await Assert.That(maximum).IsGreaterThan(1.20f);
        await Assert.That(standing.IsGrounded).IsTrue();
        await Assert.That(MathF.Abs(standing.Position.Z - .15f)).IsLessThan(.0001f);
        await Assert.That(backend.OwnedPhysics.TryReadGroundHeight(actor, out var height)).IsTrue();
        await Assert.That(height).IsEqualTo(0f);
        await Assert.That(backend.GroundMaterialFor(actor)).IsEqualTo((ushort?)17);
    }

    [Test]
    public async Task InvalidAuthoredOffsetsAndGravityAreRejectedBeforeActorRegistration()
    {
        var backend = Backend();
        var actor = new ZoneActorHandle(904, 1);
        var description = ZonePhysicsActorDescription.FromLivingStance(Stance(true), 80);
        await Assert.That(() => backend.AddActor(actor, description with { ColliderCenterOffsetZ = float.NaN }))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => backend.AddActor(actor, description with { GroundPivotHeight = float.PositiveInfinity }))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => backend.AddActor(actor, description with { Gravity = float.NaN }))
            .Throws<ArgumentOutOfRangeException>();
        backend.AddActor(actor, description);
        backend.CopyStatus(actor, out var status);
        await Assert.That(status.Position).IsEqualTo(Vector3.Zero);
    }

    private static ZoneManagedPhysicsBackend Backend() => new(new(new ZoneTerrainHeightField()));
    private static ZoneActorPhysicalizationStance Stance(bool useCapsule) =>
        new(new(.4f, .9f, .32f), default, default, 2.1f, .3f, 5, useCapsule, false);

    private static ZoneManagedTriangleMesh Floor(float z, ushort material) => new(
        [new(-100, -100, z), new(100, -100, z), new(100, 100, z), new(-100, 100, z)],
        [0, 1, 2, 0, 2, 3], [material, material]);
}
