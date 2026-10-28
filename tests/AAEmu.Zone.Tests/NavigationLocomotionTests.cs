using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Navigation;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Tests;

public sealed class NavigationLocomotionTests
{
    [Test]
    public async Task FollowingOnlyQueuesWishesAndTheExistingPhysicsStepCommitsEveryPose()
    {
        using var navigation = NavigationSearchTests.RoutingGraph(); var queries = new NavigationSearchTests.WallQueries();
        var route = navigation.FindPath(new(new(2, 10, 0), new(20, 10, 0), .25f), queries);
        var (physics, actor) = Body(); var motion = new Motion(physics, actor);
        using var follower = new ZoneNavigationPathFollower(navigation, route, queries, motion, 4, .025f);
        physics.CopyStatus(actor, out var initial);
        await Assert.That(follower.Update(.05f)).IsEqualTo(ZoneNavigationFollowOutcome.Moving);
        physics.CopyStatus(actor, out var pending);
        await Assert.That(pending.Position).IsEqualTo(initial.Position);
        physics.CopyLivingControlState(actor, out var controls);
        await Assert.That(controls.HasPendingVelocityRequest).IsTrue();
        for (var i = 0; i < 300 && follower.Outcome != ZoneNavigationFollowOutcome.Complete; i++)
        {
            physics.Step(TimeSpan.FromMilliseconds(50)); follower.Update(.05f);
            physics.CopyStatus(actor, out var current);
            await Assert.That(current.Position.Z).IsEqualTo(0f);
            await Assert.That(current.Position.X > 8 && current.Position.X < 12 && current.Position.Y > 6).IsFalse();
        }
        await Assert.That(follower.Outcome).IsEqualTo(ZoneNavigationFollowOutcome.Complete);
        physics.Step(TimeSpan.FromMilliseconds(50)); physics.CopyStatus(actor, out var complete);
        await Assert.That(Vector2.Distance(new(complete.Position.X, complete.Position.Y), new(20, 10))).IsLessThan(.03f);
        physics.CopyLivingControlState(actor, out controls);
        await Assert.That(controls.RequestedVelocity).IsEqualTo(Vector3.Zero);
    }

    [Test]
    public async Task RetiredNavigationStopsItsWishAndCannotControlAReplacementActorGeneration()
    {
        using var navigation = NavigationSearchTests.RoutingGraph(); var queries = new NavigationSearchTests.WallQueries();
        var route = navigation.FindPath(new(new(2, 10, 0), new(20, 10, 0), .25f), queries);
        var (physics, actor) = Body();
        using var follower = new ZoneNavigationPathFollower(navigation, route, queries, new Motion(physics, actor), 4);
        follower.Update(.05f); physics.Step(TimeSpan.FromMilliseconds(50)); physics.CopyStatus(actor, out var before);
        navigation.RetireTile(new(36, 56));
        // Cancellation is synchronous with retirement, even before the next AI update.
        await Assert.That(follower.Outcome).IsEqualTo(ZoneNavigationFollowOutcome.Retired);
        physics.Step(TimeSpan.FromMilliseconds(50)); physics.CopyStatus(actor, out var stopped);
        await Assert.That(stopped.Position).IsEqualTo(before.Position);
        physics.RemoveActor(actor);
        var replacement = new ZoneActorHandle(actor.UnitObjectId, 2); physics.AddActor(replacement, Description());
        physics.Teleport(replacement, new(55, 66, 0), Quaternion.Identity);
        follower.Update(.05f); physics.Step(TimeSpan.FromMilliseconds(50)); physics.CopyStatus(replacement, out var untouched);
        await Assert.That(untouched.Position).IsEqualTo(new Vector3(55, 66, 0));
        physics.CopyLivingControlState(replacement, out var control);
        await Assert.That(control.RequestedVelocity).IsEqualTo(Vector3.Zero);
    }

    private static (ZoneManagedPhysicsBackend Physics, ZoneActorHandle Actor) Body()
    {
        var physics = new ZoneManagedPhysicsBackend(new(new ZoneTerrainHeightField())); var actor = new ZoneActorHandle(1801, 1);
        physics.AddActor(actor, Description()); physics.Teleport(actor, new(2, 10, 0), Quaternion.Identity); return (physics, actor);
    }
    private static ZonePhysicsActorDescription Description() => ZonePhysicsActorDescription.FromLivingStance(
        new(new(.25f, .25f, .3f), default, default, 1, 0, 4, true, false), 80, 0) with { CollisionActive = false };
    private sealed class Motion(ZoneManagedPhysicsBackend physics, ZoneActorHandle actor) : IZoneNavigationLocomotion
    {
        public bool TryReadPosition(out Vector3 position)
        { try { physics.CopyStatus(actor, out var state); position = state.Position; return true; } catch (InvalidOperationException) { position = default; return false; } }
        public bool TryRequestVelocity(Vector3 requestedVelocity)
        { try { physics.ApplyLivingVelocityRequest(actor, requestedVelocity); return true; } catch (InvalidOperationException) { return false; } }
    }
}
