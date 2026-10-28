using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Content;
using AAEmu.Zone.Npcs;
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
public sealed class ZoneManagedNpcSceneScriptServices : IZoneManagedNpcScriptServices, IZoneManagedNpcScriptLifecycle,
    IZoneManagedNpcAuthoredChildren, IZoneManagedNpcInitializationLifecycle, IZoneManagedNpcFrameLifecycle, IDisposable
{
    private readonly ZoneModelContent _models;
    private readonly Func<ZoneManagedNpcPhysics> _physics;
    private readonly Func<ZoneManagedNpcActor, ZoneManagedNpcColliderDynamics> _colliderDynamics;
    private readonly Func<ZoneUnit, Vector3> _position;
    private readonly Func<bool> _spectatorCollisions;
    private readonly ZoneManagedNpcGameScriptRequests? _requests;
    private readonly ZoneManagedNpcCombatScriptServices? _combat;
    private readonly ZoneManagedNpcActorConstruction? _construction;
    private readonly Func<ZoneManagedNpcAiPose>? _poses;
    private readonly ZoneManagedNpcSkillPatterns? _skills;
    private readonly ZoneManagedNpcGroups? _groups;
    private readonly Func<ZoneManagedNpcActorMotion>? _motion;
    private readonly Func<ZoneManagedNpcReplication>? _replication;
    private readonly Func<ZoneManagedNpcPositionChecks>? _positionChecks;
    private readonly ZoneManagedNpcPostureState? _postures;
    private readonly ZoneNpcSkillContent? _npcSkills;
    private int _frame;
    private readonly Dictionary<ZoneActorHandle, Components> _actors = [];
    public ZoneManagedNpcScriptEffects Effects { get; }
    public ZoneManagedNpcSmartObjectStates SmartObjects { get; }
    public ZoneManagedCharacterSlots Characters { get; }
    public bool IsDisposed { get; private set; }
    public int Count => _actors.Count;
    public ulong ConfirmedAbsentAttachmentLookups { get; private set; }
    public ulong AppliedAttachmentVisibilityUpdates { get; private set; }
    public ulong DeliveredColliderEvents { get; private set; }
    public bool HasAuthoredChildren => _construction is not null || _skills is not null || _groups is not null;

    public ZoneManagedNpcSceneScriptServices(IZoneLuaRuntime lua, ZoneModelContent models, IZoneAssetSource assets,
        Func<ZoneManagedNpcPhysics> physics, Func<ZoneManagedNpcActor, ZoneManagedNpcColliderDynamics> colliderDynamics,
        Func<ZoneUnit, Vector3> position, Func<long> gameMicroseconds, Func<bool> spectatorCollisions,
        ZoneCharacterAttachmentPublicationProfile attachments, ZoneManagedNpcGameScriptRequests? requests = null,
        Action<string>? trace = null, ZoneManagedNpcCombatScriptServices? combat = null,
        ZoneManagedNpcActorConstruction? construction = null, Func<ZoneManagedNpcAiPose>? poses = null,
        ZoneManagedNpcSkillPatterns? skills = null, ZoneManagedNpcGroups? groups = null,
        Func<ZoneManagedNpcActorMotion>? motion = null, Func<ZoneManagedNpcReplication>? replication = null,
        Func<ZoneManagedNpcPositionChecks>? positionChecks = null, ZoneManagedNpcPostureState? postures = null,
        ZoneNpcSkillContent? npcSkills = null)
    {
        ArgumentNullException.ThrowIfNull(lua);
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _physics = physics ?? throw new ArgumentNullException(nameof(physics));
        _colliderDynamics = colliderDynamics ?? throw new ArgumentNullException(nameof(colliderDynamics));
        _position = position ?? throw new ArgumentNullException(nameof(position));
        _spectatorCollisions = spectatorCollisions ?? throw new ArgumentNullException(nameof(spectatorCollisions));
        _requests = requests;
        _combat = combat;
        _construction = construction; _poses = poses; _skills = skills; _groups = groups;
        _motion = motion; _replication = replication; _positionChecks = positionChecks; _postures = postures; _npcSkills = npcSkills;
        if (new object?[] { motion, replication, positionChecks, postures, npcSkills }.Any(value => value is not null) &&
            new object?[] { construction, motion, replication, positionChecks, postures, npcSkills, groups }.Any(value => value is null))
            throw new ArgumentException("Managed NPC position, motion, replication and posture consumers must be composed with all actual owners.");
        if ((construction is null) != (poses is null))
            throw new ArgumentException("NPC construction and AI proxy pose owners must be supplied together.");
        Effects = new(lua, gameMicroseconds, trace, preserveNativeScriptErrors: motion is not null);
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
            var physics = _physics();
            binding.RequireCurrent();
            var position = _position(binding.Actor.Npc.Unit);
            binding.RequireCurrent();
            owner.Body = physics.Create(binding.Actor.Handle, binding.Actor.Npc, position,
                binding.Actor.Npc.Unit.Pose.Rotation, () => !IsDisposed && binding.IsCurrent);
            CopyPublishedModelTransform(owner);
            ZoneManagedNpcColliderDynamics parameters;
            var construction = _construction?.Find(binding.Actor.Handle);
            if (_construction is not null)
            {
                if (construction is null) throw new InvalidOperationException("The adopted profile has no exact CActor construction owner.");
                var installed = construction.Physicalized(owner.Body, _frame);
                parameters = new(installed.Inertia, installed.InertiaAcceleration);
            }
            else parameters = _colliderDynamics(binding.Actor);
            binding.RequireCurrent();
            owner.Animated = new(() => !IsDisposed && binding.IsCurrent && owner.Body.IsCurrent,
                owner.Body, parameters.Inertia, parameters.InertiaAcceleration, _spectatorCollisions, _frame);
            owner.Events = new(binding.Actor, owner.Animated);
            owner.SmartObject = SmartObjects.BindEntity(binding, new());
            owner.Effects = Effects.BindEntity(binding, construction?.Health ?? new());
            if (_combat is not null) owner.Combat = _combat.Bind(binding.Actor, new());
            if (construction is not null)
            {
                owner.Pose = _poses!().Bind(binding.Actor, construction);
                //The initial no-look controller refresh establishes the real view cache.
                owner.Pose.Refresh();
            }
            if (_skills is not null) owner.Skills = _skills.BindEntity(binding, owner.Body);
            if (_groups is not null) owner.Group = _groups.BindEntity(binding);
            if (_motion is not null)
            {
                owner.Motion = _motion().Bind(binding.Actor); binding.RequireCurrent();
                owner.Replication = _replication!().Bind(binding.Actor); binding.RequireCurrent();
                owner.Posture = _postures!.BindDedicatedConstruction(binding.Actor, construction!, _models, _npcSkills!); binding.RequireCurrent();
                owner.PositionCheck = _positionChecks!().BindEntity(binding); binding.RequireCurrent();
            }
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

    public IReadOnlyList<ZoneManagedNpcScriptFault> Tick(TimeSpan elapsed)
    { Check(); return Effects.Tick(elapsed).Concat(_skills?.DrainFaults() ?? []).ToArray(); }
    public void InstallAuthoredChildren(ZoneManagedNpcScriptBinding binding)
    {
        Require(binding.Actor);
        //3935D260 ->3925B660 initializes the entity (including Server.OnInit), then
        //ServerUnit virtual240/3935CA90 calls CActor504/392E0190 to associate Unit104.
        //Only the actual construction owner supplies that event; constructor health is100
        //until this point, and later reads follow live Unit points/stat6 rather than a mirror.
        if (_construction is not null)
        {
            var construction = _construction.Find(binding.Actor.Handle) ??
                throw new InvalidOperationException("NPC post-init health association requires its exact CActor construction owner.");
            construction.Health.AssociateServerUnit(binding.Actor.Npc.Unit);
            binding.RequireCurrent();
        }
        //The actual registration parameters and39346960 spawn group precede390B35F0
        //authored child installation and the character constructor.
        _groups?.ApplyInitialParameters(binding.Actor); binding.RequireCurrent();
        _groups?.InstallAuthoredGroup(binding.Actor); binding.RequireCurrent();
        _skills?.InstallAuthoredSkillLists(binding.Actor);
    }
    public void CompleteInitialization(ZoneManagedNpcScriptBinding binding)
    {
        Require(binding.Actor);
        binding.Actor.CompleteEntityInitialization();
        binding.RequireCurrent();
        _motion?.Invoke().CompleteNpcInitialization(binding.Actor);
    }
    public int GetGroupId(ZoneManagedNpcActor actor)
    { Require(actor); return Groups().GetGroupId(actor); }
    public ZoneManagedNpcActor? GetGroupLeader(int groupId)
    { Check(); return Groups().GetGroupLeader(groupId); }
    public void SetPositionCheckContext(ZoneManagedNpcActor actor, int context)
    {
        Require(actor);
        (_positionChecks?.Invoke() ?? throw new NotSupportedException("NpcSetCheckPosContext requires its actual correction and replication consumers.")).SetContext(actor, context);
    }
    public void SetDefaultPosture(ZoneManagedNpcActor actor, bool enabled)
    { Require(actor); (_postures ?? throw new NotSupportedException("Default posture requires its actual dedicated model driver and content inventory.")).SetDefaultPosture(actor, enabled); }
    public void SetSkillUsePattern(ZoneManagedNpcActor actor, int pattern)
    { Require(actor); Skills().SetPattern(actor, pattern); }
    public void SetSkillList(ZoneManagedNpcActor actor, ZoneLuaReference table)
    { Require(actor); Skills().SetSkillList(actor, table); }
    public AAEmu.Zone.Ai.IZoneGoalOperation CreateUseSkillGoal(ZoneManagedNpcActor actor, AAEmu.Zone.Ai.ZoneGoalDefinition definition)
    { Require(actor); return Skills().CreateUseSkillGoal(actor, definition.Parameters.Integer, definition.Parameters.Text ?? ""); }
    public IReadOnlyList<ZoneManagedNpcScriptFault> PrePhysics(ZoneMovementFrame frame)
    {
        Check(); _frame = unchecked((int)frame.Frame);
        List<ZoneManagedNpcScriptFault> faults = [];
        foreach (var owner in _actors.Values.ToArray())
        {
            if (owner.IsDisposed || !owner.Binding.IsCurrent) continue;
            try
            {
                //Entity event29 samples the global frame before the native collider arbiter.
                owner.Animated!.SetFrame(_frame); owner.Animated.Update();
            }
            catch (Exception error) when (error is NotSupportedException or ZoneLuaException or InvalidDataException)
            { faults.Add(new(owner.Binding.Actor.Handle, "entity pre-physics event29", error.Message)); }
        }
        return faults;
    }
    public IReadOnlyList<ZoneManagedNpcScriptFault> PublishPose(ZoneActorHandle actor)
    {
        Check();
        if (!_actors.TryGetValue(actor, out var owner) || owner.IsDisposed || !owner.Binding.IsCurrent) return [];
        try { CopyPublishedModelTransform(owner); owner.Pose?.Refresh(); return []; }
        catch (Exception error) when (error is NotSupportedException or ZoneLuaException or InvalidDataException)
        { return [new(actor, "AI proxy pose publication", error.Message)]; }
    }
    public IReadOnlyList<ZoneManagedNpcScriptFault> PublishPhysicalPose(ZoneActorHandle actor, ZoneManagedNpcBodySnapshot body, ZoneMovementFrame frame)
    {
        var faults = PublishPose(actor);
        if (faults.Count != 0 || _motion is null || !_actors.TryGetValue(actor, out var owner) || owner.IsDisposed || !owner.Binding.IsCurrent) return faults;
        try { _motion().Update(owner.Binding.Actor, body, frame); return []; }
        catch (Exception error) when (error is NotSupportedException or ZoneLuaException or InvalidDataException)
        { return [new(actor, "CActor physical-statistics publication", error.Message)]; }
    }
    public ZoneNpcSkillModelTransform CopyModelTransform(ZoneUnit unit)
    {
        Check(); ArgumentNullException.ThrowIfNull(unit);
        var owner = _actors.Values.FirstOrDefault(value => ReferenceEquals(value.Binding.Actor.Npc.Unit, unit)) ??
            throw new NotSupportedException("Skill aim geometry requires an admitted model-transform owner for this exact Unit.");
        Require(owner.Binding.Actor);
        if (unit.Pose != owner.PublishedPose || unit.Scale.Multiplier != 1 ||
            unit.Scale.BaseScale != owner.ModelTransform.UniformScale || unit.Snapshot.ModelId != owner.ModelTransform.ModelId ||
            unit.Snapshot.Attachment.Point != -1 || unit.Snapshot.Bonding.Point != -1)
            throw new NotSupportedException("Skill aim geometry requires the applied pose, scale and parent-transition model producer.");
        return owner.ModelTransform;
    }
    private static void CopyPublishedModelTransform(Components owner)
    {
        var body = owner.Body!.CopyState(); owner.Binding.RequireCurrent();
        owner.ModelTransform = new(body.ModelId, body.Status.Position, body.Status.Orientation, body.EntityScale);
        owner.PublishedPose = owner.Binding.Actor.Npc.Unit.Pose;
    }
    public long GetActorHealth(ZoneManagedNpcActor actor) { Require(actor); return Effects.GetActorHealth(actor); }
    public long GetActorMaximumHealth(ZoneManagedNpcActor actor) { Require(actor); return Effects.GetActorMaximumHealth(actor); }
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
    public void SetReferencePosition(ZoneManagedNpcActor actor, Vector3 position)
    { Require(actor); actor.Scene.Points.SetReferencePosition(actor, position); }
    public void SetReferenceDirection(ZoneManagedNpcActor actor, Vector3 direction)
    { Require(actor); actor.Scene.Points.SetReferenceDirection(actor, direction); }
    public bool IsSimilarReferenceDirection(ZoneManagedNpcActor actor)
    { Require(actor); return actor.Scene.Points.IsSimilarReferenceDirection(actor); }
    public Vector3 GetReferencePosition(ZoneManagedNpcActor actor)
    { Require(actor); return actor.Scene.Points.GetReferencePoint(actor).Position; }
    public Vector3 GetReferenceDirection(ZoneManagedNpcActor actor)
    { Require(actor); return actor.Scene.Points.GetReferencePoint(actor).MoveDirection; }
    public void SetReferenceRadius(ZoneManagedNpcActor actor, float radius)
    { Require(actor); actor.Scene.Points.SetReferenceRadius(actor, radius); }
    public bool RemoveBuff(ZoneManagedNpcActor actor, uint buffType)
    { Require(actor); return Requests().RemoveBuff(actor, buffType); }
    public void StopCasting(ZoneManagedNpcActor actor) { Require(actor); Requests().StopCasting(actor); }
    public void ClearCombat(ZoneManagedNpcActor actor)
    {
        Require(actor);
        (_combat ?? throw new NotSupportedException("NPC ClearCombat requires its actual AI-unit restoration and combat service owner.")).ClearCombat(actor);
    }
    public void SaveUnitAttr(ZoneManagedNpcActor actor, int changeOption)
    {
        Require(actor);
        (_combat ?? throw new NotSupportedException("NPC SaveUnitAttr requires its actual independent AI-unit restoration record.")).SaveUnitAttr(actor, changeOption);
    }
    public ZoneAnimatedColliderState CopyColliderState(ZoneManagedNpcActor actor) => Require(actor).Animated!.CopyState();
    public ZoneManagedNpcColliderEvent? CopyLastColliderEvent(ZoneManagedNpcActor actor) => Require(actor).Events!.LastDelivered;

    private ZoneManagedNpcGameScriptRequests Requests() => _requests ??
        throw new NotSupportedException("NPC World script requests require their actual request sender and copied casting-field owner.");
    private ZoneManagedNpcSkillPatterns Skills() => _skills ??
        throw new NotSupportedException("NPC skill selection requires the owned authored lists, validation, request and acknowledgment consumers.");
    private ZoneManagedNpcGroups Groups() => _groups ??
        throw new NotSupportedException("NPC group queries require their actual parameter, membership and group-map owners.");
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
        Release(() => _postures?.Dispose()); Release(() => _groups?.Dispose()); Release(() => _skills?.Dispose()); Release(() => _combat?.Dispose()); Release(Characters.Dispose); Release(SmartObjects.Dispose); Release(Effects.Dispose);
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
        public ZoneManagedNpcAiPoseLease? Pose;
        public ZoneManagedNpcSkillPatternLease? Skills;
        public ZoneManagedNpcGroupLease? Group;
        public ZoneManagedNpcActorMotionLease? Motion;
        public ZoneManagedNpcReplicationLease? Replication;
        public ZoneManagedNpcPositionCheckLease? PositionCheck;
        public ZoneManagedNpcPostureLease? Posture;
        public ZoneNpcSkillModelTransform ModelTransform;
        public ZoneUnitPose PublishedPose;
        public Action<ZoneUnit, bool>? CombatTransitioned;
        public bool IsDisposed { get; private set; }
        public void Dispose()
        {
            if (IsDisposed) return; IsDisposed = true;
            scene._requests?.Retire(Binding.Actor);
            if (CombatTransitioned is { } handler) Binding.Actor.Npc.Unit.CombatTransitioned -= handler;
            CombatTransitioned = null;
            if (scene._actors.TryGetValue(Binding.Actor.Handle, out var current) && ReferenceEquals(current, this)) scene._actors.Remove(Binding.Actor.Handle);
            List<Exception>? errors = null;
            Release(() => PositionCheck?.Dispose()); Release(() => Posture?.Dispose()); Release(() => Replication?.Dispose()); Release(() => Motion?.Dispose());
            Release(() => Group?.Dispose()); Release(() => Skills?.Dispose()); Release(() => Pose?.Dispose()); Release(() => Combat?.Dispose()); Release(() => Effects?.Dispose()); Release(() => SmartObject?.Dispose()); Release(() => Events?.Dispose());
            Release(() => Animated?.Dispose()); Release(() => Body?.Dispose()); Release(() => Character?.Dispose());
            if (errors is not null) throw new AggregateException("NPC generation components failed to retire.", errors);
            void Release(Action action) { try { action(); } catch (Exception error) { (errors ??= []).Add(error); } }
        }
    }
}
