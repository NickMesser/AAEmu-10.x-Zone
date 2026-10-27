using AAEmu.Zone.Content;

namespace AAEmu.Zone.Npcs;

public readonly record struct ZoneNpcPendingSpawn(uint DueTime, uint DelayAfterSpawn);
public readonly record struct ZoneNpcSpawnAttempt(bool Accepted, uint RetryDelay);

public interface IZoneNpcSpawnerServices
{
    float FrameSeconds { get; }
    float Hour { get; }
    uint TimeMilliseconds { get; }
    ZoneNpcSpawnerSchedule? FindSchedule(uint spawnerType);
    //39348AF0's physical/member-selection/request body after its clock sample. On acceptance,
    // this scheduler owns the population increment and uses the pending entry's delay.
    ZoneNpcSpawnAttempt AttemptSpawn(ZoneNpcSpawnerState owner, bool belowMinimum, uint delayAfterSpawn);
    //39346050: actual member traversal, alertness-sensitive removal and packet delivery.
    void RemoveSpawnedNpcs(ZoneNpcSpawnerState owner, bool preserveAlertNpcs);
    //39341BD0 produces a single type2 AI command, numeric parameter and text argument,
    // with canInteract=true and no commander. The scene owns the script execution boundary.
    // Carry the exact queued command's lifetime through script lookups and callbacks.
    void RunDeferredAiCommand(ZoneNpc npc, string text, uint parameter, Func<bool> current);
    void DrawDebug(ZoneNpcSpawnerState owner);
}

/// <summary>
/// Native spawner scheduling state:3934A2D0 defaults,39349F80 update,39344CE0 countdowns,
///3933C590 hours and39340000 front rearming. Physical spawn/member ownership and scripts remain
/// required scene services. This does not claim39348AF0 terrain or weighted member selection.
/// </summary>
public sealed class ZoneNpcSpawnerState : IZoneNpcSpawnerPhase, IDisposable
{
    private sealed class Pending(ZoneNpcPendingSpawn value)
    {
        public ZoneNpcPendingSpawn Value = value;
    }
    private sealed class Deferred(ZoneNpc npc, string text, uint parameter, float remaining)
    {
        public ZoneNpc Npc { get; } = npc;
        public string Text { get; } = text;
        public uint Parameter { get; } = parameter;
        public float Remaining = remaining;
    }
    private readonly ZoneNpcSimulation _npcs;
    private readonly IZoneNpcSpawnerServices _services;
    private readonly List<Pending> _pending = [];
    private readonly List<Deferred> _deferred = [];
    private bool _updating;
    private object _contentGeneration = new();
    public uint SpawnerType { get; private set; }
    public bool Active { get; set; } = true;
    public int Population { get; private set; }
    public float LastHour { get; private set; }
    public uint LastAttemptTime { get; private set; }
    public uint AttemptDelay { get; private set; }
    public bool IsDisposed { get; private set; }
    public IReadOnlyList<ZoneNpcPendingSpawn> PendingSpawns => _pending.Select(entry => entry.Value).ToArray();
    public int DeferredCommandCount => _deferred.Count;

    public ZoneNpcSpawnerState(uint spawnerType, ZoneNpcSimulation npcs, IZoneNpcSpawnerServices services)
    {
        SpawnerType = spawnerType;
        _npcs = npcs ?? throw new ArgumentNullException(nameof(npcs));
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    private bool IsLive => !IsDisposed && !_npcs.IsStopped;
    internal bool BelongsTo(ZoneNpcSimulation npcs) => ReferenceEquals(_npcs, npcs);
    internal void ReplaceContent(uint type) { SpawnerType = type; _contentGeneration = new(); }
    private bool IsCurrent(ZoneNpc npc) => ReferenceEquals(_npcs.FindByObjectId(npc.ObjectId), npc);

    /// <summary>Admission boundary for39343810. Equal-due ordering follows the native queue search.</summary>
    public void QueueSpawn(uint dueTime, uint delayAfterSpawn)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (_npcs.IsStopped) throw new InvalidOperationException("The NPC scene has stopped accepting spawn requests.");
        var entry = new Pending(new(dueTime, delayAfterSpawn));
        var first = 0;
        var count = _pending.Count;
        while (count > 0)
        {
            var half = count / 2;
            var middle = first + half;
            //3933B700 tests only the sign of the wrapping32-bit subtraction (SETNS).
            if (unchecked((int)(_pending[middle].Value.DueTime - dueTime)) < 0) { first = middle + 1; count -= half + 1; }
            else count = half;
        }
        _pending.Insert(first, entry);
    }

    /// <summary>39343990 queues max-population entries using one clock read and the supplied delay.</summary>
    public void QueueAll(uint delayAfterSpawn)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        QueueAll(delayAfterSpawn, () => true);
    }

    internal void QueueAll(uint delayAfterSpawn, Func<bool> current)
    {
        var generation = _contentGeneration;
        if (!IsLive || !current()) return;
        var schedule = _services.FindSchedule(SpawnerType);
        if (!IsLive || !current() || !ReferenceEquals(generation, _contentGeneration) || schedule is null) return;
        var now = _services.TimeMilliseconds;
        if (!IsLive || !current() || !ReferenceEquals(generation, _contentGeneration)) return;
        var count = unchecked(schedule.MaximumPopulation - Population);
        for (var index = 0; index < count; index++) QueueSpawn(now, delayAfterSpawn);
    }

    internal void ClearPending() => _pending.Clear();

    internal ZoneNpcSpawnerSchedule? ReadSchedule() => _services.FindSchedule(SpawnerType);

    internal void RemoveSpawnedNpcs(bool preserveAlertNpcs, Func<bool> current)
    {
        if (IsLive && current()) _services.RemoveSpawnedNpcs(this, preserveAlertNpcs);
    }

    //39349800/39349730 call39348AF0 directly, outside the ordinary queue scheduler.
    // Its fourth argument is zero: success writes zero delay and increments total population,
    // while failure retains the physical attempt's retry delay. Neither path pops a queue entry.
    internal bool AttemptImmediately(Func<bool, ZoneNpcSpawnOverrides?, ZoneNpcSpawnAttempt> attempt,
        ZoneNpcSpawnOverrides? overrides, Func<bool> current)
    {
        var generation = _contentGeneration;
        if (!IsLive || !current()) return false;
        var now = _services.TimeMilliseconds;
        if (!IsLive || !current() || !ReferenceEquals(generation, _contentGeneration)) return false;
        LastAttemptTime = now;
        var result = attempt(true, overrides);
        if (!IsLive || !current() || !ReferenceEquals(generation, _contentGeneration)) return false;
        AttemptDelay = result.Accepted ? 0 : result.RetryDelay;
        if (result.Accepted) Population = unchecked(Population + 1);
        return result.Accepted;
    }

    /// <summary>
    ///39344AA0/39344900 completion boundary: call only after exact member ownership is validated,
    /// once a single NPC or its complete group retires. Group members do not each decrement it.
    /// </summary>
    public void RecordPopulationRemoved()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        Population = unchecked(Population - 1);
    }

    public void QueueDeferredCommand(ZoneNpc npc, string text, uint parameter, float seconds)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(npc);
        ArgumentNullException.ThrowIfNull(text);
        if (!IsLive || !IsCurrent(npc)) throw new InvalidOperationException("Deferred commands require a current NPC owner.");
        _deferred.Add(new(npc, text, parameter, seconds));
    }

    public void Update()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (!IsLive) return;
        if (_updating) throw new InvalidOperationException("Spawner updates cannot run recursively.");
        _updating = true;
        try { UpdateCore(); }
        finally { _updating = false; }
    }

    private void UpdateCore()
    {
        var generation = _contentGeneration;
        bool Current() => IsLive && ReferenceEquals(generation, _contentGeneration);
        // Sample frame time even for an empty command list or an inactive spawner.
        var frameSeconds = _services.FrameSeconds;
        if (!IsLive || !ReferenceEquals(generation, _contentGeneration)) return;
        foreach (var command in _deferred.ToArray())
        {
            if (!_deferred.Contains(command)) continue;
            command.Remaining -= frameSeconds;
            //39344DA8 COMISS zero,remaining / JB keeps unordered(NaN) countdowns in the list.
            if (!(command.Remaining <= 0)) continue;
            bool IsCommandCurrent() => IsLive && ReferenceEquals(generation, _contentGeneration) && _deferred.Contains(command) && IsCurrent(command.Npc);
            if (IsCommandCurrent()) _services.RunDeferredAiCommand(command.Npc, command.Text, command.Parameter, IsCommandCurrent);
            if (!IsLive || !ReferenceEquals(generation, _contentGeneration)) return;
            _deferred.Remove(command);
        }
        if (!Active) return;
        var hour = _services.Hour;
        if (!Current()) return;
        var deltaHour = hour - LastHour;
        if (deltaHour < 0 && deltaHour < -0.1f) LastHour -= 24f;
        var schedule = _services.FindSchedule(SpawnerType);
        if (!Current() || schedule is null) return;
        var crossedReset = (LastHour <= schedule.ResetHour && schedule.ResetHour < hour) ||
            (LastHour + 24f <= schedule.ResetHour && schedule.ResetHour < hour + 24f);
        LastHour = hour;
        if (schedule.ResetHour > 0.001f && crossedReset && Population != 0)
        {
            _services.RemoveSpawnedNpcs(this, true);
            if (!Current() || _pending.Count == 0) return;
            var first = _pending[0];
            var now = _services.TimeMilliseconds;
            if (Current() && _pending.Count != 0 && ReferenceEquals(_pending[0], first))
                first.Value = first.Value with { DueTime = now };
            return;
        }
        // Native IsSpawningTime performs a second content lookup, followed by a fresh hour read.
        var timeSchedule = _services.FindSchedule(SpawnerType);
        if (!Current() || timeSchedule is null) return;
        var spawnHour = _services.Hour;
        if (!Current() || !IsSpawningTime(timeSchedule.StartHour, timeSchedule.EndHour, spawnHour) || Population >= schedule.MaximumPopulation) return;
        var currentTime = _services.TimeMilliseconds;
        if (!Current()) return;
        var elapsed = unchecked((int)(currentTime - LastAttemptTime));
        if (elapsed >= 0 && unchecked(elapsed - (int)AttemptDelay) < 0) return;
        var belowMinimum = Population < schedule.MinimumPopulation;
        if (_pending.Count == 0) return;
        var request = _pending[0];
        if (!belowMinimum && unchecked((int)(currentTime - request.Value.DueTime)) < 0) return;
        var attemptTime = _services.TimeMilliseconds;
        if (!Current() || _pending.Count == 0 || !ReferenceEquals(_pending[0], request)) return;
        LastAttemptTime = attemptTime;
        var result = _services.AttemptSpawn(this, belowMinimum, request.Value.DelayAfterSpawn);
        if (!IsLive || !ReferenceEquals(generation, _contentGeneration)) return;
        AttemptDelay = result.Accepted ? request.Value.DelayAfterSpawn : result.RetryDelay;
        if (!result.Accepted) return;
        Population = unchecked(Population + 1);
        // Managed callback policy: never pop a replacement queued by a scene callback.
        _pending.Remove(request);
    }

    public static bool IsSpawningTime(float startHour, float endHour, float hour)
    {
        if (endHour <= startHour) endHour += 24f;
        if (hour < startHour) hour += 24f;
        return hour < endHour;
    }

    public void DrawDebug()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (IsLive) _services.DrawDebug(this);
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        _pending.Clear();
        _deferred.Clear();
    }
}
