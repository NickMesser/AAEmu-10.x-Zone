using System.Numerics;
using System.Text.Json;
using AAEmu.Zone.Content;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Protocol.Units;
using AAEmu.Zone.Scene;

namespace AAEmu.Zone.Tests;

public sealed class NpcBuffMovementTests
{
    [Test]
    public async Task CompleteOriginalActorPipelineSeparatesFiveStatusFlagsAndActualSpeedModifiers()
    {
        using var stream = typeof(NpcBuffMovementTests).Assembly.GetManifestResourceStream(
            "AAEmu.Zone.Tests.Fixtures.npc-actor-buff-turn-native-controls.json")!;
        using var document = JsonDocument.Parse(stream);
        var count = 0;
        foreach (var row in document.RootElement.GetProperty("rows").EnumerateArray())
        {
            var rotation = Rotation(row.GetProperty("entityRotation"));
            var request = new ZoneManagedNpcControllerRequest(4 | 0x20 | 8 | 0x800 | 0x100000 | 0x10000000 | 0x8000000,
                default, default, 3, 0, 1, Vector(row.GetProperty("localDelta")), 0, 1, false);
            var actual = ZoneNpcOrdinaryMovementMath.Build(request, new(10, 20, 10), rotation,
                new(10, 20, 11.5f), rotation, row.GetProperty("effectiveSpeed").GetSingle(), .02f, 0,
                true, true, row.GetProperty("canRotate").GetBoolean());
            var native = row.GetProperty("request").EnumerateArray().Single();
            await Near(actual.Velocity, Vector(native.GetProperty("velocity")), .000005f);
            await Assert.That(MathF.Abs(Quaternion.Dot(actual.Orientation,
                Rotation(row.GetProperty("bodyRotation"))))).IsGreaterThan(.999999f);
            await Near(actual.LookTarget, Vector(row.GetProperty("controllerLook")), .000005f);
            count++;
        }
        await Assert.That(count).IsEqualTo(34);
    }

    [Test]
    public async Task RestoredOriginalHidden6613And3590ReachRealMovementWithTheirActualSpeedModifier()
    {
        using var world = new ManagedNpcPositionChecksTests.World(movementRequests: true, movementNormalSpeed: 6,
            movementBuffContent: OriginalMovementBuffs(), initialBuffs: new([], [], [Buff(1, 6613), Buff(2, 3590)]),
            movementStatModifiers: OriginalMovementModifiers());
        Activate(world);
        var requests = world.Fixture.MovementRequests!;
        await Assert.That(world.Actor.Npc.Unit.Buffs.Hidden.Select(buff => buff.TemplateId).ToArray())
            .IsEquivalentTo(new uint[] { 6613, 3590 });
        var status = world.Actor.Npc.Unit.MovementStatus.CopySnapshot();
        await Assert.That(status.Stunned || status.Sleeping || status.Rooted || status.Psychokinesis || status.ImpossibleRotate).IsFalse();
        await Assert.That(requests.SubmitProxyOutput(world.Actor, TurningOutput())).IsTrue();
        //The proxy divides by its old unmodified stance normal6. CActor applies the
        //actual3590 attribute10=+300 afterward: .5 * 6 * 1.3, after the native yaw.
        await Near(requests.Find(world.Actor.Handle)!.Request!.Delta, new(.5f, 0, 0));
        world.StepFrame();
        var owner = requests.Find(world.Actor.Handle)!;
        await Near(owner.LastVelocity, new(0, -3.9f, 0), .00001f);
        var expected = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI * .5f);
        await Assert.That(MathF.Abs(Quaternion.Dot(world.Body.CopyState().Status.Orientation, expected))).IsGreaterThan(.999999f);
        var before = world.Position;
        world.StepFrame(3);
        await Assert.That(world.Position.Y).IsLessThan(before.Y);
        await world.AssertPublished(world.Position);
        await Assert.That(owner.PreparedFrameCount).IsEqualTo(2ul);
        await Assert.That(world.Fixture.Runtime.Count).IsEqualTo(1);
    }

    [Test]
    public async Task RestoredOriginal1965KeepsTurningButItsMinus1000ModifierStopsThePhysicalWish()
    {
        using var world = new ManagedNpcPositionChecksTests.World(movementRequests: true, movementNormalSpeed: 6,
            movementBuffContent: OriginalMovementBuffs(), initialBuffs: new([Buff(1, 1965)], [], []),
            movementStatModifiers: OriginalMovementModifiers());
        Activate(world);
        var requests = world.Fixture.MovementRequests!;
        await Assert.That(world.Actor.Npc.Unit.Buffs.Good.Single().TemplateId).IsEqualTo(1965u);
        await Assert.That(world.Fixture.Runtime.Actors.MovementEligibility!.CanMove(world.Actor)).IsTrue();
        await Assert.That(requests.SubmitProxyOutput(world.Actor, TurningOutput())).IsTrue();
        world.StepFrame();
        await Assert.That(requests.Find(world.Actor.Handle)!.LastVelocity).IsEqualTo(Vector3.Zero);
        await Assert.That(world.Body.CopyState().Control.RequestedVelocity).IsEqualTo(Vector3.Zero);
        var expected = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI * .5f);
        await Assert.That(MathF.Abs(Quaternion.Dot(world.Body.CopyState().Status.Orientation, expected))).IsGreaterThan(.999999f);
        await Assert.That(world.Fixture.Runtime.Count).IsEqualTo(1);
        await world.AssertPublished(world.Position);
    }

    [Test]
    public async Task ImpossibleRotateAppliedAfterTheRequestBlocksOnlyYawAndRemovalReenablesTheCachedRequest()
    {
        var content = new ZoneBuffContent([new(900, 2, 6, 1, 0, 0, false, false, ImpossibleRotate: true)], [], [], [], [], []);
        using var world = new ManagedNpcPositionChecksTests.World(movementRequests: true, movementNormalSpeed: 6,
            movementBuffContent: content);
        Activate(world);
        var requests = world.Fixture.MovementRequests!;
        requests.SubmitProxyOutput(world.Actor, TurningOutput());
        var created = new BuffCreated(new(ZoneCasterKind.Unit, world.Actor.Npc.ObjectId), 0,
            world.Actor.Npc.ObjectId, 1, 900, 1, 0, 0, 1, 1, 1000, 0, 0);
        await Assert.That(world.Fixture.Admission.Registry.CreateBuff(created.ToFrame().Body.Span)).IsTrue();
        await Assert.That(world.Actor.Npc.Unit.MovementStatus.CopySnapshot().ImpossibleRotate).IsTrue();
        world.StepFrame();
        await Near(requests.Find(world.Actor.Handle)!.LastVelocity, new(3, 0, 0));
        await Assert.That(world.Body.CopyState().Status.Orientation).IsEqualTo(Quaternion.Identity);
        await Assert.That(world.Fixture.Admission.Registry.RemoveBuff(
            new BuffRemoved(world.Actor.Npc.ObjectId, 1).ToFrame().Body.Span)).IsTrue();
        await Assert.That(world.Actor.Npc.Unit.MovementStatus.CopySnapshot().ImpossibleRotate).IsFalse();
        world.StepFrame(3);
        await Near(requests.Find(world.Actor.Handle)!.LastVelocity, new(0, -3, 0), .00001f);
        await Assert.That(world.Fixture.Runtime.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(901u)]
    [Arguments(902u)]
    [Arguments(903u)]
    public async Task LiveStunSleepAndRootApplyTheirIndependentTurnAndSpeedConsumers(uint template)
    {
        var content = new ZoneBuffContent([
            new(901, 2, 6, 1, 0, 0, false, false, Stun: true),
            new(902, 2, 6, 1, 0, 0, false, false, Sleep: true),
            new(903, 2, 6, 1, 0, 0, false, false, Root: true)
        ], [], [], [], [], []);
        using var world = new ManagedNpcPositionChecksTests.World(movementRequests: true, movementNormalSpeed: 6,
            movementBuffContent: content);
        Activate(world);
        var requests = world.Fixture.MovementRequests!;
        requests.SubmitProxyOutput(world.Actor, TurningOutput());
        var created = new BuffCreated(new(ZoneCasterKind.Unit, world.Actor.Npc.ObjectId), 0,
            world.Actor.Npc.ObjectId, 1, template, 1, 0, 0, 1, 1, 1000, 0, 0);
        await Assert.That(world.Fixture.Admission.Registry.CreateBuff(created.ToFrame().Body.Span)).IsTrue();
        world.StepFrame();
        await Assert.That(requests.Find(world.Actor.Handle)!.LastVelocity).IsEqualTo(Vector3.Zero);
        var expected = template == 903 ? Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI * .5f) : Quaternion.Identity;
        await Assert.That(MathF.Abs(Quaternion.Dot(world.Body.CopyState().Status.Orientation, expected))).IsGreaterThan(.999999f);
        await Assert.That(world.Fixture.Runtime.Count).IsEqualTo(1);
    }

    //Actual10.x descriptor rows: all five turn/movement flags are false.6613's
    //separate no_collide_rigid effect is not claimed by this ordinary static profile.
    private static ZoneBuffContent OriginalMovementBuffs() => new([
        new(6613, 3, 1, 1, 0, 0, false, false, Scale: 1, ScaleDuration: 1, PsychokinesisSpeed: 100),
        new(3590, 3, 1, 1, 0, 0, false, false, Scale: 1, ScaleDuration: 1, PsychokinesisSpeed: 100),
        new(1965, 1, 1, 1, 0, 0, false, false, Scale: 1, ScaleDuration: 1, PsychokinesisSpeed: 100)
    ], [], [], [], [], []);

    private static IReadOnlyList<StatModifier> OriginalMovementModifiers() => [
        new(22538, "Buff", 3590, 10, 0, 300, 0, 0),
        new(18162, "Buff", 1965, 10, 0, -1000, 0, 0)
    ];

    private static UnitBuff Buff(uint index, uint template) => new(index, new(ZoneCasterKind.Unit, 1),
        0, 1, 1, 0, 0, 0, 0, template, 1, 0, 0);
    private static ZoneManagedNpcProxyMovementOutput TurningOutput() =>
        new(1, default, false, default, Vector3.UnitX, 3, 1, false, 4, 0);
    private static void Activate(ManagedNpcPositionChecksTests.World world)
    {
        world.Fixture.Runtime.Actors.GameObjectActivationProfile = ZoneManagedNpcGameObjectActivationProfile.MultiplayerWorldServer;
        world.Actor.Npc.Activation.SetActive(true, world.Fixture.Admission.World.GetActivationServices(world.Actor.Npc));
        if (!world.Actor.UpdateProxyActivationPolicy()) throw new InvalidOperationException("Actual proxy activation refused its NPC.");
    }
    private static Vector3 Vector(JsonElement value)
    { var v = value.EnumerateArray().Select(n => n.GetSingle()).ToArray(); return new(v[0], v[1], v[2]); }
    private static Quaternion Rotation(JsonElement value)
    { var v = value.EnumerateArray().Select(n => n.GetSingle()).ToArray(); return new(v[0], v[1], v[2], v[3]); }
    private static async Task Near(Vector3 actual, Vector3 expected, float tolerance = .0001f) =>
        await Assert.That(Vector3.Distance(actual, expected)).IsLessThan(tolerance);
}
