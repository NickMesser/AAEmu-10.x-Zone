using System.Net.Sockets;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.WorldLink;

namespace AAEmu.Zone.Tests;

/// <summary>OPS-01 protocol-level regression: ZoneWorldClient.SynchronizeAsync against an in-process World.</summary>
public sealed class ZoneWorldClientTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static ZoneJoin JoinFor(int zoneKey = 1, uint iid = 0, ushort port = 1240) =>
        new(ZoneJoin.NativeProtocolVersion, ZoneJoin.NativeProtocolVersion, zoneKey, 0x0100007F, port, 0, iid, false);

    private static ZoneFaction Faction(uint id) =>
        new(id, id == 1 ? 0u : 1u, $"faction-{id}", 0, "", 0, 0, 1_700_000_000, id % 2 == 0, false, 0, 0, false);

    private static ZoneFactionRelation Relation(uint a, uint b) =>
        new(a, b, 1, 1, 0, 0, 0, "", 0, "");

    private static IReadOnlyList<PersistentSpawner> Spawners(int count) =>
        Enumerable.Range(1, count).Select(i => new PersistentSpawner((uint)i, (uint)(1000 + i), (byte)(i % 2))).ToList();

    private static async Task<(ZoneStartupState State, TcpClient Client)> SyncAsync(FakeWorld world, ZoneJoin join, TimeSpan? timeout = null)
    {
        var client = await world.ConnectAsync();
        try
        {
            var state = await ZoneWorldClient.SynchronizeAsync(client.GetStream(), join, timeout ?? Timeout);
            return (state, client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    [Test]
    public async Task SynchronizeReachesNativeGateWithWorldsSnapshot()
    {
        await using var world = new FakeWorld(new FakeWorldScript
        {
            Factions = [Faction(1), Faction(2), Faction(3)],
            Relations = [Relation(3, 2), Relation(1, 2)],
            Spawners = Spawners(3),
        });
        var (state, client) = await SyncAsync(world, JoinFor(zoneKey: 5));
        using var _ = client;
        try
        {
            await Assert.That(state.NativeGateOpen).IsTrue();
            await Assert.That(state.IsComplete).IsTrue();
            await Assert.That(state.JoinResponse!.Reason).IsEqualTo((ushort)0);
            await Assert.That(state.JoinResponse.HasFeature(60)).IsTrue();
            await Assert.That(state.JoinResponse.HasFeature(177)).IsTrue();
            await Assert.That(state.Factions.Keys.Order().ToArray()).IsEquivalentTo(new uint[] { 1, 2, 3 });
            await Assert.That(state.Factions[2].Name).IsEqualTo("faction-2");
            await Assert.That(state.Relations.Keys.Order().ToArray()).IsEquivalentTo(new[] { (1u, 2u), (2u, 3u) });
            await Assert.That(state.Spawners.Count).IsEqualTo(3);
            await Assert.That(state.Spawners[2]).IsEqualTo(new PersistentSpawner(3, 1003, 1));
            await Assert.That(state.JoinRequest!.Value.ZoneKey).IsEqualTo(5);
            // World sends the time seed and WorldGameTime after the gate (HandleJoin :248-258);
            // sync stops at the gate and ZoneSession consumes them from the same stream afterwards.
            await Assert.That(state.TimeOfDay).IsNull();
            for (var i = 0; i < 3; i++)
            {
                using var cts = new CancellationTokenSource(Timeout);
                state.Receive((await ZoneFrame.ReadAsync(client.GetStream(), cts.Token))!.Value);
            }
            await Assert.That(state.TimeOfDay).IsEqualTo(12.5f);
            await Assert.That(state.WorldGameTime).IsEqualTo(3600u);
            await Assert.That(world.SentOpcodes.SequenceEqual(new[]
            {
                WzOpcodes.JoinResponse, WzOpcodes.FactionList, WzOpcodes.FactionRelationList,
                WzOpcodes.SpawnerList, WzOpcodes.TimeOfDay, WzOpcodes.DetailedTimeOfDay, WzOpcodes.WorldGameTime,
            })).IsTrue();
            await Assert.That(world.Errors).IsEmpty();
        }
        finally { state.CloseClocks(); }
    }

    [Test]
    public async Task ZwJoinCarriesZoneKeyInstanceIdAndPort()
    {
        await using var worldA = new FakeWorld();
        await using var worldB = new FakeWorld();
        var (stateA, clientA) = await SyncAsync(worldA, JoinFor(zoneKey: 42, iid: 7, port: 1241));
        var (stateB, clientB) = await SyncAsync(worldB, JoinFor(zoneKey: 42, iid: 8, port: 1242));
        using var _a = clientA;
        using var _b = clientB;
        try
        {
            var joinA = worldA.Joins.Single();
            var joinB = worldB.Joins.Single();
            await Assert.That(joinA.ZoneKey).IsEqualTo(42);
            await Assert.That(joinA.InstanceId).IsEqualTo(7u);
            await Assert.That(joinA.Port).IsEqualTo((ushort)1241);
            await Assert.That(joinA.ProtocolFrom).IsEqualTo(ZoneJoin.NativeProtocolVersion);
            await Assert.That(joinB.ZoneKey).IsEqualTo(42);
            await Assert.That(joinB.InstanceId).IsEqualTo(8u);
            await Assert.That(joinB.Port).IsEqualTo((ushort)1242);
            await Assert.That(joinA).IsNotEqualTo(joinB);
            await Assert.That(stateA.JoinRequest!.Value.InstanceId).IsEqualTo(7u);
            await Assert.That(stateB.JoinRequest!.Value.InstanceId).IsEqualTo(8u);
        }
        finally { stateA.CloseClocks(); stateB.CloseClocks(); }
    }

    [Test]
    public async Task JoinRefusalSurfacesAsNamedFailure()
    {
        await using var world = new FakeWorld(new FakeWorldScript { JoinReason = 3 });
        using var client = await world.ConnectAsync();
        var ex = await Assert.That(async () =>
            await ZoneWorldClient.SynchronizeAsync(client.GetStream(), JoinFor(), Timeout)).Throws<InvalidDataException>();
        await Assert.That(ex!.Message).Contains("world server refused joining").And.Contains("reason=3");
    }

    [Test]
    public async Task WorldClosingBetweenFramesIsEndOfStreamNotAHang()
    {
        // JoinResponse, factions and relations arrive, then World drops before the spawner list.
        await using var world = new FakeWorld(new FakeWorldScript { Factions = [Faction(1)], CloseAfterFrames = 3 });
        using var client = await world.ConnectAsync();
        var ex = await Assert.That(async () =>
            await ZoneWorldClient.SynchronizeAsync(client.GetStream(), JoinFor(), Timeout)).Throws<EndOfStreamException>();
        await Assert.That(ex!.Message).Contains("World disconnected before the Zone startup snapshot completed");
    }

    [Test]
    public async Task WorldClosingMidFrameIsEndOfStreamNotAHang()
    {
        await using var world = new FakeWorld(new FakeWorldScript { CloseAfterFrames = 1, TruncateNextFrame = true });
        using var client = await world.ConnectAsync();
        await Assert.That(async () =>
            await ZoneWorldClient.SynchronizeAsync(client.GetStream(), JoinFor(), Timeout)).Throws<EndOfStreamException>();
    }

    [Test]
    public async Task WorldClosingBeforeJoinResponseIsNamedFailure()
    {
        await using var world = new FakeWorld(new FakeWorldScript { CloseAfterFrames = 0 });
        using var client = await world.ConnectAsync();
        await Assert.That(async () =>
            await ZoneWorldClient.SynchronizeAsync(client.GetStream(), JoinFor(), Timeout)).Throws<EndOfStreamException>();
    }

    [Test]
    public async Task SilentWorldTimesOutWithTimeoutException()
    {
        await using var world = new FakeWorld(new FakeWorldScript { Silent = true });
        using var client = await world.ConnectAsync();
        await Assert.That(async () =>
            await ZoneWorldClient.SynchronizeAsync(client.GetStream(), JoinFor(), TimeSpan.FromMilliseconds(300)))
            .Throws<TimeoutException>();
    }

    [Test]
    public async Task EmptyWorldListsCompleteWithEmptySnapshots()
    {
        // No factions, relations or spawners: World still sends {0,0}, {0,0} and {last=1,0} chunks.
        await using var world = new FakeWorld(new FakeWorldScript { SeedHour = null });
        var (state, client) = await SyncAsync(world, JoinFor());
        using var _ = client;
        try
        {
            await Assert.That(state.IsComplete).IsTrue();
            await Assert.That(state.Factions).IsEmpty();
            await Assert.That(state.Relations).IsEmpty();
            await Assert.That(state.Spawners).IsEmpty();
            await Assert.That(state.SpawnersComplete).IsTrue();
        }
        finally { state.CloseClocks(); }
    }

    [Test]
    public async Task MultiChunkSnapshotsAreReassembled()
    {
        // 45 factions -> 20+20+5; 450 relations -> 200+200+50; 250 spawners -> 100+100+50 (last on the final one).
        await using var world = new FakeWorld(new FakeWorldScript
        {
            Factions = Enumerable.Range(1, 45).Select(i => Faction((uint)i)).ToList(),
            Relations = Enumerable.Range(1, 450).Select(i => Relation((uint)i, (uint)i + 1000)).ToList(),
            Spawners = Spawners(250),
        });
        var (state, client) = await SyncAsync(world, JoinFor());
        using var _ = client;
        try
        {
            await Assert.That(state.Factions.Count).IsEqualTo(45);
            await Assert.That(state.Relations.Count).IsEqualTo(450);
            await Assert.That(state.Spawners.Count).IsEqualTo(250);
            await Assert.That(state.Spawners[249].Id).IsEqualTo(250u);
            var sent = world.SentOpcodes.ToArray();
            await Assert.That(sent.Count(o => o == WzOpcodes.FactionList)).IsEqualTo(3);
            await Assert.That(sent.Count(o => o == WzOpcodes.FactionRelationList)).IsEqualTo(3);
            await Assert.That(sent.Count(o => o == WzOpcodes.SpawnerList)).IsEqualTo(3);
        }
        finally { state.CloseClocks(); }
    }

    [Test]
    public async Task ExactChunkBoundaryDoesNotAddAnEmptyTrailingChunk()
    {
        // World's loops send exactly total/chunk frames when divisible (20 factions, 100 spawners).
        await using var world = new FakeWorld(new FakeWorldScript
        {
            Factions = Enumerable.Range(1, 20).Select(i => Faction((uint)i)).ToList(),
            Spawners = Spawners(100),
        });
        var (state, client) = await SyncAsync(world, JoinFor());
        using var _ = client;
        try
        {
            await Assert.That(state.Factions.Count).IsEqualTo(20);
            await Assert.That(state.Spawners.Count).IsEqualTo(100);
            await Assert.That(world.SentOpcodes.Count(o => o == WzOpcodes.FactionList)).IsEqualTo(1);
            await Assert.That(world.SentOpcodes.Count(o => o == WzOpcodes.SpawnerList)).IsEqualTo(1);
        }
        finally { state.CloseClocks(); }
    }
}
