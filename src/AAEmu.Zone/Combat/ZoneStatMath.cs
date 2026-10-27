namespace AAEmu.Zone.Combat;

/// <summary>x64 CVTTSS2SI/CVTTSD2SI conversion, including the indefinite value on overflow or NaN.</summary>
internal static class ZoneStatMath
{
    public static int ToInt32(float value) => !float.IsFinite(value) || value >= 2147483648f || value < -2147483648f
        ? int.MinValue : (int)value;

    public static long ToInt64(double value) => !double.IsFinite(value) || value >= 9223372036854775808d || value < -9223372036854775808d
        ? long.MinValue : (long)value;
}
