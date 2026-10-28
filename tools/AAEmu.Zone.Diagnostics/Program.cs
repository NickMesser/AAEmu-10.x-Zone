using System.Globalization;
using System.Net.Sockets;
using System.Numerics;
using System.Runtime.ExceptionServices;

using AAEmu.Zone.Actors;
using AAEmu.Zone.Content;
using AAEmu.Zone.Hosting;
using AAEmu.Zone.Native.Engine;
using AAEmu.Zone.Native.Entities;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Scene;
using AAEmu.Zone.WorldLink;
using AAEmu.Zone.WorldState;

namespace AAEmu.Zone.Diagnostics;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help"))
        {
            Console.WriteLine("AAEmu Zone diagnostics (isolated checks and research probes; the server is AAEmu.ZoneServer)");
            Console.WriteLine("  --check-content --content-db <decrypted game.compact path> --zone <name>");
            Console.WriteLine("  --check-startup --content-db <path> --zone <name> --world-host <host> --world-port <port>");
            Console.WriteLine("  --check-framework --config <runtime.json> [--content-db <path> --zone <name>]");
            Console.WriteLine("      Isolated native framework startup/shutdown: composes the real X2 FlowNode");
            Console.WriteLine("      registration, runs PostInit, resolves the configured World and retires the engine.");
            Console.WriteLine("      It never connects to World, loads a scene or starts gameplay simulation.");
            Console.WriteLine("  --check-scene --config <runtime.json>");
            Console.WriteLine("      Isolated native scene activation on the same composed engine: loads the");
            Console.WriteLine("      configured Zone's cells through the real terrain/object composition in native");
            Console.WriteLine("      pass order, runs real ground queries, then retires in the recorded order.");
            Console.WriteLine("      It requires framePeriodMilliseconds and never connects to World. A required");
            Console.WriteLine("      pass with no replacement owner fails the check by name.");
            Console.WriteLine("  --check-managed-scene --config <runtime.json> [--collision-report <coverage.json>]");
            Console.WriteLine("      Loads authored terrain and supported static proxies without engine or Lua DLLs.");
            Console.WriteLine("      Either scene check accepts --scene-probes <rays.json> --scene-probe-output <hits.json>.");
            Console.WriteLine("      --scene-shape-probes <shapes.json> --scene-shape-probe-output <hits.json> compares explicit box/capsule/cylinder queries.");
            Console.WriteLine("      Shape target is static by default; target:terrain selects only the heightfield(original mask0x100).");
            Console.WriteLine("      Native scene check accepts --scene-living-probes <cases.json> --scene-living-probe-output <states.json> to step controlled full living bodies.");
            Console.WriteLine("  --probe-pose-write / --probe-player-admission --config <runtime.json>: native research probes.");
            Console.WriteLine("  Select exactly one mode per run.");
            Console.WriteLine("  --config <runtime.json> supplies explicit paths, asset priority, zone identity, endpoint and logs.");
            Console.WriteLine("  Use --config with one existing check mode; it does not start gameplay simulation.");
            Console.WriteLine("  --locale <native locale> selects content-filter case rules (default: C).");
            Console.WriteLine("  --spawner-file <npc_spawners.g path> also validates authored spawn placement data.");
            Console.WriteLine("  --terrain-file <heightmap.dat path> also validates a native terrain cell.");
            Console.WriteLine("  --world-file <world.xml path> --world-name <name> also validates the Zone's cell layout.");
            Console.WriteLine("Startup validation requires an isolated World. Gameplay simulation is under construction.");
            return 0;
        }

        using var shutdown = new CancellationTokenSource();
        ZoneConsoleLog? log = null;
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; shutdown.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var options = ParseArguments(args);
            ZoneRuntimeConfiguration? configuration = null;
            if (options.TryGetValue("--config", out var configurationPath))
            {
                configuration = ZoneRuntimeConfiguration.LoadFile(configurationPath);
                foreach (var key in new[] { "--content-db", "--zone", "--world-host", "--world-port", "--locale", "--world-file", "--world-name" })
                    if (options.ContainsKey(key))
                        throw new ArgumentException($"{key} cannot be combined with --config; edit the explicit configuration instead.");
                options.Add("--content-db", configuration.ContentDatabasePath);
                options.Add("--zone", configuration.ZoneName);
                options.Add("--world-host", configuration.WorldHost);
                options.Add("--world-port", configuration.WorldPort.ToString(CultureInfo.InvariantCulture));
                options.Add("--locale", configuration.Locale);
                log = new(configuration.LogDirectory);
                Console.WriteLine($"Run log: {log.Path}");
                Console.WriteLine($"Configured engine directory: {configuration.EngineDirectory} (not loaded by validation).");
                Console.WriteLine($"Configured asset sources: {string.Join(" -> ", configuration.AssetSources.Select(source => $"{source.Kind}:{source.Path}"))}");
            }
            var contentNameComparer = WorldContentNameComparer.ForLocale(options.GetValueOrDefault("--locale", "C"));
            using var content = new ZoneContentDatabase(Require(options, "--content-db"));
            var zone = configuration?.ResolveZone(content) ?? content.ResolveZone(Require(options, "--zone"));
            using var assets = configuration?.OpenAssets();
            var layout = assets?.LoadLayout(zone);
            if (options.ContainsKey("--check-managed-scene"))
                return ZoneManagedSceneCheck.Run(configuration, layout, assets,
                    options.GetValueOrDefault("--collision-report"), options.GetValueOrDefault("--scene-probes"),
                    options.GetValueOrDefault("--scene-probe-output"), options.GetValueOrDefault("--scene-shape-probes"),
                    options.GetValueOrDefault("--scene-shape-probe-output"), shutdown.Token);
            var stats = content.LoadStatContent();
            var models = content.LoadModelContent();
            if (options.ContainsKey("--check-framework"))
                return await CheckFrameworkAsync(configuration, content, models, layout, shutdown.Token).ConfigureAwait(false);
            if (options.ContainsKey("--check-scene"))
                return await CheckSceneAsync(configuration, content, models, layout, assets, shutdown.Token,
                    options.GetValueOrDefault("--scene-probes"), options.GetValueOrDefault("--scene-probe-output"),
                    options.GetValueOrDefault("--scene-shape-probes"), options.GetValueOrDefault("--scene-shape-probe-output"),
                    options.GetValueOrDefault("--scene-living-probes"), options.GetValueOrDefault("--scene-living-probe-output")).ConfigureAwait(false);
            if (options.ContainsKey("--probe-pose-write"))
                return await ProbePoseWriteAsync(configuration, content, models, layout, assets, shutdown.Token).ConfigureAwait(false);
            if (options.ContainsKey("--probe-player-admission"))
                return await ProbePlayerAdmissionAsync(configuration, content, models, layout, assets, shutdown.Token,
                    options.TryGetValue("--research-igame-slot", out var rawSlot) && int.TryParse(rawSlot, out var parsedSlot) ? parsedSlot : null,
                    options.GetValueOrDefault("--research-igame-value")).ConfigureAwait(false);
            var loaded = ZoneHostContent.Load(content, stats, models);
            loaded.Print(zone, content.IsBattlefield(zone.Key));
            var worldContent = loaded.World;
            var achievements = loaded.Achievements;
            var loot = loaded.Loot;
            if (options.TryGetValue("--spawner-file", out var spawnerFile))
                loaded.PrintPlacements(ZoneNpcSpawnerPlacementContent.LoadFile(spawnerFile));
            if (options.TryGetValue("--terrain-file", out var terrainFile))
            {
                var terrain = ZoneTerrainHeightmapContent.LoadFile(terrainFile);
                Console.WriteLine($"Terrain cell loaded: version {terrain.Header.Version}, {terrain.Nodes.Count} nodes, {terrain.LeafNodes.Count} leaf nodes, {terrain.RawSampleCount} packed samples.");
            }
            if (options.TryGetValue("--world-file", out var worldFile))
            {
                var map = ZoneWorldMapContent.LoadFile(worldFile, Require(options, "--world-name"));
                var matches = map.Zones.Where(row => row.Name == zone.Name && row.Id == zone.Key).ToArray();
                if (matches.Length != 1)
                    throw new InvalidDataException("World XML does not contain one Zone matching the configured content name and key.");
                layout = new ZoneWorldMapLayout(map, matches[0]);
            }
            if (layout is not null)
            {
                var map = layout.World;
                var authored = layout.Zone;
                Console.WriteLine($"World map loaded: {map.Header.Name}, {map.Zones.Count} Zone rows, {map.Cells.Count} world cells.");
                Console.WriteLine($"Zone cell layout: {authored.CellRows.Count} authored references, {layout.Cells.Count} unique cells, origin ({layout.Origin.X}, {layout.Origin.Y}), inclusive maximum ({authored.LastCell.X}, {authored.LastCell.Y}), {layout.Cells.Count(cell => cell.InvalidLocalX || cell.InvalidLocalY)} cells outside the native local window.");
            }
            if (options.ContainsKey("--check-content"))
                return 0;

            var host = Require(options, "--world-host");
            if (!ushort.TryParse(Require(options, "--world-port"), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port == 0)
                throw new ArgumentException("World port must be between 1 and 65535.");
            using var client = new TcpClient { NoDelay = true };
            using var connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            connectDeadline.CancelAfter(TimeSpan.FromSeconds(30));
            await client.ConnectAsync(host, port, connectDeadline.Token).ConfigureAwait(false);
            var join = new ZoneJoin(ZoneJoin.NativeProtocolVersion, ZoneJoin.NativeProtocolVersion,
                zone.Key, 0, 0, 0, 0, false);
            var state = await ZoneWorldClient.SynchronizeAsync(client.GetStream(), join,
                TimeSpan.FromSeconds(30), shutdown.Token, contentNameComparer).ConfigureAwait(false);
            var contentPolicy = new ZoneWorldContentPolicy(worldContent, state.ContentFilter!);
            var filteredStats = contentPolicy.ApplyTo(stats);
            var filteredAchievements = contentPolicy.ApplyTo(achievements);
            var filteredLoot = contentPolicy.ApplyTo(loot);
            Console.WriteLine($"Startup synchronized: {state.Factions.Count} factions, {state.Relations.Count} relations, {state.Spawners.Count} saved spawners.");
            Console.WriteLine($"World content filter loaded: {state.ContentFilter!.Categories.Count} categories.");
            Console.WriteLine($"Content policy applied: {contentPolicy.ExcludedZoneKeys.Count} excluded Zone keys, {contentPolicy.ExcludedIds(WorldContentKind.TaggedBuff).Count} tag row exclusions, {filteredStats.Appellations?.Stamps.Count ?? 0} enabled merits.");
            Console.WriteLine($"Achievement indexes rebuilt: {filteredAchievements.Achievements.Count} available achievements.");
            Console.WriteLine($"NPC loot index rebuilt: {filteredLoot.NpcPacks.Count} NPCs with available packs.");
            Console.WriteLine("Startup validation complete. Simulation readiness has not been announced.");
            return 0;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Zone failed: {ex.Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            log?.Dispose();
        }
    }

    /// <summary>
    /// The engine reports every phase as it is entered, so the last reported phase is the first
    /// failing one. Load diagnostics run on the main thread; engine phases run on the owner thread.
    /// </summary>
    private static readonly string[] FrameworkPhases =
    [
        "ValidateInputs",
        "PrepareGameServices",
        "Claim",
        "Initialize",
        "InitializeGameFramework",
        "ValidateServices (initial)",
        "PublishGameServices",
        "CompleteGameFrameworkInitialization (PostInit)",
        "ValidateServices (post-init)",
        "EngineReady",
        "ResolveWorldLayout",
        "Shutdown"
    ];

    /// <summary>
    /// One isolated native framework startup and shutdown with the complete real X2 FlowNode
    /// registration. It resolves the configured World against CSystem's own registry and retires
    /// the engine. It opens no World connection, loads no scene and starts no gameplay loop.
    /// </summary>
    private static async Task<int> CheckFrameworkAsync(ZoneRuntimeConfiguration? configuration,
        ZoneContentDatabase content, ZoneModelContent models, ZoneWorldMapLayout? layout, CancellationToken shutdown)
    {
        var inputs = ZoneEngineInputs.Create(configuration, layout, content, models, "check-framework.log");
        var options = inputs.Options;
        var entered = new List<string>();
        void Trace(string phase)
        {
            entered.Add(phase);
            Console.WriteLine($"Framework phase: {phase}");
        }
        try
        {
            await ZoneSimulationThread.RunAsync((thread, _) =>
            {
                thread.VerifyAccess();
                var engine = ZoneNativeEngine.CreateWithFramework(options, models, inputs.Auxiliary, Trace);
                Trace("ResolveWorldLayout");
                var selection = engine.ResolveWorldLayout(layout!);
                Console.WriteLine($"Framework-owned CSystem World selection: {selection.Layout.World.Header.Name}/{selection.Layout.Zone.Name}, Zone={selection.Layout.Zone.Id}, cells={selection.Layout.Cells.Count}.");
                Trace("Shutdown");
                engine.Dispose();
                return Task.CompletedTask;
            }, shutdown, "AAEmu Zone framework check").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            // The exact failure and the last entered phase are both required; never convert one.
            var failing = entered.Count != 0 ? entered[^1] : FrameworkPhases[0];
            Console.Error.WriteLine($"Framework check FAILED: first failing phase {failing}.");
            Console.Error.WriteLine(error.ToString());
            Console.Error.WriteLine($"Phases reached in order: {(entered.Count != 0 ? string.Join(" -> ", entered) : "(none)")}");
            Console.Error.WriteLine($"Framework check result: FAILED in phase {failing}.");
            return 1;
        }
        Console.WriteLine($"Phases reached in order: {string.Join(" -> ", entered)}");
        Console.WriteLine("Framework check result: PASSED (native framework Init/PostInit, World layout and shutdown).");
        return 0;
    }

    /// <summary>
    /// One isolated native scene activation on the same composed engine as --check-framework. It
    /// loads the configured Zone's six cells through the real dedicated terrain/object composition
    /// in native pass order, measures the loaded ground with real queries, and then retires every
    /// owner in the recorded order. It never connects to World and never constructs the World
    /// session coordinator: that path requires a completed World synchronization and belongs to the
    /// persistent host milestone, so this check must not attempt to satisfy it.
    /// </summary>
    private static async Task<int> CheckSceneAsync(ZoneRuntimeConfiguration? configuration,
        ZoneContentDatabase content, ZoneModelContent models, ZoneWorldMapLayout? layout,
        IZoneAssetSource? assets, CancellationToken shutdown, string? probesPath = null, string? probeOutputPath = null,
        string? shapeProbesPath = null, string? shapeProbeOutputPath = null,
        string? livingProbesPath = null, string? livingProbeOutputPath = null)
    {
        var inputs = ZoneEngineInputs.Create(configuration, layout, content, models, "check-scene.log");
        var options = inputs.Options;
        // An explicit replacement frame period is a required scene input; it is not inferred.
        var framePeriod = configuration!.FramePeriod ?? throw new ArgumentException(
            "--check-scene requires framePeriodMilliseconds so the scene's scheduling period is explicit.");
        if (assets is null)
            throw new ArgumentException("--check-scene requires --config so the configured asset sources are open.");
        var entered = new List<string>();
        var failedAt = -1;
        Exception? failure = null;
        // Captured outside the scene's lifetime so the success line can name what still refuses.
        var refusedPhases = new List<string>();
        void Trace(string phase)
        {
            entered.Add(phase);
            Console.WriteLine($"Scene phase: {phase}");
        }
        try
        {
            await ZoneSimulationThread.RunAsync((thread, _) =>
            {
                thread.VerifyAccess();
                ZoneNativeEngine? engine = null;
                ZoneSceneActivation? scene = null;
                try
                {
                    engine = ZoneNativeEngine.CreateWithFramework(options, models, inputs.Auxiliary, Trace);
                    Trace("ResolveWorldLayout");
                    var selection = engine.ResolveWorldLayout(layout!);
                    Console.WriteLine($"Framework-owned CSystem World selection: {selection.Layout.World.Header.Name}/{selection.Layout.Zone.Name}, Zone={selection.Layout.Zone.Id}, cells={selection.Layout.Cells.Count}.");
                    scene = new ZoneSceneActivation(engine, selection, assets, framePeriod, Trace);
                    scene.Create();
                    Console.WriteLine($"Scene services: terrain pool={scene.Settings!.TexturePoolSize}, sync-load={scene.Settings.TextureSyncLoad}, vegetation groups={scene.VegetationGroupCount}, frame period={scene.FramePeriod.TotalMilliseconds} ms.");
                    // Load publishes its per-cell report before a refused pass is rethrown, so the
                    // connected passes and the exact refusal are both visible in one run.
                    try { scene.Load(); }
                    finally
                    {
                        if (scene.Report is { } loaded)
                        {
                            refusedPhases.AddRange(loaded.Refusals.Select(refusal => refusal.Phase.ToString()));
                            PrintSceneLoadReport(loaded);
                        }
                    }
                }
                catch (Exception error)
                {
                    failure = error;
                    failedAt = entered.Count;
                }
                if (scene is not null)
                {
                    // The ordered completion, measurement and retirement phases still run after a
                    // refused pass: they are what proves the loaded roads, water, objects, terrain
                    // replicas and the composed engine retire cleanly. They never turn the refusal
                    // into a success, and the first failure above stays the reported one.
                    TryScenePhase(() => scene.CompletePendingStaticWork(), "CompletePendingStaticPhysics",
                        completed => Console.WriteLine($"Native queued static construction completed: {completed} requests."));
                    TryScenePhase(() => scene.ScheduleNodeReads(), "ScheduleNodeReads", PrintNodeReads);
                    TryScenePhase(() => scene.QueryGround(GroundQueryPoints), "GroundQuery", PrintGroundSamples);
                    if (probesPath is not null)
                        TrySceneStep("StaticCollisionProbes", () => ZoneSceneCollisionProbes.Run(probesPath, probeOutputPath, "native",
                            (Vector3 origin, Vector3 displacement, out Physics.ZonePhysicsHit hit) =>
                            {
                                var found = scene.PhysicsWorld!.RaycastStaticScene(origin, displacement, out var value);
                                hit = new(value.Distance, value.Position, value.Normal, value.PhysicalMaterialId);
                                return found;
                            }));
                    if (shapeProbesPath is not null)
                        TrySceneStep("StaticShapeProbes", () => ZoneSceneShapeProbes.RunNative(
                            shapeProbesPath, shapeProbeOutputPath, scene.PhysicsWorld!));
                    if (livingProbesPath is not null)
                        TrySceneStep("LivingRecoveryProbes", () => ZoneLivingRecoveryProbes.Run(
                            livingProbesPath, livingProbeOutputPath, scene.PhysicsWorld!, shutdown));
                    TrySceneStep("RetireScene", () => scene.Dispose());
                }
                if (engine is not null)
                    TrySceneStep("Shutdown", () => engine.Dispose());
                if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
                return Task.CompletedTask;

                void TryScenePhase<T>(Func<T> phase, string name, Action<T> report)
                {
                    try
                    {
                        Trace(name);
                        report(phase());
                    }
                    catch (Exception error) { Record(name, error); }
                }

                void TrySceneStep(string name, Action phase)
                {
                    try
                    {
                        Trace(name);
                        phase();
                    }
                    catch (Exception error) { Record(name, error); }
                }

                void Record(string name, Exception error)
                {
                    if (failure is null) { failure = error; failedAt = entered.Count; }
                    Console.Error.WriteLine($"Scene phase {name} failed: {error.Message}");
                }
            }, shutdown, "AAEmu Zone scene check").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            // The exact failure and the first failing phase are both required; never convert one.
            failure ??= error;
            var failing = failedAt > 0 ? entered[failedAt - 1] : entered.Count != 0 ? entered[^1] : ScenePhases[0];
            Console.Error.WriteLine($"Scene check FAILED: first failing phase {failing}.");
            Console.Error.WriteLine(failure.ToString());
            Console.Error.WriteLine($"Phases reached in order: {DescribePhases(entered, failedAt)}");
            Console.Error.WriteLine($"Scene check result: FAILED in phase {failing}.");
            return 1;
        }
        Console.WriteLine($"Phases reached in order: {string.Join(" -> ", entered)}");
        // Exit 0 means the load, ground query and ordered retirement all completed. It does NOT mean
        // the scene is ready: a refused pass keeps ZoneSceneLoadReport.Ready false, and naming the
        // refusals here stops "PASSED" from being read as "ready".
        Console.WriteLine(refusedPhases.Count == 0
            ? "Scene check result: PASSED (six-cell load, real ground query and ordered retirement; every required pass loaded)."
            : $"Scene check result: PASSED (six-cell load, real ground query and ordered retirement) with " +
              $"{refusedPhases.Count} refused pass(es) - NOT ready: {string.Join(", ", refusedPhases.Distinct())}.");
        return 0;
    }

    /// <summary>
    /// Legacy pose-write probe. Its former position-labelled velocity action has been removed;
    /// ZonePoseWriteProbe now reports the named missing pose ABI without submitting that action.
    /// </summary>
    private static async Task<int> ProbePoseWriteAsync(ZoneRuntimeConfiguration? configuration,
        ZoneContentDatabase content, ZoneModelContent models, ZoneWorldMapLayout? layout,
        IZoneAssetSource? assets, CancellationToken shutdown)
    {
        var inputs = ZoneEngineInputs.Create(configuration, layout, content, models, "probe-pose-write.log");
        var framePeriod = configuration!.FramePeriod ?? throw new ArgumentException(
            "--probe-pose-write requires framePeriodMilliseconds so the scene's scheduling period is explicit.");
        if (assets is null)
            throw new ArgumentException("--probe-pose-write requires --config so the configured asset sources are open.");
        var actorModel = models.Models.FirstOrDefault(pair => pair.Value.Kind == ZoneModelKind.Actor).Key is var modelId &&
            modelId != 0
            ? models.ActorForModel(modelId) ?? throw new InvalidOperationException($"model {modelId} resolves to no actor_models row")
            : throw new InvalidOperationException("The content export contains no actor model for the probe request.");
        var request = ZoneActorPhysicalizationRequest.BuildInitial(actorModel, 1f, nativeEntity70: false, _ => null);
        var hits = new List<ZonePoseWriteProbe.Hit>();
        var failure = false;
        await ZoneSimulationThread.RunAsync((thread, _) =>
        {
            thread.VerifyAccess();
            ZoneNativeEngine? engine = null;
            ZoneSceneActivation? scene = null;
            try
            {
                engine = ZoneNativeEngine.CreateWithFramework(inputs.Options, models, inputs.Auxiliary,
                    phase => Console.WriteLine($"Scene phase: {phase}"));
                var selection = engine.ResolveWorldLayout(layout!);
                Console.WriteLine($"Framework-owned CSystem World selection: {selection.Layout.World.Header.Name}/{selection.Layout.Zone.Name}, Zone={selection.Layout.Zone.Id}, cells={selection.Layout.Cells.Count}.");
                scene = new ZoneSceneActivation(engine, selection, assets, framePeriod, phase => Console.WriteLine($"Scene phase: {phase}"));
                scene.Create();
                scene.Load();
                if (scene.Report is { } loaded) PrintSceneLoadReport(loaded);
                // The static completion is the same phase --check-scene runs before any ground
                // query: without it the terrain collision is not installed and bodies fall through.
                try
                {
                    var completed = scene.CompletePendingStaticWork();
                    Console.WriteLine($"Native queued static construction completed: {completed} requests.");
                }
                catch (Exception error) { Console.Error.WriteLine($"Pose-write probe: static completion refused: {error.Message}"); }
                var world = scene.PhysicsWorld ?? throw new InvalidOperationException(
                    "blocked-pose-write-probe: the scene published no physics world after Create.");
                var spawn = new Vector3(512f, 512f, 0f);
                var groundZ = 0f;
                if (world.RaycastTerrain(new Vector3(spawn.X, spawn.Y, spawn.Z + 2000f), new Vector3(0, 0, -4000f), out var ground))
                {
                    groundZ = ground.Position.Z;
                    spawn = new Vector3(spawn.X, spawn.Y, groundZ + 5f);
                }
                else
                    Console.WriteLine("Pose-write probe: no terrain hit under the spawn column; the authored default height is used and logged.");
                ZonePoseWriteProbe.EvidencePath = Path.Combine(configuration.LogDirectory.Replace('/', '\\'),
                    "pose-write-probe-types.log");
                try { File.Delete(ZonePoseWriteProbe.EvidencePath); } catch { /* first run */ }
                hits.AddRange(ZonePoseWriteProbe.Run(world, scene.EntityServices, actorModel, request,
                    line => Console.WriteLine(line), spawn, groundZ));
            }
            catch (Exception error)
            {
                failure = true;
                Console.Error.WriteLine($"Pose-write probe failed: {error}");
            }
            finally
            {
                if (scene is not null) { try { scene.Dispose(); } catch (Exception error) { Console.Error.WriteLine($"Scene retirement: {error.Message}"); } }
                if (engine is not null) { try { engine.Dispose(); } catch (Exception error) { Console.Error.WriteLine($"Engine shutdown: {error.Message}"); } }
            }
            return Task.CompletedTask;
        }, shutdown, "AAEmu Zone pose-write probe").ConfigureAwait(false);
        Console.WriteLine($"Pose-write probe result: {hits.Count} observed type(s) moved the body.");
        foreach (var hit in hits) Console.WriteLine($"  HIT {hit.Detail}");
        if (hits.Count == 0) Console.WriteLine("Pose-write probe result: blocked-pose-write (no registered type produced an observed read-back change).");
        return failure ? 1 : 0;
    }

    /// <summary>
    /// Runs the real native character-spawn sequence (spawn -> load-character -> initialize) in a
    /// DISPOSABLE process and records the step evidence. The Agent 6 client login proved this
    /// sequence reaches the IGame+0xE8 callback, whose managed placeholder FailFasts the process -
    /// so the step that actually enters native code is the one whose line was written last. This
    /// probe runs OUTSIDE the host precisely because the host must not be the process that dies.
    /// </summary>
    private static async Task<int> ProbePlayerAdmissionAsync(ZoneRuntimeConfiguration? configuration,
        ZoneContentDatabase content, ZoneModelContent models, ZoneWorldMapLayout? layout,
        IZoneAssetSource? assets, CancellationToken shutdown, int? optionsResearchSlot, string? optionsResearchValue)
    {
        var inputs = ZoneEngineInputs.Create(configuration, layout, content, models, "probe-player-admission.log");
        var framePeriod = configuration!.FramePeriod ?? throw new ArgumentException(
            "--probe-player-admission requires framePeriodMilliseconds so the scene's scheduling period is explicit.");
        if (assets is null)
            throw new ArgumentException("--probe-player-admission requires --config so the configured asset sources are open.");
        var actorModel = models.Models.FirstOrDefault(pair => pair.Value.Kind == ZoneModelKind.Actor).Key is var modelId &&
            modelId != 0
            ? models.ActorForModel(modelId) ?? throw new InvalidOperationException($"model {modelId} resolves to no actor_models row")
            : throw new InvalidOperationException("The content export contains no actor model for the probe request.");
        var evidence = Path.Combine(configuration.LogDirectory.Replace('/', '\\'), "player-admission-steps.log");
        ZoneNativeEntityServices.StepEvidencePath = evidence;
        // Probe-only: answer one fatal IGame slot with a string so its contract can be observed
        // instead of guessed (--research-igame-slot 0xE8 --research-igame-value <text>).
        if (optionsResearchSlot is int researchSlot)
        {
            ZoneNativeEngine.ResearchStringSlot = researchSlot;
            ZoneNativeEngine.ResearchStringValue = optionsResearchValue;
            Console.WriteLine($"Player admission probe: IGame +0x{researchSlot:X} answers {(optionsResearchValue ?? string.Empty).Length} chars (research only).");
        }
        Console.WriteLine($"Player admission probe: step evidence -> {evidence}");
        try { File.Delete(evidence); } catch { /* first run */ }
        var failure = false;
        await ZoneSimulationThread.RunAsync((thread, _) =>
        {
            thread.VerifyAccess();
            ZoneNativeEngine? engine = null;
            ZoneSceneActivation? scene = null;
            try
            {
                engine = ZoneNativeEngine.CreateWithFramework(inputs.Options, models, inputs.Auxiliary,
                    phase => Console.WriteLine($"Scene phase: {phase}"));
                var selection = engine.ResolveWorldLayout(layout!);
                Console.WriteLine($"Player admission probe: zone={selection.Layout.Zone.Id} cells={selection.Layout.Cells.Count}.");
                scene = new ZoneSceneActivation(engine, selection, assets, framePeriod, phase => Console.WriteLine($"Scene phase: {phase}"));
                scene.Create();
                scene.Load();
                if (scene.Report is { } loaded) PrintSceneLoadReport(loaded);
                try { scene.CompletePendingStaticWork(); } catch (Exception error) { Console.Error.WriteLine($"Player admission probe: static completion refused: {error.Message}"); }
                var services = scene.EntityServices ?? throw new InvalidOperationException(
                    "blocked-player-admission-probe: the scene published no entity services after Create.");
                var spawn = new Vector3(512f, 512f, 0f);
                var groundZ = 0f;
                if (scene.PhysicsWorld is { } physics &&
                    physics.RaycastTerrain(new Vector3(spawn.X, spawn.Y, spawn.Z + 2000f), new Vector3(0, 0, -4000f), out var ground))
                {
                    groundZ = ground.Position.Z;
                    spawn = new Vector3(spawn.X, spawn.Y, groundZ + 5f);
                }
                var entity = services.CreateInitialCharacter(actorModel, "admission-probe", spawn,
                    Quaternion.Identity, 1f);
                Console.WriteLine($"Player admission probe: character created id={entity.Id} pointer=0x{entity.Pointer:X}.");
            }
            catch (Exception error)
            {
                failure = true;
                Console.Error.WriteLine($"Player admission probe refused: {error.GetType().Name}: {error.Message}");
            }
            finally
            {
                if (scene is not null) { try { scene.Dispose(); } catch (Exception error) { Console.Error.WriteLine($"Scene retirement: {error.Message}"); } }
                if (engine is not null) { try { engine.Dispose(); } catch (Exception error) { Console.Error.WriteLine($"Engine shutdown: {error.Message}"); } }
            }
            return Task.CompletedTask;
        }, shutdown, "AAEmu Zone player admission probe").ConfigureAwait(false);
        Console.WriteLine("Player admission probe result: the native spawn sequence completed without reaching a fatal callback.");
        return failure ? 1 : 0;
    }

    /// <summary>Report, not claim: the scene is ready only when every cell and required pass loaded.</summary>
    private static void PrintSceneLoadReport(ZoneSceneLoadReport report)
    {
        Console.WriteLine($"Scene load report: terrain={report.TerrainLoadedCells}/{report.Cells.Count} cells, objects={report.ObjectLoadedCells}/{report.Cells.Count} cells, ready={report.Ready}, refusals={report.Refusals.Count}.");
        foreach (var cell in report.Cells)
        {
            var coordinate = cell.Placement.Cell.Coordinate;
            Console.WriteLine($"Scene cell ({coordinate.X},{coordinate.Y}) engine=({cell.Placement.EngineCell.X},{cell.Placement.EngineCell.Y}): terrain={cell.Terrain.Status}, objects={cell.ObjectsLoaded}, cover={cell.Cover?.Succeeded.ToString() ?? "none"}");
            foreach (var pass in cell.Passes) Console.WriteLine($"  {pass.Pass}: {pass.Detail}");
            if (cell.Refusal is not null) Console.WriteLine($"  REFUSED {cell.Refusal.Phase}: {cell.Refusal.Message}");
        }
    }

    private static void PrintNodeReads(IReadOnlyList<ZoneSceneNodeReadReport> reads)
    {
        foreach (var read in reads)
            Console.WriteLine($"Scheduled node read cell ({read.EngineCell.X},{read.EngineCell.Y}): started={read.Started}, state={read.State}, baseHandle={read.BaseHandle}{(read.Diagnostic is null ? "" : $", diagnostic={read.Diagnostic}")}.");
    }

    private static void PrintGroundSamples(IReadOnlyList<ZoneSceneGroundSample> samples)
    {
        foreach (var sample in samples)
        {
            var native = sample.NativeHit is { } hit
                ? $"distance={hit.Distance}, position=({hit.Position.X},{hit.Position.Y},{hit.Position.Z}), material={hit.PhysicalMaterialId}, localSurface={hit.LocalSurfaceIndex}"
                : "miss";
            Console.WriteLine($"Ground query engine cell ({sample.EngineCell.X},{sample.EngineCell.Y}) at ({sample.Point.X},{sample.Point.Y}): managedHeight={sample.ManagedHeight}, native {native}.");
        }
    }

    /// <summary>Marks the first post-failure phase so a continued check still names its first failure.</summary>
    private static string DescribePhases(List<string> phases, int failedAt) => phases.Count == 0
        ? "(none)"
        : failedAt > 0 && failedAt < phases.Count
            ? $"{string.Join(" -> ", phases.Take(failedAt))} -> [FAILED] -> {string.Join(" -> ", phases.Skip(failedAt))}"
            : string.Join(" -> ", phases);

    /// <summary>Engine-local points inside the configured six-cell window: (9,14) and (11,14).</summary>
    private static readonly Vector3[] GroundQueryPoints = [new(512, 512, 0), new(2560, 512, 0)];

    /// <summary>Scene phases after the engine's own ordered startup trace.</summary>
    private static readonly string[] ScenePhases =
    [
        "ValidateInputs", "PrepareGameServices", "Claim", "Initialize", "InitializeGameFramework",
        "ValidateServices (initial)", "PublishGameServices", "CompleteGameFrameworkInitialization (PostInit)",
        "ValidateServices (post-init)", "EngineReady", "ResolveWorldLayout", "CreateSceneServices",
        "LoadZoneTerrain", "LoadZoneObjects", "CompletePendingStaticPhysics", "ScheduleNodeReads",
        "GroundQuery", "RetireScene", "Shutdown"
    ];

    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (key is "--check-content" or "--check-startup" or "--check-framework" or "--check-scene" or "--check-managed-scene" or "--probe-pose-write" or "--probe-player-admission")
            {
                if (!options.TryAdd(key, ""))
                    throw new ArgumentException($"Duplicate option {key}.");
                continue;
            }
            if (key is not ("--config" or "--content-db" or "--zone" or "--world-host" or "--world-port" or "--locale" or "--spawner-file" or "--terrain-file" or "--world-file" or "--world-name" or "--research-igame-slot" or "--research-igame-value" or "--collision-report" or "--scene-probes" or "--scene-probe-output" or "--scene-shape-probes" or "--scene-shape-probe-output" or "--scene-living-probes" or "--scene-living-probe-output"))
                throw new ArgumentException($"Unknown option {key}.");
            if (++i == args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Missing value for {key}.");
            if (!options.TryAdd(key, args[i]))
                throw new ArgumentException($"Duplicate option {key}.");
        }
        if (new[] { "--check-content", "--check-startup", "--check-framework", "--check-scene", "--check-managed-scene", "--probe-pose-write", "--probe-player-admission" }.Count(options.ContainsKey) != 1)
            throw new ArgumentException("Select exactly one content, startup, scene, framework or probe mode.");
        if (options.ContainsKey("--collision-report") && !options.ContainsKey("--check-managed-scene"))
            throw new ArgumentException("--collision-report requires --check-managed-scene.");
        if (options.ContainsKey("--scene-probe-output") && !options.ContainsKey("--scene-probes"))
            throw new ArgumentException("--scene-probe-output requires --scene-probes.");
        if (options.ContainsKey("--scene-probes") && !(options.ContainsKey("--check-managed-scene") || options.ContainsKey("--check-scene")))
            throw new ArgumentException("--scene-probes requires a managed or native scene check.");
        if (options.ContainsKey("--scene-shape-probe-output") && !options.ContainsKey("--scene-shape-probes"))
            throw new ArgumentException("--scene-shape-probe-output requires --scene-shape-probes.");
        if (options.ContainsKey("--scene-shape-probes") && !(options.ContainsKey("--check-managed-scene") || options.ContainsKey("--check-scene")))
            throw new ArgumentException("--scene-shape-probes requires a managed or native scene check.");
        if (options.ContainsKey("--scene-living-probe-output") && !options.ContainsKey("--scene-living-probes"))
            throw new ArgumentException("--scene-living-probe-output requires --scene-living-probes.");
        if (options.ContainsKey("--scene-living-probes") && !options.ContainsKey("--check-scene"))
            throw new ArgumentException("--scene-living-probes requires the original native scene check.");
        if (options.ContainsKey("--world-file") != options.ContainsKey("--world-name"))
            throw new ArgumentException("World map validation requires both --world-file and --world-name.");
        return options;
    }

    private static string Require(Dictionary<string, string> options, string key) =>
        options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException($"Missing required option {key}.");
}
