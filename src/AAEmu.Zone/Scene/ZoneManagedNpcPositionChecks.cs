using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Npcs;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Scripting;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.Scene;

public sealed record ZoneManagedNpcPositionCheckResult(bool Relocated, Vector3? Destination = null, bool RestoredPosture = false);

/// <summary>
/// Original3933D0E0 position-context consumer. Borrows concrete group, canonical region,
/// model/body publication, replication and posture owners; it never supplies an AI locomotor.
/// </summary>
public sealed class ZoneManagedNpcPositionChecks(IZoneLuaRuntime lua, ZoneUnitRegistry units,
    ZoneManagedNpcGroups groups, ZoneManagedNpcRegions regions, ZoneManagedNpcRelocation relocation,
    ZoneManagedNpcPhysics bodies, ZoneManagedNpcSceneScriptServices components,
    ZoneManagedNpcActorMotion motion, ZoneManagedNpcReplication replication, ZoneManagedNpcPostureState postures,
    ZoneActorMovementWorldOrigin origin, Action<string>? trace = null) : IDisposable
{
    private readonly IZoneLuaRuntime _lua = lua ?? throw new ArgumentNullException(nameof(lua));
    private readonly ZoneUnitRegistry _units = units ?? throw new ArgumentNullException(nameof(units));
    private readonly ZoneManagedNpcGroups _groups = groups ?? throw new ArgumentNullException(nameof(groups));
    private readonly ZoneManagedNpcRegions _regions = regions ?? throw new ArgumentNullException(nameof(regions));
    private readonly ZoneManagedNpcRelocation _relocation = relocation ?? throw new ArgumentNullException(nameof(relocation));
    private readonly ZoneManagedNpcPhysics _bodies = bodies ?? throw new ArgumentNullException(nameof(bodies));
    private readonly ZoneManagedNpcSceneScriptServices _components = components ?? throw new ArgumentNullException(nameof(components));
    private readonly ZoneManagedNpcActorMotion _motion = motion ?? throw new ArgumentNullException(nameof(motion));
    private readonly ZoneManagedNpcReplication _replication = replication ?? throw new ArgumentNullException(nameof(replication));
    private readonly ZoneManagedNpcPostureState _postures = postures ?? throw new ArgumentNullException(nameof(postures));
    private readonly Dictionary<ZoneActorHandle, ZoneManagedNpcPositionCheckLease> _live = [];
    private bool _checking;
    public bool IsDisposed { get; private set; }
    public int Count => _live.Count;
    public ulong CorrectionCount { get; private set; }

    public ZoneManagedNpcPositionCheckLease BindEntity(ZoneManagedNpcScriptBinding binding)
    {
        Check(); ArgumentNullException.ThrowIfNull(binding); binding.RequireCurrent();
        if (!ReferenceEquals(binding.Script.Runtime, _lua)) throw new ArgumentException("Position checks require the actual bound Lua runtime.", nameof(binding));
        if (_live.ContainsKey(binding.Actor.Handle)) throw new InvalidOperationException("This NPC generation already owns position-check context.");
        if (!_replication.IsBound(binding.Actor) || _motion.Find(binding.Actor.Handle) is null || _postures.Find(binding.Actor) is null)
            throw new NotSupportedException("Position checks require actual generation-owned motion, replication and posture consumers.");
        var owner = new ZoneManagedNpcPositionCheckLease(this, binding); _live.Add(binding.Actor.Handle, owner); return owner;
    }

    public bool IsBound(ZoneManagedNpcActor actor) => !IsDisposed && _live.TryGetValue(actor.Handle, out var owner) &&
        ReferenceEquals(owner.Binding.Actor, actor) && owner.IsCurrent;
    public int CopyContext(ZoneManagedNpcActor actor) => Require(actor).Context;
    /// <summary>39354640 writes the signed integer verbatim; unknown values are diagnosed by Check.</summary>
    public void SetContext(ZoneManagedNpcActor actor, int context) => Require(actor).Context = context;

    public Vector3 GetSpawnPosition(ZoneManagedNpcActor actor) => ReadAnchor(Require(actor), "spawnPos") ??
        throw new NotSupportedException("NPC recovery requires its actual current AI.spawnPos vector; native uninitialized stack is not an anchor.");

    /// <summary>
    ///39353B80/3933B460 invokes the actual Unit168 model transform route. This performs
    ///no CheckPos region/distance test, replication-cache reset or posture restoration.
    /// </summary>
    public void Teleport(ZoneManagedNpcActor actor, Vector3 destination)
    {
        var owner = Require(actor);
        if (_checking) throw new InvalidOperationException("NPC model relocation cannot reenter its publication boundary.");
        _checking = true;
        try { Relocate(owner, destination); }
        finally { _checking = false; }
    }

    public ZoneManagedNpcPositionCheckResult Check(ZoneManagedNpcActor actor)
    {
        var owner = Require(actor);
        if (_checking) throw new InvalidOperationException("NPC position checks cannot reenter their correction boundary.");
        _checking = true;
        try
        {
            var position = _components.CopyModelTransform(actor.Npc.Unit).Position; Require(actor);
            var current = _regions.CurrentRegion;
            var region = _regions.GetRegion(position);
            Vector3? destination; var restorePosture = false;
            if (region is not null && ReferenceEquals(region, current))
            {
                var context = owner.Context;
                if (context is 0 or 4 or 5) return new(false);
                if (context is < 0 or > 7)
                { trace?.Invoke($"Unknown check pos context {context} for NPC {actor.Npc.Id}"); return new(false); }
                var groupedNonLeader = false;
                if (context is 1 or 2 or 3 or 6)
                {
                    var groupId = _groups.GetGroupId(actor); Require(actor);
                    groupedNonLeader = groupId != 0 && !ReferenceEquals(_groups.GetGroupLeader(groupId)?.AiObject, actor.AiObject);
                    Require(actor);
                }
                if (groupedNonLeader && context is 1 or 2 or 3) return new(false);
                var threshold = context switch { 1 => 15f, 2 or 3 => 7f, 6 => groupedNonLeader ? 50f : 15f, 7 => 3f, _ => 0 };
                destination = ReadAnchor(owner, context == 3 ? "talkPos" : "idlePos");
                if (destination is not { } anchor) return new(false);
                var dx = position.X - anchor.X; var dy = position.Y - anchor.Y; var dz = position.Z - anchor.Z;
                if (!((dx * dx + dy * dy) + dz * dz > threshold * threshold)) return new(false);
                restorePosture = context is 2 or 3;
            }
            else
            {
                //IPuppet.Alertness4160 is independent of CAIHandler's desired/behavior alertness.
                if (_motion.CopyPuppetAlertness(actor) == 2) return new(false);
                destination = GetSpawnPosition(actor);
            }
            Require(actor);
            if (destination is not { } target || !ReferenceEquals(_regions.GetRegion(target), current)) return new(false);
            Correct(owner, target, restorePosture);
            return new(true, target, restorePosture);
        }
        finally { _checking = false; }
    }

    private void Correct(ZoneManagedNpcPositionCheckLease owner, Vector3 target, bool restorePosture)
    {
        var actor = owner.Binding.Actor;
        if (restorePosture && _postures.Find(actor) is null)
            throw new ObjectDisposedException(nameof(ZoneManagedNpcPositionCheckLease), "NPC correction lost its posture consumer.");
        Relocate(owner, target);
        Require(actor); _replication.Reset(actor);
        if (restorePosture) { Require(actor); _postures.SetDefaultPosture(actor, false); }
        Require(actor); CorrectionCount++;
    }

    private void Relocate(ZoneManagedNpcPositionCheckLease owner, Vector3 target)
    {
        var actor = owner.Binding.Actor; Require(actor);
        if (!float.IsFinite(target.X) || !float.IsFinite(target.Y) || !float.IsFinite(target.Z))
            throw new InvalidDataException("NPC relocation requires a finite actual Lua anchor.");
        var unit = actor.Npc.Unit;
        var model = _components.CopyModelTransform(unit);
        var body = _bodies.Find(actor.Handle) ?? throw new NotSupportedException("NPC correction requires its exact actual living body.");
        var before = body.CopyState(); Require(actor);
        var orderedOrientation = body.TryReadPendingOrientation(out var pendingOrientation) ? pendingOrientation : before.Status.Orientation;
        Require(actor);
        if (model.Position != before.Status.Position || model.Orientation != orderedOrientation || model.UniformScale != before.EntityScale)
            throw new InvalidOperationException("NPC relocation preflight requires coherent model and applied or queued physical transforms.");
        if (!_replication.IsBound(actor) || _motion.Find(actor.Handle) is null)
            throw new ObjectDisposedException(nameof(ZoneManagedNpcPositionCheckLease), "NPC correction lost a required generation-owned consumer.");
        var spatial = _units.Spatial ?? throw new NotSupportedException("NPC relocation requires its actual spatial index.");
        spatial.ValidateUpdate(unit, target); Require(actor);
        _relocation.Relocate(actor, target, result =>
        {
            Require(actor);
            if (result.Current.Status.Orientation != orderedOrientation)
                throw new InvalidDataException("NPC relocation's ordered flush no longer matches its preflight logical entity orientation.");
            unit.CommitPose(new(ZoneMovementPoseMath.ToWorldPosition(result.Current.Status.Position, origin), result.Current.Status.Orientation));
            if (!_units.UpdateSpatialMembership(unit)) throw new InvalidOperationException("The relocated NPC lost its exact spatial membership.");
            Require(actor);
            var faults = _components.PublishPose(actor.Handle);
            if (faults.Count != 0) throw new NotSupportedException(string.Join("; ", faults.Select(fault => fault.Message)));
            Require(actor);
        });
        Require(actor);
    }

    private Vector3? ReadAnchor(ZoneManagedNpcPositionCheckLease owner, string field)
    {
        owner.Binding.RequireCurrent();
        using var ai = _lua.Get(owner.Binding.Script, "AI", 5) as ZoneLuaReference;
        owner.Binding.RequireCurrent(); if (ai is null) return null;
        using var value = _lua.Get(ai, field, 5) as ZoneLuaReference;
        owner.Binding.RequireCurrent(); if (value is null) return null;
        var vector = _lua.GetVector(value); owner.Binding.RequireCurrent(); return vector;
    }
    private ZoneManagedNpcPositionCheckLease Require(ZoneManagedNpcActor actor)
    {
        Check();
        return IsBound(actor) ? _live[actor.Handle] :
            throw new ObjectDisposedException(nameof(ZoneManagedNpcPositionCheckLease), "The exact NPC position-context generation retired.");
    }
    internal bool Owns(ZoneManagedNpcPositionCheckLease owner) => !IsDisposed &&
        _live.TryGetValue(owner.Binding.Actor.Handle, out var current) && ReferenceEquals(current, owner) &&
        owner.Binding.IsCurrent && _units.IsMovementCurrent(owner.Binding.Actor.Npc.Unit);
    internal void Retire(ZoneManagedNpcPositionCheckLease owner)
    { if (_live.TryGetValue(owner.Binding.Actor.Handle, out var current) && ReferenceEquals(current, owner)) _live.Remove(owner.Binding.Actor.Handle); }
    private void Check() => ObjectDisposedException.ThrowIf(IsDisposed, this);
    public void Dispose()
    { if (IsDisposed) return; IsDisposed = true; foreach (var owner in _live.Values.ToArray()) owner.Dispose(); }
}

public sealed class ZoneManagedNpcPositionCheckLease : IDisposable
{
    private readonly ZoneManagedNpcPositionChecks _owner;
    internal ZoneManagedNpcScriptBinding Binding { get; }
    //39345000 zeroes QWORD+152, including DWORD+156: fresh NPCs start at AICPC_NONE.
    //Matching PE39345158 writes the same eight bytes; the Lua setter may later replace them.
    internal int Context;
    public bool IsDisposed { get; private set; }
    public bool IsCurrent => !IsDisposed && _owner.Owns(this);
    internal ZoneManagedNpcPositionCheckLease(ZoneManagedNpcPositionChecks owner, ZoneManagedNpcScriptBinding binding)
    { _owner = owner; Binding = binding; }
    public void Dispose() { if (IsDisposed) return; IsDisposed = true; _owner.Retire(this); }
}
