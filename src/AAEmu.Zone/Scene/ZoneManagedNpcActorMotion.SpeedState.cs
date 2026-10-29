using System.Numerics;
using AAEmu.Zone.Physics;

namespace AAEmu.Zone.Scene;

/// <summary>ActorMovementController300/332: accepted CActor stance and the actual smoothed2376 field.</summary>
public readonly record struct ZoneManagedNpcMovementSpeedState(int Stance, float ControllerAngle);

public sealed partial class ZoneManagedNpcActorMotion
{
    public ZoneManagedNpcMovementSpeedState CopyMovementSpeedState(ZoneManagedNpcActor actor)
    {
        var owner = Require(actor);
        var state = owner.Body.CopyState(); Require(actor);
        return new(state.CurrentStanceSlot, owner.SurfaceSlope.Primary);
    }

    private ZoneNpcControllerSlopeState StageMovementSpeedState(ZoneManagedNpcActorMotionLease owner,
        ZoneManagedNpcBodySnapshot body, float elapsed)
    {
        if (!body.AppliedDescription.CollisionActive) return owner.SurfaceSlope;
        var pose = _actors.ProxyPoses?.Find(owner.Actor.Handle)?.Last ??
            throw new NotSupportedException("CActor controller-angle sampling requires its actual view-rotation producer.");
        //392E7140 calls392DCEF0 before updating this frame's ground/air/water statistics.
        //The admitted dry/no-jump profile owns both of the suppressing inputs as zero.
        return ZoneNpcControllerSlopeMath.Step(owner.SurfaceSlope, elapsed,
            body.Control.CachedGroundNormal, pose.ActorViewRotation, suppress: false);
    }
}

public sealed partial class ZoneManagedNpcActorMotionLease
{
    //Stats392E7D80 initializes356/360/364=(0,0,1); CActor392E6BC0 initializes
    //2376/2380/2384/2388=0 and2392=.2. These fields are independent of movement wishes.
    internal ZoneNpcControllerSlopeState SurfaceSlope = new(Vector3.UnitZ, 0, 0, 0, 0);
}

internal readonly record struct ZoneNpcControllerSlopeState(Vector3 Normal,
    float Primary, float Secondary, float PrimaryVelocity, float SecondaryVelocity);

/// <summary>392DCEF0 plus39252A80. Unnamed fields remain scalar controller state, not degrees.</summary>
internal static class ZoneNpcControllerSlopeMath
{
    internal static ZoneNpcControllerSlopeState Step(ZoneNpcControllerSlopeState state, float elapsed,
        Vector3 actualLivingGroundNormal, Quaternion actorViewRotation, bool suppress)
    {
        if (!(elapsed > 0) || !float.IsFinite(elapsed) || !Finite(actualLivingGroundNormal) ||
            !float.IsFinite(actorViewRotation.X) || !float.IsFinite(actorViewRotation.Y) ||
            !float.IsFinite(actorViewRotation.Z) || !float.IsFinite(actorViewRotation.W))
            throw new InvalidDataException("CActor controller-angle inputs must be finite with positive elapsed time.");
        var normal = state.Normal;
        if (normal != actualLivingGroundNormal)
        {
            var factor = Math.Clamp(elapsed / .15000001f, 0, 1);
            normal = new(normal.X + (actualLivingGroundNormal.X - normal.X) * factor,
                (actualLivingGroundNormal.Y - normal.Y) * factor + normal.Y,
                (actualLivingGroundNormal.Z - normal.Z) * factor + normal.Z);
        }
        var q = actorViewRotation;
        var right = new Vector3((q.X * q.X + q.W * q.W) * 2 - 1,
            (q.Y * q.X + q.Z * q.W) * 2, (q.Z * q.X - q.Y * q.W) * 2);
        var forward = new Vector3((q.Y * q.X - q.Z * q.W) * 2,
            (q.W * q.W + q.Y * q.Y) * 2 - 1, (q.Z * q.Y + q.W * q.X) * 2);
        var first = -Dot(NormalizeMinimum(Cross(right, Cross(normal, right))), forward);
        var second = Dot(NormalizeMinimum(Cross(forward, Cross(normal, forward))), right);
        if (suppress) first = second = 0;
        var (primary, primaryVelocity) = Smooth(state.Primary, state.PrimaryVelocity, first, elapsed);
        var (secondary, secondaryVelocity) = Smooth(state.Secondary, state.SecondaryVelocity, second, elapsed);
        var next = new ZoneNpcControllerSlopeState(normal, primary, secondary, primaryVelocity, secondaryVelocity);
        if (!Finite(normal) || !float.IsFinite(primary) || !float.IsFinite(secondary) ||
            !float.IsFinite(primaryVelocity) || !float.IsFinite(secondaryVelocity))
            throw new InvalidDataException("CActor controller-angle sampling overflowed finite precision.");
        return next;
    }

    private static (float Value, float Velocity) Smooth(float value, float velocity, float target, float elapsed)
    {
        //2392=.2 in the admitted constructor.39252A80's original rational spring,
        //including float operation order; this is not an exponential/Lerp substitute.
        const float smoothTime = .2f;
        var delta = value - target;
        var omega = 2 / smoothTime;
        var scaled = omega * elapsed;
        var denominator = ((scaled * .47999999f * scaled + (scaled + 1)) +
            scaled * .235f * scaled * scaled);
        var reciprocal = 1 / denominator;
        var change = (delta * omega + velocity) * elapsed;
        return ((change + delta) * reciprocal + target,
            (velocity - change * omega) * reciprocal);
    }

    private static Vector3 Cross(Vector3 a, Vector3 b) => new(
        a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    private static float Dot(Vector3 a, Vector3 b) => (a.X * b.X + a.Y * b.Y) + a.Z * b.Z;
    private static Vector3 NormalizeMinimum(Vector3 value) => value *
        (1 / MathF.Sqrt(((value.X * value.X + value.Y * value.Y) + value.Z * value.Z) + 1.1754944e-38f));
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
