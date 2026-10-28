using AAEmu.Zone.Actors;
using AAEmu.Zone.Content;
using AAEmu.Zone.Native.Ai;
using AAEmu.Zone.Native.Engine;
using AAEmu.Zone.Native.Physics;
using AAEmu.Zone.Npcs;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Units;
using AAEmu.Zone.WorldLink;
using AAEmu.Zone.WorldState;

namespace AAEmu.Zone.Hosting;

/// <summary>
/// Persistent-host composition helper — Agent 5's registered file (IMPLEMENTATION_OWNERSHIP.md
/// section 6). Builds BOTH factories plus every scene-owned object for one scene generation and
/// hands the two runner callbacks (plan C2 / §1 turn C):
/// loadScene = engine → ResolveWorldLayout → coordinator Create (both factories) →
/// CompleteLoadingAsync(placements, controllers) → physics-backed frame assembly → readiness
/// announcement; adoptSession = ZoneSession.AdoptScene with the relationship factory, content
/// indexes and the frame coordinator.
///
/// Ownership order inside loadScene (ZoneSceneResourceScope LIFO, registered exactly once per
/// allocation before the next fallible step): engine → activation → spawner registry → manager →
/// (coordinator adds) spawner factory → simulation/tick → loading → frame coordinator LAST, so
/// disposal runs frame objects → loading (lua) → tick → factory → manager → spawner registry →
/// activation (RetirePhysicsWorld trace) → engine (Shutdown). ZoneSession drains both producers
/// before any of that runs (ZoneSession.cs:262-277 → EndReceiveAsync → Cleanup, which stops
/// NPC/movement producers before _resources.Dispose — ZoneSceneCoordinator.cs:302-320).
///
/// Named decisions carried here:
/// <list type="bullet">
/// <item>Player factory is constructed MANAGED-IDENTITY only
/// (<c>new ZonePlayerActorFactory(models)</c>): the native triple (entity services, physics
/// world, engine) cannot be assembled before registry construction because the engine admits
/// exactly ONE entity borrower (ZoneNativeEngine.Entities.cs:19-20) and that lease belongs to
/// ZoneSceneActivation.Create — and activation cannot Create before the coordinator exists.
/// Consequence is NAMED, never silent: native admission stages are skipped, physicalId stays 0,
/// D2/P1 dependency rows stay unresolved, and the readiness table prints
/// blocked-activation-entities-accessor as the exact unlock (a one-line entities accessor on
/// ZoneSceneActivation — Agent 1 file, orchestrator-cleared pattern like PhysicsWorld).</item>
/// <item>Physics/AI backend selection asserted by name here even after config validation —
/// scene creation must reject Managed physics (ZoneSceneBackendSelection.cs:8-11) and managed AI
/// (TODO(managed-ai), :25-27) at the point of construction.</item>
/// <item>localOnWire: true for the outgoing encoder — retail local-on-wire
/// (MovementRelay.cs:341-343, TODO(world-seed-check)); correction origin = the layout's region
/// origin (one cell = one 1024 m region, ZoneUnitSpatialIndex.cs:9).</item>
/// </list>
/// </summary>
public static class ZoneSceneHost
{
    /// <summary>Everything ZoneServerStartup hands over after content load + the single synchronization (sync-once preserved).</summary>
    public sealed record ZoneHostInputs(
        ZoneNativeEngineOptions EngineOptions,
        ZoneModelContent Models,
        ZoneModelAuxiliaryContent Auxiliary,
        ZoneWorldMapLayout Layout,
        IZoneAssetSource Assets,
        ZoneStatContent Stats,
        ZoneNpcContent Npcs,
        ZoneNpcSpawnerControllerContent Controllers,
        ZoneWorldContent WorldContent,
        ZoneAchievementContent Achievements,
        ZoneLootContent Loot,
        ZoneSceneContent SceneContent,
        ZoneContent Zone,
        bool BattlefieldZone,
        ZoneStartupState Startup,
        ZoneRuntimeConfiguration Configuration,
        ZoneSceneBackendSelection Backends,
        ZoneNpcSpawnerPlacementContent Placements,
        Action<string> Trace);

    /// <summary>Environment override for <see cref="ZoneRuntimeConfiguration.LuaLibraryPath"/>.</summary>
    public const string LuaLibraryEnvironment = "AAEMU_ZONE_LUA_LIBRARY";

    /// <summary>The Lua library: AAEMU_ZONE_LUA_LIBRARY, else the configured luaLibraryPath; no default.</summary>
    internal static string ResolveLuaLibrary(ZoneRuntimeConfiguration configuration, Func<string, string?> environment) =>
        environment(LuaLibraryEnvironment) is { Length: > 0 } configured ? Path.GetFullPath(configured)
        : configuration.LuaLibraryPath ?? throw new ArgumentException(
            $"Scripting requires a 64-bit Lua 5.1 LNUM double+int64 library: set luaLibraryPath in the zone configuration or {LuaLibraryEnvironment} (README, Requirements).");

    /// <summary>
    /// Runs one persistent scene session end-to-end on the dedicated simulation owner
    /// (ZoneSceneRunner.RunAsync): load → readiness → adopt → receive until EOF/cancellation →
    /// ordered retirement. Returns when the scene and connection have fully retired.
    /// </summary>
    public static Task RunAsync(ZoneHostInputs inputs, Stream connection, CancellationToken shutdown)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(connection);
        var trace = inputs.Trace;
        // --host requires framePeriodMilliseconds; named before any engine/network work.
        var framePeriod = inputs.Configuration.FramePeriod ?? throw new ArgumentException(
            "--host requires framePeriodMilliseconds in the runtime configuration so the scene's scheduling period is explicit.");
        // Named pre-engine validation of the script bootstrap inputs (the load path re-validates
        // through the real ctors; failing here keeps the error ahead of native startup).
        var directories = inputs.Configuration.AssetSources.Where(source => source.Kind == "directory").ToArray();
        var scriptRoot = inputs.Configuration.ScriptRoot ?? (directories.Length == 1 ? directories[0].Path
            : throw new ArgumentException($"Script bootstrap requires exactly one directory asset source when scriptRoot is absent; the configuration supplies {directories.Length}."));
        if (!Directory.Exists(scriptRoot))
            throw new DirectoryNotFoundException($"Script root '{scriptRoot}' does not exist.");
        var luaLibraryPath = ResolveLuaLibrary(inputs.Configuration, Environment.GetEnvironmentVariable);
        if (!File.Exists(luaLibraryPath))
            throw new FileNotFoundException(
                $"Lua 5.1 library '{luaLibraryPath}' does not exist (luaLibraryPath or {LuaLibraryEnvironment}).", luaLibraryPath);
        // Backend guard by name at the construction point (selection invariants, :41-47).
        // Managed physics is ADMITTED since Agent 8: ZoneOwnedPhysicsBackend is the host-owned
        // movement authority, and its ground still comes from the native terrain raycast.
        if (inputs.Backends.Ai != ZoneAiBackendKind.Native)
            throw new NotSupportedException(
                "Scene creation rejects the Managed AI backend by name: TODO(managed-ai) (ZoneSceneBackendSelection.cs:25-27).");
        trace($"Backends: physics={inputs.Backends.Physics.ToString().ToLowerInvariant()} ai={inputs.Backends.Ai.ToString().ToLowerInvariant()} (dependency-gated)");
        trace($"Script inputs: root={scriptRoot}, lua={luaLibraryPath}, framePeriod={framePeriod.TotalMilliseconds} ms");

        // Declared HERE so LoadScene (assembly) and AdoptSession (consumer) share one variable.
        ZoneSceneFrameCoordinator? frame = null;
        return ZoneSceneRunner.RunAsync(LoadScene, AdoptSession, framePeriod, shutdown);

        // ------------------------------------------------------------------
        // loadScene: runs on the owner thread (ZoneSimulationThread inside the runner).
        // ------------------------------------------------------------------
        async Task<ZoneSceneCoordinator> LoadScene(ZoneSimulationThread owner, CancellationToken stop)
        {
            owner.VerifyAccess();
            ZoneNativeEngine? engine = null;
            ZoneSceneActivation? activation = null;
            ZoneSceneResourceScope? scope = null;
            ZoneSceneReadinessNotes? notes = null;
            ZoneSceneNpcCatalog? catalog = null;
            ZoneSceneScriptClock? scriptClock = null;
            ZoneSceneNpcWorld? npcWorld = null;
            ZoneSceneSpawnerServices? spawnerServices = null;
            ZoneNpcInitialSpawnBehavior? initialSpawnBehavior = null;
            ZoneSceneTickServices? tickServices = null;
            ZoneSceneLoadingServices? loadingServices = null;
            ZoneNativeAiProvider? aiProvider = null;
            ZoneNativePhysicsBackendAdapter? adapter = null;
            Func<ZoneUnit, System.Numerics.Vector3>? modelPosition = null;

            // Managed-identity factory (class doc decision): identity/generation/spatial only;
            // native stages skip inside NativeAdmission when entities are absent
            // (ZonePlayerActorFactory.cs:254) — named in readiness, never silent.
            var playerActors = new ZonePlayerActorFactory(inputs.Models);

            // The scene's physics backend and motion authority, chosen once below (after level
            // completion) and captured lazily by the spawner services, which are constructed first.
            IZonePhysicsBackend? backend = null;
            IZonePhysicsMotion? motion = null;
            ZoneOwnedPlayerPhysics? owned = null;

            ZoneSceneRegistryServices RegistryFactory(ZoneUnitRegistry registry, ZoneSceneResourceScope resources)
            {
                engine = ZoneNativeEngine.CreateWithFramework(inputs.EngineOptions, inputs.Models, inputs.Auxiliary, trace);
                resources.Own(engine); // 1st registration → disposed LAST
                trace("ResolveWorldLayout");
                var selection = engine.ResolveWorldLayout(inputs.Layout);
                trace($"Framework-owned CSystem World selection: {selection.Layout.World.Header.Name}/{selection.Layout.Zone.Name}, Zone={selection.Layout.Zone.Id}, cells={selection.Layout.Cells.Count}.");
                activation = new ZoneSceneActivation(engine, selection, inputs.Assets, framePeriod, trace);
                resources.Own(activation); // 2nd → disposed after every frame/spawner object
                notes = new ZoneSceneReadinessNotes();
                catalog = new ZoneSceneNpcCatalog();
                scriptClock = new ZoneSceneScriptClock();
                if (inputs.Backends.Ai == ZoneAiBackendKind.Native)
                    aiProvider = new ZoneNativeAiProvider(catalog, engine); // one per scene, disposed by the frame coordinator
                modelPosition = unit => ModelPosition(unit);
                npcWorld = new ZoneSceneNpcWorld(registry, inputs.Startup, inputs.Npcs, modelPosition,
                    () => backend, catalog, aiProvider, scriptClock, framePeriod, notes, trace);
                var updates = new ZoneSceneUnitUpdateServices(registry, inputs.Startup, notes);
                var movement = new ZoneSceneMovementServices(inputs.Startup, notes);
                return new ZoneSceneRegistryServices(npcWorld, updates, movement, modelPosition);
            }

            ZoneSceneComponents SceneFactory(ZoneUnitRegistry registry, ZoneSceneResourceScope resources)
            {
                scope = resources;
                if (scriptClock is null || notes is null || activation is null || modelPosition is null)
                    throw new InvalidOperationException("Scene components require the completed registry-services composition.");
                ZoneNpcSpawnerRegistry? spawnerRegistry = null;
                spawnerServices = new ZoneSceneSpawnerServices(registry, inputs.Startup, inputs.Npcs, framePeriod,
                    modelPosition, () => backend, () => spawnerRegistry?.IsShuttingDown == true, notes, trace);
                var initialSpawn = new ZoneNpcInitialSpawnBehavior(registry.Npcs!, inputs.Npcs, spawnerServices);
                initialSpawnBehavior = initialSpawn;
                spawnerRegistry = new ZoneNpcSpawnerRegistry(registry, inputs.Npcs, inputs.Npcs.Spawners,
                    spawnerServices, spawnerServices, initialSpawn);                resources.Own(spawnerRegistry); // 3rd → disposed after manager (blueprint order :348)
                registry.Npcs!.AttachSpawners(spawnerRegistry);
                var manager = new ZoneNpcManager(registry.Npcs!, spawnerServices, spawnerRegistry.Groups);
                resources.Own(manager); // 4th → disposed before spawner registry, after factory
                var spawnerFactory = new ZoneNpcSpawnerFactory(spawnerRegistry, manager, spawnerServices);
                tickServices = new ZoneSceneTickServices(registry, inputs.Startup, manager, scriptClock, framePeriod, notes);
                var simulation = resources.CreateSimulation(registry, tickServices);
                loadingServices = new ZoneSceneLoadingServices(registry, inputs.Startup, activation, scriptRoot,
                    luaLibraryPath, inputs.SceneContent, inputs.Zone.Key, notes, trace);
                return new ZoneSceneComponents(loadingServices, spawnerFactory, simulation);
            }

            var scene = ZoneSceneCoordinator.Create(connection, inputs.Startup, inputs.Stats, inputs.Npcs,
                inputs.Layout, RegistryFactory, SceneFactory, inputs.WorldContent, playerActors);
            try
            {
                await scene.CompleteLoadingAsync(inputs.Placements, inputs.Controllers, stop).ConfigureAwait(false);
            }
            catch
            {
                // The coordinator already retires the exact scene + connection on failure
                // (:187-199); name the first failing phase before the runner's finally observes it.
                trace($"Zone load FAILED at phase {scene.Phase} — retiring the exact scene and connection (host exits nonzero).");
                throw;
            }

            // ---- physics-backed frame assembly (activation.Create ran inside LoadLevelAsync) ----
            var physics = activation.PhysicsWorld ?? throw new InvalidOperationException(
                "blocked-activation-physics-accessor: ZoneSceneActivation.Create completed but published no physics world.");
            var entities = activation.EntityServices ?? throw new InvalidOperationException(
                "blocked-activation-entities-accessor: ZoneSceneActivation.Create completed but published no entity services.");
            // The player factory was constructed before this activation could Create, so native
            // admission is armed exactly once here, with the same entity borrower and physics world
            // this activation owns (one lease per scene generation).
            //
            // D2: the physical-surface resolver is the scene's OWN authored catalog - the same
            // surfacetypes.xml the native surface manager registered from in Create() (301CB9F0).
            // Resolve() reproduces that registration's own rule: an empty or unknown name falls
            // back to the default surface, and a real result (including ID 0) is retained. When the
            // asset is absent the resolver stays null and admission still refuses by name.
            var surfaceContent = activation.SurfaceContent;
            // Agent 7: the scene's ONE neutral surface table. The owned player physics and the
            // native-AI compatibility route both resolve hit material ids through this instance, so
            // friction, footstep radii and collision flags cannot drift between the two consumers.
            ZoneCollisionSurfaceTable? collisionSurfaces = null;
            if (surfaceContent is not null)
            {
                collisionSurfaces = new ZoneCollisionSurfaceTable(surfaceContent);
                trace($"Agent 7 collision surfaces: {collisionSurfaces.Count} authored surface(s) resolvable by id " +
                    $"(default={collisionSurfaces.Default.Name}); the owned physics and the AI route share this table.");
                // The owned authority's horizontal blocking depends entirely on this query, and until
                // now nothing had ever observed it returning a hit. A sweep grid over the loaded
                // cells is the POSITIVE CONTROL: if it finds nothing anywhere, "no block observed" is
                // not evidence of open terrain - it is evidence the query is not answering, which is
                // a very different bug and must not be mistaken for one.
                TraceCollisionSweepControl(physics, inputs.Layout, collisionSurfaces, trace);
            }
            Func<string, ushort?>? surfaceResolver = surfaceContent is null ? null : name =>
            {
                var resolved = surfaceContent.Catalog.Resolve(name);
                return checked((ushort)resolved.Id);
            };
            if (surfaceResolver is not null)
                trace($"D2 physical-surface resolver: {surfaceContent!.Surfaces.Count} authored surface(s) from the scene's surfacetypes.xml (default={surfaceContent.Catalog.Resolve(null).Name}).");
            TraceTerrainGroundProbes(physics, inputs.Layout, trace);
            playerActors.AttachNativeTriple(entities, physics, engine, surfaceResolver);
            // Owned physics: the host is the movement authority, so a player may complete admission
            // without the native flow actor (counted and reported, never silently). Native backend:
            // the engine drives that body, so the flow-actor refusal still stands.
            playerActors.AllowUnboundFlowActor = inputs.Backends.Physics == ZonePhysicsBackendKind.Managed;
            trace("Player factory mode: native admission armed (entity services + physics world borrowed from this scene's activation).");
            // Physics backend selection. NativeTerrain keeps the engine as the authority (its motion
            // gates refuse, because the admitted player's body+0x47 is 0x03 and the action defers).
            // Managed selects the OWNED authority (Agent 8): the host integrates the player and
            // resolves the ground with the SAME native terrain raycast the scene already runs, so the
            // ground is real while the transform is ours.
            if (inputs.Backends.Physics == ZonePhysicsBackendKind.Managed)
            {
                // Adapter over the scene's real terrain: the owned authority borrows the engine's
                // raycast, it does not reimplement terrain.
                var ground = new ZoneNativeGroundQuery(physics);
                owned = new ZoneOwnedPlayerPhysics(ground);
                // The native adapter stays attached for SHAPE queries only (sweeps/overlaps are the
                // engine's collision and must never be approximated); the owned authority keeps the
                // actor transform and the step.
                var shapeQueries = new ZoneNativePhysicsBackendAdapter(physics, playerActors);
                var ownedBackend = new ZoneOwnedPhysicsBackend(owned, ground, shapeQueries, collisionSurfaces);
                backend = ownedBackend;
                motion = ownedBackend;
                trace("Physics backend: OWNED (Agent 8) - the host is the movement authority; the ground comes from the native terrain raycast.");
            }
            else
            {
                var nativeAdapter = new ZoneNativePhysicsBackendAdapter(physics, playerActors);
                backend = nativeAdapter;
                motion = nativeAdapter;
                trace("Physics backend: NativeTerrain adapter (the engine is the movement authority; motion gates refuse by name).");
            }
            var diagnostics = new ZoneMovementDiagnostics();
            var frameClock = new ZoneMovementFrameClock();
            var bindings = new ZoneMovementInputBindings(frameClock, playerActors, motion!, diagnostics);
            var actorMovement = new ZoneMovementServices(frameClock, motion!, bindings, diagnostics);
            var input = new ZoneMovementInputServices(scene.Registry, diagnostics, bindings);
            var origin = new ZoneActorMovementWorldOrigin(inputs.Layout.Origin.X, inputs.Layout.Origin.Y);
            // The movement chain writes POSES in the region-local frame (the correction's fixed-point
            // Local()). The owned authority and the terrain raycast are in engine WORLD coordinates,
            // so the two domains are joined HERE, once, instead of inside either one. Velocities and
            // impulses are differences and pass through untouched.
            IZonePhysicsMotion motionInWorldDomain = inputs.Backends.Physics == ZonePhysicsBackendKind.Managed
                ? new ZoneLocalToWorldMotionAdapter(motion!, inputs.Layout.Origin.X, inputs.Layout.Origin.Y,
                    actor => backend is ZoneOwnedPhysicsBackend owned && owned.TryReadHeight(actor, out var height)
                        ? height
                        : null)
                : motion!;
            if (ReferenceEquals(motionInWorldDomain, motion))
                trace($"Motion domain: as supplied (region-local poses accepted by the {inputs.Backends.Physics} backend).");
            else
                trace($"Motion domain: region-local -> world (origin {inputs.Layout.Origin.X},{inputs.Layout.Origin.Y} = " +
                    $"{inputs.Layout.Origin.X * 1024f} m, {inputs.Layout.Origin.Y * 1024f} m) before the owned backend.");
            var correction = new ZoneMovementCorrectionServices(scene.Registry, playerActors, motionInWorldDomain, frameClock,
                diagnostics, origin);
            var outgoing = new ZoneMovementOutgoing(scene.Registry, diagnostics, localOnWire: true);
            // Agent 7's compatibility route: the native AI reads the SAME neutral surface table the
            // owned player physics reads, so both agree on what the player is standing on instead of
            // resolving surfaces twice and drifting apart.
            if (aiProvider is not null)
                aiProvider.AttachCollisionSurfaces(collisionSurfaces,
                    backend is ZoneOwnedPhysicsBackend ownedBackend ? ownedBackend.GroundMaterialFor : null);
            var phases = new ZoneMovementFramePhases(scene.Registry, backend!, playerActors, input, correction,
                outgoing, frameClock, diagnostics, bindings);
            frame = new ZoneSceneFrameCoordinator(phases, backend!, playerActors, aiProvider, actorMovement,
                scene.Simulation, inputs.Startup, framePeriod, trace);
            scene.Frame = frame;
            scope.Own(frame); // LAST registration → disposed FIRST (frame/AI before activation/engine)

            PrintReadiness(scene, activation, notes, npcWorld, spawnerServices, initialSpawnBehavior, playerActors, aiProvider, trace);
            return scene;
        }

        // ------------------------------------------------------------------
        // adoptSession: the runner verifies scene identity before and after (:38-41).
        // ------------------------------------------------------------------
        ZoneSession AdoptSession(ZoneSceneCoordinator loaded)
        {
            // Relationship factory proven in-turn (ctor ZoneRelationshipScene.cs:22-28): content +
            // zone key + join + content-loaded clock + calendar offset + world content.
            // TODO(calendar-date-offset): the local-time minute offset matches the content
            // calendar convention but has not been diffed against World's own construction yet —
            // named here so a festival-window mismatch is traceable to this line.
            return ZoneSession.AdoptScene(loaded,
                battlefieldZone: inputs.BattlefieldZone,
                relationshipWorldFactory: join => new ZoneRelationshipScene(inputs.SceneContent, inputs.Zone.Key,
                    join, (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes, inputs.WorldContent),
                achievementContent: inputs.Achievements,
                lootContent: inputs.Loot,
                frame: frame!);
        }

        System.Numerics.Vector3 ModelPosition(ZoneUnit unit)
        {
            // The scene's real per-unit position source: the immutable wire pose in the WZUnitState
            // world frame (fixed 2^-54, the exact conversion of ZonePlayerActorFactory.Admit
            // :201-204). TODO(live-model-position): physics/entity pose publication into the
            // model pose has no owner while native bodies are gated — moving owners keep their
            // admitted pose for spatial/model reads, named at readiness.
            var pose = ZoneUnitPose.FromSnapshot(unit.Snapshot);
            const double fixedToWorld = 5.684341886080802e-14; // 2^-54
            return new System.Numerics.Vector3(
                (float)(pose.Position.FixedX * fixedToWorld),
                (float)(pose.Position.FixedY * fixedToWorld),
                pose.Position.Z);
        }
    }

    /// <summary>
    /// C4 readiness announcement, printed on the owner thread right after level completion and
    /// before adoption: backends + dependency tables + ZWZoneLoaded evidence + the phase=Completed
    /// ready line + the full partial-leaf table. Nothing here claims a leaf the notes did not
    /// produce; the AI bind refusals are counted per owner.
    /// </summary>
    /// <summary>
    /// One-shot terrain ground-probe matrix, run once after the level completes.
    ///
    /// Why it exists: the owned authority's ground queries missed in the host while the standalone
    /// scene checks HIT terrain, and the only difference visible in the traces was the ray's
    /// vertical extent (host spawner rays run 4096 -> -4096; the checks run +2000 -> -4000). This
    /// fires BOTH shapes plus a short one, at the same columns, inside the host scene, so the next
    /// answer is measured rather than guessed. It reports; it never changes behavior.
    /// </summary>
    private static void TraceTerrainGroundProbes(ZoneNativeTerrainPhysicsWorld physics,
        ZoneWorldMapLayout layout, Action<string> trace)
    {
        var columns = new (string Name, float X, float Y)[]
        {
            ("probe-origin(512,512)", 512f, 512f),
            ("player-spawn(9820,15234)", 9820f, 15234f),
            ("layout-origin", layout.Origin.X, layout.Origin.Y),
        };
        var shapes = new (string Name, float From, float To)[]
        {
            ("checks(+2000/-4000)", 2000f, -4000f),
            ("host(4096/-8192)", 4096f, -8192f),
            ("short(+500/-1000)", 500f, -1000f),
        };
        foreach (var column in columns)
            foreach (var shape in shapes)
            {
                var from = new System.Numerics.Vector3(column.X, column.Y, shape.From);
                var to = new System.Numerics.Vector3(column.X, column.Y, shape.To);
                var hit = physics.RaycastTerrain(from, to, out var result);
                trace($"GROUND-PROBE column={column.Name} ray={shape.Name} {(hit
                    ? $"HIT ground=({result.Position.X}, {result.Position.Y}, {result.Position.Z}) material={result.PhysicalMaterialId}"
                    : "MISS")}");
            }
    }

    /// <summary>
    /// The positive control for the owned authority's horizontal blocking: sweep a player-sized
    /// capsule over a grid of the LOADED cells and report what the engine's scene query actually
    /// answers. A hit proves the blocking path has real data to block on; a total miss means the
    /// query is not answering in this scene, and that must be reported as a defect rather than read
    /// as "the area is empty".
    /// </summary>
    private static void TraceCollisionSweepControl(ZoneNativeTerrainPhysicsWorld physics,
        ZoneWorldMapLayout layout, ZoneCollisionSurfaceTable surfaces, Action<string> trace)
    {
        var shape = new ZonePhysicsShape(new System.Numerics.Vector3(0.4f, 0.4f, 0.32f), true);
        var hits = 0;
        var probes = 0;
        var firstHit = default(ZoneNativeSceneRayHit);
        var found = false;
        var hitColumns = new List<string>();
        // The ONLY columns that can demonstrate a walk-into-a-block are ones near ground the player
        // can already stand on. The reachable standing height in this scene is ~241 m (measured), so
        // the report is narrowed to structures within 120 m of that standing point AND tall enough to
        // be a wall rather than a kerb. Everything else is a roof the player can only fall off.
        const float standX = 512f, standY = 512f, standZ = 241.7f;
        for (var x = 400f; x <= 900f; x += 10f)
            for (var y = 400f; y <= 900f; y += 10f)
            {
                // From well ABOVE the terrain and far down through it. An earlier revision of this
                // control cast from z=1 and z=20 for 2 and 21 metres - below the terrain surface
                // entirely (the scene's ground sits at Z~240-310), so it could not have found
                // anything and its "no hit" result meant nothing at all.
                const float fromZ = 500f;
                const float distance = 1000f;
                var origin = new System.Numerics.Vector3(x, y, fromZ);
                probes++;
                try
                {
                    if (!physics.RaycastStaticScene(origin,
                        new System.Numerics.Vector3(0f, 0f, -distance), out var hit)) continue;
                    hits++;
                    if (!found) { firstHit = hit; found = true; }
                    // A walk-into test needs the COLUMN and the top of what was hit, because the
                    // player has to stand BESIDE it at the SAME height, not inside it. Only heights
                    // a walking player could occupy are worth reporting - a roof 150 m up is not a
                    // wall anyone can walk into. Bounded so a dense scene cannot flood the line.
                    if (hitColumns.Count < 16
                        && hit.Position.Z is > standZ - 2f and < standZ + 12f
                        && MathF.Abs(x - standX) < 120f && MathF.Abs(y - standY) < 120f)
                        hitColumns.Add($"({x:0},{y:0}) top={hit.Position.Z:0.0} " +
                            $"surface={surfaces.Resolve(hit.PhysicalMaterialId).Name}");
                }
                catch (Exception error)
                {
                    trace($"Agent 7 collision sweep control FAILED at ({x}, {y}): {error.Message}");
                    return;
                }
            }
        if (!found)
        {
            trace($"Agent 7 collision sweep control: {probes} downward probes across the loaded cells returned NO hit. " +
                  "The owned authority's horizontal blocking therefore has no data to block on in this scene; " +
                  "this is a query/coverage gap, NOT evidence of open terrain.");
            return;
        }
        trace($"Agent 7 collision sweep control: {hits}/{probes} probes hit static geometry. " +
            $"The blocking path has real data. Hit columns (for a walk-into test): {string.Join(", ", hitColumns)}");
    }

    private static void PrintReadiness(ZoneSceneCoordinator scene, ZoneSceneActivation activation,
        ZoneSceneReadinessNotes notes, ZoneSceneNpcWorld? npcWorld, ZoneSceneSpawnerServices? spawnerServices,
        ZoneNpcInitialSpawnBehavior? initialSpawn, ZonePlayerActorFactory playerActors,
        ZoneNativeAiProvider? aiProvider, Action<string> trace)
    {
        var report = activation.Report;
        if (scene.ZoneLoadedWasSent) trace("ZWZoneLoaded sent (phase=ZoneLoadedSent)");
        foreach (var dependency in playerActors.ReportDependencies())
            trace($"Player factory dependency: {dependency.Name} resolved={dependency.IsResolved} — {dependency.Evidence}");
        if (aiProvider is not null)
        {
            var dependencies = aiProvider.ReportDependencies();
            foreach (var dependency in dependencies.Where(entry => !entry.IsResolved))
                trace($"AI dependency UNRESOLVED: {dependency.Name} — {dependency.Evidence}");
            trace($"AI dependency table: {dependencies.Count(dependency => dependency.IsResolved)}/{dependencies.Count} resolved (dependency-gated)");
            if (npcWorld is { AiBindRefusalCount: > 0 })
                trace($"NPC AI bind refusals: {npcWorld.AiBindRefusalCount} owners blocked at their named dependency message (per-owner lines at Initialize)");
        }
        else trace("AI backend: managed — TODO(managed-ai) refusal path (no provider constructed)");
        trace(playerActors.NativeTripleAttached
            ? "Player factory mode: native admission armed (entity services + physics world borrowed from this scene's activation); " +
              "D2 physical-surface resolver and P1 class publication remain the named admission gates"
            : "Player factory mode: managed-identity only — the native triple was never attached " +
              "(blocked-activation-entities-accessor: the activation published no entity services after Create)");
        if (spawnerServices is not null)
        {
            trace($"Authored spawner outcome: {spawnerServices.AdmissionRejectionCount} admission rejections, {spawnerServices.InvalidPlacementCount} invalid placements");
            if (spawnerServices.SeparationRefusalCount > 0)
                trace($"Spawn separation refusals: {spawnerServices.SeparationRefusalCount} placement(s) rejected because separation is unevaluable — {spawnerServices.FirstSeparationRefusal}");
        }
        // TODO(path-finder) is a named per-spawn refusal; report it instead of letting it retire the
        // zone. Counted here so the dependency stays visible in the readiness evidence.
        if (initialSpawn is { PathResolutionRefusalCount: > 0 })
            trace($"AI path-type refusals (TODO(path-finder)): {initialSpawn.PathResolutionRefusalCount} spawn(s) skipped — {initialSpawn.FirstPathResolutionRefusal}");
        trace($"Zone ready: phase={scene.Phase} report.Ready={report?.Ready.ToString() ?? "n/a"} refusals={report?.Refusals.Count ?? 0} partialLeaves={notes.Lines.Count}");
        foreach (var line in notes.Lines)
            trace($"Partial leaf: {line}");
        if (report is { Ready: false })
            trace($"Zone readiness WARNING: scene load report is not ready — refusals={report.Refusals.Count} (named earlier by the loading gate)");
    }
}
