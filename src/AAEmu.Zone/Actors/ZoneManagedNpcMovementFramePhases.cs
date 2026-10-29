using System.Numerics;
using AAEmu.Zone.Npcs;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Protocol.Units;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.Actors;

/// <summary>
/// Adds exact managed NPC body publication around the scene's existing movement phases. The
/// inner phases must own the SAME backend as npcPhysics; only inner.StepPhysics advances it.
/// This decorator owns no integrator, clock, AI steering, backend, actor or NPC lifetime.
/// </summary>
public sealed class ZoneManagedNpcMovementFramePhases : IZoneMovementFramePhases, IDisposable
{
    private readonly IZoneMovementFramePhases _inner;
    private readonly ZoneUnitRegistry _units;
    private readonly ZoneSceneNpcCatalog _catalog;
    private readonly ZoneManagedNpcActorScene _actors;
    private readonly ZoneManagedNpcPhysics _physics;
    private readonly ZoneMovementOutgoing _outgoing;
    private readonly ZoneMovementDiagnostics _diagnostics;
    private readonly ZoneActorMovementWorldOrigin _origin;
    private readonly Func<ZoneManagedNpcActor, ZoneManagedNpcBodySnapshot, ZoneMovementFrame, ZoneActorMovementState?>? _movementState;
    private readonly Action<ZoneMovementFrame>? _prePhysics;
    private readonly Action<ZoneMovementFrame>? _prepareRequests;
    private readonly Action<ZoneManagedNpcActor, ZoneManagedNpcBodySnapshot, ZoneMovementFrame>? _publishPose;
    private readonly bool _managerOwnsReplication;
    private readonly Dictionary<ZoneActorHandle, Staged> _staged = [];
    private readonly Dictionary<ZoneActorHandle, Staged> _tracked = [];
    private readonly Dictionary<ZoneActorHandle, string> _refusals = [];
    private ZoneMovementFrame _frame;
    private bool _open, _stepped, _busy;
    public bool IsDisposed { get; private set; }
    public bool IsFrameOpen => _open;
    public int LastPublishedCount { get; private set; }
    public int LastEncodedCount { get; private set; }

    private sealed record Staged(ZoneActorHandle Handle, ZoneNpc Npc, ZoneManagedNpcActor Actor, ZoneManagedNpcBodyLease Body);

    /// <param name="movementState">
    /// Optional actual copied CActor metadata owner, not a Puppet-control or client-input adapter.
    /// Native393405B0 calls ActorUnitModel39FC4168+224/39278AC0: low actor flags, delta and
    /// alertness have distinct producers. Null or a named refusal suppresses encoding while
    /// physical pose/spatial/entity publication proceeds. Accepted stance comes from the body
    /// lease; pose and velocity always come from the authoritative copied physical status.
    /// </param>
    public ZoneManagedNpcMovementFramePhases(IZoneMovementFramePhases inner, ZoneUnitRegistry registry,
        ZoneSceneNpcCatalog catalog, ZoneManagedNpcActorScene actorScene, ZoneManagedNpcPhysics npcPhysics,
        ZoneMovementOutgoing outgoing, ZoneMovementDiagnostics diagnostics,
        ZoneActorMovementWorldOrigin worldOrigin = default,
        Func<ZoneManagedNpcActor, ZoneManagedNpcBodySnapshot, ZoneMovementFrame, ZoneActorMovementState?>? movementState = null,
        Action<ZoneMovementFrame>? prePhysics = null,
        Action<ZoneManagedNpcActor, ZoneManagedNpcBodySnapshot, ZoneMovementFrame>? publishPose = null,
        bool managerOwnsReplication = false, Action<ZoneMovementFrame>? prepareRequests = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _units = registry ?? throw new ArgumentNullException(nameof(registry));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _actors = actorScene ?? throw new ArgumentNullException(nameof(actorScene));
        _physics = npcPhysics ?? throw new ArgumentNullException(nameof(npcPhysics));
        _outgoing = outgoing ?? throw new ArgumentNullException(nameof(outgoing));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _origin = worldOrigin; _movementState = movementState; _prePhysics = prePhysics; _publishPose = publishPose;
        _managerOwnsReplication = managerOwnsReplication;
        _prepareRequests = prepareRequests;
    }

    public int PrePhase(in ZoneMovementFrame frame)
    {
        RequireIdleCall();
        if (_open) throw new InvalidOperationException("NPC movement PrePhase requires the preceding frame to finish.");
        if (frame.Elapsed <= TimeSpan.Zero || !float.IsFinite(frame.GameSeconds))
            throw new ArgumentOutOfRangeException(nameof(frame), "NPC movement requires the scene's valid sampled frame.");
        _busy = true; _open = true; _stepped = false; _frame = frame;
        LastPublishedCount = 0; LastEncodedCount = 0; _staged.Clear();
        try
        {
            //Prepare NPC actor-local requests before opening the inner frame. This operation
            //samples no clock and calls no Step; a refusal cannot strand an inner open frame.
            _prePhysics?.Invoke(frame);
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            _physics.PrepareFrame();
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            _prepareRequests?.Invoke(frame);
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            PruneRetired();
            //No admitted NPC body means there is nothing to stage. In particular, a large
            //refused NPC burst must not add another full-registry scan to every player frame.
            if (_physics.Count != 0)
            {
                foreach (var unit in _units.Units.Values.ToArray())
                {
                    if (unit.Snapshot.Identity.Kind != ZoneUnitKind.Npc || _catalog.TryGet(unit.Snapshot.ObjectId) is not { } handle ||
                        _catalog.Find(handle) is not { } npc || !ReferenceEquals(npc.Unit, unit) ||
                        _actors.Find(handle) is not { } actor || !ReferenceEquals(actor.Npc, npc) ||
                        _physics.Find(handle) is not { } body)
                        continue;
                    var staged = new Staged(handle, npc, actor, body);
                    if (!IsCurrent(staged)) continue;
                    _staged.Add(handle, staged); _tracked[handle] = staged;
                }
            }
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            return _inner.PrePhase(frame) + _staged.Count;
        }
        catch { _open = false; _staged.Clear(); throw; }
        finally { _busy = false; }
    }

    public void StepPhysics(TimeSpan elapsed)
    {
        RequireIdleCall();
        if (!_open || _stepped)
            throw new InvalidOperationException("NPC movement delegates exactly one physics Step inside its open frame.");
        if (elapsed <= TimeSpan.Zero || elapsed != _frame.Elapsed)
            throw new ArgumentOutOfRangeException(nameof(elapsed), "NPC physics uses the same elapsed period sampled for this frame.");
        _busy = true; _stepped = true;
        try { _inner.StepPhysics(elapsed); }
        catch { _open = false; _staged.Clear(); throw; }
        finally { _busy = false; }
    }

    public int PostPhase(in ZoneMovementFrame frame)
    {
        RequireIdleCall();
        if (!_open || !_stepped || frame != _frame)
            throw new InvalidOperationException("NPC movement PostPhase requires its exact pre-sampled, stepped frame.");
        _busy = true;
        try
        {
            var innerCount = _inner.PostPhase(frame);
            //Bodies added after PrePhase are deliberately deferred to the following frame.
            foreach (var snapshot in _physics.CopyStatuses())
            {
                if (!_staged.TryGetValue(snapshot.Actor, out var staged) || !IsCurrent(staged)) continue;
                var status = snapshot.Status;
                if (!Finite(status.Position) || !Finite(status.Velocity) || !Finite(status.Orientation))
                {
                    Refuse(staged, "NPC body status is not a finite publishable transform and velocity.");
                    continue;
                }
                var unit = staged.Npc.Unit;
                unit.CommitPose(new(ZoneMovementPoseMath.ToWorldPosition(status.Position, _origin), status.Orientation));
                if (!IsCurrent(staged)) { Discard(staged); continue; }
                _units.UpdateSpatialMembership(unit); //Never creates spatial membership implicitly.
                if (!IsCurrent(staged)) { Discard(staged); continue; }
                if (_publishPose is null)
                    staged.Actor.AiObject.Position = ZoneMovementPoseMath.ToLocalMeters(unit.Pose.Position, _origin);
                else _publishPose(staged.Actor, snapshot, frame);
                if (!IsCurrent(staged)) { Discard(staged); continue; }
                LastPublishedCount++;
                if (_managerOwnsReplication) continue;
                if (_movementState is null)
                {
                    Refuse(staged, "NPC outgoing movement requires its actual copied CActor flags/delta/alertness owner; physical pose remains published.");
                    continue;
                }
                ZoneActorMovementState? state;
                try { state = _movementState(staged.Actor, snapshot, frame); }
                catch (NotSupportedException error) { Refuse(staged, error.Message); continue; }
                catch (ObjectDisposedException) when (!IsCurrent(staged)) { Discard(staged); continue; }
                if (!IsCurrent(staged)) { Discard(staged); continue; }
                if (state is null)
                {
                    Refuse(staged, "NPC outgoing metadata owner did not supply a copied CActor state.");
                    continue;
                }
                if (!CanEncodePlainState(state))
                {
                    Refuse(staged, "NPC outgoing metadata requires unported optional ground/hanging/push/skill/fall fields or an invalid delta.");
                    continue;
                }
                if (snapshot.CurrentStanceSlot is < 0 or > 8)
                {
                    Refuse(staged, "NPC outgoing stance has no supported accepted actor slot.");
                    continue;
                }
                var wirePosition = _outgoing.LocalOnWire ? status.Position : ZoneMovementPoseMath.ToMeters(unit.Pose.Position);
                _outgoing.Sample(unit, status with { Position = wirePosition },
                    state with { Stance = (sbyte)snapshot.CurrentStanceSlot }, frame);
                _refusals.Remove(staged.Handle);
            }
            //A later metadata callback may retire an earlier sampled actor without removing
            //its Unit. The generic encoder checks Unit currency, so also discard lost NPC
            //catalog/actor/body generations here before it queues any of this batch.
            PruneRetired();
            LastEncodedCount = _outgoing.Build(frame);
            return innerCount + LastEncodedCount;
        }
        finally { _busy = false; _open = false; _stepped = false; _staged.Clear(); }
    }

    private bool IsCurrent(Staged staged)
    {
        if (IsDisposed || !ReferenceEquals(_catalog.Find(staged.Handle), staged.Npc) ||
            !ReferenceEquals(_actors.Find(staged.Handle), staged.Actor) || !_units.IsMovementCurrent(staged.Npc.Unit)) return false;
        //Find checks the body lease's external exact-owner predicate. Recheck every other
        //owner after that callback before allowing a state write or an outgoing sample.
        return ReferenceEquals(_physics.Find(staged.Handle), staged.Body) && !IsDisposed &&
            ReferenceEquals(_catalog.Find(staged.Handle), staged.Npc) &&
            ReferenceEquals(_actors.Find(staged.Handle), staged.Actor) && _units.IsMovementCurrent(staged.Npc.Unit) &&
            staged.Body.Admission.ModelId == staged.Npc.Unit.Snapshot.ModelId;
    }

    private void PruneRetired()
    {
        foreach (var staged in _tracked.Values.ToArray())
            if (!IsCurrent(staged)) Discard(staged);
    }
    private void Discard(Staged staged)
    { _outgoing.Discard(staged.Npc.Unit); _tracked.Remove(staged.Handle); _refusals.Remove(staged.Handle); }
    private void Refuse(Staged staged, string reason)
    {
        _outgoing.Discard(staged.Npc.Unit);
        if (_refusals.TryGetValue(staged.Handle, out var previous) && previous == reason) return;
        _refusals[staged.Handle] = reason;
        _diagnostics.ReportPhaseRefusal(staged.Handle.UnitObjectId, reason);
    }
    private static bool CanEncodePlainState(ZoneActorMovementState state) => Finite(state.Delta) &&
        (state.ActorFlags & ~0x1f) == 0 && state.GroundUnitId == 0 && state.GroundGimmickId == 0 &&
        state.GroundDoodadId == 0 && state.GroundPart == 0 && state.GroundPartId == 0 &&
        state.HangingObjectId == 0 && state.Push is null && state.Climb == Vector3.Zero &&
        state.PushTag == 0 && state.SkillControllerType == 0 && state.Phase == 0 && state.FallVelocity == 0;
    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    private static bool Finite(Quaternion q) => float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) &&
        float.IsFinite(q.W) && float.IsFinite(q.LengthSquared()) && q.LengthSquared() > 0;
    private void RequireIdleCall()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (_busy) throw new InvalidOperationException("NPC movement frame phases cannot reenter an active phase.");
    }
    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true; _open = false;
        foreach (var staged in _tracked.Values) _outgoing.Discard(staged.Npc.Unit);
        _staged.Clear(); _tracked.Clear(); _refusals.Clear();
    }
}
