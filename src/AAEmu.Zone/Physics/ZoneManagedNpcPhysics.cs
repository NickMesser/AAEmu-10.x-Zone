using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Content;
using AAEmu.Zone.Navigation;
using AAEmu.Zone.Npcs;
using AAEmu.Zone.Scene;

namespace AAEmu.Zone.Physics;

/// <summary>Resolved initial CActor request. This owns no body, entity, render bounds or AI identity.</summary>
public sealed class ZoneManagedNpcBodyAdmission
{
    private ZoneManagedNpcBodyAdmission(uint modelId, ZoneActorPhysicalizationRequest request,
        ZonePhysicsActorDescription description)
    { ModelId = modelId; Request = request; Description = description; Stances = new(request.ActorModel, request.EntityScale); }
    public uint ModelId { get; }
    public ZoneActorPhysicalizationRequest Request { get; }
    public ZoneActorStanceCatalog Stances { get; }
    public ZonePhysicsActorDescription Description { get; }

    /// <summary>
    /// NPC3935D260 passes the World scale in its model transform; CActor392DF300/392DF860/
    /// 392E2F20 install the same source-order stances, cubed masses and living dynamics as other
    /// actors. The local-player identity byte is false for this NPC admission. Extra geometry,
    /// non-ground locomotion and arbitrary entity physics flags are explicitly unported.
    /// </summary>
    public static ZoneManagedNpcBodyAdmission Build(ZoneModelContent models, uint modelId, float entityScale,
        Func<string, ushort?> resolvePhysicalSurface)
    {
        ArgumentNullException.ThrowIfNull(models); ArgumentNullException.ThrowIfNull(resolvePhysicalSurface);
        if (!float.IsFinite(entityScale) || entityScale <= 0) throw new ArgumentOutOfRangeException(nameof(entityScale));
        if (!models.Models.TryGetValue(modelId, out var model) || model.Kind != ZoneModelKind.Actor ||
            models.ActorForModel(modelId) is not { } actor)
            throw new NotSupportedException($"NPC model {modelId} has no resolved ActorModel living profile.");
        if (actor.Definition.AddBox) throw new NotSupportedException("NPC ActorModel.AddBox requires its additional physical geometry owner.");
        if (actor.Definition.FlyMode || actor.Definition.UnderwaterCreature)
            throw new NotSupportedException("Flying and underwater NPC motor profiles are not admitted by the ground-body owner.");
        if (actor.Definition.PhysicsFlags != 0)
            throw new NotSupportedException("NPC initial entity physics flag writes require an implemented flag profile.");
        var request = ZoneActorPhysicalizationRequest.BuildInitial(actor, entityScale, false, resolvePhysicalSurface);
        var description = ZonePhysicsActorDescription.FromLivingRequest(request);
        ZoneManagedTerrainGeometry.ValidateShape(description.Shape);
        if (!float.IsFinite(description.Mass) || description.Mass <= 0 || !float.IsFinite(description.Gravity) ||
            !float.IsFinite(description.CenterOffsetZ) || !float.IsFinite(description.GroundPivotHeight))
            throw new NotSupportedException("NPC authored living mass, gravity and dimensions must be finite and representable.");
        return new(modelId, request, description);
    }
}

/// <summary>Copied motor/request state. Accepted stance and applied physical dimensions are distinct.</summary>
public sealed record ZoneManagedNpcBodySnapshot(ZoneActorHandle Actor, uint ModelId, float EntityScale,
    int CurrentStanceSlot, int DesiredStanceSlot, ZoneActorPhysicalizationStance CurrentStance,
    ZonePhysicsActorDescription AppliedDescription, ZonePhysicsActorStatus Status,
    ZoneManagedLivingControlState Control, ZoneManagedLivingStanceUpdate? LastStanceUpdate, string? StanceRefusal);

/// <summary>
/// Body/motor leases over the scene's EXISTING backend. The global frame owns Step and Unit pose
/// publication. This owner only creates initial bodies, queues actor-local wishes/dimensions,
/// copies status and removes exact generations. It supplies no AI steering or second integrator.
/// </summary>
public sealed class ZoneManagedNpcPhysics : IDisposable
{
    private readonly ZoneModelContent _models;
    private readonly Func<string, ushort?> _resolvePhysicalSurface;
    private readonly Dictionary<ZoneActorHandle, ZoneManagedNpcBodyLease> _bodies = [];
    private readonly HashSet<uint> _preparing = [];
    private bool _preparingFrame;
    public ZoneManagedPhysicsBackend Backend { get; }
    public bool IsDisposed { get; private set; }
    public int Count => _bodies.Count;

    public ZoneManagedNpcPhysics(ZoneManagedPhysicsBackend backend, ZoneModelContent models,
        Func<string, ushort?> resolvePhysicalSurface)
    {
        Backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _resolvePhysicalSurface = resolvePhysicalSurface ?? throw new ArgumentNullException(nameof(resolvePhysicalSurface));
    }

    /// <summary>
    /// The scene supplies its exact registered NPC/catalog predicate and local initial transform.
    /// AddActor+Teleport here are initial scene admission, never an AI movement operation. A
    /// resolver/current-owner callback cannot publish a body after retiring that generation.
    /// Failed AddActor never removes a body belonging to another owner.
    /// </summary>
    public ZoneManagedNpcBodyLease Create(ZoneActorHandle actor, ZoneNpc npc, Vector3 localPosition,
        Quaternion orientation, Func<bool> exactOwnerCurrent)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(npc); ArgumentNullException.ThrowIfNull(exactOwnerCurrent);
        if (actor.UnitObjectId == 0 || actor.Generation == 0 || actor.UnitObjectId != npc.ObjectId)
            throw new ArgumentException("NPC body identity must match its nonzero exact Unit generation.", nameof(actor));
        ZoneManagedTerrainGeometry.ValidateFinite(localPosition, nameof(localPosition));
        if (!Finite(orientation) || !float.IsFinite(orientation.LengthSquared()) || orientation.LengthSquared() <= 0)
            throw new ArgumentException("NPC initial orientation must be finite and nonzero.", nameof(orientation));
        if (orientation.X != 0 || orientation.Y != 0)
            throw new NotSupportedException("Ground NPC physical admission requires an upright yaw orientation; tilted living transforms are unported.");
        if (npc.Unit.Snapshot.Attachment.Point != -1 || npc.Unit.Snapshot.Bonding.Point != -1)
            throw new NotSupportedException("Attached or bonded NPC physical admission requires its actual parent/seat owner.");
        if (npc.Unit.Scale.Multiplier != 1)
            throw new NotSupportedException("NPC live scale transitions require a separate physical scale-update owner.");
        if (_bodies.Keys.Any(handle => handle.UnitObjectId == actor.UnitObjectId) || !_preparing.Add(actor.UnitObjectId))
            throw new InvalidOperationException("This NPC numeric identity already has a body owner or admission in progress.");
        var added = false;
        try
        {
            RequireCurrent();
            //3935D260 passes the Unit-state model ID to3925B660. Npc.Model is separately
            //cached from the NPC template (e.g. its Big flag), and may be a different model.
            var admission = ZoneManagedNpcBodyAdmission.Build(_models, npc.Unit.Snapshot.ModelId,
                npc.Unit.Scale.BaseScale, _resolvePhysicalSurface);
            RequireCurrent();
            Backend.AddActor(actor, admission.Description); added = true;
            RequireCurrent();
            Backend.Teleport(actor, localPosition, orientation);
            var body = new ZoneManagedNpcBodyLease(this, actor, npc, admission, exactOwnerCurrent);
            _bodies.Add(actor, body);
            return body;
        }
        catch (Exception failure)
        {
            if (added)
                try { Backend.RemoveActor(actor); }
                catch (Exception cleanup) { throw new AggregateException(failure, cleanup); }
            throw;
        }
        finally { _preparing.Remove(actor.UnitObjectId); }
        void RequireCurrent()
        {
            if (IsDisposed || !exactOwnerCurrent() || IsDisposed)
                throw new InvalidOperationException("The exact NPC generation retired during body admission.");
        }
    }

    public ZoneManagedNpcBodyLease? Find(ZoneActorHandle actor) =>
        !IsDisposed && _bodies.TryGetValue(actor, out var body) && body.IsCurrent ? body : null;

    /// <summary>Once in the scene prephase, before its sole backend Step. Retried requests retain their desired slot.</summary>
    public void PrepareFrame()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (_preparingFrame) throw new InvalidOperationException("NPC body preparation cannot reenter its frame owner.");
        _preparingFrame = true;
        try { foreach (var body in _bodies.Values.ToArray()) if (body.IsCurrent) body.PrepareFrame(); }
        finally { _preparingFrame = false; }
    }

    /// <summary>After the scene Step, copy status for the scene's sole pose publisher. This writes no Unit or entity.</summary>
    public IReadOnlyList<ZoneManagedNpcBodySnapshot> CopyStatuses()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var copied = new List<ZoneManagedNpcBodySnapshot>();
        foreach (var body in _bodies.Values.ToArray()) if (body.IsCurrent) copied.Add(body.CopyState());
        return copied.AsReadOnly();
    }

    internal bool Owns(ZoneManagedNpcBodyLease body) => !IsDisposed &&
        _bodies.TryGetValue(body.Actor, out var current) && ReferenceEquals(body, current) &&
        Backend.OwnedPhysics.TryReadPosition(body.Actor, out _);
    internal void Retire(ZoneManagedNpcBodyLease body)
    {
        if (!_bodies.TryGetValue(body.Actor, out var current) || !ReferenceEquals(body, current)) return;
        _bodies.Remove(body.Actor);
        Backend.RemoveActor(body.Actor); // Body/queued commands retire before the enclosing actor/proxy.
    }
    public void Dispose()
    {
        if (IsDisposed) return; IsDisposed = true;
        List<Exception>? errors = null;
        foreach (var body in _bodies.Values.ToArray())
            try { body.Dispose(); } catch (Exception error) { (errors ??= []).Add(error); }
        if (errors is not null) throw new AggregateException("Managed NPC physical bodies failed to retire.", errors);
    }
    private static bool Finite(Quaternion q) => float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W);
}

/// <summary>One exact NPC generation's living body and movement-request capability.</summary>
public sealed class ZoneManagedNpcBodyLease : IZoneNavigationLocomotion, IZoneAnimatedColliderBodyEffects, IDisposable
{
    private readonly ZoneManagedNpcPhysics _owner;
    private readonly Func<bool> _exactOwnerCurrent;
    private bool _checking, _preparing;
    public ZoneActorHandle Actor { get; }
    public ZoneNpc Npc { get; }
    public ZoneManagedNpcBodyAdmission Admission { get; }
    public int CurrentStanceSlot { get; private set; } = ZoneActorStanceCatalog.InitialSlot;
    public int DesiredStanceSlot { get; private set; } = ZoneActorStanceCatalog.InitialSlot;
    public string? LastStanceRefusal { get; private set; }
    public bool IsDisposed { get; private set; }
    public int PhysicalEntityType => IsCurrent ? 4 : 0;
    public void Apply(ZoneAnimatedColliderPhysicsUpdate update)
    { Require(); _owner.Backend.ApplyAnimatedColliderUpdate(Actor, update); }
    public bool IsCurrent
    {
        get
        {
            if (IsDisposed) return false;
            if (!_owner.Owns(this)) { Dispose(); return false; }
            if (_checking) throw new InvalidOperationException("The NPC current-owner predicate cannot reenter its body lease.");
            _checking = true;
            try
            {
                if (_exactOwnerCurrent() && !IsDisposed && _owner.Owns(this)) return true;
                Dispose(); return false;
            }
            catch { Dispose(); throw; }
            finally { _checking = false; }
        }
    }
    internal ZoneManagedNpcBodyLease(ZoneManagedNpcPhysics owner, ZoneActorHandle actor, ZoneNpc npc,
        ZoneManagedNpcBodyAdmission admission, Func<bool> current)
    { _owner = owner; Actor = actor; Npc = npc; Admission = admission; _exactOwnerCurrent = current; }

    /// <summary>World-local meters/second, not an actual velocity or pose. Native modes0/3 remain distinct.</summary>
    public void RequestVelocity(Vector3 velocity, ZonePhysicsLivingVelocityMode mode = ZonePhysicsLivingVelocityMode.Normal)
    { Require(); _owner.Backend.ApplyLivingVelocityRequest(Actor, velocity, mode); }
    /// <summary>Queue the source-produced absolute upright orientation without a pose correction or motion reset.</summary>
    public void RequestOrientation(Quaternion absoluteOrientation)
    { Require(); _owner.Backend.SetLivingOrientation(Actor, absoluteOrientation); }
    /// <summary>Copied latest queued rotation, independent of the currently applied physical orientation.</summary>
    public bool TryReadPendingOrientation(out Quaternion orientation)
    { Require(); return _owner.Backend.TryReadPendingLivingOrientation(Actor, out orientation); }
    public bool TryRequestVelocity(Vector3 velocity)
    { if (!IsCurrent) return false; _owner.Backend.ApplyLivingVelocityRequest(Actor, velocity); return true; }
    public bool TryReadPosition(out Vector3 position)
    {
        position = default;
        if (!IsCurrent) return false;
        _owner.Backend.CopyStatus(Actor, out var status); position = status.Position; return true;
    }
    public void CopyStatus(out ZonePhysicsActorStatus status)
    { Require(); _owner.Backend.CopyStatus(Actor, out status); }
    public void CopyDescription(out ZonePhysicsActorDescription description)
    { Require(); _owner.Backend.CopyDescription(Actor, out description); }
    internal (int FlushedCommands, bool TranslationChanged) Relocate(Vector3 previousEntityPosition, Vector3 destination)
    { Require(); return _owner.Backend.RelocateLivingBody(Actor, previousEntityPosition, destination); }
    public ZoneManagedNpcBodySnapshot CopyState()
    {
        Require();
        _owner.Backend.CopyDescription(Actor, out var description); _owner.Backend.CopyStatus(Actor, out var status);
        _owner.Backend.CopyLivingControlState(Actor, out var control);
        ZoneManagedLivingStanceUpdate? stance = _owner.Backend.TryReadLivingStanceUpdate(Actor, out var report) ? report : null;
        return new(Actor, Admission.ModelId, Admission.Request.EntityScale, CurrentStanceSlot, DesiredStanceSlot,
            Admission.Stances.ForSlot(CurrentStanceSlot).Physicalization, description, status, control, stance, LastStanceRefusal);
    }
    public void RequestStanceSlot(int slot)
    {
        Require();
        if (slot == ZoneActorStanceCatalog.UnchangedRequest) return;
        if (DesiredStanceSlot != slot) LastStanceRefusal = null;
        DesiredStanceSlot = slot;
    }
    public void PrepareFrame()
    {
        Require();
        if (_preparing) throw new InvalidOperationException("NPC stance preparation cannot reenter its request writer.");
        if (DesiredStanceSlot == CurrentStanceSlot) return;
        _preparing = true;
        try
        {
            var requested = DesiredStanceSlot;
            ZoneActorStanceDefinition stance;
            try { stance = Admission.Stances.ForSlot(requested); }
            catch (NotSupportedException error) { LastStanceRefusal = error.Message; return; }
            if (!_owner.Backend.TryChangeLivingStance(Actor, stance.Physicalization, out var refusal))
            { LastStanceRefusal = refusal; return; }
            Require(); CurrentStanceSlot = requested; LastStanceRefusal = null;
        }
        finally { _preparing = false; }
    }
    private void Require()
    { if (!IsCurrent) throw new ObjectDisposedException(nameof(ZoneManagedNpcBodyLease), "The exact NPC physical generation retired."); }
    public void Dispose()
    { if (IsDisposed) return; IsDisposed = true; _owner.Retire(this); }
}
