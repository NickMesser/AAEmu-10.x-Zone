using System.Numerics;

namespace AAEmu.Zone.Scene;

/// <summary>
///392EAFC0's no-animation-target input and the upright/no-jump derived Player path.
///World callbacks, stat inputs, the turn-slowing profile and lifetime remain concrete owners.
/// </summary>
internal static class ZoneNpcOrdinaryMovementMath
{
    //392DC900 preserves zero/negative authored values; only positive inertia is
    //suppressed by CActor264/392E97A0's actual124&0x1000 walking latch.
    internal static float EffectiveInertia(float authored, bool walking) => authored > 0 && walking ? 0 : authored;

    internal static (Vector3 Velocity, Quaternion Orientation, Vector3 LookTarget) Build(
        ZoneManagedNpcControllerRequest request, Vector3 position, Quaternion entityRotation,
        Vector3 controllerViewPosition, Quaternion actorViewRotation, float effectiveNormalSpeed,
        float groundedSeconds, float airborneSeconds, bool playerClass, bool aiEnabled, bool canRotate = true)
    {
        var forward = ZoneNpcProxyMovementMath.Rotate(Vector3.UnitY, entityRotation);
        var positiveSpeed = (request.Flags & 4) != 0 && request.DesiredSpeed > .000019999999f;
        var target = (request.Flags & 0x4000) != 0 ? request.LookTarget :
            positiveSpeed ? position + ZoneNpcProxyMovementMath.Rotate(request.Delta, entityRotation) : position + forward;
        var facing = target - position;
        if (canRotate && facing.Z != 0)
            throw new NotSupportedException("NPC body-facing pitch requires the complete source view/rotation-limit producer.");
        var square = (facing.X * facing.X + facing.Y * facing.Y) + facing.Z * facing.Z;
        facing = square <= 1.1920928955078125e-7f ? Vector3.Zero : facing * (1 / MathF.Sqrt(square));
        var localFacing = ZoneNpcProxyMovementMath.Rotate(facing, Quaternion.Conjugate(entityRotation));
        //392EAFC0's dead zone is on normalized forward Y, not a yaw epsilon.
        //392EAFC0 gates generated facing angles through392D73D0/39258910 before
        //derived rotation. This leaves translation and independent head look intact.
        var yaw = canRotate && 1 - localFacing.Y > .000001f && square > 1.1920928955078125e-7f
            ? MathF.Atan2(-localFacing.X, localFacing.Y) : 0;
        if (!playerClass && yaw != 0)
            throw new NotSupportedException("Turning Monster/Quadruped movement requires its distinct derived rotation producer.");
        var halfYaw = yaw * .5f;
        var orientation = Quaternion.Normalize(Quaternion.Multiply(actorViewRotation,
            new Quaternion(0, 0, MathF.Sin(halfYaw), MathF.Cos(halfYaw))));
        var velocity = Vector3.Zero;
        //392D70A0 admits NPC Unit type1. Its branch rotates XY without clamping the
        //local input to1 or applying the player's directional speed multipliers.
        if (request.Delta.X != 0 || request.Delta.Y != 0)
            velocity = ZoneNpcProxyMovementMath.Rotate(new(request.Delta.X, request.Delta.Y, 0), orientation);
        velocity *= effectiveNormalSpeed;
        if (groundedSeconds == 0 && (velocity.X * velocity.X + velocity.Y * velocity.Y) + velocity.Z * velocity.Z <= .0099999998f)
            velocity = Vector3.Zero;
        var originalLength = MathF.Sqrt((velocity.X * velocity.X + velocity.Y * velocity.Y) + velocity.Z * velocity.Z);
        velocity.Z = 0;
        if (airborneSeconds != 0 && originalLength > 0)
        {
            var length = MathF.Sqrt((velocity.X * velocity.X + velocity.Y * velocity.Y) + velocity.Z * velocity.Z);
            if (length > 0) velocity *= originalLength / length;
        }
        var look = controllerViewPosition + forward * 10;
        if ((request.Flags & 1) != 0 && aiEnabled)
        {
            var eyeOrigin = new Vector3(position.X, position.Y, controllerViewPosition.Z);
            var direction = request.MoveTarget - eyeOrigin;
            var lengthSquared = (direction.X * direction.X + direction.Y * direction.Y) + direction.Z * direction.Z;
            if (lengthSquared > 1.1920928955078125e-7f)
            {
                var normalized = direction * (1 / MathF.Sqrt(lengthSquared));
                var dot = (normalized.X * forward.X + normalized.Y * forward.Y) + normalized.Z * forward.Z;
                if (dot <= MathF.Cos(2.0943952f * .5f))
                    throw new NotSupportedException("NPC clamped head-look cone requires its original vector/quaternion interpolation consumer.");
            }
            look = request.MoveTarget;
        }
        if (!Finite(velocity) || !Finite(look) || !float.IsFinite(orientation.LengthSquared()))
            throw new InvalidDataException("NPC actor movement conversion overflowed finite request precision.");
        return (velocity, orientation, look);
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
