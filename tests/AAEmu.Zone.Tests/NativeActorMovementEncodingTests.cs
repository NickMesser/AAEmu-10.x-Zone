using System.Numerics;
using System.Text.Json;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Protocol.Units;

namespace AAEmu.Zone.Tests;

public sealed class NativeActorMovementEncodingTests
{
    [Test]
    public async Task WholeActorWriterMatches68OriginalInstructionControlsIncludingAxisAngleAndSignedTies()
    {
        using var stream = typeof(NativeActorMovementEncodingTests).Assembly.GetManifestResourceStream(
            "AAEmu.Zone.Tests.Fixtures.npc-movement-wire-native-controls.json")!;
        using var json = JsonDocument.Parse(stream);
        var compared = 0;
        var prefix = new ZoneWriter(); prefix.WriteBc(777); var prefixLength = prefix.ToArray().Length;
        foreach (var control in json.RootElement.GetProperty("controls").EnumerateArray())
        {
            var input = control.GetProperty("input"); var flags = input.GetProperty("actorFlags").GetUInt16();
            var rotation = input.GetProperty("rotation");
            var heading = UnitHeading.FromQuaternion(new(rotation[0].GetSingle(), rotation[1].GetSingle(), rotation[2].GetSingle(), rotation[3].GetSingle()));
            var movement = new UnitMovementData(1, input.GetProperty("time").GetUInt32(), input.GetProperty("movementFlags").GetByte(),
                input.GetProperty("controllerId").GetUInt32(), input.GetProperty("phase").GetByte(),
                UnitMovementQuantization.Position(new(input.GetProperty("fixedX").GetInt64(), input.GetProperty("fixedY").GetInt64(), input.GetProperty("z").GetSingle())),
                UnitMovementQuantization.ActorVelocity(Vector(input.GetProperty("velocity"))),
                new(heading.AxisX, heading.AxisY, heading.AxisZ),
                new UnitActorMovement(UnitMovementQuantization.ActorDelta(Vector(input.GetProperty("delta"))),
                    input.GetProperty("stance").GetSByte(), input.GetProperty("alertness").GetSByte(), flags,
                    UnitMovementQuantization.ActorFallVelocity(input.GetProperty("fallVelocity").GetSingle())));
            var wire = new UnitMovement(777, movement).Write();
            await Assert.That(Convert.ToHexString(wire.AsSpan(prefixLength))).IsEqualTo(control.GetProperty("expectedPayload").GetString());
            await Assert.That(UnitMovement.Read(wire).Movement).IsEqualTo(movement);
            compared++;
        }
        await Assert.That(compared).IsEqualTo(68);
    }

    [Test]
    public async Task NonFiniteMovementIsRefusedBeforeQuantization()
    {
        await Assert.That(() => UnitMovementQuantization.ActorVelocity(new(float.NaN, 0, 0))).Throws<InvalidDataException>();
        await Assert.That(() => UnitMovementQuantization.ActorDelta(new(0, float.PositiveInfinity, 0))).Throws<InvalidDataException>();
        await Assert.That(() => UnitMovementQuantization.ActorFallVelocity(float.NegativeInfinity)).Throws<InvalidDataException>();
        await Assert.That(() => UnitMovementQuantization.Position(new(0, 0, float.NaN))).Throws<InvalidDataException>();
    }

    private static Vector3 Vector(JsonElement e) => new(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle());
}
