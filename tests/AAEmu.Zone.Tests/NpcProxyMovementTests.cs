using System.Numerics;
using System.Text.Json;
using AAEmu.Zone.Scene;

namespace AAEmu.Zone.Tests;

public sealed class NpcProxyMovementTests
{
    [Test]
    public async Task OriginalFullActorDynamicsPreservesSignedNonpositiveInertiaAndAppliesTheActualWalkingLatch()
    {
        using var stream = typeof(NpcProxyMovementTests).Assembly.GetManifestResourceStream(
            "AAEmu.Zone.Tests.Fixtures.npc-actor-dynamics-native-controls.json")!;
        using var document = JsonDocument.Parse(stream);
        var count = 0;
        foreach (var row in document.RootElement.GetProperty("inertia").EnumerateArray())
        {
            var actual = ZoneNpcOrdinaryMovementMath.EffectiveInertia(row.GetProperty("authored").GetSingle(),
                row.GetProperty("walking").GetBoolean());
            await Bits(actual, row.GetProperty("actual").GetSingle());
            count++;
        }
        await Assert.That(count).IsEqualTo(12);
    }

    [Test]
    public async Task CompleteOriginalOrdinaryPlayerPipelinePreservesItsNativeRotationThenVelocityOrder()
    {
        using var stream = typeof(NpcProxyMovementTests).Assembly.GetManifestResourceStream(
            "AAEmu.Zone.Tests.Fixtures.npc-actor-ordinary-native-controls.json")!;
        using var document = JsonDocument.Parse(stream);
        var count = 0;
        foreach (var row in document.RootElement.GetProperty("rows").EnumerateArray())
        {
            var rotation = Rotation(row.GetProperty("entityRotation"));
            var request = new ZoneManagedNpcControllerRequest(4 | 0x20 | 8 | 0x800 | 0x100000 | 0x10000000 | 0x8000000,
                default, default, 3, 0, 1, Vector(row.GetProperty("localDelta")), 0, 1, false);
            var air = row.GetProperty("air").GetBoolean();
            var actual = ZoneNpcOrdinaryMovementMath.Build(request, new(10, 20, 10), rotation, new(10, 20, 11.5f), rotation,
                6, air ? 0 : .02f, air ? 1 : 0, true, true);
            var native = row.GetProperty("request").EnumerateArray().Single();
            await Assert.That(native.GetProperty("type").GetInt32()).IsEqualTo(1);
            await Assert.That(Vector3.Distance(actual.Velocity, Vector(native.GetProperty("velocity")))).IsLessThan(.000005f);
            await Assert.That(MathF.Abs(Quaternion.Dot(actual.Orientation, Rotation(row.GetProperty("bodyRotation"))))).IsGreaterThan(.999999f);
            await Assert.That(Vector3.Distance(actual.LookTarget, Vector(row.GetProperty("controllerLook")))).IsLessThan(.000005f);
            count++;
        }
        await Assert.That(count).IsEqualTo(6);
    }

    [Test]
    public async Task CompleteOriginalProxyRequestBuilderMatchesTwoHundredEightyEightControls()
    {
        using var stream = typeof(NpcProxyMovementTests).Assembly.GetManifestResourceStream(
            "AAEmu.Zone.Tests.Fixtures.npc-proxy-movement-native-controls.json")!;
        using var document = JsonDocument.Parse(stream);
        var count = 0;
        foreach (var row in document.RootElement.GetProperty("rows").EnumerateArray())
        {
            var input = row.GetProperty("input");
            var output = new ZoneManagedNpcProxyMovementOutput(input.GetProperty("bodyState").GetInt32(),
                Vector(input.GetProperty("lookTarget")), input.GetProperty("lookTargetEnabled").GetBoolean(),
                Vector(input.GetProperty("bodyTarget")), Vector(input.GetProperty("moveDirection")),
                input.GetProperty("desiredSpeed").GetSingle(), input.GetProperty("movementUrgency").GetSingle(),
                input.GetProperty("allowStrafing").GetBoolean(), input.GetProperty("distanceToPathEnd").GetSingle(),
                input.GetProperty("movementContext").GetInt32());
            var rotation = row.GetProperty("rotation").EnumerateArray().Select(value => value.GetSingle()).ToArray();
            var actual = ZoneNpcProxyMovementMath.Build(output, new(rotation[0], rotation[1], rotation[2], rotation[3]),
                row.GetProperty("normalSpeed").GetSingle(), row.GetProperty("canMove").GetBoolean(), row.GetProperty("alertness").GetInt32());
            var expected = row.GetProperty("result");
            await Assert.That(actual.Flags).IsEqualTo(expected.GetProperty("flags").GetUInt32());
            await Bits(actual.MoveTarget, Vector(expected.GetProperty("moveTarget")));
            await Bits(actual.LookTarget, Vector(expected.GetProperty("lookTarget")));
            await Bits(actual.DesiredSpeed, expected.GetProperty("desiredSpeed").GetSingle());
            await Bits(actual.DistanceToPathEnd, expected.GetProperty("distanceToPathEnd").GetSingle());
            await Bits(actual.Delta, Vector(expected.GetProperty("delta")));
            await Assert.That(actual.Stance).IsEqualTo(expected.GetProperty("stance").GetInt32());
            await Assert.That(actual.Alertness).IsEqualTo(expected.GetProperty("alertness").GetInt32());
            await Bits(actual.MovementScalar, expected.GetProperty("movementScalar").GetSingle());
            var walking = expected.GetProperty("walking");
            await Assert.That(actual.Walking).IsEqualTo(walking.ValueKind == JsonValueKind.Null ? (bool?)null : walking.GetBoolean());
            count++;
        }
        await Assert.That(count).IsEqualTo(288);
    }

    private static Vector3 Vector(JsonElement value)
    { var v = value.EnumerateArray().Select(n => n.GetSingle()).ToArray(); return new(v[0], v[1], v[2]); }
    private static Quaternion Rotation(JsonElement value)
    { var v = value.EnumerateArray().Select(n => n.GetSingle()).ToArray(); return new(v[0], v[1], v[2], v[3]); }
    private static async Task Bits(Vector3 actual, Vector3 expected)
    { await Bits(actual.X, expected.X); await Bits(actual.Y, expected.Y); await Bits(actual.Z, expected.Z); }
    private static async Task Bits(float actual, float expected) =>
        await Assert.That(BitConverter.SingleToUInt32Bits(actual)).IsEqualTo(BitConverter.SingleToUInt32Bits(expected));
}
