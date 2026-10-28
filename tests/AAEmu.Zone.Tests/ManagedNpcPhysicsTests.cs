using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Content;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Tests;

public sealed class ManagedNpcPhysicsTests
{
    [Test]
    public async Task ResolvedActorRequestUsesWorldScaleSourceOrderDynamicsAndNarrowedMaterial()
    {
        var models = Models();
        var admission = ZoneManagedNpcBodyAdmission.Build(models, 17, 2, _ => 143);
        await Assert.That(admission.Description.Shape.Kind).IsEqualTo(ZonePhysicsShapeKind.Capsule);
        await Assert.That(admission.Description.Shape.Radius).IsEqualTo(.6f);
        await Assert.That(admission.Description.Shape.SegmentHalfHeight).IsEqualTo(.6f);
        await Assert.That(admission.Description.Mass).IsEqualTo(640f);
        await Assert.That(admission.Description.Gravity).IsEqualTo(-25f);
        await Assert.That(admission.Description.GroundPivotHeight).IsEqualTo(.2f);
        await Assert.That(admission.Description.LivingMovement!.HeightCollider).IsEqualTo(2.4f);
        await Assert.That(admission.Description.LivingMovement.MinimumSlideAngleDegrees).IsEqualTo(42f);
        await Assert.That(admission.Description.PhysicalMaterialId).IsEqualTo((ushort)143);
        await Assert.That(admission.Request.EntityParameters.ReadByte(0x70)).IsEqualTo((byte?)0);
        await Assert.That(admission.Description.CollisionActive).IsTrue();
        await Assert.That(admission.Description.LivingFlag4).IsFalse();
        await Assert.That(ZoneManagedNpcBodyAdmission.Build(models, 17, 1, _ => 1025).Description.PhysicalMaterialId).IsEqualTo((ushort)1);
        await Assert.That(ZoneManagedNpcBodyAdmission.Build(models, 17, 1, _ => null).Description.PhysicalMaterialId).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task ActualWorldModelOwnsTheBodyEvenWhenTheNpcTemplateCachesAnotherModel()
    {
        using var world = new World(new() { Id = 99, Subtype = "PrefabModel", SubtypeId = 456, Big = true });
        using var body = world.Admit(new(1100, 1100, 10.1f));
        await Assert.That(world.Npc.Model.Id).IsEqualTo(99u);
        await Assert.That(body.Admission.ModelId).IsEqualTo(17u);
        await Assert.That(body.Admission.Request.ActorModel.Id).IsEqualTo(8u);
        await Assert.That(body.CopyState().AppliedDescription.Shape.Radius).IsEqualTo(.3f);
    }

    [Test]
    public async Task ActualNpcBodyFallsOntoLoadedSupportAndOnlyTheSceneStepMovesIt()
    {
        using var world = new World(); using var body = world.Admit(new(1100, 1100, 12));
        var originalUnitPose = world.Npc.Unit.Pose;
        body.RequestVelocity(new(2, 0, 0)); world.Bodies.PrepareFrame();
        body.CopyStatus(out var beforeStep);
        await Assert.That(beforeStep.Position).IsEqualTo(new Vector3(1100, 1100, 12));
        for (var i = 0; i < 80; i++) { world.Bodies.PrepareFrame(); world.Backend.Step(TimeSpan.FromMilliseconds(20)); }
        var copied = world.Bodies.CopyStatuses().Single();
        await Assert.That(copied.Status.IsGrounded).IsTrue();
        await Assert.That(MathF.Abs(copied.Status.Position.Z - 10.1f)).IsLessThan(.003f);
        await Assert.That(copied.Status.Position.X).IsGreaterThan(1101f);
        await Assert.That(world.Npc.Unit.Pose).IsEqualTo(originalUnitPose);
        await Assert.That(copied.Control.RequestedVelocity).IsEqualTo(new Vector3(2, 0, 0));
        await Assert.That(ReferenceEquals(world.Bodies.Backend, world.Backend)).IsTrue();
    }

    [Test]
    public async Task SceneWallStopsTheTrueLivingBodyAndTargetQueriesUseItsAppliedMaterial()
    {
        using var world = new World(); using var body = world.Admit(new(1100, 1100, 10.1f));
        world.Statics.AddInstance(new ZoneManagedTriangleMesh(
            [new(1101, 1098, 9), new(1101, 1102, 9), new(1101, 1102, 15), new(1101, 1098, 15)], [0, 1, 2, 0, 2, 3]), Matrix4x4.Identity);
        body.RequestVelocity(new(3, 0, 0));
        for (var i = 0; i < 80; i++) world.Backend.Step(TimeSpan.FromMilliseconds(20));
        body.CopyStatus(out var stopped);
        await Assert.That(stopped.Position.X).IsGreaterThan(1100.1f);
        await Assert.That(stopped.Position.X).IsLessThan(1100.71f);
        body.CopyDescription(out var applied);
        var center = stopped.Position + new Vector3(0, 0, applied.CenterOffsetZ);
        await Assert.That(world.Backend.TrySweep(ZonePhysicsShape.Box(new(.01f)), center - new Vector3(2, 0, 0),
            new(3, 0, 0), null, out var target)).IsTrue();
        await Assert.That(target.PhysicalMaterialId).IsEqualTo((ushort)143);
    }

    [Test]
    public async Task AcceptedStanceIsCopiedSeparatelyFromAppliedGeometryAndRetainsLiveDynamicsMaterial()
    {
        using var world = new World(); using var body = world.Admit(new(1100, 1100, 10.1f));
        world.Backend.SetLivingInertia(body.Actor, 4);
        body.RequestStanceSlot(0); world.Bodies.PrepareFrame();
        var pending = body.CopyState();
        await Assert.That(pending.CurrentStanceSlot).IsEqualTo(0);
        await Assert.That(pending.DesiredStanceSlot).IsEqualTo(0);
        await Assert.That(pending.CurrentStance.Size.X).IsEqualTo(.5f);
        await Assert.That(pending.AppliedDescription.Shape.Radius).IsEqualTo(.3f);
        await Assert.That(pending.LastStanceUpdate!.Value.Phase).IsEqualTo(ZoneManagedLivingStancePhase.Queued);
        world.Backend.Step(TimeSpan.FromMilliseconds(20));
        var applied = body.CopyState();
        await Assert.That(applied.AppliedDescription.Shape.Radius).IsEqualTo(.5f);
        await Assert.That(applied.AppliedDescription.LivingMovement!.Inertia).IsEqualTo(4f);
        await Assert.That(applied.AppliedDescription.PhysicalMaterialId).IsEqualTo((ushort)143);
        await Assert.That(applied.LastStanceUpdate!.Value.Phase).IsEqualTo(ZoneManagedLivingStancePhase.Applied);
    }

    [Test]
    public async Task BlockedStanceKeepsItsWishAndRetriesAfterTheActualCeilingRetires()
    {
        using var world = new World(); using var body = world.Admit(new(1100, 1100, 10.1f));
        var ceiling = world.Statics.AddInstance(new ZoneManagedTriangleMesh(
            [new(1098, 1098, 12), new(1102, 1098, 12), new(1102, 1102, 12), new(1098, 1102, 12)], [0, 1, 2, 0, 2, 3]), Matrix4x4.Identity);
        body.RequestStanceSlot(0); world.Bodies.PrepareFrame();
        var refused = body.CopyState();
        await Assert.That(refused.CurrentStanceSlot).IsEqualTo(1);
        await Assert.That(refused.DesiredStanceSlot).IsEqualTo(0);
        await Assert.That(refused.StanceRefusal).IsNotNull();
        world.Backend.Step(TimeSpan.FromMilliseconds(20));
        await Assert.That(body.CopyState().AppliedDescription.Shape.Radius).IsEqualTo(.3f);
        body.RequestStanceSlot(-1); world.Statics.RemoveInstance(ceiling); world.Bodies.PrepareFrame();
        world.Backend.Step(TimeSpan.FromMilliseconds(20));
        await Assert.That(body.CopyState().CurrentStanceSlot).IsEqualTo(0);
        await Assert.That(body.CopyState().AppliedDescription.Shape.Radius).IsEqualTo(.5f);
    }

    [Test]
    public async Task RetirementCancelsQueuedMotionAndStancesWithoutAliasingFreshGenerationOrOtherBodies()
    {
        using var world = new World(); var body = world.Admit(new(1100, 1100, 10.1f));
        body.RequestVelocity(new(9, 0, 0), ZonePhysicsLivingVelocityMode.Immediate);
        body.RequestStanceSlot(0); world.Bodies.PrepareFrame(); var old = body.Actor;
        body.Dispose(); world.Admission.Catalog.Retire(old); world.Admission.Registry.RemovePrepared(world.Npc.Unit);
        var replacement = world.Admission.Npc(old.UnitObjectId, world.Npc.Id, world.Model);
        world.Admission.Publish(replacement); var handle = world.Admission.Catalog.Register(replacement);
        using var current = world.Bodies.Create(handle, replacement, new(1100, 1100, 10.1f), Quaternion.Identity,
            () => ReferenceEquals(world.Admission.Catalog.Find(handle), replacement));
        await Assert.That(handle.Generation).IsGreaterThan(old.Generation);
        await Assert.That(body.TryRequestVelocity(Vector3.UnitX)).IsFalse();
        await Assert.That(body.TryReadPosition(out _)).IsFalse();
        await Assert.That(current.CopyState().Control.HasPendingVelocityRequest).IsFalse();
        world.Backend.Step(TimeSpan.FromMilliseconds(20));
        await Assert.That(current.CopyState().Status.Position.X).IsEqualTo(1100f);
        await Assert.That(current.CopyState().AppliedDescription.Shape.Radius).IsEqualTo(.3f);
        var external = new ZoneActorHandle(old.UnitObjectId + 1_000_000, 1);
        world.Backend.AddActor(external, new(ZonePhysicsShape.Box(new(.1f)), 1));
        world.Bodies.Dispose();
        await Assert.That(world.Backend.OwnedPhysics.TryReadPosition(external, out _)).IsTrue();
    }

    [Test]
    public async Task ExactOwnerRetirementBeforePrephaseRemovesTheBodyAndPendingRequests()
    {
        using var world = new World(); using var body = world.Admit(new(1100, 1100, 10.1f));
        body.RequestVelocity(new(7, 0, 0)); world.Admission.Catalog.Retire(body.Actor);
        world.Bodies.PrepareFrame();
        await Assert.That(body.IsDisposed).IsTrue();
        await Assert.That(world.Bodies.Count).IsEqualTo(0);
        await Assert.That(world.Backend.OwnedPhysics.TryReadPosition(body.Actor, out _)).IsFalse();
    }

    [Test]
    public async Task AdmissionCallbackRetirementAndDuplicateBackendRegistrationNeverPublishOrRemoveAnotherOwner()
    {
        using var world = new World(); var handle = world.Admission.Catalog.Register(world.Npc);
        using var retiring = new ZoneManagedNpcPhysics(world.Backend, world.Models, _ =>
        { world.Admission.Catalog.Retire(handle); return 143; });
        await Assert.That(() => retiring.Create(handle, world.Npc, Vector3.Zero, Quaternion.Identity,
            () => ReferenceEquals(world.Admission.Catalog.Find(handle), world.Npc))).Throws<InvalidOperationException>();
        await Assert.That(retiring.Count).IsEqualTo(0);
        await Assert.That(world.Backend.OwnedPhysics.TryReadPosition(handle, out _)).IsFalse();
        world.Admission.Registry.RemovePrepared(world.Npc.Unit);
        var replacement = world.Admission.Npc(world.Npc.ObjectId, world.Npc.Id, world.Model);
        world.Admission.Publish(replacement);
        var fresh = world.Admission.Catalog.Register(replacement);
        world.Backend.AddActor(fresh, new(ZonePhysicsShape.Box(new(.2f)), 2));
        world.Backend.Teleport(fresh, new(1110, 1110, 1110), Quaternion.Identity);
        await Assert.That(() => world.Bodies.Create(fresh, replacement, Vector3.Zero, Quaternion.Identity,
            () => true)).Throws<InvalidOperationException>();
        world.Backend.CopyStatus(fresh, out var untouched);
        await Assert.That(untouched.Position).IsEqualTo(new Vector3(1110));
        await Assert.That(world.Bodies.Count).IsEqualTo(0);
    }

    [Test]
    public async Task UnportedPhysicalProfilesAreRefusedInsteadOfUsingRenderBoundsOrKinematicStandIns()
    {
        foreach (var definition in new[] { Definition() with { AddBox = true }, Definition() with { FlyMode = true },
            Definition() with { UnderwaterCreature = true }, Definition() with { PhysicsFlags = 8 } })
            await Assert.That(() => ZoneManagedNpcBodyAdmission.Build(Models(definition), 17, 1, _ => 143)).Throws<NotSupportedException>();
        await Assert.That(() => ZoneManagedNpcBodyAdmission.Build(Models(), 100, 1, _ => 143)).Throws<NotSupportedException>();
        await Assert.That(() => ZoneManagedNpcBodyAdmission.Build(Models(), 17, float.NaN, _ => 143)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task TiltedGroundProfilesRefuseWithoutPoisoningAnEquivalentUprightQuaternion()
    {
        using var world = new World(); var actor = world.Admission.Catalog.Register(world.Npc);
        await Assert.That(() => world.Bodies.Create(actor, world.Npc, new(1100, 1100, 10.1f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, .1f), () => true)).Throws<NotSupportedException>();
        await Assert.That(world.Bodies.Count).IsEqualTo(0);
        using var body = world.Bodies.Create(actor, world.Npc, new(1100, 1100, 10.1f), new(0, 0, 0, -1), () => true);
        body.CopyStatus(out var status);
        await Assert.That(status.Orientation).IsEqualTo(new Quaternion(0, 0, 0, -1));
    }

    private static ZoneActorModelDefinition Definition() => new()
    {
        Id = 8, PhysicsLivingMass = 80, PhysicsLivingGravity = 25, PhysicsLivingKAirControl = .1f,
        PhysicsLivingColliderMat = "mat_player_collider", PhysicsLivingMinSlideAngle = 42,
        PhysicsLivingMaxClimbAngle = 55, PhysicsLivingMinFallAngle = 72,
        PhysicsLivingMaxVelGround = 10, PhysicsLivingTimeImpulseRecover = 5,
    };
    private static ZoneModelContent Models(ZoneActorModelDefinition? definition = null) => new(
        [new() { Id = 17, Name = "physical_npc", Subtype = "ActorModel", SubtypeId = 8 }],
        actorModels: new([definition ?? Definition()],
        [new() { Id = 1, ActorModelId = 8, StanceId = 1, SizeX = .5f, SizeY = .9f, SizeZ = 1,
            HeightCollider = 2, HeightPivot = .1f, UseCapsule = true, MaxSpeed = 4 },
         new() { Id = 2, ActorModelId = 8, StanceId = 2, SizeX = 9, SizeY = 9, SizeZ = 9 },
         new() { Id = 3, ActorModelId = 8, StanceId = 2, SizeX = .3f, SizeY = .7f, SizeZ = .3f,
            HeightCollider = 1.2f, HeightPivot = .1f, UseCapsule = true, MaxSpeed = 3 }]));

    internal sealed class World : IDisposable
    {
        public ManagedNpcAdmissionTests.Fixture Admission { get; } = new();
        public ZoneModelContent Models { get; } = ManagedNpcPhysicsTests.Models();
        public ZoneModelDefinition Model => Models.Models[17];
        public AAEmu.Zone.Npcs.ZoneNpc Npc { get; }
        public ZoneManagedStaticGeometry Statics { get; } = new();
        public ZoneManagedPhysicsBackend Backend { get; }
        public ZoneManagedNpcPhysics Bodies { get; }
        public World(ZoneModelDefinition? templateModel = null)
        {
            var terrain = new ZoneTerrainHeightField(); terrain.LoadCell(1, 1, TerrainHeightFieldTests.CreateCell(10, 0));
            Backend = new(new(terrain), staticGeometry: Statics); Bodies = new(Backend, Models, _ => 143);
            Npc = Admission.Npc(model: templateModel ?? Model, unitModelId: 17); Admission.Publish(Npc);
        }
        public ZoneManagedNpcBodyLease Admit(Vector3 position)
        {
            var handle = Admission.Catalog.Register(Npc);
            return Bodies.Create(handle, Npc, position, Quaternion.Identity,
                () => ReferenceEquals(Admission.Catalog.Find(handle), Npc));
        }
        public void Dispose() { try { Bodies.Dispose(); } finally { Admission.Dispose(); } }
    }
}
