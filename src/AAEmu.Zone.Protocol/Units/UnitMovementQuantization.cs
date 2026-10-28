using System.Numerics;
using AAEmu.Zone.Protocol.Messages;

namespace AAEmu.Zone.Protocol.Units;

/// <summary>Native actor movement serializer3938B0B0's scalar encoders, before wire writing.</summary>
public static class UnitMovementQuantization
{
    /// <summary>39B11410: absolute fixed XY magnitudes and clamped native22-bit height.</summary>
    public static UnitPosition Position(UnitWorldPosition value)
    {
        if (!float.IsFinite(value.Z)) throw new InvalidDataException("Unit height must be finite.");
        static uint Magnitude(long component)
        {
            var sign = component >> 63;
            return unchecked((uint)((sign ^ (sign + component)) >> 35));
        }
        var z = (uint)(int)MathF.Floor(((Math.Clamp(value.Z, -100f, 4096f) + 100f) / 4196f) * 4194304f + .5f);
        //The source writer retains only22 bits, including at the exact upper boundary.
        return new(Magnitude(value.FixedX), Magnitude(value.FixedY), z & 0x3fffff, value.FixedX < 0, value.FixedY < 0);
    }

    /// <summary>39370D80: divide first, clamp, then floorf(value*32767+.5). The negative limit is -32767.</summary>
    public static MovementVector16 ActorVelocity(Vector3 value)
    {
        RequireFinite(value);
        return new(Velocity(value.X), Velocity(value.Y), Velocity(value.Z));
    }

    /// <summary>39370E80: signed normalized actor request, with ties toward positive infinity.</summary>
    public static MovementVector8 ActorDelta(Vector3 value)
    {
        RequireFinite(value);
        return new(Delta(value.X), Delta(value.Y), Delta(value.Z));
    }

    /// <summary>3938B0B0's optional actor-flag128 fall speed.</summary>
    public static ushort ActorFallVelocity(float value)
    {
        if (!float.IsFinite(value)) throw new InvalidDataException("Actor fall velocity must be finite.");
        return (ushort)(int)MathF.Floor(Math.Clamp(value / 128f, 0f, 1f) * 65535f + .5f);
    }

    private static short Velocity(float value) => (short)(int)MathF.Floor(Math.Clamp(value / 60f, -1f, 1f) * 32767f + .5f);
    private static sbyte Delta(float value) => (sbyte)(int)MathF.Floor(Math.Clamp(value, -1f, 1f) * 127f + .5f);
    private static void RequireFinite(Vector3 value)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            throw new InvalidDataException("Actor movement quantization requires a finite vector.");
    }
}
