namespace AAEmu.Zone.Simulation;

/// <summary>
/// 311614B4 scheduling, 31160424 retirement and 31160504 clearing. Calls run in the actor phase.
/// Pipe selection and the enclosing actor's path/movement update remain separate responsibilities.
/// </summary>
public sealed class ZoneGoalExecutor(IZoneGoalExecutionServices services) : IDisposable
{
    private readonly IZoneGoalExecutionServices _services = services ?? throw new ArgumentNullException(nameof(services));
    private readonly List<ZoneGoalLease> _active = [];
    private bool _disposed, _updating, _clearing;
    public bool IsBlocked { get; private set; }
    public int ActiveGoalCount => _active.Count;
    public ZoneGoalOperation CurrentOperation { get; private set; }
    public ZoneSignalWaitRegistry SignalWaits { get; } = new();

    public ZoneGoalDefinition GetActiveGoal(int index) => _active[index].Definition;
    public ZoneGoalLease RetainActiveGoal(int index) => _active[index].Retain();
    internal void ClearBlocking() => IsBlocked = false;

    /// <summary>Retains an operation for the actor's active list; the caller retains its own lease.</summary>
    public void AddActiveGoal(ZoneGoalLease goal)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(goal);
        if (goal.Operation is null) throw new ArgumentException("An active goal requires an executable operation.", nameof(goal));
        if (_clearing) throw new InvalidOperationException("Cannot activate goals during active-goal cleanup.");
        // Native still adopts the blocking flag when its 110-entry storage limit is reached.
        if (_active.Count < 110) _active.Add(goal.Retain());
        IsBlocked = goal.Definition.Blocking;
    }

    public void Update()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_updating || _clearing) throw new InvalidOperationException("Goal updates cannot run recursively or during cleanup.");
        _updating = true;
        try
        {
            if (_services.CurrentPipe is { IsDisposed: false } pipe)
            {
                var count = 0;
                for (var child = pipe.SubPipe; child is not null; child = child.SubPipe) count++;
                if (count > 9) _services.ReportSubPipeCount(count, pipe.Name);
                if (!_disposed && !IsBlocked) StartGoals();
            }
            if (_disposed) return;
            if (_active.Count > 9) _services.ReportActiveGoalCount(_active.Count, _services.CurrentPipe?.Name, _active[^1].Definition);
            if (_disposed) return;
            _services.AuxiliaryGoal?.Execute();
            if (_disposed) return;
            for (var index = 0; index < _active.Count;)
            {
                // A wait can remove other goals and move this goal while it executes.
                using var executing = _active[index].Retain();
                var entry = _active[index];
                CurrentOperation = entry.Definition.Operation;
                var result = executing.Operation!.Execute();
                if (_disposed) return;
                var currentIndex = _active.IndexOf(entry);
                if (currentIndex < 0) continue; // A callback removed it; never retire the replacement.
                if (result != 1)
                {
                    if (entry.Definition.Blocking)
                    {
                        if (result != 4) SetLastResult(result);
                        IsBlocked = false;
                    }
                    RemoveActiveGoal(currentIndex);
                    if (currentIndex < index) index = currentIndex;
                }
                else index++;
            }
        }
        finally { _updating = false; }
    }

    private void StartGoals()
    {
        ZoneGoalLease? goal = null;
        var stop = false;
        ZoneGoalPopStatus status;
        try
        {
            while (true)
            {
                if (_disposed || _services.CurrentPipe is not { IsDisposed: false } pipe) return;
                status = pipe.Pop(ref goal);
                if (_disposed) return;
                if (status != ZoneGoalPopStatus.Goal) break;
                var definition = goal!.Definition;
                var parameters = definition.Parameters;
                if (definition.Operation == ZoneGoalOperation.Branch)
                {
                    stop = definition.Blocking;
                    if (EvaluateBranch(parameters) != ((parameters.Integer & 0x100) != 0))
                    {
                        var current = _services.CurrentPipe;
                        if (current is { IsDisposed: false })
                            stop |= parameters.Text.Length == 0 ? current.Jump(parameters.Integer2) : current.JumpToLabel(parameters.Text);
                    }
                    if (!stop) continue;
                }
                else if (definition.Operation == ZoneGoalOperation.Random)
                {
                    stop = true;
                    if ((float)(_services.NextRandom() % 100) < parameters.First)
                        _services.CurrentPipe?.Jump(parameters.Integer); // Native uses Integer, even after label compilation sets Integer2.
                }
                else if (definition.Operation == ZoneGoalOperation.Clear)
                {
                    ClearActiveGoals();
                    if (_disposed) return;
                    if (parameters.First == 0) stop = true;
                    else { _services.ClearAttentionTarget(); if (!_disposed) _services.ResetActorAfterClear(); }
                }
                else
                {
                    var result = goal.Operation!.Execute();
                    if (_disposed) return;
                    if (result != 1)
                    {
                        if (result != 4) SetLastResult(result);
                        continue;
                    }
                }
                break;
            }
            if (_disposed || status == ZoneGoalPopStatus.Yield) return;
            if (goal?.Operation is null)
            {
                if (_services.CurrentPipe is { IsDisposed: false, Loop: true } pipe)
                {
                    pipe.Reset();
                    ClearActiveGoals();
                    // Native queries the target twice and leaves the saved position unchanged when absent.
                    if (!_disposed && _services.GetAttentionTargetPosition() is not null &&
                        _services.GetAttentionTargetPosition() is { } position && _services.CurrentPipe is { IsDisposed: false } current)
                        current.AttentionTargetPositionAtStart = position;
                }
            }
            else if (!stop) AddActiveGoal(goal);
        }
        finally { goal?.Dispose(); }
    }

    private bool EvaluateBranch(ZoneGoalParameters parameters)
    {
        var kind = parameters.Integer & ~0x100;
        return kind switch
        {
            < 1 or > 48 => _active.Count != 0,
            42 => _services.CurrentPipe is { IsDisposed: false } pipe && pipe.Innermost.LastResult == 3,
            43 => _services.CurrentPipe is { IsDisposed: false } pipe && pipe.Innermost.LastResult == 2,
            48 => true,
            _ => _services.EvaluateBranch(parameters)
        };
    }

    private void SetLastResult(int result)
    { if (_services.CurrentPipe is { IsDisposed: false } pipe) pipe.SetLastResult(result); }

    /// <summary>31159D94: grouped active operations, excluding wait operations.</summary>
    public int CountGroupedGoals() => _active.Count(goal => IsGrouped(goal.Definition));
    private static bool IsGrouped(ZoneGoalDefinition goal) => goal.Grouping == ZoneGoalGrouping.Grouped && goal.Operation != ZoneGoalOperation.Wait;

    /// <summary>31161364 removes grouped operations with the same swap-last ordering as normal retirement.</summary>
    public void ClearGroupedGoals()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        for (var index = 0; index < _active.Count;)
            if (IsGrouped(_active[index].Definition)) RemoveActiveGoal(index); else index++;
    }

    public void RemoveActiveGoal(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_clearing) throw new InvalidOperationException("Cannot retire goals during active-goal cleanup.");
        if (_active.Count == 0) return;
        if (_active.Count == 1) index = 0; // Native singleton removal ignores the index.
        var removed = _active[index];
        try { removed.Operation!.Reset(); }
        finally
        {
            var currentIndex = _active.IndexOf(removed);
            if (currentIndex >= 0)
            {
                _active[currentIndex] = _active[^1]; _active.RemoveAt(_active.Count - 1);
                removed.Dispose();
            }
        }
    }

    public void ClearActiveGoals()
    {
        if (_clearing) throw new InvalidOperationException("Active-goal cleanup cannot run recursively.");
        _clearing = true;
        List<Exception>? errors = null;
        try
        {
            foreach (var goal in _active)
                try { goal.Operation!.Reset(); } catch (Exception exception) { (errors ??= []).Add(exception); }
            var removed = _active.ToArray(); _active.Clear(); IsBlocked = false;
            foreach (var goal in removed)
                try { goal.Dispose(); } catch (Exception exception) { (errors ??= []).Add(exception); }
        }
        finally { _clearing = false; }
        if (errors is not null) throw new AggregateException("Active-goal cleanup failed.", errors);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (!_clearing) ClearActiveGoals(); }
        finally { SignalWaits.Dispose(); }
    }
}
