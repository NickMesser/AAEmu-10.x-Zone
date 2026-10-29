using System.Numerics;

namespace AAEmu.Zone.Scene;

/// <summary>
///392E0C80 copies living status2+24 to AIObject16, and status2+72 to AIObject28.
///The latter is body496's gravity, even though39213180 compares its squared magnitude.
///These cached fields are neither requested movement nor the normalized AI directions.
/// </summary>
public readonly record struct ZoneManagedNpcAiPhysicsState(Vector3 Velocity, Vector3 Gravity);

public sealed partial class ZoneManagedNpcActorMotion
{
    public ZoneManagedNpcAiPhysicsState CopyAiPhysicsState(ZoneManagedNpcActor actor) => Require(actor).AiPhysics;

    internal void CommitOwnedMovementRequest(ZoneManagedNpcActor actor, ZoneManagedNpcControllerRequest request, bool walking)
    {
        ArgumentNullException.ThrowIfNull(request);
        var owner = Require(actor);
        owner.ControllerRequest = request;
        owner.SourceWalking = walking;
    }

    internal void CompleteOwnedProxyUpdate(ZoneManagedNpcActor actor, int handlerAlertness)
    {
        var owner = Require(actor);
        if (!actor.IsPuppet) return;
        //30725888+96=30514F20 reads the actual CAIHandler176 after signals.311680BC
        //copies it to Puppet4160 and updates system active-alertness counts. This ordinary
        //constructor/idle profile owns the zero branch; nonzero counts require their owner.
        if (handlerAlertness != 0)
            throw new NotSupportedException("Nonzero Puppet proxy alertness transfer requires its actual AI-system active-alertness count consumers.");
        owner.PuppetAlertness = handlerAlertness;
    }
}

public sealed partial class ZoneManagedNpcActorMotionLease
{
    internal ZoneManagedNpcControllerRequest? ControllerRequest;
    internal bool SourceWalking; //CActor392E6BC0: flags124=0, independent of SAIActorState.AllowStrafing.
    internal int PuppetAlertness; //CPuppet31171F8C: m_Alertness4160=0.
    internal ZoneManagedNpcAiPhysicsState AiPhysics; //3103D7F8 zeroes both vectors before the first physical copy.
}
