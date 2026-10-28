using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Ai;
using AAEmu.Zone.Npcs;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Units;
using AAEmu.Zone.WorldLink;
using AAEmu.Zone.WorldState;

namespace AAEmu.Zone.Hosting;

/// <summary>
/// Independent scene composition with no engine creation, native entity admission or Lua
/// bootstrap. The existing World protocol, scene lifetime and player movement stack are reused.
/// </summary>
public static class ZoneManagedSceneHost
{
    public static Task RunAsync(ZoneSceneHost.ZoneHostInputs inputs, Stream connection, CancellationToken shutdown)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(connection);
        if (inputs.Backends != ZoneSceneBackendSelection.NativeFree)
            throw new ArgumentException("The managed host requires managed engine, managed-terrain physics and managed AI.", nameof(inputs));
        var period = inputs.Configuration.FramePeriod ?? throw new ArgumentException(
            "The managed host requires framePeriodMilliseconds.");
        if (period <= TimeSpan.Zero || period > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(inputs), "Scene frame period must be positive and at most 10 seconds.");
        inputs.Trace("Backends: engine=managed physics=managed-terrain ai=managed; no CryEngine or Lua DLL initialization.");
        ZoneSceneFrameCoordinator? frame = null;
        return ZoneSceneRunner.RunAsync(LoadScene, AdoptSession, period, shutdown);

        async Task<ZoneSceneCoordinator> LoadScene(ZoneSimulationThread owner, CancellationToken stop)
        {
            owner.VerifyAccess();
            var origin = new ZoneActorMovementWorldOrigin(inputs.Layout.Origin.X, inputs.Layout.Origin.Y);
            var players = new ZonePlayerActorFactory(inputs.Models, worldOrigin: origin);
            var notes = new ZoneSceneReadinessNotes();
            var catalog = new ZoneSceneNpcCatalog();
            var clock = new ZoneSceneScriptClock();
            ZoneManagedSceneActivation? activation = null;
            ZoneManagedAiProvider? ai = null;
            ZoneManagedPhysicsBackend? backend = null;
            ZoneSceneResourceScope? scope = null;

            Vector3 Position(ZoneUnit unit) => ZoneMovementPoseMath.ToLocalMeters(unit.Pose.Position, origin);
            ZoneActorKind Kind(ZoneActorHandle handle) => players.Find(handle) is { IsLive: true }
                ? ZoneActorKind.Player : catalog.GetKind(handle);

            ZoneSceneRegistryServices Registry(ZoneUnitRegistry registry, ZoneSceneResourceScope resources)
            {
                scope = resources;
                activation = resources.Own(new ZoneManagedSceneActivation(inputs.Layout, inputs.Assets, inputs.Trace));
                ai = resources.Own(new ZoneManagedAiProvider(Kind, _ => new ZoneManagedAiActor()));
                var world = new ZoneSceneNpcWorld(registry, inputs.Startup, inputs.Npcs, Position,
                    () => backend, catalog, ai, clock, period, notes, inputs.Trace);
                return new(world, new ZoneSceneUnitUpdateServices(registry, inputs.Startup, notes),
                    new ZoneSceneMovementServices(inputs.Startup, notes), Position);
            }

            ZoneSceneComponents Components(ZoneUnitRegistry registry, ZoneSceneResourceScope resources)
            {
                ZoneNpcSpawnerRegistry? spawners = null;
                var services = resources.Own(new ZoneSceneSpawnerServices(registry, inputs.Startup, inputs.Npcs, period,
                    Position, () => backend, () => spawners?.IsShuttingDown == true, notes, inputs.Trace));
                var initial = new ZoneNpcInitialSpawnBehavior(registry.Npcs!, inputs.Npcs, services);
                spawners = resources.Own(new ZoneNpcSpawnerRegistry(registry, inputs.Npcs, inputs.Npcs.Spawners,
                    services, services, initial));
                registry.Npcs!.AttachSpawners(spawners);
                var manager = resources.Own(new ZoneNpcManager(registry.Npcs!, services, spawners.Groups));
                var factory = new ZoneNpcSpawnerFactory(spawners, manager, services);
                var simulation = resources.CreateSimulation(registry,
                    new ZoneSceneTickServices(registry, inputs.Startup, manager, clock, period, notes));
                var loading = new ZoneManagedSceneLoadingServices(registry, inputs.Startup, activation!,
                    inputs.SceneContent, inputs.Zone.Key, notes, () =>
                    {
                        var geometry = new ZoneManagedTerrainGeometry(activation!.Heights, activation.Surfaces);
                        var surfaces = activation.SurfaceContent is { } content ? new ZoneCollisionSurfaceTable(content) : null;
                        backend = new ZoneManagedPhysicsBackend(geometry, surfaces);
                    }, inputs.Trace);
                return new(loading, factory, simulation);
            }

            var scene = ZoneSceneCoordinator.Create(connection, inputs.Startup, inputs.Stats, inputs.Npcs,
                inputs.Layout, Registry, Components, inputs.WorldContent, players);
            try
            {
                await scene.CompleteLoadingAsync(inputs.Placements, inputs.Controllers, stop);
                if (backend is null) throw new InvalidOperationException("Managed terrain loaded without publishing its physics backend.");
                var diagnostics = new ZoneMovementDiagnostics();
                var frameClock = new ZoneMovementFrameClock();
                var motion = new ZoneOwnedMovementMotion(backend, backend);
                var bindings = new ZoneMovementInputBindings(frameClock, players, motion, diagnostics);
                var movement = new ZoneMovementServices(frameClock, motion, bindings, diagnostics);
                var phases = new ZoneMovementFramePhases(scene.Registry, backend, players,
                    new ZoneMovementInputServices(scene.Registry, diagnostics, bindings),
                    new ZoneMovementCorrectionServices(scene.Registry, players, motion, frameClock, diagnostics, origin),
                    new ZoneMovementOutgoing(scene.Registry, diagnostics, localOnWire: false),
                    frameClock, diagnostics, bindings, origin);
                frame = new(phases, backend, players, ai, movement, scene.Simulation, inputs.Startup, period, inputs.Trace);
                scene.Frame = frame;
                scope!.Own(frame);
                inputs.Trace($"Zone ready: phase={scene.Phase}, engine=managed, terrainReady={activation!.Report!.TerrainReady}, cells={inputs.Layout.Cells.Count}; full feature parity=false");
                inputs.Trace($"Managed AI: {ai!.Count} live NPC generations; behavior, perception, navigation and combat remain gated.");
                inputs.Trace("Managed physics supports authored terrain and actor bounds; capsules use conservative bounds. Static meshes, vegetation, vehicles and rigid-body dynamics remain unimplemented.");
                foreach (var note in notes.Lines) inputs.Trace($"Partial leaf: {note}");
                return scene;
            }
            catch (Exception failure)
            {
                try { scene.Dispose(); }
                catch (Exception cleanup)
                { throw new AggregateException("Managed scene loading and cleanup failed.", failure, cleanup); }
                throw;
            }
        }

        ZoneSession AdoptSession(ZoneSceneCoordinator loaded) => ZoneSession.AdoptScene(loaded,
            battlefieldZone: inputs.BattlefieldZone,
            relationshipWorldFactory: join => new ZoneRelationshipScene(inputs.SceneContent, inputs.Zone.Key,
                join, (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes, inputs.WorldContent),
            achievementContent: inputs.Achievements, lootContent: inputs.Loot, frame: frame!);
    }
}
