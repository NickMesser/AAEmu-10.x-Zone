using AAEmu.Zone.Actors;

namespace AAEmu.Zone.Physics;

public sealed partial class ZoneManagedPhysicsBackend
{
    public void SetLivingCollisionActive(ZoneActorHandle actor, bool active)
    {
        Require(actor);
        var description = _actors[actor];
        if (description.Profile != ZonePhysicsActorProfile.Living)
            throw new NotSupportedException("Collision-active dynamics require a registered living body.");
        _actors[actor] = description with { CollisionActive = active };
    }

    /// <summary>392DC900's immediate type4 inertia update preserves pose, velocity and support.</summary>
    public void SetLivingInertia(ZoneActorHandle actor, float inertia)
    {
        Require(actor);
        var description = _actors[actor];
        if (description.Profile != ZonePhysicsActorProfile.Living || description.LivingMovement is null)
            throw new NotSupportedException("A live authored living body is required for an inertia update.");
        if (!float.IsFinite(inertia))
            throw new ArgumentOutOfRangeException(nameof(inertia), "Living inertia must be finite.");
        if (description.LivingMovement.Inertia == inertia) return;
        _actors[actor] = description with { LivingMovement = description.LivingMovement.WithInertia(inertia) };
    }
}
