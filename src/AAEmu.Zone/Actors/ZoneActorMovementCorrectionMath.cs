using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using AAEmu.Zone.Protocol.Messages;

namespace AAEmu.Zone.Actors;

public readonly record struct ZoneActorMovementDouble3(double X, double Y, double Z);
public readonly record struct ZoneActorMovementWorldOrigin(int X, int Y);

/// <summary>Scalar native correction arithmetic. These operations deliberately preserve native evaluation order.</summary>
internal static class ZoneActorMovementCorrectionMath
{
    internal static UnitWorldPosition Origin(int x, int y) => new(unchecked((long)x << 54), unchecked((long)y << 54), 0);
    internal static Vector3 Local(UnitWorldPosition value, UnitWorldPosition origin) =>
        new((float)unchecked(value.FixedX - origin.FixedX) * 5.684341886080802e-14f,
            (float)unchecked(value.FixedY - origin.FixedY) * 5.684341886080802e-14f, value.Z - origin.Z);
    internal static ZoneActorMovementDouble3 World(Vector3 value, UnitWorldPosition origin) =>
        World(new UnitWorldPosition(unchecked(NativeInt64(value.X * 17592186044416f) + origin.FixedX),
            unchecked(NativeInt64(value.Y * 17592186044416f) + origin.FixedY), value.Z + origin.Z));
    internal static ZoneActorMovementDouble3 World(UnitWorldPosition value) =>
        new(value.FixedX * 5.684341886080802e-14, value.FixedY * 5.684341886080802e-14, value.Z);
    private static long NativeInt64(float value) => value >= -9223372036854775808f && value < 9223372036854775808f ? (long)value : long.MinValue;
    internal static float Frame(float value)
    {
        var upper = ((value + .033f) - MathF.Abs(value - .033f)) * .5f;
        var absolute = MathF.Abs(upper - .001f);
        var sum = upper + .001f;
        // The native ADDSS keeps its first NaN operand. Preserve that order when the two
        // operands have different NaN signs (for example, an infinite input frame).
        return (Sse.IsSupported ? Sse.AddScalar(Vector128.CreateScalar(absolute), Vector128.CreateScalar(sum)).ToScalar()
            : float.IsNaN(absolute) ? absolute : absolute + sum) * .5f;
    }
    internal static float SmoothFactor(float elapsed, float smoothing, out float rate)
    {
        rate = 2f / smoothing;
        var x = rate * elapsed;
        return 1f / ((x * .48f * x + (x + 1f)) + x * .235f * x * x);
    }
    internal static Vector3 Smooth(Vector3 value, ref Vector3 velocity, float elapsed, Vector3 target, float smoothing)
    {
        if (smoothing > 0)
        {
            var factor = SmoothFactor(elapsed, smoothing, out var rate);
            var x = value.X - target.X; var y = value.Y - target.Y; var z = value.Z - target.Z;
            var dx = (velocity.X + x * rate) * elapsed;
            var dy = (velocity.Y + y * rate) * elapsed;
            var dz = (velocity.Z + z * rate) * elapsed;
            velocity = new((velocity.X - dx * rate) * factor, (velocity.Y - dy * rate) * factor, (velocity.Z - dz * rate) * factor);
            return new((dx + x) * factor + target.X, (dy + y) * factor + target.Y, (dz + z) * factor + target.Z);
        }
        if (elapsed > 0)
        {
            var inverse = 1f / elapsed;
            velocity = new((target.X - value.X) * inverse, (target.Y - value.Y) * inverse, (target.Z - value.Z) * inverse);
        }
        else velocity = new(velocity.X - velocity.X, velocity.Y - velocity.Y, velocity.Z - velocity.Z);
        return target;
    }
    internal static ZoneActorMovementDouble3 Smooth(ZoneActorMovementDouble3 value, ref ZoneActorMovementDouble3 velocity,
        float elapsed, ZoneActorMovementDouble3 target, float smoothing)
    {
        if (smoothing > 0)
        {
            var factor = (double)SmoothFactor(elapsed, smoothing, out var singleRate);
            var rate = (double)singleRate;
            var x = value.X - target.X; var y = value.Y - target.Y; var z = value.Z - target.Z;
            var dx = (velocity.X + rate * x) * elapsed;
            var dy = (velocity.Y + rate * y) * elapsed;
            var dz = (velocity.Z + rate * z) * elapsed;
            velocity = new(factor * (velocity.X - rate * dx), factor * (velocity.Y - rate * dy), factor * (velocity.Z - rate * dz));
            return new(factor * (dx + x) + target.X, factor * (dy + y) + target.Y, factor * (dz + z) + target.Z);
        }
        if (elapsed > 0)
        {
            var inverse = 1.0 / elapsed;
            velocity = new((target.X - value.X) * inverse, (target.Y - value.Y) * inverse, (target.Z - value.Z) * inverse);
        }
        else velocity = new(velocity.X - velocity.X, velocity.Y - velocity.Y, velocity.Z - velocity.Z);
        return target;
    }
    internal static bool Zero(Vector3 value) => MathF.Abs(value.X) <= 0 && MathF.Abs(value.Y) <= 0 && MathF.Abs(value.Z) <= 0;
    internal static bool Zero(ZoneActorMovementDouble3 value) => Math.Abs(value.X) <= 0 && Math.Abs(value.Y) <= 0 && Math.Abs(value.Z) <= 0;
    internal static float BoundsRadiusSquared(ZoneActorMovementCorrectionBounds bounds)
    {
        if (bounds.Minimum.X > bounds.Maximum.X) return 0;
        var x = bounds.Maximum.X - bounds.Minimum.X; var y = bounds.Maximum.Y - bounds.Minimum.Y; var z = bounds.Maximum.Z - bounds.Minimum.Z;
        return x * x * .25f + y * y * .25f + z * z * .25f;
    }
    internal static Vector3 Rotate(Quaternion q, Vector3 v)
    {
        var x = (v.Z * q.Y - v.Y * q.Z) + v.X * q.W;
        var y = (v.X * q.Z - q.X * v.Z) + q.W * v.Y;
        var z = (q.X * v.Y - v.X * q.Y) + q.W * v.Z;
        var rx = q.Y * z - q.Z * y; var ry = q.Z * x - q.X * z; var rz = q.X * y - q.Y * x;
        return new(rx + v.X + rx, ry + v.Y + ry, rz + v.Z + rz);
    }
    internal static Quaternion GroundRotation(Quaternion part, Quaternion entity) => new(
        (part.W * entity.X + part.X * entity.W) - part.Y * entity.Z + part.Z * entity.Y,
        (part.W * entity.Y + part.Y * entity.W) - part.Z * entity.X + part.X * entity.Z,
        (part.W * entity.Z + part.Z * entity.W) - part.X * entity.Y + part.Y * entity.X,
        -(part.Z * entity.Z + part.Y * entity.Y) - part.X * entity.X + part.W * entity.W);
}
