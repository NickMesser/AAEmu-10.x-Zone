using System.Collections.Concurrent;
using Neo.IronLua;

namespace AAEmu.Zone.Scripting;

public sealed partial class ZoneManagedLuaRuntime
{
    private readonly HashSet<OwnedCoroutine> _coroutines = [];
    [ThreadStatic] private static OwnedCoroutine? _runningCoroutine;
    public int LiveContinuationCount { get { if (IsCallbackThread) return _coroutines.Count; lock (_gate) return _coroutines.Count; } }

    private void InstallCoroutines()
    {
        var library = (LuaTable)_globals.GetValue("coroutine", true);
        library.SetValue("create", new Func<object, object>(CreateCoroutine), true);
        library.SetValue("resume", new Func<object, object?[], LuaResult>(ResumeCoroutine), true);
        library.SetValue("status", new Func<object, string>(CoroutineStatus), true);
        library.SetValue("running", new Func<LuaResult>(() => _runningCoroutine is { } active && ReferenceEquals(active.Owner, this)
            ? new LuaResult(active) : new LuaResult((object?)null)), true);
        library.SetValue("yield", new Func<object?[], LuaResult>(YieldCoroutine), true);
        library.SetValue("wrap", new Func<object, object>(function =>
        {
            var coroutine = CreateCoroutine(function);
            return new Func<object?[], LuaResult>(arguments =>
            {
                var result = ResumeCoroutine(coroutine, arguments);
                if (result[0] is not true) throw new LuaRuntimeException(result[1]?.ToString() ?? "coroutine failed", 1, true);
                return new LuaResult(result.Values.Skip(1).ToArray());
            });
        }), true);
        var originalType = _globals.GetValue("type", true);
        _globals.SetValue("type", new Func<object?, object?>(value => value is OwnedCoroutine
            ? "thread" : new LuaResult(Lua.RtInvoke(originalType, [value!]))[0]), true);
    }

    private OwnedCoroutine CreateCoroutine(object function)
    {
        if (function is not Delegate) throw new LuaRuntimeException("coroutine.create requires a function.", 1, true);
        return new OwnedCoroutine(this, function);
    }
    private LuaResult ResumeCoroutine(object value, object?[] arguments)
    {
        if (value is not OwnedCoroutine coroutine || !ReferenceEquals(coroutine.Owner, this))
            throw new LuaRuntimeException("coroutine.resume requires a thread from this runtime.", 1, true);
        return coroutine.Resume(arguments);
    }
    private string CoroutineStatus(object value)
    {
        if (value is not OwnedCoroutine coroutine || !ReferenceEquals(coroutine.Owner, this))
            throw new LuaRuntimeException("coroutine.status requires a thread from this runtime.", 1, true);
        return coroutine.Status;
    }
    private LuaResult YieldCoroutine(object?[] arguments)
    {
        if (_runningCoroutine is not { } coroutine || !ReferenceEquals(coroutine.Owner, this))
            throw new LuaRuntimeException("attempt to yield from outside a coroutine", 1, true);
        return coroutine.Yield(arguments);
    }
    private void RetireCoroutines()
    { foreach (var coroutine in _coroutines.ToArray()) coroutine.Dispose(); _coroutines.Clear(); }

    /// <summary>
    /// Stackful source continuations, resumed serially by the owning VM. Completion publishes
    /// dead state before returning final values; suspended workers are cancelled on VM disposal.
    /// </summary>
    private sealed class OwnedCoroutine(ZoneManagedLuaRuntime owner, object function) : IDisposable
    {
        private readonly object _stateGate = new();
        private BlockingCollection<object?[]>? _resumes;
        private BlockingCollection<LuaResult>? _results;
        private CancellationTokenSource? _retirement;
        private Thread? _worker;
        private bool _running, _completed, _retired;
        internal ZoneManagedLuaRuntime Owner { get; } = owner;
        internal string Status
        {
            get
            {
                lock (_stateGate)
                    return _completed || _retired ? "dead" : _running
                        ? ReferenceEquals(_runningCoroutine, this) ? "running" : "normal" : "suspended";
            }
        }

        internal LuaResult Resume(object?[] arguments)
        {
            lock (_stateGate)
            {
                if (_retired || _completed) return new(false, "cannot resume dead coroutine");
                if (_running) return new(false, "cannot resume non-suspended coroutine");
                _running = true;
                if (_worker is null)
                {
                    _resumes = new(1); _results = new(1); _retirement = new();
                    Owner._coroutines.Add(this);
                    _worker = new Thread(Run) { IsBackground = true, Name = "Zone Lua continuation" };
                    _resumes.Add(arguments);
                    _worker.Start();
                }
                else _resumes!.Add(arguments);
            }
            var result = _results!.Take(_retirement!.Token);
            // The Lua thread object may remain in a script table. It keeps only its dead
            // state after completion, rather than rooting functions, workers and queues.
            if (_completed) Dispose();
            return result;
        }

        private void Run()
        {
            var previous = _runningCoroutine;
            _runningCoroutine = this;
            (_callbackScopes ??= new()).Push(Owner);
            try
            {
                var arguments = _resumes!.Take(_retirement!.Token);
                var result = new LuaResult(Lua.RtInvoke(function, arguments!));
                lock (_stateGate) { _running = false; _completed = true; }
                _results!.Add(new LuaResult(new object?[] { true }.Concat(result.Values).ToArray()!), _retirement.Token);
            }
            catch (OperationCanceledException) when (_retired) { }
            catch (Exception error)
            {
                lock (_stateGate) { _running = false; _completed = true; }
                if (!_retired) _results!.Add(new(false, VmString(error.Message)), _retirement!.Token);
            }
            finally
            {
                _callbackScopes.Pop(); _runningCoroutine = previous;
            }
        }

        internal LuaResult Yield(object?[] arguments)
        {
            lock (_stateGate) _running = false;
            _results!.Add(new LuaResult(new object?[] { true }.Concat(arguments).ToArray()!), _retirement!.Token);
            var resumed = _resumes!.Take(_retirement.Token);
            return new(resumed!);
        }

        public void Dispose()
        {
            lock (_stateGate) { if (_retired) return; _retired = true; _running = false; }
            _retirement?.Cancel();
            // Only suspended/completed coroutines can exist outside an active VM call.
            // A cancelled yield throws before returning control to its source continuation.
            if (_worker is { } worker && !worker.Join(TimeSpan.FromSeconds(2)))
                throw new InvalidOperationException("A managed Lua continuation did not unwind during retirement.");
            _resumes?.Dispose(); _results?.Dispose(); _retirement?.Dispose();
            _resumes = null; _results = null; _retirement = null;
            function = null!; _worker = null;
            Owner._coroutines.Remove(this);
        }
    }
}
