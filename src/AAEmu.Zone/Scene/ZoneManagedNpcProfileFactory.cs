using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Ai;
using AAEmu.Zone.Content;
using AAEmu.Zone.Hosting;
using AAEmu.Zone.Npcs;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.Scene;

/// <summary>
/// Allocates the verified initial CPuppet script-construction state for an exact NPC generation.
/// This does not supply a physical entity, persistent COPStick, perception or the full actor
/// parameter block; those service admissions remain separate from successful Lua initialization.
/// </summary>
public sealed class ZoneManagedNpcProfileFactory : IDisposable
{
    private static long _nextProxyKey;
    private readonly ZoneUnitRegistry _units;
    private readonly ZoneSceneNpcCatalog _catalog;
    private readonly Func<ZoneUnit, Vector3> _position;
    private readonly Dictionary<ZoneActorHandle, ZoneManagedNpcProxyOwner> _live = [];
    public bool IsDisposed { get; private set; }
    public int LiveCount => _live.Count;
    public ulong RetiredCount { get; private set; }
    public static string CapabilitySummary =>
        "Original initial CPuppet identity/control state and managed proxy activation lifetime; " +
        "physical entity, complete Puppet parameters, persistent stick, perception and gameplay admission remain separate.";

    public ZoneManagedNpcProfileFactory(ZoneUnitRegistry units, ZoneSceneNpcCatalog catalog, Func<ZoneUnit, Vector3> position)
    {
        _units = units ?? throw new ArgumentNullException(nameof(units));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _position = position ?? throw new ArgumentNullException(nameof(position));
    }

    public ZoneManagedNpcActorProfile Create(ZoneActorHandle actor, ZoneNpc npc, uint entityId)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(npc);
        if (entityId == 0) throw new ArgumentOutOfRangeException(nameof(entityId));
        if (!ReferenceEquals(_catalog.Find(actor), npc) ||
            !_units.Units.TryGetValue(npc.ObjectId, out var unit) || !ReferenceEquals(unit, npc.Unit))
            throw new InvalidOperationException("NPC proxy creation requires its exact live catalog and registered Unit owners.");
        if (!ZoneAiBackendBindings.IsBound(actor))
            throw new InvalidOperationException("Claim the NPC's AI backend before constructing its managed proxy.");
        if (npc.Model.Kind != ZoneModelKind.Actor)
            throw new NotSupportedException($"NPC model {npc.Model.Id} does not resolve an ActorModel construction profile.");
        if (_live.ContainsKey(actor)) throw new InvalidOperationException("This NPC generation already owns a managed proxy.");
        var position = _position(npc.Unit);
        // Position callbacks may retire the generation or the enclosing scene.
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (!ReferenceEquals(_catalog.Find(actor), npc) ||
            !_units.Units.TryGetValue(npc.ObjectId, out unit) || !ReferenceEquals(unit, npc.Unit) ||
            !ZoneAiBackendBindings.IsBound(actor))
            throw new InvalidOperationException("The NPC generation retired while resolving its proxy position.");
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
            throw new InvalidOperationException("NPC proxy positions must be finite.");
        var key = Interlocked.Increment(ref _nextProxyKey);
        if (key <= 0) throw new InvalidOperationException("Managed NPC proxy allocation keys are exhausted.");
        // 3105CE68(type5) -> 31171F8C -> 3103D7F8 initializes the independent AI-object
        // radius to0. Entity registration32012370 and initial parameter application do not
        // replace it with actor passRadius, model radius or collider radius.
        var aiObject = new ZoneAiObjectState { EntityId = entityId, Position = position, Radius = 0 };
        var controls = ZoneManagedNpcActorScene.CreatePipeUserControls();
        controls.SetDelayedStance(-1); // CPuppet31171F8C initializes its own +4448 field.
        var owner = new ZoneManagedNpcProxyOwner(this, actor, npc, entityId, (ulong)key, aiObject);
        _live.Add(actor, owner);
        return new(aiObject, 5, owner.AllocationKey, true, controls, owner);
    }

    internal void Retire(ZoneManagedNpcProxyOwner owner)
    {
        if (!_live.TryGetValue(owner.Actor, out var current) || !ReferenceEquals(current, owner)) return;
        _live.Remove(owner.Actor);
        if (RetiredCount != ulong.MaxValue) RetiredCount++;
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        List<Exception>? errors = null;
        foreach (var owner in _live.Values.ToArray())
            try { owner.Dispose(); } catch (Exception error) { (errors ??= []).Add(error); }
        if (errors is not null) throw new AggregateException("Managed NPC proxy retirement failed.", errors);
    }
}

/// <summary>
/// One managed proxy allocation, distinct from World/NPC/entity IDs. After adoption it retains
/// the actual registry entry whose cadence state consumes this key; retirement seals that owner.
/// The enclosing runtime separately owns entity-script timers, Lua references and physics.
/// </summary>
public sealed class ZoneManagedNpcProxyOwner : IDisposable
{
    private readonly ZoneManagedNpcProfileFactory _factory;
    private readonly ZoneAiObjectState _object;
    private ZoneManagedNpcActor? _attached;
    public ZoneActorHandle Actor { get; }
    public ZoneNpc Npc { get; }
    public uint EntityId { get; }
    public ulong AllocationKey { get; }
    public bool IsDisposed { get; private set; }
    public ZoneNpcActorEntry? Entry => IsDisposed ? null : _attached?.Entry;

    internal ZoneManagedNpcProxyOwner(ZoneManagedNpcProfileFactory factory, ZoneActorHandle actor,
        ZoneNpc npc, uint entityId, ulong allocationKey, ZoneAiObjectState aiObject)
    {
        _factory = factory; Actor = actor; Npc = npc; EntityId = entityId; AllocationKey = allocationKey; _object = aiObject;
    }

    public void Attach(ZoneManagedNpcActor actor)
    {
        ObjectDisposedException.ThrowIf(IsDisposed || _factory.IsDisposed, this);
        ArgumentNullException.ThrowIfNull(actor);
        if (_attached is not null) throw new InvalidOperationException("A managed proxy can be attached only once.");
        if (!actor.HasActor || actor.Handle != Actor || !ReferenceEquals(actor.Npc, Npc) ||
            !ReferenceEquals(actor.AiObject, _object) || actor.Entry.EntityId != EntityId ||
            actor.Entry.ProxyAllocationKey != AllocationKey)
            throw new InvalidOperationException("The proxy must attach to its exact adopted NPC actor and allocation key.");
        _attached = actor;
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        var attached = _attached; _attached = null;
        _factory.Retire(this);
        attached?.Dispose();
    }
}
