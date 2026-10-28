using System.Numerics;
using AAEmu.Zone.Content;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.Tests;

public sealed class ManagedNpcSkillGeometryTests
{
    [Test]
    public async Task ActualTemplate7987AimSpheresAdmitThirtyUnitOriginsForItsTwentyFiveUnitRange()
    {
        using var f = new Fixture();
        f.Transforms[f.Caster] = new(970, new(8000, 7900, 350), Quaternion.Identity, 1);
        f.Transforms[f.Target] = new(970, new(8030, 7900, 350), Quaternion.Identity, 1);
        var originalPose = f.Caster.Pose;
        await Assert.That(f.Geometry.CopyAimSphere(f.Caster)).IsEqualTo(new ZoneNpcSkillAimSphere(new(8000, 7900, 352.5f), 2.5f));
        await Assert.That(f.Geometry.ValidateLocation(f.Caster, f.Target, new(14133), 0, 25)).IsEqualTo((byte)0);
        await Assert.That(f.Geometry.Distance(f.Caster, f.Target)).IsEqualTo(25f);
        f.Transforms[f.Target] = f.Transforms[f.Target] with { Position = new(8030.01f, 7900, 350) };
        await Assert.That(f.Geometry.ValidateLocation(f.Caster, f.Target, new(14133), 0, 25)).IsEqualTo((byte)15);
        await Assert.That(f.Caster.Pose).IsEqualTo(originalPose);
    }

    [Test]
    public async Task PositiveMinimumIsStrictMaximumInclusiveAndOverlappingDistanceRemainsSigned()
    {
        using var f = new Fixture();
        f.Transforms[f.Target] = new(970, new(6, 0, 0), Quaternion.Identity, 1);
        await Assert.That(f.Geometry.ValidateLocation(f.Caster, f.Target, new(1), 1, 25)).IsEqualTo((byte)14);
        f.Transforms[f.Target] = f.Transforms[f.Target] with { Position = new(6.01f, 0, 0) };
        await Assert.That(f.Geometry.ValidateLocation(f.Caster, f.Target, new(1), 1, 25)).IsEqualTo((byte)0);
        f.Transforms[f.Target] = f.Transforms[f.Target] with { Position = Vector3.Zero };
        await Assert.That(f.Geometry.Distance(f.Caster, f.Target)).IsEqualTo(-5f);
        await Assert.That(f.Geometry.ValidateLocation(f.Caster, f.Target, new(1), 0, 0)).IsEqualTo((byte)0);
    }

    [Test]
    public async Task HeightUsesAimCentersAndTheAuthoredEdgeToEdgeRadiusSubtraction()
    {
        using var f = new Fixture();
        f.Transforms[f.Target] = new(970, new(0, 0, 10), Quaternion.Identity, 1);
        await Assert.That(f.Geometry.ValidateLocation(f.Caster, f.Target, new(1) { ValidHeight = 5 }, 0, 25)).IsEqualTo((byte)13);
        await Assert.That(f.Geometry.ValidateLocation(f.Caster, f.Target,
            new(1) { ValidHeight = 5, ValidHeightEdgeToEdge = true }, 0, 25)).IsEqualTo((byte)0);
        await Assert.That(f.Geometry.ValidateLocation(f.Caster, f.Target,
            new(1) { ValidHeight = 4.99f, ValidHeightEdgeToEdge = true }, 0, 25)).IsEqualTo((byte)13);
    }

    [Test]
    public async Task FallbackDistanceTakesTheSmallerOriginMetricWhileLocationStillUsesAimVolumes()
    {
        using var f = new Fixture(100, .1f, 0, .1f);
        f.Transforms[f.Target] = new(971, new(3, 0, 0), Quaternion.Identity, 1);
        await Assert.That(f.Geometry.Distance(f.Caster, f.Target)).IsEqualTo(3f);
        await Assert.That(f.Geometry.ValidateLocation(f.Caster, f.Target, new(1), 0, 25)).IsEqualTo((byte)15);
    }

    [Test]
    public async Task SphereProducerPreservesRawQuaternionAndActualUniformScale()
    {
        var sphere = ZoneManagedNpcSkillGeometry.TransformActorSphere(new() { Height = 2.5f, Radius = 2.5f },
            new(970, new(8000, 7900, 350), new(0, .5f, 0, .5f), 2));
        await Assert.That(sphere).IsEqualTo(new ZoneNpcSkillAimSphere(new(8002.5f, 7900, 352.5f), 5));
        await Assert.That(() => ZoneManagedNpcSkillGeometry.TransformActorSphere(new() { Height = 2.5f, Radius = 2.5f },
            new(970, Vector3.Zero, Quaternion.Identity, -1))).Throws<NotSupportedException>();
    }

    [Test]
    public async Task UnownedProfilesRefuseOnlyWhenReachedAndActualWorldModelChoosesTheAimGeometry()
    {
        using var f = new Fixture();
        //The fixture's Npc.Model cache is model17; the actual admitted Unit model is970.
        await Assert.That(f.Geometry.CopyAimSphere(f.Caster).Radius).IsEqualTo(2.5f);
        var partial = new ZoneNpcSkillDefinition(1) { TargetAngle = 90 };
        await Assert.That(() => f.Geometry.ValidateLocation(f.Caster, f.Target, partial, 0, 25)).Throws<NotSupportedException>();
        await Assert.That(() => f.Geometry.ValidateLocation(f.Caster, f.Target,
            new(1) { CheckObstacle = true }, 0, 25)).Throws<NotSupportedException>();
        f.Transforms[f.Target] = new(970, new(100, 0, 0), Quaternion.Identity, 1);
        await Assert.That(f.Geometry.ValidateLocation(f.Caster, f.Target, partial, 0, 25)).IsEqualTo((byte)15);
        f.Transforms[f.Target] = new(999, Vector3.Zero, Quaternion.Identity, 1);
        await Assert.That(() => f.Geometry.CopyAimSphere(f.Target)).Throws<NotSupportedException>();
    }

    [Test]
    public async Task RetirementDuringARealTransformCopyCannotLeaveAValidRangeResult()
    {
        using var f = new Fixture();
        var service = new ZoneManagedNpcSkillGeometry(f.World.Registry, f.Models, unit =>
        {
            if (ReferenceEquals(unit, f.Target)) f.World.Registry.RemovePrepared(f.Caster);
            return f.Transforms[unit];
        });
        await Assert.That(() => service.ValidateLocation(f.Caster, f.Target, new(1), 0, 25)).Throws<InvalidOperationException>();
        await Assert.That(() => f.Geometry.Distance(f.Caster, f.Target)).Throws<InvalidOperationException>();
    }

    private sealed class Fixture : IDisposable
    {
        public ManagedNpcAdmissionTests.Fixture World { get; } = new();
        public ZoneUnit Caster { get; }
        public ZoneUnit Target { get; }
        public Dictionary<ZoneUnit, ZoneNpcSkillModelTransform> Transforms { get; } = [];
        public ZoneModelContent Models { get; }
        public ZoneManagedNpcSkillGeometry Geometry { get; }

        public Fixture(float sourceHeight = 2.5f, float sourceRadius = 2.5f, float? targetHeight = null, float? targetRadius = null)
        {
            Models = new([new() { Id = 970, Subtype = "ActorModel", SubtypeId = 433 },
                new() { Id = 971, Subtype = "ActorModel", SubtypeId = 434 }], actorModels: new(
                [new() { Id = 433, Height = sourceHeight, Radius = sourceRadius },
                 new() { Id = 434, Height = targetHeight ?? sourceHeight, Radius = targetRadius ?? sourceRadius }], []));
            var source = World.Npc(objectId: 100, npcId: 1000, unitModelId: 970);
            var target = World.Npc(objectId: 101, npcId: 1001, unitModelId: targetHeight.HasValue ? 971u : 970u);
            World.Publish(source); World.Publish(target); Caster = source.Unit; Target = target.Unit;
            Transforms[Caster] = new(Caster.Snapshot.ModelId, Vector3.Zero, Quaternion.Identity, 1);
            Transforms[Target] = new(Target.Snapshot.ModelId, Vector3.Zero, Quaternion.Identity, 1);
            Geometry = new(World.Registry, Models, unit => Transforms[unit]);
        }

        public void Dispose() => World.Dispose();
    }
}
