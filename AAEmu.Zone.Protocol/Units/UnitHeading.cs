using System.Numerics;

namespace AAEmu.Zone.Protocol.Units;

/// <summary>Native compressed axis-angle vector; housing instead carries full yaw radians.</summary>
public sealed record UnitHeading(sbyte AxisX = 0, sbyte AxisY = 0, sbyte AxisZ = 0, float? HouseYaw = null)
{
    public Quaternion ToQuaternion()
    {
        if (HouseYaw is { } yaw) return FromYaw(yaw);
        // 3937F4A0: preserve the f32 normalization and f64 angle conversion boundaries.
        var x = AxisX / 127f; var y = AxisY / 127f; var z = AxisZ / 127f;
        var lengthSquared = x * x + y * y + z * z;
        if (lengthSquared < 0.00006200012285262346f || lengthSquared > 0.9999380111694336f)
            return Quaternion.Identity;
        var length = MathF.Sqrt(lengthSquared);
        var inverse = 1f / length;
        var halfAngle = (float)((double)length * (Math.PI * 2)) * .5f;
        var sine = MathF.Sin(halfAngle);
        return new(x * inverse * sine, y * inverse * sine, z * inverse * sine, MathF.Cos(halfAngle));
    }

    public static Quaternion FromYaw(float yaw) => new(0, 0, MathF.Sin(yaw * .5f), MathF.Cos(yaw * .5f));

    public static UnitHeading FromQuaternion(Quaternion rotation)
    {
        if (!float.IsFinite(rotation.X) || !float.IsFinite(rotation.Y) || !float.IsFinite(rotation.Z) || !float.IsFinite(rotation.W))
            throw new InvalidDataException("Unit heading quaternion must be finite.");
        // 393753C0 uses an arithmetic clamp of W to [-1,1]. Its float rounding matters.
        var w = ((rotation.W + 1f) - MathF.Abs(rotation.W - 1f)) * .5f;
        w = (MathF.Abs(w + 1f) + (w - 1f)) * .5f;
        var angle = MathF.Acos(w) * 2f;
        if (angle <= .0001f) return new();
        var inverseSine = 1f / MathF.Sin(angle * .5f);
        var x = inverseSine * rotation.X; var y = inverseSine * rotation.Y; var z = inverseSine * rotation.Z;
        var lengthSquared = x * x + y * y + z * z;
        if (!float.IsFinite(lengthSquared)) throw new InvalidDataException("Unit heading axis exceeds the native float range.");
        if (lengthSquared > 0.00000011920928955078125f)
        {
            var inverseLength = 1f / MathF.Sqrt(lengthSquared);
            x *= inverseLength; y *= inverseLength; z *= inverseLength;
        }
        else
        {
            x = 1f; y = z = 0f; // native fallback axis at 3A4DC160
        }
        var turns = (float)((double)angle / (Math.PI * 2));
        return new(Quantize(x * turns), Quantize(y * turns), Quantize(z * turns));
    }

    private static sbyte Quantize(float value) => checked((sbyte)(int)MathF.Floor(127f * value + .5f));
}
