using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Content;
using AAEmu.Zone.Physics;

namespace AAEmu.Zone.Tests;

public sealed class ManagedLivingMovementMathTests
{
    [Test]
    public async Task FreshStanceUsesOriginalConstructorDegreeAndCosineBits()
    {
        var settings = ZonePhysicsLivingMovementSettings.FromStance(Stance);
        await Bits(settings.MinimumSlideAngleDegrees, 0x42100000);
        await Bits(settings.MaximumClimbAngleDegrees, 0x42580001);
        await Bits(settings.MaximumJumpAngleDegrees, 0x42580001);
        await Bits(settings.MinimumFallAngleDegrees, 0x428c6666);
        await Bits(settings.MinimumSlideCosine, 0x3f4f1bbd);
        await Bits(settings.MaximumClimbCosine, 0x3f167917);
        await Bits(settings.MaximumJumpCosine, 0x3f167917);
        await Bits(settings.MinimumFallCosine, 0x3ead6f10);
        await Assert.That(settings.MaximumSupportVelocity).IsEqualTo(10f);
        await Assert.That(settings.Inertia).IsEqualTo(8f);
        await Assert.That(settings.InertiaAcceleration).IsEqualTo(0f);
        await Assert.That(settings.AirControl).IsEqualTo(.1f);
        await Assert.That(settings.AirResistance).IsEqualTo(0f);
        await Assert.That(settings.ImpulseRecoveryTime).IsEqualTo(5f);
    }

    [Test]
    public async Task InitialRequestCopiesAuthoredAnglesDimensionsAndIndependentSpeedFields()
    {
        var request = Request(scale: 2);
        var settings = ZonePhysicsLivingMovementSettings.FromRequest(request);
        await Assert.That(settings.MinimumSlideAngleDegrees).IsEqualTo(75f);
        await Assert.That(settings.MaximumClimbAngleDegrees).IsEqualTo(80f);
        await Assert.That(settings.MinimumFallAngleDegrees).IsEqualTo(80f);
        await Assert.That(settings.MaximumSupportVelocity).IsEqualTo(30f);
        await Assert.That(settings.StanceMaximumSpeed).IsEqualTo(3.8f);
        await Assert.That(settings.MaximumJumpAngleDegrees).IsEqualTo(ZonePhysicsLivingMovementSettings.DefaultMaximumJumpAngleDegrees);
        await Near(settings.HeightCollider, 2.4f); await Near(settings.HeightPivot, 2);
        await Near(settings.SegmentHalfHeight, 1.2f); await Near(settings.SupportHalfHeight, 1.2f);
        await Near(settings.SupportHeight, 1.2f); await Near(settings.SupportRayStartOffsetZ, -.8f);
        await Near(settings.SupportRayLength, 3.8f); await Near(settings.MaximumUnprojectionDistance, 2);
        request.Dynamics.Single(0x30, 1); request.Dynamics.Single(0x40, 500);
        request.Dimensions.Single(0x18, 100); request.Dimensions.Single(0x04, 40);
        await Assert.That(settings.MinimumSlideAngleDegrees).IsEqualTo(75f);
        await Assert.That(settings.MaximumSupportVelocity).IsEqualTo(30f);
        await Near(settings.HeightCollider, 2.4f); await Near(settings.HeightPivot, 2);
    }

    [Test]
    public async Task StanceReplacementPreservesDynamicsAndKeepsTinyRawGeometrySeparate()
    {
        var settings = ZonePhysicsLivingMovementSettings.FromRequest(Request());
        var updated = settings.WithStance(Stance with { Size = new(.2f, .9f, .0001f), HeightCollider = .7f, HeightPivot = 2, MaximumSpeed = 6 });
        await Assert.That(updated.MinimumSlideAngleDegrees).IsEqualTo(settings.MinimumSlideAngleDegrees);
        await Assert.That(updated.MaximumClimbCosine).IsEqualTo(settings.MaximumClimbCosine);
        await Assert.That(updated.MinimumFallCosine).IsEqualTo(settings.MinimumFallCosine);
        await Assert.That(updated.MaximumSupportVelocity).IsEqualTo(settings.MaximumSupportVelocity);
        await Assert.That(updated.AirControl).IsEqualTo(settings.AirControl);
        await Assert.That(updated.AirResistance).IsEqualTo(settings.AirResistance);
        await Assert.That(updated.ImpulseRecoveryTime).IsEqualTo(settings.ImpulseRecoveryTime);
        await Assert.That(updated.SegmentHalfHeight).IsEqualTo(.0001f);
        await Bits(updated.SupportHalfHeight, BitConverter.SingleToUInt32Bits(.001f));
        await Assert.That(updated.MaximumUnprojectionDistance).IsEqualTo(2f);
        await Assert.That(updated.StanceMaximumSpeed).IsEqualTo(6f);
        await Assert.That(settings.StanceMaximumSpeed).IsEqualTo(3.8f);
        await Near(settings.HeightCollider, 1.2f);
    }

    [Test]
    public async Task OnlyTheOriginalUnusedFloatMaskRetainsDefaults()
    {
        var request = Request();
        foreach (var offset in new[] { 0x04, 0x08, 0x0C, 0x10, 0x30, 0x34, 0x38, 0x3C, 0x40, 0x44 })
            request.Dynamics.Word(offset, ZoneActorPhysicalizationRequest.UnusedFloatBits);
        var settings = ZonePhysicsLivingMovementSettings.FromRequest(request);
        await Bits(settings.MinimumSlideCosine, 0x3f4f1bbd);
        await Bits(settings.MaximumClimbCosine, 0x3f167917);
        await Bits(settings.MinimumFallCosine, 0x3ead6f10);
        await Assert.That(settings.MaximumSupportVelocity).IsEqualTo(10f);
        await Assert.That(settings.AirControl).IsEqualTo(.1f);
        request.Dynamics.Word(0x30, 0x7fc00000); // Ordinary positive NaN is not the unused sentinel.
        await Assert.That(() => ZonePhysicsLivingMovementSettings.FromRequest(request)).Throws<NotSupportedException>();
        request.Dynamics.Single(0x30, float.PositiveInfinity);
        await Assert.That(() => ZonePhysicsLivingMovementSettings.FromRequest(request)).Throws<NotSupportedException>();
        request.Dynamics.Single(0x30, 0);
        var zeroAngle = ZonePhysicsLivingMovementSettings.FromRequest(request);
        await Assert.That(zeroAngle.MinimumSlideCosine).IsEqualTo(1f);
    }

    [Test]
    public async Task EverySlopeBoundaryKeepsItsOwnStrictComparison()
    {
        var request = Request();
        request.Dynamics.Single(0x30, 45); request.Dynamics.Single(0x34, 50); request.Dynamics.Single(0x3C, 60);
        var settings = ZonePhysicsLivingMovementSettings.FromRequest(request);
        var slideBoundary = ZoneLivingMovementMath.CompareSlope(settings.MinimumSlideCosine, settings);
        await Assert.That(slideBoundary.BelowSlideCosine).IsFalse();
        await Assert.That(ZoneLivingMovementMath.CompareSlope(MathF.BitDecrement(settings.MinimumSlideCosine), settings).BelowSlideCosine).IsTrue();
        await Assert.That(ZoneLivingMovementMath.CompareSlope(settings.MaximumClimbCosine, settings).BelowClimbCosine).IsFalse();
        await Assert.That(ZoneLivingMovementMath.CompareSlope(MathF.BitDecrement(settings.MaximumClimbCosine), settings).BelowClimbCosine).IsTrue();
        var fallBoundary = ZoneLivingMovementMath.CompareSlope(settings.MinimumFallCosine, settings);
        await Assert.That(fallBoundary.BelowFallCosine).IsFalse(); await Assert.That(fallBoundary.AboveFallCosine).IsFalse();
        await Assert.That(ZoneLivingMovementMath.CompareSlope(MathF.BitIncrement(settings.MinimumFallCosine), settings).AboveFallCosine).IsTrue();
        await Assert.That(ZoneLivingMovementMath.CompareSlope(MathF.BitDecrement(settings.MinimumFallCosine), settings).BelowFallCosine).IsTrue();
        var band = ZoneLivingMovementMath.CompareSlope(.68f, settings);
        await Assert.That(band.BelowSlideCosine).IsTrue(); await Assert.That(band.BelowClimbCosine).IsFalse();
        await Assert.That(band.AboveFallCosine).IsTrue();
    }

    [Test]
    public async Task ProjectionUsesTheTruePlaneAndDoesNotInventAHorizontalClamp()
    {
        var normal = new Vector3(.6f, 0, .8f);
        var gravity = ZoneLivingMovementMath.ProjectOntoPlane(new(0, 0, -10), normal);
        await Near(gravity, new(4.8f, 0, -3.6f));
        await Near(Vector3.Dot(gravity, normal), 0);
        var motion = ZoneLivingMovementMath.ProjectOntoPlane(new(5, 0, 0), normal);
        await Near(motion, new(3.2f, 0, -2.4f));
        await Assert.That(() => ZoneLivingMovementMath.ProjectOntoPlane(Vector3.One, normal * 2)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task SupportGeometryKeepsRawColliderSizeAndStrictLargeCoordinateRiseWindow()
    {
        var settings = ZonePhysicsLivingMovementSettings.FromStance(Stance with { Size = new(.4f, 999, 0), HeightCollider = .9f, HeightPivot = .2f });
        await Assert.That(settings.SegmentHalfHeight).IsEqualTo(0f);
        await Near(settings.SupportHalfHeight, .001f);
        await Near(settings.SupportHeight, .899f);
        var ray = ZoneLivingMovementMath.CreateSupportRay(new(8000, 8000, 100), settings);
        await Near(ray.Origin, new(8000, 8000, 100.699f), .00001f);
        await Near(ray.Displacement, new(0, 0, -3.3485f));
        var floor = 8000f;
        var boundary = floor - settings.SupportHeight;
        await Assert.That(ZoneLivingMovementMath.CanRaiseToSupport(boundary, floor, settings)).IsFalse();
        await Assert.That(ZoneLivingMovementMath.CanRaiseToSupport(MathF.BitIncrement(boundary), floor, settings)).IsTrue();
        await Assert.That(ZoneLivingMovementMath.CanRaiseToSupport(floor, floor, settings)).IsFalse();
        var unusual = ZonePhysicsLivingMovementSettings.FromStance(Stance with { HeightCollider = -4, HeightPivot = -10 });
        await Assert.That(unusual.HeightCollider).IsEqualTo(-4f);
        await Assert.That(unusual.SupportRayLength).IsLessThan(0f); // Retain source dimensions, never substitute .3m.
    }

    [Test]
    public async Task StanceEnvelopeUsesRawSegmentWithoutTheCapsuleHemisphere()
    {
        var settings = ZonePhysicsLivingMovementSettings.FromStance(Stance with { Size = new(.4f, .4f, .32f), HeightPivot = .1f });
        var bounds = ZoneLivingMovementMath.CreateStanceCandidateBounds(new(8000, 8000, 8000), settings);
        await Assert.That(bounds.Minimum).IsEqualTo(new Vector3(7999.6f, 7999.6f, 7999.9f));
        await Assert.That(bounds.Maximum).IsEqualTo(new Vector3(8000.4f, 8000.4f, 8001.42f));
        var colliderTop = 8000 + (settings.HeightCollider - settings.HeightPivot) + settings.SegmentHalfHeight + settings.Radius;
        await Assert.That(colliderTop - bounds.Maximum.Z).IsGreaterThan(.399f);
        var firstPass = ZoneLivingMovementMath.CreateStanceCandidateBounds(new(8000, 8000, 8000), .4f, .32f, 1.2f, 0);
        await Assert.That(firstPass.Minimum.Z).IsEqualTo(8000f);
        await Assert.That(firstPass.Maximum.Z).IsEqualTo(8001.52f);
    }

    [Test]
    public async Task AirSubroutineKeepsRequestedVelocityDistinctAndInheritsSupportOnlyWhenDeparting()
    {
        var settings = ZonePhysicsLivingMovementSettings.FromStance(Stance);
        var result = ZoneLivingMovementMath.ApplyAirGravityAndResistance(new(10, 0, 0), new(100, 0, 0),
            new(0, 0, -10), new(3, 0, 0), 8, .1f, settings, true, false, false);
        await Near(result.Velocity, new(10, 0, -1));
        await Assert.That(result.InheritedSupportVelocity).IsFalse();
        var departed = ZoneLivingMovementMath.ApplyAirGravityAndResistance(new(10, 0, 0), new(100, 0, 0),
            new(0, 0, -10), new(3, 0, 0), 8, .1f, settings, false, false, false);
        await Near(departed.Velocity, new(13, 0, -1));
        await Assert.That(departed.InheritedSupportVelocity).IsTrue();
        var retained = ZoneLivingMovementMath.ApplyAirGravityAndResistance(new(10, 0, 0), new(100, 0, 0),
            new(0, 0, -10), new(3, 0, 0), 8, .1f, settings, false, true, false);
        await Near(retained.Velocity, result.Velocity);
    }

    [Test]
    public async Task AirSubroutineAppliesFlag4BeforeResistanceAndKeepsTheOriginalOvershootGuard()
    {
        var request = Request(); request.Dynamics.Single(0x10, .5f);
        var settings = ZonePhysicsLivingMovementSettings.FromRequest(request);
        var result = ZoneLivingMovementMath.ApplyAirGravityAndResistance(Vector3.Zero, new(2, 0, 0),
            new(0, 0, -10), Vector3.Zero, 5, .1f, settings, true, false, true);
        await Near(result.Velocity, new(.95f, 0, -.95f));
        foreach (var (resistance, expected, stopped) in new[] { (15f, -5f, false), (20f, 0f, true), (-30f, 10f, false) })
        {
            request.Dynamics.Single(0x10, resistance); settings = ZonePhysicsLivingMovementSettings.FromRequest(request);
            var drag = ZoneLivingMovementMath.ApplyAirGravityAndResistance(new(10, 0, 0), Vector3.Zero,
                Vector3.Zero, Vector3.Zero, 8, .1f, settings, true, false, false);
            await Near(drag.Velocity, new(expected, 0, 0)); await Assert.That(drag.StoppedByResistance).IsEqualTo(stopped);
        }
    }

    private static readonly ZoneActorPhysicalizationStance Stance = new(new(.4f, .4f, .6f), default, default, 1.2f, 1, 3.8f, true, false);
    private static ZoneActorPhysicalizationRequest Request(float scale = 1)
    {
        var model = new ZoneActorModel(new ZoneActorModelDefinition
        {
            Id = 1, PhysicsLivingMinSlideAngle = 75, PhysicsLivingMaxClimbAngle = 80,
            PhysicsLivingMinFallAngle = 80, PhysicsLivingMaxVelGround = 30,
            PhysicsLivingKAirControl = .3f, PhysicsLivingAirResistance = .2f, PhysicsLivingTimeImpulseRecover = .5f,
        }, [new ZoneGameStanceDefinition { Id = 1, ActorModelId = 1, StanceId = 2, SizeX = .4f, SizeY = .4f,
            SizeZ = .6f, HeightCollider = 1.2f, HeightPivot = 1, MaxSpeed = 3.8f, UseCapsule = true }]);
        return ZoneActorPhysicalizationRequest.BuildInitial(model, scale, false, _ => null);
    }
    private static async Task Bits(float actual, uint expected) => await Assert.That(BitConverter.SingleToUInt32Bits(actual)).IsEqualTo(expected);
    private static async Task Near(float actual, float expected, float tolerance = .000002f) => await Assert.That(MathF.Abs(actual - expected)).IsLessThan(tolerance);
    private static async Task Near(Vector3 actual, Vector3 expected, float tolerance = .000002f) => await Assert.That(Vector3.Distance(actual, expected)).IsLessThan(tolerance);
}
