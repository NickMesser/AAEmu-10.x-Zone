using System.Text;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Scene;

namespace AAEmu.Zone.Scripting;

/// <summary>The actual ordered entity links owned by a managed entity allocation, not guessed Unit relationships.</summary>
public sealed class ZoneManagedNpcSmartObjectLinks
{
    private readonly List<(string Name, ZoneManagedNpcActor Target)> _links = [];
    public void Add(string name, ZoneManagedNpcActor target)
    { ArgumentNullException.ThrowIfNull(name); ArgumentNullException.ThrowIfNull(target); _links.Add((CString(name), target)); }
    public bool Remove(string name, ZoneManagedNpcActor target)
    {
        name = CString(name);
        var index = _links.FindIndex(link => link.Name == name && ReferenceEquals(link.Target, target));
        if (index < 0) return false; _links.RemoveAt(index); return true;
    }
    internal ZoneManagedNpcActor? FirstBusyTarget() => _links.FirstOrDefault(link => link.Name == "Busy").Target;
    private static string CString(string value) { var end = value.IndexOf('\0'); return end < 0 ? value : value[..end]; }
}

/// <summary>
/// Actual per-entity smart-object classes and states, plus the native state-filter consumer.
/// This does not execute smart-object rules, animations, helpers or navigation actions.
/// Native source: class registration3118711C/3118700C, constructor311868CC,
/// singular set/add/remove3118337C/311833CC/3118372C, expression311850AC and filters31186A1C/31190614.
/// </summary>
public sealed class ZoneManagedNpcSmartObjectStates : IDisposable
{
    private readonly IZoneLuaRuntime _lua;
    private readonly Dictionary<ZoneActorHandle, ZoneManagedNpcSmartObjectLease> _actors = [];
    public bool IsDisposed { get; private set; }
    //CAISystem+6836 gates replacement and expression modification; singular add/remove are independent.
    public bool StateReplacementEnabled { get; set; } = true;
    public int Count => _actors.Count;
    public ZoneManagedNpcSmartObjectStates(IZoneLuaRuntime lua) => _lua = lua ?? throw new ArgumentNullException(nameof(lua));

    /// <summary>
    /// Register actual authored classes before OnInit. Properties and PropertiesInstance append
    /// distinct class names in source order. A classless entity is an explicitly known absent
    /// smart object. Fresh entities own an empty link collection until real links are added.
    /// </summary>
    public ZoneManagedNpcSmartObjectLease BindEntity(ZoneManagedNpcScriptBinding binding, ZoneManagedNpcSmartObjectLinks links)
    {
        Check(); ArgumentNullException.ThrowIfNull(binding); ArgumentNullException.ThrowIfNull(links); binding.RequireCurrent();
        if (!ReferenceEquals(binding.Script.Runtime, _lua)) throw new ArgumentException("Smart-object registration requires its own Lua runtime.");
        if (_actors.TryGetValue(binding.Actor.Handle, out var previous))
        {
            if (previous.IsCurrent) throw new InvalidOperationException("The exact actor already owns a smart-object registration.");
            previous.Dispose();
        }
        var classes = new List<string>();
        //3118711C excludes entity class names beginning with literal Spawn. The managed
        //allocator supplies EntityName separately; do not infer a native class from that name.
        foreach (var property in new[] { "Properties", "PropertiesInstance" })
        {
            using var table = _lua.Get(binding.Script, property, 5) as ZoneLuaReference;
            var text = table is null ? null : _lua.Get(table, "soclasses_SmartObjectClass", 4) as string;
            if (text is null) continue;
            foreach (var token in Tokens(TruncateBytes(text)))
                if (!classes.Contains(token, StringComparer.Ordinal)) classes.Add(token);
        }
        var owner = new ZoneManagedNpcSmartObjectLease(this, binding, links, classes.ToArray());
        _actors.Add(binding.Actor.Handle, owner); return owner;
    }

    /// <summary>AI system+1216: clear and insert one literal state, case-sensitive; empty strings leave state unchanged.</summary>
    public void SetState(ZoneManagedNpcActor actor, string state)
    {
        var owner = Require(actor); state = CString(state);
        if (!owner.HasSmartObject || !StateReplacementEnabled || state.Length == 0) return;
        owner.States.Clear(); owner.States.Add(state);
    }
    /// <summary>AI system+1224 adds one literal state, not a comma/minus expression.</summary>
    public void AddState(ZoneManagedNpcActor actor, string state) => Change(Require(actor), CString(state), true);
    public void RemoveState(ZoneManagedNpcActor actor, string state) => Change(Require(actor), CString(state), false);

    /// <summary>AI system+1240: remove tokens after the first minus, then add tokens before it.</summary>
    public void ModifyStates(ZoneManagedNpcActor actor, string expression)
    {
        var owner = Require(actor); ArgumentNullException.ThrowIfNull(expression);
        if (!StateReplacementEnabled) return;
        var (add, remove) = ParseChange(expression);
        foreach (var state in remove) Change(owner, state, false);
        foreach (var state in add) Change(owner, state, true);
    }

    public bool HasState(ZoneManagedNpcActor actor, string state) => Require(actor).States.Contains(CString(state));
    public IReadOnlyList<string> CopyStates(ZoneManagedNpcActor actor) => Require(actor).States.Order(StringComparer.Ordinal).ToArray();
    public IReadOnlyList<string> CopyClasses(ZoneManagedNpcActor actor) => Require(actor).Classes.ToArray();

    /// <summary>
    /// Concrete native filter consumer: alternatives separated by |; each requires all positive
    /// tokens and none of its negative tokens. Empty alternatives are omitted; an empty filter
    /// matches a registered object, while an entity without a smart object never matches.
    /// </summary>
    public bool MatchesStateFilter(ZoneManagedNpcActor actor, string expression)
    {
        var owner = Require(actor); expression = CString(expression);
        if (!owner.HasSmartObject) return false;
        var alternatives = expression.Split('|').Select(ParseChange).Where(part => part.Add.Length != 0 || part.Remove.Length != 0).ToArray();
        return alternatives.Length == 0 || alternatives.Any(part => part.Add.All(owner.States.Contains) && !part.Remove.Any(owner.States.Contains));
    }

    private void Change(ZoneManagedNpcSmartObjectLease owner, string state, bool add)
    {
        if (!owner.HasSmartObject || state.Length == 0) return;
        var changed = add ? owner.States.Add(state) : owner.States.Remove(state);
        //311833CC/3118372C propagate only changed Busy membership through the first exact
        //Busy entity link. Captured actor ownership prevents a stale link reaching a reused ID.
        if (!changed || state != "Busy" || owner.Links.FirstBusyTarget() is not { } target || !target.HasActor) return;
        if (!_actors.TryGetValue(target.Handle, out var linked) || !linked.IsCurrent || !ReferenceEquals(linked.Binding.Actor, target))
            throw new NotSupportedException("The live Busy-linked entity has no admitted smart-object registration inventory.");
        Change(linked, state, add);
    }
    private ZoneManagedNpcSmartObjectLease Require(ZoneManagedNpcActor actor)
    {
        Check(); ArgumentNullException.ThrowIfNull(actor);
        if (!_actors.TryGetValue(actor.Handle, out var owner) || !ReferenceEquals(owner.Binding.Actor, actor) || !owner.IsCurrent)
            throw new InvalidOperationException("Smart-object state requires its exact current registered actor generation.");
        return owner;
    }
    private static (string[] Add, string[] Remove) ParseChange(string expression)
    {
        expression = TruncateBytes(expression);
        var minus = expression.IndexOf('-');
        return minus < 0 ? (Tokens(expression).ToArray(), []) :
            (Tokens(expression[..minus]).ToArray(), Tokens(expression[(minus + 1)..]).ToArray());
    }
    private static IEnumerable<string> Tokens(string value)
    {
        //31180DAC: tokens must start with ASCII letter/underscore; digits may follow.
        for (var i = 0; i < value.Length;)
        {
            while (i < value.Length && !Letter(value[i])) i++;
            var start = i;
            while (i < value.Length && (Letter(value[i]) || value[i] is >= '0' and <= '9')) i++;
            if (i > start) yield return value[start..i];
        }
        static bool Letter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_';
    }
    private static string TruncateBytes(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(CString(value));
        //The parser only admits ASCII identifiers; byte preservation also gives the correct
        //native1023-byte truncation when a UTF8 attribute contains non-ASCII separators.
        return Encoding.Latin1.GetString(bytes.AsSpan(0, Math.Min(1023, bytes.Length)));
    }
    private static string CString(string value)
    { ArgumentNullException.ThrowIfNull(value); var end = value.IndexOf('\0'); return end < 0 ? value : value[..end]; }
    private void Check() { ObjectDisposedException.ThrowIf(IsDisposed, this); ObjectDisposedException.ThrowIf(_lua.IsDisposed, _lua); }
    internal void Retire(ZoneManagedNpcSmartObjectLease owner)
    {
        if (_actors.TryGetValue(owner.Binding.Actor.Handle, out var current) && ReferenceEquals(current, owner)) _actors.Remove(owner.Binding.Actor.Handle);
    }
    public void Dispose()
    { if (IsDisposed) return; IsDisposed = true; foreach (var owner in _actors.Values.ToArray()) owner.Dispose(); }
}

public sealed class ZoneManagedNpcSmartObjectLease : IDisposable
{
    private readonly ZoneManagedNpcSmartObjectStates _owner;
    internal ZoneManagedNpcScriptBinding Binding { get; }
    internal ZoneManagedNpcSmartObjectLinks Links { get; }
    internal string[] Classes { get; }
    internal HashSet<string> States { get; } = new(StringComparer.Ordinal);
    public bool HasSmartObject => Classes.Length != 0;
    public bool IsDisposed { get; private set; }
    public bool IsCurrent => !IsDisposed && !_owner.IsDisposed && Binding.IsCurrent;
    internal ZoneManagedNpcSmartObjectLease(ZoneManagedNpcSmartObjectStates owner, ZoneManagedNpcScriptBinding binding,
        ZoneManagedNpcSmartObjectLinks links, string[] classes)
    { _owner = owner; Binding = binding; Links = links; Classes = classes; if (HasSmartObject) States.Add("Idle"); }
    public void Dispose() { if (IsDisposed) return; IsDisposed = true; States.Clear(); _owner.Retire(this); }
}
