using System.Collections.ObjectModel;
using System.Text;

using AAEmu.Zone.Ai;

namespace AAEmu.Zone.Npcs;

/// <summary>Native AI actor signal storage, proxy draining and auxiliary behavior delay state.</summary>
public sealed class ZoneNpcAiSignals
{
    private readonly ZoneNpc _npc;
    private readonly IZoneNpcWorld _world;
    private readonly IZoneNpcAiRuntime _runtime;
    private readonly List<ZoneAiSignal> _pending = [];
    private bool _disposed;
    private bool _updating;
    private uint _actorGeneration;
    private ZoneAiAuxiliaryRequest? _auxiliaryRequest;
    private string _auxiliaryName = "";
    private float _remainingDelay;
    private int _auxiliaryKind, _auxiliaryPriority, _ignoredRequests;
    private bool _auxiliaryStarted;

    public IReadOnlyList<ZoneAiSignal> Pending { get; }
    internal bool IsDisposed => _disposed;
    internal uint ActorGeneration => _actorGeneration;
    public ZoneAiAuxiliaryRequest? AuxiliaryRequest => _auxiliaryRequest;
    public ZoneAiAuxiliaryState Auxiliary => new(_auxiliaryName, _remainingDelay, _auxiliaryKind,
        _auxiliaryPriority, _auxiliaryStarted, _ignoredRequests);

    internal ZoneNpcAiSignals(ZoneNpc npc, IZoneNpcWorld world)
    {
        _npc = npc; _world = world; _runtime = world.AiRuntime;
        Pending = new ReadOnlyCollection<ZoneAiSignal>(_pending);
    }

    /// <summary>390B30D0 / CAISystem filter 0: send to this NPC with its own actor entity as sender.</summary>
    public void Send(string name, int kind = 0, ZoneAiSignalData? data = null)
    {
        if (_disposed || !_world.HasSignalActor(_npc) || _runtime.GetEntityId(_npc.Unit) is not { } sender) return;
        Submit(kind, name, sender, data);
    }

    /// <summary>3100756C: actor-local admission after AI routing has selected this recipient.</summary>
    public void Submit(int kind, string name, uint senderEntityId, ZoneAiSignalData? data = null, uint crc = 0)
    {
        if (_disposed) return;
        name = ZoneAiSignalName.CString(name);
        // Native storage is char[50]. Reject unterminated input instead of reading beyond that array.
        if (Encoding.UTF8.GetByteCount(name) > 49) throw new ArgumentException("AI signal names must fit in 49 UTF-8 bytes.", nameof(name));
        if (crc == 0) crc = ZoneAiSignalName.Crc(name);
        if (kind is not (9 or 10) && _pending.Any(signal => signal.Crc == crc)) return;
        if (kind is not (0 or 9 or 10) && !_runtime.IsEnabled(_npc)) return;
        var signal = new ZoneAiSignal(kind, name, crc, senderEntityId, data);
        var generation = _actorGeneration;
        if (kind != 30) _runtime.SignalReceived(_npc, name);
        if (!IsActive(generation) || kind == 9) return;
        // Constructor remains at the end so that the proxy's reverse drain executes it first.
        var index = _pending.Count;
        if (index != 0 && ZoneAiSignalName.IsConstructor(_pending[^1].Name)) index--;
        _pending.Insert(index, signal);
    }

    internal bool CancelPendingTalk()
    {
        if (_disposed || !_world.HasSignalActor(_npc) || !_runtime.HasPipeUser(_npc)) return false;
        var cancelled = false;
        // 39342EF0 increments after erasure: an adjacent OnTalk shifts into the skipped slot.
        for (var index = 0; index < _pending.Count; index++)
            if (_pending[index].Name == "OnTalk") { _pending.RemoveAt(index); cancelled = true; }
        return cancelled;
    }

    public void RemoveSender(uint entityId)
    {
        // 31006E0C / 31170B2C erase every reference to the removed sender, including adjacent rows.
        if (entityId != 0) _pending.RemoveAll(signal => signal.SenderEntityId == entityId);
    }

    public void RequestAuxiliary(ZoneAiAuxiliaryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_disposed) _auxiliaryRequest = request with { Name = ZoneAiSignalName.CString(request.Name) };
    }

    /// <summary>30518D30: called in the actor proxy phase, separately from the NPC manager's frame schedule.</summary>
    internal void Update()
    {
        if (_disposed) return;
        if (_updating) throw new InvalidOperationException("An actor signal update cannot recursively drain itself.");
        _updating = true;
        var generation = _actorGeneration;
        try
        {
            if (_runtime.HasSignalHandler(_npc)) _runtime.RunPendingConstructors(_npc);
            if (!IsActive(generation)) return;
            if (_runtime.CanReceiveSignals(_npc)) DrainAll(generation);
            else DrainForced(generation);
            if (IsActive(generation)) UpdateAuxiliary(generation);
        }
        finally { _updating = false; }
    }

    private bool IsActive(uint generation) => !_disposed && generation == _actorGeneration;

    private void DrainAll(uint generation)
    {
        var deferred = new List<ZoneAiSignal>();
        while (IsActive(generation) && _pending.Count != 0)
        {
            var signal = _pending[^1]; _pending.RemoveAt(_pending.Count - 1);
            if (signal.Kind == 3) deferred.Add(signal with { Kind = 30 });
            else Deliver(signal, generation);
        }
        // Append directly; a deferred signal is neither re-admitted nor notified a second time.
        if (IsActive(generation)) _pending.AddRange(deferred);
    }

    private void DrainForced(uint generation)
    {
        var count = _pending.Count;
        for (var index = count; IsActive(generation) && index != 0;)
        {
            var signal = _pending[--index];
            if (signal.Kind != -100) continue;
            _pending.RemoveAt(index); Deliver(signal, generation);
            count--;
            // Native restarts its reverse traversal if a callback changes the expected queue size.
            if (count != _pending.Count) count = index = _pending.Count;
        }
    }

    private void Deliver(ZoneAiSignal signal, uint generation)
    {
        if (!_runtime.HasSignalHandler(_npc)) return;
        _runtime.DispatchScriptSignal(_npc, signal);
        if (!IsActive(generation)) return;
        // 30511450 resolves the extra-data entity AFTER script callbacks, never the sender entity.
        var target = _runtime.GetUnitObjectId(unchecked((uint)(signal.Data?.EntityId ?? 0)));
        if (IsActive(generation)) _npc.Signals.Process(target, signal.Name);
    }

    private void UpdateAuxiliary(uint generation)
    {
        if (!_runtime.HasSignalHandler(_npc)) return;
        if (_auxiliaryRequest is { Kind: not 0 } request)
        {
            if (_auxiliaryName.Length == 0 || (_auxiliaryPriority < request.Priority && request.Priority < 100))
            {
                _auxiliaryName = request.Name; _remainingDelay = request.Delay;
                _auxiliaryKind = request.Kind; _auxiliaryPriority = request.Priority;
                _auxiliaryStarted = false; _ignoredRequests = 0;
            }
            else _ignoredRequests = unchecked(_ignoredRequests + 1);
            _auxiliaryRequest = null;
        }
        if (_auxiliaryName.Length == 0) return;
        if (!_auxiliaryStarted)
        {
            // 30518A3A compares zero to the delay with COMISS/JAE: an unordered delay keeps waiting.
            if (!(_remainingDelay <= 0)) { _remainingDelay -= _runtime.FrameTimeSeconds; return; }
            var started = _runtime.StartAuxiliaryBehavior(_npc, _auxiliaryName, _auxiliaryKind);
            if (!IsActive(generation)) return;
            if (started) { _auxiliaryStarted = true; return; }
        }
        else if (!_runtime.IsAuxiliaryBehaviorFinished(_npc) || !IsActive(generation)) return;
        _auxiliaryName = ""; _remainingDelay = 0; _auxiliaryPriority = 0; _auxiliaryKind = 0; _auxiliaryStarted = false;
    }

    internal void Dispose()
    {
        if (_disposed) return;
        _disposed = true; ActorRemoved();
    }

    internal void ActorRemoved()
    {
        _actorGeneration = unchecked(_actorGeneration + 1);
        _pending.Clear(); _auxiliaryRequest = null;
        _auxiliaryName = ""; _remainingDelay = 0; _auxiliaryKind = 0; _auxiliaryPriority = 0;
        _auxiliaryStarted = false; _ignoredRequests = 0;
        _runtime.ReleaseScriptState(_npc);
    }
}
