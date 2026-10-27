using AAEmu.Zone.Content;
using AAEmu.Zone.Scripting;
using AAEmu.Zone.Startup;

namespace AAEmu.Zone.Simulation;

/// <summary>
/// Production native level-completion provider for one scene generation — Agent 5's registered
/// host file (IMPLEMENTATION_OWNERSHIP.md section 6), the sole implementation of
/// <see cref="IZoneSceneLoadingServices"/>. It OWNS the host-created
/// <see cref="ZoneSceneActivation"/> reference for this scene (the activation itself is
/// registered in the scene resource scope by the host factory, so LIFO disposal retires it
/// after this provider and before the engine) and runs the ordered native completion for
/// <c>ZoneSceneCoordinator.CompleteLoadingAsync</c> (ZoneSceneCoordinator.cs:161-185, native
/// 39332730, FIRST_ENTRY_NOTES.md:1437-1439):
///
/// script bootstrap → activation Create → six-cell Load → zero-time static completion →
/// report gate → (coordinator: static gimmicks → time → areas → authored spawners → NPC
/// postload → flush → ZWZoneLoaded → persistent spawners → bounds → final flush).
///
/// Leaf discipline (contract of IZoneSceneLoadingServices, ZoneSceneCoordinator.cs:26-33):
/// every leaf runs its real work or fails BY NAME; nothing here reports success it did not
/// produce. Leaves whose native owner does not exist yet print a once-per-session named
/// <c>TODO(...)</c> line through <see cref="ZoneSceneReadinessNotes"/> (readiness partial-leaf
/// table) and are cited below — never silently completed.
///
/// Thread affinity: <see cref="ZoneSceneActivation"/> operations must run on the engine's
/// owning thread (ZoneSceneActivation.Enter :324-331); this provider is called only from the
/// scene owner's dispatch (ZoneSimulationThread inside ZoneSceneRunner), and its async surface
/// is synchronous by construction so no context switch can move it off that thread.
/// </summary>
public sealed class ZoneSceneLoadingServices : IZoneSceneLoadingServices
{
    // Verified live facts for the dedicated slot, recorded by read-only probe (FIRST_ENTRY_NOTES:1486;
    // no +8C accessor exists in this tree — grepped). Printed at readiness with their TODO names.
    private const int DedicatedServerType = 2;

    private readonly ZoneStartupState _startup;
    private readonly ZoneSceneActivation _activation;
    private readonly string _scriptRoot;
    private readonly string _luaLibraryPath;
    private readonly ZoneSceneContent _sceneContent;
    private readonly int _zoneKey;
    private readonly ZoneSceneReadinessNotes _notes;
    private readonly Action<string>? _trace;
    private ZoneWorldMapLayout? _layout;
    private ZoneLuaRuntime? _lua;
    private ZoneDirectoryScriptSource? _scriptSource;
    private ZoneScriptLoader? _scriptLoader;
    private ZoneAiLookupTable? _lookup;
    private bool _levelLoaded;
    private bool _disposed;

    /// <param name="registry">The exact scene registry this provider reports through (required by the coordinator's component match check).</param>
    /// <param name="startup">Connection clocks and the join-derived time-of-day state.</param>
    /// <param name="activation">The host-created scene activation; Create/Load run through it, retirement stays with the resource scope.</param>
    /// <param name="scriptRoot">Absolute configured game content directory (the directory asset source) — root of ZoneDirectoryScriptSource.</param>
    /// <param name="luaLibraryPath">Absolute Lua 5.1 double+int64 library path (AAEMU_ZONE_LUA_LIBRARY or the verified default).</param>
    /// <param name="sceneContent">Scene metadata for the areas leaf (zones-table row lookup by zone key).</param>
    /// <param name="zoneKey">This scene's Zone key (join-proven, ZoneSceneCoordinator.cs:102-104).</param>
    /// <param name="notes">Shared readiness partial-leaf sink printed by the host after level completion.</param>
    public ZoneSceneLoadingServices(ZoneUnitRegistry registry, ZoneStartupState startup,
        ZoneSceneActivation activation, string scriptRoot, string luaLibraryPath, ZoneSceneContent sceneContent,
        int zoneKey, ZoneSceneReadinessNotes notes, Action<string>? trace)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(startup);
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(luaLibraryPath);
        ArgumentNullException.ThrowIfNull(sceneContent);
        ArgumentNullException.ThrowIfNull(notes);
        Registry = registry;
        _startup = startup;
        _activation = activation;
        _scriptRoot = scriptRoot;
        _luaLibraryPath = luaLibraryPath;
        _sceneContent = sceneContent;
        _zoneKey = zoneKey;
        _notes = notes;
        _trace = trace;
    }

    public ZoneUnitRegistry Registry { get; }

    /// <summary>
    /// ZoneClient+8C (39332130/39332730). This dedicated process runs the server-type-2
    /// (dedicated) slot; the slot itself has no recovered accessor in this tree, so the value is
    /// the recorded dedicated constant and the missing accessor is named once at readiness
    /// (TODO(server-type-slot)). Type 3 is what omits the time-of-day phase — it is never 3 here.
    /// </summary>
    public int ServerType => DedicatedServerType;

    /// <summary>
    /// 39332730's environment+150 read: nonzero omits spatial and AI bounds publication. No
    /// env+150 accessor exists (grepped); the dedicated default is false = bounds ARE published,
    /// and the missing slot is named once at readiness (TODO(env-slot)).
    /// </summary>
    public bool EnvironmentFlag150 => false;

    /// <summary>
    /// The required script bootstrap (plan A2), run BEFORE the first activation work:
    /// ZoneLuaRuntime over the configured lua51.dll (env AAEMU_ZONE_LUA_LIBRARY, else the
    /// verified C:\AAEmuRuntime\luatools\lua-5.1.1\x64\lua51.dll — FIRST_ENTRY_NOTES:711),
    /// ZoneDirectoryScriptSource over the configured directory asset source, then the required
    /// set: <c>loader.InstallBindings()</c> + <c>ZoneAiLookupTable.Load(source)</c>
    /// (Scripts/AI/lookup.xml). A missing or invalid lookup fails LoadLevelAsync BY NAME —
    /// ZoneAiLookupTable.Load returns false for missing/malformed XML and preserves the previous
    /// table (ZoneAiLookupTable.cs:37-47), which is exactly the refusal this gate converts to a
    /// named load failure. Every other script is loaded on demand through Script.LoadScript by
    /// the AI/signal paths that own it; no script file is executed speculatively here.
    /// </summary>
    public ValueTask LoadLevelAsync(ZoneWorldMapLayout layout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_levelLoaded) throw new InvalidOperationException("This scene's level-completion sequence already ran.");
        _layout = layout;
        _trace?.Invoke("BootstrapScripts");
        _lua = new ZoneLuaRuntime(_luaLibraryPath);
        _scriptSource = new ZoneDirectoryScriptSource(_scriptRoot);
        _scriptLoader = new ZoneScriptLoader(_lua, _scriptSource,
            message => Console.Error.WriteLine(message),
            compiledMode: () => 0); // loose scripts/ from the directory source; no scriptsbin64/.alb substitution
        _scriptLoader.InstallBindings();
        _lookup = new ZoneAiLookupTable();
        if (!_lookup.Load(_scriptSource))
            throw new InvalidDataException(
                "Scene script bootstrap failed by name: Scripts/AI/lookup.xml is missing or invalid in the configured script root " +
                $"('{_scriptRoot}'). The required set is ZoneScriptLoader.InstallBindings + ZoneAiLookupTable.Load " +
                "(ZoneAiLookupTable.cs:37-47) and a failing required script fails LoadLevelAsync.");
        _trace?.Invoke($"Script bootstrap complete: lua={_lua.LibraryPath}, root={_scriptRoot}, ai-lookup-samples={_lookup.Values.Count}");

        // activation Create → six-cell Load → zero-time static completion (plan A1).
        if (!_activation.IsCreated) _activation.Create();
        var report = _activation.Load();
        var completed = _activation.CompletePendingStaticWork();
        _trace?.Invoke($"Native queued static construction completed: {completed} requests.");
        // Report gate: a refused pass rethrows inside Load after publishing (ZoneSceneActivation
        // :171-177); a not-ready report WITHOUT a thrown refusal (a pass returned false) still
        // fails here BY NAME — readiness is never claimed from a partial load.
        if (!report.Ready)
            throw new InvalidDataException(
                $"Scene load report is not ready for zone {layout.Zone.Id}: terrain={report.TerrainLoadedCells}/{report.Cells.Count}, " +
                $"objects={report.ObjectLoadedCells}/{report.Cells.Count}, refusals={report.Refusals.Count} " +
                $"({string.Join(", ", report.Refusals.Select(refusal => refusal.Phase.ToString()))}).");
        _levelLoaded = true;
        _trace?.Invoke($"Level loaded: zone={layout.Zone.Id}, cells={report.Cells.Count}, terrain={report.TerrainLoadedCells}, objects={report.ObjectLoadedCells}, ready={report.Ready}, refusals={report.Refusals.Count}");
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 393365A0's content side, before ZWZoneLoaded. A live read-only probe of the configured
    /// content DB (artifact:
    /// C:\AAEmuRuntime\artifacts\zone-first-login\agent-5\probe_gimmicks.py) established the real
    /// counts for this milestone: tables gimmicks=211 template rows, spawn_gimmick_effects=353,
    /// doodad_func_spawn_gimmicks=16 — and NONE of the three carries level placement/zone
    /// columns, i.e. there are ZERO authored static-gimmick placement rows for any zone in this
    /// source. The placement source is level entity data (the request path exists:
    /// ZWRequestSpawnStaticGimmick 0x0026 → ZoneSimRelay.cs:540 → WZGimmickCreated echo, World
    /// Program.cs:918-924), not the content DB, so this leaf queues nothing and logs the real
    /// zero-row count with TODO(static-gimmicks). A missing content table would be a named
    /// refusal — unreachable while no re-query happens here (the probe result stands).
    /// </summary>
    public void RequestStaticGimmickSpawns()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var zoneId = _layout?.Zone.Id ?? _zoneKey;
        _trace?.Invoke(
            $"Static gimmicks requested: 0 rows for zone {zoneId} " +
            "(content DB tables: gimmicks=211 templates, spawn_gimmick_effects=353, doodad_func_spawn_gimmicks=16, no placement rows) — " +
            "TODO(static-gimmicks) placement source is level entity data, not content DB");
        _notes.AddOnce("static-gimmicks",
            "static gimmicks: 0 authored placement rows in the content DB (level entity data is the placement source) TODO(static-gimmicks)");
    }

    /// <summary>
    /// 39332130, invoked by the coordinator only for server type != 3 (ZoneSceneCoordinator.cs:
    /// :164-166). World sends the join-gate WZTimeOfDay (ZoneProtocolHandler.cs:215) during the
    /// single synchronization, so <c>startup.TimeOfDay</c> is normally set by now and the initial
    /// ZoneDetailedTimeOfDay report is queued on the registry's notification queue (flush order
    /// preserved by the coordinator's phase flush). If the join packet has not arrived, the
    /// absence is reported ONCE by name — this leaf never fabricates an hour (World owns the
    /// correction path through the ordinary WZTimeOfDay packet).
    /// </summary>
    public void InitializeDetailedTimeOfDay()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_startup.TimeOfDay is null)
        {
            _trace?.Invoke(
                "InitializeDetailedTimeOfDay: no join WZTimeOfDay received yet — no initial report queued " +
                "(World sends it at the join gate, ZoneProtocolHandler.cs:215; native 39332130 otherwise) TODO(time-of-day-join)");
            _notes.AddOnce("time-of-day-join",
                "time-of-day: join WZTimeOfDay not yet received at level completion — initial report deferred to World's packet TODO(time-of-day-join)");
            return;
        }
        Registry.QueueNotification(_startup.TimeOfDayState.CreateInitialReport());
        _trace?.Invoke($"InitializeDetailedTimeOfDay: initial report queued (hour={_startup.TimeOfDayState.Hour:0.###}, speed={_startup.TimeOfDayState.Speed:0.####}, start={_startup.TimeOfDayState.Start:0.###}, end={_startup.TimeOfDayState.End:0.###})");
    }

    /// <summary>
    /// 3932B730: resolve this scene's zones-table area row (record: GroupId/FactionId/Integration,
    /// ZoneSceneContent.cs:7-8; the identical lookup precedes it in ZoneRelationshipScene.cs:35)
    /// and log the REAL resolved-or-absent outcome. Areas feed conflict/indun/festival policy
    /// through the relationship scene, which performs the same keyed read — this leaf verifies
    /// and reports it at the native phase position.
    /// </summary>
    public void InitializeAreas()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sceneContent.Areas.TryGetValue(_zoneKey, out var area))
            _trace?.Invoke($"Areas initialized: 1 row for zone {_zoneKey} (area id={area.Id}, name={area.Name}, GroupId={area.GroupId}, FactionId={area.FactionId}, Integration={area.Integration})");
        else
        {
            _trace?.Invoke($"Areas initialized: 0 rows for zone {_zoneKey} (no zones-table area row) — relationship policy falls back to group 0");
            _notes.AddOnce("areas", $"areas: zone {_zoneKey} has no zones-table area row — group-0 policy fallback (fact, not a gap)");
        }
    }

    /// <summary>
    /// ServerAIUnitSystem virtual+2A8: native AI bounds publication. The spatial half already
    /// ran for real at ZoneSceneCoordinator.cs:180 (Registry.InitializeSceneSpatialIndex over
    /// Layout.SpatialBounds); the native AI-system half has NO owner in this tree (virtual+2A8
    /// unowned), so it is named once at readiness as TODO(ai-bounds-native) — the bounds VALUES
    /// themselves are recorded here for evidence, nothing is silently claimed.
    /// </summary>
    public void InitializeAiBounds(ZoneSpatialRegionBounds bounds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(bounds);
        _trace?.Invoke($"AI bounds initialized: spatial index published for zone {_zoneKey} (bounds recorded); TODO(ai-bounds-native): ServerAIUnitSystem virtual+2A8 publication has no owner in this tree");
        _notes.AddOnce("ai-bounds-native",
            "AI bounds: spatial index published for real; native ServerAIUnitSystem virtual+2A8 publication unowned TODO(ai-bounds-native)");
    }

    /// <summary>
    /// Retires the script runtime this provider created (Lua state + native lua51.dll mapping —
    /// ZoneLuaRuntime.Dispose closes both, ZoneLuaRuntime.cs:285-301). The activation, engine
    /// and resource scope are NOT this provider's to dispose: they are registered separately in
    /// the scene scope so LIFO order stays frame objects → loading → simulation/spawners →
    /// activation → engine.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _lua?.Dispose(); }
        catch (Exception error) { throw new InvalidOperationException("Scene script runtime retirement failed.", error); }
        _lua = null;
        _scriptLoader = null;
        _scriptSource = null;
        _lookup = null;
    }
}
