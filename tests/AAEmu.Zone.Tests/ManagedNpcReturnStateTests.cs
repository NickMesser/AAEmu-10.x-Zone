using System.Numerics;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Scripting;
using World = AAEmu.Zone.Tests.ManagedNpcPositionChecksTests.World;

namespace AAEmu.Zone.Tests;

public sealed class ManagedNpcReturnStateTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PendingLogicalTurnDrainsBeforeRealScriptTeleportOrPositionCorrectionAndPublishesItsExactRotation(bool positionCheck)
    {
        using var world = new World();
        Expose(world);
        var before = world.Body.CopyState();
        var first = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .3f);
        var last = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .8f);
        world.Body.RequestVelocity(new(2, 3, 0), AAEmu.Zone.Physics.ZonePhysicsLivingVelocityMode.Immediate);
        world.Fixture.Components.SetEntityOrientation(world.Actor, first);
        world.Fixture.Components.SetEntityOrientation(world.Actor, last);
        await Assert.That(world.Fixture.Components.CopyModelTransform(world.Actor.Npc.Unit).Orientation).IsEqualTo(last);
        await Assert.That(world.Body.CopyState().Status).IsEqualTo(before.Status);
        await Assert.That(world.Actor.Npc.Unit.Pose.Rotation).IsEqualTo(before.Status.Orientation);
        await Assert.That(world.Body.TryReadPendingOrientation(out var copied)).IsTrue();
        await Assert.That(copied).IsEqualTo(last);
        var destination = world.Position + new Vector3(20, 0, 0);
        if (positionCheck)
        {
            world.SetContext(1); world.Anchor("idlePos", destination);
            await Assert.That(world.Checks.Check(world.Actor).Relocated).IsTrue();
        }
        else
        {
            SetVector(world, "destination", destination);
            Run(world, "entity.unit:NpcTeleportTo(destination)");
        }
        await world.AssertPublished(destination);
        await Assert.That(world.Body.CopyState().Status.Orientation).IsEqualTo(last);
        await Assert.That(world.Actor.Npc.Unit.Pose.Rotation).IsEqualTo(last);
        await Assert.That(world.Body.CopyState().Status.Velocity).IsEqualTo(Vector3.Zero);
        await Assert.That(world.Body.CopyState().Control.RequestedVelocity).IsEqualTo(Vector3.Zero);
        await Assert.That(world.Body.CopyState().Control.HasPendingVelocityRequest).IsFalse();
        await Assert.That(world.Body.TryReadPendingOrientation(out _)).IsFalse();
        await Assert.That(world.Checks.CorrectionCount).IsEqualTo(positionCheck ? 1ul : 0ul);
    }

    [Test]
    public async Task PhysicalOnlyPendingRotationCannotMasqueradeAsTheLogicalEntityRotationDuringTeleportPreflight()
    {
        using var world = new World();
        Expose(world);
        var before = world.Body.CopyState();
        var logical = world.Fixture.Components.CopyModelTransform(world.Actor.Npc.Unit);
        var queued = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .9f);
        world.Body.RequestOrientation(queued); //Deliberately bypass the logical entity writer.
        SetVector(world, "destination", world.Position + new Vector3(4, 0, 0));
        await Assert.That(() => Run(world, "entity.unit:NpcTeleportTo(destination)")).Throws<ZoneLuaException>();
        await Assert.That(world.Body.CopyState()).IsEqualTo(before);
        await Assert.That(world.Fixture.Components.CopyModelTransform(world.Actor.Npc.Unit)).IsEqualTo(logical);
        await Assert.That(world.Body.TryReadPendingOrientation(out var copied)).IsTrue();
        await Assert.That(copied).IsEqualTo(queued);
        //A source-owned logical rotation supplies matching metadata; the same body
        //commands can then drain and publish through the real teleport boundary.
        world.Fixture.Components.SetEntityOrientation(world.Actor, queued);
        Run(world, "entity.unit:NpcTeleportTo(destination)");
        await Assert.That(world.Body.CopyState().Status.Orientation).IsEqualTo(queued);
        await world.AssertPublished(logical.Position + new Vector3(4, 0, 0));
    }

    [Test]
    public async Task AlertnessReadsTheActualPuppetInsteadOfTheBehaviorBeingEntered()
    {
        using var world = new World();
        Expose(world);
        Run(world, "AIBehaviour.controlled_combat={Name='controlled_combat',alertness=2}");
        world.Actor.Behavior!.SetBehavior("controlled_combat", mode: ZoneBehaviorConstructorMode.Restore);
        await Assert.That(world.Actor.Behavior.Alertness).IsEqualTo(2);
        using var result = world.Fixture.Runtime.Lua.Execute("return AI.GetAlertness(entity.id)");
        await Assert.That(Convert.ToSingle(result[0])).IsEqualTo(0f);
        await Assert.That(world.Motion.CopyPuppetAlertness(world.Actor)).IsEqualTo((int?)0);
    }

    [Test]
    public async Task ScriptTeleportPublishesTheActualBodyModelUnitSpatialAndEyeWithoutAReplicationOrPostureReset()
    {
        using var world = new World();
        Expose(world);
        world.Replication.Reset(world.Actor);
        await Assert.That(world.Replication.Sample(world.Actor, world.Fixture.CurrentFrame)).IsTrue();
        world.Replication.Flush(); world.Drain();
        var cache = world.Replication.CopyState(world.Actor);
        var before = world.Body.CopyState();
        var posture = world.Actor.Npc.Unit.Snapshot.Posture;
        var destination = world.Position + new Vector3(4, -3, 2);
        SetVector(world, "destination", destination);
        using var result = world.Fixture.Runtime.Lua.Execute("return select('#',entity.unit:NpcTeleportTo(destination))");
        await Assert.That(result[0]).IsEqualTo(0L);
        await world.AssertPublished(destination);
        var after = world.Body.CopyState();
        await Assert.That(after.Status.Orientation).IsEqualTo(before.Status.Orientation);
        await Assert.That(after.EntityScale).IsEqualTo(before.EntityScale);
        await Assert.That(world.Actor.Npc.Unit.Snapshot.Posture).IsEqualTo(posture);
        await Assert.That(world.Replication.CopyState(world.Actor)).IsEqualTo(cache);
        await Assert.That(world.Checks.CorrectionCount).IsEqualTo(0ul);
        await Assert.That(world.Drain().Any(f => f.Opcode == ZwOpcodes.UnitModelPostureChanged)).IsFalse();
    }

    [Test]
    public async Task InvalidTeleportVectorsHaveNoEffectAndAStaleReceiverCannotTouchAReplacement()
    {
        using var world = new World();
        Expose(world);
        var before = world.Body.CopyState();
        using (var result = world.Fixture.Runtime.Lua.Execute("""
            return select('#',entity.unit:NpcTeleportTo()),
                   select('#',entity.unit:NpcTeleportTo('not a vector')),
                   select('#',entity.unit:NpcTeleportTo({x=1,y=2}))
            """))
            for (var index = 0; index < 3; index++) await Assert.That(result[index]).IsEqualTo(0L);
        await Assert.That(world.Body.CopyState()).IsEqualTo(before);
        using var script = world.Actor.Entry.RetainScript()!;
        using var unit = (ZoneLuaReference)script.Get("unit")!;
        using var teleport = (ZoneLuaReference)unit.Get("NpcTeleportTo")!;
        var writer = new ZoneWriter(); writer.WriteBc(world.Actor.Npc.ObjectId);
        await Assert.That(world.Fixture.Admission.Registry.Remove(writer.ToArray())).IsTrue();
        var replacement = world.Fixture.Admission.CreateFromWire(world.Fixture.Model.Id, new(1100, 1100, 10.1f));
        var handle = world.Fixture.Admission.Catalog.TryGet(replacement)!.Value;
        var fresh = world.Fixture.Runtime.Actors.Find(handle)!;
        var body = world.Fixture.Bodies!.Find(handle)!;
        var freshState = body.CopyState();
        using var vector = world.Fixture.Runtime.Lua.CreateTable();
        vector.Set("x", 1110); vector.Set("y", 1120); vector.Set("z", 30);
        await Assert.That(() => world.Fixture.Runtime.Lua.Invoke(teleport, unit, vector)).Throws<ZoneLuaException>();
        await Assert.That(body.CopyState()).IsEqualTo(freshState);
        await Assert.That(fresh.Handle.Generation).IsGreaterThan(world.Actor.Handle.Generation);
    }

    [Test]
    [Arguments(2f, 0f, false)]
    [Arguments(0f, 2f, false)]
    [Arguments(2f, .1f, true)]
    public async Task UnchangedReturnCompletionUsesTheStrictThreeDimensionalTwoMeterThreshold(float x, float z, bool teleports)
    {
        using var world = new World(gameRequests: true);
        InstallOriginal(world);
        var before = world.Position;
        var destination = before + new Vector3(x, 0, z);
        SetVector(world, "destination", destination);
        Run(world, "AI.SetRefPointPosition(entity.id,destination);entity.AI.returnSignal='RETURN_FINISHED'");
        InvokeOriginal(world, "OnCompletedReturn");
        await world.AssertPublished(teleports ? destination : before);
        await Assert.That(world.Actor.Npc.AiSignals.Pending.Single().Name).IsEqualTo("RETURN_FINISHED");
        await Assert.That(world.Actor.Npc.AiSignals.Pending.Single().SenderEntityId).IsEqualTo(world.Actor.AiObject.EntityId);
        using var result = world.Fixture.Runtime.Lua.Execute("return entity.AI.returnSignal==nil");
        await Assert.That(result[0] is true).IsTrue();
        await Assert.That(world.Checks.CorrectionCount).IsEqualTo(0ul);
    }

    [Test]
    public async Task UnchangedNonLocomotionConstructorAndInterruptedDestructorRestoreActualOwnedState()
    {
        using var world = new World(gameRequests: true);
        InstallOriginal(world);
        //CAIHandler alertness2 does not establish the independent Puppet's constructor0.
        Run(world, "AIBehaviour.controlled_combat={Name='controlled_combat',alertness=2}");
        world.Actor.Behavior!.SetBehavior("controlled_combat", mode: ZoneBehaviorConstructorMode.Restore);
        //This real authored branch completes without constructing an Approach goal. The
        //fixture intentionally does not claim a pathfinding/locomotion owner.
        Run(world, "entity.AI.param.goReturnState=false;entity.unit:NpcActivateAITimeEvent(true,1)");
        //30514D60 consumes this final positive countdown tick to0; status1 alone does
        //not cancel a fresh proxy's startup grace. This establishes the true restore branch.
        world.Fixture.Runtime.Actors.GameObjectActivationProfile = ZoneManagedNpcGameObjectActivationProfile.MultiplayerWorldServer;
        world.Actor.SetEntityHidden(false);
        world.Actor.Entry.SetUpdateAlways(1);
        await Assert.That(ZoneNpcAutoDisable.Get(world.Actor)).IsTrue();
        world.Actor.Behavior.SetBehavior("return_state");
        RequireHealthy(world);
        await Assert.That(world.Actor.Behavior.CurrentName).IsEqualTo("return_state");
        await Assert.That(world.Actor.Npc.AggroEnabled).IsFalse();
        await Assert.That(world.Actor.Npc.Timers.AiTimeEventEnabled).IsTrue();
        await Assert.That(world.Actor.Npc.Timers.AiTimeEventInterval).IsEqualTo(1000u);
        await Assert.That(ZoneNpcAutoDisable.Get(world.Actor)).IsFalse();
        await Assert.That(world.Actor.Npc.AiSignals.Pending.Single().Name).IsEqualTo("GO_TO_RUN_COMMAND_SET");
        using (var result = world.Fixture.Runtime.Lua.Execute("return entity.AI.move_timeoutCount==nil,entity.AI.needRestorationOnReturn"))
        {
            await Assert.That(result[0] is true).IsTrue();
            await Assert.That(result[1] is false).IsTrue();
        }
        //No restore skill/buff is fabricated from the behavior alertness2.
        await Assert.That(world.Drain().Any(f => f.Opcode is ZwOpcodes.CreateBuff or ZwOpcodes.StartSkill)).IsFalse();
        world.Actor.Behavior.SetBehavior("idle");
        await Assert.That(world.Actor.Npc.AggroEnabled).IsTrue();
        await Assert.That(world.Actor.Npc.Timers.AiTimeEventEnabled).IsFalse();
        await Assert.That(ZoneNpcAutoDisable.Get(world.Actor)).IsTrue();
        using (var result = world.Fixture.Runtime.Lua.Execute("return entity.AI.move_timeoutCount==nil,entity.AI.prevAutoDisable==nil,entity.AI.needRestorationOnReturn==nil"))
            for (var index = 0; index < 3; index++) await Assert.That(result[index] is true).IsTrue();
        await Assert.That(world.Fixture.Runtime.Scripts.RecentErrors.Count).IsEqualTo(0);
    }

    [Test]
    public async Task UnchangedTimeoutFromItsCopiedTwentyEventStateUsesTheRealTeleportThenSignalOwners()
    {
        using var world = new World(gameRequests: true);
        InstallOriginal(world);
        var destination = world.Position + new Vector3(4, 0, 0);
        SetVector(world, "destination", destination);
        //Copied state at the normal constructor's timeout boundary. This exercises the
        //unchanged callback separately from the still independently owned Approach path.
        Run(world, "AI.SetRefPointPosition(entity.id,destination);entity.AI.move_timeoutCount=20");
        var before = world.Position;
        for (var index = 0; index < 19; index++)
        {
            InvokeOriginal(world, "OnAITimeEvent");
            using var count = world.Fixture.Runtime.Lua.Execute("return entity.AI.move_timeoutCount");
            await Assert.That(Convert.ToInt32(count[0])).IsEqualTo(19 - index);
            if (world.Position != before)
                throw new InvalidOperationException($"Return timer relocated after event {index + 1}, remaining={count[0]}, from={before}, to={world.Position}.");
        }
        await world.AssertPublished(before);
        await Assert.That(world.Actor.Npc.AiSignals.Pending.Count).IsEqualTo(0);
        InvokeOriginal(world, "OnAITimeEvent");
        await world.AssertPublished(destination);
        await Assert.That(world.Actor.Npc.AiSignals.Pending.Single().Name).IsEqualTo("GO_TO_RUN_COMMAND_SET");
        await Assert.That(world.Fixture.Runtime.Scripts.RecentErrors.Count).IsEqualTo(0);
    }

    private static void InstallOriginal(World world)
    {
        Expose(world);
        using var math = world.Fixture.Runtime.Lua.Execute(Resource("npc-original-math.lua"), "@scripts/Utils/Math.lua");
        using var state = world.Fixture.Runtime.Lua.Execute(Resource("npc-return-state.lua"), "@scripts/ai/behaviors/x2/common/return_state.lua");
        //Original BasicAI.ClearAICombat, scripts/Entities/AI/Shared/BasicAI.lua488.
        //The fixture has no unrelated BasicAI entity setup, so install only this original method.
        Run(world, """
            function BasicAI:ClearAICombat()
                if (self.unit) then
                    self.unit:ClearCombat();
                    self.unit:StopCasting();
                end
                AI.ClearAttentionTargetOf(self.id);
            end
            entity.ClearAICombat=BasicAI.ClearAICombat
            """);
    }

    private static void Expose(World world)
    { using var script = world.Actor.Entry.RetainScript()!; world.Fixture.Runtime.Lua.SetGlobal("entity", script); }
    private static void InvokeOriginal(World world, string method)
        => Run(world, "AIBehaviour.return_state:" + method + "(entity)");
    private static void Run(World world, string source)
    { using var ignored = world.Fixture.Runtime.Lua.Execute(source); }
    private static void RequireHealthy(World world)
    {
        if (world.Fixture.Runtime.Scripts.RecentErrors.Count != 0)
            throw new InvalidOperationException(string.Join("; ", world.Fixture.Runtime.Scripts.RecentErrors.Select(error => error.Message)));
    }
    private static void SetVector(World world, string name, Vector3 value)
    {
        using var vector = world.Fixture.Runtime.Lua.CreateTable();
        vector.Set("x", value.X); vector.Set("y", value.Y); vector.Set("z", value.Z);
        world.Fixture.Runtime.Lua.SetGlobal(name, vector);
    }
    private static string Resource(string name)
    {
        using var stream = typeof(ManagedNpcReturnStateTests).Assembly.GetManifestResourceStream("AAEmu.Zone.Tests.Fixtures." + name)!;
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
}
