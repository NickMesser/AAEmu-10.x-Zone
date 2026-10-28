using System.Numerics;
using System.Runtime.ExceptionServices;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Content;
using AAEmu.Zone.Hosting;
using AAEmu.Zone.Npcs;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Units;
using AAEmu.Zone.WorldLink;
using AAEmu.Zone.WorldState;

namespace AAEmu.Zone.Scene;

/// <summary>Mandatory real model/actor services, constructed against the initialized core registry.</summary>
public sealed class ZoneSceneRegistryServices
{
    public IZoneNpcWorld Npcs { get; }
    public IZoneUnitUpdateServices Updates { get; }
    public IZoneUnitMovementServices Movement { get; }
    public Func<ZoneUnit, Vector3> ModelPosition { get; }
    public ZoneSceneRegistryServices(IZoneNpcWorld npcs, IZoneUnitUpdateServices updates,
        IZoneUnitMovementServices movement, Func<ZoneUnit, Vector3> modelPosition)
    {
        Npcs = npcs ?? throw new ArgumentNullException(nameof(npcs));
        Updates = updates ?? throw new ArgumentNullException(nameof(updates));
        Movement = movement ?? throw new ArgumentNullException(nameof(movement));
        ModelPosition = modelPosition ?? throw new ArgumentNullException(nameof(modelPosition));
    }
}

/// <summary>
/// Required native level-completion leaves. Implementations must finish their actual configured
/// assets, terrain/object/physics, dedicated navigation, scripts and actors in LoadLevelAsync;
/// unsupported prerequisites must fail. There is deliberately no default provider.
/// Outbound messages use the supplied registry's notification queue, preserving cross-phase order.
/// An owning-thread provider must preserve that context or explicitly await its owner's SwitchTo
/// before touching engine state after context-free I/O. Cancellation callbacks only signal work.
/// </summary>
public interface IZoneSceneLoadingServices : IDisposable
{
    ZoneUnitRegistry Registry { get; }
    int ServerType { get; } // ZoneClient+8C, 39332130.
    bool EnvironmentFlag150 { get; } // 39332730: nonzero omits spatial and AI bounds.
    ValueTask LoadLevelAsync(ZoneWorldMapLayout layout, CancellationToken cancellationToken);
    void RequestStaticGimmickSpawns(); // 393365A0, before ZWZoneLoaded.
    void InitializeDetailedTimeOfDay(); // 39332130, only server type != 3.
    void InitializeAreas(); // 3932B730; provider retains environment+120/+121 gates.
    void InitializeAiBounds(ZoneSpatialRegionBounds bounds); // ServerAIUnitSystem virtual+2A8.
}

public sealed record ZoneSceneComponents(IZoneSceneLoadingServices Loading,
    ZoneNpcSpawnerFactory Spawners, ZoneSimulationTick Simulation);

public enum ZoneSceneLoadingPhase
{
    ManagersConstructed, LevelLoaded, StaticGimmicksRequested, TimeInitialized, AreasInitialized,
    AuthoredSpawnersLoaded, NpcPostLoadInitialized, ZoneLoadedSent, PersistentSpawnersRestored,
    BoundsInitialized, Completed, Retired
}

/// <summary>
/// One accepted World connection and exact scene lifetime. The caller synchronizes once, then
/// transfers that stream/startup here. 39332730 sends ZoneLoaded before persistent restoration
/// and bounds; Completed records the later native loaded flag. Failure cannot unsend a packet:
/// it closes this connection and retires this scene. Program remains validation-only until real
/// providers can satisfy every required leaf. All operations use the scene's sequential owner.
/// </summary>
public sealed class ZoneSceneCoordinator : IDisposable
{
    private readonly ZoneSceneResourceScope _resources = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _cancellationFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Exception?> _cleanupFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationToken _loadingCancellation;
    private List<Exception>? _retirementErrors;
    private volatile bool _loading, _retired, _dispatching, _receiving;
    private bool _started, _adopted, _ticking;
    private int _retirementClaimed, _cleanupClaimed;
    private ZoneSceneComponents _components = null!;
    internal ZoneSimulationThread? Owner { get; } = ZoneSimulationThread.Current;
    private bool PreserveOwnerContext => Owner is not null;
    internal Stream Connection { get; }
    public ZoneStartupState Startup { get; }
    public ZoneUnitRegistry Registry { get; }
    public ZoneStatContent StatContent { get; }
    public ZoneNpcContent NpcContent { get; }
    public ZoneWorldContentPolicy? ContentPolicy { get; }
    public ZoneWorldMapLayout Layout { get; }
    public ZoneSceneLoadingPhase Phase { get; private set; } = ZoneSceneLoadingPhase.ManagersConstructed;
    public bool ZoneLoadedWasSent { get; private set; }
    public bool IsRetired => _retired;
    internal ZoneSimulationTick Simulation => _components.Simulation;
    /// <summary>
    /// The scene's global frame composition (Agent 5, registered section 6), assembled by the
    /// host after level completion — when the physics world exists — and read by the adopted
    /// session's frame-aware tick branch. Null on scenes that never assemble a frame (their tick
    /// falls back to the bare simulation.Update path unchanged).
    /// </summary>
    internal ZoneSceneFrameCoordinator? Frame { get; set; }
    public ZoneGameScheduleController GameSchedules => _components.Spawners.GameSchedules;

    private ZoneSceneCoordinator(Stream connection, ZoneStartupState startup, ZoneStatContent stats,
        ZoneNpcContent npcs, ZoneWorldMapLayout layout, ZoneWorldContent? worldContent,
        ZonePlayerActorFactory? playerActors)
    {
        ArgumentNullException.ThrowIfNull(connection); ArgumentNullException.ThrowIfNull(startup);
        ArgumentNullException.ThrowIfNull(stats); ArgumentNullException.ThrowIfNull(npcs); ArgumentNullException.ThrowIfNull(layout);
        if (!startup.IsComplete) throw new ArgumentException("Scene construction requires the completed World synchronization.", nameof(startup));
        if (worldContent is null && startup.ContentFilter!.Categories.Count != 0)
            throw new ArgumentException("Filtered scene construction requires the configured World content catalog.", nameof(worldContent));
        Connection = connection; Startup = startup; Layout = layout; NpcContent = npcs;
        ContentPolicy = worldContent is null ? null : new(worldContent, startup.ContentFilter!);
        StatContent = ContentPolicy?.ApplyTo(stats) ?? stats;
        Registry = new(statContent: StatContent, playerActors: playerActors);
        // The response contains neither identity. Match only the original request's proven
        // Zone key; no world-name/IID mapping is inferred from the selected assets.
        startup.ClaimScene(connection, layout.Zone.Id);
    }

    public static ZoneSceneCoordinator Create(Stream connection, ZoneStartupState startup, ZoneStatContent stats,
        ZoneNpcContent npcs, ZoneWorldMapLayout layout,
        Func<ZoneUnitRegistry, ZoneSceneResourceScope, ZoneSceneRegistryServices> registryServicesFactory,
        Func<ZoneUnitRegistry, ZoneSceneResourceScope, ZoneSceneComponents> sceneServicesFactory,
        ZoneWorldContent? worldContent = null, ZonePlayerActorFactory? playerActors = null)
    {
        ArgumentNullException.ThrowIfNull(registryServicesFactory); ArgumentNullException.ThrowIfNull(sceneServicesFactory);
        // Invalid arguments retain caller ownership. Once construction begins, rollback owns the
        // stream, clocks, core registry and every allocation registered by either factory.
        var scene = new ZoneSceneCoordinator(connection, startup, stats, npcs, layout, worldContent, playerActors);
        try
        {
            var services = registryServicesFactory(scene.Registry, scene._resources) ??
                throw new InvalidOperationException("The registry service factory returned no services.");
            scene.Registry.InitializeSceneServices(services, npcs);
            var components = sceneServicesFactory(scene.Registry, scene._resources) ??
                throw new InvalidOperationException("The scene service factory returned no components.");
            // Adopt valid returned siblings before rejecting malformed components. A component
            // that already belongs to another registry never transfers through this return path.
            var spawnersMatch = components.Spawners is not null && components.Spawners.GameSchedules.BelongsTo(scene.Registry);
            var simulationMatches = components.Simulation is not null && ReferenceEquals(components.Simulation.Registry, scene.Registry);
            if (spawnersMatch) scene._resources.Own(components.Spawners!);
            if (simulationMatches) scene._resources.Own(components.Simulation!);
            // This getter is provider code and can fail. Verified siblings already belong to
            // rollback; an unverified loading service remains its factory's responsibility.
            var loadingMatches = components.Loading is not null && ReferenceEquals(components.Loading.Registry, scene.Registry);
            if (loadingMatches) scene._resources.Own(components.Loading!);
            ArgumentNullException.ThrowIfNull(components.Loading); ArgumentNullException.ThrowIfNull(components.Spawners);
            ArgumentNullException.ThrowIfNull(components.Simulation);
            if (!loadingMatches || !spawnersMatch || !simulationMatches)
                throw new ArgumentException("Scene components must belong to the exact initialized registry.", nameof(sceneServicesFactory));
            scene._components = components;
            return scene;
        }
        catch (Exception error)
        {
            scene.Fail(error);
            throw;
        }
    }

    public async Task CompleteLoadingAsync(ZoneNpcSpawnerPlacementContent placements,
        ZoneNpcSpawnerControllerContent controllers, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(placements); ArgumentNullException.ThrowIfNull(controllers);
        EnsureLive();
        if (_started) throw new InvalidOperationException("This scene's native level-completion sequence can run only once.");
        using var loadStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = _loadingCancellation = loadStop.Token;
        _started = _loading = true;
        Exception? failure = null;
        try
        {
            EnsureLive();
            await _components.Loading.LoadLevelAsync(Layout, cancellationToken).ConfigureAwait(PreserveOwnerContext);
            Advance(ZoneSceneLoadingPhase.LevelLoaded);
            _components.Loading.RequestStaticGimmickSpawns(); Advance(ZoneSceneLoadingPhase.StaticGimmicksRequested);
            var serverType = _components.Loading.ServerType; EnsureLive();
            if (serverType != 3) _components.Loading.InitializeDetailedTimeOfDay();
            Advance(ZoneSceneLoadingPhase.TimeInitialized);
            _components.Loading.InitializeAreas(); Advance(ZoneSceneLoadingPhase.AreasInitialized);
            // Reload's false result includes the native editor-disabled gate; it does not prevent
            // the later manager post-load leaf or ZoneLoaded notification in 3934B180/39332730.
            _components.Spawners.Reload(placements, controllers); Advance(ZoneSceneLoadingPhase.AuthoredSpawnersLoaded);
            Registry.Npcs!.Diagnostics.InitializePostLoad(); Advance(ZoneSceneLoadingPhase.NpcPostLoadInitialized);
            await FlushAsync(cancellationToken).ConfigureAwait(PreserveOwnerContext);
            await Connection.WriteAsync(new ZoneFrame(ZwOpcodes.ZoneLoaded, ReadOnlyMemory<byte>.Empty).Encode(), cancellationToken).ConfigureAwait(PreserveOwnerContext);
            ZoneLoadedWasSent = true; Advance(ZoneSceneLoadingPhase.ZoneLoadedSent);
            Registry.Npcs!.Spawners!.ApplyPersistentSpawners(Startup.Spawners);
            Advance(ZoneSceneLoadingPhase.PersistentSpawnersRestored);
            var skipBounds = _components.Loading.EnvironmentFlag150; EnsureLive();
            if (!skipBounds)
            {
                Registry.InitializeSceneSpatialIndex(Layout.SpatialBounds);
                _components.Loading.InitializeAiBounds(Layout.SpatialBounds);
            }
            Advance(ZoneSceneLoadingPhase.BoundsInitialized);
            await FlushAsync(cancellationToken).ConfigureAwait(PreserveOwnerContext);
            Advance(ZoneSceneLoadingPhase.Completed);
        }
        catch (Exception error) { failure = error; _retired = true; Phase = ZoneSceneLoadingPhase.Retired; }
        finally
        {
            _loading = false;
            if (_retired)
            {
                // Cancel can resume this continuation on another thread before all provider
                // cancellation callbacks have returned. Their resources and errors stay live.
                if (Volatile.Read(ref _retirementClaimed) != 0)
                    await _cancellationFinished.Task.ConfigureAwait(PreserveOwnerContext);
                try { Cleanup(); } catch (Exception error) { failure = failure is null ? error : new AggregateException(failure, error); }
            }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        EnsureLive();
        while (Registry.TryDequeueNotification(out var frame))
        {
            await Connection.WriteAsync(frame.Encode(), cancellationToken).ConfigureAwait(PreserveOwnerContext);
            EnsureLive();
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void Advance(ZoneSceneLoadingPhase phase) { EnsureLive(); Phase = phase; }
    private void EnsureLive()
    {
        Owner?.VerifyAccess();
        ObjectDisposedException.ThrowIf(_retired, this);
        if (_loading) _loadingCancellation.ThrowIfCancellationRequested();
        if (_components is not null && (!_components.Spawners.HasLiveOwners || _components.Simulation.IsDisposed ||
            Registry.Npcs!.IsStopped || Registry.IsMovementStopped || Registry.Updates!.IsDisposed || Registry.Spatial?.IsDisposed == true ||
            Startup.TimeOfDayState.IsDisposed || Startup.WorldGameClock.IsDisposed))
            throw new InvalidOperationException("A required scene owner retired before level completion.");
    }
    internal void EnsureCompleted()
    {
        EnsureLive();
        if (Phase != ZoneSceneLoadingPhase.Completed) throw new InvalidOperationException("External scene dispatch requires native level completion.");
    }
    internal void ClaimSession()
    {
        EnsureCompleted();
        if (_adopted) throw new InvalidOperationException("This scene already belongs to a Zone session.");
        _adopted = true;
    }

    internal void Dispatch(Action action, bool ticking = false)
    {
        EnsureCompleted();
        if (_dispatching) throw new InvalidOperationException("Scene dispatch cannot run recursively.");
        _dispatching = true;
        _ticking = ticking;
        Exception? failure = null;
        try { action(); }
        catch (Exception error) { failure = error; }
        finally
        {
            _dispatching = _ticking = false;
            if (_retired && !_receiving)
                try { Cleanup(); }
                catch (Exception error) { failure = failure is null ? error : new AggregateException(failure, error); }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal CancellationToken BeginReceive()
    {
        EnsureCompleted();
        if (_receiving) throw new InvalidOperationException("This scene is already receiving World frames.");
        _receiving = true;
        return _lifetime.Token;
    }

    internal async Task EndReceiveAsync()
    {
        _receiving = false;
        Dispose();
        if (Volatile.Read(ref _retirementClaimed) != 0)
            await _cancellationFinished.Task.ConfigureAwait(PreserveOwnerContext);
        Cleanup();
    }

    public void Dispose()
    {
        // Owned scenes retire on their owner. Outsiders use InvokeAsync or signal the root token;
        // a foreign cleanup claim could block the owner behind its own native resource release.
        Owner?.VerifyAccess();
        if (_retired || Interlocked.CompareExchange(ref _retirementClaimed, 1, 0) != 0) return;
        _retired = true; Phase = ZoneSceneLoadingPhase.Retired;
        Registry.CloseNotifications();
        // Cancel cooperative asset reads/writes so deferred cleanup cannot remain stranded in
        // an await after its scene owner has retired. Callback failures join terminal cleanup.
        try { _lifetime.Cancel(); }
        catch (Exception error) { (_retirementErrors ??= []).Add(error); }
        finally { _cancellationFinished.TrySetResult(); }
        // Stop later tick phases immediately, but retain its services until Session drains
        // both producers. Ordinary tick Dispose then releases the backend exactly once.
        if (_ticking) _components.Simulation.RetireForSceneShutdown();
        if (!_loading && !_dispatching && !_receiving) Cleanup();
    }
    private void Fail(Exception failure)
    {
        try { Dispose(); }
        catch (Exception cleanup) { throw new AggregateException(failure, cleanup); }
        ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private void Cleanup()
    {
        Owner?.VerifyAccess();
        if (Interlocked.CompareExchange(ref _cleanupClaimed, 1, 0) == 0)
        {
            List<Exception>? errors = _retirementErrors;
            Registry.CloseNotifications();
            Run(() => Registry.Npcs?.Stop()); Run(Registry.StopMovement);
            Run(_resources.Dispose);
            Run(() => Registry.Updates?.Dispose()); Run(() => Registry.Spatial?.Dispose());
            Run(Startup.CloseClocks); Run(Connection.Dispose); Run(_lifetime.Dispose);
            _cleanupFinished.TrySetResult(errors is null ? null : new AggregateException("Zone scene shutdown failed.", errors));
            void Run(Action action) { try { action(); } catch (Exception error) { (errors ??= []).Add(error); } }
        }
        // A concurrent Dispose and loading continuation share one outcome, including errors.
        // The claim is never held across a lock; reentrant Dispose exits at retirement above.
        var failure = _cleanupFinished.Task.GetAwaiter().GetResult();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>The scene's TowerDef manager (native 39359xxx); owned by the spawner factory like the game schedules.</summary>
    public ZoneTowerDefController TowerDefs => _components.Spawners.TowerDefs;
}
