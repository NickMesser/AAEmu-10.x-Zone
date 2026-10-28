using System.Numerics;
using System.Globalization;
using AAEmu.Zone.Combat;

namespace AAEmu.Zone.Scene;

/// <summary>Copied entity-transform math for script GetDirectionVector, not a normalized facing vector.</summary>
public static class ZoneManagedEntityOrientation
{
    /// <summary>
    ///3203CB90 accepts a numeric argument through a native float before truncating to int.
    ///Absent/nonnumeric arguments keep axis1. Out-of-range integer axes also selectY in the
    ///direction getter. Numeric strings are not accepted as numbers by this binding.
    /// </summary>
    public static int AxisFromScriptArgument(object? value) => value switch
    {
        sbyte or byte or short or ushort or int or uint or long or ulong => ZoneStatMath.ToInt32((float)Convert.ToDouble(value, CultureInfo.InvariantCulture)),
        float number => ZoneStatMath.ToInt32(number),
        double number => ZoneStatMath.ToInt32((float)number),
        _ => 1,
    };

    /// <summary>
    /// Parentless entity with the actual uniform scale.32012F50→32016930 expands the stored
    ///quaternion without normalization, multiplies the scale, and32013100 caches that matrix.
    ///Keep the original double intermediates after the rounded float doubling operations.
    ///Attached/parented entities must instead supply their actual composed world matrix below.
    /// </summary>
    public static Vector3 GetDirectionVector(Quaternion rotation, float uniformScale, int axis = 1)
    {
        if (!Finite(rotation.X) || !Finite(rotation.Y) || !Finite(rotation.Z) || !Finite(rotation.W) || !Finite(uniformScale))
            throw new ArgumentException("Entity orientation and scale must be finite.");
        var dx = (double)(rotation.X * 2f); var dy = (double)(rotation.Y * 2f); var dz = (double)(rotation.Z * 2f);
        var yy = rotation.Y * dy; var yz = rotation.Y * dz; var xy = rotation.X * dy;
        var complementX = 1d - rotation.X * dx;
        var xz = rotation.X * dz; var wx = rotation.W * dx; var wy = rotation.W * dy;
        var zz = rotation.Z * dz; var wz = rotation.W * dz;
        // System.Numerics uses row vectors; each row here is a Cry Matrix34 column.
        var first = ScaleColumn(new((float)((1d - yy) - zz), (float)(wz + xy), (float)(xz - wy)), uniformScale);
        var second = ScaleColumn(new((float)(xy - wz), (float)(complementX - zz), (float)(yz + wx)), uniformScale);
        var third = ScaleColumn(new((float)(xz + wy), (float)(yz - wx), (float)(complementX - yy)), uniformScale);
        var world = new Matrix4x4(
            first.X, first.Y, first.Z, 0,
            second.X, second.Y, second.Z, 0,
            third.X, third.Y, third.Z, 0,
            0, 0, 0, 1);
        return GetDirectionVector(world, axis);
    }

    /// <summary>
    ///320CD998+C0 resolves3200DCD0, copying the cached world Matrix34.3203CB90 returns its
    ///selected column:0=X,2=Z,otherwiseY. Preserve scale, mirroring and parent shear; ignore
    ///translation. This overload consumes a System.Numerics row-vector world transform.
    /// </summary>
    public static Vector3 GetDirectionVector(Matrix4x4 world, int axis = 1)
    {
        if (!Finite(world.M11) || !Finite(world.M12) || !Finite(world.M13) ||
            !Finite(world.M21) || !Finite(world.M22) || !Finite(world.M23) ||
            !Finite(world.M31) || !Finite(world.M32) || !Finite(world.M33))
            throw new ArgumentException("Entity world-transform basis must be finite.");
        var x = axis == 0 ? 1f : 0f; var z = axis == 2 ? 1f : 0f;
        var y = axis is 0 or 2 ? 0f : 1f;
        // Preserve the getter's float dot-product order rather than normalizing/extracting a row.
        return new((y * world.M21 + x * world.M11) + z * world.M31,
            (x * world.M12 + y * world.M22) + z * world.M32,
            (x * world.M13 + y * world.M23) + z * world.M33);
    }

    private static bool Finite(float value) => float.IsFinite(value);
    //320155D0 multiplies the complete diagonal scale matrix, including its zero terms.
    //Those additions preserve the cached matrix's signed-zero behavior for mirrored scales.
    private static Vector3 ScaleColumn(Vector3 column, float scale) => new(
        (column.X * scale + column.Y * 0f) + column.Z * 0f,
        (column.X * 0f + column.Y * scale) + column.Z * 0f,
        (column.X * 0f + column.Y * 0f) + column.Z * scale);
}
