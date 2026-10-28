using AAEmu.Zone.Actors;
using AAEmu.Zone.Hosting;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.Tests;

public sealed class ZoneUnitLocationTests
{
    [Test]
    public async Task WireMatchesTheNativeBcAndUncompressedPositionSerializer()
    {
        var message = new ZoneUnitLocation(0x123456, ZoneMovementPoseMath.ToUnitWorldPosition(new(15.5f, -8.25f, 3.25f)));
        var frame = message.ToFrame();
        await Assert.That(frame.Opcode).IsEqualTo(ZwOpcodes.UnitLocation);
        await Assert.That(frame.Body.ToArray().SequenceEqual(Convert.FromHexString(
            "5634120000000000f8000000000000007cffff00005040"))).IsTrue();
        await Assert.That(ZoneUnitLocation.Read(frame.Body.Span)).IsEqualTo(message);
        for (var length = 0; length < frame.Body.Length; length++)
        {
            var truncated = frame.Body[..length].ToArray();
            await Assert.That(() => ZoneUnitLocation.Read(truncated)).Throws<Exception>();
        }
        await Assert.That(() => ZoneUnitLocation.Read([.. frame.Body.ToArray(), 0])).Throws<Exception>();
    }

    [Test]
    public async Task ReportsLiveWorldPosesOnlyWhenTheNativeDebugGateIsEnabled()
    {
        await Assert.That(ZoneEngineCVars.Empty.GetBool(ZoneEngineCVars.UnitLocationDebug, false)).IsFalse();
        var cvars = ZoneEngineCVars.Parse("unit_location_debug = 1", new Dictionary<string, string> { [ZoneEngineCVars.UnitLocationDebug] = "0" });
        await Assert.That(cvars.GetBool(ZoneEngineCVars.UnitLocationDebug, false)).IsFalse();
        var registry = new ZoneUnitRegistry();
        registry.Create(Convert.FromHexString(UnitStateSnapshotTests.PlayerEnterBody));
        var unit = registry.Units.Values.Single();
        var reporter = new ZoneUnitLocationReporter(registry, enabled: false);
        await Assert.That(reporter.Update()).IsEqualTo(0);
        await Assert.That(registry.TryDequeueNotification(out _)).IsFalse();
        var live = ZoneMovementPoseMath.ToWorldPosition(new(100, 200, 300), new(9, 14));
        unit.CommitPose(new(live, unit.Pose.Rotation));
        reporter.Enabled = true;
        await Assert.That(reporter.Update()).IsEqualTo(1);
        await Assert.That(registry.TryDequeueNotification(out var output)).IsTrue();
        await Assert.That(ZoneUnitLocation.Read(output.Body.Span)).IsEqualTo(new ZoneUnitLocation(unit.Snapshot.ObjectId, live));
        await Assert.That(ZoneUnitLocation.Read(output.Body.Span).Position.X).IsEqualTo(9316d);
        await Assert.That(reporter.Update()).IsEqualTo(1); // native debug reporting has no movement/delta suppression.
        registry.TryDequeueNotification(out _);
        var writer = new ZoneWriter(); writer.WriteBc(unit.Snapshot.ObjectId);
        registry.Remove(writer.ToArray());
        await Assert.That(reporter.Update()).IsEqualTo(0);
    }
}
