using System.Numerics;
using System.Runtime.CompilerServices;

using AAEmu.Zone.Ai;
using AAEmu.Zone.Scripting;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.Npcs;

/// <summary>
/// Connection-owned NPC actor identities, Lua references, AI index memberships and goal phases.
/// All calls run on the owning scene thread. This adopts initialized actors; it does not reproduce
/// CPipeUser construction, create its persistent COPStick or schedule Lua, goals and physics together.
/// </summary>
public sealed class ZoneNpcActorRegistry : IZoneNpcActorRuntime, IZoneEntityGoalPipeServices, IDisposable
{
    private static readonly ConditionalWeakTable<object, object> Owners = new();
    private static readonly object OwnershipMarker = new();
    private static readonly object OwnershipGate = new();
    private readonly Dictionary<ZoneNpc, ZoneNpcActorEntry> _npcs = [];
    private readonly Dictionary<ZoneUnit, ZoneNpcActorEntry> _units = [];
    private readonly Dictionary<uint, ZoneNpcActorEntry> _entities = [];
    private readonly Dictionary<object, ZoneNpcActorEntry> _objects = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ZoneGoalPipeController, ZoneNpcActorEntry> _pipes = [];
    private readonly ConditionalWeakTable<ZoneUnit, object> _knownUnits = new();
    private readonly IZoneLuaRuntime _lua;
    private readonly ZoneAiObjectRegistry _index;
    private readonly IZoneNpcActorScene _scene;

    public ZoneNpcLuaRuntime AiRuntime { get; }
    public bool IsDisposed { get; private set; }
    public int Count => _npcs.Count;

    public ZoneNpcActorRegistry(IZoneLuaRuntime lua, ZoneAiObjectRegistry index, IZoneNpcActorScene scene)
    {
        _lua = lua ?? throw new ArgumentNullException(nameof(lua));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        ObjectDisposedException.ThrowIf(lua.IsDisposed, lua);
        AiRuntime = new(this, Dispose);
    }

    /// <summary>
    /// Transfers the phase and one new index registration on success; retains its own reference to the borrowed
    /// entity table. Failed adoption leaves the caller's phase and table owned by the caller. The supplied AI
    /// entity ID and actor components must remain stable while registered. Register behavior after adoption,
    /// before running its constructors, so Lua entity methods can resolve this controller during construction.
    /// Retired AI, control, navigation and continuation objects cannot be adopted again; use fresh actor state.
    /// The scene retains ownership of native auxiliary operations and generated reference/look-target objects.
    /// proxyAllocationKey is the fresh proxy's actual allocation key, never its NPC or entity ID. Adoption
    /// creates its activation countdown and cadence state; a retired proxy must receive a new entry.
    /// </summary>
    public ZoneNpcActorEntry Adopt(ZoneNpc npc, string name, short objectType, ZoneAiObjectState aiObject,
        ZoneActorGoalPhase phase, ulong proxyAllocationKey, ZoneLuaReference? entityScript)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ObjectDisposedException.ThrowIf(_lua.IsDisposed, _lua);
        ArgumentNullException.ThrowIfNull(npc); ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(aiObject); ArgumentNullException.ThrowIfNull(phase);
        ObjectDisposedException.ThrowIf(phase.IsDisposed, phase);
        ObjectDisposedException.ThrowIf(npc.AiSignals.IsDisposed, npc);
        ObjectDisposedException.ThrowIf(npc.Activation.IsDisposed, npc);
        var entityId = aiObject.EntityId;
        if (entityId == 0)
            throw new ArgumentException("An adopted actor requires its actual nonzero entity ID.", nameof(aiObject));
        if (entityScript is not null)
        {
            ObjectDisposedException.ThrowIf(entityScript.IsDisposed, entityScript);
            if (!entityScript.IsTable || !ReferenceEquals(entityScript.Runtime, _lua))
                throw new ArgumentException("The entity script must be a table in the owning Lua runtime.", nameof(entityScript));
            // 3202E630 accepts a handle-valued id, truncating its bits to the native entity ID.
            // Get is protected but __index can run managed callbacks; publish nothing until it returns.
            var id = _lua.Get(entityScript, "id", 2);
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            ObjectDisposedException.ThrowIf(_lua.IsDisposed, _lua);
            ObjectDisposedException.ThrowIf(phase.IsDisposed, phase);
            ObjectDisposedException.ThrowIf(npc.AiSignals.IsDisposed, npc);
            ObjectDisposedException.ThrowIf(npc.Activation.IsDisposed, npc);
            ObjectDisposedException.ThrowIf(entityScript.IsDisposed, entityScript);
            if (aiObject.EntityId != entityId)
                throw new InvalidOperationException("The actor's entity identity changed during script validation.");
            if (id is not ZoneLuaHandle handle || unchecked((uint)handle.Value) != entityId)
                throw new ArgumentException("The entity script requires a handle matching the actor's entity ID.", nameof(entityScript));
        }
        if (_npcs.ContainsKey(npc) || _entities.ContainsKey(aiObject.EntityId) || _units.ContainsKey(npc.Unit))
            throw new InvalidOperationException("An NPC, Unit or entity ID already has an actor owner.");

        var entry = new ZoneNpcActorEntry(this, npc, ZoneAiSignalName.CString(name), aiObject, phase, proxyAllocationKey);
        lock (OwnershipGate)
        {
            foreach (var component in entry.Components)
                if (Owners.TryGetValue(component, out _))
                    throw new InvalidOperationException("An actor component already belongs to another owner.");
            try
            {
                entry.Script = entityScript?.Retain();
                foreach (var component in entry.Components) Owners.Add(component, OwnershipMarker);
                entry.Registration = _index.Register(objectType, aiObject, aiObject);
                _npcs.Add(npc, entry); _units.Add(npc.Unit, entry); _entities.Add(entry.EntityId, entry);
                _objects.Add(aiObject, entry); _pipes.Add(phase.Pipes, entry);
                _knownUnits.GetValue(npc.Unit, _ => new object());
                entry.IsRegistered = true;
                npc.Diagnostics.InitializeActor(entry);
                return entry;
            }
            catch
            {
                _npcs.Remove(npc); _units.Remove(npc.Unit); _entities.Remove(entry.EntityId);
                _objects.Remove(aiObject); _pipes.Remove(phase.Pipes);
                entry.Registration?.Dispose(); entry.Script?.Dispose();
                foreach (var component in entry.Components) Owners.Remove(component);
                throw;
            }
        }
    }

    public ZoneNpcActorEntry? Find(ZoneNpc npc) =>
        !IsDisposed && _npcs.TryGetValue(npc, out var entry) && entry.IsActive ? entry : null;

    public ZoneNpcActorEntry? Find(uint entityId) =>
        !IsDisposed && _entities.TryGetValue(entityId, out var entry) && entry.IsActive ? entry : null;

    public bool IsEnabled(ZoneNpc npc) => Find(npc)?.AiObject.Enabled == true;
    public bool CanReceiveSignals(ZoneNpc npc) => Find(npc)?.Phase.Controls.SignalsEnabled == true;
    public bool HasPipeUser(ZoneNpc npc) => Find(npc) is not null;
    public bool HasSignalHandler(ZoneNpc npc)
    {
        var entry = Find(npc);
        return entry?.Script is not null && _scene.HasSignalHandler(entry) && entry.IsActive;
    }

    private IZoneActorEntityView? Entity(uint id)
    {
        if (IsDisposed || id == 0) return null;
        if (_entities.TryGetValue(id, out var owned)) return owned.IsActive ? owned : null;
        var external = _scene.FindExternalEntity(id);
        return external?.EntityId == id &&
            (external.Unit is not { } unit || !_knownUnits.TryGetValue(unit, out _)) ? external : null;
    }

    public uint? GetEntityId(ZoneUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (IsDisposed) return null;
        if (_units.TryGetValue(unit, out var entry)) return entry.IsActive ? entry.EntityId : null;
        if (_knownUnits.TryGetValue(unit, out _)) return null;
        var external = _scene.FindExternalEntity(unit);
        return external is { EntityId: not 0 } && ReferenceEquals(external.Unit, unit) ? external.EntityId : null;
    }
    public uint GetUnitObjectId(uint entityId) => Entity(entityId)?.Unit?.Snapshot.ObjectId ?? 0;
    public ZoneLuaReference? GetEntityScript(uint entityId) => Entity(entityId)?.RetainScript();
    public Vector3? GetEntityPosition(uint entityId) => Entity(entityId)?.GetPosition();
    internal Vector3? Position(ZoneNpcActorEntry entry)
    {
        if (!entry.IsActive) return null;
        var position = _scene.GetEntityPosition(entry);
        return entry.IsActive ? position : null;
    }

    internal bool ShouldUpdate(ZoneNpcActorEntry entry)
    {
        if (!entry.IsActive) return false;
        var alertness = _scene.GetActorAlertness(entry);
        // Native reads the distance band after alertness. A managed callback may retire this proxy.
        return entry.IsActive && entry.Activation.ShouldUpdate(() => alertness);
    }

    internal void UpdateEntityPolicy(ZoneNpcActorEntry entry)
    {
        if (!entry.IsActive) return;
        entry.Activation.UpdateEntityPolicy(() => _scene.GlobalAiUpdateAlways,
            policy => { if (entry.IsActive) _scene.SetEntityUpdatePolicy(entry, policy); });
    }

    internal bool SetUpdateAlways(ZoneNpcActorEntry entry, short count) => entry.IsActive &&
        entry.Activation.SetUpdateAlways(count, () => _scene.GlobalAiUpdateAlways,
            policy => { if (entry.IsActive) _scene.SetEntityUpdatePolicy(entry, policy); });

    internal void ActivationEvent(ZoneNpcActorEntry entry, int eventId)
    {
        if (eventId is not (6 or 7)) throw new ArgumentOutOfRangeException(nameof(eventId));
        if (entry.IsActive) _scene.ActorActivationEvent(entry, eventId);
    }

    public float FrameTimeSeconds
    { get { ObjectDisposedException.ThrowIf(IsDisposed, this); return _scene.FrameTimeSeconds; } }

    public void SignalReceived(ZoneNpc npc, string name)
    {
        if (Find(npc) is not { } entry) return;
        name = ZoneAiSignalName.CString(name);
        _scene.ActorSignalEvent(entry, name);
        if (!entry.IsActive) return;
        _scene.RecordSignal(entry, name);
        if (entry.IsActive) entry.Phase.Pipes.Executor.SignalWaits.Notify(name);
    }

    public bool StartAuxiliaryBehavior(ZoneNpc npc, string name, int kind)
    {
        if (Find(npc) is not { } entry) return false;
        var started = _scene.StartAuxiliaryBehavior(entry, ZoneAiSignalName.CString(name), kind);
        return entry.IsActive && started;
    }
    public bool IsAuxiliaryBehaviorFinished(ZoneNpc npc) =>
        Find(npc) is not { } entry || _scene.IsAuxiliaryBehaviorFinished(entry) || !entry.IsActive;

    public IZoneGoalPipeScriptEntity? FindEntity(uint entityId) => Entity(entityId);
    public ZoneGoalPipeController? GetPipeUser(object aiObject)
    {
        if (IsDisposed) return null;
        if (_objects.TryGetValue(aiObject, out var entry)) return entry.IsActive ? entry.Phase.Pipes : null;
        if (Owners.TryGetValue(aiObject, out _)) return null;
        return _scene.GetExternalPipeUser(aiObject);
    }
    public object? ResolvePipeTarget(ZoneGoalPipeController pipeUser, string name)
    {
        if (IsDisposed) return null;
        _pipes.TryGetValue(pipeUser, out var entry);
        if (entry is null && Owners.TryGetValue(pipeUser, out _)) return null;
        if (entry is { IsActive: false }) return null;
        var result = _scene.ResolvePipeTarget(pipeUser, ZoneAiSignalName.CString(name));
        return entry is null || entry.IsActive ? result : null;
    }
    public object? FindAiObject(string name) => IsDisposed ? null : _scene.FindAiObject(ZoneAiSignalName.CString(name));
    public void Warning(string message) { if (!IsDisposed) _scene.Warning(message); }

    /// <summary>
    /// Releases only this entry's resources. Invalidate admission and index membership before callbacks;
    /// clear sender references (31006E0C / 31170B2C) and the old actor generation before releasing its goals.
    /// This is the managed ownership boundary, not the full CPipeUser/native entity destructor.
    /// </summary>
    public bool Remove(ZoneNpcActorEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!ReferenceEquals(entry.Owner, this) || !entry.IsRegistered) return false;
        entry.IsRegistered = false;
        _npcs.Remove(entry.Npc); _units.Remove(entry.Npc.Unit); _entities.Remove(entry.EntityId);
        _objects.Remove(entry.AiObject); _pipes.Remove(entry.Phase.Pipes);
        entry.Registration!.Dispose();
        foreach (var other in _npcs.Values) other.Npc.AiSignals.RemoveSender(entry.EntityId);
        List<Exception>? errors = null;
        // NPC removal already disposes its queue before destroying the actor. Do not release its
        // script state again through that disposed owner. Live NPC actor replacement still resets it.
        if (!entry.Npc.AiSignals.IsDisposed) Cleanup(entry.Npc.AiSignals.ActorRemoved);
        Cleanup(() => AiRuntime.ReleaseScriptState(entry.Npc));
        Cleanup(entry.Phase.Dispose);
        Cleanup(() => entry.Script?.Dispose());
        entry.Script = null;
        // A goal can still hold an execution lease after Phase.Dispose returns. Actor state must never
        // be adopted by another generation: its old Execute/Reset can still mutate it. Weak claims
        // preserve this rule without retaining unreachable retired actors. The NPC/Unit may get fresh state.
        lock (OwnershipGate) { Owners.Remove(entry.Npc); Owners.Remove(entry.Npc.Unit); }
        if (errors is not null) throw new AggregateException("NPC actor cleanup failed.", errors);
        return true;

        void Cleanup(Action action)
        { try { action(); } catch (Exception error) { (errors ??= []).Add(error); } }
    }

    public bool Remove(ZoneNpc npc) => _npcs.TryGetValue(npc, out var entry) && Remove(entry);

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        List<Exception>? errors = null;
        foreach (var entry in _npcs.Values.ToArray())
            try { Remove(entry); } catch (Exception error) { (errors ??= []).Add(error); }
        try { AiRuntime.Dispose(); } catch (Exception error) { (errors ??= []).Add(error); }
        if (errors is not null) throw new AggregateException("NPC actor registry cleanup failed.", errors);
    }
}

/// <summary>One adopted actor. State and phase properties are borrowed; disposing the entry releases its owner.</summary>
public sealed class ZoneNpcActorEntry : IZoneActorEntityView, IZoneNpcActivationAi, IDisposable
{
    internal ZoneNpcActorRegistry Owner { get; }
    internal ZoneLuaReference? Script { get; set; }
    internal ZoneAiObjectRegistry.Registration? Registration { get; set; }
    internal object[] Components { get; }
    public ZoneNpc Npc { get; }
    public ZoneUnit Unit => Npc.Unit;
    public uint EntityId { get; }
    public string Name { get; }
    public ZoneAiObjectState AiObject { get; }
    public ZoneActorGoalPhase Phase { get; }
    public ZoneActorActivationState Activation { get; }
    public ulong ProxyAllocationKey { get; }
    public bool IsRegistered { get; internal set; }
    internal bool IsActive => IsRegistered && !Owner.IsDisposed && !Phase.IsDisposed &&
        !Npc.AiSignals.IsDisposed && AiObject.EntityId == EntityId;

    internal ZoneNpcActorEntry(ZoneNpcActorRegistry owner, ZoneNpc npc, string name,
        ZoneAiObjectState aiObject, ZoneActorGoalPhase phase, ulong proxyAllocationKey)
    {
        Owner = owner; Npc = npc; Name = name; AiObject = aiObject; EntityId = aiObject.EntityId; Phase = phase;
        ProxyAllocationKey = proxyAllocationKey; Activation = new(proxyAllocationKey);
        Components = [npc, npc.Unit, aiObject, phase, phase.Pipes, phase.Controls, phase.Navigation, phase.Continuation, Activation];
    }

    public object? GetAiObject() => IsActive ? AiObject : null;
    public Vector3? GetPosition() => Owner.Position(this);
    public ZoneLuaReference? RetainScript() => IsActive ? Script?.Retain() : null;
    public ZoneActorActivationState? GetActorActivation() => IsActive ? Activation : null;
    public void SendActivationEvent(int eventId) => Owner.ActivationEvent(this, eventId);
    /// <summary>Actor-proxy cadence only; the enclosing engine phase decides when to call it.</summary>
    public bool ShouldUpdate() => Owner.ShouldUpdate(this);
    public void UpdateEntityPolicy() => Owner.UpdateEntityPolicy(this);
    public bool SetUpdateAlways(short count) => Owner.SetUpdateAlways(this, count);
    public void Dispose() => Owner.Remove(this);
}
