using System.Numerics;
using AAEmu.Zone.Protocol.Units;
using AAEmu.Zone.Scripting;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.Scene;

/// <summary>Actual Model.flags40 and CActor548, independently consumed by AIUnitSystem39212E70.</summary>
public readonly record struct ZoneManagedNpcMovementEligibilityState(uint ModelFlags, bool IsPsychokinesisTarget);

/// <summary>
/// The ordered model latch, its CActor552 scalar and completed cached-movement resets. This
/// is not a count of current buffs; removing either overlapping effect clears the native latch.
/// </summary>
public readonly record struct ZoneManagedNpcPsychokinesisState(bool IsTarget, float Speed, ulong MovementResetCount);

public sealed partial class ZoneManagedNpcActorMotion
{
    //Replacement resource bound, matching the Unit's retained pre-admission journal.
    //Overflow is an explicit unsupported history, never a dropped/collapsed latch write.
    private const int MaximumPendingModelEffects = 1024;
    private readonly Queue<(ZoneManagedNpcActorMotionLease Owner, ZoneManagedNpcScriptFault Fault)> _modelEffectFaults = [];

    public ZoneManagedNpcMovementEligibilityState CopyMovementEligibilityState(ZoneManagedNpcActor actor)
    {
        var owner = Require(actor);
        RequireDependencies(owner);
        RefuseModelEffectFailure(owner);
        return new(owner.ModelFlags, owner.Psychokinesis.IsTarget);
    }

    public ZoneManagedNpcPsychokinesisState CopyPsychokinesisState(ZoneManagedNpcActor actor)
    {
        var owner = Require(actor);
        RequireDependencies(owner);
        RefuseModelEffectFailure(owner);
        return owner.Psychokinesis;
    }

    /// <summary>
    /// World buff state remains committed. The next safe scene boundary consumes one copied
    /// failure and retires its exact generation before any subsequent body preparation.
    /// </summary>
    public IReadOnlyList<ZoneManagedNpcScriptFault> DrainFaults()
    {
        Check();
        var result = new List<ZoneManagedNpcScriptFault>();
        while (_modelEffectFaults.TryDequeue(out var entry))
            if (entry.Owner.IsCurrent) result.Add(entry.Fault);
        return result.AsReadOnly();
    }

    private static void RefuseModelEffectFailure(ZoneManagedNpcActorMotionLease owner)
    {
        if (owner.ModelEffectFailure is { } failure)
            throw new NotSupportedException(failure);
    }

    private void PoisonModelEffects(ZoneManagedNpcActorMotionLease owner, string failure)
    {
        if (!OwnsModelEffectSubscription(owner) || owner.ModelEffectFailure is not null) return;
        owner.ModelEffectFailure = failure;
        _modelEffectFaults.Enqueue((owner, new(owner.Actor.Handle, "model psychokinesis buff effect", failure)));
    }

    private bool OwnsModelEffectSubscription(ZoneManagedNpcActorMotionLease owner) => !IsDisposed && !owner.IsDisposed &&
        _live.TryGetValue(owner.Actor.Handle, out var current) && ReferenceEquals(current, owner) &&
        ReferenceEquals(_actors.Find(owner.Actor.Handle), owner.Actor) && owner.Construction.IsCurrent;

    private void BindModelEffects(ZoneManagedNpcActorMotionLease owner)
    {
        var unit = owner.Actor.Npc.Unit;
        owner.ProcessingModelEffects = true;
        owner.BuffEffectHandler = (source, effect) =>
        {
            if (!ReferenceEquals(source, unit) || !OwnsModelEffectSubscription(owner) ||
                owner.ModelEffectFailure is not null || !effect.Template.Psychokinesis) return;
            QueueModelEffect(owner, effect);
            if (owner.ModelInitializationCompleted && !owner.ProcessingModelEffects) DrainModelEffects(owner);
        };
        unit.BuffEffect += owner.BuffEffectHandler;
        try
        {
            var initial = unit.CopyInitialBuffEffects();
            foreach (var effect in unit.CopyBuffModelEffectJournal()) QueueModelEffect(owner, effect);
            //3935D260 restores actual snapshot effects before39346960's final flag1.
            //The mutable current collection cannot replace this ordered constructor input.
            foreach (var effect in initial)
            {
                ApplyPsychokinesisEffect(owner, effect);
                if (!OwnsModelEffectSubscription(owner))
                    throw new ObjectDisposedException(nameof(ZoneManagedNpcActorMotionLease), "The NPC retired during restored model effects.");
            }
            RefuseModelEffectFailure(owner);
        }
        finally { owner.ProcessingModelEffects = false; }
    }

    private void QueueModelEffect(ZoneManagedNpcActorMotionLease owner, ZoneUnitBuffEffect effect)
    {
        if (!effect.Template.Psychokinesis || effect.Sequence <= owner.AppliedModelEffectSequence && effect.Sequence != 0) return;
        if (effect.Sequence == 0 || effect.IsInitialRestore)
        {
            PoisonModelEffects(owner, "NPC model effects require their actual ordered post-enter buff mutation sequence.");
            return;
        }
        if (owner.PendingModelEffects.TryGetValue(effect.Sequence, out var previous))
        {
            if (previous != effect) PoisonModelEffects(owner, "NPC model effect sequence was reused for different authored effects.");
            return;
        }
        if (owner.PendingModelEffects.Count == MaximumPendingModelEffects)
        {
            PoisonModelEffects(owner, "NPC model effect backlog exceeded the bounded replacement history capacity.");
            return;
        }
        owner.PendingModelEffects.Add(effect.Sequence, effect);
    }

    private void DrainModelEffects(ZoneManagedNpcActorMotionLease owner)
    {
        if (owner.ProcessingModelEffects || owner.ModelEffectFailure is not null) return;
        owner.ProcessingModelEffects = true;
        try
        {
            var work = 0;
            while (owner.PendingModelEffects.Count != 0 && OwnsModelEffectSubscription(owner))
            {
                if (++work > MaximumPendingModelEffects)
                    throw new NotSupportedException("NPC model effect callback work exceeded the bounded replacement history capacity.");
                var next = owner.PendingModelEffects.First();
                if (next.Key != owner.AppliedModelEffectSequence + 1)
                    throw new NotSupportedException("NPC model effect history is incomplete; ordered psychokinesis mutations cannot be inferred from current buffs.");
                ApplyPsychokinesisEffect(owner, next.Value);
                if (!OwnsModelEffectSubscription(owner)) return;
                owner.AppliedModelEffectSequence = next.Key;
                owner.PendingModelEffects.Remove(next.Key);
            }
        }
        catch (Exception error) when (error is NotSupportedException or InvalidDataException or ObjectDisposedException)
        { PoisonModelEffects(owner, error.Message); }
        finally { owner.ProcessingModelEffects = false; }
    }

    private void ApplyPsychokinesisEffect(ZoneManagedNpcActorMotionLease owner, ZoneUnitBuffEffect effect)
    {
        if (!effect.Template.Psychokinesis) return;
        if (effect.Kind is not (ZoneUnitBuffEffectKind.Apply or ZoneUnitBuffEffectKind.Remove))
            throw new InvalidDataException("NPC psychokinesis requires an actual buff apply/remove effect boundary.");
        var apply = effect.Kind == ZoneUnitBuffEffectKind.Apply;
        var speed = apply ? effect.Template.PsychokinesisSpeed : 0f;
        if (!float.IsFinite(speed))
            throw new NotSupportedException("NPC psychokinesis requires its finite authored psychokinesis_speed scalar.");
        RequireDependencies(owner);
        var body = owner.Body.CopyState();
        RequireDependencies(owner);
        //39280090 clears CActor's cached request (mask0x1B1F8000), then resets
        //NetActorMovementController inputs. It never sets living velocity or a pose.
        //This profile has only constructor-empty actor/network caches. Nonempty raw
        //living wishes cannot stand in for the missing higher-level reset consumer.
        if (body.Control.RequestedVelocity != Vector3.Zero || body.Control.HasPendingVelocityRequest ||
            owner.ControllerId != 0)
            throw new NotSupportedException("NPC psychokinesis model reset requires its nonempty movement/controller request consumer.");
        var pose = _actors.ProxyPoses?.Find(owner.Actor.Handle) ?? throw new NotSupportedException(
            "NPC psychokinesis model reset requires its actual actor/controller pose-cache owner.");
        pose.RequireEmptyModelMovementResetProfile();
        RequireDependencies(owner);
        RefuseModelEffectFailure(owner);
        if (owner.Psychokinesis.MovementResetCount == ulong.MaxValue)
            throw new InvalidDataException("NPC model movement reset counter overflowed.");
        owner.Psychokinesis = new(apply, speed, owner.Psychokinesis.MovementResetCount + 1);
        //The same native flag setter calls ActorUnitModel1480/nullsub392631F0.
        //Neither a count of current buffs nor independent entity/AI activation owns it.
        if (owner.Actor.Npc.Unit.Snapshot.Identity.Kind == ZoneUnitKind.Npc)
            owner.ModelFlags = apply ? owner.ModelFlags & ~1u : owner.ModelFlags | 1u;
    }
}

public sealed partial class ZoneManagedNpcActorMotionLease
{
    //392E7D80 zeroes stats+412/+416, corresponding to CActor548 and552.
    internal ZoneManagedNpcPsychokinesisState Psychokinesis;
    internal string? ModelEffectFailure;
    internal Action<ZoneUnit, ZoneUnitBuffEffect>? BuffEffectHandler;
    internal bool ModelInitializationCompleted, ProcessingModelEffects;
    internal ulong AppliedModelEffectSequence;
    internal SortedDictionary<ulong, ZoneUnitBuffEffect> PendingModelEffects { get; } = [];
}
