using System.Numerics;
using AAEmu.Zone.Actors;

namespace AAEmu.Zone.Physics;

/// <summary>
/// Ordinary player input/correction over owned physics. Gravity and jump impulses own vertical
/// motion; repeated client poses and horizontal movement requests must not erase that integration.
/// Explicit scene teleports still call the backend directly. Positions use engine zone-local meters.
/// </summary>
public sealed class ZoneOwnedMovementMotion : IZonePhysicsMotion
{
    private readonly IZonePhysicsBackend _backend;
    private readonly IZonePhysicsMotion _motion;

    public ZoneOwnedMovementMotion(ZoneOwnedPhysicsBackend backend) : this(backend, backend) { }

    public ZoneOwnedMovementMotion(IZonePhysicsBackend backend, IZonePhysicsMotion motion)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _motion = motion ?? throw new ArgumentNullException(nameof(motion));
    }

    public void ApplyVelocity(ZoneActorHandle actor, Vector3 velocity)
    {
        _backend.CopyStatus(actor, out var current);
        _motion.ApplyVelocity(actor, new(velocity.X, velocity.Y, current.Velocity.Z));
    }

    public void ApplyImpulse(ZoneActorHandle actor, Vector3 impulse) => _motion.ApplyImpulse(actor, impulse);

    public void ApplyPose(ZoneActorHandle actor, Vector3 position, Quaternion orientation)
    {
        _backend.CopyStatus(actor, out var current);
        _motion.ApplyPose(actor, new(position.X, position.Y, current.Position.Z), orientation);
        // The backend's explicit pose correction clears velocity. A regular turn/correction
        // changes neither the integrated vertical velocity nor the pending horizontal velocity.
        _motion.ApplyVelocity(actor, current.Velocity);
    }
}
