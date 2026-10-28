using System.Numerics;

using AAEmu.Zone.Ai;
using AAEmu.Zone.Combat;

namespace AAEmu.Zone.Scripting;

public enum ZoneBehaviorConstructorMode { Immediate, Deferred, Restore }

/// <summary>Native CAIHandler script dispatch and behavior transitions. The actor phase serializes access.</summary>
public sealed class ZoneNpcBehavior : IDisposable
{
    private readonly IZoneLuaRuntime _lua;
    private readonly IZoneNpcBehaviorServices _services;
    private readonly ZoneLuaReference _entity, _behaviors;
    private readonly ZoneLuaReference? _character, _defaultCharacter, _defaultBehavior, _available, _internal;
    private ZoneLuaReference? _current, _previous;
    public string CurrentName { get; private set; } = "";
    public string DefaultName { get; }
    public string FirstName { get; }
    public int Alertness { get; private set; }
    public bool Exclusive { get; private set; }
    public bool PendingCharacterConstructor { get; private set; }
    public bool PendingBehaviorConstructor { get; private set; }
    public bool IsDisposed { get; private set; }

    /// <summary>Inputs are borrowed. This owner retains its own references, released on actor removal.</summary>
    public ZoneNpcBehavior(IZoneLuaRuntime lua, ZoneLuaReference entity, ZoneLuaReference behaviors,
        ZoneLuaReference? character, ZoneLuaReference? defaultCharacter, ZoneLuaReference? defaultBehavior,
        string defaultName, string firstName, IZoneNpcBehaviorServices services)
    {
        ArgumentNullException.ThrowIfNull(lua); ArgumentNullException.ThrowIfNull(services);
        _lua = lua; _services = services;
        DefaultName = ZoneAiSignalName.CString(defaultName); FirstName = ZoneAiSignalName.CString(firstName);
        // Validate every input before retaining anything, including runtime ownership.
        foreach (var reference in new[] { entity, behaviors, character, defaultCharacter, defaultBehavior })
            if (reference is not null && (!reference.IsTable || reference.IsDisposed || !ReferenceEquals(reference.Runtime, lua)))
                throw new ArgumentException("Behavior inputs must be live tables from the same Lua runtime.");
        ArgumentNullException.ThrowIfNull(entity); ArgumentNullException.ThrowIfNull(behaviors);
        try
        {
            _entity = entity.Retain(); _behaviors = behaviors.Retain();
            _character = character?.Retain(); _defaultCharacter = defaultCharacter?.Retain(); _defaultBehavior = defaultBehavior?.Retain();
            _available = Table(behaviors, "AVAILABLE"); _internal = Table(behaviors, "INTERNAL");
        }
        catch { Dispose(); throw; }
    }

    public ZoneLuaReference? RetainCurrent() => IsDisposed ? null : _current?.Retain();
    public ZoneLuaReference? RetainPrevious() => IsDisposed ? null : _previous?.Retain();
    public void QueueCharacterConstructor() { if (!IsDisposed) PendingCharacterConstructor = true; }

    // 305108E0 clears flags after callbacks; a constructor cannot rearm itself in that same phase.
    public void RunPendingConstructors()
    {
        if (IsDisposed) return;
        if (PendingCharacterConstructor) { CharacterConstructor(); PendingCharacterConstructor = false; }
        if (!IsDisposed && PendingBehaviorConstructor) { BehaviorConstructor(null); PendingBehaviorConstructor = false; }
    }

    private void CharacterConstructor()
    {
        using var function = Function(_character, "Constructor");
        if (function is not null) Invoke(function, "Constructor", [_character, _entity]);
    }

    private void BehaviorConstructor(ZoneAiSignalData? data)
    {
        if (_current is null || IsDisposed) return;
        _services.RecordEvent(10, CurrentName);
        CallScript(_current, "Constructor", data: data);
        if (IsDisposed) return;
        var following = String(_entity, "EventToCall");
        if (!string.IsNullOrEmpty(following))
        {
            CallScript(_current, following);
            if (!IsDisposed) _entity.Set("EventToCall", "");
        }
    }

    /// <summary>30511450. The actor signal queue performs the Zone event dispatch after this returns.</summary>
    public void Dispatch(ZoneAiSignal signal, ZoneLuaReference? sender = null)
    {
        if (IsDisposed) return;
        var name = ZoneAiSignalName.CString(signal.Name);
        if (signal.Kind != 3) { _services.ActorEvent(4, name); _services.RecordEvent(4, name); }
        if (name == "ACTION_DONE") _services.NotifyActionDone();
        if (IsDisposed) return;
        if (!CallScript(_current, name, sender, signal.Data) && !IsDisposed && CallScript(_defaultBehavior, name, sender, signal.Data))
            _services.RecordEvent(4, "from defaultDefault behaviour");
        if (!IsDisposed && GetTransition(name) is { } transition) SetBehavior(transition, signal.Data);
    }

    /// <summary>3050EA90. A present function handles the signal even when it returns false or raises an error.</summary>
    public bool CallScript(ZoneLuaReference? behavior, string name, ZoneLuaReference? sender = null,
        ZoneAiSignalData? data = null, float? numericFallback = null)
    {
        if (IsDisposed || behavior is null) return false;
        name = ZoneAiSignalName.CString(name);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        using var initial = behavior.Retain();
        var current = initial; ZoneLuaReference? inherited = null;
        try
        {
            while (!IsDisposed)
            {
                using var function = Function(current, name);
                if (function is not null)
                {
                    var arguments = new List<object?> { current, _entity };
                    if (sender is not null) arguments.Add(sender);
                    else if (numericFallback is { } number) arguments.Add(number);
                    using var extra = data is null ? null : CreateSignalData(data);
                    if (extra is not null) arguments.Add(extra);
                    Invoke(function, name, [.. arguments]); return true;
                }
                var baseName = String(current, "Base");
                if (baseName is null) return false;
                if (!visited.Add(baseName)) throw new ZoneLuaException($"Behavior inheritance cycle at '{baseName}'.");
                var next = Table(_behaviors, baseName);
                inherited?.Dispose(); inherited = next;
                if (next is null) return false;
                current = next;
            }
            return false;
        }
        finally { inherited?.Dispose(); }
    }

    private void Invoke(ZoneLuaReference function, string name, object?[] arguments)
    {
        try { _lua.Invoke(function, arguments); }
        catch (ZoneLuaException error) { _services.ScriptError(name, error); }
    }

    // 3050D8F0: a state-local empty string vetoes all fallback; an empty AnyBehavior does not.
    public string? GetTransition(string signal)
    {
        if (IsDisposed) return null;
        signal = ZoneAiSignalName.CString(signal);
        if (System.Text.Encoding.UTF8.GetByteCount(signal) < 2) return null;
        if (_current is not null && _character is not null)
        {
            var name = String(_current, "Name");
            using var state = name is null ? null : Table(_character, name);
            if (state is not null && String(state, signal) == "") return null;
            using var any = Table(_character, "AnyBehavior");
            var destination = String(state, signal) ?? String(any, signal);
            if (!string.IsNullOrEmpty(destination)) return destination;
        }
        using var fallback = (_current is null ? null : Table(_defaultCharacter, CurrentName)) ?? Table(_defaultCharacter, "NoBehaviorFound");
        var result = String(fallback, signal);
        return string.IsNullOrEmpty(result) ? null : result;
    }

    public void SetBehavior(string? name, ZoneAiSignalData? data = null,
        ZoneBehaviorConstructorMode mode = ZoneBehaviorConstructorMode.Immediate)
    {
        if (IsDisposed) return;
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        var requested = name is null ? null : ZoneAiSignalName.CString(name);
        if (requested == "unknown") return;
        var previous = requested == "PREVIOUS";
        name = requested is null ? DefaultName : requested == "FIRST" ? FirstName : requested;
        using var selected = previous ? _previous?.Retain() : FindBehavior(name);
        if (IsDisposed) return;
        if (previous)
        {
            if (selected is not null)
            {
                if (String(selected, "Name") == CurrentName) return;
                CurrentName = "PREVIOUS";
            }
            // Native keeps an empty local CryString on this branch and copies it below.
            name = "";
        }
        if (mode != ZoneBehaviorConstructorMode.Restore && _current is not null)
        {
            if (Number(_current, "NOPREVIOUS") is null) Replace(ref _previous, _current);
            CallScript(_current, "Destructor", data: data);
            if (IsDisposed) return;
            _services.RecordEvent(9, CurrentName);
            if (_services.HasActor) { _services.ActorEvent(9, CurrentName); _services.ResetPipeUserAfterBehaviorExit(); }
        }
        if (IsDisposed) return;
        CurrentName = name; Replace(ref _current, selected); _entity.Set("Behaviour", _current);
        _services.RecordEvent(8, CurrentName);
        if (_services.HasActor) _services.ActorEvent(8, CurrentName);
        if (IsDisposed) return;
        var alertness = Number(_current, "alertness") is { } alert ? ZoneStatMath.ToInt32(alert) : 0;
        var exclusive = Number(_current, "exclusive") is { } ex ? ZoneStatMath.ToInt32(ex) : 0;
        if (_current is not null)
        {
            if (mode == ZoneBehaviorConstructorMode.Deferred) PendingBehaviorConstructor = true;
            else if (mode == ZoneBehaviorConstructorMode.Immediate)
            {
                _services.ActorEvent(10, name); PendingBehaviorConstructor = false; BehaviorConstructor(data);
            }
        }
        if (IsDisposed) return;
        var becameAlert = Alertness == 0 && alertness > 0;
        if (alertness != -1 && Alertness != alertness)
        {
            Alertness = alertness;
            _services.ChangeSmartObjectState(alertness switch
            { 0 => "Idle-Alerted,Combat", 1 => "Alerted-Idle,Combat", 2 => "Combat-Idle,Alerted", _ => "" });
        }
        Exclusive = exclusive > 0;
        if (mode != ZoneBehaviorConstructorMode.Restore && becameAlert) _services.NotifyAlert();
    }

    private ZoneLuaReference? FindBehavior(string name)
    {
        if (name.Length == 0) return null;
        var found = Table(_behaviors, name);
        if (found is not null) return found;
        var filename = String(_available, name) ?? String(_internal, name);
        if (filename is not null) _services.LoadScript(filename);
        return IsDisposed ? null : Table(_behaviors, name);
    }

    private ZoneLuaReference CreateSignalData(ZoneAiSignalData data)
    {
        var table = _lua.CreateTable();
        try
        {
            table.Set("id", new ZoneLuaHandle(data.EntityId)); table.Set("fValue", data.FloatValue);
            table.Set("iValue", (float)data.Value); table.Set("iValue2", (float)data.Value2);
            using var point = Vector(data.Point); using var point2 = Vector(data.Point2);
            table.Set("point", point); table.Set("point2", point2);
            var name = ZoneAiSignalName.CString(data.ObjectName);
            if (name.Length != 0) table.Set("ObjectName", name);
            return table;
        }
        catch { table.Dispose(); throw; }
    }

    private ZoneLuaReference Vector(Vector3 value)
    {
        var table = _lua.CreateTable();
        try { table.Set("x", value.X); table.Set("y", value.Y); table.Set("z", value.Z); return table; }
        catch { table.Dispose(); throw; }
    }

    private static ZoneLuaReference? Reference(ZoneLuaReference? table, string key, bool isTable)
        => table?.Runtime.Get(table, key, isTable ? 5 : 6) as ZoneLuaReference;
    private static ZoneLuaReference? Table(ZoneLuaReference? table, string key) => Reference(table, key, true);
    private static ZoneLuaReference? Function(ZoneLuaReference? table, string key) => Reference(table, key, false);
    private static string? String(ZoneLuaReference? table, string key)
    {
        var value = table?.Runtime.Get(table, key, 4);
        return value is string text ? ZoneAiSignalName.CString(text) : null;
    }
    private static float? Number(ZoneLuaReference? table, string key)
    {
        var value = table?.Runtime.Get(table, key, 3);
        return value switch { long integer => (float)(double)integer, double number => (float)number, _ => null };
    }
    private static void Replace(ref ZoneLuaReference? target, ZoneLuaReference? source)
    { var retained = source?.Retain(); target?.Dispose(); target = retained; }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true; PendingCharacterConstructor = PendingBehaviorConstructor = false;
        foreach (var reference in new[] { _entity, _behaviors, _character, _defaultCharacter, _defaultBehavior,
                     _available, _internal, _current, _previous }) reference?.Dispose();
        _current = _previous = null;
    }
}
