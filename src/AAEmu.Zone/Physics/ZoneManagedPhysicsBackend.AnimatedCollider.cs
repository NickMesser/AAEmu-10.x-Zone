using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Scene;

namespace AAEmu.Zone.Physics;

/// <summary>Copied applied raw fields; no unproved name is assigned to base type26's byte332.</summary>
public readonly record struct ZoneManagedColliderBodyState(uint PartGeometryFlags, uint PartColliderFlags,
    uint EntityFlags, uint LivingCollisionTypes, byte Type26Value, int? LastAnimatedMode);

public sealed partial class ZoneManagedPhysicsBackend
{
    //35025530: living part+80/+84, entity+92, body940;35071D30: base byte332.
    private static readonly ZoneManagedColliderBodyState InitialColliderBodyState = new(4324863, 2, 1603, 271, 0, null);
    private readonly Dictionary<ZoneActorHandle, ZoneManagedColliderBodyState> _animatedColliderBodies = [];

    public ZoneManagedColliderBodyState CopyAnimatedColliderState(ZoneActorHandle actor)
    {
        Require(actor);
        if (_actors[actor].Profile != ZonePhysicsActorProfile.Living)
            throw new NotSupportedException("Animated collider parameters require an actual living body.");
        return _animatedColliderBodies.GetValueOrDefault(actor, InitialColliderBodyState);
    }

    /// <summary>
    /// Consume the verified uncontended Disabled1/Pushable4 actor profile. Mode1's action1 is
    /// action_move(mode0,zero wish), NOT action_reset4: actual velocity, pose and suspension
    /// survive. Native35023650 queues when earlier body commands exist; this profile refuses
    /// that unported interleaving before any write. No auxiliary body has ever been admitted.
    /// Type26=false equals the fresh body's actual byte; a true byte and other collision masks
    /// require their consumers and remain explicit refusals. Friction/rigid-body response is separate.
    /// </summary>
    public void ApplyAnimatedColliderUpdate(ZoneActorHandle actor, ZoneAnimatedColliderPhysicsUpdate update)
    {
        Require(actor); ArgumentNullException.ThrowIfNull(update);
        var applied = _actors[actor];
        if (applied.Profile != ZonePhysicsActorProfile.Living || applied.LivingMovement is null)
            throw new NotSupportedException("Animated collider updates require an actual authored living body.");
        if (OwnedPhysics.IsStepping)
            throw new InvalidOperationException("Animated collider parameters cannot reenter an active staged physics step.");
        if (update.RequiresNonPushableAuxiliary || update.Mode == 5)
            throw new NotSupportedException("Collider mode5 requires the original owned non-pushable auxiliary physical entity.");
        if (update.Mode is not (1 or 4))
            throw new NotSupportedException($"Animated collider mode{update.Mode} requires unported living collision-type/response consumers.");
        if ((update.StateFlags & 4) != 0)
            throw new NotSupportedException("Animated collider type26 byte1 has no verified managed consumer.");
        if ((update.StateFlags & 2) != 0)
            throw new NotSupportedException("Animated collider reduced collision-type masks have no complete managed consumer.");
        if (_pendingLivingVelocity.Exists(command => command.Actor == actor) ||
            _pendingLivingStances.Exists(command => command.Actor == actor) ||
            _pendingLivingCorrectionPoses.Exists(command => command.Actor == actor))
            throw new NotSupportedException("Animated collider updates interleaved with pending native body commands require a unified command queue.");
        var inertia = update.Living.ReadSingle(4); var acceleration = update.Living.ReadSingle(8);
        if (inertia is not { } first || acceleration is not { } second || !float.IsFinite(first) || !float.IsFinite(second))
            throw new NotSupportedException("Animated collider inertia fields must be defined finite actor values.");
        var expected = ZoneAnimatedColliderParameters.Create(update.Mode, update.StateFlags, first, second, false);
        if (update.ResetAction1 != expected.ResetAction1 || update.RequiresNonPushableAuxiliary != expected.RequiresNonPushableAuxiliary)
            throw new NotSupportedException("Animated collider action/auxiliary metadata does not match its supported original profile.");
        Same(update.Living, expected.Living); Same(update.Part, expected.Part);
        Same(update.EntityFlags, expected.EntityFlags); Same(update.Type26, expected.Type26);
        var previous = CopyAnimatedColliderState(actor);
        var next = new ZoneManagedColliderBodyState(
            Mask(previous.PartGeometryFlags, update.Part, 0x44, 0x48),
            Mask(previous.PartColliderFlags, update.Part, 0x4C, 0x50),
            Mask(previous.EntityFlags, update.EntityFlags, 8, 12),
            update.Living.ReadUInt32(0x48)!.Value, update.Type26.ReadByte(4)!.Value, update.Mode);
        // All admitted flag changes are implemented: part bit2 controls target query admission;
        // the remaining part/entity/collision/type26 fields retain their verified living defaults.
        if ((next.PartGeometryFlags & ~2u) != (InitialColliderBodyState.PartGeometryFlags & ~2u) ||
            next.PartColliderFlags != 2 || next.EntityFlags != InitialColliderBodyState.EntityFlags ||
            next.LivingCollisionTypes != 271 || next.Type26Value != 0)
            throw new NotSupportedException("Animated collider flags exceed the verified disabled/pushable managed body profile.");
        var replacement = applied with
        {
            LivingMovement = applied.LivingMovement.WithInertia(first, second),
            CollisionActive = update.Living.ReadUInt32(0x5C) != 0,
        };
        if (update.ResetAction1)
        {
            var control = _livingControls[actor];
            control.Requested = Vector3.Zero; control.LastMode = ZonePhysicsLivingVelocityMode.Normal;
            control.Flag2 = false; control.ForceRequestedOnce = false;
        }
        _actors[actor] = replacement; _animatedColliderBodies[actor] = next;
    }

    internal bool ColliderBodyAdmitsQuery(ZoneActorHandle actor) =>
        (_animatedColliderBodies.GetValueOrDefault(actor, InitialColliderBodyState).PartGeometryFlags & 2) != 0;
    private void RetireAnimatedColliderBody(ZoneActorHandle actor) => _animatedColliderBodies.Remove(actor);
    private static uint Mask(uint previous, ZoneActorPhysicsParameterFields block, int orOffset, int andOffset)
    {
        var or = block.ReadUInt32(orOffset)!.Value; var and = block.ReadUInt32(andOffset)!.Value;
        return (or == ZoneActorPhysicalizationRequest.UnusedIntegerBits ? 0 : or) |
            previous & (and == ZoneActorPhysicalizationRequest.UnusedIntegerBits ? uint.MaxValue : and);
    }
    private static void Same(ZoneActorPhysicsParameterFields supplied, ZoneActorPhysicsParameterFields supported)
    {
        if (supplied.Length != supported.Length) throw new NotSupportedException("Unsupported animated collider parameter block size.");
        for (var i = 0; i < supported.Length; i++)
            if (supplied.IsDefined(i) != supported.IsDefined(i) || supplied.ReadByte(i) != supported.ReadByte(i))
                throw new NotSupportedException("An animated collider parameter contains an unsupported or undefined field write.");
    }
}
