namespace AAEmu.Zone.Scene;

/// <summary>
/// Server AIUnitSystem39212E70's movement predicate over the exact World Unit and model.
/// AI/entity activity, health and physical velocity are independent of these inputs.
/// </summary>
public sealed class ZoneManagedNpcMovementEligibility
{
    private readonly ZoneManagedNpcActorScene _actors;
    private readonly ZoneManagedNpcActorMotion _motion;

    internal ZoneManagedNpcMovementEligibility(ZoneManagedNpcActorScene actors, ZoneManagedNpcActorMotion motion)
    {
        _actors = actors;
        _motion = motion;
    }

    public bool CanMove(ZoneManagedNpcActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        //CryAI's proxy.v328 supplies the World Unit ID, independently of AI entity identity.
        //The scene already resolves that chain to this exact Unit/model generation. Native's
        //missing-Unit log+true does not grant a retired managed owner access to its replacement.
        _actors.Require(actor);
        var status = actor.Npc.Unit.MovementStatus.CopySnapshot();
        var model = _motion.CopyMovementEligibilityState(actor);
        _actors.Require(actor);
        return Evaluate(status.Stunned, status.Rooted, status.Sleeping, model.ModelFlags, model.IsPsychokinesisTarget);
    }

    //Golden controls execute the original complete leaf, including independent model bits.
    internal static bool Evaluate(bool stunned, bool rooted, bool sleeping, uint modelFlags, bool psychokinesis) =>
        !stunned && !rooted && !sleeping && (modelFlags & 1) != 0 && !psychokinesis;
}
