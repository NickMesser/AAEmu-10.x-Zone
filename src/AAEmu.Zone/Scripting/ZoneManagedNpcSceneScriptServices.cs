using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Content;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.Scripting;

/// <summary>Copied, actually installed animated-character dynamics, not model-row defaults inferred as applied.</summary>
public readonly record struct ZoneManagedNpcColliderDynamics(float Inertia, float InertiaAcceleration);

/// <summary>
/// Composes actual character, smart-object, living-body and entity-script owners for an admitted
/// generation. This borrows the scene's one physics manager; it never steps physics or writes a pose.
/// Actor construction still has to supply installed animated-character parameters and a profile.
/// Optional combat services transfer to this owner; their nonempty-threat/flying paths and
/// unowned posture, casting and position-check services retain named refusals.
/// </summary>
public sealed class ZoneManagedNpcSceneScriptServices : IZoneManagedNpcScriptServices, IZoneManagedNpcScriptLifecycle, IDisposable
{
    private readonly ZoneModelContent _models;
    private readonly Func<ZoneManagedNpcPhysics> _physics;
    private readonly Func<ZoneManagedNpcActor, ZoneManagedNpcColliderDynamics> _colliderDynamics;
    private readonly Func<ZoneUnit, Vector3> _position;
    private readonly Func<bool> _spectatorCollisions;
    private readonly ZoneManagedNpcGameScriptRequests? _requests;
    private readonly ZoneManagedNpcCombatScriptServices? _combat;
    private readonly Dictionary<ZoneActorHandle, Components> _actors = [];
    public ZoneManagedNpcScriptEffects Effects { get; }
    public ZoneManagedNpcSmartObjectStates SmartObjects { get; }
    public ZoneManagedCharacterSlots Characters { get; }
    public bool IsDisposed { get; private set; }
    public int Count => _actors.Count;
    public ulong ConfirmedAbsentAttachmentLookups { get; private set; }
    public ulong AppliedAttachmentVisibilityUpdates { get; private set; }
    public ulong DeliveredColliderEvents { get; private set; }

    public ZoneManagedNpcSceneScriptServices(IZoneLuaRuntime lua, ZoneModelContent models, IZoneAssetSource assets,
        Func<ZoneManagedNpcPhysics> physics, Func<ZoneManagedNpcActor, ZoneManagedNpcColliderDynamics> colliderDynamics,
        Func<ZoneUnit, Vector3> position, Func<long> gameMicroseconds, Func<bool> spectatorCollisions,
        ZoneCharacterAttachmentPublicationProfile attachments, ZoneManagedNpcGameScriptRequests? requests = null,
        Action<string>? trace = null, ZoneManagedNpcCombatScriptServices? combat = null)
    {
        ArgumentNullException.ThrowIfNull(lua);
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _physics = physics ?? throw new ArgumentNullException(nameof(physics));
        _colliderDynamics = colliderDynamics ?? throw new ArgumentNullException(nameof(colliderDynamics));
        _position = position ?? throw new ArgumentNullException(nameof(position));
        _spectatorCollisions = spectatorCollisions ?? throw new ArgumentNullException(nameof(spectatorCollisions));
        _requests = requests;
        _combat = combat;
        Effects = new(lua, gameMicroseconds, trace);
        SmartObjects = new(lua);
        // The allocation dictionary, plus the binding's exact currency, owns character access.
        Characters = new(assets, handle => !IsDisposed && _actors.TryGetValue(handle, out var owner) && owner.Binding.IsCurrent, attachments);
    }

    public IDisposable BindEntity(ZoneManagedNpcScriptBinding binding)
    {
        Check(); ArgumentNullException.ThrowIfNull(binding); binding.RequireCurrent();
        if (_actors.ContainsKey(binding.Actor.Handle)) throw new InvalidOperationException("This actor generation already owns its scene components.");
        var owner = new Components(this, binding);
        _actors.Add(binding.Actor.Handle, owner);
        try
        {
            var model = _models.ActorForModel(binding.Actor.Npc.Unit.Snapshot.ModelId) ??
                throw new NotSupportedException("The World NPC model does not resolve an actor construction profile.");
            owner.Character = Characters.BindInitial(binding.Actor.Handle, model);
            binding.RequireCurrent();
            var parameters = _colliderDynamics(binding.Actor);
            binding.RequireCurrent();
            var physics = _physics();
            binding.RequireCurrent();
            var position = _position(binding.Actor.Npc.Unit);
            binding.RequireCurrent();
            owner.Body = physics.Create(binding.Actor.Handle, binding.Actor.Npc, position,
                binding.Actor.Npc.Unit.Pose.Rotation, () => !IsDisposed && binding.IsCurrent);
            owner.Animated = new(() => !IsDisposed && binding.IsCurrent && owner.Body.IsCurrent,
                owner.Body, parameters.Inertia, parameters.InertiaAcceleration, _spectatorCollisions);
            owner.Events = new(binding.Actor, owner.Animated);
            owner.SmartObject = SmartObjects.BindEntity(binding, new());
            owner.Effects = Effects.BindEntity(binding, new());
            if (_combat is not null) owner.Combat = _combat.Bind(binding.Actor, new());
            //392587B0/39258800 ->39259530 sends entity44 only on an actual combat-bit
            //transition. No initial interval is selected by polling the snapshot flag.
            owner.CombatTransitioned = (unit, engaged) =>
            {
                if (!ReferenceEquals(unit, binding.Actor.Npc.Unit)) throw new InvalidOperationException("Combat event44 belongs to another Unit owner.");
                Require(binding.Actor); Effects.SelectScriptUpdateInterval(binding.Actor, engaged);
            };
            binding.Actor.Npc.Unit.CombatTransitioned += owner.CombatTransitioned;
            binding.RequireCurrent();
            return owner;
        }
        catch (Exception failure)
        {
            try { owner.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException("NPC scene component admission and retirement failed.", failure, cleanup); }
            throw;
        }
    }

    public IReadOnlyList<ZoneManagedNpcScriptFault> Tick(TimeSpan elapsed) { Check(); return Effects.Tick(elapsed); }
    public float GetActorHealth(ZoneManagedNpcActor actor) { Require(actor); return Effects.GetActorHealth(actor); }
    public float GetActorMaximumHealth(ZoneManagedNpcActor actor) { Require(actor); return Effects.GetActorMaximumHealth(actor); }
    public void SetEntityTimer(ZoneManagedNpcActor actor, int id, int milliseconds) { Require(actor); Effects.SetEntityTimer(actor, id, milliseconds); }
    public void KillEntityTimer(ZoneManagedNpcActor actor, int id) { Require(actor); Effects.KillEntityTimer(actor, id); }
    public void SetScriptUpdateRate(ZoneManagedNpcActor actor, float combat, float peace) { Require(actor); Effects.SetScriptUpdateRate(actor, combat, peace); }
    public void HideAttachment(ZoneManagedNpcActor actor, int slot, string name, bool hide, bool shadow)
    {
        Require(actor);
        var result = Characters.HideAttachment(actor.Handle, slot, name, hide, shadow);
        if (result is ZoneManagedAttachmentHideResult.AbsentSlot or ZoneManagedAttachmentHideResult.AbsentAttachment)
        { if (ConfirmedAbsentAttachmentLookups != ulong.MaxValue) ConfirmedAbsentAttachmentLookups++; }
        else if (AppliedAttachmentVisibilityUpdates != ulong.MaxValue) AppliedAttachmentVisibilityUpdates++;
    }
    public void SetColliderMode(ZoneManagedNpcActor actor, int mode)
    {
        Require(actor).Events!.SetColliderMode(mode);
        if (DeliveredColliderEvents != ulong.MaxValue) DeliveredColliderEvents++;
    }
    public Vector3 GetDirectionVector(ZoneManagedNpcActor actor, int axis)
    {
        var owner = Require(actor);
        var body = owner.Body ?? throw new NotSupportedException("Entity orientation requires its actual admitted transform owner.");
        if (!body.IsCurrent) throw new ObjectDisposedException(nameof(ZoneManagedNpcBodyLease));
        var unit = actor.Npc.Unit;
        if (unit.Scale.Multiplier != 1 || unit.Scale.BaseScale != body.Admission.Request.EntityScale)
            throw new NotSupportedException("Entity direction after a scale transition requires its applied model-transform owner.");
        // Parentless admitted entities use the scene-published rotation and applied initial scale.
        //3203CB90 copies the world matrix column without normalizing it.
        return ZoneManagedEntityOrientation.GetDirectionVector(unit.Pose.Rotation, body.Admission.Request.EntityScale, axis);
    }
    public void SetSmartObjectState(ZoneManagedNpcActor actor, string state, bool modify)
    { Require(actor); if (modify) SmartObjects.AddState(actor, state); else SmartObjects.SetState(actor, state); }
    public void ModifySmartObjectStates(ZoneManagedNpcActor actor, string expression)
    { Require(actor); SmartObjects.ModifyStates(actor, expression); }
    public bool RemoveBuff(ZoneManagedNpcActor actor, uint buffType)
    { Require(actor); return Requests().RemoveBuff(actor, buffType); }
    public void StopCasting(ZoneManagedNpcActor actor) { Require(actor); Requests().StopCasting(actor); }
    public void ClearCombat(ZoneManagedNpcActor actor)
    {
        Require(actor);
        (_combat ?? throw new NotSupportedException("NPC ClearCombat requires its actual AI-unit restoration and combat service owner.")).ClearCombat(actor);
    }
    public ZoneAnimatedColliderState CopyColliderState(ZoneManagedNpcActor actor) => Require(actor).Animated!.CopyState();
    public ZoneManagedNpcColliderEvent? CopyLastColliderEvent(ZoneManagedNpcActor actor) => Require(actor).Events!.LastDelivered;

    private ZoneManagedNpcGameScriptRequests Requests() => _requests ??
        throw new NotSupportedException("NPC World script requests require their actual request sender and copied casting-field owner.");
    private Components Require(ZoneManagedNpcActor actor)
    {
        Check(); ArgumentNullException.ThrowIfNull(actor);
        if (!_actors.TryGetValue(actor.Handle, out var owner) || !ReferenceEquals(owner.Binding.Actor, actor) || owner.IsDisposed)
            throw new InvalidOperationException("NPC scene services require their exact generation's component owner.");
        owner.Binding.RequireCurrent(); return owner;
    }
    private void Check() => ObjectDisposedException.ThrowIf(IsDisposed, this);
    public void Dispose()
    {
        if (IsDisposed) return; IsDisposed = true;
        List<Exception>? errors = null;
        foreach (var owner in _actors.Values.ToArray()) Release(owner.Dispose);
        Release(() => _combat?.Dispose()); Release(Characters.Dispose); Release(SmartObjects.Dispose); Release(Effects.Dispose);
        if (errors is not null) throw new AggregateException("NPC scene script services failed to retire.", errors);
        void Release(Action action) { try { action(); } catch (Exception error) { (errors ??= []).Add(error); } }
    }

    private sealed class Components(ZoneManagedNpcSceneScriptServices scene, ZoneManagedNpcScriptBinding binding) : IDisposable
    {
        public ZoneManagedNpcScriptBinding Binding { get; } = binding;
        public ZoneManagedCharacterSlotOwner? Character;
        public ZoneManagedNpcBodyLease? Body;
        public ZoneAnimatedColliderController? Animated;
        public ZoneManagedNpcColliderEvents? Events;
        public ZoneManagedNpcSmartObjectLease? SmartObject;
        public ZoneManagedNpcScriptEffectsLease? Effects;
        public ZoneManagedNpcCombatScriptLease? Combat;
        public Action<ZoneUnit, bool>? CombatTransitioned;
        public bool IsDisposed { get; private set; }
        public void Dispose()
        {
            if (IsDisposed) return; IsDisposed = true;
            if (CombatTransitioned is { } handler) Binding.Actor.Npc.Unit.CombatTransitioned -= handler;
            CombatTransitioned = null;
            if (scene._actors.TryGetValue(Binding.Actor.Handle, out var current) && ReferenceEquals(current, this)) scene._actors.Remove(Binding.Actor.Handle);
            List<Exception>? errors = null;
            Release(() => Combat?.Dispose()); Release(() => Effects?.Dispose()); Release(() => SmartObject?.Dispose()); Release(() => Events?.Dispose());
            Release(() => Animated?.Dispose()); Release(() => Body?.Dispose()); Release(() => Character?.Dispose());
            if (errors is not null) throw new AggregateException("NPC generation components failed to retire.", errors);
            void Release(Action action) { try { action(); } catch (Exception error) { (errors ??= []).Add(error); } }
        }
    }
}
