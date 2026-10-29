using System.Numerics;
using System.Text.Json;
using AAEmu.Zone.Actors;

namespace AAEmu.Zone.Tests;

public sealed class ActorMovementSpeedTests
{
    [Test]
    public async Task OriginalSpeedLeafMatchesEveryStanceUrgencyAndDirectionalControl()
    {
        using var stream = typeof(ActorMovementSpeedTests).Assembly.GetManifestResourceStream(
            "AAEmu.Zone.Tests.Fixtures.npc-movement-speed-native-controls.json")!;
        using var json = JsonDocument.Parse(stream);
        var rows = json.RootElement.GetProperty("tableBits").EnumerateArray().Select(row =>
            new ZoneActorMovementSpeeds(Float(row[0]), Float(row[1]), Float(row[2]))).ToArray();
        var table = new ZoneActorMovementSpeedTable(rows);
        var count = 0;
        foreach (var control in json.RootElement.GetProperty("controls").EnumerateArray())
        {
            var referenceMinimum = Float(control.GetProperty("referenceMinimumBits"));
            var result = table.Calculate(Float(control.GetProperty("urgencyBits")), control.GetProperty("stance").GetInt32(),
                referenceMinimum, Float(control.GetProperty("referenceMaximumBits")), Float(control.GetProperty("turnRateBits")),
                Vector(control.GetProperty("bodyBits")), Vector(control.GetProperty("moveBits")), Float(control.GetProperty("controllerAngleBits")));
            var expected = control.GetProperty("expectedBits");
            if (!Same(result.Desired, Float(expected[0]), referenceMinimum > 0 ? 2 : 0))
                throw new InvalidOperationException($"Native speed control {count}: desired {result.Desired:R} (0x{BitConverter.SingleToUInt32Bits(result.Desired):X8}), expected {Float(expected[0]):R} (0x{expected[0].GetUInt32():X8}).");
            await Assert.That(Same(result.Desired, Float(expected[0]), referenceMinimum > 0 ? 2 : 0)).IsTrue();
            await Assert.That(Same(result.Minimum, Float(expected[1]), 0)).IsTrue();
            await Assert.That(Same(result.Maximum, Float(expected[2]), 0)).IsTrue();
            count++;
        }
        await Assert.That(count).IsEqualTo(183);
    }

    private static bool Same(float actual, float expected, int ulps)
    {
        if (float.IsNaN(expected)) return float.IsNaN(actual);
        if (float.IsNaN(actual)) return false;
        var a = BitConverter.SingleToInt32Bits(actual); var b = BitConverter.SingleToInt32Bits(expected);
        if (a == b) return true;
        static long Ordered(int value) => value < 0 ? (long)int.MinValue - value : value;
        return Math.Abs(Ordered(a) - Ordered(b)) <= ulps;
    }
    private static float Float(JsonElement value) => BitConverter.UInt32BitsToSingle(value.GetUInt32());
    private static Vector3 Vector(JsonElement value) => new(Float(value[0]), Float(value[1]), Float(value[2]));
}
