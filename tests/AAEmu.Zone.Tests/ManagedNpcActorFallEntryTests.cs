using AAEmu.Zone.Hosting;
using AAEmu.Zone.Scene;

namespace AAEmu.Zone.Tests;

public sealed class ManagedNpcActorFallEntryTests
{
    [Test]
    public async Task HighCurrentFallVelocityUsesPreviousSpeedAndNearTerrainLandingRemainsAdmitted()
    {
        //The actual 7987 row has gravity 1000/resistance 20. This independent fixture
        //uses a 20ms period and different dimensions, producing VZ -12 on its first
        //frame. Previous actor376 is 0, then the real near-terrain landing is allowed.
        using var world = new ManagedNpcActorMotionTests.World(gravity:1000,z:10.5f,airResistance:20);
        world.Step();
        await Assert.That(world.Lease.State.Mode).IsEqualTo(ZoneManagedNpcActorMotionMode.Air);
        await Assert.That(world.Lease.State.ActorVelocity.Z).IsLessThan(-10f);
        await Assert.That(world.Lease.State.FallSpeed).IsGreaterThan(10f);
        for (var i=0;i<4 && world.Lease.State.Mode!=ZoneManagedNpcActorMotionMode.Ground;i++) world.Step();
        await Assert.That(world.Lease.State.Mode).IsEqualTo(ZoneManagedNpcActorMotionMode.Ground);
        await Assert.That(world.Lease.State.FirstLanding).IsFalse();
        await Assert.That(world.Lease.LandingCount).IsEqualTo(1ul);
    }

    [Test]
    public async Task PriorSpeedAndActualRendererHeightUseStrictConfiguredEntryBoundariesAtomically()
    {
        var controls = new (float Z,float PriorSpeed,string? Config,bool Refuses)[]
        {
            (20,10,null,false), //Equality with the registered velocity threshold.
            (12,11,null,false), //Equality with terrain10 + registered height2.
            (MathF.BitIncrement(12),11,null,true),
            (12.5f,11,"pl_fall_start_height = 3",false),
            (20,11,"pl_fall_start_velocity = 12",false),
            (20,10,"pl_fall_start_velocity = 9",true)
        };
        foreach (var control in controls)
        {
            using var world = new ManagedNpcActorMotionTests.World(gravity:0,z:control.Z,
                engineCVars:ZoneEngineCVars.Parse(control.Config));
            world.Backend.Step(TimeSpan.FromMilliseconds(20));
            world.Lease.State = world.Lease.State with {FallSpeed=control.PriorSpeed,MaximumFallSpeed=control.PriorSpeed};
            var previous=world.Lease.State;var body=world.Body.CopyState();
            if (control.Refuses)
            {
                await Assert.That(() => world.Motion.Update(world.Actor,body,world.Frame(1))).Throws<NotSupportedException>();
                await Assert.That(world.Lease.State).IsEqualTo(previous);
                await Assert.That(world.Body.CopyState()).IsEqualTo(body);
            }
            else
            {
                world.Motion.Update(world.Actor,body,world.Frame(1));
                await Assert.That(world.Lease.State.Frame).IsEqualTo((uint?)1);
                await Assert.That(world.Lease.State.FallSpeed).IsEqualTo(0f);
            }
        }
    }

    [Test]
    public async Task PotentialFastEntryNeedsRealTerrainWhileHigherConfiguredSpeedAvoidsThatQuery()
    {
        using var missing = new ManagedNpcActorMotionTests.World(gravity:0,z:20,supplyTerrain:false);
        missing.Backend.Step(TimeSpan.FromMilliseconds(20));
        missing.Lease.State = missing.Lease.State with {FallSpeed=11};
        var previous=missing.Lease.State;
        await Assert.That(() => missing.Motion.Update(missing.Actor,missing.Body.CopyState(),missing.Frame(1))).Throws<NotSupportedException>();
        await Assert.That(missing.Lease.State).IsEqualTo(previous);
        using var gated = new ManagedNpcActorMotionTests.World(gravity:0,z:20,supplyTerrain:false,
            engineCVars:ZoneEngineCVars.Parse("pl_fall_start_velocity = 12"));
        gated.Backend.Step(TimeSpan.FromMilliseconds(20));
        gated.Lease.State = gated.Lease.State with {FallSpeed=11};
        gated.Motion.Update(gated.Actor,gated.Body.CopyState(),gated.Frame(1));
        await Assert.That(gated.Lease.State.Frame).IsEqualTo((uint?)1);
    }

    [Test]
    public async Task RepeatedHardLandingStillRefusesAtItsOriginalFiveMeterPerSecondBoundary()
    {
        foreach (var fall in new[] {MathF.BitDecrement(5),5f})
        {
            using var world = new ManagedNpcActorMotionTests.World();
            world.Backend.Step(TimeSpan.FromMilliseconds(20));
            world.Lease.State = world.Lease.State with {FirstLanding=false,FallSpeed=fall};
            var previous=world.Lease.State;
            if (fall==5)
            {
                await Assert.That(() => world.Motion.Update(world.Actor,world.Body.CopyState(),world.Frame(1))).Throws<NotSupportedException>();
                await Assert.That(world.Lease.State).IsEqualTo(previous);
            }
            else
            {
                world.Motion.Update(world.Actor,world.Body.CopyState(),world.Frame(1));
                await Assert.That(world.Lease.State.LandingTransition).IsTrue();
            }
        }
    }
}
