using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Ai;
using AAEmu.Zone.Content;
using AAEmu.Zone.Navigation;
using AAEmu.Zone.Npcs;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Scripting;
using AAEmu.Zone.Units;
using AAEmu.Zone.WorldLink;
using AAEmu.Zone.WorldState;

namespace AAEmu.Zone.Hosting;

/// <summary>
/// Independent scene composition with no engine creation or native library bootstrap. Original
/// NPC source scripts use the managed VM; the World protocol and player movement stack are reused.
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
            ZoneManagedNpcRuntime? scripts = null;
            ZoneManagedNpcSceneScriptServices? npcComponents = null;
            ZoneManagedNpcPhysics? npcPhysics = null;
            ZoneManagedNpcAiPose? npcPoses = null;
            ZoneManagedNpcAdmissionQueue? admissions = null;
            ZoneNavigationTiledGraph? navigation = null;
            ZoneManagedPhysicsBackend? backend = null;
            ZoneSceneResourceScope? scope = null;

            Vector3 Position(ZoneUnit unit) => ZoneMovementPoseMath.ToLocalMeters(unit.Pose.Position, origin);
            ZoneActorKind Kind(ZoneActorHandle handle) => players.Find(handle) is { IsLive: true }
                ? ZoneActorKind.Player : catalog.GetKind(handle);

            ZoneSceneRegistryServices Registry(ZoneUnitRegistry registry, ZoneSceneResourceScope resources)
            {
                scope = resources;
                activation = resources.Own(new ZoneManagedSceneActivation(inputs.Layout, inputs.Assets, inputs.Trace));
                IZoneScriptSource source = inputs.Configuration.ScriptRoot is { } scriptRoot
                    ? new ZoneDirectoryScriptSource(scriptRoot) : new ZoneAssetScriptSource(inputs.Assets);
                var random = new Random();
                uint NextRandom() => (uint)random.NextInt64(1L << 32);
                var construction = resources.Own(new ZoneManagedNpcActorConstruction(registry, catalog, inputs.Models));
                var profiles = resources.Own(new ZoneManagedNpcProfileFactory(registry, catalog, Position, inputs.Models));
                IZoneManagedNpcScriptServices ScriptServices(IZoneLuaRuntime lua)
                {
                    ZoneManagedNpcSkillPatterns? skills = null;
                    skills = new ZoneManagedNpcSkillPatterns(lua, registry, catalog,
                        inputs.NpcSkills ?? throw new InvalidOperationException("Managed NPC admission requires loaded NPC skill content."),
                        inputs.Stats, value => registry.QueueNotification(value), NextRandom, () => clock.GameMicroseconds,
                        new ZoneManagedNpcSkillGeometry(registry, inputs.Models, unit => npcComponents!.CopyModelTransform(unit)),
                        (actor, value) => registry.QueueNotification(value, actor.Npc.Unit, skills!.GetRequestScope(actor)));
                    try
                    {
                        return npcComponents = new ZoneManagedNpcSceneScriptServices(lua,
                            inputs.Models, inputs.Assets, () => npcPhysics ?? throw new InvalidOperationException("NPC physics has not loaded."),
                            _ => throw new InvalidOperationException("The CActor construction owner must install collider dynamics before binding."),
                            Position, () => clock.GameMicroseconds,
                            () => inputs.Startup.EngineCVars.GetBool("g_spectatorcollisions", false),
                            ZoneCharacterAttachmentPublicationProfile.BoneOnly,
                            new ZoneManagedNpcGameScriptRequests(registry, catalog, value => registry.QueueNotification(value), actor =>
                            {
                                var state = actor.Npc.Unit.Casting.CopySnapshot();
                                return new(state.CastTimeline, state.CurrentSkill, state.ChannelTimeline);
                            }, queueWithActorScope: true),
                            inputs.Trace, new ZoneManagedNpcCombatScriptServices(registry, catalog, value => registry.QueueNotification(value)),
                            construction, () => npcPoses ?? throw new InvalidOperationException("NPC proxy pose owner has not loaded."), skills);
                    }
                    catch { skills.Dispose(); throw; }
                }
                scripts = resources.Own(new ZoneManagedNpcRuntime(registry, catalog, inputs.Models, inputs.Npcs,
                    source, Position, () => clock.GameMicroseconds,
                    NextRandom, () => DateTimeOffset.UtcNow, period, inputs.Trace,
                    createProfile: (handle, npc, entityId) => profiles.CreateWithConstruction(handle, npc, entityId, construction),
                    createScriptServices: ScriptServices));
                scripts.Actors.SmartObjectStateConsumer = npcComponents!.ModifySmartObjectStates;
                scripts.Actors.OperationFactory = npcComponents.CreateUseSkillGoal;
                notes.AddOnce("managed-npc-profile", "Original NPC construction and supported script services are admitted per generation, at most one initialization per frame. " +
                    "Unsupported profiles retire with a named refusal; complete perception, locomotion and combat remain gated. " +
                    ZoneManagedNpcProfileFactory.CapabilitySummary);
                ZoneSceneNpcWorld? world = null;
                ulong admittedCount = 0;
                admissions = resources.Own(new ZoneManagedNpcAdmissionQueue(scripts.CreateActor,
                    handle => catalog.Find(handle) is { } npc && catalog.GetAdmission(handle)?.Phase != ZoneSceneNpcAdmissionPhase.Refused &&
                        registry.Units.TryGetValue(npc.ObjectId, out var unit) && ReferenceEquals(unit, npc.Unit),
                    (handle, error) => { ai!.RemoveNpc(handle); world!.RecordAiAdmissionRefusal(handle, error.Message); },
                    handle =>
                    {
                        catalog.SetAdmission(handle, ZoneSceneNpcAdmissionPhase.Scheduled); admittedCount++;
                        if (admittedCount <= 5 && catalog.Find(handle) is { } npc)
                            inputs.Trace($"Managed NPC admitted unit={npc.ObjectId} template={npc.TemplateId} gen={handle.Generation}; script/constructor ready, gameplay parity=false.");
                    }));
                ai = resources.Own(new ZoneManagedAiProvider(Kind, admissions.Enqueue));
                world = new ZoneSceneNpcWorld(registry, inputs.Startup, inputs.Npcs, Position,
                    () => backend, catalog, ai, clock, period, notes, inputs.Trace);
                world.AttachAiRuntime(scripts.Actors.Actors.AiRuntime);
                return new(world, new ZoneSceneUnitUpdateServices(registry, inputs.Startup, notes),
                    new ZoneSceneMovementServices(inputs.Startup, notes), Position);
            }

            ZoneSceneComponents Components(ZoneUnitRegistry registry, ZoneSceneResourceScope resources)
            {
                ZoneNpcSpawnerRegistry? spawners = null;
                var services = resources.Own(new ZoneSceneSpawnerServices(registry, inputs.Startup, inputs.Npcs, period,
                    Position, () => backend, () => spawners?.IsShuttingDown == true, notes, inputs.Trace,
                    actorRuntime: scripts!.Actors.Actors));
                var initial = new ZoneNpcInitialSpawnBehavior(registry.Npcs!, inputs.Npcs, services);
                spawners = resources.Own(new ZoneNpcSpawnerRegistry(registry, inputs.Npcs, inputs.Npcs.Spawners,
                    services, services, initial));
                registry.Npcs!.AttachSpawners(spawners);
                var manager = resources.Own(new ZoneNpcManager(registry.Npcs!, services, spawners.Groups));
                var factory = new ZoneNpcSpawnerFactory(spawners, manager, services);
                var simulation = resources.CreateSimulation(registry,
                    new ZoneSceneTickServices(registry, inputs.Startup, manager, clock, period, notes, advanceScriptClock: false));
                var loading = new ZoneManagedSceneLoadingServices(registry, inputs.Startup, activation!,
                    inputs.SceneContent, inputs.Zone.Key, notes, () =>
                    {
                        var geometry = new ZoneManagedTerrainGeometry(activation!.Heights, activation.Surfaces);
                        var surfaces = activation.SurfaceContent is { } content ? new ZoneCollisionSurfaceTable(content) : null;
                        backend = new ZoneManagedPhysicsBackend(geometry, surfaces, activation.Collision!.Geometry);
                        var materials = activation.SurfaceContent?.Catalog ?? ZonePhysicalSurfaceCatalog.DefaultOnly;
                        npcPhysics = resources.Own(new ZoneManagedNpcPhysics(backend, inputs.Models,
                            name => (ushort)materials.Resolve(name).Id));
                        npcPoses = resources.Own(new ZoneManagedNpcAiPose(scripts!.Actors, npcPhysics));
                        scripts.Actors.ProxyPoses = npcPoses;
                        var bootstrap = scripts!.Bootstrap();
                        inputs.Trace($"Managed original NPC script bootstrap ready={bootstrap.Ready}; files={scripts.Loader.LoadedScripts.Count}" +
                            (bootstrap.Refusal is { } refusal ? $"; {refusal}" : ""));
                        navigation = resources.Own(new ZoneNavigationTiledGraph(inputs.Layout.Zone.Origin,
                            ZoneNavigationTileContent.LoadZone(inputs.Assets, inputs.Layout.World.RequestedWorldName, inputs.Layout.Zone)));
                    }, inputs.Trace, () => scripts!.ScriptingStatus, NavigationStatus);
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
                var npcPhases = scope!.Own(new ZoneManagedNpcMovementFramePhases(phases, scene.Registry, catalog,
                    scripts!.Actors, npcPhysics ?? throw new InvalidOperationException("NPC physical owner has not loaded."),
                    new ZoneMovementOutgoing(scene.Registry, diagnostics, localOnWire: false), diagnostics, origin,
                    prePhysics: sample =>
                    {
                        TraceFaults(scripts!.PrePhysics(sample, ai!.RemoveNpc));
                        //Cold Lua/model construction must not run once per queued World packet:
                        //the fair packet+tick loop would otherwise throttle the entire entry burst.
                        if (frame is { WorldPacketProcessed: false }) admissions!.Pump(1);
                    },
                    publishPose: (actor, _, _) => TraceFaults(scripts!.PublishPose(actor.Handle, ai!.RemoveNpc))));
                frame = new(npcPhases, backend, players, ai, movement, scene.Simulation, inputs.Startup, period, inputs.Trace,
                    elapsed =>
                    {
                        TraceFaults(scripts!.TickScripts(elapsed, ai!.RemoveNpc));
                    }, beginFrame: clock.Advance);
                scene.Frame = frame;
                scope!.Own(frame);
                inputs.Trace($"Zone ready: phase={scene.Phase}, engine=managed, terrainReady={activation!.Report!.TerrainReady}, cells={inputs.Layout.Cells.Count}; full feature parity=false");
                inputs.Trace(scripts!.ScriptingStatus);
                inputs.Trace(NavigationStatus());
                inputs.Trace($"Managed AI: ready={admissions!.ReadyCount}, pending={admissions.PendingCount}, refused={admissions.RefusedCount}; perception, locomotion and full combat remain gated.");
                inputs.Trace($"Managed physics supports authored terrain, {activation.Collision!.Geometry.InstanceCount} admitted static proxies and upright box/capsule/cylinder queries; " +
                    "living dimensions, queued stances, pivot, gravity and movement dynamics are authored. Suspension, vegetation, vehicles and rigid-body dynamics remain incomplete.");
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

            string NavigationStatus() => navigation is null ? "Managed navigation has not loaded." :
                $"Managed navigation: tiles={navigation.Snapshot.TileLoads.Count(tile => tile.Status == ZoneNavigationTileLoadStatus.Loaded)}/{navigation.Snapshot.TileLoads.Count}, " +
                $"nodes={navigation.Snapshot.Nodes.Count}, pairedLinks={navigation.Snapshot.AllocatedLinkPairCount}; " +
                "topology search is available; physical NPC route admission remains gated.";
            void TraceFaults(IReadOnlyList<ZoneManagedNpcScriptFault> faults)
            {
                foreach (var fault in faults)
                    inputs.Trace($"Managed NPC callback refused actor={fault.Actor} {fault.Callback}: {fault.Message}");
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
