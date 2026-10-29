using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Combat;
using AAEmu.Zone.Content;
using AAEmu.Zone.Hosting;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Protocol.Units;
using AAEmu.Zone.Scripting;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.Scene;

/// <summary>
/// The ordinary upright NPC path from CAIProxy30518020 through ActorMovementController
/// to the scene's one living body. Owns cached requests, never a physical Step or Unit pose.
/// </summary>
public sealed class ZoneManagedNpcMovementRequests : IDisposable
{
    private readonly ZoneManagedNpcActorScene _actors;
    private readonly ZoneManagedNpcPhysics _physics;
    private readonly ZoneManagedNpcActorConstruction _construction;
    private readonly ZoneManagedNpcActorMotion _motion;
    private readonly ZoneManagedNpcAiPose _poses;
    private readonly ZoneManagedNpcSceneScriptServices _components;
    private readonly ZoneStatCalculator _stats;
    private readonly ZoneEngineCVars _engineCVars;
    private readonly Dictionary<ZoneActorHandle, ZoneManagedNpcMovementRequestLease> _live = [];
    public bool IsDisposed { get; private set; }
    public int Count => _live.Count;
    public static string CapabilitySummary =>
        "Actual proxy request/stance/delta and ordinary upright dry NPC actor-to-living request; " +
        "copied buff turn flags and complete attribute10 modifiers; pitched body-facing, clamped head look, jumping, swimming, " +
        "animated root motion and moving/parented supports retain named refusals.";

    public ZoneManagedNpcMovementRequests(ZoneManagedNpcActorScene actors, ZoneManagedNpcPhysics physics,
        ZoneManagedNpcActorConstruction construction, ZoneManagedNpcActorMotion motion, ZoneManagedNpcAiPose poses,
        ZoneManagedNpcSceneScriptServices components, ZoneStatContent stats, ZoneEngineCVars? engineCVars = null)
    {
        _actors = actors ?? throw new ArgumentNullException(nameof(actors));
        _physics = physics ?? throw new ArgumentNullException(nameof(physics));
        _construction = construction ?? throw new ArgumentNullException(nameof(construction));
        _motion = motion ?? throw new ArgumentNullException(nameof(motion));
        _poses = poses ?? throw new ArgumentNullException(nameof(poses));
        _components = components ?? throw new ArgumentNullException(nameof(components));
        _stats = new(stats ?? throw new ArgumentNullException(nameof(stats)));
        _engineCVars = engineCVars ?? ZoneEngineCVars.Empty;
    }

    public ZoneManagedNpcMovementRequestLease Bind(ZoneManagedNpcActor actor)
    {
        Check(); _actors.Require(actor);
        if (_live.ContainsKey(actor.Handle)) throw new InvalidOperationException("This generation already owns its CActor movement requests.");
        var body = _physics.Find(actor.Handle) ?? throw new NotSupportedException("NPC movement requests require the actual living body.");
        var construction = _construction.Find(actor.Handle) ?? throw new NotSupportedException("NPC movement requests require actual CActor construction.");
        var pose = _poses.Find(actor.Handle) ?? throw new NotSupportedException("NPC movement requests require the actual actor/proxy pose owner.");
        if (!ReferenceEquals(body.Npc, actor.Npc) || !ReferenceEquals(construction.Npc, actor.Npc) ||
            !ReferenceEquals(pose.Actor, actor) || construction.PhysicalizedFrame is null || _motion.Find(actor.Handle) is null)
            throw new InvalidOperationException("NPC movement request dependencies belong to a different generation or incomplete physicalization.");
        if (actor.Npc.Unit.Snapshot.Identity.Kind != ZoneUnitKind.Npc)
            throw new NotSupportedException("This request producer owns original NPC Unit type1, not player/mate input profiles.");
        var owner = new ZoneManagedNpcMovementRequestLease(this, actor, body, construction, pose);
        _live.Add(actor.Handle, owner); return owner;
    }

    public ZoneManagedNpcMovementRequestLease? Find(ZoneActorHandle actor) =>
        !IsDisposed && _live.TryGetValue(actor, out var owner) && owner.IsCurrent ? owner : null;

    /// <summary>Call after the actual proxy activation gate and handler/signal phase, once per produced AI output.</summary>
    public bool SubmitProxyOutput(ZoneManagedNpcActor actor, ZoneManagedNpcProxyMovementOutput output)
    {
        var owner = Require(actor); ArgumentNullException.ThrowIfNull(output);
        if (!actor.EntityActive) return false;
        var model = _motion.CopyMovementEligibilityState(actor); Require(actor);
        var alertness = actor.Behavior?.Alertness ?? throw new NotSupportedException("NPC proxy requests require the actual CAIHandler.");
        //Dedicated ActorUnitModel constructor owns client-input pointer144=null. The
        //separate model flag1 is the actual AIUnitSystem.v224 gate, not AI enabled/health.
        if ((model.ModelFlags & 1) == 0)
        { _motion.CompleteOwnedProxyUpdate(actor, alertness); return false; }
        var entity = _components.CopyModelTransform(actor.Npc.Unit);
        var state = owner.Body.CopyState(); Require(actor);
        var normal = NormalSpeed(owner, state.CurrentStanceSlot);
        var canMove = (_actors.MovementEligibility ?? throw new NotSupportedException("NPC proxy movement requires its actual Unit/model eligibility owner.")).CanMove(actor);
        Require(actor);
        var request = ZoneNpcProxyMovementMath.Build(output, entity.Orientation, normal, canMove, alertness);
        //392EA280 copies the original request before CActor's next prephysical pass.
        owner.Body.RequestStanceSlot(request.Stance); Require(actor);
        owner.Request = request;
        if (request.Walking is { } walking) owner.Walking = walking;
        _motion.CommitOwnedMovementRequest(actor, request, owner.Walking);
        _motion.CompleteOwnedProxyUpdate(actor, alertness);
        owner.ProxyUpdateCount++;
        return true;
    }

    /// <summary>After the existing stance preparation and before the scene's existing physical Step.</summary>
    public void PrepareFrame(ZoneManagedNpcActor actor, ZoneMovementFrame frame)
    {
        var owner = Require(actor);
        if (owner.LastPreparedFrame == frame.Frame) throw new InvalidOperationException("NPC movement requests already consumed this frame.");
        var elapsed = (float)frame.Elapsed.TotalSeconds;
        if (!(elapsed > 0) || !float.IsFinite(elapsed)) throw new ArgumentOutOfRangeException(nameof(frame));
        if (!actor.EntityActive || actor.EntityHidden) return; //Original CActor prephysical hidden gate.
        if (actor.Npc.Unit.IsDead) throw new NotSupportedException("Dead CActor movement requires the actual death/ragdoll request owner.");
        var request = owner.Request;
        if (request is null) return; //No proxy request has reached this generation yet.
        RequireTurnSlowdownProfile();
        var body = owner.Body.CopyState();
        var entity = _components.CopyModelTransform(actor.Npc.Unit);
        var pose = owner.Pose.Last ?? throw new NotSupportedException("NPC movement requires a produced controller/view cache.");
        var motion = _motion.Find(actor.Handle)?.State ?? throw new ObjectDisposedException(nameof(ZoneManagedNpcActorMotionLease));
        Require(actor);
        //392D73D0/39258910 reads copied Unit flags, not whether any buff exists.
        //Root is deliberately absent from the turn gate;392DA390 owns its speed gate.
        var movementStatus = actor.Npc.Unit.MovementStatus.CopySnapshot();
        if (body.Status.LivingHasRetainedGroundEntity != false || actor.Npc.Unit.IsFlying ||
            motion.Mode == ZoneManagedNpcActorMotionMode.Swimming || motion.LandingTransition && motion.FallSpeed >= 5)
            throw new NotSupportedException("NPC movement requires its dry ordinary unparented/static-support CActor profile.");
        if (entity.Orientation.X != 0 || entity.Orientation.Y != 0 ||
            pose.ActorViewRotation.X != 0 || pose.ActorViewRotation.Y != 0)
            throw new NotSupportedException("NPC movement with pitched/tilted actor or entity rotation requires its complete derived rotation producer.");
        var movement = BuildOrdinary(owner, request, entity.Position, entity.Orientation, pose,
            motion.GroundedSeconds, motion.AirborneSeconds, movementStatus);
        Require(actor);
        //392DC900 is the final CActor dynamics producer after animated-character
        //restoration. Its virtual264 is the source walking latch, not strafing. Preserve
        //nonpositive authored inertia and the independently installed acceleration.
        var inertia = ZoneNpcOrdinaryMovementMath.EffectiveInertia(owner.Construction.GameParams.Inertia, owner.Walking);
        _physics.Backend.SetLivingInertia(actor.Handle, inertia); Require(actor);
        //All source and finite validation precedes queued physical/model mutations.
        owner.Body.RequestVelocity(movement.Velocity, ZonePhysicsLivingVelocityMode.Normal); Require(actor);
        _components.SetEntityOrientation(actor, movement.Orientation); Require(actor);
        owner.Pose.CopyActorMovementOutput(movement.Velocity);
        owner.Pose.CopyActorViewRotation(movement.Orientation);
        owner.Pose.CopyControllerLookTarget(movement.LookTarget);
        Require(actor);
        owner.LastPreparedFrame = frame.Frame;
        owner.LastVelocity = movement.Velocity;
        owner.PreparedFrameCount++;
    }

    private (Vector3 Velocity, Quaternion Orientation, Vector3 LookTarget) BuildOrdinary(
        ZoneManagedNpcMovementRequestLease owner, ZoneManagedNpcControllerRequest request,
        Vector3 position, Quaternion entityRotation, ZoneManagedNpcAiPoseSnapshot pose,
        float groundedSeconds, float airborneSeconds, ZoneUnitMovementStatusSnapshot status) => ZoneNpcOrdinaryMovementMath.Build(request,
            position, entityRotation, pose.Position, pose.ActorViewRotation, EffectiveNormalSpeed(owner, status),
            groundedSeconds, airborneSeconds, owner.Construction.ClassKind == ZoneManagedNpcActorClass.Player,
            owner.Actor.AiObject.Enabled, !status.Stunned && !status.Sleeping && !status.Psychokinesis && !status.ImpossibleRotate);

    private static float NormalSpeed(ZoneManagedNpcMovementRequestLease owner, int stance)
    {
        //392E86A0 owns actor1200=1;392DF300 does not replace that field. The accepted
        //stance row and actual124&0x1000 walking latch supply the remaining factors.
        var speed = owner.Construction.Stances.ForSlot(stance).NormalSpeed;
        return owner.Walking ? speed * owner.Construction.GameParams.WalkMultiplier : speed;
    }
    private void RequireTurnSlowdownProfile()
    {
        //392B1240's matching PE binds Console80 RegisterFloatRef: hidden XMM3 defaults
        //are .4 (procedural leaning) and1 (curving scale). CryAnimation31581750 owns
        //ca_GameControlledStrafing default1. Source defaults therefore take the identity path.
        var controlledStrafing = _engineCVars.GetInt("ca_GameControlledStrafing", 1);
        var leaning = _engineCVars.GetFloat("ac_enableProceduralLeaning", .4f);
        var scale = _engineCVars.GetFloat("pl_curvingSlowdownSpeedScale", 1);
        if (!float.IsFinite(leaning) || !float.IsFinite(scale))
            throw new NotSupportedException("NPC actor movement requires finite actual turn/lean CVars.");
        if (controlledStrafing != 0 && leaning == 0 && scale < 1)
            throw new NotSupportedException("Nonidentity NPC curving slowdown requires the source turn-rate scalar consumer.");
    }
    private float EffectiveNormalSpeed(ZoneManagedNpcMovementRequestLease owner, ZoneUnitMovementStatusSnapshot status)
    {
        var unit = owner.Actor.Npc.Unit;
        if (status.Stunned || status.Sleeping || status.Rooted) return 0;
        var values = _stats.CalculateAttributes(unit.Snapshot, [10], unit.Buffs, unit.Progression, unit.Equipment, unit.AttributeOverrides);
        if (!values.IsComplete) throw new NotSupportedException("NPC movement attribute10 has unresolved actual sources: " + string.Join(", ", values.UnresolvedSources));
        var adjustment = unchecked((int)values.Values[10]);
        var multiplier = adjustment == 0 ? 1 : MathF.Max(0, adjustment * .001f + 1);
        return NormalSpeed(owner, owner.Body.CurrentStanceSlot) * multiplier;
    }

    private ZoneManagedNpcMovementRequestLease Require(ZoneManagedNpcActor actor)
    {
        Check(); _actors.Require(actor);
        return Find(actor.Handle) is { } owner && ReferenceEquals(owner.Actor, actor) ? owner :
            throw new ObjectDisposedException(nameof(ZoneManagedNpcMovementRequestLease), "This exact NPC movement request generation retired.");
    }
    internal bool Owns(ZoneManagedNpcMovementRequestLease owner) => !IsDisposed && !owner.IsDisposed &&
        _live.TryGetValue(owner.Actor.Handle, out var current) && ReferenceEquals(current, owner) &&
        ReferenceEquals(_actors.Find(owner.Actor.Handle), owner.Actor) && ReferenceEquals(_physics.Find(owner.Actor.Handle), owner.Body) &&
        ReferenceEquals(_construction.Find(owner.Actor.Handle), owner.Construction) && ReferenceEquals(_poses.Find(owner.Actor.Handle), owner.Pose) &&
        !IsDisposed && !owner.IsDisposed && _live.TryGetValue(owner.Actor.Handle, out current) && ReferenceEquals(current, owner) &&
        ReferenceEquals(_actors.Find(owner.Actor.Handle), owner.Actor);
    internal void Retire(ZoneManagedNpcMovementRequestLease owner)
    { if (_live.TryGetValue(owner.Actor.Handle, out var current) && ReferenceEquals(current, owner)) _live.Remove(owner.Actor.Handle); }
    private void Check() => ObjectDisposedException.ThrowIf(IsDisposed, this);
    public void Dispose()
    { if (IsDisposed) return; IsDisposed = true; foreach (var owner in _live.Values.ToArray()) owner.Dispose(); }
}

public sealed class ZoneManagedNpcMovementRequestLease : IDisposable
{
    private readonly ZoneManagedNpcMovementRequests _owner;
    public ZoneManagedNpcActor Actor { get; }
    internal ZoneManagedNpcBodyLease Body { get; }
    internal ZoneManagedNpcActorConstructionLease Construction { get; }
    internal ZoneManagedNpcAiPoseLease Pose { get; }
    public ZoneManagedNpcControllerRequest? Request { get; internal set; }
    public bool Walking { get; internal set; }
    public Vector3 LastVelocity { get; internal set; }
    public uint? LastPreparedFrame { get; internal set; }
    public ulong ProxyUpdateCount { get; internal set; }
    public ulong PreparedFrameCount { get; internal set; }
    public bool IsDisposed { get; private set; }
    public bool IsCurrent => !IsDisposed && _owner.Owns(this);
    internal ZoneManagedNpcMovementRequestLease(ZoneManagedNpcMovementRequests owner, ZoneManagedNpcActor actor,
        ZoneManagedNpcBodyLease body, ZoneManagedNpcActorConstructionLease construction, ZoneManagedNpcAiPoseLease pose)
    { _owner = owner; Actor = actor; Body = body; Construction = construction; Pose = pose; }
    public void Dispose() { if (IsDisposed) return; IsDisposed = true; _owner.Retire(this); }
}
