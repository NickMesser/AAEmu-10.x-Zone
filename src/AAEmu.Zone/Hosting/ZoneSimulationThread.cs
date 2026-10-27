using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace AAEmu.Zone.Hosting;

/// <summary>
/// One dedicated thread and continuation queue for a complete scene lifetime. The root operation
/// owns and awaits its producers and resource cleanup; cancellation does not terminate the pump.
/// Native callbacks and scene mutations run synchronously on this owner. Detached asynchronous
/// operations and async-void callbacks must not outlive the root operation.
/// </summary>
public sealed class ZoneSimulationThread
{
    [ThreadStatic] private static ZoneSimulationThread? _current;
    private readonly object _gate = new();
    private readonly BlockingCollection<Action> _queue = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop;
    private readonly Thread _thread;
    private readonly Func<ZoneSimulationThread, CancellationToken, Task> _run;
    private readonly List<Exception> _callbackErrors = [];
    private bool _finished;
    private volatile bool _rootCompleted;
    private volatile Task? _root;
    private Exception? _rootError;
    private bool RootCompleted => _rootCompleted || _root?.IsCompleted == true;

    public static ZoneSimulationThread? Current => _current;
    public bool HasAccess => ReferenceEquals(_current, this) && ReferenceEquals(Thread.CurrentThread, _thread);

    private ZoneSimulationThread(Func<ZoneSimulationThread, CancellationToken, Task> run,
        CancellationToken cancellationToken, string name)
    {
        _run = run;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _thread = new Thread(Pump) { IsBackground = true, Name = name };
    }

    /// <summary>
    /// Returns only after the root operation and its owner queue have finished. Construct native
    /// resources inside run and dispose them there in finally. Every asynchronous child must be
    /// awaited. External cancellation callbacks may signal I/O or post work, not touch the engine.
    /// </summary>
    public static Task RunAsync(Func<ZoneSimulationThread, CancellationToken, Task> run,
        CancellationToken cancellationToken = default, string name = "AAEmu Zone simulation")
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
        var owner = new ZoneSimulationThread(run, cancellationToken, name);
        try { owner._thread.Start(); }
        catch { owner._stop.Dispose(); owner._queue.Dispose(); throw; }
        return owner._completion.Task;
    }

    public void VerifyAccess()
    {
        if (!HasAccess) throw new InvalidOperationException("This operation belongs to the Zone simulation thread.");
    }

    /// <summary>
    /// Explicit, uncancelable owner resumption, including after ConfigureAwait(false) and during
    /// cleanup. Awaiting an ordinary Task completed on this thread would not provide that guarantee.
    /// </summary>
    public OwnerAwaitable SwitchTo() => new(this);

    /// <summary>
    /// Queues one synchronous operation from an external caller. Admission stops on cancellation;
    /// already queued operations are rejected once the root has retired. Never pass async void.
    /// Calls already on the owner execute immediately, including owner-thread cleanup.
    /// </summary>
    public Task InvokeAsync(Action operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (RootCompleted) return Task.FromException(new ObjectDisposedException(nameof(ZoneSimulationThread)));
        if (HasAccess)
        {
            try { operation(); return Task.CompletedTask; }
            catch (Exception error) { return Task.FromException(error); }
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_finished || RootCompleted) return Task.FromException(new ObjectDisposedException(nameof(ZoneSimulationThread)));
            if (_stop.IsCancellationRequested) return Task.FromCanceled(_stop.Token);
            _queue.Add(() =>
            {
                try
                {
                    if (RootCompleted) throw new ObjectDisposedException(nameof(ZoneSimulationThread));
                    operation(); completion.TrySetResult();
                }
                catch (Exception error) { completion.TrySetException(error); }
            });
        }
        return completion.Task;
    }

    private void PostContinuation(Action continuation)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_finished, this);
            _queue.Add(continuation);
        }
    }

    private void Pump()
    {
        _current = this;
        SynchronizationContext.SetSynchronizationContext(new OwnerContext(this));
        try
        {
            Task root;
            try { root = _run(this, _stop.Token) ?? throw new InvalidOperationException("The Zone root operation returned no task."); }
            catch (Exception error) { root = Task.FromException(error); }
            _root = root; // IsCompleted also covers delayed RunContinuationsAsynchronously callbacks.
            // Completion is itself queued: the current callback must return before retirement.
            _ = root.ContinueWith(task =>
            {
                _rootCompleted = true; // Reject queued commands before the later Finish marker.
                PostContinuation(() => Finish(task));
            }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            foreach (var operation in _queue.GetConsumingEnumerable())
            {
                try { operation(); }
                catch (Exception error)
                {
                    _callbackErrors.Add(error);
                    // Keep pumping while the root observes cancellation and retires its resources.
                    try { _stop.Cancel(); }
                    catch (Exception cancellationError) { _callbackErrors.Add(cancellationError); }
                }
            }
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(null);
            _current = null;
            _stop.Dispose();
            _queue.Dispose();
        }
        if (_callbackErrors.Count != 0)
        {
            if (_rootError is not null) _callbackErrors.Insert(0, _rootError);
            _completion.TrySetException(new AggregateException("Zone owner callbacks failed.", _callbackErrors));
        }
        else if (_rootError is OperationCanceledException canceled) _completion.TrySetCanceled(canceled.CancellationToken);
        else if (_rootError is not null) _completion.TrySetException(_rootError);
        else _completion.TrySetResult();
    }

    private void Finish(Task root)
    {
        VerifyAccess();
        try { root.GetAwaiter().GetResult(); }
        catch (Exception error) { _rootError = error; }
        lock (_gate)
        {
            _finished = true;
            _queue.CompleteAdding();
        }
    }

    private sealed class OwnerContext(ZoneSimulationThread owner) : SynchronizationContext
    {
        public override SynchronizationContext CreateCopy() => this;
        public override void Post(SendOrPostCallback callback, object? state) =>
            owner.PostContinuation(() => callback(state));
        public override void Send(SendOrPostCallback callback, object? state)
        {
            if (owner.HasAccess) { callback(state); return; }
            // CancellationToken.Register(useSynchronizationContext: true) can Send from the
            // cancelling thread. Keep this path available while cancellation cleanup is pumping.
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            owner.PostContinuation(() =>
            {
                try { callback(state); completion.TrySetResult(); }
                catch (Exception error) { completion.TrySetException(error); }
            });
            completion.Task.GetAwaiter().GetResult(); // Only the foreign caller waits.
        }
    }

    public readonly struct OwnerAwaitable(ZoneSimulationThread owner)
    {
        public Awaiter GetAwaiter() => new(owner);
        public readonly struct Awaiter(ZoneSimulationThread owner) : ICriticalNotifyCompletion
        {
            public bool IsCompleted => owner.HasAccess;
            public void GetResult() => owner.VerifyAccess();
            public void OnCompleted(Action continuation) => owner.PostContinuation(continuation);
            public void UnsafeOnCompleted(Action continuation) => owner.PostContinuation(continuation);
        }
    }
}
