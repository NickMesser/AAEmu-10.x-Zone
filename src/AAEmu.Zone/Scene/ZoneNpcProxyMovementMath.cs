using System.Numerics;

namespace AAEmu.Zone.Scene;

/// <summary>Defined fields of the original0xB0 MovementRequest passed to ActorMovementController.</summary>
public sealed record ZoneManagedNpcControllerRequest(uint Flags, Vector3 MoveTarget, Vector3 LookTarget,
    float DesiredSpeed, float DistanceToPathEnd, int Stance, Vector3 Delta,
    int Alertness, float MovementScalar, bool? Walking);

/// <summary>CAIProxy30518020's copied ordinary no-animation-override request builder.</summary>
internal static class ZoneNpcProxyMovementMath
{
    internal static ZoneManagedNpcControllerRequest Build(ZoneManagedNpcProxyMovementOutput output,
        Quaternion entityRotation, float actorNormalSpeed, bool canMove, int alertness)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!Finite(output.LookTarget) || !Finite(output.BodyTarget) || !Finite(output.MoveDirection) ||
            !float.IsFinite(output.DesiredSpeed) || !float.IsFinite(output.MovementUrgency) ||
            !float.IsFinite(output.DistanceToPathEnd) || !float.IsFinite(actorNormalSpeed) ||
            !float.IsFinite(entityRotation.X) || !float.IsFinite(entityRotation.Y) ||
            !float.IsFinite(entityRotation.Z) || !float.IsFinite(entityRotation.W))
            throw new InvalidDataException("NPC proxy movement requires finite actual output, rotation and normal-speed inputs.");
        var flags = output.LookTargetEnabled ? 0x4000u : 0x10000000u;
        var look = output.LookTargetEnabled ? output.LookTarget : Vector3.Zero;
        var hasTarget = output.LookTarget != Vector3.Zero;
        if (hasTarget)
        {
            flags = (flags & 0xF7FFFFFEu) | 1;
            if (output.AllowStrafing)
            { flags = (flags & 0xEFFFBFFFu) | 0x4000; look = output.BodyTarget; }
        }
        else flags = (flags & 0xF7FFFFFEu) | 0x8000000;
        //The admitted source output has constructor lean0; the lean producer is separate.
        flags = (flags & 0xFFEFFEFFu) | 0x100000;
        flags = (flags & 0xFEFFFFF7u) | 8;
        var stance = output.BodyState switch { -1 => 6, 1 => 1, 5 => 5, 7 => 7, _ => 0 };
        var moving = output.MoveDirection != Vector3.Zero && output.DesiredSpeed > .000019999999f && canMove;
        var delta = Vector3.Zero;
        var desired = 0f;
        var scalar = 0f;
        bool? walking = null;
        if (!moving)
        {
            scalar = output.MovementContext == 1 ? MathF.Abs(output.MovementUrgency) : 0;
            flags = (flags & 0xFDFDF7DBu) | 0x2000800;
        }
        else
        {
            var urgency = output.MovementUrgency;
            if (MathF.Abs(urgency * output.DesiredSpeed) <= .0000099999997f)
            {
                if (urgency == 0)
                { desired = output.DesiredSpeed; scalar = output.DesiredSpeed; flags = (flags & 0xFDFFFFFBu) | 4; }
                else { scalar = 0; flags = (flags & 0xFDFFFFFBu) | 0x2000000; }
                flags = (flags & 0xFFFDF7FFu) | 0x800;
            }
            else
            {
                desired = urgency > 0 ? output.DesiredSpeed : -output.DesiredSpeed;
                scalar = MathF.Abs(urgency);
                flags = (flags & 0xFDFDF7FBu) | 0x804;
                //Every admitted owner has its actual valid World Unit ID.
                flags = (flags & 0xFDFFFFFBu) | 4;
            }
            var local = Rotate(output.MoveDirection, Quaternion.Conjugate(entityRotation));
            var squared = (local.X * local.X + local.Y * local.Y) + local.Z * local.Z;
            local = squared <= 1.1920928955078125e-7f ? Vector3.Zero : local * (1 / MathF.Sqrt(squared));
            delta = local * (actorNormalSpeed > 0 ? output.DesiredSpeed / actorNormalSpeed : 0);
            walking = scalar <= .40002f;
            flags |= 0x20;
        }
        flags = output.AllowStrafing ? flags | 0x20000000u : flags & 0xDFFFFFFFu;
        if (!Finite(delta)) throw new InvalidDataException("NPC proxy movement conversion overflowed finite input precision.");
        return new(flags, hasTarget ? output.LookTarget : Vector3.Zero, look,
            desired, output.DistanceToPathEnd, stance, delta, alertness, scalar, walking);
    }

    //390ADC00/30519740's quaternion/vector expansion, retaining float ordering.
    internal static Vector3 Rotate(Vector3 vector, Quaternion q)
    {
        var x = (q.Y * vector.Z - q.Z * vector.Y) + q.W * vector.X;
        var y = (q.Z * vector.X - q.X * vector.Z) + vector.Y * q.W;
        var z = (q.X * vector.Y - q.Y * vector.X) + vector.Z * q.W;
        return new((q.Y * z - q.Z * y) + ((q.Y * z - q.Z * y) + vector.X),
            (q.Z * x - q.X * z) + ((q.Z * x - q.X * z) + vector.Y),
            (q.X * y - q.Y * x) + ((q.X * y - q.Y * x) + vector.Z));
    }
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
