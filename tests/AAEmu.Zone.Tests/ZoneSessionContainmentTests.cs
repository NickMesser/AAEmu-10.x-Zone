using System.Reflection;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.WorldLink;

namespace AAEmu.Zone.Tests;

public sealed class ZoneSessionContainmentTests
{
    private static ushort[] AllOpcodes() => typeof(WzOpcodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
        .Where(field => field.IsLiteral && field.FieldType == typeof(ushort))
        .Select(field => (ushort)field.GetRawConstantValue()!).Distinct().ToArray();

    private static ZoneSession NewSession() => ZoneSession.CreateDetached(new MemoryStream());

    [Test]
    public async Task EveryOpcodeWithEmptyAndGarbageBodiesLeavesSessionAlive()
    {
        var session = NewSession();
        var opcodes = AllOpcodes();
        await Assert.That(opcodes.Length).IsGreaterThan(100);
        byte[][] bodies = [[], [0xFF], Enumerable.Repeat((byte)0xFF, 300).ToArray(), [1, 2, 3, 4, 5, 6, 7, 8]];
        foreach (var opcode in opcodes)
            foreach (var body in bodies)
                session.ReceiveForTest(new ZoneFrame(opcode, body));
        await Assert.That(session.IsClosed).IsFalse();
        // Unimplemented opcodes keep their own counter; implemented ones with bad bodies are faults or gate drops.
        await Assert.That(session.UnimplementedOpcodes.Count).IsGreaterThan(0);
        await Assert.That(session.HandlerFaults.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task UnconfiguredServicesAreCountedNotThrown()
    {
        var session = NewSession();
        session.ReceiveForTest(new ZoneFrame(WzOpcodes.SiegeState, new byte[8]));
        session.ReceiveForTest(new ZoneFrame(WzOpcodes.SiegeState, new byte[8]));
        session.ReceiveForTest(new ZoneFrame(WzOpcodes.UpdateAggro, new byte[8]));
        await Assert.That(session.HandlerFaults[WzOpcodes.SiegeState]).IsEqualTo(2);
        await Assert.That(session.HandlerFaults[WzOpcodes.UpdateAggro]).IsEqualTo(1);
    }

    [Test]
    public async Task NpcAndScheduleFramesAreDroppedBeforeLevelLoad()
    {
        var session = NewSession();
        await Assert.That(session.IsLevelLoaded).IsFalse();
        session.ReceiveForTest(new ZoneFrame(WzOpcodes.NpcState, new byte[4]));
        session.ReceiveForTest(new ZoneFrame(WzOpcodes.NpcSpawnerEvent, new byte[4]));
        session.ReceiveForTest(new ZoneFrame(WzOpcodes.GameScheduleStart, new byte[4]));
        await Assert.That(session.LevelGateDrops.Count).IsEqualTo(3);
        await Assert.That(session.HandlerFaults.ContainsKey(WzOpcodes.NpcState)).IsFalse();
    }

    [Test]
    public async Task ConnectionCancellationStillPropagates()
    {
        var session = NewSession();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.That(async () => await session.ReceiveAsync(cancel.Token)).Throws<OperationCanceledException>();
        await Assert.That(session.WorldDisconnected).IsFalse();
    }

    [Test]
    public async Task EndOfStreamIsRecordedAsWorldDisconnect()
    {
        var session = NewSession();
        await session.ReceiveAsync();
        await Assert.That(session.IsClosed).IsTrue();
        await Assert.That(session.WorldDisconnected).IsTrue();
    }

    [Test]
    public async Task GmDeferredSelectorsNeverThrow()
    {
        var session = NewSession();
        foreach (byte selector in new byte[] { 49, 142, 143, 144, 7 })
            foreach (var text in new[] { "", "1", "1,2", "1,2,3", "(" })
                session.ReceiveForTest(new ZoneFrame(WzOpcodes.GmCommand, GmCommand.FromText(1, selector, text).ToFrame().Body));
        await Assert.That(session.HandlerFaults.ContainsKey(WzOpcodes.GmCommand)).IsFalse();
        await Assert.That(session.Registry.ApplyGmCommand(GmCommand.FromText(1, 49, "5").ToFrame().Body.Span)).IsFalse();
    }

    [Test]
    public async Task DisconnectAndRefusalAreNamed()
    {
        await Assert.That(ZoneWorldLifecycle.DisconnectMessage(7)).IsEqualTo("zone: client disconnected (reason=7)");
        await Assert.That(ZoneWorldLifecycle.QuitsOnDisconnect(1)).IsFalse();
        await Assert.That(ZoneWorldLifecycle.QuitsOnDisconnect(2)).IsTrue();
        await Assert.That(ZoneWorldLifecycle.QuitsOnDisconnect(3)).IsTrue();
        await Assert.That(ZoneWorldLifecycle.JoinRefusalName(1)).IsEqualTo("off_3A51AB48[1]");
        await Assert.That(ZoneWorldLifecycle.JoinRefusalName(99)).IsEqualTo("off_3A51AB48[3]");
        await Assert.That(ZoneWorldLifecycle.JoinRefusalMessage(2)).Contains("refused joining(off_3A51AB48[2])");
    }
}
