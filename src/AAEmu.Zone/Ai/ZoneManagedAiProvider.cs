using System.Runtime.CompilerServices;
using System.Text;

using AAEmu.Zone.Actors;

namespace AAEmu.Zone.Ai;

/// <summary>One generation's managed behavior. The provider owns and retires the returned instance.</summary>
public interface IZoneManagedAiActor : IDisposable
{
    void SetActive(bool active);
    void SetTarget(ZoneActorHandle? target);
    void SendSignal(ZoneAiSignal signal);
    void Update(uint timeMilliseconds, float elapsedSeconds);
}

public sealed record ZoneManagedAiBindingState(bool Active, ZoneActorHandle? Target,
    TimeSpan? UpdatePeriod, uint? NextUpdateMilliseconds);

/// <summary>
/// Native-free NPC admission, signal routing and deterministic behavior scheduling. Perception,
/// navigation and combat are supplied by each actor's managed behavior; this provider does not
/// pretend that registering an idle actor implements those systems. Calls run on the scene thread.
/// </summary>
public sealed class ZoneManagedAiProvider : IZoneAiProvider, IZoneAiFramePump, IDisposable
{
    private static readonly object BehaviorGate = new();
    private static readonly object BehaviorMarker = new();
    // Retired behavior state stays claimed weakly: outstanding goal leases must never mutate a new generation.
    private static readonly ConditionalWeakTable<IZoneManagedAiActor, object> KnownBehaviors = new();
    private readonly Func<ZoneActorHandle, ZoneActorKind> _getKind;
    private readonly Func<ZoneActorHandle, IZoneManagedAiActor> _createActor;
    private readonly Dictionary<ZoneActorHandle, Binding> _bindings = [];
    private readonly HashSet<ZoneActorHandle> _retired = [];
    private bool _disposed, _pumping;

    public int Count => _bindings.Count;

    /// <param name="getKind">Resolves live scene generations; throws InvalidOperationException for retired or unknown handles.</param>
    /// <param name="createActor">Constructs fresh behavior state for an admitted NPC. Ownership transfers on return.</param>
    public ZoneManagedAiProvider(Func<ZoneActorHandle, ZoneActorKind> getKind,
        Func<ZoneActorHandle, IZoneManagedAiActor> createActor)
    {
        _getKind = getKind ?? throw new ArgumentNullException(nameof(getKind));
        _createActor = createActor ?? throw new ArgumentNullException(nameof(createActor));
    }

    public void CreateNpc(ZoneActorHandle actor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_getKind(actor) != ZoneActorKind.Npc)
            throw new ArgumentException("Managed AI can bind only live NPC generations.", nameof(actor));
        if (_retired.Contains(actor))
            throw new InvalidOperationException($"AI generation {actor} has retired; re-entry requires a fresh generation.");
        if (!ZoneAiBackendBindings.TryClaim(actor, this))
            throw new InvalidOperationException($"AI generation {actor} already has a backend owner.");
        var binding = new Binding();
        _bindings.Add(actor, binding);
        try
        {
            var behavior = _createActor(actor) ?? throw new InvalidOperationException("An AI actor factory returned null.");
            lock (BehaviorGate)
            {
                if (KnownBehaviors.TryGetValue(behavior, out _))
                    throw new InvalidOperationException("Each AI generation requires fresh behavior state; this instance already has an owner.");
                KnownBehaviors.Add(behavior, BehaviorMarker);
            }
            try
            {
                _getKind(actor);
                // Construction callbacks can remove the NPC or dispose the scene. Never publish it again.
                if (_disposed || !IsCurrent(actor, binding))
                    throw new InvalidOperationException($"AI generation {actor} retired during construction.");
            }
            catch
            {
                behavior.Dispose();
                throw;
            }
            binding.Behavior = behavior;
        }
        catch
        {
            _bindings.Remove(actor);
            ZoneAiBackendBindings.Release(actor, this);
            throw;
        }
    }

    public void RemoveNpc(ZoneActorHandle actor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Retire(actor);
    }

    private void Retire(ZoneActorHandle actor)
    {
        if (!_bindings.Remove(actor, out var binding)) return;
        _retired.Add(actor);
        List<Exception>? errors = null;
        // Clear references before destruction callbacks; a later signal cannot observe a dead target.
        foreach (var (otherHandle, other) in _bindings.ToArray())
        {
            if (!IsCurrent(otherHandle, other) || other.Target != actor) continue;
            other.Target = null;
            try { other.Behavior?.SetTarget(null); }
            catch (Exception error) { (errors ??= []).Add(error); }
        }
        try { binding.Behavior?.Dispose(); }
        catch (Exception error) { (errors ??= []).Add(error); }
        finally { ZoneAiBackendBindings.Release(actor, this); }
        if (errors is not null) throw new AggregateException("Managed NPC AI retirement failed.", errors);
    }

    public void SendSignal(ZoneActorHandle actor, ZoneAiSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        var binding = RequireBinding(actor);
        RefreshTarget(actor, binding);
        if (!IsCurrent(actor, binding))
            throw new InvalidOperationException($"AI generation {actor} retired while clearing its old target.");
        var name = ZoneAiSignalName.CString(signal.Name);
        if (Encoding.UTF8.GetByteCount(name) > 49)
            throw new ArgumentException("AI signal names must fit in 49 UTF-8 bytes.", nameof(signal));
        binding.Behavior!.SendSignal(signal with { Name = name, Crc = signal.Crc == 0 ? ZoneAiSignalName.Crc(name) : signal.Crc });
    }

    public void SetActive(ZoneActorHandle actor, bool active)
    {
        var binding = RequireBinding(actor);
        if (binding.Active == active) return;
        binding.Active = active;
        binding.ScheduleArmed = false;
        binding.Behavior!.SetActive(active);
    }

    public void SetTarget(ZoneActorHandle actor, ZoneActorHandle? target)
    {
        var binding = RequireBinding(actor);
        if (target is { } handle) _getKind(handle);
        if (!IsCurrent(actor, binding))
            throw new InvalidOperationException($"AI generation {actor} retired during target validation.");
        binding.Target = target;
        binding.Behavior!.SetTarget(target);
    }

    public void SetUpdateSchedule(ZoneActorHandle actor, TimeSpan? period)
    {
        var binding = RequireBinding(actor);
        if (period is { } value && (value <= TimeSpan.Zero || value.TotalMilliseconds > int.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(period), "The cadence must be positive and fit the wrapping clock's half range.");
        binding.Period = period;
        // Sub-millisecond requests remain positive rather than becoming a zero-period busy loop.
        binding.PeriodMilliseconds = period is { } span ? (uint)Math.Ceiling(span.TotalMilliseconds) : 0;
        binding.ScheduleArmed = false;
    }

    public ZoneManagedAiBindingState GetState(ZoneActorHandle actor)
    {
        var binding = RequireBinding(actor);
        return new(binding.Active, binding.Target, binding.Period, binding.ScheduleArmed ? binding.NextDue : null);
    }

    public void Pump(uint timeMilliseconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pumping) throw new InvalidOperationException("Managed AI cannot recursively pump its frame clock.");
        _pumping = true;
        try
        {
            foreach (var (handle, binding) in _bindings.ToArray())
            {
                if (_disposed) return;
                if (!IsCurrent(handle, binding)) continue;
                try { _getKind(handle); }
                catch (InvalidOperationException) { Retire(handle); continue; }
                RefreshTarget(handle, binding);
                if (!IsCurrent(handle, binding) || !binding.Active || binding.Period is null) continue;
                if (!binding.ScheduleArmed)
                {
                    binding.NextDue = unchecked(timeMilliseconds + binding.PeriodMilliseconds);
                    binding.LastUpdate = timeMilliseconds;
                    binding.ScheduleArmed = true;
                    continue;
                }
                if (unchecked(timeMilliseconds - binding.NextDue) >= 0x80000000u) continue;
                var elapsed = unchecked(timeMilliseconds - binding.LastUpdate) * .001f;
                // Advance before calling behavior: callback cancellation, rescheduling or retirement wins.
                binding.NextDue = unchecked(timeMilliseconds + binding.PeriodMilliseconds);
                binding.LastUpdate = timeMilliseconds;
                binding.Behavior!.Update(timeMilliseconds, elapsed);
            }
        }
        finally { _pumping = false; }
    }

    private void RefreshTarget(ZoneActorHandle actor, Binding binding)
    {
        if (binding.Target is not { } target) return;
        try { _getKind(target); }
        catch (InvalidOperationException)
        {
            if (!IsCurrent(actor, binding)) return;
            binding.Target = null;
            binding.Behavior!.SetTarget(null);
        }
    }

    private bool IsCurrent(ZoneActorHandle actor, Binding binding) =>
        _bindings.TryGetValue(actor, out var current) && ReferenceEquals(current, binding);

    private Binding RequireBinding(ZoneActorHandle actor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _getKind(actor);
        if (_bindings.TryGetValue(actor, out var binding) && binding.Behavior is not null) return binding;
        throw new InvalidOperationException($"AI generation {actor} is unbound, retired or still being constructed.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        List<Exception>? errors = null;
        foreach (var actor in _bindings.Keys.ToArray())
            try { Retire(actor); }
            catch (Exception error) { (errors ??= []).Add(error); }
        if (errors is not null) throw new AggregateException("Managed AI disposal failed.", errors);
    }

    private sealed class Binding
    {
        public IZoneManagedAiActor? Behavior;
        public bool Active = true;
        public ZoneActorHandle? Target;
        public TimeSpan? Period;
        public uint PeriodMilliseconds, NextDue, LastUpdate;
        public bool ScheduleArmed;
    }
}
