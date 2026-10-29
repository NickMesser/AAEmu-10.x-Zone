using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Ai;
using AAEmu.Zone.Combat;
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
            ZoneManagedNpcActorConstruction? construction = null;
            ZoneManagedNpcGroups? npcGroups = null;
            ZoneManagedNpcPostureState? npcPostures = null;
            ZoneManagedNpcActorMotion? npcMotion = null;
            ZoneManagedNpcReplication? npcReplication = null;
            ZoneManagedNpcPositionChecks? npcPositions = null;
            ZoneManagedNpcMovementAbility? npcMovementAbility = null;
            ZoneManagedNpcMovementRequests? npcMovementRequests = null;
            ZoneManagedNpcNavigation? npcNavigation = null;
            ZoneManagedNpcPuppetOutput? npcPuppetOutput = null;
            ZoneManagedActorGrids? npcActorGrids = null;
            var aiInitialized = false;
            ZoneManagedWaterQueries? water = null;
            ZoneSceneNpcWorld? world = null;
            ZoneMovementFrame? npcFrame = null;
            ZoneManagedNpcAdmissionQueue? admissions = null;
            ZoneNavigationTiledGraph? navigation = null;
            ZoneManagedPhysicsBackend? backend = null;
            ZoneSceneResourceScope? scope = null;
            var random = new Random();
            uint NextRandom() => (uint)random.NextInt64(1L << 32);
            ZoneMovementFrame CurrentNpcFrame() => npcFrame ?? throw new InvalidOperationException("NPC movement requires the scene's actual sampled frame.");

            Vector3 Position(ZoneUnit unit) => ZoneMovementPoseMath.ToLocalMeters(unit.Pose.Position, origin);
            ZoneActorKind Kind(ZoneActorHandle handle) => players.Find(handle) is { IsLive: true }
                ? ZoneActorKind.Player : catalog.GetKind(handle);

            ZoneSceneRegistryServices Registry(ZoneUnitRegistry registry, ZoneSceneResourceScope resources)
            {
                scope = resources;
                activation = resources.Own(new ZoneManagedSceneActivation(inputs.Layout, inputs.Assets, inputs.Trace, inputs.Startup.EngineCVars));
                IZoneScriptSource source = inputs.Configuration.ScriptRoot is { } scriptRoot
                    ? new ZoneDirectoryScriptSource(scriptRoot) : new ZoneAssetScriptSource(inputs.Assets);
                construction = resources.Own(new ZoneManagedNpcActorConstruction(registry, catalog, inputs.Models));
                var profiles = resources.Own(new ZoneManagedNpcProfileFactory(registry, catalog, Position, inputs.Models));
                IZoneManagedNpcScriptServices ScriptServices(IZoneLuaRuntime lua)
                {
                    ZoneManagedNpcSkillPatterns? skills = null;
                    ZoneManagedNpcGroups? groups = null;
                    skills = new ZoneManagedNpcSkillPatterns(lua, registry, catalog,
                        inputs.NpcSkills ?? throw new InvalidOperationException("Managed NPC admission requires loaded NPC skill content."),
                        inputs.Stats, value => registry.QueueNotification(value), NextRandom, () => clock.GameMicroseconds,
                        new ZoneManagedNpcSkillGeometry(registry, inputs.Models, unit => npcComponents!.CopyModelTransform(unit)),
                        (actor, value) => registry.QueueNotification(value, actor.Npc.Unit, skills!.GetRequestScope(actor)));
                    try
                    {
                        groups = new ZoneManagedNpcGroups(lua, inputs.Npcs);
                        npcGroups = groups;
                        npcPostures = new ZoneManagedNpcPostureState(inputs.Npcs.Postures,
                            inputs.AnimationActions ?? throw new InvalidOperationException("Managed NPC posture requires loaded animation-action content."),
                            () => inputs.Startup.TimeOfDayState.Hour, value => registry.QueueNotification(value), notifications: registry);
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
                            construction, () => npcPoses ?? throw new InvalidOperationException("NPC proxy pose owner has not loaded."), skills, groups,
                            () => npcMotion ?? throw new InvalidOperationException("NPC CActor motion owner has not loaded."),
                            () => npcReplication ?? throw new InvalidOperationException("NPC replication owner has not loaded."),
                            () => npcPositions ?? throw new InvalidOperationException("NPC position-check owner has not loaded."), npcPostures, inputs.NpcSkills,
                            () => npcMovementAbility ?? throw new InvalidOperationException("NPC movement parameter owner has not loaded."),
                            () => npcMovementRequests ?? throw new InvalidOperationException("NPC movement request owner has not loaded."),
                            () => npcNavigation ?? throw new InvalidOperationException("NPC navigation owner has not loaded."),
                            () => npcPuppetOutput ?? throw new InvalidOperationException("NPC Puppet output owner has not loaded."));
                    }
                    catch { npcPostures?.Dispose(); groups?.Dispose(); skills.Dispose(); throw; }
                }
                scripts = resources.Own(new ZoneManagedNpcRuntime(registry, catalog, inputs.Models, inputs.Npcs,
                    source, Position, () => clock.GameMicroseconds,
                    NextRandom, () => DateTimeOffset.UtcNow, period, inputs.Trace,
                    createProfile: (handle, npc, entityId) => profiles.CreateWithConstruction(handle, npc, entityId, construction),
                    createScriptServices: ScriptServices));
                scripts.Actors.GameObjectActivationProfile = ZoneManagedNpcGameObjectActivationProfile.MultiplayerWorldServer;
                scripts.Actors.SmartObjectStateConsumer = npcComponents!.ModifySmartObjectStates;
                scripts.Actors.OperationFactory = npcComponents.CreateUseSkillGoal;
                notes.AddOnce("managed-npc-profile", "Original NPC construction and supported script services are admitted per generation, at most one initialization per frame. " +
                    "Unsupported profiles retire with a named refusal; complete perception, locomotion and combat remain gated. " +
                    ZoneManagedNpcProfileFactory.CapabilitySummary);
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
                ai.BeginFrame = _ => scripts.Actors.BeginAiFrame(aiInitialized, inputs.Startup.EngineCVars);
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
                    actorRuntime: scripts!.Actors.Actors, managedActorScene: () => scripts.Actors,
                    managedPostures: () => npcPostures, managedMovementFrame: CurrentNpcFrame, managedWaterQueries: () => water,
                    managedGroups: () => npcGroups));
                var initial = new ZoneNpcInitialSpawnBehavior(registry.Npcs!, inputs.Npcs, services);
                spawners = resources.Own(new ZoneNpcSpawnerRegistry(registry, inputs.Npcs, inputs.Npcs.Spawners,
                    services, services, initial));
                registry.Npcs!.AttachSpawners(spawners);
                var manager = resources.Own(new ZoneNpcManager(registry.Npcs!, services, spawners.Groups));
                var factory = new ZoneNpcSpawnerFactory(spawners, manager, services);
                var simulation = resources.CreateSimulation(registry,
                    new ZoneSceneTickServices(registry, inputs.Startup, manager, clock, period, notes, advanceScriptClock: false,
                        npcReplication: () => npcReplication));
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
                        //Fresh dedicated water state has no active environment/vortex producer.
                        //Dynamic areas and environment-driven waves remain explicit profile limits.
                        water = resources.Own(new ZoneManagedWaterQueries(activation.Heights.Nodes!.OceanWaterLevel,
                            activation.Report!.Cells, ZoneManagedWaterQueryProfile.StaticServerWithoutOceanVortex));
                        npcMotion = resources.Own(new ZoneManagedNpcActorMotion(scripts.Actors, npcPhysics, construction!,
                            water, npcComponents!.Characters, inputs.Assets, inputs.Models, activation.Heights, inputs.Startup.EngineCVars));
                        npcMovementAbility = resources.Own(new ZoneManagedNpcMovementAbility(scripts.Actors, npcMotion, inputs.Models));
                        npcMovementRequests = resources.Own(new ZoneManagedNpcMovementRequests(scripts.Actors, npcPhysics,
                            construction!, npcMotion, npcPoses, npcComponents, inputs.Stats, inputs.Startup.EngineCVars));
                        scripts.Actors.MovementAbility = npcMovementAbility;
                        npcReplication = resources.Own(new ZoneManagedNpcReplication(registry, scripts.Actors, npcMotion,
                            inputs.Startup.EngineCVars, NextRandom, localOnWire: false, inputs.Trace));
                        npcPositions = resources.Own(new ZoneManagedNpcPositionChecks(scripts.Lua, registry, npcGroups!,
                            resources.Own(new ZoneManagedNpcRegions(inputs.Layout)), new ZoneManagedNpcRelocation(npcPhysics),
                            npcPhysics, npcComponents, npcMotion, npcReplication, npcPostures!, origin, inputs.Trace));
                        world!.AttachManagedMovement(scripts.Actors, npcMotion, npcPositions, npcReplication, CurrentNpcFrame);
                        inputs.Trace($"Managed water inventory: rows={water.Report.AuthoredRows}, physicalAreas={water.Report.LoadedPhysicalAreas}, complete={water.Report.Complete}; fresh dedicated static profile.");
                        inputs.Trace(ZoneManagedNpcActorMotion.CapabilitySummary);
                        var bootstrap = scripts!.Bootstrap();
                        inputs.Trace($"Managed original NPC script bootstrap ready={bootstrap.Ready}; files={scripts.Loader.LoadedScripts.Count}" +
                            (bootstrap.Refusal is { } refusal ? $"; {refusal}" : ""));
                        navigation = resources.Own(new ZoneNavigationTiledGraph(inputs.Layout.Zone.Origin,
                            ZoneNavigationTileContent.LoadZone(inputs.Assets, inputs.Layout.World.RequestedWorldName, inputs.Layout.Zone)));
                        npcActorGrids = resources.Own(new ZoneManagedActorGrids(scripts.Actors.Objects,
                            ZoneManagedActorGridConfiguration.FromLayout(inputs.Layout)));
                        scripts.Actors.ActorGrids = npcActorGrids;
                        //Explicit bounded route/physical admission. These are replacement query
                        //work bounds, not recovered native walkability/search constants.
                        var pathQueries = new ZoneManagedNpcNavigationQueries(backend, activation.Heights, activation.Report!,
                            activation.Collision!, water, new(new(128, .25f, 1024, .75f, 2, .7f), 2, 65536), activation.CellCollisionCoverage);
                        npcNavigation = resources.Own(new ZoneManagedNpcNavigation(scripts.Actors, npcMovementAbility!,
                            npcMotion!, npcPhysics!, navigation, pathQueries, inputs.Startup.EngineCVars, new ZoneStatCalculator(inputs.Stats), npcComponents));
                        scripts.Actors.Navigation = npcNavigation;
                        npcPuppetOutput = resources.Own(new ZoneManagedNpcPuppetOutput(scripts.Actors, npcPoses!,
                            npcMovementAbility!, npcMotion!, new ZoneStatCalculator(inputs.Stats), actor => npcNavigation.Find(actor.Handle),
                            inputs.Startup.EngineCVars));
                        scripts.Actors.PuppetOutput = npcPuppetOutput;
                        scripts.Actors.MovementRequests = npcMovementRequests;
                        aiInitialized = bootstrap.Ready;
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
                        npcFrame = sample;
                        TraceFaults(scripts!.PrePhysics(sample, ai!.RemoveNpc));
                        //Cold Lua/model construction must not run once per queued World packet:
                        //the fair packet+tick loop would otherwise throttle the entire entry burst.
                        if (frame is { WorldPacketProcessed: false }) admissions!.Pump(1);
                    },
                    publishPose: (actor, body, sample) => TraceFaults(scripts!.PublishPhysicalPose(actor.Handle, body, sample, ai!.RemoveNpc)),
                    managerOwnsReplication: true,
                    prepareRequests: sample => TraceFaults(scripts!.PrepareMovementRequests(sample, ai!.RemoveNpc))));
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
                    inputs.Trace($"Managed NPC callback {(fault.RetireActor ? "refused" : "reported protected error")} actor={fault.Actor} {fault.Callback}: {fault.Message}");
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
