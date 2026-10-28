using AAEmu.Zone.Actors;
using AAEmu.Zone.Content;
using AAEmu.Zone.Scripting;

namespace AAEmu.Zone.Scene;

/// <summary>CAIObject212/216/220. Group type, runtime ID and member index are independent identities.</summary>
public readonly record struct ZoneManagedNpcGroupRecord(uint Type, int Id, byte MemberIndex);

public sealed record ZoneManagedNpcGroupSnapshot(ZoneManagedNpcGroupRecord Record,
    bool InitialParametersApplied, bool AuthoredGroupInstalled, bool MembershipIndexed);

/// <summary>
/// A copied real AIGroup allocation. Its independent CLeader object is absent until an actual
/// leader producer executes; membership or a content leadership flag does not elect an actor.
/// </summary>
public sealed record ZoneManagedNpcGroupMapSnapshot(int Id, IReadOnlyList<ZoneActorHandle> Members,
    bool HasLeaderObject, ZoneActorHandle? Leader);

/// <summary>
/// Owns actual CPuppet group parameters, NPC39346960 group installation and generation-scoped
/// membership used by GetGroupOf/GetLeader. CLeader formation execution remains a named
/// refusal; this owner never substitutes a content leader, first member or nearby actor.
/// </summary>
public sealed class ZoneManagedNpcGroups : IDisposable
{
    private readonly IZoneLuaRuntime _lua;
    private readonly ZoneNpcContent _content;
    private readonly Dictionary<ZoneActorHandle, ZoneManagedNpcGroupLease> _actors = [];
    private readonly Dictionary<int, Group> _groups = [];
    public bool IsDisposed { get; private set; }
    public int Count { get { Check(); Prune(); return _actors.Count; } }
    public int GroupCount { get { Check(); Prune(); return _groups.Count; } }

    public ZoneManagedNpcGroups(IZoneLuaRuntime lua, ZoneNpcContent content)
    { _lua = lua ?? throw new ArgumentNullException(nameof(lua)); _content = content ?? throw new ArgumentNullException(nameof(content)); }

    /// <summary>Bind the fresh constructor record before OnInit; parameter application is a distinct later operation.</summary>
    public ZoneManagedNpcGroupLease BindEntity(ZoneManagedNpcScriptBinding binding)
    {
        Check(); ArgumentNullException.ThrowIfNull(binding); binding.RequireCurrent();
        if (!ReferenceEquals(binding.Script.Runtime, _lua)) throw new ArgumentException("NPC group parameters require the bound Lua runtime.", nameof(binding));
        if (!binding.Actor.IsPuppet) throw new NotSupportedException("NPC group registration requires the actual CPuppet profile.");
        Prune();
        if (_actors.ContainsKey(binding.Actor.Handle)) throw new InvalidOperationException("This exact NPC actor already owns group state.");
        var lease = new ZoneManagedNpcGroupLease(this, binding);
        _actors.Add(binding.Actor.Handle, lease);
        return lease;
    }

    /// <summary>
    /// 392A10F0 defaults120 to0;392ADC00 reads Properties.groupid first, then
    /// PropertiesInstance.groupid. Read after original OnInit, before the later NPC group copy.
    /// </summary>
    public void ApplyInitialParameters(ZoneManagedNpcActor actor)
    {
        var owner = Require(actor);
        if (owner.InitialParametersApplied || owner.AuthoredGroupInstalled)
            throw new InvalidOperationException("Initial NPC group parameters are applied once, before authored group installation.");
        var parameter = ReadGroupParameter(owner, "Properties") ?? ReadGroupParameter(owner, "PropertiesInstance") ?? 0;
        Require(actor);
        ChangeId(owner, parameter);
        owner.InitialParametersApplied = true;
    }

    /// <summary>
    /// 39346960 installs only a resolvable Spawn.Group type, then writes actual Properties.groupid
    /// and AI.formationTension. The first movement-leader member invokes the separate formation
    /// producer; that branch refuses before publishing partial membership or script writes.
    /// </summary>
    public void InstallAuthoredGroup(ZoneManagedNpcActor actor)
    {
        var owner = Require(actor);
        if (!owner.InitialParametersApplied) throw new InvalidOperationException("Apply actual initial group parameters before NPC child installation.");
        if (owner.AuthoredGroupInstalled) throw new InvalidOperationException("The original NPC group is installed once for this actor allocation.");
        var source = actor.Npc.Spawn.Group;
        if (!_content.Groups.TryGetValue(source.Type, out var content))
        { owner.AuthoredGroupInstalled = true; return; }
        if (source.Id > int.MaxValue)
            throw new NotSupportedException("NPC runtime group IDs above signed32 require the native truncated membership-index profile.");
        if (source.MemberIndex >= content.Members.Count)
            throw new InvalidDataException($"NPC group type {source.Type} has no authored member {source.MemberIndex}.");
        //39346960 scans the actual source-order vector for bit2, not IsLeader/bit1.
        if (content.MoveLeaderIndex == source.MemberIndex)
            throw new NotSupportedException("The original movement-leader producer requires CLeader creation, formation descriptors and authored CreateFormation execution.");
        var tension = content.Members[source.MemberIndex].FormationTension;
        if (!float.IsFinite(tension)) throw new NotSupportedException("NPC formation tension must be a finite authored float.");
        using var properties = _lua.Get(owner.Binding.Script, "Properties", 5) as ZoneLuaReference;
        Require(actor);
        using var ai = _lua.Get(owner.Binding.Script, "AI", 5) as ZoneLuaReference;
        Require(actor);
        var id = (int)source.Id;
        ChangeId(owner, id);
        //3103D1C8 copies the full record even when the group ID is unchanged.
        owner.Record = new(source.Type, id, source.MemberIndex);
        if (properties is not null) { properties.Set("groupid", (float)id); Require(actor); }
        if (ai is not null) { ai.Set("formationTension", tension); Require(actor); }
        owner.AuthoredGroupInstalled = true;
    }

    public int GetGroupId(ZoneManagedNpcActor actor) => Require(actor).Record.Id;

    /// <summary>
    ///39344AA0 applies the actual empty group through512/3103D1C8 during spawner detach.
    /// It changes real membership and all three record fields; it does not rewrite Lua
    /// Properties.groupid or formationTension. Admitted groups have no CLeader producer.
    /// </summary>
    public void ClearActorGroup(ZoneManagedNpcActor actor)
    {
        var owner = Require(actor);
        ChangeId(owner, 0);
        owner.Record = default;
    }

    /// <summary>
    /// 31049B58 ->31054078 -> AIGroup232 -> CLeader560. Supported groups own an
    /// explicitly absent CLeader allocation. Unsupported formation producers cannot be admitted.
    /// </summary>
    public ZoneManagedNpcActor? GetGroupLeader(int id)
    {
        Check(); Prune();
        //The concrete map records absence, rather than deriving a leader from its member list.
        if (!_groups.TryGetValue(id, out var group)) return null;
        return group.LeaderObject?.Association;
    }

    public ZoneManagedNpcGroupSnapshot CopyState(ZoneManagedNpcActor actor)
    {
        var owner = Require(actor);
        return new(owner.Record, owner.InitialParametersApplied, owner.AuthoredGroupInstalled, owner.MembershipIndexed);
    }

    public ZoneManagedNpcGroupMapSnapshot? CopyGroup(int id)
    {
        Check(); Prune();
        return _groups.TryGetValue(id, out var group)
            ? new(id, Array.AsReadOnly(group.Members.Select(member => member.Binding.Actor.Handle).ToArray()),
                group.LeaderObject is not null, group.LeaderObject?.Association?.Handle)
            : null;
    }

    private int? ReadGroupParameter(ZoneManagedNpcGroupLease owner, string name)
    {
        using var table = _lua.Get(owner.Binding.Script, name, 5) as ZoneLuaReference;
        Require(owner.Binding.Actor);
        var value = table is null ? null : _lua.Get(table, "groupid", 3);
        Require(owner.Binding.Actor);
        if (value is null) return null;
        //ScriptAnyValue is a float before native CVTTSS2SI, including integer Lua values.
        var number = Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture);
        if (!float.IsFinite(number)) throw new NotSupportedException("NPC numeric group parameters must be finite.");
        return number >= 2147483648f || number < -2147483648f ? int.MinValue : (int)number;
    }

    private void ChangeId(ZoneManagedNpcGroupLease owner, int requested)
    {
        var id = Math.Max(requested, 0); //310063BC, before equality/membership checks.
        if (id == owner.Record.Id) return;
        RemoveMembership(owner);
        owner.Record = owner.Record with { Id = id };
        owner.MembershipIndexed = true;
        //31059B48 never creates AIGroup0; its separate object-index entry may still exist.
        if (id == 0) return;
        if (!_groups.TryGetValue(id, out var group)) _groups.Add(id, group = new());
        group.Members.Add(owner);
    }

    private void RemoveMembership(ZoneManagedNpcGroupLease owner)
    {
        if (owner.MembershipIndexed && _groups.TryGetValue(owner.Record.Id, out var group)) group.Members.Remove(owner);
        owner.MembershipIndexed = false;
        //3105B238 retains the AIGroup map allocation after its last member leaves.
    }

    internal void Retire(ZoneManagedNpcGroupLease owner)
    {
        if (!_actors.TryGetValue(owner.Binding.Actor.Handle, out var current) || !ReferenceEquals(owner, current)) return;
        RemoveMembership(owner); _actors.Remove(owner.Binding.Actor.Handle);
    }
    internal bool Owns(ZoneManagedNpcGroupLease owner) => !IsDisposed && !owner.IsDisposed && owner.Binding.IsCurrent &&
        _actors.TryGetValue(owner.Binding.Actor.Handle, out var current) && ReferenceEquals(owner, current);
    private ZoneManagedNpcGroupLease Require(ZoneManagedNpcActor actor)
    {
        Check(); ArgumentNullException.ThrowIfNull(actor); Prune();
        if (!_actors.TryGetValue(actor.Handle, out var owner) || !ReferenceEquals(owner.Binding.Actor, actor) || !Owns(owner))
            throw new ObjectDisposedException(nameof(ZoneManagedNpcGroupLease), "NPC group access requires the exact current script/actor allocation.");
        return owner;
    }
    private void Prune()
    { foreach (var owner in _actors.Values.ToArray()) if (!Owns(owner)) owner.Dispose(); }
    private void Check() => ObjectDisposedException.ThrowIf(IsDisposed, this);
    public void Dispose()
    { if (IsDisposed) return; IsDisposed = true; foreach (var owner in _actors.Values.ToArray()) owner.Dispose(); _groups.Clear(); }

    private sealed class Group
    {
        public List<ZoneManagedNpcGroupLease> Members { get; } = [];
        //3101BDD0 explicitly initializes AIGroup232 to null. The unsupported formation
        //producer must not publish a partial CLeader object or fabricated association.
        public Leader? LeaderObject { get; }
        public Group() { LeaderObject = null; }
    }
    private sealed record Leader(ZoneManagedNpcActor? Association);
}

/// <summary>One constructor/group registration lifetime; retiring an old lease cannot alter a replacement.</summary>
public sealed class ZoneManagedNpcGroupLease : IDisposable
{
    private readonly ZoneManagedNpcGroups _owner;
    internal ZoneManagedNpcScriptBinding Binding { get; }
    internal ZoneManagedNpcGroupRecord Record;
    internal bool InitialParametersApplied, AuthoredGroupInstalled, MembershipIndexed;
    public bool IsDisposed { get; private set; }
    public bool IsCurrent => _owner.Owns(this);
    internal ZoneManagedNpcGroupLease(ZoneManagedNpcGroups owner, ZoneManagedNpcScriptBinding binding)
    { _owner = owner; Binding = binding; }
    public void Dispose() { if (IsDisposed) return; IsDisposed = true; _owner.Retire(this); }
}
