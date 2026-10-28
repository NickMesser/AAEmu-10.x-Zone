using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Physics;

namespace AAEmu.Zone.Scene;

/// <summary>Copied proxy pose. The entity origin, AI view position and three directions are distinct.</summary>
public sealed record ZoneManagedNpcAiPoseSnapshot(Vector3 EntityPosition, Vector3 Position,
    Vector3 BodyDirection, Vector3 MoveDirection, Vector3 ViewDirection,
    Vector3 ActorMovementOutput, Vector3 ControllerLookTarget, Quaternion ActorViewRotation);

/// <summary>
/// Actual ActorMovementController392E99D0 -> CAIProxy305159C0 -> CAIObject3103DA78 pose
/// transfer for admitted, upright, unparented NPC bodies. Owns copied proxy state only: it
/// never writes Unit/body transforms, moves an actor, or interprets desired Puppet controls.
/// </summary>
public sealed class ZoneManagedNpcAiPose : IDisposable
{
    private readonly ZoneManagedNpcActorScene _actors;
    private readonly ZoneManagedNpcPhysics _bodies;
    private readonly Dictionary<ZoneActorHandle, ZoneManagedNpcAiPoseLease> _live = [];
    public bool IsDisposed { get; private set; }
    public int Count => _live.Count;
    public static string CapabilitySummary =>
        "Live world/local facing, accepted stance view position and exact copied actor/controller direction outputs; " +
        "Puppet look-request generation, actor turning, parent transforms and animated skeleton pose remain separate owners.";

    public ZoneManagedNpcAiPose(ZoneManagedNpcActorScene actors, ZoneManagedNpcPhysics bodies)
    { _actors = actors ?? throw new ArgumentNullException(nameof(actors)); _bodies = bodies ?? throw new ArgumentNullException(nameof(bodies)); }

    public ZoneManagedNpcAiPoseLease Bind(ZoneManagedNpcActor actor, ZoneManagedNpcActorConstructionLease construction)
    {
        Check(); ArgumentNullException.ThrowIfNull(actor); ArgumentNullException.ThrowIfNull(construction);
        if (!ReferenceEquals(_actors.Find(actor.Handle), actor) || !construction.IsCurrent ||
            construction.Actor != actor.Handle || !ReferenceEquals(construction.Npc, actor.Npc))
            throw new InvalidOperationException("AI proxy pose requires the exact adopted actor and construction owners.");
        var body = _bodies.Find(actor.Handle) ?? throw new InvalidOperationException("AI proxy pose requires an admitted physical body.");
        if (!ReferenceEquals(body.Npc, actor.Npc) || body.Admission.ModelId != construction.ModelId ||
            body.Admission.Request.EntityScale != construction.EntityScale)
            throw new InvalidOperationException("AI proxy pose body does not match its actor construction.");
        _ = construction.CopyAnimatedParameters();
        if (_live.ContainsKey(actor.Handle)) throw new InvalidOperationException("This actor generation already owns an AI proxy pose.");
        //ActorUnitModel39277D00 calls each derived virtual368 with the initial model rotation.
        //392FCC00/392ECAC0/392EEF40 copy it to the independent CActor view quaternion848.
        var rotation = actor.Npc.Unit.Pose.Rotation;
        ValidateRotation(rotation);
        var result = new ZoneManagedNpcAiPoseLease(this, actor, construction, body, rotation);
        if (!Current(result)) throw new ObjectDisposedException(nameof(ZoneManagedNpcActor), "The actor retired while binding its AI proxy pose.");
        _live.Add(actor.Handle, result);
        return result;
    }

    public ZoneManagedNpcAiPoseLease? Find(ZoneActorHandle actor) =>
        !IsDisposed && _live.TryGetValue(actor, out var current) && current.IsCurrent ? current : null;
    public ZoneManagedNpcAiPoseSnapshot Refresh(ZoneActorHandle actor) =>
        (Find(actor) ?? throw new ObjectDisposedException(nameof(ZoneManagedNpcAiPoseLease), "The exact AI proxy pose generation retired.")).Refresh();

    private bool Current(ZoneManagedNpcAiPoseLease owner)
    {
        if(IsDisposed || !owner.Construction.IsCurrent || !ReferenceEquals(_actors.Find(owner.Actor.Handle),owner.Actor))return false;
        //Body.Find invokes its external current-owner callback; validate the other owners again.
        return ReferenceEquals(_bodies.Find(owner.Actor.Handle),owner.Body) && !IsDisposed &&
            owner.Construction.IsCurrent && ReferenceEquals(_actors.Find(owner.Actor.Handle),owner.Actor);
    }
    internal bool Owns(ZoneManagedNpcAiPoseLease owner) =>
        _live.TryGetValue(owner.Actor.Handle, out var current) && ReferenceEquals(current, owner) && Current(owner);
    internal void Retire(ZoneManagedNpcAiPoseLease owner)
    { if (_live.TryGetValue(owner.Actor.Handle, out var current) && ReferenceEquals(current, owner)) _live.Remove(owner.Actor.Handle); }
    private void Check() => ObjectDisposedException.ThrowIf(IsDisposed, this);
    internal static void ValidateFinite(Vector3 vector)
    {
        if (!float.IsFinite(vector.X) || !float.IsFinite(vector.Y) || !float.IsFinite(vector.Z) || !float.IsFinite(vector.LengthSquared()))
            throw new ArgumentException("AI proxy pose vectors must have finite components and squared length.");
    }
    internal static void ValidateRotation(Quaternion rotation)
    {
        var squared = rotation.LengthSquared();
        if (!float.IsFinite(rotation.X) || !float.IsFinite(rotation.Y) || !float.IsFinite(rotation.Z) ||
            !float.IsFinite(rotation.W) || !float.IsFinite(squared) || MathF.Abs(squared - 1) > .0001f)
            throw new NotSupportedException("AI proxy pose requires an actual normalized entity/view quaternion.");
    }
    public void Dispose()
    { if (IsDisposed) return; IsDisposed = true; foreach (var owner in _live.Values.ToArray()) owner.Dispose(); }
}

public sealed class ZoneManagedNpcAiPoseLease : IDisposable
{
    private readonly ZoneManagedNpcAiPose _owner;
    private Vector3 _previousViewPosition, _actorMovementOutput, _controllerLookTarget;
    private Quaternion _actorViewRotation;
    private bool _hasMovementOutput, _hasViewPosition;
    internal ZoneManagedNpcBodyLease Body { get; }
    public ZoneManagedNpcActor Actor { get; }
    public ZoneManagedNpcActorConstructionLease Construction { get; }
    public bool IsDisposed { get; private set; }
    public bool IsCurrent => !IsDisposed && _owner.Owns(this);
    public ZoneManagedNpcAiPoseSnapshot? Last { get; private set; }
    public ulong SampleCount { get; private set; }

    internal ZoneManagedNpcAiPoseLease(ZoneManagedNpcAiPose owner, ZoneManagedNpcActor actor,
        ZoneManagedNpcActorConstructionLease construction, ZoneManagedNpcBodyLease body, Quaternion viewRotation)
    { _owner=owner; Actor=actor; Construction=construction; Body=body; _actorViewRotation=viewRotation; }

    /// <summary>
    /// Copy CActor876 from the actual actor movement-output producer (39302A20/392F0E60),
    /// not Controls.MovementDirection, status velocity or an arbitrary desired AI vector.
    /// The producer remains responsible for updating this field at its original frame boundary.
    /// </summary>
    public void CopyActorMovementOutput(Vector3 output)
    { Require(); ZoneManagedNpcAiPose.ValidateFinite(output); _actorMovementOutput=output; _hasMovementOutput=true; }

    /// <summary>Copy actual ActorMovementController376 after request generation; no look/turn request is synthesized.</summary>
    public void CopyControllerLookTarget(Vector3 target)
    { Require(); ZoneManagedNpcAiPose.ValidateFinite(target); _controllerLookTarget=target; }

    /// <summary>
    /// Copy the actual cached movement-controller view position228 after a known native-equivalent
    /// reset/update. Raw constructor zero is not evidence of the post-reset cache. A no-look
    /// Refresh also establishes this cache without reading its previous value.
    /// </summary>
    public void CopyControllerViewPosition(Vector3 position)
    { Require(); ZoneManagedNpcAiPose.ValidateFinite(position); _previousViewPosition=position; _hasViewPosition=true; }

    /// <summary>Copy the independent CActor848 producer. Physical body rotation does not implicitly replace it.</summary>
    public void CopyActorViewRotation(Quaternion rotation)
    { Require(); ZoneManagedNpcAiPose.ValidateRotation(rotation); _actorViewRotation=rotation; }

    public ZoneManagedNpcAiPoseSnapshot Refresh()
    {
        Require();
        var state=Body.CopyState();
        Require();
        if (state.Actor!=Actor.Handle || state.ModelId!=Construction.ModelId || state.EntityScale!=Construction.EntityScale)
            throw new InvalidOperationException("AI proxy pose sample belongs to a different construction/body generation.");
        if (Actor.Npc.Unit.Snapshot.Attachment.Point!=-1 || Actor.Npc.Unit.Snapshot.Bonding.Point!=-1 ||
            state.Status.Orientation.X!=0 || state.Status.Orientation.Y!=0)
            throw new NotSupportedException("Parented, seated or tilted NPC AI proxy pose needs its original transform owner.");
        if (!_hasMovementOutput && state.Control.RequestedVelocity!=Vector3.Zero)
            throw new NotSupportedException("A moving NPC AI proxy requires the actual CActor movement-output producer; physical wishes are not that copied field.");
        ZoneManagedNpcAiPose.ValidateFinite(state.Status.Position);
        ZoneManagedNpcAiPose.ValidateRotation(state.Status.Orientation);
        var worldForward=Forward(state.Status.Orientation);
        //The admitted profile has no parent. Native Entity.local and Entity.world quaternion
        //getters are consequently equal, without introducing scale-bearing Matrix columns.
        var moveSquared=_actorMovementOutput.LengthSquared();
        var move=moveSquared>.01f ? NormalizeWithMinimum(_actorMovementOutput) : worldForward;
        var view=worldForward;
        if (_controllerLookTarget!=Vector3.Zero)
        {
            if(!_hasViewPosition)
                throw new NotSupportedException("A copied controller look target requires its actual cached view-position producer before directional refresh.");
            var difference=_controllerLookTarget-_previousViewPosition;
            ZoneManagedNpcAiPose.ValidateFinite(difference);
            view=difference.LengthSquared()<=1.1920928955078125e-7f ? worldForward : Normalize(difference);
        }
        var offset=state.CurrentStance.ViewOffset;
        ZoneManagedNpcAiPose.ValidateFinite(offset);
        var position=state.Status.Position+Vector3.Transform(offset,_actorViewRotation);
        ZoneManagedNpcAiPose.ValidateFinite(position);
        var result=new ZoneManagedNpcAiPoseSnapshot(state.Status.Position,position,Normalize(worldForward),
            Normalize(move),Normalize(view),_actorMovementOutput,_controllerLookTarget,_actorViewRotation);
        Require();
        //CAIProxy305159C0 BodyInfo+12 is the view position.3103DA78's nearby same-entity
        //branch copies it to AIObject.Position, then normalizes body/move/view independently.
        Actor.AiObject.Position=result.Position;
        Actor.AiObject.BodyDirection=result.BodyDirection;
        Actor.AiObject.MoveDirection=result.MoveDirection;
        Actor.AiObject.ViewDirection=result.ViewDirection;
        _previousViewPosition=position; _hasViewPosition=true; Last=result;
        if(SampleCount!=ulong.MaxValue) SampleCount++;
        return result;
    }

    private static Vector3 Forward(Quaternion q) => new(
        (q.Y*q.X-q.Z*q.W)*2, (q.Y*q.Y+q.W*q.W)*2-1, (q.W*q.X+q.Z*q.Y)*2);
    private static Vector3 Normalize(Vector3 v)
    {
        var squared=(v.X*v.X+v.Y*v.Y)+v.Z*v.Z;
        return squared<=1.1920928955078125e-7f ? Vector3.Zero : v*(1/MathF.Sqrt(squared));
    }
    private static Vector3 NormalizeWithMinimum(Vector3 v)
    {
        var squared=(v.X*v.X+v.Y*v.Y)+v.Z*v.Z;
        return v*(1/MathF.Sqrt(squared+1.1754943508222875e-38f));
    }
    private void Require()
    { if(!IsCurrent) throw new ObjectDisposedException(nameof(ZoneManagedNpcAiPoseLease),"The exact AI proxy pose generation retired."); }
    public void Dispose()
    { if(IsDisposed)return; IsDisposed=true; _owner.Retire(this); }
}
