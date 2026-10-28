using System.Numerics;
using System.Runtime.InteropServices;
using static AAEmu.Zone.Native.Engine.ZoneNativePhysicsModule;

namespace AAEmu.Zone.Native.Physics;

/// <summary>
/// A copied original geom_contact. Normal retains the original query-first/body-second argument
/// convention; the diagnostic's common collider-outward sweep normal is its negative. Native
/// border/area pointers never escape the query.
/// </summary>
public readonly record struct ZoneNativePrimitiveContact(double Distance, Vector3 Position, Vector3 Normal,
    int PhysicalMaterialId, int EntityId, int PartId);

public enum ZoneNativePrimitiveQueryOutcome { Miss, Hit, Indeterminate }

/// <summary>
/// The original return and copied contact remain separate. A zero-distance sweep contact is
/// handled defensively from the recovered control flow; executed controls have not observed one.
/// An overlap returns a positive contact count rather than a sweep distance.
/// </summary>
public readonly record struct ZoneNativePrimitiveQueryResult(float NativeReturn, bool IsSweep,
    ZoneNativePrimitiveQueryOutcome Outcome, ZoneNativePrimitiveContact? Contact, string? Diagnostic);

public sealed unsafe partial class ZoneNativeTerrainPhysicsWorld
{
    /// <summary>
    /// Original synchronous IPhysicalWorld+208 box sweep or overlap against static player-collidable
    /// parts (required/any geometry flags2/2). A zero sweep selects overlap. Center is the primitive
    /// center in the engine-local window, independent of actor foot/stance offsets. Terrain and
    /// dynamic bodies are excluded. This diagnostic oracle does not replace the backend adapter.
    /// </summary>
    public ZoneNativePrimitiveQueryResult QueryStaticBox(Vector3 center, Vector3 halfExtents, Vector3 sweep)
    {
        using var operation = Enter();
        RequireTerrainEntityGrid();
        if (!Finite(halfExtents) || halfExtents.X <= 0 || halfExtents.Y <= 0 || halfExtents.Z <= 0)
            throw new ArgumentOutOfRangeException(nameof(halfExtents), "Box half extents must be finite and positive.");
        ValidatePrimitiveSegment(center, halfExtents, sweep);
        var primitive = new PrimitiveBox
        {
            M00 = 1, M11 = 1, M22 = 1, Center = center, HalfExtents = halfExtents
        };
        return QueryStaticPrimitive(0, &primitive, sweep);
    }

    /// <summary>
    /// Original capsule primitive: radius and half of its center segment are distinct. Axis is
    /// normalized into a private writable copy; zero segment half-height is the sphere limit.
    /// All input and native contact storage is scoped to this synchronous call.
    /// </summary>
    public ZoneNativePrimitiveQueryResult QueryStaticCapsule(Vector3 center, float radius,
        float segmentHalfHeight, Vector3 sweep, Vector3? axis = null)
    {
        using var operation = Enter();
        RequireTerrainEntityGrid();
        if (!float.IsFinite(radius) || radius <= 0)
            throw new ArgumentOutOfRangeException(nameof(radius), "Capsule radius must be finite and positive.");
        if (!float.IsFinite(segmentHalfHeight) || segmentHalfHeight < 0)
            throw new ArgumentOutOfRangeException(nameof(segmentHalfHeight), "Capsule segment half-height must be finite and nonnegative.");
        var direction = PrimitiveAxis(axis);
        var bounds = Vector3.Abs(direction) * segmentHalfHeight + new Vector3(radius);
        ValidatePrimitiveSegment(center, bounds, sweep);
        var primitive = new PrimitiveAxial
        {
            Center = center, Axis = direction, Radius = radius, HalfHeight = segmentHalfHeight
        };
        return QueryStaticPrimitive(6, &primitive, sweep);
    }

    /// <summary>
    /// Original flat-capped cylinder query (kind5), using the same32-byte axial primitive layout
    /// as capsules. Half-height excludes any hemispheres; radius and half-height must be positive.
    /// </summary>
    public ZoneNativePrimitiveQueryResult QueryStaticCylinder(Vector3 center, float radius,
        float halfHeight, Vector3 sweep, Vector3? axis = null)
    {
        using var operation = Enter();
        RequireTerrainEntityGrid();
        if (!float.IsFinite(radius) || radius <= 0)
            throw new ArgumentOutOfRangeException(nameof(radius), "Cylinder radius must be finite and positive.");
        if (!float.IsFinite(halfHeight) || halfHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(halfHeight), "Cylinder half-height must be finite and positive.");
        var direction = PrimitiveAxis(axis);
        // This conservative envelope is only a finite-input guard. Native kind5 constructs its
        // actual flat cylinder; no capsule or box substitute enters the intersection routine.
        ValidatePrimitiveSegment(center, Vector3.Abs(direction) * halfHeight + new Vector3(radius), sweep);
        var primitive = new PrimitiveAxial
        {
            Center = center, Axis = direction, Radius = radius, HalfHeight = halfHeight
        };
        return QueryStaticPrimitive(5, &primitive, sweep);
    }

    private static Vector3 PrimitiveAxis(Vector3? axis)
    {
        var direction = axis ?? Vector3.UnitZ;
        var squared = direction.LengthSquared();
        if (!Finite(direction) || !float.IsFinite(squared) || squared <= 0)
            throw new ArgumentOutOfRangeException(nameof(axis), "Primitive axis must be finite and nonzero.");
        return Vector3.Normalize(direction);
    }

    private ZoneNativePrimitiveQueryResult QueryStaticPrimitive(int kind, void* primitive, Vector3 sweep)
    {
        // ValidateFiles pins the complete binary identity. Also verify this newly exposed vtable
        // target before invocation; the public wrapper350964F0 and implementation35093100 agree
        // on the96-byte request. This entry has no explicit caller argument (TLS defaults to4).
        if (Marshal.ReadIntPtr(Marshal.ReadIntPtr(_world) + 0x208) != _module.Handle + 0x93100)
            throw new InvalidDataException("The native primitive query target does not match the verified 10.x profile.");
        PrimitiveContact* contacts = null;
        var request = new PrimitiveRequest
        {
            Kind = kind, Primitive = primitive, Sweep = sweep, ObjectTypes = 1,
            Contacts = &contacts, RequiredGeometryFlags = 2, AnyGeometryFlags = 2
        };
        var isSweep = sweep.LengthSquared() > 0;
        float returned;
        ZoneNativePrimitiveContact? copied = null;
        try
        {
            // Native temporarily zeros/restores the primitive center. Both primitive and request
            // are writable locals. No queue bit, params, skip pointers or callback metadata exist.
            returned = Virtual<PrimitiveQueryCall>(_world, 0x208)(_world, &request, 0);
            if (contacts != null && (isSweep || returned > 0))
            {
                var distance = contacts->Distance;
                // A nonzero sweep always publishes its per-caller closest-contact storage; an
                // unmodified1e10 sentinel means miss. Copy only real contact scalar fields before
                // CompleteCall or any subsequent native operation can reuse borrowed storage.
                if (double.IsFinite(distance) && Math.Abs(distance) < 1e10)
                    copied = new(distance, contacts->Position, contacts->Normal,
                        contacts->PhysicalMaterialId, contacts->EntityId, contacts->PartId);
            }
        }
        catch (Exception error) { NativeInvocationFailed(error); throw; }
        CompleteCall();
        if (!float.IsFinite(returned) || copied is { } invalid &&
            (!Finite(invalid.Position) || !Finite(invalid.Normal)))
            throw new InvalidDataException("The original primitive query returned non-finite contact data.");

        if (!isSweep)
        {
            if (returned > 0 && copied is not null)
                return new(returned, false, ZoneNativePrimitiveQueryOutcome.Hit, copied, null);
            if (returned == 0 && copied is null)
                return new(returned, false, ZoneNativePrimitiveQueryOutcome.Miss, null, null);
            return new(returned, false, ZoneNativePrimitiveQueryOutcome.Indeterminate, copied,
                "Native overlap return and contact availability do not match the recovered default profile.");
        }
        if (copied is null)
            return new(returned, true, returned == 0 ? ZoneNativePrimitiveQueryOutcome.Miss : ZoneNativePrimitiveQueryOutcome.Indeterminate,
                null, returned == 0 ? null : "Native sweep returned a value without a copied bounded contact.");
        var contact = copied.Value;
        var length = sweep.Length();
        if (contact.Distance < 0 || contact.Distance > length + Math.Max(.0001f, length * .00001f) ||
            returned != (float)contact.Distance)
            return new(returned, true, ZoneNativePrimitiveQueryOutcome.Indeterminate, copied,
                "Native sweep contact/return is outside the recovered bounded-distance profile.");
        return new(returned, true, ZoneNativePrimitiveQueryOutcome.Hit, copied, null);
    }

    private static void ValidatePrimitiveSegment(Vector3 center, Vector3 bounds, Vector3 sweep)
    {
        var squared = sweep.LengthSquared();
        if (!Finite(center) || !Finite(bounds) || !Finite(sweep) || !float.IsFinite(squared) ||
            !Finite(center + sweep) || !Finite(center - bounds) || !Finite(center + bounds) ||
            !Finite(center + sweep - bounds) || !Finite(center + sweep + bounds) ||
            squared == 0 && sweep != Vector3.Zero)
            throw new ArgumentOutOfRangeException(nameof(sweep), "Primitive queries require finite bounded centers, sizes and sweeps.");
    }

    [StructLayout(LayoutKind.Explicit, Size = 96)]
    private struct PrimitiveRequest
    {
        [FieldOffset(24)] internal int Kind;
        [FieldOffset(32)] internal void* Primitive;
        [FieldOffset(40)] internal Vector3 Sweep;
        [FieldOffset(52)] internal uint ObjectTypes;
        [FieldOffset(56)] internal PrimitiveContact** Contacts;
        [FieldOffset(64)] internal uint RequiredGeometryFlags;
        [FieldOffset(68)] internal uint AnyGeometryFlags;
        [FieldOffset(72)] internal nint IntersectionParameters;
        [FieldOffset(80)] internal int SkipCount;
        [FieldOffset(88)] internal nint SkipEntities;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct PrimitiveBox
    {
        [FieldOffset(0)] internal float M00;
        [FieldOffset(16)] internal float M11;
        [FieldOffset(32)] internal float M22;
        [FieldOffset(36)] internal int Oriented;
        [FieldOffset(40)] internal Vector3 Center;
        [FieldOffset(52)] internal Vector3 HalfExtents;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct PrimitiveAxial
    {
        [FieldOffset(0)] internal Vector3 Center;
        [FieldOffset(12)] internal Vector3 Axis;
        [FieldOffset(24)] internal float Radius;
        [FieldOffset(28)] internal float HalfHeight;
    }

    [StructLayout(LayoutKind.Explicit, Size = 136)]
    private struct PrimitiveContact
    {
        [FieldOffset(0)] internal double Distance;
        [FieldOffset(8)] internal Vector3 Position;
        [FieldOffset(20)] internal Vector3 Normal;
        [FieldOffset(56)] internal int PhysicalMaterialId;
        [FieldOffset(60)] internal int EntityId;
        [FieldOffset(64)] internal int PartId;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate float PrimitiveQueryCall(nint world, PrimitiveRequest* request, nint profilingKey);
}
