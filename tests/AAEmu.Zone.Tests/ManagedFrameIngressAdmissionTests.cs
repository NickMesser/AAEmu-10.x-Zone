using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Threading.Channels;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Ai;
using AAEmu.Zone.Content;
using AAEmu.Zone.Hosting;
using AAEmu.Zone.Npcs;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Terrain;
using AAEmu.Zone.Units;
using AAEmu.Zone.WorldLink;

namespace AAEmu.Zone.Tests;

public sealed class ManagedFrameIngressAdmissionTests
{
    [Test]
    public async Task TcpPacketOnlyIterationKeepsIngressDirtyUntilTheLaterTickThenTheFollowingIdleTickAdmits()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var zone = new TcpClient();
        var connecting = zone.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var world = await listener.AcceptTcpClientAsync(deadline.Token);
        await connecting; listener.Stop();
        using var connection = new ReadBoundaryStream(zone.GetStream());
        var worldStream = world.GetStream();
        var synchronized = ZoneWorldClient.SynchronizeAsync(connection,
            new(1203, 1203, 129, 0, 0, 0, 0, false), TimeSpan.FromSeconds(5), deadline.Token);
        await Assert.That((await ZoneFrame.ReadAsync(worldStream, deadline.Token))!.Value.Opcode).IsEqualTo(ZwOpcodes.Join);
        foreach (var packet in new[]
        {
            new ZoneJoinResponse(0, ReadOnlyMemory<byte>.Empty, false, 0, false, false, 0, 0, ReadOnlyMemory<byte>.Empty).ToFrame(),
            new FactionSnapshot(0, []).ToFrame(), new FactionRelationSnapshot(0, []).ToFrame(), new SpawnerSnapshot(true, []).ToFrame()
        }) await worldStream.WriteAsync(packet.Encode(), deadline.Token);
        var startup = await synchronized; startup.WorldName = "main_world";
        var layout = new ManagedSceneActivationTests.Fixture((9, 14)).Layout;
        var stats = new ZoneStatContent([], [], [], [], [], [], [], [], []);
        var npcs = new ZoneNpcContent([], [], [], [], []);
        var notes = new ZoneSceneReadinessNotes();
        var catalog = new ZoneSceneNpcCatalog();
        ZoneSceneResourceScope resources = null!;
        using var scene = ZoneSceneCoordinator.Create(connection, startup, stats, npcs, layout,
            (registry, scope) =>
            {
                resources = scope;
                var ai = scope.Own(new ZoneManagedAiProvider(catalog.GetKind, _ => new ZoneManagedAiActor()));
                var npcWorld = new ZoneSceneNpcWorld(registry, startup, npcs, _ => Vector3.Zero, () => null,
                    catalog, ai, new(), TimeSpan.FromMilliseconds(20), notes, null);
                return new(npcWorld, new ZoneSceneUnitUpdateServices(registry, startup, notes),
                    new ZoneSceneMovementServices(startup, notes), _ => Vector3.Zero);
            },
            (registry, scope) =>
            {
                ZoneNpcSpawnerRegistry? spawners = null;
                var services = scope.Own(new ZoneSceneSpawnerServices(registry, startup, npcs, TimeSpan.FromMilliseconds(20),
                    _ => Vector3.Zero, () => null, () => spawners?.IsShuttingDown == true, notes, null));
                var initial = new ZoneNpcInitialSpawnBehavior(registry.Npcs!, npcs, services);
                spawners = scope.Own(new ZoneNpcSpawnerRegistry(registry, npcs, npcs.Spawners, services, services, initial));
                registry.Npcs!.AttachSpawners(spawners);
                var manager = scope.Own(new ZoneNpcManager(registry.Npcs, services, spawners.Groups));
                return new(new TransportLoading(registry), new(spawners, manager, services),
                    scope.CreateSimulation(registry, new TickServices(() => { })));
            });
        await scene.CompleteLoadingAsync(ZoneNpcSpawnerPlacementContent.Read("\n"u8), new([], [], [], [], [], [], []), deadline.Token);
        await Assert.That((await ZoneFrame.ReadAsync(worldStream, deadline.Token))!.Value.Opcode).IsEqualTo(ZwOpcodes.ZoneLoaded);
        using var frames = new ControlledFrames();
        using var fixture = new Fixture(scene.Registry, startup, scene.Simulation);
        resources.Own(fixture.Coordinator); scene.Frame = fixture.Coordinator;
        using var pending = fixture.Admissions.Enqueue(new(89401, 1));
        var session = ZoneSession.AdoptScene(scene, frame: fixture.Coordinator);
        connection.ObserveBoundaries = true;
        var receive = session.ReceiveAsync(stop.Token, frames);
        try
        {
            await connection.Boundaries.Reader.ReadAsync(deadline.Token); //Initial packet read is waiting.
            await frames.Reads.Reader.ReadAsync(deadline.Token); //No tick has been released.
            var time = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(time, 1234);
            await worldStream.WriteAsync(new ZoneFrame(WzOpcodes.WorldGameTime, time).Encode(), deadline.Token);
            //The next header read proves Receive+flush finished and a NEW loop iteration
            //started while the original tick task is still pending. No timing sleep is used.
            await connection.Boundaries.Reader.ReadAsync(deadline.Token);
            await Assert.That(startup.WorldGameTime).IsEqualTo((uint?)1234);
            await Assert.That(fixture.Observed.Count).IsEqualTo(0);
            frames.Release(1);
            await frames.Reads.Reader.ReadAsync(deadline.Token); //First tick+flush has completed.
            await Assert.That(fixture.Observed.Where(value => value.Phase == "pre").Single().WorldPacketProcessed).IsTrue();
            await Assert.That(fixture.Created.Count).IsEqualTo(0);
            await Assert.That(fixture.Admissions.PendingCount).IsEqualTo(1);
            frames.Release(2);
            await frames.Reads.Reader.ReadAsync(deadline.Token); //Following idle tick+flush completed.
            await Assert.That(fixture.Observed.Where(value => value.Phase == "pre").Select(value => value.WorldPacketProcessed).SequenceEqual([true, false])).IsTrue();
            await Assert.That(fixture.Created.Single()).IsEqualTo(new ZoneActorHandle(89401, 1));
            await Assert.That(((IZoneManagedAiActorReadiness)pending).IsReady).IsTrue();
            await Assert.That(fixture.StepCalls).IsEqualTo(2);
            world.Client.Shutdown(SocketShutdown.Send);
            await receive.WaitAsync(deadline.Token);
            await Assert.That(session.IsClosed).IsTrue();
        }
        finally
        {
            await stop.CancelAsync();
            try { await receive.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    [Test]
    public async Task PacketFramesDeferColdAdmissionsWhilePhysicsScriptsAiAndSimulationKeepTheirOrder()
    {
        using var scene = new Fixture();
        using var first = scene.Admissions.Enqueue(new(89101, 1));
        using var second = scene.Admissions.Enqueue(new(89102, 1));
        using var third = scene.Admissions.Enqueue(new(89103, 1));
        var initial = scene.CopyBody();
        await Assert.That(scene.Coordinator.WorldPacketProcessed).IsFalse();
        scene.Run(1, true); scene.Run(2, true); scene.Run(3, true);
        await Assert.That(scene.Created.Count).IsEqualTo(0);
        await Assert.That(scene.Admissions.PendingCount).IsEqualTo(3);
        await Assert.That(((IZoneManagedAiActorReadiness)first).IsReady).IsFalse();
        await Assert.That(scene.CopyBody().Position.Z).IsLessThan(initial.Position.Z);
        await Assert.That(scene.CopyBody().Velocity.Z).IsLessThan(0f);
        foreach (var frame in new uint[] { 1, 2, 3 })
        {
            var observed = scene.Observed.Where(value => value.Frame == frame).ToArray();
            await Assert.That(observed.Select(value => value.Phase).SequenceEqual(
                ["begin", "pre", "step", "post", "scripts", "ai", "simulation"])).IsTrue();
            await Assert.That(observed.All(value => value.WorldPacketProcessed)).IsTrue();
        }
        await Assert.That(scene.Coordinator.WorldPacketProcessed).IsFalse();
        //The legacy one-argument API is an idle tick, including immediately after a busy tick.
        scene.Run(4);
        await Assert.That(scene.Created.SequenceEqual(new ZoneActorHandle[] { new(89101, 1) })).IsTrue();
        await Assert.That(scene.Admissions.ReadyCount).IsEqualTo(1);
        await Assert.That(scene.Admissions.PendingCount).IsEqualTo(2);
        await Assert.That(((IZoneManagedAiActorReadiness)first).IsReady).IsTrue();
        await Assert.That(scene.Observed.Where(value => value.Frame == 4).All(value => !value.WorldPacketProcessed)).IsTrue();
        scene.Run(5, true);
        await Assert.That(scene.Created.Count).IsEqualTo(1);
        scene.Run(6, false);
        await Assert.That(scene.Created.SequenceEqual(new ZoneActorHandle[] { new(89101, 1), new(89102, 1) })).IsTrue();
        await Assert.That(scene.Admissions.PendingCount).IsEqualTo(1);
        await Assert.That(scene.StepCalls).IsEqualTo(6);
        await Assert.That(scene.Observed.Count(value => value.Phase == "scripts")).IsEqualTo(6);
        await Assert.That(scene.Coordinator.WorldPacketProcessed).IsFalse();
    }

    [Test]
    [Arguments("begin")]
    [Arguments("pre")]
    [Arguments("scripts")]
    public async Task FailedFrameClearsIngressContextBeforeTheNextIdleCall(string phase)
    {
        using var scene = new Fixture();
        using var pending = scene.Admissions.Enqueue(new(89201, 1));
        scene.FailPhase = phase;
        await Assert.That(() => scene.Run(1, true)).Throws<InvalidDataException>();
        await Assert.That(scene.Coordinator.WorldPacketProcessed).IsFalse();
        await Assert.That(scene.Created.Count).IsEqualTo(0);
        await Assert.That(scene.Admissions.PendingCount).IsEqualTo(1);
        scene.FailPhase = null;
        //Directly exercise the coordinator's finally contract; production dispatch retires
        //a failed scene rather than resuming it after an arbitrary phase exception.
        scene.Run(2);
        await Assert.That(scene.Created.Single()).IsEqualTo(new ZoneActorHandle(89201, 1));
        await Assert.That(scene.Observed.Where(value => value.Frame == 2).All(value => !value.WorldPacketProcessed)).IsTrue();
        await Assert.That(scene.Coordinator.WorldPacketProcessed).IsFalse();
    }

    [Test]
    public async Task ReentrantFrameCannotOverwriteItsOuterPacketContextOrPerformAnotherAdmission()
    {
        using var scene = new Fixture();
        using var pending = scene.Admissions.Enqueue(new(89301, 1));
        var refused = false; var keptOuterContext = false;
        scene.BeforePre = () =>
        {
            try { scene.Coordinator.RunFrame(99, worldPacketProcessed: false); }
            catch (InvalidOperationException) { refused = true; }
            keptOuterContext = scene.Coordinator.WorldPacketProcessed;
        };
        scene.Run(1, true);
        await Assert.That(refused).IsTrue(); await Assert.That(keptOuterContext).IsTrue();
        await Assert.That(scene.Created.Count).IsEqualTo(0);
        await Assert.That(scene.StepCalls).IsEqualTo(1);
        await Assert.That(scene.Observed.Count(value => value.Phase == "begin")).IsEqualTo(1);
        await Assert.That(scene.Coordinator.WorldPacketProcessed).IsFalse();
        scene.BeforePre = null; scene.Run(2);
        await Assert.That(scene.Created.Count).IsEqualTo(1);
    }

    private sealed record Observation(uint Frame, string Phase, bool WorldPacketProcessed);

    private sealed class Fixture : IDisposable
    {
        private static int _nextBody = 8_910_000;
        private readonly ZoneActorHandle _body = new((uint)Interlocked.Increment(ref _nextBody), 1);
        private readonly ZoneStartupState _startup;
        private readonly ZoneUnitRegistry _units;
        private readonly ZoneManagedPhysicsBackend _backend = new(new(new ZoneTerrainHeightField()));
        private readonly ZoneSimulationTick _simulation;
        private uint _frame;
        public ZoneSceneFrameCoordinator Coordinator { get; }
        public ZoneManagedNpcAdmissionQueue Admissions { get; }
        public List<ZoneActorHandle> Created { get; } = [];
        public List<Observation> Observed { get; } = [];
        public string? FailPhase { get; set; }
        public Action? BeforePre { get; set; }
        public int StepCalls { get; private set; }

        public Fixture(ZoneUnitRegistry? units = null, ZoneStartupState? startup = null, ZoneSimulationTick? simulation = null)
        {
            _startup = startup ?? new(localGameMilliseconds: () => 1000);
            _units = units ?? new(unitUpdateServices: new EmptyUnitUpdates());
            _startup.WorldGameClock.Receive(1000);
            _backend.AddActor(_body, new(new(new(.3f, .3f, .9f), false), 80));
            _backend.Teleport(_body, new(0, 0, 20), Quaternion.Identity);
            Admissions = new(handle => { Created.Add(handle); return new ZoneManagedAiActor(); }, _ => true,
                (_, error) => throw new InvalidOperationException("This scheduling fixture has no expected constructor refusal.", error));
            var players = new ZonePlayerActorFactory(new ZoneModelContent([]));
            var clock = new ZoneMovementFrameClock(); var diagnostics = new ZoneMovementDiagnostics();
            var motion = new ZoneOwnedMovementMotion(_backend, _backend);
            var bindings = new ZoneMovementInputBindings(clock, players, motion, diagnostics);
            var movement = new ZoneMovementServices(clock, motion, bindings, diagnostics);
            _simulation = simulation ?? new(_units, new TickServices(() => Observe("simulation")));
            Coordinator = new(new Phases(this), _backend, players, new AiPump(() => Observe("ai")),
                movement, _simulation, _startup, TimeSpan.FromMilliseconds(20), null,
                scriptTick: _ => Observe("scripts"), beginFrame: _ => Observe("begin"));
        }

        public void Run(uint frame, bool? packet = null)
        {
            _frame = frame;
            if (packet is { } value) Coordinator.RunFrame(frame, value);
            else Coordinator.RunFrame(frame);
        }
        private void Observe(string phase)
        {
            Observed.Add(new(_frame, phase, Coordinator.WorldPacketProcessed));
            if (FailPhase == phase) throw new InvalidDataException("controlled frame phase failure");
        }
        public ZonePhysicsActorStatus CopyBody() { _backend.CopyStatus(_body, out var state); return state; }
        public void Dispose()
        {
            try { Coordinator.Dispose(); }
            finally
            {
                Admissions.Dispose(); _backend.RemoveActor(_body); _simulation.Dispose();
                _units.Updates!.Dispose(); _startup.CloseClocks();
            }
        }

        private sealed class Phases(Fixture scene) : IZoneMovementFramePhases
        {
            public int PrePhase(in ZoneMovementFrame frame)
            {
                scene.Observe("pre"); scene.BeforePre?.Invoke();
                if (!scene.Coordinator.WorldPacketProcessed) scene.Admissions.Pump(1);
                return 0;
            }
            public void StepPhysics(TimeSpan elapsed)
            { scene.Observe("step"); scene._backend.Step(elapsed); scene.StepCalls++; }
            public int PostPhase(in ZoneMovementFrame frame) { scene.Observe("post"); return 0; }
        }
        private sealed class AiPump(Action record) : IZoneAiFramePump
        { public void Pump(uint timeMilliseconds) => record(); }
    }

    /// <summary>Explicit transport-fixture leaves; this test asserts no terrain/AI loading capability.</summary>
    private sealed class TransportLoading(ZoneUnitRegistry registry) : IZoneSceneLoadingServices
    {
        public ZoneUnitRegistry Registry => registry;
        public int ServerType => 3;
        public bool EnvironmentFlag150 => false;
        public ValueTask LoadLevelAsync(ZoneWorldMapLayout layout, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public void RequestStaticGimmickSpawns() { }
        public void InitializeDetailedTimeOfDay() => throw new InvalidOperationException("Server type3 omits time of day.");
        public void InitializeAreas() { }
        public void InitializeAiBounds(ZoneSpatialRegionBounds bounds) { }
        public void Dispose() { }
    }

    private sealed class ControlledFrames : IZoneFrameSource, IDisposable
    {
        private readonly Channel<uint?> _frames = Channel.CreateUnbounded<uint?>();
        public Channel<bool> Reads { get; } = Channel.CreateUnbounded<bool>();
        public ValueTask<uint?> ReadAsync(CancellationToken cancellationToken)
        { Reads.Writer.TryWrite(true); return _frames.Reader.ReadAsync(cancellationToken); }
        public void Release(uint frame) => _frames.Writer.TryWrite(frame);
        public void Dispose() => _frames.Writer.TryComplete();
    }

    /// <summary>Observes the next real TCP frame read after dispatch, without inspecting Session internals.</summary>
    private sealed class ReadBoundaryStream(Stream inner) : Stream
    {
        private readonly byte[] _header = new byte[2];
        private int _headerBytes, _remaining;
        public bool ObserveBoundaries { get; set; }
        public Channel<bool> Boundaries { get; } = Channel.CreateUnbounded<bool>();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (ObserveBoundaries && _headerBytes == 0 && _remaining == 0) Boundaries.Writer.TryWrite(true);
            var count = await inner.ReadAsync(buffer, cancellationToken);
            Consume(buffer.Span[..count]);
            return count;
        }
        private void Consume(ReadOnlySpan<byte> bytes)
        {
            while (!bytes.IsEmpty)
            {
                if (_headerBytes < 2)
                {
                    _header[_headerBytes++] = bytes[0]; bytes = bytes[1..];
                    if (_headerBytes == 2) _remaining = BinaryPrimitives.ReadUInt16LittleEndian(_header);
                }
                else
                {
                    var count = Math.Min(_remaining, bytes.Length); _remaining -= count; bytes = bytes[count..];
                    if (_remaining == 0) _headerBytes = 0;
                }
            }
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    private sealed class TickServices(Action record) : IZoneSimulationTickServices
    {
        public bool IsInitialized => true;
        public int ServerType => 3;
        public void UpdateContentReload() => record();
        public bool UpdateDetailedTimeOfDay() => throw new InvalidOperationException("Server type3 omits time of day.");
        public void UpdateTimeOfDay() => throw new InvalidOperationException("Server type3 omits time of day.");
        public void ProcessDeferredUnitRemovals() { }
        public void ExpireSkillContexts() { }
        public void UpdateNpcManager(uint frame) { }
        public void UpdateHousingManager() { }
        public void UpdateGimmicks() { }
        public void UpdateDoodads() { }
        public void UpdatePlots() { }
        public void UpdateAiDebugSessions() { }
        public void UpdateAiAggroReports() { }
        public void UpdateUnitLocationReports() { }
        public void UpdateMoleReports() { }
        public void UpdateDoodadUnitCollisions() { }
        public void UpdateHeartbeat() { }
        public void UpdateMovementBuffers() { }
        public void Dispose() { }
    }

    private sealed class EmptyUnitUpdates : IZoneUnitUpdateServices
    {
        private static InvalidOperationException Unexpected() => new("This frame scheduling fixture registers no Units.");
        public uint TimeMilliseconds => throw Unexpected();
        public ulong GetAllocationKey(ZoneUnit unit) => throw Unexpected();
        public void UpdateScaleTransition(ZoneUnit unit) => throw Unexpected();
        public bool UpdateUnitModels => throw Unexpected();
        public void UpdateUnitModel(ZoneUnit unit) => throw Unexpected();
        public void UpdateSkills(ZoneUnit unit, uint elapsedMilliseconds) => throw Unexpected();
        public void UpdateBuffs(ZoneUnit unit, uint elapsedMilliseconds) => throw Unexpected();
        public bool IsSpatiallyExcluded(ZoneUnit unit) => throw Unexpected();
        public void UpdateSpatialMembership(ZoneUnit unit) => throw Unexpected();
        public void UpdateShipMass(ZoneUnit unit) => throw Unexpected();
        public void UpdateShipSubmergeDepth(ZoneUnit unit) => throw Unexpected();
    }
}
