using AAEmu.Zone.Actors;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.Scripting;

/// <summary>
/// CActor392D7400/392D7460 read their associated Unit, or100 before either association exists.
/// The dedicated profile owns the server-Unit104 producer; client-Unit112 is a separate profile.
/// Health stays signed64 throughout. The unrelated stats160/184 are water state.
/// </summary>
public sealed class ZoneManagedNpcActorHealth
{
    private ZoneUnit? _expectedUnit, _serverUnit;
    private Func<bool>? _current;
    public bool HasServerUnit => _serverUnit is not null;
    public ZoneManagedNpcActorHealth() { }
    internal ZoneManagedNpcActorHealth(ZoneUnit expectedUnit, Func<bool> current) => BindOwner(expectedUnit, current);
    internal void BindOwner(ZoneUnit expectedUnit, Func<bool> current)
    {
        ArgumentNullException.ThrowIfNull(expectedUnit); ArgumentNullException.ThrowIfNull(current);
        if (_expectedUnit is not null && !ReferenceEquals(expectedUnit, _expectedUnit))
            throw new InvalidOperationException("CActor health cannot bind a different Unit allocation.");
        _expectedUnit = expectedUnit; _current ??= current;
    }
    /// <summary>392E0190: call only at the actual server-Unit association event, independently of Lua construction.</summary>
    public void AssociateServerUnit(ZoneUnit unit)
    {
        Require();
        if (_expectedUnit is null || !ReferenceEquals(unit, _expectedUnit))
            throw new InvalidOperationException("The CActor server-Unit association must retain its exact owned Unit.");
        _serverUnit = unit;
    }
    public long CurrentHealth
    {
        get
        {
            Require();
            return _serverUnit is null ? 100 : ReadCurrent(_serverUnit.PreciseHealth);
        }
    }
    public long MaximumHealth
    {
        get
        {
            Require();
            if (_serverUnit is null) return 100;
            return _serverUnit.Stats is { IsComplete: true } stats ? stats.MaximumHealth :
                throw new NotSupportedException("Associated CActor.GetMaxHealth requires the Unit's complete native stat6 owner.");
        }
    }
    internal static long ReadCurrent(long preciseHealth) => preciseHealth / 100 + (preciseHealth % 100 != 0 ? 1L : 0L);
    private void Require()
    { if (_current is not null && !_current()) throw new ObjectDisposedException(nameof(ZoneManagedNpcActorHealth), "The exact CActor/Unit health generation retired."); }
}

public sealed record ZoneManagedNpcScriptFault(ZoneActorHandle Actor, string Callback, string Message, bool RetireActor = true);

public readonly record struct ZoneManagedNpcScriptEffectState(long CurrentHealth, long MaximumHealth,
    float CombatInterval, float PeaceInterval, float SelectedInterval, float AccumulatedTime,
    int Timers, bool HasUpdate, bool Faulted, string? Failure);

/// <summary>
/// Generation-owned entity timers, default-state dedicated-server script updates, and copied
/// Unit-backed actor health. Other IZoneManagedNpcScriptServices effects keep their named refusals.
/// The scene supplies the game clock and invokes Tick at its documented frame boundary.
/// This owns Lua references, never the runtime, actor, or entity binding.
/// </summary>
public sealed class ZoneManagedNpcScriptEffects : IZoneManagedNpcScriptServices, IZoneManagedNpcScriptLifecycle, IDisposable
{
    private readonly IZoneLuaRuntime _lua;
    private readonly Func<long> _gameMicroseconds;
    private readonly Action<string>? _trace;
    private readonly bool _preserveNativeScriptErrors;
    private readonly Dictionary<ZoneActorHandle, ZoneManagedNpcScriptEffectsLease> _actors = [];
    private readonly SortedDictionary<(long Deadline, long Sequence), Timer> _timers = [];
    private readonly Queue<ZoneManagedNpcScriptFault> _faults = [];
    private long _sequence;
    private bool _ticking;
    public bool IsDisposed { get; private set; }
    public int Count => _actors.Count;
    public int TimerCount => _timers.Count;
    public ulong ProtectedErrorCount { get; private set; }
    public IReadOnlyList<ZoneManagedNpcScriptFault> Faults => _faults.ToArray();

    public ZoneManagedNpcScriptEffects(IZoneLuaRuntime lua, Func<long> gameMicroseconds, Action<string>? trace = null,
        bool preserveNativeScriptErrors = false)
    {
        _lua = lua ?? throw new ArgumentNullException(nameof(lua)); _gameMicroseconds = gameMicroseconds ?? throw new ArgumentNullException(nameof(gameMicroseconds));
        _trace = trace; _preserveNativeScriptErrors = preserveNativeScriptErrors;
    }

    IDisposable IZoneManagedNpcScriptLifecycle.BindEntity(ZoneManagedNpcScriptBinding binding) => BindEntity(binding, new());
    IReadOnlyList<ZoneManagedNpcScriptFault> IZoneManagedNpcScriptLifecycle.Tick(TimeSpan elapsed) => Tick(elapsed);

    /// <summary>
    /// Call after exact registry adoption and before original OnInit. Only default script state
    /// is admitted: no GoToState/reset event, client callbacks, or inferred combat event is supplied.
    /// Functions are retained as the original entity class retains callback references.
    /// </summary>
    public ZoneManagedNpcScriptEffectsLease BindEntity(ZoneManagedNpcScriptBinding binding, ZoneManagedNpcActorHealth health)
    {
        Check(); ArgumentNullException.ThrowIfNull(binding); ArgumentNullException.ThrowIfNull(health);
        binding.RequireCurrent();
        if (!ReferenceEquals(binding.Script.Runtime, _lua)) throw new ArgumentException("Entity effects require their own exact Lua runtime.");
        health.BindOwner(binding.Actor.Npc.Unit, () => binding.IsCurrent);
        if (_actors.TryGetValue(binding.Actor.Handle, out var previous))
        {
            if (previous.IsCurrent) throw new InvalidOperationException("This actor generation already owns script effects.");
            previous.Dispose();
        }
        ZoneLuaReference? timer = null, update = null;
        try
        {
            timer = Callback(binding.Script, "Server", "OnTimer");
            update = Callback(binding.Script, "Server", "OnUpdate");
            using var clientUpdate = Callback(binding.Script, "Client", "OnUpdate");
            var owner = new ZoneManagedNpcScriptEffectsLease(this, binding, health, timer, update, update is not null || clientUpdate is not null);
            _actors.Add(binding.Actor.Handle, owner); timer = null; update = null;
            return owner;
        }
        finally { timer?.Dispose(); update?.Dispose(); }
    }

    public long GetActorHealth(ZoneManagedNpcActor actor) => Require(actor).Health.CurrentHealth;
    public long GetActorMaximumHealth(ZoneManagedNpcActor actor) => Require(actor).Health.MaximumHealth;

    //32033420 forwards both float parameters to script-proxy virtual+72/320732C0.
    //That setter writes+40/+44 only; it does not select a new current interval at+36.
    public void SetScriptUpdateRate(ZoneManagedNpcActor actor, float combatInterval, float peaceInterval)
    {
        var owner = Require(actor);
        if (!float.IsFinite(combatInterval) || !float.IsFinite(peaceInterval)) throw new ArgumentException("Script update intervals must be finite.");
        owner.CombatInterval = combatInterval; owner.PeaceInterval = peaceInterval;
    }

    /// <summary>Exact script-proxy event44 param0 selection; the scene must own its actual producer.</summary>
    public void SelectScriptUpdateInterval(ZoneManagedNpcActor actor, bool firstInterval)
    {
        var owner = Require(actor); owner.SelectedInterval = firstInterval ? owner.CombatInterval : owner.PeaceInterval;
    }

    public void SetEntityTimer(ZoneManagedNpcActor actor, int timerId, int milliseconds)
    {
        var owner = Require(actor);
        //320103E0 first invokes KillTimer with the unchanged EDX timer ID.32020040 stores
        //signed milliseconds and adds1000LL*milliseconds to the game clock's microseconds.
        Kill(owner, timerId);
        var key = (unchecked(_gameMicroseconds() + 1000L * milliseconds), checked(++_sequence));
        _timers.Add(key, new(owner, timerId, milliseconds)); owner.Timers.Add(timerId, key);
    }

    public void KillEntityTimer(ZoneManagedNpcActor actor, int timerId) => Kill(Require(actor), timerId);

    public ZoneManagedNpcScriptEffectState CopyState(ZoneManagedNpcActor actor)
    {
        var owner = Find(actor);
        return new(owner.Health.CurrentHealth, owner.Health.MaximumHealth, owner.CombatInterval, owner.PeaceInterval,
            owner.SelectedInterval, owner.AccumulatedTime, owner.Timers.Count, owner.HasUpdate, owner.IsFaulted, owner.Failure);
    }

    /// <summary>
    ///320217A0 removes all timers with deadline&lt;=now before dispatching entity event1. Equal
    ///deadlines preserve insertion order; callbacks may not cancel another already-copied event.
    ///Timers scheduled from callbacks wait until the next Tick.32071E70 updates only when the
    ///accumulated float time is strictly greater than the selected interval, once per frame.
    ///Returned faults must retire their exact generations; faulted owners receive no more calls.
    ///The caller owns the replacement frame ordering relative to AI and physics.
    /// </summary>
    public IReadOnlyList<ZoneManagedNpcScriptFault> Tick(TimeSpan elapsed, bool scriptsEnabled = true)
    {
        Check();
        if (elapsed < TimeSpan.Zero || !float.IsFinite((float)elapsed.TotalSeconds)) throw new ArgumentOutOfRangeException(nameof(elapsed));
        if (_ticking) throw new InvalidOperationException("Entity script effects cannot tick recursively.");
        _ticking = true;
        List<ZoneManagedNpcScriptFault> faults = [];
        try
        {
            foreach (var retired in _actors.Values.Where(owner => !owner.Binding.IsCurrent).ToArray()) retired.Dispose();
            var now = _gameMicroseconds();
            var due = _timers.TakeWhile(pair => pair.Key.Deadline <= now).ToArray();
            foreach (var item in due)
            {
                _timers.Remove(item.Key);
                //A negative ID cancels all at scheduling time, but is itself a valid stored ID.
                if (item.Value.Owner.Timers.TryGetValue(item.Value.Id, out var current) && current == item.Key)
                    item.Value.Owner.Timers.Remove(item.Value.Id);
            }
            foreach (var item in due)
            {
                var timer = item.Value;
                if (timer.Owner.IsCurrent && timer.Owner.OnTimer is { } function)
                    Dispatch(timer.Owner, "Server.OnTimer", function, [(float)timer.Id, (float)timer.Milliseconds], faults);
            }
            if (scriptsEnabled)
            {
                var seconds = (float)elapsed.TotalSeconds;
                foreach (var owner in _actors.Values.ToArray())
                {
                    if (!owner.IsCurrent || !owner.HasUpdate || !owner.Binding.Actor.IsEntityScriptUpdateEligible) continue;
                    owner.AccumulatedTime = seconds + owner.AccumulatedTime;
                    if (owner.AccumulatedTime <= owner.SelectedInterval) continue;
                    if (owner.OnUpdate is { } function)
                        Dispatch(owner, "Server.OnUpdate", function, [owner.AccumulatedTime], faults);
                    owner.AccumulatedTime = 0;
                }
            }
        }
        finally { _ticking = false; }
        return faults.ToArray();
    }

    private void Dispatch(ZoneManagedNpcScriptEffectsLease owner, string name, ZoneLuaReference function,
        object?[] arguments, List<ZoneManagedNpcScriptFault> faults)
    {
        try { owner.Binding.InvokeCallback(function, arguments); }
        catch (Exception error)
        {
            var retire = !_preserveNativeScriptErrors || error is not ZoneLuaException { IsHostFailure: false };
            if (retire)
            {
                owner.IsFaulted = true; owner.Failure = $"{name}: {error.Message}";
                Kill(owner, -1);
            }
            else
            {
                //32073E20 ignores protected EndCall's false return. Preserve owner and
                //timers;32071E70 still resets the accumulator after this dispatch returns.
                ProtectedErrorCount++;
                var key = $"{name}: {error.Message}";
                if (owner.LastReportedProtectedError == key) return;
                owner.LastReportedProtectedError = key;
            }
            var fault = new ZoneManagedNpcScriptFault(owner.Binding.Actor.Handle, name, error.Message, retire);
            if (_faults.Count == 256) _faults.Dequeue(); _faults.Enqueue(fault); faults.Add(fault);
            _trace?.Invoke($"Managed NPC script {fault.Actor} {(retire ? "refused" : "reported protected error in")} {name}: {error.Message}");
        }
    }

    private ZoneLuaReference? Callback(ZoneLuaReference entity, string side, string name)
    { using var table = _lua.Get(entity, side, 5) as ZoneLuaReference; return table is null ? null : _lua.Get(table, name, 6) as ZoneLuaReference; }
    private ZoneManagedNpcScriptEffectsLease Require(ZoneManagedNpcActor actor)
    {
        var owner = Find(actor);
        if (owner.IsFaulted) throw new InvalidOperationException($"Entity script effects are faulted: {owner.Failure}");
        return owner;
    }
    private ZoneManagedNpcScriptEffectsLease Find(ZoneManagedNpcActor actor)
    {
        Check(); ArgumentNullException.ThrowIfNull(actor);
        if (!_actors.TryGetValue(actor.Handle, out var owner) || !ReferenceEquals(owner.Binding.Actor, actor) ||
            owner.IsDisposed || !owner.Binding.IsCurrent)
            throw new InvalidOperationException("Entity script effects require their exact current adopted actor generation.");
        return owner;
    }
    private void Kill(ZoneManagedNpcScriptEffectsLease owner, int timerId)
    {
        if (timerId < 0)
        {
            foreach (var key in owner.Timers.Values) _timers.Remove(key); owner.Timers.Clear();
        }
        else if (owner.Timers.Remove(timerId, out var key)) _timers.Remove(key);
    }
    internal void Retire(ZoneManagedNpcScriptEffectsLease owner)
    {
        Kill(owner, -1);
        if (_actors.TryGetValue(owner.Binding.Actor.Handle, out var current) && ReferenceEquals(current, owner))
            _actors.Remove(owner.Binding.Actor.Handle);
    }
    private void Check() { ObjectDisposedException.ThrowIf(IsDisposed, this); ObjectDisposedException.ThrowIf(_lua.IsDisposed, _lua); }
    public void Dispose()
    {
        if (IsDisposed) return; IsDisposed = true;
        foreach (var owner in _actors.Values.ToArray()) owner.Dispose(); _timers.Clear();
    }
    private sealed record Timer(ZoneManagedNpcScriptEffectsLease Owner, int Id, int Milliseconds);
}

/// <summary>Revocable script-proxy ownership; retirement cancels timers and releases retained callbacks.</summary>
public sealed class ZoneManagedNpcScriptEffectsLease : IDisposable
{
    private readonly ZoneManagedNpcScriptEffects _owner;
    internal ZoneManagedNpcScriptBinding Binding { get; }
    internal ZoneLuaReference? OnTimer { get; }
    internal ZoneLuaReference? OnUpdate { get; }
    internal Dictionary<int, (long Deadline, long Sequence)> Timers { get; } = [];
    internal float CombatInterval { get; set; } = .05f;
    internal float PeaceInterval { get; set; } = 1f / 3f;
    internal float SelectedInterval { get; set; } = 1f / 3f;
    internal float AccumulatedTime { get; set; } = 10f;
    internal bool HasUpdate { get; }
    internal string? LastReportedProtectedError;
    public ZoneActorHandle Actor => Binding.Actor.Handle;
    public ZoneManagedNpcActorHealth Health { get; }
    public bool IsDisposed { get; private set; }
    public bool IsFaulted { get; internal set; }
    public string? Failure { get; internal set; }
    public bool IsCurrent => !IsDisposed && !IsFaulted && !_owner.IsDisposed && Binding.IsCurrent;
    internal ZoneManagedNpcScriptEffectsLease(ZoneManagedNpcScriptEffects owner, ZoneManagedNpcScriptBinding binding,
        ZoneManagedNpcActorHealth health, ZoneLuaReference? timer, ZoneLuaReference? update, bool hasUpdate)
    { _owner = owner; Binding = binding; Health = health; OnTimer = timer; OnUpdate = update; HasUpdate = hasUpdate; }
    public void Dispose()
    {
        if (IsDisposed) return; IsDisposed = true; _owner.Retire(this); OnTimer?.Dispose(); OnUpdate?.Dispose();
    }
}
