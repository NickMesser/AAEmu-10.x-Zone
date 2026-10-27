using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace AAEmu.Zone.Simulation;

/// <summary>Scalar rotation operations used by native 39245A30; they preserve the native evaluation order.</summary>
public static class ZoneActorMovementInputMath
{
    // 392535E0 accepts the caller's direction, including a non-normalized model direction.
    public static Quaternion DirectionRotation(Vector3 direction)
    {
        var result = new Quaternion((float)((double)(direction.Z + direction.Z) * .3535533845424652),
            0, 0, .70710677f);
        var horizontal = (double)MathF.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
        if (horizontal > .00001)
        {
            var x = (double)direction.X / horizontal;
            var y = (double)direction.Y / horizontal + 1.0;
            var z = horizontal + 1.0;
            var xyLength = Math.Sqrt(y * y + x * x);
            var zLength = Math.Sqrt((double)(direction.Z * direction.Z) + z * z);
            var cosine = 0.0;
            var sine = -1.0;
            if (xyLength > .00001) { cosine = y / xyLength; sine = -(x / xyLength); }
            result = new((float)(((double)direction.Z / zLength) * cosine),
                (float)(((double)direction.Z / zLength) * sine),
                (float)((z / zLength) * sine), (float)((z / zLength) * cosine));
        }
        return result;
    }

    // The native product uses scalar sums; System.Numerics SIMD changes rounding and signed zero.
    public static Quaternion Multiply(Quaternion first, Quaternion second) => new(
        Add(Subtract(Add(MultiplyScalar(first.W, second.X), MultiplyScalar(first.X, second.W)), MultiplyScalar(first.Z, second.Y)), MultiplyScalar(first.Y, second.Z)),
        Add(Subtract(Add(MultiplyScalar(first.W, second.Y), MultiplyScalar(first.Y, second.W)), MultiplyScalar(first.X, second.Z)), MultiplyScalar(first.Z, second.X)),
        Add(Subtract(Add(MultiplyScalar(first.W, second.Z), MultiplyScalar(first.Z, second.W)), MultiplyScalar(first.Y, second.X)), MultiplyScalar(first.X, second.Y)),
        Add(Subtract(-Add(MultiplyScalar(first.Y, second.Y), MultiplyScalar(first.Z, second.Z)), MultiplyScalar(first.X, second.X)), MultiplyScalar(first.W, second.W)));

    // 3924607D passes DAT_39F96420 (.2f) to 3916CE10 / 390B3AE0.
    public static Quaternion InterpolateRotation(Quaternion reference, Quaternion value)
    {
        if (reference.X * value.X + reference.Y * value.Y + reference.Z * value.Z + value.W * reference.W < 0)
            value = new(-value.X, -value.Y, -value.Z, -value.W);
        var x = Add(reference.X, MultiplyScalar(Subtract(value.X, reference.X), .2f));
        var y = Add(reference.Y, MultiplyScalar(Subtract(value.Y, reference.Y), .2f));
        var z = Add(reference.Z, MultiplyScalar(Subtract(value.Z, reference.Z), .2f));
        var w = Add(MultiplyScalar(Subtract(value.W, reference.W), .2f), reference.W);
        var sum = Add(Add(Add(MultiplyScalar(x, x), MultiplyScalar(w, w)), MultiplyScalar(y, y)), MultiplyScalar(z, z));
        var inverse = 1f / MathF.Sqrt(sum);
        return new(MultiplyScalar(x, inverse), MultiplyScalar(y, inverse), MultiplyScalar(z, inverse), MultiplyScalar(w, inverse));
    }

    // 39253770 retains its ordered clamp and pole branch, including their NaN behavior.
    public static Vector3 RotationAngles(Quaternion rotation)
    {
        var value = (rotation.X * rotation.Z - rotation.Y * rotation.W) * -2f;
        if (!(1f > value)) value = 1f;
        if (!(value > -1f)) value = -1f;
        var clamped = ((value + 1f) - MathF.Abs(value - 1f)) * .5f;
        var y = NativeAsin((MathF.Abs(clamped - -1f) + (clamped - 1f)) * .5f);
        float x, numerator, subtrahend;
        if (.01f <= MathF.Abs(MathF.Abs(y) - 1.5707964f))
        {
            x = MathF.Atan2((rotation.Y * rotation.Z + rotation.X * rotation.W) * 2f,
                1f - (rotation.Y * rotation.Y + rotation.X * rotation.X) * 2f);
            subtrahend = (rotation.Y * rotation.Y + rotation.Z * rotation.Z) * 2f;
            numerator = (rotation.Y * rotation.X + rotation.Z * rotation.W) * 2f;
        }
        else
        {
            x = 0;
            subtrahend = (rotation.Z * rotation.Z + rotation.X * rotation.X) * 2f;
            numerator = (rotation.X * rotation.Y - rotation.Z * rotation.W) * -2f;
        }
        return new(x, y, MathF.Atan2(numerator, 1f - subtrahend));
    }

    public static Vector3 TargetDirection(Vector3 position, Vector3 target)
    {
        var x = target.X - position.X;
        var y = target.Y - position.Y;
        var z = target.Z - position.Z;
        var lengthSquared = x * x + y * y + z * z;
        if (!(lengthSquared > 1.1920929e-7f)) return Vector3.UnitX; // DAT_3A4DC160; COMISS/JBE includes NaN.
        var inverse = 1f / MathF.Sqrt(lengthSquared);
        return new(inverse * x, inverse * y, inverse * z);
    }

    // Scalar C# additions/products may be commuted by the JIT, which changes which NaN payload
    // survives. Keep the original SSE destination/source order for the quaternion operations.
    private static float Add(float first, float second) => Sse.IsSupported
        ? Sse.AddScalar(Vector128.CreateScalar(first), Vector128.CreateScalar(second)).ToScalar()
        : NativeNaN(first, second, first + second);
    private static float Subtract(float first, float second) => Sse.IsSupported
        ? Sse.SubtractScalar(Vector128.CreateScalar(first), Vector128.CreateScalar(second)).ToScalar()
        : NativeNaN(first, second, first - second);
    private static float MultiplyScalar(float first, float second) => Sse.IsSupported
        ? Sse.MultiplyScalar(Vector128.CreateScalar(first), Vector128.CreateScalar(second)).ToScalar()
        : NativeNaN(first, second, first * second);
    private static float NativeNaN(float first, float second, float result) =>
        float.IsNaN(first) ? BitConverter.UInt32BitsToSingle(BitConverter.SingleToUInt32Bits(first) | 0x00400000) :
        float.IsNaN(second) ? BitConverter.UInt32BitsToSingle(BitConverter.SingleToUInt32Bits(second) | 0x00400000) :
        float.IsNaN(result) ? BitConverter.UInt32BitsToSingle(0xFFC00000) : result;

    // MSVCR100 asinf, RVA 82D6C. Its float rational polynomial differs from MathF.Asin
    // by an ULP for some valid inputs. This reproduces returned values; CRT errno,
    // floating-point exception flags and matherr callbacks are not part of this primitive.
    private static float NativeAsin(float value)
    {
        var bits = BitConverter.SingleToUInt32Bits(value);
        var magnitude = bits & 0x7FFFFFFFu;
        var sign = bits & 0x80000000u;
        if (magnitude > 0x7F800000u) return BitConverter.UInt32BitsToSingle(bits | 0x00400000u);
        if (magnitude < 0x38800000u) return value;
        if (magnitude >= 0x3F800000u)
            return BitConverter.UInt32BitsToSingle(magnitude == 0x3F800000u ? sign | 0x3FC90FDBu : 0xFFC00000u);
        var x = BitConverter.UInt32BitsToSingle(magnitude);
        var transformed = magnitude >= 0x3F000000u;
        float square;
        if (transformed) { square = (1f - x) * .5f; x = MathF.Sqrt(square); }
        else square = x * x;
        var numerator = -0.013381929136812687f - square * 0.003961374517530203f;
        numerator *= square;
        numerator -= 0.05652986839413643f;
        numerator *= square;
        numerator += 0.18416160345077515f;
        numerator *= square;
        var denominator = 1.1049696207046509f - square * 0.8364112973213196f;
        var ratio = numerator / denominator;
        float result;
        if (transformed)
        {
            var truncatedRoot = BitConverter.UInt32BitsToSingle(BitConverter.SingleToUInt32Bits(x) & 0xFFFF0000u);
            var correction = (square - truncatedRoot * truncatedRoot) / (truncatedRoot + x);
            var remainder = (x * 2f) * ratio;
            remainder -= 7.549789415861596e-08f - correction * 2f;
            remainder -= 0.7853981256484985f - truncatedRoot * 2f;
            result = 0.7853981256484985f - remainder;
        }
        else result = ratio * x + x;
        return BitConverter.UInt32BitsToSingle(BitConverter.SingleToUInt32Bits(result) ^ sign);
    }
}
