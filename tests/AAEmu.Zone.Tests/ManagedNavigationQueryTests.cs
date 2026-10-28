using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Navigation;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Tests;

public sealed class ManagedNavigationQueryTests
{
    private static readonly ZoneManagedNavigationQueryPolicy Policy = new(16, .25f, 128, .3f, .5f, .8f);

    [Test]
    public async Task LoadedGroundAndActualBodyClearanceAdmitWalkingWithoutWritingAnyMotion()
    {
        var (physics, actor, _) = World(); physics.ApplyLivingVelocityRequest(actor, Vector3.UnitY);
        physics.CopyStatus(actor, out var before); physics.CopyLivingControlState(actor, out var controlBefore);
        var queries = Query(physics, actor);
        await Assert.That(queries.CheckWalkSegment(new(1100, 1100, 100.1f), new(1102, 1100, 100.1f), .01f).Outcome).IsEqualTo(ZoneNavigationSegmentOutcome.Clear);
        physics.CopyStatus(actor, out var after); physics.CopyLivingControlState(actor, out var controlAfter);
        await Assert.That(after).IsEqualTo(before); await Assert.That(controlAfter).IsEqualTo(controlBefore);
    }

    [Test]
    public async Task PhysicalRadiusDoesNotBecomeTheAiPassRadiusAndActualCornerObstaclesStillBlock()
    {
        var (physics, actor, _) = World(); var statics = physics.StaticGeometry!;
        var corner = statics.AddInstance(WallX(1101, 1100.25f, 1101), Matrix4x4.Identity);
        var queries = Query(physics, actor);
        await Assert.That(queries.CheckLineOfSight(new(1100, 1100, 101.2f), new(1102, 1100, 101.2f)).Outcome).IsEqualTo(ZoneNavigationSegmentOutcome.Clear);
        await Assert.That(queries.CheckWalkSegment(new(1100, 1100, 100.1f), new(1102, 1100, 100.1f), .01f).Outcome).IsEqualTo(ZoneNavigationSegmentOutcome.Blocked);
        statics.RemoveInstance(corner);
        statics.AddInstance(new ZoneManagedTriangleMesh([new(1090, 1100.7f, 100), new(1110, 1100.7f, 100), new(1110, 1100.7f, 103), new(1090, 1100.7f, 103)], [0, 1, 2, 0, 2, 3]), Matrix4x4.Identity);
        await Assert.That(queries.CheckWalkSegment(new(1100, 1100, 100.1f), new(1102, 1100, 100.1f), 2).Outcome).IsEqualTo(ZoneNavigationSegmentOutcome.Clear);
    }

    [Test]
    public async Task UnavailableCoverageAndRealUnloadedTerrainCannotTurnIntoClearSpace()
    {
        var (physics, actor, terrain) = World();
        var missing = new ZoneManagedNavigationQueries(physics, actor, Policy,
            _ => new(ZoneNavigationSegmentOutcome.MissingData, "unloaded terrain region"), Available);
        await Assert.That(missing.CheckWalkSegment(new(1100, 1100, 100.1f), new(1102, 1100, 100.1f), .25f).Outcome).IsEqualTo(ZoneNavigationSegmentOutcome.MissingData);
        var unknown = new ZoneManagedNavigationQueries(physics, actor, Policy, Available,
            _ => new(ZoneNavigationSegmentOutcome.Unsupported, "unsupported authored proxy in corridor"));
        await Assert.That(unknown.CheckWalkSegment(new(1100, 1100, 100.1f), new(1102, 1100, 100.1f), .25f).Outcome).IsEqualTo(ZoneNavigationSegmentOutcome.Unsupported);
        terrain.UnloadCell(1, 1);
        await Assert.That(Query(physics, actor).CheckWalkSegment(new(1100, 1100, 100.1f), new(1102, 1100, 100.1f), .25f).Outcome).IsEqualTo(ZoneNavigationSegmentOutcome.MissingData);
    }

    [Test]
    public async Task ExactSelfIsIgnoredOtherRegisteredBodiesBlockAndRetirementCannotAliasANewGeneration()
    {
        var (physics, actor, _) = World(); var queries = Query(physics, actor);
        var other = new ZoneActorHandle(1902, 1); physics.AddActor(other, Description()); physics.Teleport(other, new(1101, 1100, 100.1f), Quaternion.Identity);
        await Assert.That(queries.CheckWalkSegment(new(1100, 1100, 100.1f), new(1102, 1100, 100.1f), .25f).Outcome).IsEqualTo(ZoneNavigationSegmentOutcome.Blocked);
        physics.RemoveActor(other); physics.RemoveActor(actor);
        var replacement = new ZoneActorHandle(actor.UnitObjectId, 2); physics.AddActor(replacement, Description()); physics.Teleport(replacement, new(1100, 1100, 100.1f), Quaternion.Identity);
        await Assert.That(queries.CheckWalkSegment(new(1100, 1100, 100.1f), new(1102, 1100, 100.1f), .25f).Outcome).IsEqualTo(ZoneNavigationSegmentOutcome.Unsupported);
        physics.CopyLivingControlState(replacement, out var control); await Assert.That(control.HasPendingVelocityRequest).IsFalse();
    }

    [Test]
    public async Task QueriesUseAppliedStanceDimensionsAndDoNotTreatAPendingRequestAsTheBody()
    {
        var (physics, actor, _) = World(); var queries = Query(physics, actor);
        physics.StaticGeometry!.AddInstance(WallX(1101, 1100.4f, 1101), Matrix4x4.Identity);
        var larger = new ZoneActorPhysicalizationStance(new(.6f, .6f, .3f), default, default, 1.2f, .1f, 4, true, false);
        await Assert.That(physics.TryChangeLivingStance(actor, larger, out _)).IsTrue();
        await Assert.That(queries.CheckWalkSegment(new(1100, 1100, 100.1f), new(1102, 1100, 100.1f), .01f).Outcome).IsEqualTo(ZoneNavigationSegmentOutcome.Clear);
        physics.Step(TimeSpan.FromMilliseconds(1));
        await Assert.That(queries.CheckWalkSegment(new(1100, 1100, 100.1f), new(1102, 1100, 100.1f), .01f).Outcome).IsEqualTo(ZoneNavigationSegmentOutcome.Blocked);
    }

    [Test]
    public async Task AppliedBodyAndSupportProjectionRemainFiniteAtEightKilometerCoordinates()
    {
        var (physics, actor, terrain) = World(); terrain.UnloadCell(1, 1);
        terrain.LoadCell(7, 7, TerrainHeightFieldTests.CreateCell(100, 0)); physics.Teleport(actor, new(8000, 8000, 100.1f), Quaternion.Identity);
        await Assert.That(Query(physics, actor).CheckWalkSegment(new(8000, 8000, 100.1f), new(8002, 8000, 100.1f), .25f).Outcome).IsEqualTo(ZoneNavigationSegmentOutcome.Clear);
    }

    [Test]
    public async Task ExplicitLengthSampleProjectionAndSlopePoliciesRefuseTheirActualLimits()
    {
        var (physics, actor, terrain) = World();
        await Assert.That(Query(physics, actor, Policy with { MaximumSegmentLength = 1 }).CheckWalkSegment(new(1100, 1100, 100.1f), new(1102, 1100, 100.1f), .25f).Outcome).IsEqualTo(ZoneNavigationSegmentOutcome.Unsupported);
        await Assert.That(Query(physics, actor, Policy with { MaximumSupportSamples = 2 }).CheckWalkSegment(new(1100, 1100, 100.1f), new(1102, 1100, 100.1f), .25f).Outcome).IsEqualTo(ZoneNavigationSegmentOutcome.Unsupported);
        await Assert.That(Query(physics, actor, Policy with { MaximumDrop = .1f }).CheckWalkSegment(new(1100, 1100, 100.4f), new(1102, 1100, 100.4f), .25f).Outcome).IsEqualTo(ZoneNavigationSegmentOutcome.Blocked);
        terrain.UnloadCell(1, 1); terrain.LoadCell(1, 1, TerrainHeightFieldTests.CreateCell(0, .03125f, sample: (x, _, sx, _) => (ushort)((x / 2 + sx) * 32)));
        physics.Teleport(actor, new(1100, 1100, 38.1f), Quaternion.Identity);
        await Assert.That(Query(physics, actor, Policy with { MinimumSupportNormalZ = .95f }).CheckWalkSegment(new(1100, 1100, 38.1f), new(1101, 1100, 38.6f), .25f).Outcome).IsEqualTo(ZoneNavigationSegmentOutcome.Blocked);
    }

    [Test]
    public async Task VisibilityUsesBoundedRealSurfacesAndRecognizesSolidInteriorEndpoints()
    {
        var (physics, actor, _) = World(); var statics = physics.StaticGeometry!; var queries = Query(physics, actor);
        await Assert.That(queries.CheckLineOfSight(new(1100, 1100, 99), new(1100, 1100, 101)).Outcome).IsEqualTo(ZoneNavigationSegmentOutcome.Blocked);
        var wall = statics.AddInstance(WallX(1101, 1099, 1101), Matrix4x4.Identity);
        await Assert.That(queries.CheckLineOfSight(new(1100, 1100, 101), new(1102, 1100, 101)).Outcome).IsEqualTo(ZoneNavigationSegmentOutcome.Blocked);
        statics.RemoveInstance(wall);
        await Assert.That(queries.CheckLineOfSight(new(1100, 1100, 101), new(1102, 1100, 101)).Outcome).IsEqualTo(ZoneNavigationSegmentOutcome.Clear);
        statics.AddInstance(new ZoneManagedPrimitiveGeometry(ZoneManagedPrimitiveKind.Sphere, Vector3.Zero, Vector3.Zero, 1, 0, 7), Matrix4x4.CreateScale(2, 1, .5f) * Matrix4x4.CreateTranslation(1102, 1102, 102));
        await Assert.That(queries.CheckLineOfSight(new(1102, 1102, 102), new(1102.1f, 1102, 102)).Outcome).IsEqualTo(ZoneNavigationSegmentOutcome.Blocked);
    }

    [Test]
    public async Task CoverageCallbacksSeeThePhysicalFootprintAndCannotRetireTheBodyThenPublishClear()
    {
        var (physics, actor, _) = World(); var checkedFootprint = false;
        var queries = new ZoneManagedNavigationQueries(physics, actor, Policy, bounds =>
        {
            checkedFootprint = bounds.Minimum.X < 1099.9f && bounds.Maximum.X > 1100.1f;
            physics.RemoveActor(actor); return new(ZoneNavigationSegmentOutcome.Clear);
        }, Available);
        await Assert.That(queries.CheckWalkSegment(new(1100, 1100, 100.1f), new(1102, 1100, 100.1f), .01f).Outcome).IsEqualTo(ZoneNavigationSegmentOutcome.Unsupported);
        await Assert.That(checkedFootprint).IsTrue();
    }

    private static (ZoneManagedPhysicsBackend Physics, ZoneActorHandle Actor, ZoneTerrainHeightField Terrain) World()
    {
        // The shared fixture's default quantized sample is32, so zero scale makes this
        // independent flat floor exactly100 instead of base100+sample32.
        var terrain = new ZoneTerrainHeightField(); terrain.LoadCell(1, 1, TerrainHeightFieldTests.CreateCell(100, 0));
        var physics = new ZoneManagedPhysicsBackend(new(terrain), staticGeometry: new()); var actor = new ZoneActorHandle(1901, 1);
        physics.AddActor(actor, Description()); physics.Teleport(actor, new(1100, 1100, 100.1f), Quaternion.Identity); return (physics, actor, terrain);
    }
    private static ZonePhysicsActorDescription Description() => ZonePhysicsActorDescription.FromLivingStance(new(new(.3f, .3f, .3f), default, default, 1.2f, .1f, 4, true, false), 80);
    private static ZoneManagedNavigationQueries Query(ZoneManagedPhysicsBackend physics, ZoneActorHandle actor, ZoneManagedNavigationQueryPolicy? policy = null)
        => new(physics, actor, policy ?? Policy, Available, Available);
    private static ZoneNavigationSegmentAdmission Available(ZonePhysicsQueryBounds _) => new(ZoneNavigationSegmentOutcome.Clear);
    private static ZoneManagedTriangleMesh WallX(float x, float y0, float y1) => new([new(x, y0, 100), new(x, y1, 100), new(x, y1, 103), new(x, y0, 103)], [0, 1, 2, 0, 2, 3]);
}
