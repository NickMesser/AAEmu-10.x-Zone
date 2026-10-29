using System.Numerics;

namespace AAEmu.Zone.Scene;

internal readonly record struct ZoneNpcPuppetLookTarget(Vector3 ViewPosition, Vector3 EntityPosition, int ObjectType);
internal readonly record struct ZoneNpcPuppetLookInput(Vector3 ViewPosition, Vector3 EntityPosition, Vector3 BodyDirection,
    ZoneNpcPuppetLookTarget? Attention, bool ThreeDimensionalMovement, bool HasPath, float DesiredSpeed,
    Vector3 MovementDirection, int ActorTargetPhase, Vector3? PathLookAhead, bool AllowStrafing, bool WasStrafing,
    Vector3 PreviousLookTarget, Vector3 PreviousBodyTarget, float TurnSpeed, float ElapsedSeconds);
internal readonly record struct ZoneNpcPuppetLookResult(Vector3 LookTarget, Vector3 BodyTarget);

/// <summary>3116CCBC and3116BD2C. The actor owner supplies actual target/path/pose state.</summary>
internal static class ZoneNpcPuppetLookMath
{
    private const float Minimum = 1.1754943508222875e-38f;

    internal static ZoneNpcPuppetLookResult Build(ZoneNpcPuppetLookInput input)
    {
        var look = Vector3.Zero; var body = Vector3.Zero;
        var useAttention = input.DesiredSpeed < .01f || !input.HasPath || input.AllowStrafing;
        if (!useAttention || input.Attention is null)
        {
            var move = input.MovementDirection;
            var moving = input.DesiredSpeed > 0 && input.ActorTargetPhase == 0 &&
                (MathF.Abs(move.X) > 0 || MathF.Abs(move.Y) > 0 || MathF.Abs(move.Z) > 0);
            if (moving)
            {
                var sample = input.PathLookAhead ?? input.EntityPosition;
                var x = sample.X - input.EntityPosition.X; var y = sample.Y - input.EntityPosition.Y; var z = 0f;
                var squared = (x * x + y * y) + z * z;
                if (squared < 1)
                {
                    var blend = 1 - MathF.Sqrt(squared);
                    x = (input.BodyDirection.X - x) * blend + x;
                    y = (input.BodyDirection.Y - y) * blend + y;
                    z = (0f - 0f) * blend + 0f;
                }
                var inverse = 1 / MathF.Sqrt(((x * x + y * y) + z * z) + Minimum);
                body = new((x * inverse) * 40 + input.EntityPosition.X,
                    (y * inverse) * 40 + input.EntityPosition.Y, (z * inverse) * 40 + input.EntityPosition.Z);
                //The native look vector retains the copied view Z; only the body
                //candidate receives the entity-origin Z from the planar path target.
                look = new(body.X, body.Y, input.ViewPosition.Z);
            }
        }
        else
        {
            var target = input.Attention.Value;
            look = target.ViewPosition; body = target.EntityPosition;
            var delta = target.ViewPosition - input.ViewPosition;
            var distance = MathF.Sqrt((delta.X * delta.X + delta.Y * delta.Y) + delta.Z * delta.Z);
            var kind = target.ObjectType;
            if ((distance < 1 || (kind is 0 or 13 or 12 || kind > 100) && distance < 5) && !input.ThreeDimensionalMovement)
            {
                var limit = MathF.Sin(.2617994f) * distance;
                look.Z = Clamp(look.Z, input.ViewPosition.Z - limit, input.ViewPosition.Z + limit);
                body.Z = Clamp(body.Z, input.EntityPosition.Z - limit, input.EntityPosition.Z + limit);
            }
        }
        if (input.TurnSpeed > .000001f)
            look = Smooth(input.ViewPosition, input.ViewPosition, input.BodyDirection,
                input.PreviousLookTarget, look, input.TurnSpeed, input.ElapsedSeconds);
        var bodyOutput = input.PreviousBodyTarget;
        if (input.AllowStrafing)
        {
            bodyOutput = body;
            if (input.TurnSpeed > .000001f)
            {
                var previous = input.PreviousBodyTarget;
                if (!input.WasStrafing)
                {
                    var forward = Rotate(CreateRotationDirection(input.BodyDirection), Vector3.UnitY);
                    previous = Add(input.EntityPosition, forward);
                }
                bodyOutput = Smooth(input.ViewPosition, input.EntityPosition, input.BodyDirection,
                    previous, body, input.TurnSpeed, input.ElapsedSeconds);
            }
        }
        if (!Finite(look) || !Finite(bodyOutput))
            throw new InvalidDataException("Puppet look output overflowed finite target/pose precision.");
        return new(look, bodyOutput);
    }

    //The native new-target vector uses actor.Position even for body smoothing; only
    //the retained vector and final target use the caller's view/entity origin.
    internal static Vector3 Smooth(Vector3 actorPosition, Vector3 origin, Vector3 bodyDirection,
        Vector3 previousTarget, Vector3 target, float speed, float elapsed)
    {
        if (!Nonzero(target)) return target;
        var previous = bodyDirection;
        if (Nonzero(previousTarget))
        {
            previous = previousTarget - origin;
            var inverse = 1 / MathF.Sqrt(((previous.Y * previous.Y + previous.X * previous.X) + previous.Z * previous.Z) + Minimum);
            previous = Scale(previous, inverse);
        }
        var next = target - actorPosition;
        var squared = (next.Y * next.Y + next.X * next.X) + next.Z * next.Z;
        var length = 0f;
        if (squared > 1.1920928955078125e-7f)
        {
            var inverse = 1 / MathF.Sqrt(squared); next = Scale(next, inverse); length = 1 / inverse;
        }
        if (length < .000001f) return target;
        var dot = (next.Y * previous.Y + next.X * previous.X) + next.Z * previous.Z;
        var limit = elapsed * speed;
        if (dot > MathF.Cos(limit) || limit < .000001f) return target;
        var upper = ((dot + 1) - MathF.Abs(dot - 1)) * .5f;
        var angle = MathF.Acos((MathF.Abs(upper - -1f) + (upper - 1)) * .5f);
        var factor = (angle - .78539819f) / .78539819f;
        factor = ((factor + 1) - MathF.Abs(factor - 1)) * .5f;
        factor = ((MathF.Abs(factor - 0f) + (factor + 0f)) * .5f) * 2 + 1;
        var step = factor * limit;
        if (angle < .000001f || step > angle) return target;
        var rotation = Slerp(CreateRotationDirection(previous), CreateRotationDirection(next), step / angle);
        return Add(Scale(Rotate(rotation, Vector3.UnitY), length), origin);
    }

    //310F41FC preserves its double intermediate construction rather than using a
    //generic look-at matrix, whose roll and vertical fallback differ.
    internal static Quaternion CreateRotationDirection(Vector3 direction)
    {
        var result = new Quaternion((float)((direction.Z + direction.Z) * .3535533845424652), 0, 0, .70710677f);
        double planar = MathF.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
        if (planar > .00001)
        {
            var x = direction.X / planar; var y = direction.Y / planar;
            var height = planar + 1; var forward = y + 1;
            var yawLength = Math.Sqrt(forward * forward + x * x);
            var pitchLength = Math.Sqrt(direction.Z * direction.Z + height * height);
            var yawCos = 0d; var yawSin = -1d;
            if (yawLength > .00001) { yawCos = forward / yawLength; yawSin = -(x / yawLength); }
            var pitchCos = height / pitchLength; var pitchSin = direction.Z / pitchLength;
            result = new((float)(pitchSin * yawCos), (float)(pitchSin * yawSin),
                (float)(pitchCos * yawSin), (float)(pitchCos * yawCos));
        }
        return result;
    }

    private static Quaternion Slerp(Quaternion previous, Quaternion next, float amount)
    {
        var dot = ((previous.Y * next.Y + previous.X * next.X) + previous.Z * next.Z) + previous.W * next.W;
        if (dot < 0) { next = new(-next.X, -next.Y, -next.Z, -next.W); dot = -dot; }
        if (dot <= .99989998f)
        {
            var residual = new Quaternion(next.X - previous.X * dot, next.Y - previous.Y * dot,
                next.Z - previous.Z * dot, next.W - previous.W * dot);
            var length = MathF.Sqrt(((residual.Y * residual.Y + residual.X * residual.X) + residual.Z * residual.Z) + residual.W * residual.W);
            var angle = MathF.Atan2(length, dot) * amount;
            var sin = MathF.Sin(angle); var cos = MathF.Cos(angle);
            return new((sin * residual.X) / length + cos * previous.X,
                (sin * residual.Y) / length + cos * previous.Y,
                (sin * residual.Z) / length + cos * previous.Z,
                (sin * residual.W) / length + cos * previous.W);
        }
        var result = new Quaternion(previous.X + (next.X - previous.X) * amount,
            previous.Y + (next.Y - previous.Y) * amount, previous.Z + (next.Z - previous.Z) * amount,
            (next.W - previous.W) * amount + previous.W);
        var inverse = 1 / MathF.Sqrt(((result.X * result.X + result.W * result.W) + result.Y * result.Y) + result.Z * result.Z);
        return new(result.X * inverse, result.Y * inverse, result.Z * inverse, result.W * inverse);
    }

    private static Vector3 Rotate(Quaternion q, Vector3 v)
    {
        var x = (v.Z * q.Y - v.Y * q.Z) + v.X * q.W;
        var y = (v.X * q.Z - q.X * v.Z) + q.W * v.Y;
        var z = (q.X * v.Y - v.X * q.Y) + q.W * v.Z;
        var cx = q.Y * z - q.Z * y; var cy = q.Z * x - q.X * z; var cz = q.X * y - q.Y * x;
        return new((cx + v.X) + cx, (cy + v.Y) + cy, (cz + v.Z) + cz);
    }
    private static Vector3 Scale(Vector3 v, float scalar) => new(v.X * scalar, v.Y * scalar, v.Z * scalar);
    private static Vector3 Add(Vector3 a, Vector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    private static bool Nonzero(Vector3 value) => MathF.Abs(value.X) > 0 || MathF.Abs(value.Y) > 0 || MathF.Abs(value.Z) > 0;
    private static float Clamp(float value, float minimum, float maximum) => value < minimum ? minimum : value > maximum ? maximum : value;
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
