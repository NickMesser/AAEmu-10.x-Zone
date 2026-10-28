using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Content;
using AAEmu.Zone.Physics;

namespace AAEmu.Zone.Tests;

public sealed class ManagedLivingVelocityMathTests
{
    [Test]
    public async Task EffectiveInertiaUsesIndependentAccelerationAndRecoveryGates()
    {
        var settings = Settings(inertiaAcceleration: 3);
        await Near(ZoneLivingMovementMath.SelectEffectiveInertia(new(.2f, .2f, 0), 0, settings), 8);
        await Near(ZoneLivingMovementMath.SelectEffectiveInertia(new(.3f, .3f, 0), 0, settings), 3);
        await Near(ZoneLivingMovementMath.SelectEffectiveInertia(Vector3.UnitX, .0001f, settings), 3);
        await Near(ZoneLivingMovementMath.SelectEffectiveInertia(Vector3.UnitX, MathF.BitIncrement(.0001f), settings), 6);
        await Near(ZoneLivingMovementMath.SelectEffectiveInertia(Vector3.Zero, 1, settings), 6);
        await Near(ZoneLivingMovementMath.SelectEffectiveInertia(Vector3.UnitX, 0, Settings()), 8);
    }

    [Test]
    public async Task GroundPreparationProjectsWishOnlyForZeroInertiaAndRetainsMode3Override()
    {
        var normal = new Vector3(.6f, 0, .8f); var actual = new Vector3(1, 0, 7); var wish = new Vector3(5, 0, 0);
        var projected = Prepare(actual, wish, Settings(inertia: 0), false, normal: normal);
        await Near(projected.Velocity, new(3.2f, 0, -2.4f));
        await Near(Prepare(actual, wish, Settings(inertia: 0), false, flag2: true, normal: normal).Velocity, actual);
        await Near(Prepare(actual, wish, Settings(), false, normal: normal).Velocity, actual);
        var forced = Prepare(actual, wish, Settings(inertia: 0), false, force: true, normal: normal);
        await Near(forced.Velocity, wish); await Assert.That(forced.OverrodeWithRequestedVelocity).IsTrue();
    }

    [Test]
    public async Task TenAirStepsMatchTheExecutedOriginalControlVelocities()
    {
        // Original controls: mode0 wishX1, gravity25,10*.01s; source artifacts live outside the
        // repo in native-living-movement-followup-results.json. No native module is needed here.
        foreach (var (control, inertia, expectedX) in new[] { (0f, 8f, 0f), (.1f, 8f, .08f), (1f, 8f, 1f), (.1f, 0f, 1f) })
        {
            var settings = Settings(inertia: inertia, airControl: control);
            var velocity = Vector3.Zero;
            for (var i = 0; i < 10; i++)
            {
                var prepared = Prepare(velocity, Vector3.UnitX, settings, true, seconds: .01f);
                velocity = ZoneLivingMovementMath.ApplyAirGravityAndResistance(prepared.Velocity, Vector3.UnitX,
                    new(0, 0, -25), Vector3.Zero, prepared.EffectiveInertia, .01f, settings, true, false, false).Velocity;
            }
            await Near(velocity, new(expectedX, 0, -2.5f));
        }
        var resistanceSettings = Settings(airControl: 0, airResistance: 1);
        var falling = Vector3.Zero;
        for (var i = 0; i < 10; i++)
            falling = ZoneLivingMovementMath.ApplyAirGravityAndResistance(falling, Vector3.Zero, new(0, 0, -25),
                Vector3.Zero, 8, .01f, resistanceSettings, true, false, false).Velocity;
        await Near(falling.Z, -2.366544f, .000003f);
    }

    [Test]
    public async Task AirControlUsesWishBasedSignedIncrementsAndOnlyBypassesXyAtOne()
    {
        var settings = Settings(airControl: .1f);
        await Near(Prepare(new(2, -2, -3), new(1, -1, 0), settings, true).Velocity, new(2, -2, -3));
        await Near(Prepare(new(-.5f, .5f, -3), new(1, -1, 0), settings, true).Velocity, new(-.42f, .42f, -3));
        await Near(Prepare(new(.99f, -.99f, -3), new(1, -1, 0), settings, true).Velocity, new(1, -1, -3));
        var fullControl = Settings(airControl: 1);
        await Near(Prepare(new(2, 2, -3), new(1, -1, 1), fullControl, true, seconds: .01f).Velocity, new(1, -1, -2.92f));
        var recovery = Prepare(Vector3.Zero, Vector3.UnitX, settings, true, seconds: .01f, timer: 1);
        await Near(recovery.Velocity.X, .008f); await Near(recovery.EffectiveInertia, 6);
    }

    [Test]
    public async Task ZeroBaseInertiaRetainsTheOldGravityComponentForArbitraryGravityDirection()
    {
        var settings = Settings(inertia: 0);
        var actual = new Vector3(3, 4, 5); var wish = new Vector3(1, 2, 3);
        var result = ZoneLivingMovementMath.PrepareVelocity(actual, wish, new(-2, 0, 0), Vector3.UnitZ,
            .1f, settings, true, false, false, 0);
        await Near(result.Velocity, new(3, 2, 3));
        await Near(ZoneLivingMovementMath.PrepareVelocity(actual, wish, Vector3.Zero, Vector3.UnitZ,
            .1f, settings, true, false, false, 0).Velocity, wish);
    }

    [Test]
    public async Task GroundRelaxationUsesIndependentWishAndCapsItsSingleStepCorrection()
    {
        var response = Respond(Vector3.Zero, new(2, 0, 0), Vector3.UnitZ, Settings(), .01f, 8);
        await Near(response.Velocity, new(.16f, 0, 0)); await Near(response.Acceleration, new(16, 0, 0));
        var saturated = Respond(new(7, -1, 0), new(1, 2, 0), Vector3.UnitZ, Settings(), .25f, 100);
        await Near(saturated.Velocity, new(1, 2, 0));
        var verticalWish = Respond(Vector3.Zero, new(0, 0, 5), Vector3.UnitZ, Settings(), .1f, 8);
        await Near(verticalWish.Velocity, Vector3.Zero);
        var requestedReversal = Respond(Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, Settings(), .1f, 100);
        await Near(requestedReversal.Velocity, -Vector3.UnitX); await Assert.That(requestedReversal.StoppedByReversal).IsFalse();
    }

    [Test]
    public async Task SlidingAddsActualTangentGravityOnlyBelowItsOwnThreshold()
    {
        var normal = new Vector3(.6f, 0, .8f);
        var sliding = Respond(Vector3.Zero, Vector3.Zero, normal, Settings(slide: 30, climb: 60), .1f, 0);
        await Near(sliding.Acceleration, new(4.8f, 0, -3.6f)); await Near(sliding.Velocity, new(.48f, 0, -.36f));
        await Assert.That(sliding.AppliedSlideGravity).IsTrue(); await Assert.That(sliding.ClimbLimited).IsFalse();
        var standing = Respond(Vector3.Zero, Vector3.Zero, normal, Settings(slide: 45, climb: 60), .1f, 0);
        await Near(standing.Velocity, Vector3.Zero); await Assert.That(standing.AppliedSlideGravity).IsFalse();
        var flag4 = Respond(Vector3.Zero, Vector3.Zero, normal, Settings(slide: 30, climb: 60), .1f, 0, flag4: true);
        await Near(flag4.Velocity, Vector3.Zero); await Assert.That(flag4.AppliedSlideGravity).IsFalse();
        var reversing = Respond(new(-.06f, 0, .045f), Vector3.Zero, normal, Settings(slide: 30, climb: 60), .1f, 0);
        await Near(reversing.Velocity, Vector3.Zero); await Assert.That(reversing.StoppedByReversal).IsTrue();
    }

    [Test]
    public async Task ClimbLimitsPostMoveVelocityWithoutRejectingTheAlreadyCompletedPose()
    {
        var normal = new Vector3(-.6f, 0, .8f);
        var limited = Respond(Vector3.Zero, Vector3.UnitX, normal, Settings(slide: 80, climb: 20), .1f, 10);
        var allowed = Respond(Vector3.Zero, Vector3.UnitX, normal, Settings(slide: 80, climb: 60), .1f, 10);
        await Near(limited.Velocity, new(.64f, 0, 0)); await Near(allowed.Velocity, new(.64f, 0, .48f));
        await Assert.That(limited.ClimbLimited).IsTrue(); await Assert.That(allowed.ClimbLimited).IsFalse();
        var settings = Settings(slide: 80, climb: 20);
        var threshold = settings.SupportHalfHeight * .001f;
        var atBoundary = Respond(-Vector3.UnitZ, -Vector3.UnitZ, normal, settings, .1f, 0, rise: threshold);
        var beyondBoundary = Respond(-Vector3.UnitZ, -Vector3.UnitZ, normal, settings, .1f, 0, rise: MathF.BitIncrement(threshold));
        await Near(atBoundary.Velocity.Z, -1); await Near(beyondBoundary.Velocity.Z, 0);
        // Binary MOVSS/MULSS threshold: .001f*.001f is0x358637be, one ULP above a
        // double-literal multiplication rounded to float. Native equality must remain allowed.
        var tiny = Settings(slide: 80, climb: 20, halfHeight: .001f);
        var singleBoundary = BitConverter.UInt32BitsToSingle(0x358637be);
        await Near(Respond(-Vector3.UnitZ, -Vector3.UnitZ, normal, tiny, .1f, 0, rise: singleBoundary).Velocity.Z, -1);
        await Near(Respond(-Vector3.UnitZ, -Vector3.UnitZ, normal, tiny, .1f, 0, rise: MathF.BitIncrement(singleBoundary)).Velocity.Z, 0);
    }

    [Test]
    public async Task ClimbEqualityIsAllowedInPostResponseButHasItsOwnLandingGate()
    {
        var settings = Settings(slide: 80, climb: 50, fall: 80);
        var z = settings.MaximumClimbCosine; var normal = new Vector3(-MathF.Sqrt(1 - z * z), 0, z);
        var response = Respond(Vector3.Zero, Vector3.UnitX, normal, settings, .1f, 10);
        await Assert.That(response.Velocity.Z).IsGreaterThan(0f); await Assert.That(response.ClimbLimited).IsFalse();
        var landing = ZoneLivingMovementMath.PrepareGroundContact(new(1, 0, 1), Vector3.UnitX, normal, Vector3.UnitZ,
            0, 0, settings, true, false, false, false, true, 60);
        await Near(landing.Velocity, Vector3.Zero); await Assert.That(landing.ZeroedOnLanding).IsTrue();
    }

    [Test]
    public async Task LandingReturnsCopiedRecoveryAndStrictPoseRaiseRatherThanMovingTheBody()
    {
        var settings = Settings(pivot: .5f, halfHeight: .6f);
        var result = ZoneLivingMovementMath.PrepareGroundContact(new(2, 0, -5), Vector3.Zero, Vector3.UnitZ, Vector3.UnitZ,
            .4f, 0, settings, true, false, false, true, false, 60);
        await Near(result.Velocity, Vector3.Zero); await Assert.That(result.ZeroedOnLanding).IsTrue();
        await Near(result.RaiseAlongUp!.Value, .1f); await Near(result.LandingResponse!.Value.UpVelocity, -5);
        await Near(result.LandingResponse.Value.Acceleration, 60);
        var hard = ZoneLivingMovementMath.PrepareGroundContact(new(0, 0, -10), Vector3.UnitX, Vector3.UnitZ, Vector3.UnitZ,
            .5f, 0, settings, true, false, false, true, false, 60);
        await Near(hard.LandingResponse!.Value.Acceleration, 100 / 1.2f, .00002f);
        var ongoing = ZoneLivingMovementMath.PrepareGroundContact(new(2, 0, -5), Vector3.Zero, Vector3.UnitZ, Vector3.UnitZ,
            .4f, 0, settings, false, false, false, true, false, 60);
        await Near(ongoing.Velocity, new(2, 0, -5)); await Assert.That(ongoing.RaiseAlongUp).IsNull();
        await Assert.That(ongoing.LandingResponse).IsNull();
    }

    [Test]
    public async Task HardLandingAndPostFallKickKeepTheExplicitBlockedGate()
    {
        var settings = Settings(slide: 80, climb: 80, fall: 20, pivot: .5f);
        var normal = new Vector3(.6f, 0, .8f);
        var landing = ZoneLivingMovementMath.PrepareGroundContact(new(0, 0, -5), Vector3.UnitX, normal, Vector3.UnitZ,
            .4f, 0, settings, true, false, false, false, true, 60);
        await Near(landing.Velocity, new(2.4f, 0, -1.8f)); await Assert.That(landing.RequestsFlying).IsTrue();
        await Assert.That(landing.RaiseAlongUp).IsNull();
        var blockedLanding = ZoneLivingMovementMath.PrepareGroundContact(new(0, 0, -5), Vector3.UnitX, normal, Vector3.UnitZ,
            .4f, 0, settings, true, false, true, false, true, 60);
        await Assert.That(blockedLanding.RequestsFlying).IsFalse(); await Near(blockedLanding.RaiseAlongUp!.Value, .1f);
        var fall = Respond(Vector3.Zero, Vector3.Zero, normal, settings, .1f, 0);
        await Near(fall.Velocity, new(.6f, 0, 0)); await Assert.That(fall.RequestsFlying).IsTrue();
        var blocked = Respond(Vector3.Zero, Vector3.Zero, normal, settings, .1f, 0, blocked: true);
        await Near(blocked.Velocity, Vector3.Zero); await Assert.That(blocked.RequestsFlying).IsFalse();
    }

    [Test]
    public async Task GroundDeadbandsDependOnWhetherWishIsZero()
    {
        await Near(Respond(new(.02f, 0, 0), Vector3.Zero, Vector3.UnitZ, Settings(), .1f, 0).Velocity, Vector3.Zero);
        await Near(Respond(new(.02f, 0, 0), Vector3.UnitX, Vector3.UnitZ, Settings(), .1f, 0).Velocity, new(.02f, 0, 0));
        await Near(Respond(new(.005f, 0, 0), Vector3.UnitX, Vector3.UnitZ, Settings(), .1f, 0).Velocity, Vector3.Zero);
    }

    private static ZonePhysicsLivingPreparedVelocity Prepare(Vector3 actual, Vector3 wish, ZonePhysicsLivingMovementSettings settings,
        bool flying, bool flag2 = false, bool force = false, Vector3? normal = null, float seconds = .1f, float timer = 0) =>
        ZoneLivingMovementMath.PrepareVelocity(actual, wish, new(0, 0, -25), normal ?? Vector3.UnitZ, seconds,
            settings, flying, flag2, force, timer);
    private static ZonePhysicsLivingGroundVelocity Respond(Vector3 actual, Vector3 wish, Vector3 normal,
        ZonePhysicsLivingMovementSettings settings, float seconds, float inertia, bool flag4 = false, bool blocked = false, float rise = 0) =>
        ZoneLivingMovementMath.ApplyGroundVelocityResponse(actual, wish, new(0, 0, -10), normal, Vector3.UnitZ,
            seconds, inertia, settings, flag4, blocked, rise);
    private static ZonePhysicsLivingMovementSettings Settings(float inertia = 8, float inertiaAcceleration = 0,
        float airControl = .1f, float airResistance = 0, float slide = 45, float climb = 50, float fall = 70,
        float pivot = 0, float halfHeight = .32f)
    {
        var model = new ZoneActorModel(new ZoneActorModelDefinition
        {
            Id = 1, PhysicsLivingMinSlideAngle = slide, PhysicsLivingMaxClimbAngle = climb,
            PhysicsLivingMinFallAngle = fall, PhysicsLivingMaxVelGround = 30,
            PhysicsLivingKAirControl = airControl, PhysicsLivingAirResistance = airResistance,
        }, [new ZoneGameStanceDefinition { Id = 1, ActorModelId = 1, StanceId = 2, SizeX = .4f, SizeY = .4f,
            SizeZ = halfHeight, HeightCollider = 1.2f, HeightPivot = pivot, MaxSpeed = 3.8f, UseCapsule = true }]);
        var request = ZoneActorPhysicalizationRequest.BuildInitial(model, 1, false, _ => null);
        request.Dynamics.Single(0x04, inertia); request.Dynamics.Single(0x08, inertiaAcceleration);
        return ZonePhysicsLivingMovementSettings.FromRequest(request);
    }
    private static async Task Near(float actual, float expected, float tolerance = .000002f) => await Assert.That(MathF.Abs(actual - expected)).IsLessThan(tolerance);
    private static async Task Near(Vector3 actual, Vector3 expected, float tolerance = .000002f) => await Assert.That(Vector3.Distance(actual, expected)).IsLessThan(tolerance);
}
