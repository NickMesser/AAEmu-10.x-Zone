using System.Numerics;
using System.Runtime.InteropServices;
using static AAEmu.Zone.Native.Engine.ZoneNativePhysicsModule;

namespace AAEmu.Zone.Native.Physics;

/// <summary>Copied native scene-ray fields. Part identities belong to the hit geometry, not a Zone object.</summary>
public readonly record struct ZoneNativeSceneRayHit(float Distance, Vector3 Position, Vector3 Normal,
    ushort PhysicalMaterialId, ushort LocalSurfaceIndex, int PartIndex, int PartId);

public sealed unsafe partial class ZoneNativeTerrainPhysicsWorld
{
    /// <summary>
    /// Intersects a bounded segment with the world's actual static collision geometry. Displacement
    /// includes the segment length. Native ray-collidable part filtering is retained; all material
    /// pierceability values stop the ray, and the nearest blocking hit is returned. Terrain and
    /// dynamic bodies are excluded. ConfigureTerrainEntityGrid must have completed; the grid may
    /// remain usable after its heightfield replicas are cleared.
    /// </summary>
    /// <remarks>
    /// Calls original IPhysicalWorld+150 synchronously on caller slot 4 under the same owner-thread
    /// and engine gate as creation and retirement. The request and hit storage live only through
    /// this call: queued queries, user callbacks, hit caches and skip pointers are never supplied.
    /// The result contains no borrowed collider pointer and remains usable after scene retirement.
    /// </remarks>
    public bool RaycastStaticScene(Vector3 origin, Vector3 displacement, out ZoneNativeSceneRayHit hit)
    {
        using var operation = Enter();
        RequireTerrainEntityGrid();
        var lengthSquared = displacement.LengthSquared();
        if (!Finite(origin) || !Finite(displacement) || !float.IsFinite(lengthSquared) ||
            !Finite(origin + displacement))
            throw new ArgumentOutOfRangeException(nameof(displacement),
                "Native scene rays require finite bounded segments.");
        hit = default;
        if (lengthSquared == 0) return false;

        var nativeHit = new SceneRayHit { Distance = -1 };
        var request = new SceneRayRequest
        {
            Origin = origin, Displacement = displacement,
            ObjectTypes = 1, Flags = 0x0f, Hits = &nativeHit, HitCount = 1
        };
        int count;
        try
        {
            // 351AA730: RCX=world, RDX=request, R8=profiling identity, R9D=caller slot.
            // Identity zero is a valid numeric profiling key (35081EF0), not a string pointer.
            count = Virtual<SceneRayCall>(_world, 0x150)(_world, &request, 0, 4);
        }
        catch (Exception error) { NativeInvocationFailed(error); throw; }
        CompleteCall();
        if (count <= 0 || nativeHit.Collider == 0) return false;
        hit = new(nativeHit.Distance, nativeHit.Position, nativeHit.Normal,
            nativeHit.PhysicalMaterial, nativeHit.SurfaceIndex, nativeHit.PartIndex, nativeHit.PartId);
        return true;
    }

    // 351AA730 reads only the fields below on the synchronous path. The zeroed prefix is reserved
    // for queued event metadata. Queue flag 0x800 is deliberately absent and count is exactly one.
    [StructLayout(LayoutKind.Explicit, Size = 0x60)]
    private struct SceneRayRequest
    {
        [FieldOffset(0x18)] internal Vector3 Origin;
        [FieldOffset(0x24)] internal Vector3 Displacement;
        [FieldOffset(0x30)] internal uint ObjectTypes;
        [FieldOffset(0x34)] internal uint Flags;
        [FieldOffset(0x38)] internal SceneRayHit* Hits;
        [FieldOffset(0x40)] internal int HitCount;
        [FieldOffset(0x48)] internal nint CachedHit;
        [FieldOffset(0x50)] internal int SkipCount;
        [FieldOffset(0x58)] internal nint SkipEntities;
    }

    // 351ABC10 writes geometry hits; 351AA730 converts the internal primitive index at +14 to
    // the authored part ID before returning. The collider is inspected only while the gate is held.
    [StructLayout(LayoutKind.Explicit, Size = 0x50)]
    private struct SceneRayHit
    {
        [FieldOffset(0)] internal float Distance;
        [FieldOffset(8)] internal nint Collider;
        [FieldOffset(0x10)] internal int PartIndex;
        [FieldOffset(0x14)] internal int PartId;
        [FieldOffset(0x18)] internal ushort PhysicalMaterial;
        [FieldOffset(0x1a)] internal ushort SurfaceIndex;
        [FieldOffset(0x24)] internal Vector3 Position;
        [FieldOffset(0x30)] internal Vector3 Normal;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int SceneRayCall(nint world, SceneRayRequest* request, nint profilingIdentity, int caller);
}
