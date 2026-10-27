namespace AAEmu.Zone.Simulation;

public interface IZoneNpcManagerServices : IZoneNpcPostureServices
{
    bool MovementRateLimitEnabled { get; }
    uint MovementIntervalMilliseconds { get; }
    uint TimeMilliseconds { get; }
    bool DebugDrawingAvailable { get; } // Native environment +120.
    bool DrawSpawners { get; } // Configuration at 3A607CE8, read only when drawing is available.
}

/// <summary>A registered spawner's complete update (39349F80) and renderer work (3933FDD0).</summary>
public interface IZoneNpcSpawnerPhase
{
    void Update();
    void DrawDebug();
}

/// <summary>The scene's group-supporter update (39343460) and formation debug drawing (393423A0).</summary>
public interface IZoneNpcGroupPhase
{
    void Update();
    void DrawDebug();
}

/// <summary>
/// Complete 3934A170 ordering around actual NPC owners, the retained movement gate and posture state.
/// Spawners run in registration order. Services, spawners and group state remain caller-owned;
/// this manager owns their registration order and its own gate/posture state.
/// </summary>
public sealed class ZoneNpcManager : IDisposable
{
    private sealed class Registration(IZoneNpcSpawnerPhase spawner)
    {
        public IZoneNpcSpawnerPhase Spawner { get; } = spawner;
    }
    private readonly ZoneNpcSimulation _npcs;
    private readonly IZoneNpcManagerServices _services;
    private readonly IZoneNpcGroupPhase _groups;
    private readonly List<Registration> _spawners = [];
    private readonly Dictionary<IZoneNpcSpawnerPhase, Registration> _registrations = new(ReferenceEqualityComparer.Instance);
    private readonly ZoneNpcPostureState _postures;
    private bool _updating;
    public ZoneNpcMovementGate Movement { get; } = new();
    public ZoneNpcDiagnostics Diagnostics => _npcs.Diagnostics;
    public int LastPostureHour => _postures.LastHour;
    public uint PosturePassCount => _postures.PassCount;
    public int SpawnerCount => _spawners.Count;
    public bool IsDisposed { get; private set; }

    internal bool BelongsTo(ZoneNpcSimulation npcs, IZoneNpcGroupPhase groups) =>
        ReferenceEquals(_npcs, npcs) && ReferenceEquals(_groups, groups);

    public ZoneNpcManager(ZoneNpcSimulation npcs, IZoneNpcManagerServices services, IZoneNpcGroupPhase groups)
    {
        _npcs = npcs ?? throw new ArgumentNullException(nameof(npcs));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _groups = groups ?? throw new ArgumentNullException(nameof(groups));
        _postures = new(npcs, services);
    }

    public bool RegisterSpawner(IZoneNpcSpawnerPhase spawner)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(spawner);
        if (_npcs.IsStopped) throw new InvalidOperationException("The NPC scene has stopped accepting spawners.");
        if (_registrations.ContainsKey(spawner)) return false;
        var registration = new Registration(spawner);
        _registrations.Add(spawner, registration);
        _spawners.Add(registration);
        return true;
    }

    public bool RemoveSpawner(IZoneNpcSpawnerPhase spawner)
    {
        ArgumentNullException.ThrowIfNull(spawner);
        if (!_registrations.Remove(spawner, out var registration)) return false;
        _spawners.Remove(registration);
        return true;
    }

    public void Update(uint frame)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (_npcs.IsStopped) return;
        if (_updating) throw new InvalidOperationException("NPC manager updates cannot run recursively.");
        _updating = true;
        try { UpdateCore(frame); }
        finally { _updating = false; }
    }

    private bool IsActive => !IsDisposed && !_npcs.IsStopped;
    private bool IsCurrent(Registration registration) => _registrations.TryGetValue(registration.Spawner, out var current) &&
        ReferenceEquals(registration, current);

    private void UpdateCore(uint frame)
    {
        var limited = _services.MovementRateLimitEnabled;
        if (!IsActive) return;
        if (limited)
        {
            var interval = _services.MovementIntervalMilliseconds;
            if (!IsActive) return;
            var now = _services.TimeMilliseconds;
            if (!IsActive) return;
            Movement.Update(true, interval, () => now);
        }
        else Movement.Update(false, 0, static () => 0);
        _npcs.Update(frame, Movement.CanReplicate);
        if (!IsActive) return;
        _postures.Update(frame);
        if (!IsActive) return;
        // Managed callback policy: snapshot each traversal, and guard registration identity.
        // Mutations can affect a later phase; removing and re-registering creates a new owner.
        foreach (var registration in _spawners.ToArray())
        {
            if (!IsCurrent(registration)) continue;
            registration.Spawner.Update();
            if (!IsActive) return;
        }
        _groups.Update();
        if (!IsActive) return;
        var canDraw = _services.DebugDrawingAvailable;
        if (!IsActive || !canDraw) return;
        var draw = _services.DrawSpawners;
        if (!IsActive || !draw) return;
        foreach (var registration in _spawners.ToArray())
        {
            if (!IsCurrent(registration)) continue;
            registration.Spawner.DrawDebug();
            if (!IsActive) return;
        }
        _groups.DrawDebug();
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        _postures.Dispose();
        _spawners.Clear();
        _registrations.Clear();
    }
}
