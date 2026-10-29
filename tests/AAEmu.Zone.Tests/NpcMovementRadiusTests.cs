using System.Text.Json;
using AAEmu.Zone.Content;
using AAEmu.Zone.Scripting;

namespace AAEmu.Zone.Tests;

public sealed class NpcMovementRadiusTests
{
    [Test]
    public async Task OriginalActorModelFallbackAndInflationControlsMatchExactly()
    {
        using var stream = typeof(NpcMovementRadiusTests).Assembly.GetManifestResourceStream("AAEmu.Zone.Tests.Fixtures.npc-movement-radius-native-controls.json")!;
        using var document = JsonDocument.Parse(stream); var count = 0;
        foreach (var value in document.RootElement.GetProperty("controls").EnumerateArray())
        {
            var model = new ZoneModelDefinition
            {
                Id = 42, Subtype = "ActorModel", SubtypeId = 7, Big = value.GetProperty("big").GetBoolean(),
                ForceNavigateAroundObject = value.GetProperty("forceNavigateAroundObject").GetBoolean(),
            };
            var rows = value.GetProperty("stances").EnumerateArray().Select((row, index) => new ZoneGameStanceDefinition
            {
                Id = (uint)index + 1, ActorModelId = 7, StanceId = row.GetProperty("id").GetInt32(),
                SizeX = row.GetProperty("x").GetSingle(), SizeY = row.GetProperty("y").GetSingle(),
                UseCapsule = row.GetProperty("capsule").GetBoolean(),
            }).ToArray();
            var content = new ZoneActorModelContent([new() { Id = 7, Radius = value.GetProperty("modelRadius").GetSingle() }], rows);
            var actual = ZoneNpcMovementRadiusMath.Resolve(value.GetProperty("passRadius").GetSingle(),
                value.GetProperty("pathRadius").GetSingle(), value.GetProperty("scale").GetSingle(), model, content.Models[7]);
            var expected = value.GetProperty("expected");
            if (BitConverter.SingleToUInt32Bits(actual.PassRadius) != expected.GetProperty("passRadiusBits").GetUInt32() ||
                BitConverter.SingleToUInt32Bits(actual.PathRadius) != expected.GetProperty("pathRadiusBits").GetUInt32())
                throw new InvalidOperationException($"Native radius control {value.GetProperty("name")}: {actual}.");
            count++;
        }
        await Assert.That(count).IsEqualTo(102);
    }
}
