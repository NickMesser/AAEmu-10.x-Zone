using System.Numerics;
using AAEmu.Zone.Physics;

namespace AAEmu.Zone.Scene;

/// <summary>Copied source3015C1E0 exclusion data: an OBB and its world position, not a physical proxy.</summary>
public sealed record ZoneManagedVegetationExclusion(int SourceId, Matrix4x4 Basis, Vector3 HalfExtents,
    Vector3 LocalCenter, Vector3 WorldPosition);

/// <summary>
/// Immutable snapshot for302221D0's final301597D0 gate. The source engine starts with an empty table;
/// later exclusion insertion/removal and destruction of already published objects need their own owner.
/// This object represents only the table at this cell-load boundary, with the actual current engine origin.
/// </summary>
public sealed class ZoneManagedVegetationExclusions
{
    private readonly ZoneManagedVegetationExclusion[] _entries;
    public Vector3 EngineOrigin { get; }
    public IReadOnlyList<ZoneManagedVegetationExclusion> Entries { get; }
    /// <summary>
    /// Dedicated nonstreaming365704D0 sets Seamless160/164 from the resolved Zone origin before cell loading;
    /// v32/3653EF00 converts those signed shifted cells to float meters. Runtime mutation/streaming is not admitted.
    /// </summary>
    public static ZoneManagedVegetationExclusions FreshStaticScene(ZoneWorldMapLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return new(new Vector3(unchecked(layout.Origin.X << 10), unchecked(layout.Origin.Y << 10), 0), []);
    }
    public ZoneManagedVegetationExclusions(Vector3 engineOrigin, IEnumerable<ZoneManagedVegetationExclusion> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (!Finite(engineOrigin)) throw new ArgumentException("Vegetation exclusion engine origin must be finite.");
        EngineOrigin = engineOrigin; _entries = entries.ToArray(); Entries = Array.AsReadOnly(_entries);
        foreach (var entry in _entries) Validate(entry);
    }
    public bool Excludes(ZonePhysicsQueryBounds authoredBounds)
    {
        if (!Finite(authoredBounds.Minimum) || !Finite(authoredBounds.Maximum) ||
            Vector3.Min(authoredBounds.Minimum, authoredBounds.Maximum) != authoredBounds.Minimum)
            throw new ArgumentException("Vegetation requires finite ordered authored exclusion bounds.");
        foreach (var entry in _entries)
            if (Intersects(authoredBounds, entry, EngineOrigin)) return true;
        return false;
    }
    //30164440: AABB/OBB SAT. Equality intersects; no epsilon changes the native exclusion boundary.
    private static bool Intersects(ZonePhysicsQueryBounds a, ZoneManagedVegetationExclusion b, Vector3 origin)
    {
        var m = b.Basis;
        var x = new Vector3(m.M11, m.M12, m.M13); var y = new Vector3(m.M21, m.M22, m.M23);
        var z = new Vector3(m.M31, m.M32, m.M33);
        var h = b.HalfExtents; var c = b.LocalCenter; var p = b.WorldPosition - origin;
        var bc = new Vector3((m.M11 * c.X + m.M21 * c.Y) + m.M31 * c.Z,
            (m.M12 * c.X + m.M22 * c.Y) + m.M32 * c.Z,
            (m.M13 * c.X + m.M23 * c.Y) + m.M33 * c.Z) + p;
        var ah = (a.Maximum - a.Minimum) * .5f; var ac = (a.Maximum + a.Minimum) * .5f;
        var d = bc - ac;
        if (!Finite(bc) || !Finite(ah) || !Finite(ac) || !Finite(d))
            throw new NotSupportedException("Vegetation exclusion coordinates exceed the finite source-math profile.");
        if (MathF.Abs(d.X) > ((MathF.Abs(x.X * h.X) + MathF.Abs(y.X * h.Y)) + MathF.Abs(z.X * h.Z)) + MathF.Abs(ah.X) ||
            MathF.Abs(d.Y) > ((MathF.Abs(x.Y * h.X) + MathF.Abs(y.Y * h.Y)) + MathF.Abs(z.Y * h.Z)) + MathF.Abs(ah.Y) ||
            MathF.Abs(d.Z) > ((MathF.Abs(x.Z * h.X) + MathF.Abs(y.Z * h.Y)) + MathF.Abs(z.Z * h.Z)) + MathF.Abs(ah.Z)) return false;
        if (MathF.Abs((x.Y * d.Y + x.X * d.X) + x.Z * d.Z) >
                ((MathF.Abs(x.Y * ah.Y) + MathF.Abs(x.X * ah.X)) + MathF.Abs(x.Z * ah.Z)) + h.X ||
            MathF.Abs((y.Y * d.Y + y.X * d.X) + y.Z * d.Z) >
                ((MathF.Abs(y.Y * ah.Y) + MathF.Abs(y.X * ah.X)) + MathF.Abs(y.Z * ah.Z)) + h.Y ||
            MathF.Abs((z.Y * d.Y + z.X * d.X) + z.Z * d.Z) >
                ((MathF.Abs(z.Y * ah.Y) + MathF.Abs(z.X * ah.X)) + MathF.Abs(z.Z * ah.Z)) + h.Z) return false;
        //The nine cross axes retain30164440's operand grouping.
        if (MathF.Abs(x.Y * d.Z - x.Z * d.Y) >
                (MathF.Abs(y.X) * h.Z + MathF.Abs(z.X) * h.Y) + (MathF.Abs(x.Z) * ah.Y + MathF.Abs(x.Y) * ah.Z) ||
            MathF.Abs(y.Y * d.Z - y.Z * d.Y) >
                (MathF.Abs(x.X) * h.Z + MathF.Abs(z.X) * h.X) + (MathF.Abs(y.Z) * ah.Y + MathF.Abs(y.Y) * ah.Z) ||
            MathF.Abs(z.Y * d.Z - z.Z * d.Y) >
                (MathF.Abs(x.X) * h.Y + MathF.Abs(y.X) * h.X) + (MathF.Abs(z.Z) * ah.Y + MathF.Abs(z.Y) * ah.Z) ||
            MathF.Abs(x.Z * d.X - x.X * d.Z) >
                (MathF.Abs(y.Y) * h.Z + MathF.Abs(z.Y) * h.Y) + (MathF.Abs(x.X) * ah.Z + MathF.Abs(x.Z) * ah.X) ||
            MathF.Abs(y.Z * d.X - y.X * d.Z) >
                (MathF.Abs(z.Y) * h.X + MathF.Abs(x.Y) * h.Z) + (MathF.Abs(y.Z) * ah.X + MathF.Abs(y.X) * ah.Z) ||
            MathF.Abs(z.Z * d.X - z.X * d.Z) >
                (MathF.Abs(y.Y) * h.X + MathF.Abs(x.Y) * h.Y) + (MathF.Abs(z.Z) * ah.X + MathF.Abs(z.X) * ah.Z) ||
            MathF.Abs(x.X * d.Y - x.Y * d.X) >
                (MathF.Abs(y.Z) * h.Z + MathF.Abs(z.Z) * h.Y) + (MathF.Abs(x.X) * ah.Y + MathF.Abs(x.Y) * ah.X) ||
            MathF.Abs(y.X * d.Y - y.Y * d.X) >
                (MathF.Abs(z.Z) * h.X + MathF.Abs(x.Z) * h.Z) + (MathF.Abs(y.Y) * ah.X + MathF.Abs(y.X) * ah.Y)) return false;
        return MathF.Abs(z.X * d.Y - z.Y * d.X) <=
            (MathF.Abs(y.Z) * h.X + MathF.Abs(x.Z) * h.Y) + (MathF.Abs(z.Y) * ah.X + MathF.Abs(z.X) * ah.Y);
    }
    private static void Validate(ZoneManagedVegetationExclusion b)
    {
        ArgumentNullException.ThrowIfNull(b);
        var m = b.Basis; var x = new Vector3(m.M11, m.M12, m.M13);
        var y = new Vector3(m.M21, m.M22, m.M23); var z = new Vector3(m.M31, m.M32, m.M33);
        if (!Finite(x) || !Finite(y) || !Finite(z) || !Finite(b.HalfExtents) || !Finite(b.LocalCenter) || !Finite(b.WorldPosition) ||
            b.HalfExtents.X < 0 || b.HalfExtents.Y < 0 || b.HalfExtents.Z < 0 ||
            MathF.Abs(x.LengthSquared() - 1) > 1e-4f || MathF.Abs(y.LengthSquared() - 1) > 1e-4f || MathF.Abs(z.LengthSquared() - 1) > 1e-4f ||
            MathF.Abs(Vector3.Dot(x, y)) > 1e-4f || MathF.Abs(Vector3.Dot(x, z)) > 1e-4f || MathF.Abs(Vector3.Dot(y, z)) > 1e-4f ||
            m.M14 != 0 || m.M24 != 0 || m.M34 != 0 || m.M41 != 0 || m.M42 != 0 || m.M43 != 0 || m.M44 != 1)
            throw new NotSupportedException("Vegetation exclusions require finite source orthonormal OBB data; affine substitutes are unsupported.");
    }
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
