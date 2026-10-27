namespace AAEmu.Zone.Simulation;

/// <summary>Owns the actor's selected pipe, pending cancellations, listeners and active-goal executor.</summary>
public sealed class ZoneGoalPipeController : IZoneGoalPipeRuntime, IZoneGoalExecutionServices, IDisposable
{
    private sealed record Registration(ZoneGoalPipeListener Listener, int EventId, string? DebugName);
    private readonly ZoneGoalPipeCatalog _catalog;
    private readonly IZoneGoalPipeActorServices _actor;
    private readonly ZoneGoalPipeEventDispatcher _events;
    private readonly SortedSet<int> _pendingCancellations = [];
    private readonly List<Registration> _listeners = [];
    private bool _disposed;
    public ZoneGoalPipeInstance? CurrentPipe { get; private set; }
    public ZoneGoalExecutor Executor { get; }
    public IReadOnlyCollection<int> PendingCancellations => _pendingCancellations.ToArray();

    public ZoneGoalPipeController(ZoneGoalPipeCatalog catalog, IZoneGoalPipeActorServices actor, ZoneGoalPipeEventDispatcher events)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _actor = actor ?? throw new ArgumentNullException(nameof(actor));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        Executor = new(this);
    }

    public void RegisterListener(ZoneGoalPipeListener listener, int eventId, string? debugName = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); ArgumentNullException.ThrowIfNull(listener);
        listener.Attach(this, eventId);
        _listeners.Add(new(listener, eventId, debugName)); // Duplicate registrations are distinct native multimap entries.
    }

    public void UnregisterListener(ZoneGoalPipeListener listener, int eventId)
    {
        var index = _listeners.FindIndex(entry => ReferenceEquals(entry.Listener, listener) && entry.EventId == eventId);
        if (index < 0) return;
        _listeners.RemoveAt(index); listener.Detach(this, eventId);
    }

    internal void DeliverEvent(int eventId, ZoneGoalPipeEvent kind)
    {
        // A listener may unregister itself or remove the actor. Never follow a freed native registration node.
        foreach (var entry in _listeners.Where(entry => entry.EventId == eventId).ToArray())
            if (!_disposed && _listeners.Any(current => ReferenceEquals(current, entry))) entry.Listener.Notify(this, kind, eventId);
    }

    public void Notify(int eventId, ZoneGoalPipeEvent kind) => _events.Dispatch(this, eventId, kind);

    // 3115A984 compares every selected pipe name, including the root and innermost pipe.
    public bool IsUsingPipe(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        name = ZoneAiSignalName.CString(name);
        for (var pipe = CurrentPipe; pipe is not null; pipe = pipe.SubPipe)
            if (pipe.Name == name) return true;
        return false;
    }

    // 31161C64. Same-name reuse changes only a non-null argument and the loop flag after actor preparation.
    public bool Select(ZoneGoalPipeFlags flags, string name, object? argument = null, int eventId = 0, bool force = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        name = ZoneAiSignalName.CString(name);
        _actor.PreparePipeSelection();
        if (_disposed) return false;
        if (CurrentPipe is { } same && !force && same.Name == name)
        {
            if (argument is not null) same.Argument = argument;
            same.Loop = (flags & ZoneGoalPipeFlags.RunOnce) == 0;
            return true;
        }
        ZoneGoalPipeInstance? priority = CurrentPipe;
        while (priority is not null && !priority.HighPriority) priority = priority.SubPipe;
        if (priority is null && argument is not null) _actor.SetArgument(argument);
        if (_disposed) return false;
        if (priority is null || force) _actor.ResetForPipeSelection(flags);
        if (_disposed) return false;

        ZoneGoalPipeInstance? pending = null, preserved = null;
        try
        {
            pending = _catalog.Instantiate(name, this);
            if (pending is null)
            {
                _actor.Diagnostic($"SelectPipe: Goalpipe '{name}' does not exists. Selecting default goalpipe '_first_' instead.");
                pending = _catalog.Instantiate("_first_", this) ?? throw new InvalidOperationException("The default goal pipe is missing.");
                pending.Argument = null;
            }
            else
            {
                pending.AttentionTargetPositionAtStart = ReadTargetPosition();
                pending.Argument = argument;
            }
            if (_disposed) return false;
            if (priority is null || force) ClearCurrent(force);
            if (_disposed) return false;
            // Native skips ClearCurrent when preserving priority and deletes this still-attached child.
            // Transfer it explicitly instead of reattaching a freed pipe and losing its goal definitions.
            if (priority is not null && !force)
            {
                if (ReferenceEquals(priority, CurrentPipe)) { preserved = priority; CurrentPipe = null; }
                else
                {
                    var parent = CurrentPipe;
                    while (parent is not null && !ReferenceEquals(parent.SubPipe, priority)) parent = parent.SubPipe;
                    preserved = parent?.DetachSubPipe();
                }
            }
            var previous = CurrentPipe; CurrentPipe = null; previous?.Dispose();
            CurrentPipe = pending; pending = null;
            CurrentPipe.Reset(); CurrentPipe.EventId = eventId;
            if (preserved is not null) { CurrentPipe.InsertSubPipe(preserved); preserved = null; }
            else _actor.ClearSelectionTarget();
            CurrentPipe.Loop = (flags & ZoneGoalPipeFlags.RunOnce) == 0;
            _actor.RecordPipeEvent(5, name); _actor.ActorPipeEvent(5, name);
            if (!_disposed) _actor.ClearSelectionControls();
            return !_disposed;
        }
        finally
        {
            try { pending?.Dispose(); }
            // The detached chain remains owned here if disposal of the previous root or attachment throws.
#pragma warning disable CA1508
            finally { preserved?.Dispose(); }
#pragma warning restore CA1508
        }
    }

    // 311622C4. Returns a borrowed pipe; the controller owns it after insertion.
    public ZoneGoalPipeInstance? Insert(ZoneGoalPipeFlags flags, string name, object? argument = null, int eventId = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        name = ZoneAiSignalName.CString(name);
        if (CurrentPipe is not { } root) return null;
        if (eventId != 0 && _pendingCancellations.Remove(eventId)) return null;
        var depth = 0;
        for (var child = root.SubPipe; child is not null; child = child.SubPipe) depth++;
        if (depth > 7) _actor.ReportSubPipeCount(depth, root.Name, name);
        ZoneGoalPipeInstance? pending = null;
        try
        {
            pending = _catalog.Instantiate(name, this);
            if (pending is null)
            {
                _actor.Diagnostic($"InsertSubPipe: Goalpipe '{name}' does not exists. No goalpipe inserted.");
                _actor.ActorPipeEvent(6, name + "[Not Found!]");
                return null;
            }
            pending.HighPriority = (flags & ZoneGoalPipeFlags.HighPriority) != 0;
            var parent = root; var traversed = 0;
            while (parent.SubPipe is { } child)
            {
                if ((flags & ZoneGoalPipeFlags.NoDuplicate) != 0 && parent.Name == name) return null;
                if ((flags & (ZoneGoalPipeFlags.SamePriority | ZoneGoalPipeFlags.HighPriority)) == 0 && child.HighPriority) break;
                traversed++; parent = child;
            }
            if (traversed > 10)
            {
                var chain = new List<ZoneGoalPipeInstance>();
                for (var pipe = root; pipe.SubPipe is not null; pipe = pipe.SubPipe) chain.Add(pipe);
                _actor.ReportInsertionChain(chain);
            }
            if ((flags & ZoneGoalPipeFlags.NoDuplicate) != 0 && parent.Name == name) return null;
            if (parent.SubPipe is null)
            {
                if (Executor.ActiveGoalCount != 0)
                {
                    var blocked = Executor.IsBlocked;
                    Executor.ClearActiveGoals();
                    if (blocked) parent.RewindGroup();
                }
                Notify(parent, ZoneGoalPipeEvent.Suspended);
                if (_disposed) return null;
                if (argument is not null) _actor.SetArgument(argument);
                Executor.ClearBlocking();
            }
            if (_disposed || parent.IsDisposed) return null;
            var inserted = pending;
            parent.InsertSubPipe(inserted); pending = null;
            inserted.AttentionTargetPositionAtStart = ReadTargetPosition();
            inserted.Argument = argument; inserted.EventId = eventId;
            _actor.RecordPipeEvent(6, name); _actor.ActorPipeEvent(6, name);
            return _disposed || inserted.IsDisposed ? null : inserted;
        }
        finally { pending?.Dispose(); } // Also releases the clone that native leaks on duplicate rejection.
    }

    // 31162064 cancels identified children, preserving the first high-priority suffix.
    public bool Cancel(int eventId, bool keepChildren = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (eventId == 0) return false;
        if (CurrentPipe is { } root)
        {
            var oldTail = root.Innermost;
            ZoneGoalPipeInstance? matching = null;
            for (var child = root.SubPipe; child is not null; child = child.SubPipe) if (child.EventId == eventId) matching = child;
            if (!keepChildren && matching is not null)
            {
                var parent = matching;
                while (parent.SubPipe is { HighPriority: false } child)
                {
                    if (child.EventId != 0) Remove(child.EventId);
                    else parent = child;
                    if (_disposed || root.IsDisposed) return false;
                }
            }
            var id = eventId;
            if (root.RemoveSubPipe(ref id, keepChildren, true))
            {
                Notify(id, ZoneGoalPipeEvent.Canceled);
                if (_disposed || CurrentPipe is null) return true;
                var tail = CurrentPipe.Innermost;
                if (!ReferenceEquals(tail, oldTail))
                {
                    Executor.ClearActiveGoals(); _actor.SetArgument(tail.Argument); Notify(tail, ZoneGoalPipeEvent.Resumed);
                }
                return true;
            }
        }
        _pendingCancellations.Add(eventId);
        return false;
    }

    // 311621A4 removes one child. A failed nonzero request suppresses one later insertion of that ID.
    public bool Remove(int eventId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var id = eventId;
        if (CurrentPipe is { } root)
        {
            var wasTail = eventId == 0 || root.Innermost.EventId == eventId;
            _actor.EnableSignals();
            if (root.RemoveSubPipe(ref id, true, false))
            {
                Notify(id, ZoneGoalPipeEvent.Deselected);
                if (wasTail && !_disposed && CurrentPipe is { } current)
                {
                    Executor.ClearActiveGoals(); var tail = current.Innermost;
                    _actor.SetArgument(tail.Argument); Notify(tail, ZoneGoalPipeEvent.Resumed);
                }
                return true;
            }
        }
        if (id != 0) _pendingCancellations.Add(id);
        return false;
    }

    private void ClearCurrent(bool force)
    {
        _actor.RecordPipeEvent(7, CurrentPipe?.Name); _actor.ActorPipeEvent(7, CurrentPipe?.Name);
        if (_disposed) return;
        var pipe = CurrentPipe;
        while (pipe is not null)
        {
            Notify(pipe, ZoneGoalPipeEvent.Deselected);
            if (_disposed) return;
            if (!force && pipe.SubPipe is { HighPriority: true }) break;
            pipe = pipe.SubPipe;
        }
        if (pipe is not null) throw new InvalidOperationException("Priority ownership must be transferred before clearing the root pipe.");
        Executor.ClearActiveGoals();
        var previous = CurrentPipe; CurrentPipe = null; previous?.Dispose();
        if (!_disposed) _actor.ResetAfterPipeClear();
    }

    private System.Numerics.Vector3 ReadTargetPosition() => _actor.GetAttentionTargetPosition() is null
        ? System.Numerics.Vector3.Zero : _actor.GetAttentionTargetPosition() ?? System.Numerics.Vector3.Zero;

    public void Update() { ObjectDisposedException.ThrowIf(_disposed, this); Executor.Update(); }
    public void Notify(ZoneGoalPipeInstance pipe, ZoneGoalPipeEvent kind) => Notify(pipe.EventId, kind);
    IZoneGoalOperation IZoneGoalPipeRuntime.CreateOperation(ZoneGoalDefinition definition) => _actor.CreateOperation(definition, Executor);
    System.Numerics.Vector3? IZoneGoalPipeRuntime.GetAttentionTargetPosition() => _actor.GetAttentionTargetPosition();
    void IZoneGoalPipeRuntime.RestoreArgument(object argument) => _actor.SetArgument(argument);
    void IZoneGoalPipeRuntime.Diagnostic(string message) => _actor.Diagnostic(message);
    IZoneGoalOperation? IZoneGoalExecutionServices.AuxiliaryGoal => _actor.AuxiliaryGoal;
    uint IZoneGoalExecutionServices.NextRandom() => _actor.NextRandom();
    bool IZoneGoalExecutionServices.EvaluateBranch(ZoneGoalParameters parameters) => _actor.EvaluateBranch(parameters);
    System.Numerics.Vector3? IZoneGoalExecutionServices.GetAttentionTargetPosition() => _actor.GetAttentionTargetPosition();
    void IZoneGoalExecutionServices.ClearAttentionTarget() => _actor.ClearAttentionTarget();
    void IZoneGoalExecutionServices.ResetActorAfterClear() => _actor.ResetActorAfterClear();
    void IZoneGoalExecutionServices.ReportSubPipeCount(int count, string rootName) => _actor.ReportSubPipeCount(count, rootName, null);
    void IZoneGoalExecutionServices.ReportActiveGoalCount(int count, string? rootName, ZoneGoalDefinition lastGoal) => _actor.ReportActiveGoalCount(count, rootName, lastGoal);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var entry in _listeners) entry.Listener.Detach(this, entry.EventId);
        _listeners.Clear();
        try { Executor.Dispose(); }
        finally { var previous = CurrentPipe; CurrentPipe = null; previous?.Dispose(); }
    }
}
