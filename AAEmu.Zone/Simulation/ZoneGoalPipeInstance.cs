using System.Collections.ObjectModel;
using System.Numerics;

namespace AAEmu.Zone.Simulation;

/// <summary>Native CGoalPipe cursor, nesting and operation ownership, serialized by its actor.</summary>
public sealed class ZoneGoalPipeInstance : IDisposable
{
    private readonly ZoneGoalPipeCatalog _catalog;
    private readonly IZoneGoalPipeRuntime _runtime;
    private readonly ZoneGoalDefinition[] _definitions;
    private readonly ZoneGoalOperationOwner?[] _operations;
    private readonly Dictionary<string, int> _labels = new(StringComparer.Ordinal);
    private ZoneGoalPipeInstance? _parent;
    public string Name { get; }
    public IReadOnlyList<ZoneGoalDefinition> Goals { get; }
    public IReadOnlyDictionary<string, int> Labels { get; }
    public ZoneGoalPipeInstance? SubPipe { get; private set; }
    public uint Position { get; private set; }
    public int LastResult { get; private set; }
    public int EventId { get; set; }
    public bool HighPriority { get; set; }
    public bool Loop { get; set; } = true;
    public object? Argument { get; set; }
    public Vector3 AttentionTargetPositionAtStart { get; set; }
    public bool IsDisposed { get; private set; }

    internal ZoneGoalPipeInstance(ZoneGoalPipeCatalog catalog, ZoneGoalPipeDefinition definition, IZoneGoalPipeRuntime runtime)
    {
        _catalog = catalog; _runtime = runtime; Name = definition.Name;
        _definitions = definition.Goals.ToArray(); Goals = Array.AsReadOnly(_definitions);
        Labels = new ReadOnlyDictionary<string, int>(_labels);
        _operations = new ZoneGoalOperationOwner?[_definitions.Length];
        try
        {
            for (var index = 0; index < _definitions.Length; index++)
            {
                var goal = _definitions[index];
                // Native named-pipe and forget entries have no executable goal object.
                if (goal.Operation is ZoneGoalOperation.Pipe or ZoneGoalOperation.Forget) continue;
                if (goal.Operation == ZoneGoalOperation.Trace && ((goal.Parameters.Integer & 0x4000) != 0 ? Math.Abs(goal.Parameters.First) : goal.Parameters.First) > 0)
                    runtime.Diagnostic($"{Name} - Specifying distance/duration in 'trace' goal-op is deprecated.");
                _operations[index] = new(runtime.CreateOperation(goal) ?? throw new InvalidOperationException($"No operation created for {goal.Operation}."));
            }
        }
        catch (Exception creationError)
        {
            try { Dispose(); }
            catch (Exception cleanupError) { throw new AggregateException("Goal creation and cleanup failed.", creationError, cleanupError); }
            throw;
        }
    }

    public ZoneGoalPipeInstance Innermost
    {
        get { var pipe = this; while (pipe.SubPipe is { } child) pipe = child; return pipe; }
    }

    // 310F59C4 resets the cursor/result and deletes children, without resetting owned goal operations.
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        Position = 0;
        var child = DetachSubPipe();
        try { child?.Dispose(); } finally { LastResult = 0; }
    }

    public void SetLastResult(int result)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        for (var pipe = this; pipe is not null; pipe = pipe.SubPipe) pipe.LastResult = result;
    }

    // 310F5A44 inserts the new chain before the existing child chain.
    public void InsertSubPipe(ZoneGoalPipeInstance child)
    {
        ArgumentNullException.ThrowIfNull(child);
        ObjectDisposedException.ThrowIf(IsDisposed, this); ObjectDisposedException.ThrowIf(child.IsDisposed, child);
        if (!ReferenceEquals(_runtime, child._runtime) || !ReferenceEquals(_catalog, child._catalog) || child._parent is not null)
            throw new InvalidOperationException("A subpipe must be detached and belong to the same actor runtime and catalog.");
        for (var node = child; node is not null; node = node.SubPipe)
            if (ReferenceEquals(node, this)) throw new InvalidOperationException("A pipe cannot contain itself.");
        var previous = DetachSubPipe();
        if (previous is not null) { var tail = child.Innermost; tail.SubPipe = previous; previous._parent = tail; }
        SubPipe = child; child._parent = this;
    }

    /// <summary>Transfer ownership of the child chain without deleting it.</summary>
    public ZoneGoalPipeInstance? DetachSubPipe()
    {
        var child = SubPipe; SubPipe = null;
        if (child is not null) child._parent = null;
        return child;
    }

    // 310F5A84. Event ID zero selects the first zero-ID child, or the innermost child's ID.
    public bool RemoveSubPipe(ref int eventId, bool keepChildren, bool keepHighPriority)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var parent = this;
        while (true)
        {
            if (parent.HighPriority) keepHighPriority = false;
            if (parent.SubPipe is not { } child) return false;
            if (eventId == 0 && child.SubPipe is null) eventId = child.EventId;
            if (child.EventId == eventId) break;
            parent = child;
        }
        var removed = parent.DetachSubPipe()!;
        if (keepChildren)
        {
            if (removed.DetachSubPipe() is { } following) parent.InsertSubPipe(following);
        }
        else if (keepHighPriority)
        {
            var tail = removed;
            while (tail.SubPipe is { } next && !next.HighPriority) tail = next;
            if (tail.SubPipe is not null && tail.DetachSubPipe() is { } following) parent.InsertSubPipe(following);
        }
        removed.Dispose();
        return true;
    }

    // 310F5A04 counts negative jumps from the just-popped goal, positive jumps from the next goal.
    public bool Jump(int offset)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var pipe = Innermost;
        if (offset < 0) offset = unchecked(offset - 1);
        if (pipe.Position != 0) pipe.Position = unchecked(pipe.Position + (uint)offset);
        return offset < 0;
    }

    public void AddLabel(string label)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        label = ZoneAiSignalName.CString(label);
        if (_labels.ContainsKey(label)) _runtime.Diagnostic($"Label {label} already exists in goal pipe {Name}. Overriding previous label!");
        _labels[label] = _definitions.Length;
    }

    // 310F5FC4 operates on the deepest pipe and reports whether its cursor moved backwards.
    public bool JumpToLabel(string label)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var pipe = Innermost; label = ZoneAiSignalName.CString(label);
        if (!pipe._labels.TryGetValue(label, out var target))
        { _runtime.Diagnostic($"Label {label} not found in goal pipe {pipe.Name}."); return false; }
        var backward = unchecked(target - (int)pipe.Position) < 0;
        pipe.Position = (uint)target;
        return backward;
    }

    // 310F5B74 rewinds the receiving pipe, not necessarily the innermost one.
    public void RewindGroup()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (Position == 0) return;
        if (Position > _definitions.Length) throw new InvalidOperationException("Cannot rewind an out-of-range goal cursor.");
        if (_definitions[Position - 1].Grouping == ZoneGoalGrouping.Grouped)
        {
            Position--;
            while (Position != 0 && _definitions[Position - 1].Grouping == ZoneGoalGrouping.Grouped) Position--;
        }
        else
        {
            do { Position--; } while (Position != 0 && _definitions[Position].Grouping == ZoneGoalGrouping.WithPrevious);
        }
    }

    /// <summary>
    /// 310F5C24. The output is replaced only for a concrete goal entry, including forget's null operation.
    /// End/yield preserve the caller's previous lease, matching the native in/out goal parameter.
    /// </summary>
    public ZoneGoalPopStatus Pop(ref ZoneGoalLease? goal)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return PopCore(ref goal);
    }

    private ZoneGoalPopStatus PopCore(ref ZoneGoalLease? goal)
    {
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        var pipe = this;
        while (true)
        {
            if (pipe.SubPipe is { } child)
            {
                var status = child.PopCore(ref goal);
                if (pipe.IsDisposed) return ZoneGoalPopStatus.End;
                if (status != ZoneGoalPopStatus.End) return status;
                var eventId = child.EventId;
                pipe.DetachSubPipe()?.Dispose();
                if (pipe.Argument is { } argument) _runtime.RestoreArgument(argument);
                _runtime.Notify(pipe, ZoneGoalPipeEvent.Resumed);
                if (pipe.IsDisposed || eventId != 0) return ZoneGoalPopStatus.Yield;
            }
            if (pipe.Position >= pipe._definitions.Length) break;
            var index = pipe.Position++;
            var definition = pipe._definitions[index];
            if (definition.Operation != ZoneGoalOperation.Pipe)
            {
                var next = new ZoneGoalLease(definition, pipe._operations[index]);
                var previous = goal; goal = next; previous?.Dispose();
                return next.Operation is null ? ZoneGoalPopStatus.End : ZoneGoalPopStatus.Goal;
            }
            // Native malformed self-expansion allocates indefinitely; fail before exhausting the process.
            if (!expanded.Add(definition.PipeName)) throw new InvalidOperationException($"Goal pipe '{definition.PipeName}' recursively expands without yielding a goal.");
            ZoneGoalPipeInstance? pending = null;
            try
            {
                pending = _catalog.Instantiate(definition.PipeName, _runtime);
                if (pending is null) break;
                var nested = pending;
                pipe.InsertSubPipe(nested);
                pending = null; // Ownership has transferred to the parent, including when the target callback fails.
                nested.AttentionTargetPositionAtStart = _runtime.GetAttentionTargetPosition() is null
                    ? Vector3.Zero : _runtime.GetAttentionTargetPosition() ?? Vector3.Zero;
                pipe = nested;
            }
            finally { pending?.Dispose(); }
        }
        _runtime.Notify(pipe, ZoneGoalPipeEvent.Finished);
        if (!pipe.IsDisposed) pipe.Reset();
        return ZoneGoalPopStatus.End;
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        _parent?.DetachSubPipe();
        List<Exception>? errors = null;
        foreach (var operation in _operations)
            try { operation?.Release(); } catch (Exception exception) { (errors ??= []).Add(exception); }
        try { DetachSubPipe()?.Dispose(); } catch (Exception exception) { (errors ??= []).Add(exception); }
        if (errors is not null) throw new AggregateException("Goal-pipe disposal failed.", errors);
    }
}
