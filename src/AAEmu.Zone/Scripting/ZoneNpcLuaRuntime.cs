using System.Numerics;

using AAEmu.Zone.Ai;
using AAEmu.Zone.Npcs;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.Scripting;

/// <summary>Connects actor signal queues to owned Lua behavior handlers.</summary>
public sealed class ZoneNpcLuaRuntime(IZoneNpcActorRuntime actors) : IZoneNpcAiRuntime, IDisposable
{
    private readonly IZoneNpcActorRuntime _actors = actors ?? throw new ArgumentNullException(nameof(actors));
    private readonly Dictionary<ZoneNpc, ZoneNpcBehavior> _behaviors = [];
    private readonly Action? _disposeActorOwners;
    private bool _disposed;

    internal ZoneNpcLuaRuntime(IZoneNpcActorRuntime actors, Action disposeActorOwners) : this(actors)
    { _disposeActorOwners = disposeActorOwners ?? throw new ArgumentNullException(nameof(disposeActorOwners)); }

    /// <summary>Transfers ownership of a prepared behavior handler. Actor removal releases it.</summary>
    public void Register(ZoneNpc npc, ZoneNpcBehavior behavior)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(npc); ArgumentNullException.ThrowIfNull(behavior);
        ObjectDisposedException.ThrowIf(behavior.IsDisposed, behavior);
        if (_behaviors.ContainsKey(npc) || _behaviors.ContainsValue(behavior))
            throw new InvalidOperationException("A behavior handler can belong to only one registered actor.");
        _behaviors.Add(npc, behavior);
    }

    public bool IsEnabled(ZoneNpc npc) => !_disposed && _actors.IsEnabled(npc);
    public bool CanReceiveSignals(ZoneNpc npc) => !_disposed && _actors.CanReceiveSignals(npc);
    public bool HasSignalHandler(ZoneNpc npc) => !_disposed && _actors.HasSignalHandler(npc) &&
        _behaviors.TryGetValue(npc, out var behavior) && !behavior.IsDisposed;
    public bool HasPipeUser(ZoneNpc npc) => !_disposed && _actors.HasPipeUser(npc);
    public uint? GetEntityId(ZoneUnit unit) => _disposed ? null : _actors.GetEntityId(unit);
    public uint GetUnitObjectId(uint entityId) => _disposed ? 0 : _actors.GetUnitObjectId(entityId);
    public ZoneLuaReference? GetEntityScript(uint entityId) => _disposed ? null : _actors.GetEntityScript(entityId);
    public Vector3? GetEntityPosition(uint entityId) => _disposed ? null : _actors.GetEntityPosition(entityId);
    public float FrameTimeSeconds => _actors.FrameTimeSeconds;
    public void SignalReceived(ZoneNpc npc, string name) { if (!_disposed) _actors.SignalReceived(npc, name); }

    public void CaptureTalkPosition(ZoneNpc npc)
    {
        if (GetEntityId(npc.Unit) is not { } id || GetEntityPosition(id) is not { } position) return;
        // 393401B0 only requires the entity's AI table, independently of the actor's signal handler.
        using var entity = GetEntityScript(id);
        if (entity is null) return;
        using var ai = entity.Runtime.Get(entity, "AI", 5) as ZoneLuaReference;
        if (ai is null) return;
        using var point = entity.Runtime.CreateTable();
        point.Set("x", position.X); point.Set("y", position.Y); point.Set("z", position.Z);
        ai.Set("talkPos", point);
    }

    public void RunPendingConstructors(ZoneNpc npc)
    { if (!_disposed && _behaviors.TryGetValue(npc, out var behavior)) behavior.RunPendingConstructors(); }

    public void DispatchScriptSignal(ZoneNpc npc, ZoneAiSignal signal)
    {
        if (_disposed || !_behaviors.TryGetValue(npc, out var behavior)) return;
        using var sender = GetEntityScript(signal.SenderEntityId);
        behavior.Dispatch(signal, sender);
    }

    public bool StartAuxiliaryBehavior(ZoneNpc npc, string name, int kind) =>
        !_disposed && _actors.StartAuxiliaryBehavior(npc, name, kind);
    public bool IsAuxiliaryBehaviorFinished(ZoneNpc npc) => _disposed || _actors.IsAuxiliaryBehaviorFinished(npc);
    public void ReleaseScriptState(ZoneNpc npc)
    { if (_behaviors.Remove(npc, out var behavior)) behavior.Dispose(); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        List<Exception>? errors = null;
        try { _disposeActorOwners?.Invoke(); } catch (Exception error) { (errors ??= []).Add(error); }
        foreach (var behavior in _behaviors.Values.ToArray())
            try { behavior.Dispose(); } catch (Exception error) { (errors ??= []).Add(error); }
        _behaviors.Clear();
        if (errors is not null) throw new AggregateException("NPC Lua runtime cleanup failed.", errors);
    }
}
