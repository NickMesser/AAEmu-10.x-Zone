using System.Runtime.ExceptionServices;

using AAEmu.Zone.Units;

namespace AAEmu.Zone.Hosting;

/// <summary>
/// Required scene and manager phases of the X2 Zone tick, 39333110. Each implementation retains its
/// native internal clocks, gates and state. Engine AI, queued actor signals and physics are separate host phases.
/// </summary>
public interface IZoneSimulationTickServices : IDisposable
{
    bool IsInitialized { get; } // 3A607AC1, set after 39333490 initializes the managers.
    int ServerType { get; } // ZoneClient +8C; type 3 omits time-of-day synchronization.
    void UpdateContentReload(); // 39331D30
    bool UpdateDetailedTimeOfDay(); // 393322F0; true suppresses the ordinary time-of-day phase.
    void UpdateTimeOfDay(); // 39332400
    void ProcessDeferredUnitRemovals(); // 3935FE10; received World removals are immediate instead.
    void ExpireSkillContexts(); // 39456970(false)
    /// <summary>Complete 3934A170 manager update, including its gate, NPC owners, time-of-day work and spawners.</summary>
    void UpdateNpcManager(uint frame);
    void UpdateHousingManager(); // Second owner-manager callback in 39360C10.
    void UpdateGimmicks(); // 39339B70
    void UpdateDoodads(); // 39452AA0
    void UpdatePlots(); // 39455230
    void UpdateAiDebugSessions(); // 3944EB80
    void UpdateAiAggroReports(); // 3944DA20
    void UpdateUnitLocationReports(); // 3935E7E0
    void UpdateMoleReports(); // 39332C50, both miner and trader ray results.
    void UpdateDoodadUnitCollisions(); // 39454970
    void UpdateHeartbeat(); // 39332510
    void UpdateMovementBuffers(); // 3936A010
}

/// <summary>
/// Orders the complete native Zone tick around the registry's real rolling unit scheduler.
/// Successful construction transfers ownership of services; the session retains ownership of the registry
/// and its scheduler. Construction performs no scene callbacks. Disposal during a callback retires the tick
/// immediately and releases services after that callback returns.
/// </summary>
public sealed class ZoneSimulationTick : IDisposable
{
    private readonly IZoneSimulationTickServices _services;
    private readonly ZoneUnitUpdateScheduler _updates;
    private bool _updating;
    private bool _servicesDisposed;
    private bool _releaseServices;

    public ZoneUnitRegistry Registry { get; }
    public bool IsDisposed { get; private set; }

    public ZoneSimulationTick(ZoneUnitRegistry registry, IZoneSimulationTickServices services)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(services);
        var updates = registry.Updates ?? throw new ArgumentException(
            "The Zone tick requires the registry's initialized unit update scheduler.", nameof(registry));
        ObjectDisposedException.ThrowIf(updates.IsDisposed, updates);
        Registry = registry;
        _updates = updates;
        _services = services;
    }

    public void Update(uint frame)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ObjectDisposedException.ThrowIf(_updates.IsDisposed, _updates);
        if (_updating) throw new InvalidOperationException("Zone simulation ticks cannot run recursively.");
        _updating = true;
        Exception? failure = null;
        try { UpdateCore(frame); }
        catch (Exception exception) { failure = exception; }
        finally
        {
            _updating = false;
            if (IsDisposed && _releaseServices)
            {
                try { DisposeServices(); }
                catch (Exception exception)
                {
                    failure = failure is null ? exception : new AggregateException(failure, exception);
                }
            }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private bool Continue()
    {
        if (IsDisposed) return false;
        ObjectDisposedException.ThrowIf(_updates.IsDisposed, _updates);
        return true;
    }

    private bool Run(Action phase)
    {
        phase();
        return Continue();
    }

    private void UpdateCore(uint frame)
    {
        var initialized = _services.IsInitialized;
        if (!Continue() || !initialized) return;
        if (!Run(_services.UpdateContentReload)) return;
        var serverType = _services.ServerType;
        if (!Continue()) return;
        if (serverType != 3)
        {
            var detailedTimeChanged = _services.UpdateDetailedTimeOfDay();
            if (!Continue()) return;
            if (!detailedTimeChanged && !Run(_services.UpdateTimeOfDay)) return;
        }
        if (!Run(_updates.Update)) return;
        if (!Run(_services.ProcessDeferredUnitRemovals)) return;
        if (!Run(_services.ExpireSkillContexts)) return;
        _services.UpdateNpcManager(frame);
        if (!Continue()) return;
        if (!Run(_services.UpdateHousingManager)) return;
        if (!Run(_services.UpdateGimmicks)) return;
        if (!Run(_services.UpdateDoodads)) return;
        if (!Run(_services.UpdatePlots)) return;
        if (!Run(_services.UpdateAiDebugSessions)) return;
        if (!Run(_services.UpdateAiAggroReports)) return;
        if (!Run(_services.UpdateUnitLocationReports)) return;
        if (!Run(_services.UpdateMoleReports)) return;
        if (!Run(_services.UpdateDoodadUnitCollisions)) return;
        if (!Run(_services.UpdateHeartbeat)) return;
        Run(_services.UpdateMovementBuffers);
    }

    public void Dispose()
    {
        if (_releaseServices) return;
        IsDisposed = true;
        _releaseServices = true;
        if (!_updating) DisposeServices();
    }

    // The adopted session must drain its pending network/frame producers before freeing any
    // backend. This marker stops Continue; its later ordinary Dispose completes ownership.
    internal void RetireForSceneShutdown() => IsDisposed = true;

    private void DisposeServices()
    {
        if (_servicesDisposed) return;
        _servicesDisposed = true;
        _services.Dispose();
    }
}
