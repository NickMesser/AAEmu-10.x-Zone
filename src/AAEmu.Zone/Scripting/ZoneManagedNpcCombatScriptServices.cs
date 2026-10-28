using AAEmu.Zone.Actors;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.Scripting;

/// <summary>
/// Actual CPipeUser AI-unit attribute record.31163974 initializes mode3 and pending/original
/// flying bytes0 at full+3592/+3596/+3597 (IAIActor interface+1288/+1292/+1293).
/// SaveUnitAttr31159914 snapshots the actual Unit flying byte; pending writes belong to the
/// separate fly-skill producers and are not invented by saving the record.
/// </summary>
public sealed class ZoneNpcAiUnitAttributeRestoreState
{
    public int Mode { get; private set; } = 3;
    public bool RestorePending { get; private set; }
    public bool OriginalFlying { get; private set; }
    public void CopyFields(int mode, bool pending, bool originalFlying)
    { Mode = mode; RestorePending = pending; OriginalFlying = originalFlying; }

    internal void Save(int changeOption, bool flying)
    {
        Mode = changeOption == 0 ? flying ? 2 : 3 : changeOption;
        OriginalFlying = flying;
        //31159914 writes+1288/+1293 only. In particular, +1292 is unchanged.
    }
}

/// <summary>
/// Bounded original ClearCombat for an actual fresh/no-target NPC actor and a clean attribute
/// restore record. Nonempty threat/hostility and saved flying restoration remain named refusals;
/// neither can be replaced with clearing attention or a Unit-only flying bit.
/// </summary>
public sealed class ZoneManagedNpcCombatScriptServices : IDisposable
{
    private readonly ZoneUnitRegistry _units;
    private readonly ZoneSceneNpcCatalog _catalog;
    private readonly Action<ZoneFrame> _send;
    private readonly Dictionary<ZoneActorHandle, ZoneManagedNpcCombatScriptLease> _actors = [];
    public bool IsDisposed { get; private set; }
    public ZoneManagedNpcCombatScriptServices(ZoneUnitRegistry units, ZoneSceneNpcCatalog catalog, Action<ZoneFrame> send)
    { _units = units ?? throw new ArgumentNullException(nameof(units)); _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog)); _send = send ?? throw new ArgumentNullException(nameof(send)); }

    public ZoneManagedNpcCombatScriptLease Bind(ZoneManagedNpcActor actor, ZoneNpcAiUnitAttributeRestoreState state)
    {
        Check(); ArgumentNullException.ThrowIfNull(state); RequireActor(actor);
        if (!actor.IsPuppet) throw new NotSupportedException("AI-unit restoration requires an admitted CPipeUser/Puppet construction record.");
        if (_actors.TryGetValue(actor.Handle, out var previous))
        {
            if (previous.IsCurrent) throw new InvalidOperationException("The actor already owns its combat script record.");
            previous.Dispose();
        }
        var owner = new ZoneManagedNpcCombatScriptLease(this, actor, state); _actors.Add(actor.Handle, owner); return owner;
    }

    public void ClearCombat(ZoneManagedNpcActor actor)
    {
        Check(); RequireActor(actor);
        if (!_actors.TryGetValue(actor.Handle, out var owner) || !ReferenceEquals(owner.Actor, actor) || !owner.IsCurrent)
            throw new InvalidOperationException("ClearCombat requires its exact owned AI-unit restoration record.");
        //Preflight the bounded profile before any mutation. Native non-hostile target handling
        //removes reverse attackers and may clear OTHER Units; that chain is not an attention reset.
        if (actor.Npc.Aggro.TargetId != 0 || actor.Npc.Aggro.Entries.Count != 0)
            throw new NotSupportedException("ClearCombat with threat entries requires actual AI hostility, reverse-attacker combat removal and notifications.");
        if (owner.Attributes.RestorePending)
            throw new NotSupportedException("ClearCombat saved flying restoration requires its actual Unit, CActor and AI flying/navigation-parameter consumers.");
        //393534D0: no target ->393200D0(reason10). This still clears owned sleep/abuser
        //state when the entry array is empty; the existing aggro world owns its notifications.
        actor.Npc.Aggro.Clear(10); RequireActor(actor);
        //311599D4 -> CAIProxy328 -> CActor288 returns the bound WORLD UNIT ID, then
        //CGame912 -> ServerAIUnitSystem344/39325C20. A clean saved record has no writes.
        if (!actor.Npc.Unit.IsInCombat) return;
        actor.Npc.Unit.ClearCombatFromScript(); RequireActor(actor);
        var writer = new ZoneWriter(); writer.WriteBc(actor.Npc.ObjectId);
        _send(new(ZwOpcodes.ClearCombat, writer.ToArray()));
    }

    /// <summary>
    ///39351510 -> IAIActor+424/31159914: retain the signed32-bit change option (zero chooses
    /// mode2 for flying,3 otherwise), then save actual Unit.IsFlying. This does not arm a
    /// restore, change flying/navigation/physics, clear combat, or send a World request.
    /// </summary>
    public void SaveUnitAttr(ZoneManagedNpcActor actor, int changeOption)
    {
        Check(); RequireActor(actor);
        if (!_actors.TryGetValue(actor.Handle, out var owner) || !ReferenceEquals(owner.Actor, actor) || !owner.IsCurrent)
            throw new InvalidOperationException("SaveUnitAttr requires its exact owned AI-unit attribute record.");
        owner.Attributes.Save(changeOption, actor.Npc.Unit.IsFlying);
    }

    private void RequireActor(ZoneManagedNpcActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!actor.HasActor || !ReferenceEquals(_catalog.Find(actor.Handle), actor.Npc) ||
            !_units.Units.TryGetValue(actor.Npc.ObjectId, out var unit) || !ReferenceEquals(unit, actor.Npc.Unit))
            throw new InvalidOperationException("NPC combat script effects require their exact actor, NPC and World Unit generation.");
    }
    private void Check() => ObjectDisposedException.ThrowIf(IsDisposed, this);
    internal void Retire(ZoneManagedNpcCombatScriptLease owner)
    { if (_actors.TryGetValue(owner.Actor.Handle, out var current) && ReferenceEquals(current, owner)) _actors.Remove(owner.Actor.Handle); }
    public void Dispose()
    { if (IsDisposed) return; IsDisposed = true; foreach (var owner in _actors.Values.ToArray()) owner.Dispose(); }
}

public sealed class ZoneManagedNpcCombatScriptLease : IDisposable
{
    private readonly ZoneManagedNpcCombatScriptServices _owner;
    internal ZoneManagedNpcActor Actor { get; }
    public ZoneNpcAiUnitAttributeRestoreState Attributes { get; }
    public bool IsDisposed { get; private set; }
    public bool IsCurrent => !IsDisposed && !_owner.IsDisposed && Actor.HasActor;
    internal ZoneManagedNpcCombatScriptLease(ZoneManagedNpcCombatScriptServices owner, ZoneManagedNpcActor actor,
        ZoneNpcAiUnitAttributeRestoreState attributes) { _owner = owner; Actor = actor; Attributes = attributes; }
    public void Dispose() { if (IsDisposed) return; IsDisposed = true; _owner.Retire(this); }
}
