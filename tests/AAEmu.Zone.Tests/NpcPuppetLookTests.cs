using System.Numerics;
using System.Text.Json;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Navigation;
using AAEmu.Zone.Scene;

namespace AAEmu.Zone.Tests;

public sealed class NpcPuppetLookTests
{
    [Test]
    public async Task OriginalFortyTwoControlsExercisePathAttentionStrafeAndRetainedTargetSmoothing()
    {
        using var stream = typeof(NpcPuppetLookTests).Assembly.GetManifestResourceStream("AAEmu.Zone.Tests.Fixtures.npc-puppet-look-native-controls.json")!;
        using var document = JsonDocument.Parse(stream);
        var count = 0; var maximumError = 0f;
        foreach (var value in document.RootElement.GetProperty("controls").EnumerateArray())
        {
            var path = Path(value.GetProperty("path")); path.CurrentFraction = Number("fraction");
            var original = Path(value.GetProperty("originalPath")); original.CurrentFraction = Number("fraction");
            var attentionValue = value.GetProperty("attention");
            ZoneNpcPuppetLookTarget? attention = attentionValue.ValueKind == JsonValueKind.Null ? null :
                new(Vector(attentionValue.GetProperty("viewPosition")), Vector(attentionValue.GetProperty("entityPosition")), attentionValue.GetProperty("type").GetInt32());
            var controls = new ZoneActorControlState
            {
                AllowStrafing = Boolean("wasStrafing"), DistanceToPathEnd = Number("remaining"),
                AllowedStrafeDistances = new(Number("strafeStart"), Number("strafeEnd"), Boolean("direct")),
                StrafeStartDistance = Number("strafeProgress"), PathStartPosition = Vector(value.GetProperty("pathStart")),
            };
            var strafe = new StrafeServices(Boolean("canTurn"), original, Vector(value.GetProperty("viewPosition")),
                Boolean("loose") ? null : attention, value.GetProperty("movementOutput").EnumerateArray().Select(Vector).ToArray());
            ZoneActorMovementControl.UpdateStrafing(controls, strafe);
            var expected = value.GetProperty("expected");
            await Assert.That(controls.AllowStrafing).IsEqualTo(expected.GetProperty("allowStrafing").GetBoolean());
            await Assert.That(controls.StrafeStartDistance).IsEqualTo(expected.GetProperty("strafeProgress").GetSingle());
            var speed = controls.AllowStrafing && Boolean("instantStrafe") ? 0 :
                value.GetProperty("alertness").GetInt32() <= 0 ? Number("idleTurnSpeed") : Number("combatTurnSpeed");
            if (speed >= .000001f) speed *= value.GetProperty("attribute72").GetInt32() * .01f + 1;
            Vector3? sample = null;
            var position = Vector(value.GetProperty("entityPosition"));
            var pathSample = position;
            if (path.TryGetPosition(2.5f, true, true, ref pathSample)) sample = pathSample;
            var output = ZoneNpcPuppetLookMath.Build(new(Vector(value.GetProperty("viewPosition")), position,
                Vector(value.GetProperty("bodyDirection")), attention, false, path.HasPath, Number("desiredSpeed"),
                Vector(value.GetProperty("movementDirection")), value.GetProperty("actorTargetPhase").GetInt32(), sample,
                controls.AllowStrafing, Boolean("wasStrafing"), Vector(value.GetProperty("previousLook")),
                Vector(value.GetProperty("previousBody")), speed, Number("elapsed")));
            var lookError = Vector3.Distance(output.LookTarget, Vector(expected.GetProperty("look")));
            var bodyError = Vector3.Distance(output.BodyTarget, Vector(expected.GetProperty("body")));
            maximumError = MathF.Max(maximumError, MathF.Max(lookError, bodyError));
            if (lookError > .00003f || bodyError > .00003f)
                throw new InvalidOperationException($"{value.GetProperty("name").GetString()}: look={output.LookTarget}/{expected.GetProperty("look")}, body={output.BodyTarget}/{expected.GetProperty("body")}; errors={lookError:R}/{bodyError:R}");
            count++;
            float Number(string name) => value.GetProperty(name).GetSingle();
            bool Boolean(string name) => value.GetProperty(name).GetBoolean();
        }
        await Assert.That(count).IsEqualTo(42);
        await Assert.That(maximumError).IsLessThan(.00003f);
    }

    [Test]
    public async Task MovingLookKeepsViewHeightWhileNewStrafingBodyUsesTheEntityOrigin()
    {
        var input = new ZoneNpcPuppetLookInput(new(10, 20, 2), new(10, 20, 0), Vector3.UnitY,
            null, false, true, 2, Vector3.UnitX, 0, new(20, 20, 0), true, false, default, default, 0, .1f);
        var actual = ZoneNpcPuppetLookMath.Build(input);
        await Assert.That(actual.LookTarget).IsEqualTo(new Vector3(50, 20, 2));
        await Assert.That(actual.BodyTarget).IsEqualTo(new Vector3(50, 20, 0));
        var stopped = ZoneNpcPuppetLookMath.Build(input with
        { DesiredSpeed = 0, AllowStrafing = false, PreviousBodyTarget = actual.BodyTarget, PreviousLookTarget = actual.LookTarget });
        await Assert.That(stopped.LookTarget).IsEqualTo(Vector3.Zero);
        await Assert.That(stopped.BodyTarget).IsEqualTo(actual.BodyTarget);
    }

    [Test]
    public async Task OriginalSigned64ClockAndActorIntervalControlsMatchExactly()
    {
        using var stream = typeof(NpcPuppetLookTests).Assembly.GetManifestResourceStream("AAEmu.Zone.Tests.Fixtures.npc-puppet-look-native-controls.json")!;
        using var document = JsonDocument.Parse(stream);
        var count = 0;
        foreach (var control in document.RootElement.GetProperty("intervalControls").EnumerateArray())
        {
            var previous = control.GetProperty("previous").GetInt64(); var now = control.GetProperty("now").GetInt64();
            var actual = ZoneManagedNpcPuppetOutput.CalculateTimePassed(previous, now);
            await Assert.That(BitConverter.SingleToUInt32Bits(actual)).IsEqualTo(control.GetProperty("timePassedBits").GetUInt32());
            await Assert.That(control.GetProperty("publishedClock").GetInt64()).IsEqualTo(now);
            count++;
        }
        await Assert.That(count).IsEqualTo(15);
    }

    [Test]
    public async Task ActualNpcControlConstructorMatchesTheExecutedOriginalSobjectStateDefaults()
    {
        using var stream = typeof(NpcPuppetLookTests).Assembly.GetManifestResourceStream("AAEmu.Zone.Tests.Fixtures.npc-puppet-look-native-controls.json")!;
        using var document = JsonDocument.Parse(stream);
        var control = document.RootElement.GetProperty("constructor");
        var actual = ZoneManagedNpcActorScene.CreatePipeUserControls();
        await Assert.That(actual.BodyState).IsEqualTo(control.GetProperty("bodyState").GetInt32());
        await Assert.That(BitConverter.SingleToUInt32Bits(actual.DesiredSpeed)).IsEqualTo(control.GetProperty("desiredSpeedBits").GetUInt32());
        await Assert.That(BitConverter.SingleToUInt32Bits(actual.DistanceToPathEnd)).IsEqualTo(control.GetProperty("distanceBits").GetUInt32());
        await Assert.That(BitConverter.SingleToUInt32Bits(actual.MovementUrgency)).IsEqualTo(control.GetProperty("urgencyBits").GetUInt32());
        await Assert.That(BitConverter.SingleToUInt32Bits(actual.GoalMovementUrgency)).IsEqualTo(control.GetProperty("goalUrgencyBits").GetUInt32());
        await Assert.That(actual.MovementDirection).IsEqualTo(Vector(control.GetProperty("move")));
        await Assert.That(actual.AllowStrafing).IsEqualTo(control.GetProperty("allowStrafing").GetBoolean());
        await Assert.That(Vector(control.GetProperty("look"))).IsEqualTo(Vector3.Zero);
        await Assert.That(Vector(control.GetProperty("body"))).IsEqualTo(Vector3.Zero);
        await Assert.That(control.GetProperty("lookEnabled").GetBoolean()).IsFalse();
    }

    private static Vector3 Vector(JsonElement element) => new(element[0].GetSingle(), element[1].GetSingle(), element[2].GetSingle());
    private static ZoneNavigationPath Path(JsonElement value)
    { var path = new ZoneNavigationPath(); foreach (var point in value.EnumerateArray()) path.PushBack(new(Vector(point), 2), true); return path; }
    private sealed class StrafeServices(bool canTurn, ZoneNavigationPath path, Vector3 position,
        ZoneNpcPuppetLookTarget? attention, Vector3[] movementOutput) : IZoneActorStrafeServices
    {
        public bool CanStrafe() => canTurn;
        public bool HasPath() => path.HasPath;
        public float GetPathLength() => path.GetLength();
        public Vector3 GetPosition() => position;
        public object? GetAttentionTarget() => attention;
        public Vector3 GetTargetPosition(object target) => ((ZoneNpcPuppetLookTarget)target).ViewPosition;
        public Vector3? GetFirstPathOutputPosition() => movementOutput.Length == 0 ? null : movementOutput[0];
    }
}
