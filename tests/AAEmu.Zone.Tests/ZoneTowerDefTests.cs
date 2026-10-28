using AAEmu.Zone.Content;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.WorldLink;
using AAEmu.Zone.WorldState;

namespace AAEmu.Zone.Tests;

public sealed class ZoneTowerDefTests
{
    // Real capture body from the native baseline: towerDef 13, zoneGroup 1.
    private static readonly byte[] QueryBody = [0x0d, 0x00, 0x00, 0x00, 0x01, 0x00];

    private static ZoneNpcSpawnerControllerContent Content() => new(
        [new ZoneNpcSpawnerTowerDefinition(13, 500), new ZoneNpcSpawnerTowerDefinition(14, 0), new ZoneNpcSpawnerTowerDefinition(15, 600)],
        [new ZoneNpcSpawnerTowerPhaseDefinition(1, 13), new ZoneNpcSpawnerTowerPhaseDefinition(2, 14)],
        [], [], [], [], []);

    // Spawner type 500 has 3 physical spawners in this synthetic zone; everything else has none.
    private static ZoneTowerDefController Ready(Func<uint, int>? counter = null)
    {
        var controller = new ZoneTowerDefController(counter ?? (type => type == 500 ? 3 : 0));
        controller.BeginReload(Content());
        controller.CompletePostLoad();
        return controller;
    }

    [Test]
    public async Task ParsesRealBodies()
    {
        var query = TowerDefQueryPlayability.Read(QueryBody);
        await Assert.That(query.Key).IsEqualTo(new TowerDefKey(13, 1));
        var start = TowerDefSpotMessage.Read([0x0d, 0, 0, 0, 1, 0, 2, 0, 0, 0]);
        await Assert.That(start.SpotIndex).IsEqualTo(2);
        var wave = TowerDefWaveStart.Read([0x0d, 0, 0, 0, 1, 0, 2, 0, 0, 0, 4, 0, 0, 0]);
        await Assert.That(wave.Step).IsEqualTo(4);
        await Assert.That(() => TowerDefQueryPlayability.Read([1, 2, 3])).Throws<Exception>();
        await Assert.That(() => TowerDefReload.Read([1])).Throws<Exception>();
    }

    [Test]
    public async Task ReplyFrameLayout()
    {
        var frame = new TowerDefReportPlayability(new(13, 1), 3).ToFrame();
        await Assert.That(frame.Opcode).IsEqualTo(ZwOpcodes.TowerDefReportPlayability);
        await Assert.That(frame.Body.ToArray()).IsEquivalentTo(new byte[] { 0x0d, 0, 0, 0, 1, 0, 3, 0, 0, 0 });
    }

    [Test]
    public async Task ZoneWithoutPlayableSpotsStaysSilent()
    {
        var controller = Ready(_ => 0);
        await Assert.That(controller.QueryPlayability(TowerDefQueryPlayability.Read(QueryBody))).IsNull();
        // Unknown tower, phaseless tower and zero-primary tower are also silent.
        var live = Ready();
        foreach (var id in new uint[] { 999, 14, 15 })
            await Assert.That(live.QueryPlayability(new(new(id, 1)))).IsNull();
    }

    [Test]
    public async Task NotReadyIsSilent()
    {
        var controller = new ZoneTowerDefController(_ => 3);
        controller.BeginReload(Content());
        await Assert.That(controller.QueryPlayability(new(new(13, 1)))).IsNull();
    }

    [Test]
    public async Task ZoneWithSpotsReplies()
    {
        var frame = Ready().QueryPlayability(TowerDefQueryPlayability.Read(QueryBody));
        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.Body.ToArray()).IsEquivalentTo(new byte[] { 0x0d, 0, 0, 0, 1, 0, 3, 0, 0, 0 });
    }

    [Test]
    public async Task StateTransitions()
    {
        var controller = Ready();
        var key = new TowerDefKey(13, 1);
        await Assert.That(controller.Start(new(key, 3))).IsFalse();   // out of range
        await Assert.That(controller.Start(new(new(15, 1), 0))).IsFalse(); // not indexed
        await Assert.That(controller.WaveStart(new(key, 0, 1))).IsFalse(); // not playing
        await Assert.That(controller.Start(new(key, 2))).IsTrue();
        await Assert.That(controller.Start(new(key, 2))).IsFalse();   // already playing
        await Assert.That(controller.QueryPlayability(new(key))).IsNull(); // playing: silent
        await Assert.That(controller.WaveStart(new(key, 2, 5))).IsTrue();
        await Assert.That(controller.Running[key]).IsEqualTo(5);
        await Assert.That(controller.End(new(key, 2))).IsTrue();
        await Assert.That(controller.End(new(key, 2))).IsFalse();
        await Assert.That(controller.QueryPlayability(new(key))).IsNotNull(); // playable again
    }

    [Test]
    public async Task SessionDispatchQueuesReplyOnlyForPlayableTower()
    {
        var session = ZoneSession.CreateDetached(new MemoryStream());
        session.ReceiveForTest(new ZoneFrame(WzOpcodes.TowerDefQueryPlayability, QueryBody)); // no controller: dropped
        await Assert.That(session.Registry.TryDequeueNotification(out _)).IsFalse();
        session.TowerDefsOverride = Ready();
        session.ReceiveForTest(new ZoneFrame(WzOpcodes.TowerDefQueryPlayability, new byte[] { 0x63, 0, 0, 0, 1, 0 }));
        await Assert.That(session.Registry.TryDequeueNotification(out _)).IsFalse();
        session.ReceiveForTest(new ZoneFrame(WzOpcodes.TowerDefQueryPlayability, QueryBody));
        await Assert.That(session.Registry.TryDequeueNotification(out var reply)).IsTrue();
        await Assert.That(reply.Opcode).IsEqualTo(ZwOpcodes.TowerDefReportPlayability);
        session.ReceiveForTest(new ZoneFrame(WzOpcodes.TowerDefStart, new byte[] { 0x0d, 0, 0, 0, 1, 0, 0, 0, 0, 0 }));
        await Assert.That(session.TowerDefs!.Running.Count).IsEqualTo(1);
        session.ReceiveForTest(new ZoneFrame(WzOpcodes.TowerDefEnd, new byte[] { 0x0d, 0, 0, 0, 1, 0, 0, 0, 0, 0 }));
        await Assert.That(session.TowerDefs.Running.Count).IsEqualTo(0);
        await Assert.That(session.UnimplementedOpcodes.ContainsKey(WzOpcodes.TowerDefQueryPlayability)).IsFalse();
    }
}
