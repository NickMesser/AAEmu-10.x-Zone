using System.Numerics;
using System.Text.Json;
using AAEmu.Zone.Scene;

namespace AAEmu.Zone.Tests;

public sealed class NpcControllerAngleTests
{
    [Test]
    public async Task OriginalCompleteControllerAngleAndSpringMatchTheCopiedSeventyTwoControls()
    {
        using var stream = typeof(NpcControllerAngleTests).Assembly.GetManifestResourceStream(
            "AAEmu.Zone.Tests.Fixtures.npc-controller-angle-native-controls.json")!;
        using var document = JsonDocument.Parse(stream);
        var count = 0;
        foreach (var row in document.RootElement.GetProperty("rows").EnumerateArray())
        {
            var before = State(row.GetProperty("before"));
            var rotation = row.GetProperty("rotation").EnumerateArray().Select(v => v.GetSingle()).ToArray();
            var actual = ZoneNpcControllerSlopeMath.Step(before, row.GetProperty("elapsed").GetSingle(),
                Vector(row.GetProperty("inputNormal")), new(rotation[0], rotation[1], rotation[2], rotation[3]),
                row.GetProperty("suppress").GetBoolean());
            var expected = State(row.GetProperty("after"));
            await Bits(actual.Normal.X, expected.Normal.X); await Bits(actual.Normal.Y, expected.Normal.Y); await Bits(actual.Normal.Z, expected.Normal.Z);
            await Bits(actual.Primary, expected.Primary); await Bits(actual.Secondary, expected.Secondary);
            await Bits(actual.PrimaryVelocity, expected.PrimaryVelocity); await Bits(actual.SecondaryVelocity, expected.SecondaryVelocity);
            count++;
        }
        await Assert.That(count).IsEqualTo(72);
    }

    [Test]
    public async Task ActualPhysicalPublicationOwnsTheSpeedStateAndRetirementRejectsTheOldGeneration()
    {
        using var world = new ManagedNpcPositionChecksTests.World();
        var actual = world.Motion.CopyMovementSpeedState(world.Actor);
        await Assert.That(actual.Stance).IsEqualTo(world.Body.CopyState().CurrentStanceSlot);
        await Assert.That(actual.ControllerAngle).IsEqualTo(0f);
        world.Fixture.Admission.World.Remove(world.Actor.Npc);
        await Assert.That(() => world.Motion.CopyMovementSpeedState(world.Actor)).Throws<ObjectDisposedException>();
    }

    private static Vector3 Vector(JsonElement value)
    { var v = value.EnumerateArray().Select(n => n.GetSingle()).ToArray(); return new(v[0], v[1], v[2]); }
    private static ZoneNpcControllerSlopeState State(JsonElement value) => new(Vector(value.GetProperty("normal")),
        value.GetProperty("primary").GetSingle(), value.GetProperty("secondary").GetSingle(),
        value.GetProperty("primaryVelocity").GetSingle(), value.GetProperty("secondaryVelocity").GetSingle());
    private static async Task Bits(float actual, float expected) =>
        await Assert.That(BitConverter.SingleToUInt32Bits(actual)).IsEqualTo(BitConverter.SingleToUInt32Bits(expected));
}
