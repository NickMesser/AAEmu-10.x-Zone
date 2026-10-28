using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Ai;
using AAEmu.Zone.Combat;
using AAEmu.Zone.Content;
using AAEmu.Zone.Scene;

namespace AAEmu.Zone.Scripting;

/// <summary>
/// Scene effects which require an actual owner beyond the script/actor registries. Default methods
/// refuse by name. Implementing a callback means performing that effect, not recording success.
/// Actor health is deliberately separate from World Unit vital stats until their mirror is owned.
/// </summary>
public interface IZoneManagedNpcScriptServices
{
    float GetActorHealth(ZoneManagedNpcActor actor) => throw Missing("actor.GetHealth / actor health mirror");
    float GetActorMaximumHealth(ZoneManagedNpcActor actor) => throw Missing("actor.GetMaxHealth / actor health mirror");
    Vector3 GetDirectionVector(ZoneManagedNpcActor actor, int axis) => throw Missing("entity.GetDirectionVector / entity orientation");
    void SetEntityTimer(ZoneManagedNpcActor actor, int timerId, int milliseconds) => throw Missing("entity.SetTimer / entity script timer scheduler");
    void KillEntityTimer(ZoneManagedNpcActor actor, int timerId) => throw Missing("entity.KillTimer / entity script timer scheduler");
    void SetScriptUpdateRate(ZoneManagedNpcActor actor, float combatInterval, float peaceInterval) => throw Missing("entity.SetScriptUpdateRate / script proxy update policy");
    void HideAttachment(ZoneManagedNpcActor actor, int slot, string name, bool hide, bool hideShadow) => throw Missing("entity.HideAttachment / character attachments");
    void SetColliderMode(ZoneManagedNpcActor actor, int mode) => throw Missing("entity.SetColliderMode / actor collider policy");
    void SetSmartObjectState(ZoneManagedNpcActor actor, string state, bool modify) => throw Missing("unit.SetSoState / smart-object state owner");
    void SetDefaultPosture(ZoneManagedNpcActor actor, bool enabled) => throw Missing("unit.SetDefaultPosture / authored posture owner");
    void ClearCombat(ZoneManagedNpcActor actor) => throw Missing("unit.ClearCombat / combat owner and notifications");
    void StopCasting(ZoneManagedNpcActor actor) => throw Missing("unit.StopCasting / casting owner");
    bool RemoveBuff(ZoneManagedNpcActor actor, uint buffType) => throw Missing("unit.RemoveBuff / buff-type removal request");
    void SetPositionCheckContext(ZoneManagedNpcActor actor, int context) => throw Missing("unit.NpcSetCheckPosContext / position-check owner");
    void SetSkillUsePattern(ZoneManagedNpcActor actor, int pattern) => throw Missing("AI.SetSkillUsePattern / skill executor");
    void SetReferencePosition(ZoneManagedNpcActor actor, Vector3 value) => throw Missing("AI.SetRefPointPosition / indexed reference-point owner");
    void SetReferenceDirection(ZoneManagedNpcActor actor, Vector3 value) => throw Missing("AI.SetRefPointDirection / reference-point orientation");
    bool IsSimilarReferenceDirection(ZoneManagedNpcActor actor) => throw Missing("AI.IsSimilarRefPointDirection / reference-point orientation");
    int GetGroupId(ZoneManagedNpcActor actor) => throw Missing("AI.GetGroupOf / AI group membership");
    ZoneManagedNpcActor? GetGroupLeader(int groupId) => throw Missing("AI.GetLeader / AI group membership");
    int IsMoving(ZoneManagedNpcActor actor, float threshold) => throw Missing("AI.IsMoving / actor physical movement");
    float GetTerrainElevation(Vector3 position) => throw Missing("System.GetTerrainElevation / authored terrain query");
    private static NotSupportedException Missing(string api) => new($"Managed NPC script dependency is not admitted: {api}.");
}

/// <summary>Explicit partial profile: every unowned scene effect keeps the named interface refusal.</summary>
public sealed class ZoneManagedNpcScriptServices : IZoneManagedNpcScriptServices;

public sealed record ZoneManagedNpcScriptError(ZoneActorHandle? Actor, string Message);

/// <summary>
/// Production script bindings over the actual managed actor registry and descriptor catalogs.
/// This owns no engine module, actor allocation, physics, timer scheduler or gameplay admission.
/// All actor callbacks validate the exact generation before touching state; returned entity tables
/// remain owned by their binding until it retires. The enclosing scene owns the Lua runtime.
/// </summary>
public sealed class ZoneManagedNpcScriptHost : IDisposable
{
    private readonly IZoneLuaRuntime _lua;
    private readonly ZoneManagedNpcActorScene _actors;
    private readonly IZoneManagedNpcScriptServices _services;
    private readonly Func<long> _frameMicroseconds;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Action<string>? _trace;
    private readonly Dictionary<uint, ZoneManagedNpcScriptBinding> _bindings = [];
    private readonly Queue<ZoneManagedNpcScriptError> _errors = [];
    private ZoneManagedNpcScriptBinding? _executing;
    private ZoneLuaReference? _vectorFactory;
    private bool _installed;
    public bool IsDisposed { get; private set; }
    public ZoneNpcAuthoredScripts Scripts { get; }
    public ZoneScriptLoader ScriptLoader { get; }
    public ZoneGoalPipeCatalog GoalPipes => _actors.GoalPipes;
    public ZoneTrackPatternCatalog TrackPatterns { get; } = new();
    public ZoneAiCombatClasses CombatClasses { get; } = new();
    public ZoneNpcScriptBootstrapReport BootstrapReport => Scripts.BootstrapReport;
    public IReadOnlyList<ZoneManagedNpcScriptError> RecentErrors => _errors.ToArray();

    public ZoneManagedNpcScriptHost(IZoneLuaRuntime lua, ZoneScriptLoader loader, ZoneNpcContent content,
        ZoneManagedNpcActorScene actors, IZoneManagedNpcScriptServices services, Func<long> frameMicroseconds,
        Func<DateTimeOffset> utcNow, Action<string>? trace = null)
    {
        _lua = lua ?? throw new ArgumentNullException(nameof(lua));
        ScriptLoader = loader ?? throw new ArgumentNullException(nameof(loader));
        ArgumentNullException.ThrowIfNull(content);
        _actors = actors ?? throw new ArgumentNullException(nameof(actors));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _frameMicroseconds = frameMicroseconds ?? throw new ArgumentNullException(nameof(frameMicroseconds));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow)); _trace = trace;
        Scripts = new(lua, loader, content);
    }

    public ZoneNpcScriptBootstrapReport Bootstrap()
    {
        InstallBindings();
        return Scripts.Bootstrap();
    }

    public void InstallBindings()
    {
        Check();
        if (_installed) return;
        ScriptLoader.InstallBindings();
        ZoneAiScriptConstants.Install(_lua);
        using var ai = _lua.CreateTable();
        using var system = _lua.CreateTable();
        using var clock = _lua.CreateTable();
        _lua.SetGlobal("AI", ai); _lua.SetGlobal("System", system); _lua.SetGlobal("os", clock);
        ZoneAiLogBindings.Install(ai, Log, Log);
        _ = new ZoneGoalPipeBinding(ai, GoalPipes, Log, name => _actors.FindAiObject(name));
        _ = new ZoneTrackPatternBinding(ai, TrackPatterns, Log);
        ZoneAiCombatClassBinding.Install(ai, CombatClasses);
        Bind(clock, "time", _ => [_utcNow().ToUnixTimeSeconds()]);
        Bind(system, "IsEditor", _ => [false]); // This host is the dedicated zone scene, never the editor.
        Bind(system, "GetCurrTime", _ => [(float)((double)_frameMicroseconds() / 1_000_000)]);
        Bind(system, "Log", args => { Log(Text(args, 0, "System.Log")); return []; });
        Bind(system, "Warning", args => { Log(Text(args, 0, "System.Warning")); return []; });
        Bind(system, "Error", args => { ReportError(Text(args, 0, "System.Error")); return []; });
        Bind(system, "GetEntity", args => args.Count > 0 && args[0] is ZoneLuaHandle id && Find(id) is { } bound ? [bound.Script] : []);
        Bind(system, "GetTerrainElevation", args => [_services.GetTerrainElevation(Vector(args, 0, "System.GetTerrainElevation"))]);
        Bind(ai, "Signal", Signal);
        Bind(ai, "GetAlertness", args => [Actor(args, 0, "AI.GetAlertness").Actor.Behavior?.Alertness ?? 0]);
        Bind(ai, "GetGroupOf", args => [_services.GetGroupId(Actor(args, 0, "AI.GetGroupOf").Actor)]);
        Bind(ai, "GetLeader", args =>
        {
            var leader = _services.GetGroupLeader(Integer(args, 0, "AI.GetLeader"));
            return leader is not null && _bindings.TryGetValue(leader.AiObject.EntityId, out var bound) &&
                ReferenceEquals(bound.Actor, leader) && bound.IsCurrent ? [bound.Script] : [];
        });
        Bind(ai, "ClearAttentionTargetOf", args => { Actor(args, 0, "AI.ClearAttentionTargetOf").Actor.Services.SetAttentionTarget(null); return []; });
        Bind(ai, "SetAttentionTargetOf", args =>
        {
            var actor = Actor(args, 0, "AI.SetAttentionTargetOf");
            var target = Actor(args, 1, "AI.SetAttentionTargetOf");
            actor.Actor.Services.SetAttentionTarget(target.Actor.AiObject);
            actor.LastAttentionReason = args.Count > 2 ? Integer(args, 2, "AI.SetAttentionTargetOf") : 0;
            return [];
        });
        Bind(ai, "GetAttentionTargetOf", args => Actor(args, 0, "AI.GetAttentionTargetOf").Actor.AttentionTarget is { } target
            ? [new ZoneLuaHandle(target.EntityId)] : []);
        Bind(ai, "SetRefPointPosition", args => { _services.SetReferencePosition(Actor(args, 0, "AI.SetRefPointPosition").Actor, Vector(args, 1, "AI.SetRefPointPosition")); return []; });
        Bind(ai, "SetRefPointDirection", args => { _services.SetReferenceDirection(Actor(args, 0, "AI.SetRefPointDirection").Actor, Vector(args, 1, "AI.SetRefPointDirection")); return []; });
        Bind(ai, "IsSimilarRefPointDirection", args => [_services.IsSimilarReferenceDirection(Actor(args, 0, "AI.IsSimilarRefPointDirection").Actor)]);
        Bind(ai, "SetSkillUsePattern", args => { _services.SetSkillUsePattern(Actor(args, 0, "AI.SetSkillUsePattern").Actor, Integer(args, 1, "AI.SetSkillUsePattern")); return []; });
        Bind(ai, "IsMoving", args => [_services.IsMoving(Actor(args, 0, "AI.IsMoving").Actor, args.Count > 1 ? Number(args, 1, "AI.IsMoving") : 0)]);
        using (var factory = _lua.Execute("""
            return function(f) return function(self,...)
                local x,y,z=f(self,...);return {x=x,y=y,z=z}
            end end
            """, "=zone-npc-vector-binding")) _vectorFactory = ((ZoneLuaReference)factory[0]!).Retain();
        RefuseMissing(ai, "AI"); RefuseMissing(system, "System"); RefuseMissing(clock, "os");
        _installed = true;
    }

    /// <summary>
    /// Bind before adoption, but run no callbacks until this exact actor has been adopted. entityName
    /// is the actual allocated entity name, independently supplied from the Unit/NPC/entity IDs.
    /// The caller owns the returned lease; disposing it revokes callbacks without retiring the actor.
    /// </summary>
    public ZoneManagedNpcScriptBinding BindEntity(ZoneNpcAuthoredEntity prepared, ZoneManagedNpcActor actor, string entityName)
    {
        Check(); ArgumentNullException.ThrowIfNull(prepared); ArgumentNullException.ThrowIfNull(actor); ArgumentNullException.ThrowIfNull(entityName);
        if (!_installed || !BootstrapReport.Ready) throw new InvalidOperationException(BootstrapReport.Refusal ?? "NPC script bindings have not bootstrapped.");
        if (!ReferenceEquals(prepared.Owner, Scripts) || !ReferenceEquals(actor.Scene, _actors) || actor.Npc.TemplateId != prepared.NpcTemplateId)
            throw new ArgumentException("Script and actor must belong to this exact scene and NPC template.");
        var id = _lua.Get(prepared.EntityScript, "id", 2);
        if (id is not ZoneLuaHandle handle || handle.Value != actor.AiObject.EntityId)
            throw new ArgumentException("The script entity handle must match its independently allocated actor entity ID.");
        if (_bindings.TryGetValue(actor.AiObject.EntityId, out var previous))
        {
            if (previous.IsCurrent) throw new InvalidOperationException("This entity ID already has a current script binding.");
            previous.Dispose();
        }
        var bound = new ZoneManagedNpcScriptBinding(this, actor, prepared, prepared.EntityScript.Retain(), ZoneAiSignalName.CString(entityName));
        _bindings.Add(actor.AiObject.EntityId, bound);
        try
        {
            using var methods = _lua.CreateTable();
            _ = new ZoneEntityGoalPipeBinding(methods, _actors.Actors);
            foreach (var name in new[] { "SelectPipe", "InsertSubpipe", "CancelSubpipe", "IsUsingPipe" })
            {
                var original = (ZoneLuaReference)methods.Get(name)!; bound.Own(original);
                Entity(bound, name, args => { using var result = _lua.Call(original, args.ToArray()); return result.ToArray(); });
            }
            Entity(bound, "GetName", _ => [bound.EntityName]);
            EntityVector(bound, "GetPos", _ => bound.Actor.Services.GetPosition());
            EntityVector(bound, "GetWorldPos", _ => bound.Actor.Services.GetPosition());
            EntityVector(bound, "GetDirectionVector", args => _services.GetDirectionVector(actor,
                ZoneManagedEntityOrientation.AxisFromScriptArgument(args.Count > 1 ? args[1] : null)));
            // 32031030 only validates its argument and returns: there is no native state write.
            Entity(bound, "NetPresent", _ => []);
            // 32033420's decompile omits its float arguments. The binary preserves XMM2/XMM3
            // and forwards them to320732C0, which writes script-proxy+40/+44.
            Entity(bound, "SetScriptUpdateRate", args => { _services.SetScriptUpdateRate(actor,
                Number(args, 1, "entity.SetScriptUpdateRate"), Number(args, 2, "entity.SetScriptUpdateRate")); return []; });
            Entity(bound, "SetTimer", args => { _services.SetEntityTimer(actor, Integer(args, 1, "entity.SetTimer"), Integer(args, 2, "entity.SetTimer")); return []; });
            Entity(bound, "KillTimer", args => { _services.KillEntityTimer(actor, Integer(args, 1, "entity.KillTimer")); return []; });
            Entity(bound, "SetColliderMode", args => { _services.SetColliderMode(actor, Integer(args, 1, "entity.SetColliderMode")); return []; });
            Entity(bound, "HideAttachment", args => { _services.HideAttachment(actor, Integer(args, 1, "entity.HideAttachment"), Text(args, 2, "entity.HideAttachment"), Boolean(args, 3, "entity.HideAttachment"), Boolean(args, 4, "entity.HideAttachment")); return []; });

            using var actorTable = _lua.CreateTable(); actorTable.Set("id", handle);
            //39310CF0 -> CActor virtual+240 ->392E9850 reads the actor's independent+160 field.
            Method(bound, actorTable, "GetHealth", actor.AiObject.EntityId, _ => [_services.GetActorHealth(actor)]);
            Method(bound, actorTable, "GetMaxHealth", actor.AiObject.EntityId, _ => [_services.GetActorMaximumHealth(actor)]);
            Method(bound, actorTable, "IsPlayer", actor.AiObject.EntityId, _ => [false]);
            //Dedicated39310CA0 -> actor-binding vtable39FD5B88+192 ->3930BEC0 only
            //ends the function.39311540 parses three vectors and a float before that leaf.
            //Keep strict managed argument refusals; a valid call has no locomotion effect.
            Method(bound, actorTable, "SetMovementTarget", actor.AiObject.EntityId, args =>
            {
                _ = Vector(args, 1, "actor.SetMovementTarget"); _ = Vector(args, 2, "actor.SetMovementTarget");
                _ = Vector(args, 3, "actor.SetMovementTarget"); _ = Number(args, 4, "actor.SetMovementTarget"); return [];
            });
            RefuseMissing(actorTable, "actor"); bound.Script.Set("actor", actorTable);
            using var unit = _lua.CreateTable(); unit.Set("id", new ZoneLuaHandle(actor.Npc.ObjectId));
            var worldId = actor.Npc.ObjectId;
            // 393517C0 converts the function handler's World Unit ID to float; it is neither
            // Npc.Id nor the actor's entity handle. 39351830 resolves that ID via3935C830.
            Method(bound, unit, "GetId", worldId, _ => [(float)worldId]);
            Method(bound, unit, "GetName", worldId, _ => [ZoneAiSignalName.CString(actor.Npc.Unit.Snapshot.Name)]);
            Method(bound, unit, "NpcActivateAggroUpdate", worldId, args => { actor.Npc.AggroEnabled = Boolean(args, 1, "unit.NpcActivateAggroUpdate"); return []; });
            Method(bound, unit, "NpcActivateAITimeEvent", worldId, args =>
            {
                var enabled = Boolean(args, 1, "unit.NpcActivateAITimeEvent");
                // 39353EE0: convert to float seconds first, multiply in single precision,
                // truncate to signed int, then pass its bits to3933B3B0's uint interval.
                var interval = ZoneStatMath.ToInt32(Number(args, 2, "unit.NpcActivateAITimeEvent") * 1000f);
                actor.Npc.Timers.ActivateAiTimeEvent(enabled, unchecked((uint)interval)); return [];
            });
            Method(bound, unit, "NpcSetInteractable", worldId, args => { actor.Npc.Talks.SetInteractable(Boolean(args, 1, "unit.NpcSetInteractable")); return []; });
            Method(bound, unit, "SetSoState", worldId, args => { _services.SetSmartObjectState(actor, Text(args, 1, "unit.SetSoState"), true); return []; });
            Method(bound, unit, "SetDefaultSoState", worldId, args => { _services.SetSmartObjectState(actor, Boolean(args, 1, "unit.SetDefaultSoState") ? actor.Npc.Template.SoState : "idle", false); return []; });
            Method(bound, unit, "SetDefaultPosture", worldId, args => { _services.SetDefaultPosture(actor, Boolean(args, 1, "unit.SetDefaultPosture")); return []; });
            Method(bound, unit, "ClearCombat", worldId, _ => { _services.ClearCombat(actor); return []; });
            Method(bound, unit, "StopCasting", worldId, _ => { _services.StopCasting(actor); return []; });
            Method(bound, unit, "RemoveBuff", worldId, args => [_services.RemoveBuff(actor, unchecked((uint)Integer(args, 1, "unit.RemoveBuff")))]);
            Method(bound, unit, "NpcSetCheckPosContext", worldId, args => { _services.SetPositionCheckContext(actor, Integer(args, 1, "unit.NpcSetCheckPosContext")); return []; });
            RefuseMissing(unit, "unit"); bound.Script.Set("unit", unit);
            return bound;
        }
        catch { bound.Dispose(); throw; }
    }

    public ZoneNpcAuthoredScriptReadiness Initialize(ZoneManagedNpcScriptBinding binding)
    {
        Check(); binding.RequireOwner(this); binding.RequireCurrent();
        var previous = _executing; _executing = binding;
        binding.InitializationError = null;
        try { return Scripts.Initialize(binding.Prepared, () => binding.IsCurrent, () => binding.InitializationError); }
        finally { _executing = previous; }
    }

    internal void InvokeCallback(ZoneManagedNpcScriptBinding binding, ZoneLuaReference function, object?[] arguments)
    {
        Check(); binding.RequireOwner(this); binding.RequireCurrent();
        var previous = _executing; var previousError = binding.InitializationError;
        _executing = binding; binding.InitializationError = null;
        try
        {
            _lua.Invoke(function, new object?[] { binding.Script }.Concat(arguments).ToArray());
            if (binding.InitializationError is { } error) throw new ZoneLuaException(error);
        }
        finally { _executing = previous; binding.InitializationError = previousError; }
    }

    private object?[] Signal(IReadOnlyList<object?> args)
    {
        var filter = Integer(args, 0, "AI.Signal");
        var kind = Integer(args, 1, "AI.Signal");
        var name = Text(args, 2, "AI.Signal");
        var sender = Actor(args, 3, "AI.Signal");
        if (filter != 0) throw new NotSupportedException($"Managed NPC script signal filter {filter} has no admitted group/perception router.");
        ZoneAiSignalData? data = null;
        if (args.Count > 4 && args[4] is not null)
        {
            if (args[4] is not ZoneLuaReference { IsTable: true } table) throw new ArgumentException("AI.Signal extra data requires a table.");
            using var point = _lua.Get(table, "point", 5) as ZoneLuaReference;
            using var point2 = _lua.Get(table, "point2", 5) as ZoneLuaReference;
            data = new(point is null ? default : _lua.GetVector(point) ?? default,
                point2 is null ? default : _lua.GetVector(point2) ?? default,
                _lua.Get(table, "id", 2) is ZoneLuaHandle target ? target.Value : 0,
                Scalar(_lua.Get(table, "fValue", 3)), ZoneStatMath.ToInt32(Scalar(_lua.Get(table, "iValue", 3))),
                ZoneStatMath.ToInt32(Scalar(_lua.Get(table, "iValue2", 3))), _lua.Get(table, "ObjectName", 4) as string ?? "");
        }
        sender.Actor.Npc.AiSignals.Submit(kind, name, sender.Actor.AiObject.EntityId, data);
        return [];
    }

    private void Entity(ZoneManagedNpcScriptBinding bound, string name, Func<IReadOnlyList<object?>, object?[]> callback) =>
        Method(bound, bound.Script, name, bound.Actor.AiObject.EntityId, callback);
    private void EntityVector(ZoneManagedNpcScriptBinding bound, string name, Func<IReadOnlyList<object?>, Vector3> callback)
    {
        using var scalar = _lua.CreateFunction(args =>
        {
            bound.RequireReceiver(args, bound.Actor.AiObject.EntityId);
            var v = callback(args); return [v.X, v.Y, v.Z];
        });
        using var wrapped = _lua.Call(_vectorFactory!, scalar);
        bound.Script.Set(name, wrapped[0]);
    }
    private void Method(ZoneManagedNpcScriptBinding bound, ZoneLuaReference table, string name, uint receiverId,
        Func<IReadOnlyList<object?>, object?[]> callback) => Bind(table, name, args =>
        { bound.RequireReceiver(args, receiverId); return callback(args); });
    private void Bind(ZoneLuaReference table, string name, Func<IReadOnlyList<object?>, object?[]> callback)
    {
        using var function = _lua.CreateFunction(args => { Check(); return callback(args); });
        table.Set(name, function);
    }
    private void RefuseMissing(ZoneLuaReference table, string domain)
    {
        using var metatable = _lua.CreateTable();
        using var missing = _lua.CreateFunction(args => throw new NotSupportedException(
            $"Managed NPC script API is not admitted: {domain}.{(args.Count > 1 ? args[1] : "?")}."));
        metatable.Set("__index", missing);
        using var install = _lua.Execute("return function(t,m) setmetatable(t,m) end", "=zone-npc-api-refusals");
        _lua.Invoke((ZoneLuaReference)install[0]!, table, metatable);
    }
    private ZoneManagedNpcScriptBinding Actor(IReadOnlyList<object?> args, int index, string api) =>
        index < args.Count && args[index] is ZoneLuaHandle id && Find(id) is { } actor ? actor :
            throw new InvalidOperationException($"{api} requires an exact live managed actor entity handle.");
    private ZoneManagedNpcScriptBinding? Find(ZoneLuaHandle id) => id.Value <= uint.MaxValue &&
        _bindings.TryGetValue((uint)id.Value, out var bound) && bound.IsCurrent ? bound : null;
    private Vector3 Vector(IReadOnlyList<object?> args, int index, string api) =>
        index < args.Count && args[index] is ZoneLuaReference { IsTable: true } table && _lua.GetVector(table) is { } vector
            ? vector : throw new ArgumentException($"{api} requires a vector table at argument {index + 1}.");
    private static string Text(IReadOnlyList<object?> args, int index, string api) =>
        index < args.Count && args[index] is string text ? ZoneAiSignalName.CString(text) :
            throw new ArgumentException($"{api} requires a string at argument {index + 1}.");
    private static float Number(IReadOnlyList<object?> args, int index, string api)
    {
        var value = index < args.Count ? args[index] : null;
        if (value is not (long or double)) throw new ArgumentException($"{api} requires a number at argument {index + 1}.");
        var number = Scalar(value);
        if (!float.IsFinite(number)) throw new ArgumentException($"{api} requires a finite number.");
        return number;
    }
    private static int Integer(IReadOnlyList<object?> args, int index, string api) => ZoneStatMath.ToInt32(Number(args, index, api));
    private static float Scalar(object? value) => value switch { long integer => (float)(double)integer, double number => (float)number, _ => 0 };
    private static bool Boolean(IReadOnlyList<object?> args, int index, string api) =>
        index < args.Count ? args[index] switch
        { bool value => value, long value => value != 0, double value => value != 0, _ => throw new ArgumentException($"{api} requires a boolean.") } :
            throw new ArgumentException($"{api} requires a boolean at argument {index + 1}.");
    private void ReportError(string message)
    {
        if (_errors.Count == 256) _errors.Dequeue();
        _errors.Enqueue(new(_executing?.Actor.Handle, message));
        if (_executing is { } bound) bound.InitializationError ??= message;
        Log("[Lua Error] " + message);
    }
    private void Log(string message) => _trace?.Invoke(message);
    private void Check() { ObjectDisposedException.ThrowIf(IsDisposed, this); ObjectDisposedException.ThrowIf(_lua.IsDisposed, _lua); }
    internal void Retire(ZoneManagedNpcScriptBinding binding)
    {
        if (_bindings.TryGetValue(binding.EntityId, out var current) && ReferenceEquals(current, binding))
            _bindings.Remove(binding.EntityId);
    }
    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        foreach (var binding in _bindings.Values.ToArray()) binding.Dispose();
        Scripts.Dispose(); _vectorFactory?.Dispose();
    }
}

/// <summary>A revocable binding lease. Actor retirement and binding retirement independently revoke its callbacks.</summary>
public sealed class ZoneManagedNpcScriptBinding : IDisposable
{
    private readonly ZoneManagedNpcScriptHost _host;
    private readonly List<ZoneLuaReference> _owned = [];
    public ZoneManagedNpcActor Actor { get; }
    public ZoneNpcAuthoredEntity Prepared { get; }
    internal ZoneLuaReference Script { get; }
    public string EntityName { get; }
    public uint EntityId { get; }
    public int LastAttentionReason { get; internal set; }
    internal string? InitializationError { get; set; }
    public bool IsDisposed { get; private set; }
    public bool IsCurrent => !IsDisposed && !_host.IsDisposed && !Script.IsDisposed && Actor.HasActor;
    internal ZoneManagedNpcScriptBinding(ZoneManagedNpcScriptHost host, ZoneManagedNpcActor actor,
        ZoneNpcAuthoredEntity prepared, ZoneLuaReference script, string entityName)
    { _host = host; Actor = actor; Prepared = prepared; Script = script; EntityName = entityName; EntityId = actor.AiObject.EntityId; }
    internal void Own(ZoneLuaReference reference) => _owned.Add(reference);
    internal void InvokeCallback(ZoneLuaReference function, object?[] arguments) => _host.InvokeCallback(this, function, arguments);
    internal void RequireOwner(ZoneManagedNpcScriptHost host)
    { if (!ReferenceEquals(_host, host)) throw new ArgumentException("The script binding belongs to another host."); }
    internal void RequireCurrent()
    { if (!IsCurrent) throw new ObjectDisposedException(nameof(ZoneManagedNpcScriptBinding), "The exact NPC script actor generation is not current."); }
    internal void RequireReceiver(IReadOnlyList<object?> args, uint expected)
    {
        RequireCurrent();
        if (args.Count == 0 || args[0] is not ZoneLuaReference { IsTable: true } table ||
            table.Runtime.Get(table, "id", 2) is not ZoneLuaHandle handle || handle.Value != expected)
            throw new ArgumentException("The NPC script method receiver does not match its owned entity or World Unit identity.");
        RequireCurrent();
    }
    public void Dispose()
    {
        if (IsDisposed) return; IsDisposed = true; _host.Retire(this);
        foreach (var reference in _owned) reference.Dispose(); _owned.Clear(); Script.Dispose();
    }
}
