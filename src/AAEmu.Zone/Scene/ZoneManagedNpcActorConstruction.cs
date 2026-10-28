using AAEmu.Zone.Actors;
using AAEmu.Zone.Content;
using AAEmu.Zone.Npcs;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Scripting;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.Scene;

public enum ZoneManagedNpcActorClass { Player, Monster, Quadruped }

/// <summary>
/// The nineteen scalar fields actually overwritten by CActor392DF300. This is deliberately
/// not a claim to own the entire GameParams structure, animation graph or player IK settings.
/// These values are not scaled; the separate stance catalog applies the entity scale.
/// </summary>
public sealed record ZoneManagedNpcGameParameters(
    float MaximumGrabMass, float MaximumGrabVolume, float SprintMultiplier,
    float ForwardMultiplier, float ForwardDiagonalMultiplier, float StrafeMultiplier,
    float BackwardMultiplier, float BackwardDiagonalMultiplier, float GrabMultiplier,
    float WalkMultiplier, float WalkForwardDiagonalMultiplier, float WalkStrafeMultiplier,
    float WalkBackwardMultiplier, float WalkBackwardDiagonalMultiplier,
    float Inertia, float InertiaAcceleration, float JumpHeight, float LeanShift, float LeanAngle)
{
    internal static ZoneManagedNpcGameParameters FromModel(ZoneActorModelDefinition model)
    {
        var values = new ZoneManagedNpcGameParameters(model.GameMaxGrabMass, model.GameMaxGrabVolume,
            model.GameSprintMultiplier, model.GameForwardMultiplier, model.GameForwardDiagonalMultiplier,
            model.GameStrafeMultiplier, model.GameBackwardMultiplier, model.GameBackwardDiagonalMultiplier,
            model.GameGrabMultiplier, model.GameWalkMultiplier, model.GameWalkForwardDiagonalMultiplier,
            model.GameWalkStrafeMultiplier, model.GameWalkBackwardMultiplier, model.GameWalkBackwardDiagonalMultiplier,
            model.GameInertia, model.GameInertiaAccel, model.GameJumpHeight, model.GameLeanShift, model.GameLeanAngle);
        foreach (var value in new[] { values.MaximumGrabMass, values.MaximumGrabVolume, values.SprintMultiplier,
            values.ForwardMultiplier, values.ForwardDiagonalMultiplier, values.StrafeMultiplier,
            values.BackwardMultiplier, values.BackwardDiagonalMultiplier, values.GrabMultiplier,
            values.WalkMultiplier, values.WalkForwardDiagonalMultiplier, values.WalkStrafeMultiplier,
            values.WalkBackwardMultiplier, values.WalkBackwardDiagonalMultiplier, values.Inertia,
            values.InertiaAcceleration, values.JumpHeight, values.LeanShift, values.LeanAngle })
            if (!float.IsFinite(value))
                throw new NotSupportedException("NPC CActor descriptor parameters must be finite.");
        return values;
    }
}

/// <summary>
/// Defined writes made by the derived actor's postphysicalization routine to the existing
/// animated-character parameter block:372/376/380, flags OR0x18, and byte400=1. No unknown
/// preserved bytes are manufactured. The body still owns its independently applied dynamics.
/// </summary>
public sealed record ZoneManagedNpcAnimatedParameters(float Inertia, float InertiaAcceleration,
    float ImpulseRecoveryTime)
{
    public uint ParameterFlagsToSet => 0x18;
    public bool UpdateRequired => true;
}

/// <summary>
/// Owns the verified CActor construction subset for exact registered NPC generations. It owns
/// neither Unit statistics nor bodies, Lua, proxies, scripts or movement. Call Physicalized
/// only after real body creation, and supply its copied parameters to the animated consumer.
/// </summary>
public sealed class ZoneManagedNpcActorConstruction : IDisposable
{
    private readonly ZoneUnitRegistry _units;
    private readonly ZoneSceneNpcCatalog _catalog;
    private readonly ZoneModelContent _models;
    private readonly Dictionary<ZoneActorHandle, ZoneManagedNpcActorConstructionLease> _actors = [];
    public bool IsDisposed { get; private set; }
    public int Count => _actors.Count;
    public static string CapabilitySummary =>
        "Exact model-class selection, descriptor-written CActor scalars, source-order scaled stances, " +
        "independent constructor health and postphysicalization animated parameters; " +
        "full GameParams, actor frame statistics and outgoing movement metadata remain separate owners.";

    public ZoneManagedNpcActorConstruction(ZoneUnitRegistry units, ZoneSceneNpcCatalog catalog, ZoneModelContent models)
    {
        _units = units ?? throw new ArgumentNullException(nameof(units));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _models = models ?? throw new ArgumentNullException(nameof(models));
    }

    public ZoneManagedNpcActorConstructionLease Create(ZoneActorHandle actor, ZoneNpc npc)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(npc);
        if (!Current(actor, npc))
            throw new InvalidOperationException("CActor construction requires its exact live NPC catalog and registered Unit owners.");
        if (_actors.TryGetValue(actor, out var existing))
        {
            if (existing.IsCurrent) throw new InvalidOperationException("This NPC generation already owns CActor construction state.");
            existing.Dispose();
        }
        var modelId = npc.Unit.Snapshot.ModelId;
        if (!_models.Models.TryGetValue(modelId, out var model) || model.Kind != ZoneModelKind.Actor ||
            _models.ActorForModel(modelId) is not { } actorModel)
            throw new NotSupportedException($"World NPC model {modelId} has no resolved CActor descriptor.");
        var scale = npc.Unit.Scale.BaseScale;
        if (!float.IsFinite(scale) || scale <= 0 || npc.Unit.Scale.Multiplier != 1)
            throw new NotSupportedException("CActor initial construction requires a finite positive World scale; live scale changes need their own owner.");
        var parameters = ZoneManagedNpcGameParameters.FromModel(actorModel.Definition);
        if (!float.IsFinite(actorModel.Definition.PhysicsLivingTimeImpulseRecover))
            throw new NotSupportedException("NPC impulse recovery time must be finite.");
        var result = new ZoneManagedNpcActorConstructionLease(this, actor, npc, modelId, actorModel, scale,
            parameters, new(actorModel, scale));
        _actors.Add(actor, result);
        return result;
    }

    public ZoneManagedNpcActorConstructionLease? Find(ZoneActorHandle actor) =>
        !IsDisposed && _actors.TryGetValue(actor, out var result) && result.IsCurrent ? result : null;

    private bool Current(ZoneActorHandle actor, ZoneNpc npc) => actor.UnitObjectId != 0 && actor.Generation != 0 &&
        actor.UnitObjectId == npc.ObjectId && ReferenceEquals(_catalog.Find(actor), npc) &&
        _units.Units.TryGetValue(npc.ObjectId, out var unit) && ReferenceEquals(unit, npc.Unit);

    internal bool Owns(ZoneManagedNpcActorConstructionLease actor) => !IsDisposed &&
        _actors.TryGetValue(actor.Actor, out var current) && ReferenceEquals(current, actor) &&
        Current(actor.Actor, actor.Npc) && actor.ModelId == actor.Npc.Unit.Snapshot.ModelId &&
        actor.EntityScale == actor.Npc.Unit.Scale.BaseScale && actor.Npc.Unit.Scale.Multiplier == 1;
    internal void Retire(ZoneManagedNpcActorConstructionLease actor)
    {
        if (_actors.TryGetValue(actor.Actor, out var current) && ReferenceEquals(current, actor)) _actors.Remove(actor.Actor);
    }
    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        foreach (var actor in _actors.Values.ToArray()) actor.Dispose();
    }
}

/// <summary>
/// An initial native actor instance, distinct from its CPuppet, model resource and Unit. Derived
/// class selection39277AC0 uses MovementId1/3=>Quadruped,2=>Monster,otherwise CPlayer. The
/// descriptor producer runs before body physicalization392E2F20 and derived virtual+560.
/// </summary>
public sealed class ZoneManagedNpcActorConstructionLease : IDisposable
{
    private readonly ZoneManagedNpcActorConstruction _owner;
    private readonly float _impulseRecoveryTime;
    private ZoneManagedNpcBodyLease? _body;
    private ZoneManagedNpcAnimatedParameters? _animated;
    public ZoneActorHandle Actor { get; }
    public ZoneNpc Npc { get; }
    public uint ModelId { get; }
    public uint ActorModelId { get; }
    public float EntityScale { get; }
    public ZoneManagedNpcActorClass ClassKind { get; }
    public ZoneManagedNpcGameParameters GameParams { get; }
    public ZoneActorStanceCatalog Stances { get; }
    public ZoneManagedNpcActorHealth Health { get; } = new();
    public int? PhysicalizedFrame { get; private set; }
    public bool IsDisposed { get; private set; }
    public bool IsCurrent => !IsDisposed && _owner.Owns(this);

    internal ZoneManagedNpcActorConstructionLease(ZoneManagedNpcActorConstruction owner, ZoneActorHandle actor,
        ZoneNpc npc, uint modelId, ZoneActorModel model, float scale, ZoneManagedNpcGameParameters parameters,
        ZoneActorStanceCatalog stances)
    {
        _owner = owner; Actor = actor; Npc = npc; ModelId = modelId; ActorModelId = model.Id; EntityScale = scale;
        ClassKind = model.Definition.MovementId switch
        { 1 or 3 => ZoneManagedNpcActorClass.Quadruped, 2 => ZoneManagedNpcActorClass.Monster, _ => ZoneManagedNpcActorClass.Player };
        GameParams = parameters; Stances = stances; _impulseRecoveryTime = model.Definition.PhysicsLivingTimeImpulseRecover;
    }

    /// <summary>
    /// Record the actual body creation, not a speculative model-row installation. The immutable
    /// result is consumed by the scene's animated collider controller; this method never Steps,
    /// resets velocity/support, changes a stance, writes a Unit pose or owns the passed body.
    /// </summary>
    public ZoneManagedNpcAnimatedParameters Physicalized(ZoneManagedNpcBodyLease body, int frame)
    {
        Require(); ArgumentNullException.ThrowIfNull(body);
        if (frame < 0) throw new ArgumentOutOfRangeException(nameof(frame));
        if (body.Actor != Actor || !ReferenceEquals(body.Npc, Npc) || body.Admission.ModelId != ModelId ||
            body.Admission.Request.ActorModel.Id != ActorModelId || body.Admission.Request.EntityScale != EntityScale || !body.IsCurrent)
            throw new InvalidOperationException("CActor physicalization must use this exact generation's admitted body, model and scale.");
        Require(); // The body's exact-owner callback may retire this construction owner.
        if (_body is not null && !ReferenceEquals(_body, body))
            throw new NotSupportedException("Replacing a physical body requires a new CActor construction/event owner.");
        var installed = new ZoneManagedNpcAnimatedParameters(GameParams.Inertia, GameParams.InertiaAcceleration, _impulseRecoveryTime);
        _body = body; _animated = installed; PhysicalizedFrame = frame;
        return installed;
    }

    public ZoneManagedNpcAnimatedParameters CopyAnimatedParameters()
    {
        Require();
        if (_animated is null || _body is null)
            throw new InvalidOperationException("Animated-character parameters have not been installed by an actual physicalization event.");
        if (!_body.IsCurrent) throw new ObjectDisposedException(nameof(ZoneManagedNpcBodyLease), "The physicalized body retired.");
        Require();
        return _animated;
    }

    private void Require()
    { if (!IsCurrent) throw new ObjectDisposedException(nameof(ZoneManagedNpcActorConstructionLease), "The exact CActor construction generation retired or changed model/scale."); }
    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true; _body = null; _animated = null;
        _owner.Retire(this);
    }
}
