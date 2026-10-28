using AAEmu.Zone.Actors;

namespace AAEmu.Zone.Ai;

public sealed record ZoneManagedAiCapabilities(bool ScriptSignalDispatch, bool GoalPipeExecution,
    IReadOnlyList<string> UnsupportedSystems);

/// <summary>
/// Managed behavior adapter. Optional script/scene hooks can use the existing NPC runtimes;
/// a supplied pipe controller executes the existing managed goals and owns its signal waits.
/// With no hooks this is an observable idle actor, and signal delivery fails explicitly.
/// It does not supply perception, pathfinding or combat decisions on its own.
/// </summary>
public sealed class ZoneManagedAiActor : IZoneManagedAiActor
{
    private readonly Action<ZoneAiSignal>? _signal;
    private readonly Action<uint, float>? _update;
    private readonly Action<bool>? _activation;
    private readonly Action<ZoneActorHandle?>? _target;
    private readonly Action? _retire;
    private readonly ZoneGoalPipeController? _goalPipes;
    public bool IsDisposed { get; private set; }
    public bool IsActive { get; private set; } = true;
    public ZoneActorHandle? Target { get; private set; }
    public ZoneAiSignal? LastSignal { get; private set; }
    public uint UpdateCount { get; private set; }
    public uint LastUpdateMilliseconds { get; private set; }
    public ZoneManagedAiCapabilities Capabilities { get; }

    /// <param name="goalPipes">An optional fresh controller. Ownership transfers to this actor.</param>
    public ZoneManagedAiActor(Action<ZoneAiSignal>? signal = null, Action<uint, float>? update = null,
        Action<bool>? activation = null, Action<ZoneActorHandle?>? target = null, Action? retire = null,
        ZoneGoalPipeController? goalPipes = null)
    {
        _signal = signal;
        _update = update;
        _activation = activation;
        _target = target;
        _retire = retire;
        _goalPipes = goalPipes;
        Capabilities = new(signal is not null, goalPipes is not null,
            Array.AsReadOnly(new[] { "authored behavior construction", "perception", "navigation/pathfinding", "combat and skill decisions" }));
    }

    public void SetActive(bool active)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (IsActive == active) return;
        IsActive = active;
        _activation?.Invoke(active);
    }

    public void SetTarget(ZoneActorHandle? target)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        Target = target;
        _target?.Invoke(target);
    }

    public void SendSignal(ZoneAiSignal signal)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(signal);
        if (_signal is null && _goalPipes is null)
            throw new NotSupportedException("This managed NPC has no script signal handler or goal pipes; authored behavior dispatch is not attached.");
        LastSignal = signal;
        // Normal native admission notifies waits by name without signal extra data.
        _goalPipes?.Executor.SignalWaits.Notify(signal.Name);
        if (!IsDisposed) _signal?.Invoke(signal);
    }

    public void Update(uint timeMilliseconds, float elapsedSeconds)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (!float.IsFinite(elapsedSeconds) || elapsedSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        if (!IsActive) return;
        LastUpdateMilliseconds = timeMilliseconds;
        UpdateCount++;
        _update?.Invoke(timeMilliseconds, elapsedSeconds);
        if (!IsDisposed && IsActive) _goalPipes?.Executor.Update();
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        Target = null;
        LastSignal = null;
        try { _goalPipes?.Dispose(); }
        finally { _retire?.Invoke(); }
    }
}
