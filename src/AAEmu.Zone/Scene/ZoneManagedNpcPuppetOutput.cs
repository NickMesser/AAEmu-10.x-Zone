using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Ai;
using AAEmu.Zone.Combat;
using AAEmu.Zone.Hosting;
using AAEmu.Zone.Protocol.Units;
using AAEmu.Zone.Scripting;

namespace AAEmu.Zone.Scene;

/// <summary>The live SAIActorState fields read by ServerAIUnitSystem39213180 during goals.</summary>
public readonly record struct ZoneManagedNpcPuppetMovementState(Vector3 MoveDirection, float DesiredSpeed);

/// <summary>
///31172DEC's ordinary Puppet output phases, including3116CCBC look/body targets. Borrows
///the actual actor, proxy pose, installed ability, stats and navigation owners; never moves a
///body or drains a signal. Full perception, smart-object and special actor-target phases remain separate.
/// </summary>
public sealed class ZoneManagedNpcPuppetOutput : IDisposable
{
    private readonly ZoneManagedNpcActorScene _actors;
    private readonly ZoneManagedNpcAiPose _poses;
    private readonly ZoneManagedNpcMovementAbility _ability;
    private readonly ZoneManagedNpcActorMotion _motion;
    private readonly ZoneStatCalculator _stats;
    private readonly Func<ZoneManagedNpcActor, ZoneManagedNpcNavigationLease?>? _navigation;
    private readonly ZoneEngineCVars _cvars;
    private readonly Dictionary<ZoneActorHandle, ZoneManagedNpcPuppetOutputLease> _live = [];
    public bool IsDisposed { get; private set; }
    public int Count => _live.Count;
    public bool ProxyUpdatesEnabled => _cvars.GetInt("ai_UpdateProxy", 1) != 0;

    public ZoneManagedNpcPuppetOutput(ZoneManagedNpcActorScene actors, ZoneManagedNpcAiPose poses,
        ZoneManagedNpcMovementAbility ability, ZoneManagedNpcActorMotion motion, ZoneStatCalculator stats,
        Func<ZoneManagedNpcActor, ZoneManagedNpcNavigationLease?>? navigation = null, ZoneEngineCVars? engineCVars = null)
    {
        _actors = actors ?? throw new ArgumentNullException(nameof(actors)); _poses = poses ?? throw new ArgumentNullException(nameof(poses));
        _ability = ability ?? throw new ArgumentNullException(nameof(ability)); _motion = motion ?? throw new ArgumentNullException(nameof(motion));
        _stats = stats ?? throw new ArgumentNullException(nameof(stats)); _navigation = navigation; _cvars = engineCVars ?? ZoneEngineCVars.Empty;
    }

    public ZoneManagedNpcPuppetOutputLease Bind(ZoneManagedNpcActor actor)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this); _actors.Require(actor);
        if (!actor.IsPuppet || actor.Entry.Registration?.ObjectType != 5 || actor.Npc.Unit.Snapshot.Identity.Kind != ZoneUnitKind.Npc)
            throw new NotSupportedException("Puppet output requires an actual registered ordinary type5 NPC Puppet.");
        if (_poses.Find(actor.Handle) is not { } pose || _ability.Find(actor.Handle) is not { } ability ||
            ability.State.Look is null || _motion.Find(actor.Handle) is null)
            throw new NotSupportedException("Puppet output requires its actual proxy pose, model-backed registered look ability and CActor motion owners.");
        if (_live.ContainsKey(actor.Handle)) throw new InvalidOperationException("This exact actor already owns its Puppet output history.");
        var owner = new ZoneManagedNpcPuppetOutputLease(this, actor, pose, ability);
        _live.Add(actor.Handle, owner); return owner;
    }

    /// <summary>After proxy/body sampling and before Entry.Phase.Update. DesiredSpeed is retained.</summary>
    public void BeforeGoals(ZoneManagedNpcActor actor)
    {
        var owner = Require(actor);
        if (owner.GoalsPrepared) throw new InvalidOperationException("Puppet goals were already prepared without completing their output phase.");
        var now = _actors.FrameMicroseconds(); Require(actor);
        //31173855 is CVTSI2SS rdx (full signed64), despite the decompiler's int cast.
        //The actor interval is distinct from global AI frame delta used by look/body turn.
        var timePassed = CalculateTimePassed(owner.LastUpdateMicroseconds, now);
        owner.TimePassed = timePassed; owner.LastUpdateMicroseconds = now;
        owner.OutputReady = false; owner.GoalsPrepared = true;
        actor.Controls.MovementDirection = Vector3.Zero; //31172DEC actor280..288, not actor1200.
    }

    /// <summary>After goal/continuation output and before the ai_UpdateProxy/activation gate or signal drain.</summary>
    public ZoneManagedNpcProxyMovementOutput BuildBeforeProxy(ZoneManagedNpcActor actor)
    {
        var owner = Require(actor);
        if (!owner.GoalsPrepared) throw new InvalidOperationException("Puppet output requires this scheduled update's before-goal phase.");
        var parameters = owner.Ability.State;
        var lookAbility = parameters.Look!;
        var pose = owner.Pose.Last ?? throw new NotSupportedException("Puppet output requires an actually sampled proxy pose.");
        var navigation = _navigation?.Invoke(actor); Require(actor);
        if (navigation is not null && (!navigation.IsCurrent || !ReferenceEquals(navigation.Actor, actor)))
            throw new ObjectDisposedException(nameof(ZoneManagedNpcNavigationLease), "Puppet output received another or retired navigation owner.");
        if (!Finite(pose.Position) || !Finite(pose.EntityPosition) || !Finite(pose.BodyDirection) ||
            !Finite(actor.Controls.MovementDirection) || !float.IsFinite(actor.Controls.DesiredSpeed) ||
            !float.IsFinite(actor.Controls.MovementUrgency) || !float.IsFinite(actor.Controls.GoalMovementUrgency) ||
            !float.IsFinite(actor.Controls.DistanceToPathEnd) || !float.IsFinite(lookAbility.IdleTurnSpeed) || !float.IsFinite(lookAbility.CombatTurnSpeed))
            throw new NotSupportedException("Puppet output requires finite actual movement, look and pose inputs.");
        //39213110(...,true)->39260030: NPC kind1 returnsfalse without inspecting equipment.
        //3100675C therefore repairs urgency from the independent goal field.
        var urgency = actor.Controls.MovementUrgency;
        if (actor.Controls.GoalMovementUrgency > urgency + .0001f) urgency = actor.Controls.GoalMovementUrgency;
        var canTurn = CanTurn(actor); Require(actor);
        var staged = StageControls(actor.Controls);
        var oldStrafe = staged.AllowStrafing;
        ZoneActorMovementControl.UpdateStrafing(staged, new StrafeServices(this, actor, canTurn));
        var target = actor.AttentionTarget;
        if (actor.Controls.LooseAttention && actor.LookState.LooseAttentionTarget is { } loose)
            target = loose as ZoneAiObjectState ?? throw new NotSupportedException("Puppet loose attention requires actual AI-object state.");
        ZoneNpcPuppetLookTarget? attention = target is null ? null : CopyTarget(actor, target, navigation);
        var threeDimensional = navigation?.UsesThreeDimensionalNavigation ??
            (parameters.NavigationCapabilities == 134 ? false : throw new NotSupportedException("Puppet look output requires the actual mixed/three-dimensional navigation owner."));
        Require(actor);
        Vector3? pathSample = null;
        if (actor.Controls.DesiredSpeed > 0 && actor.Controls.MovementDirection != Vector3.Zero &&
            (actor.Controls.DesiredSpeed >= .01f && actor.Navigation.CurrentPath.HasPath && !staged.AllowStrafing || attention is null))
        {
            if (navigation?.ExistingPredictiveFollower is { } follower)
                pathSample = follower.GetPointAtDistance(follower.Parameters.LookAhead, out _);
            else
            {
                var sample = pose.EntityPosition;
                if (actor.Navigation.CurrentPath.TryGetPosition(2.5f, !parameters.ThreeDimensionalMovement, true, ref sample)) pathSample = sample;
            }
        }
        var speed = staged.AllowStrafing && lookAbility.FaceTargetInstantlyOnStrafing ? 0 :
            (_motion.CopyPuppetAlertness(actor) ?? throw new NotSupportedException("Puppet look requires its actual alertness state.")) <= 0 ?
                lookAbility.IdleTurnSpeed : lookAbility.CombatTurnSpeed;
        if (speed >= .000001f)
        {
            var unit = actor.Npc.Unit;
            var attribute = _stats.CalculateAttributes(unit.Snapshot, [72], unit.Buffs, unit.Progression, unit.Equipment, unit.AttributeOverrides);
            if (!attribute.IsComplete) throw new NotSupportedException("Puppet turn attribute72 has unresolved actual sources: " + string.Join(", ", attribute.UnresolvedSources));
            speed *= unchecked((int)attribute.Values[72]) * .01f + 1;
        }
        Require(actor);
        var result = ZoneNpcPuppetLookMath.Build(new(pose.Position, pose.EntityPosition, pose.BodyDirection,
            attention, threeDimensional, actor.Navigation.CurrentPath.HasPath, actor.Controls.DesiredSpeed,
            actor.Controls.MovementDirection, 0, pathSample, staged.AllowStrafing, oldStrafe,
            owner.LookTarget, owner.BodyTarget, speed, _actors.FrameTimeSeconds));
        if (ProxyUpdatesEnabled && _cvars.GetString("ai_ForceLookAimTarget", "none") != "none")
            throw new NotSupportedException("Forced Puppet look targets require the actual named entity lookup and target producer.");
        //All externally queried owners and calculations have succeeded before publication.
        Require(actor);
        actor.Controls.MovementUrgency = urgency;
        actor.Controls.AllowStrafing = staged.AllowStrafing;
        actor.Controls.StrafeStartDistance = staged.StrafeStartDistance;
        ZoneActorMovementControl.UpdateDelayedStance(actor.Controls);
        owner.LookTarget = result.LookTarget; owner.BodyTarget = result.BodyTarget;
        if (ProxyUpdatesEnabled)
        {
            var forceStance = _cvars.GetInt("ai_ForceStance", -1); if (forceStance > -1) actor.Controls.BodyState = forceStance;
            var forceStrafe = _cvars.GetInt("ai_ForceAllowStrafing", -1); if (forceStrafe > -1) actor.Controls.AllowStrafing = forceStrafe != 0;
        }
        owner.GoalsPrepared = false; owner.OutputReady = true; owner.BuildCount++;
        return CopyStateForProxy(actor);
    }

    /// <summary>Recopy live scalars after CAIHandler's signal phase without recomputing look or delayed stance.</summary>
    public ZoneManagedNpcProxyMovementOutput CopyStateForProxy(ZoneManagedNpcActor actor)
    {
        var owner = Require(actor);
        if (!owner.OutputReady) throw new InvalidOperationException("Puppet proxy state has no completed output phase for this scheduled update.");
        var navigation = _navigation?.Invoke(actor); Require(actor);
        if (navigation is not null && (!navigation.IsCurrent || !ReferenceEquals(navigation.Actor, actor)))
            throw new ObjectDisposedException(nameof(ZoneManagedNpcNavigationLease));
        var controls = actor.Controls;
        var output = new ZoneManagedNpcProxyMovementOutput(controls.BodyState, owner.LookTarget, actor.LookState.UseBodyDirection,
            owner.BodyTarget, controls.MovementDirection, controls.DesiredSpeed, controls.MovementUrgency,
            controls.AllowStrafing, controls.DistanceToPathEnd, navigation?.MovementContext ?? 0);
        if (!Finite(output.LookTarget) || !Finite(output.BodyTarget) || !Finite(output.MoveDirection) ||
            !float.IsFinite(output.DesiredSpeed) || !float.IsFinite(output.MovementUrgency) || !float.IsFinite(output.DistanceToPathEnd))
            throw new InvalidDataException("The live Puppet proxy state became nonfinite during its handler phase.");
        return output;
    }

    public ZoneManagedNpcPuppetMovementState CopyStuckMovementInput(ZoneManagedNpcActor actor)
    { Require(actor); return new(actor.Controls.MovementDirection, actor.Controls.DesiredSpeed); }
    public float CopyTimePassed(ZoneManagedNpcActor actor) => Require(actor).TimePassed;

    internal static float CalculateTimePassed(long previous, long now)
    {
        if (!((float)previous * .000001f > 0)) return 0;
        var elapsed = (float)unchecked(now - previous) * .000001f;
        return elapsed < .5f ? elapsed : .5f;
    }

    private bool CanTurn(ZoneManagedNpcActor actor)
    {
        //39258910 reads copied Unit32080/81/91/92, not current buff presence or
        //collection identity. The actual status owner retains cache/publication history.
        var status = actor.Npc.Unit.MovementStatus.CopySnapshot();
        //Actual dedicated ActorUnitModel39277D00 initializes client input144=null;
        //model.v320/392A4970 reads that distinct pointer. No client owner is installed here.
        return !status.Stunned && !status.Sleeping && !status.Psychokinesis && !status.ImpossibleRotate;
    }

    private ZoneNpcPuppetLookTarget CopyTarget(ZoneManagedNpcActor actor, ZoneAiObjectState state, ZoneManagedNpcNavigationLease? navigation)
    {
        if (_actors.ObjectOwner(state) is { } target)
            return new(state.Position, _actors.Position(target), target.Entry.Registration!.ObjectType);
        foreach (var candidate in _actors.CopyActors())
            if (ReferenceEquals(candidate.Points?.Look.State, state) || ReferenceEquals(candidate.Points?.Reference.State, state) ||
                ReferenceEquals(_navigation?.Invoke(candidate)?.TraceTarget, state))
            { Require(actor); return new(state.Position, state.Position, 2); }
        if (navigation is not null && ReferenceEquals(navigation.TraceTarget, state)) return new(state.Position, state.Position, 2);
        throw new NotSupportedException("Puppet attention requires its actual registered target type and entity/view position owners.");
    }

    private static ZoneActorControlState StageControls(ZoneActorControlState state) => new()
    {
        AllowStrafing = state.AllowStrafing, AllowedStrafeDistances = state.AllowedStrafeDistances,
        DistanceToPathEnd = state.DistanceToPathEnd, PathStartPosition = state.PathStartPosition,
        StrafeStartDistance = state.StrafeStartDistance,
    };
    private sealed class StrafeServices(ZoneManagedNpcPuppetOutput owner, ZoneManagedNpcActor actor, bool canTurn) : IZoneActorStrafeServices
    {
        public bool CanStrafe() => canTurn;
        public bool HasPath() => actor.Navigation.OriginalPath.HasPath;
        public float GetPathLength() => actor.Navigation.OriginalPath.GetLength();
        public Vector3 GetPosition() => actor.AiObject.Position;
        public object? GetAttentionTarget() => actor.AttentionTarget;
        public Vector3 GetTargetPosition(object target) => owner.CopyTarget(actor, (ZoneAiObjectState)target, null).ViewPosition;
        public Vector3? GetFirstPathOutputPosition() => actor.Navigation.MovementOutput.Count == 0 ? null : actor.Navigation.MovementOutput[0].Position;
    }
    public ZoneManagedNpcPuppetOutputLease? Find(ZoneActorHandle actor) => !IsDisposed &&
        _live.TryGetValue(actor, out var owner) && owner.IsCurrent ? owner : null;
    private ZoneManagedNpcPuppetOutputLease Require(ZoneManagedNpcActor actor)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this); _actors.Require(actor);
        return Find(actor.Handle) is { } owner && ReferenceEquals(owner.Actor, actor) ? owner :
            throw new ObjectDisposedException(nameof(ZoneManagedNpcPuppetOutputLease), "This exact Puppet output generation retired.");
    }
    internal bool Owns(ZoneManagedNpcPuppetOutputLease owner) => !IsDisposed && !owner.IsDisposed &&
        _live.TryGetValue(owner.Actor.Handle, out var current) && ReferenceEquals(current, owner) &&
        ReferenceEquals(_actors.Find(owner.Actor.Handle), owner.Actor) && ReferenceEquals(_poses.Find(owner.Actor.Handle), owner.Pose) &&
        ReferenceEquals(_ability.Find(owner.Actor.Handle), owner.Ability) && _motion.Find(owner.Actor.Handle) is not null;
    internal void Retire(ZoneManagedNpcPuppetOutputLease owner)
    { if (_live.TryGetValue(owner.Actor.Handle, out var current) && ReferenceEquals(current, owner)) _live.Remove(owner.Actor.Handle); }
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    public void Dispose()
    { if (IsDisposed) return; IsDisposed = true; foreach (var owner in _live.Values.ToArray()) owner.Dispose(); }
}

public sealed class ZoneManagedNpcPuppetOutputLease : IDisposable
{
    private readonly ZoneManagedNpcPuppetOutput _owner;
    public ZoneManagedNpcActor Actor { get; }
    internal ZoneManagedNpcAiPoseLease Pose { get; }
    internal ZoneManagedNpcMovementAbilityLease Ability { get; }
    internal Vector3 LookTarget, BodyTarget; //The base actor's SOBJECTSTATE constructor initializes these independently tozero.
    internal bool GoalsPrepared, OutputReady;
    public long LastUpdateMicroseconds { get; internal set; }
    public float TimePassed { get; internal set; }
    public ulong BuildCount { get; internal set; }
    public bool IsDisposed { get; private set; }
    public bool IsCurrent => _owner.Owns(this);
    internal ZoneManagedNpcPuppetOutputLease(ZoneManagedNpcPuppetOutput owner, ZoneManagedNpcActor actor,
        ZoneManagedNpcAiPoseLease pose, ZoneManagedNpcMovementAbilityLease ability)
    { _owner = owner; Actor = actor; Pose = pose; Ability = ability; }
    public void Dispose() { if (IsDisposed) return; IsDisposed = true; _owner.Retire(this); }
}
