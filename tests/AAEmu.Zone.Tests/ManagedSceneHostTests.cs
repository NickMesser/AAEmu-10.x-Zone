using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text.Json;

using AAEmu.Zone.Content;
using AAEmu.Zone.Hosting;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Protocol.Units;
using AAEmu.Zone.Scene;
using AAEmu.Zone.WorldLink;

namespace AAEmu.Zone.Tests;

public sealed class ManagedSceneHostTests
{
    [Test]
    public async Task NativeFreeHostLoadsAdmitsMovesAndRetiresOnWorldEofWithoutEngineOrLuaPaths()
    {
        await Assert.That(CryEngineModules().Length).IsEqualTo(0);
        var fixture = new ManagedSceneActivationTests.Fixture((9, 14), (10, 15));
        fixture.Cell(9, 14, ManagedSceneActivationTests.Heightmap(24, 334));
        fixture.Cell(10, 15, ManagedSceneActivationTests.Heightmap(24, 334));
        var body = Convert.FromHexString(UnitStateSnapshotTests.PlayerEnterBody);
        var player = UnitStateSnapshot.Read(body);
        var models = new ZoneModelContent([new() { Id = player.ModelId, Subtype = "ActorModel", SubtypeId = 1 }],
            actorModels: new ZoneActorModelContent(
                [new() { Id = 1, PhysicsLivingMass = 80, PhysicsLivingGravity = 9.81f, GameJumpHeight = 1, TurnSpeed = 180,
                    PhysicsLivingMinSlideAngle = 45, PhysicsLivingMaxClimbAngle = 50, PhysicsLivingMinFallAngle = 50 }],
                [new() { Id = 1, ActorModelId = 1, StanceId = 2, MaxSpeed = 5, NormalSpeed = 5,
                    SizeX = .4f, SizeY = .4f, SizeZ = .32f, HeightCollider = .8f, HeightPivot = .15f, UseCapsule = true }]));
        // Synthetic gameplay content keeps this test focused on the real host/World/physics path.
        // Every formula referenced by the authored player fixture has an explicit constant source.
        var formulas = Enumerable.Range(1, 70).Select(kind =>
            new UnitFormulaDefinition((uint)kind, ZoneUnitKind.Character, (uint)kind, new ContentFormula("1000")));
        var buffs = new ZoneBuffContent(player.Buffs.Good.Concat(player.Buffs.Bad).Concat(player.Buffs.Hidden)
            .Select(buff => buff.TemplateId).Distinct().Select(id => new BuffStatTemplate(id, 1, 1, 1, 1, 0, false, false)),
            [], [], [], [], []);
        var stats = new ZoneStatContent(formulas, [], [], [], [], [], [], [], [], buffs: buffs);
        var configuration = ZoneRuntimeConfiguration.Read(JsonSerializer.Serialize(new
        {
            contentDatabasePath = "unused-test-content.sqlite3", logDirectory = "unused-test-logs",
            zoneName = "test", zoneKey = 129, worldName = "main_world", worldMapAsset = "game/worlds/authored/world.xml",
            worldHost = "127.0.0.1", worldPort = 1240, locale = "C", engineBackend = "managed",
            framePeriodMilliseconds = 10, assetSources = new[] { new { kind = "directory", path = "." } }
        }), Environment.CurrentDirectory);
        await Assert.That(configuration.EngineDirectory).IsNull();
        await Assert.That(configuration.LuaLibraryPath).IsNull();

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var zoneClient = new TcpClient();
        var connecting = zoneClient.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var worldClient = await listener.AcceptTcpClientAsync(deadline.Token);
        await connecting;
        listener.Stop();
        var zoneStream = zoneClient.GetStream();
        var worldStream = worldClient.GetStream();
        var join = new ZoneJoin(1203, 1203, 129, 0, 0, 0, 0, false);
        var synchronized = ZoneWorldClient.SynchronizeAsync(zoneStream, join, TimeSpan.FromSeconds(5), deadline.Token);
        var request = await ZoneFrame.ReadAsync(worldStream, deadline.Token);
        await Assert.That(request!.Value.Opcode).IsEqualTo(ZwOpcodes.Join);
        foreach (var packet in new[]
        {
            new ZoneJoinResponse(0, ReadOnlyMemory<byte>.Empty, false, 0, false, false, 0, 0, ReadOnlyMemory<byte>.Empty).ToFrame(),
            new FactionSnapshot(0, []).ToFrame(), new FactionRelationSnapshot(0, []).ToFrame(), new SpawnerSnapshot(true, []).ToFrame()
        }) await worldStream.WriteAsync(packet.Encode(), deadline.Token);
        var startup = await synchronized;
        startup.WorldName = "main_world";
        startup.WorldGameClock.Receive(1000);
        startup.EngineCVars = ZoneEngineCVars.Parse("unit_location_debug=1", new Dictionary<string, string>());
        var trace = new ConcurrentQueue<string>();
        var inputs = new ZoneSceneHost.ZoneHostInputs(null, models, null, fixture.Layout, fixture.Assets, stats,
            new ZoneNpcContent([], [], [], [], [], models: models),
            new ZoneNpcSpawnerControllerContent([], [], [], [], [], [], []),
            new ZoneWorldContent([]), new ZoneAchievementContent([], [], [], [], []), new ZoneLootContent([], [], [], [], []),
            new ZoneSceneContent([], [], [], [], []), new ZoneContent(1, 129, "test", 0, ""), false,
            startup, configuration, ZoneSceneBackendSelection.NativeFree, ZoneNpcSpawnerPlacementContent.Read("\n"u8), trace.Enqueue,
            ZoneNpcSkillContent.Empty, new ZoneAnimationActionContent([]));
        var host = ZoneSceneHost.RunAsync(inputs, zoneStream, stop.Token);
        try
        {
            await ReadUntil(worldStream, frame => frame.Opcode == ZwOpcodes.ZoneLoaded, deadline.Token);
            await worldStream.WriteAsync(new ZoneFrame(WzOpcodes.UnitState, body).Encode(), deadline.Token);
            var standing = await ReadUntil(worldStream, frame => frame.Opcode == ZwOpcodes.UnitLocation &&
                ZoneUnitLocation.Read(frame.Body.Span) is { } location && location.ObjectId == player.ObjectId &&
                Math.Abs(location.Position.Z - 366.15f) < .001f, deadline.Token);
            var initial = ZoneUnitLocation.Read(standing.Body.Span);
            await Assert.That(Math.Abs(initial.Position.X - player.Position.X)).IsLessThan(.002);
            await Assert.That(Math.Abs(initial.Position.Y - player.Position.Y)).IsLessThan(.002);

            // This synthetic world has settled the captured admission onto a different floor.
            // Send the observed support height as a client would after consuming the location.
            var settled = AAEmu.Commons.Utils.Helpers.ConvertPosition((float)initial.Position.X,
                (float)initial.Position.Y, initial.Position.Z);
            var settledZ = (uint)(settled[8] | settled[9] << 8 | (settled[10] & 0x3F) << 16);
            var moved = player.Position with { QuantizedX = player.Position.QuantizedX + 512, QuantizedZ = settledZ };
            // Wire slot 1 selects the fixture's content stance ID 2 (relaxed).
            var movement = new UnitMovement(player.ObjectId, new(1, 1500, 0, 0, 0, moved,
                new MovementVector16(2000, 0, 0), default, new UnitActorMovement(new(127, 0, 0), 1, 0, 1)));
            await worldStream.WriteAsync(new ZoneFrame(WzOpcodes.UnitMovement, movement.Write()).Encode(), deadline.Token);
            var output = await ReadUntil(worldStream, frame => frame.Opcode == ZwOpcodes.UnitMovements &&
                BinaryPrimitives.ReadInt32LittleEndian(frame.Body.Span) == 1 &&
                UnitMovement.Read(frame.Body.Span[4..]).Movement.Position.X > player.Position.X + .5, deadline.Token);
            var replicated = UnitMovement.Read(output.Body.Span[4..]);
            await Assert.That(replicated.ObjectId).IsEqualTo(player.ObjectId);
            await Assert.That(((UnitActorMovement)replicated.Movement.Details).Stance).IsEqualTo((sbyte)1);
            await Assert.That(Math.Abs(replicated.Movement.Position.Y - player.Position.Y)).IsLessThan(.01);
            await Assert.That(replicated.Movement.Position.Z).IsEqualTo(366.15f).Within(.01f);
            for (var sample = 0; sample < 2; sample++)
            {
                var previousX = replicated.Movement.Position.X;
                output = await ReadUntil(worldStream, frame => frame.Opcode == ZwOpcodes.UnitMovements &&
                    BinaryPrimitives.ReadInt32LittleEndian(frame.Body.Span) == 1 &&
                    UnitMovement.Read(frame.Body.Span[4..]).Movement.Position.X > previousX + .1, deadline.Token);
                replicated = UnitMovement.Read(output.Body.Span[4..]);
                await Assert.That(replicated.Movement.Position.Z).IsEqualTo(366.15f).Within(.01f);
            }
            await Assert.That(CryEngineModules().Length).IsEqualTo(0);

            // Preserve the read half so the host can flush and close after its incoming clean EOF.
            worldClient.Client.Shutdown(SocketShutdown.Send);
            while (await ZoneFrame.ReadAsync(worldStream, deadline.Token) is not null) { }
            await host.WaitAsync(deadline.Token);
            await Assert.That(startup.TimeOfDayState.IsDisposed).IsTrue();
            await Assert.That(startup.WorldGameClock.IsDisposed).IsTrue();
            await Assert.That(fixture.Assets.Opened).IsEqualTo(fixture.Assets.Closed);
            var lines = trace.ToArray();
            var playersRetired = Array.FindIndex(lines, line => line == "Scene player actors retired count=0");
            var terrainRetired = Array.FindIndex(lines, line => line == "Managed scene terrain retired cells=2");
            await Assert.That(playersRetired).IsGreaterThanOrEqualTo(0);
            await Assert.That(terrainRetired).IsGreaterThan(playersRetired);
            await Assert.That(lines.Any(line => line.StartsWith($"Backend registered unit={player.ObjectId} ", StringComparison.Ordinal))).IsTrue();
            await Assert.That(lines.Any(line => line.Contains("shape=Capsule", StringComparison.Ordinal) &&
                line.Contains("pivotHeight=0.15", StringComparison.Ordinal) && line.Contains("gravity=-9.81", StringComparison.Ordinal))).IsTrue();
            await Assert.That(CryEngineModules().Length).IsEqualTo(0);
        }
        finally
        {
            await stop.CancelAsync();
            try { await host.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    private static async Task<ZoneFrame> ReadUntil(Stream input, Func<ZoneFrame, bool> predicate, CancellationToken stop)
    {
        while (await ZoneFrame.ReadAsync(input, stop) is { } frame)
            if (predicate(frame)) return frame;
        throw new EndOfStreamException("The managed host closed before the expected World notification.");
    }

    private static string[] CryEngineModules()
    {
        if (!OperatingSystem.IsWindows()) return [];
        using var process = Process.GetCurrentProcess();
        return process.Modules.Cast<ProcessModule>().Select(module => module.ModuleName)
            // Windows' crypt32/cryptbase/cryptsp modules are OS cryptography, not CryEngine.
            .Where(name => (name.StartsWith("cry", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("crypt", StringComparison.OrdinalIgnoreCase)) ||
                name.StartsWith("x2game", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("xlcommon", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("tbb", StringComparison.OrdinalIgnoreCase)).ToArray();
    }
}
