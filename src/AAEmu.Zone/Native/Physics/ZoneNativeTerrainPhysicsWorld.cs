using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using static AAEmu.Zone.Native.Engine.ZoneNativePhysicsModule;

using AAEmu.Zone.Native.Engine;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Native.Physics;

/// <summary>A copied native terrain hit. Coordinates belong to the loaded engine-local window.</summary>
public readonly record struct ZoneNativeTerrainRayHit(float Distance, Vector3 Position, Vector3 Normal,
    ushort PhysicalMaterialId, ushort LocalSurfaceIndex);

/// <summary>
/// Original Windows x64 CryPhysics surface tables, five-replica heightfield geometry and terrain
/// rays, synchronous static-scene rays, and owned living bodies without skeletons.
/// Create/use/dispose on one native thread. Character/static geometry producers, stepping,
/// and broader or queued world queries belong to their separate owners.
/// One world may be active per process; the verified module/host stay pinned. A real CSystem may
/// instead lend its world through CreatePhysicsWorld; that adapter retires only its own resources.
/// Callers must serialize heightfield mutations with physics operations. A failed retirement keeps
/// the native owner and callback roots alive; Dispose never falls through to unsafe populated Release.
/// </summary>
public sealed unsafe partial class ZoneNativeTerrainPhysicsWorld : IZoneTerrainPhysicsWorld, IZonePhysicalSurfaceBackend, IDisposable
{
    private readonly uint _ownerThread;
    private readonly Thread _ownerManagedThread = Thread.CurrentThread;
    private readonly ZoneNativePhysicsModule _module;
    private readonly List<Callbacks> _callbackRoots = [];
    private nint _world;
    private bool _busy;
    private bool _insideCallback;
    private bool _installed;
    private bool _disposed;
    private long _generation;
    private Exception? _fault;
    private Exception? _callbackError;
    private nint _retirementInProgress;
    private bool _nativeInvocationFailed;
    private bool _releaseStarted;
    private Entity? _entity;

    private ZoneNativeTerrainPhysicsWorld(string binaryDirectory)
    {
        // Platform admission precedes the Win32 thread ID call.
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("The original terrain physics backend requires Windows x64.");
        _ownerThread = GetCurrentThreadId();
        _module = ZoneNativePhysicsModule.Acquire(binaryDirectory, this);
        // Keep the active-world claim if a native call fails before returning a known world.
        _world = _module.Factory(_module.System);
        if (_world == 0)
        {
            _module.ReleaseClaim(this);
            throw new InvalidOperationException("The native physical world factory returned null.");
        }
        _module.ValidateWorld(_world);
        _module.SetWorld(_world);
        try { _module.ThrowHostError(); }
        catch
        {
            // These are captured managed host callback errors, after native construction returned.
            // Disposal retains ownership if its retirement cannot complete.
            Dispose();
            throw;
        }
    }

    public static ZoneNativeTerrainPhysicsWorld Create(string binaryDirectory) => new(binaryDirectory);

    public bool SetSurfaceParameters(int surfaceId, float elasticity, float friction, uint flags)
    {
        using var operation = Enter();
        int result;
        try { result = Virtual<SetSurfaceCall>(_world, 0xe8)(_world, surfaceId, elasticity, friction, flags); }
        catch (Exception error) { NativeInvocationFailed(error); throw; }
        CompleteCall();
        return result != 0;
    }

    public IZoneTerrainPhysicsEntity SetHeightfield(ZoneTerrainPhysicsDescriptor descriptor, IReadOnlyList<int> physicalMaterials)
    {
        using var operation = Enter(); // Claim before caller Count/indexers can execute user code.
        ArgumentNullException.ThrowIfNull(descriptor);
        var map = Snapshot(physicalMaterials);
        var callbacks = new Callbacks(this, descriptor);
        _callbackRoots.Add(callbacks); // Proposed and prior callbacks survive partial native failure.
        var shape = Heightfield.From(descriptor, callbacks);
        _installed = false;
        try
        {
            nint entity;
            try
            {
                fixed (int* materials = map)
                    entity = Virtual<InstallCall>(_world, 0x88)(_world, &shape, materials, (uint)map.Length);
            }
            catch (Exception error) { NativeInvocationFailed(error); throw; }
            CaptureBorrowedReplicas();
            if (entity == 0 || entity != Marshal.ReadIntPtr(_world + 0x620))
                throw new InvalidOperationException("The native heightfield did not return its first entity.");
            for (var i = 0; i < 5; i++)
                if (Marshal.ReadIntPtr(_world + 0x620 + i * 8) == 0)
                    throw new InvalidOperationException("The native heightfield has an incomplete replica set.");
            CompleteCall();
            if (_entity is null || _entity.Pointer != entity || _entity.Generation != _generation)
                _entity = new Entity(this, entity, _generation);
            _installed = true;
            _callbackRoots.Clear();
            _callbackRoots.Add(callbacks);
            return _entity;
        }
        catch (Exception error)
        {
            _fault = error;
            throw;
        }
    }

    public void SetHeightfieldMaterials(IReadOnlyList<int> physicalMaterials)
    {
        using var operation = Enter();
        RequireInstalled();
        var map = Snapshot(physicalMaterials);
        try
        {
            fixed (int* materials = map)
                Virtual<MaterialsCall>(_world, 0x98)(_world, materials, (uint)map.Length);
        }
        catch (Exception error) { NativeInvocationFailed(error); throw; }
        CompleteCall();
    }

    /// <summary>
    /// Executes original +160 terrain intersection on replica4, using flags0. The displacement
    /// defines the bounded ray segment; this method owns the mutable native copy and copied hit.
    /// </summary>
    public bool RaycastTerrain(Vector3 origin, Vector3 displacement, out ZoneNativeTerrainRayHit hit)
    {
        using var operation = Enter();
        RequireInstalled();
        if (!Finite(origin) || !Finite(displacement) || !float.IsFinite(displacement.LengthSquared()))
            throw new ArgumentOutOfRangeException(nameof(displacement), "Native terrain rays require finite bounded vectors.");
        hit = default;
        if (displacement.LengthSquared() == 0) return false;
        var nativeHit = new RayHit { Distance = -1 };
        try { Virtual<RayCall>(_world, 0x160)(_world, &origin, &displacement, &nativeHit, 0, 4); }
        catch (Exception error) { NativeInvocationFailed(error); throw; }
        CompleteCall();
        if (nativeHit.Collider == 0) return false;
        hit = new(nativeHit.Distance, nativeHit.Position, nativeHit.Normal,
            nativeHit.PhysicalMaterial, nativeHit.SurfaceIndex);
        return true;
    }

    public void ClearHeightfield()
    {
        using var operation = Enter(allowFaulted: true);
        if (_livingEntities.Any(body => body.Pointer != 0))
            throw new InvalidOperationException("Retire owned living bodies before clearing their terrain.");
        Retire();
    }

    public void Dispose()
    {
        if (_disposed) return;
        using var operation = Enter(allowFaulted: true);
        RetireLivingEntities();
        Retire(); // On failure, keep world, callbacks and module claim. Never call Release below.
        for (var i = 0; i < 5; i++)
            if (Marshal.ReadIntPtr(_world + 0x620 + i * 8) != 0)
                throw new InvalidOperationException("Cannot release a world with live terrain replicas.");
        if (_engineLease is not null)
        {
            CompleteBorrowedDisposal();
            return;
        }
        _releaseStarted = true; // Any surfaced failure leaves the pointer permanently unsafe to retry.
        try { Virtual<FreeCall>(_world, 0x38)(_world); }
        catch (Exception error) { NativeInvocationFailed(error); throw; }
        _world = 0;
        _module.SetWorld(0);
        ReleaseLivingLeases();
        _disposed = true;
        _module.ReleaseClaim(this);
        // Host services/listeners and module roots remain owned by the process-wide module.
    }

    private void Retire()
    {
        if (_retirementInProgress != 0)
            throw new InvalidOperationException("Native retirement failed after unlinking an entity; ownership must remain retained.", _fault);
        _installed = false;
        _generation++;
        _entity = null;
        try
        {
            ValidateBorrowedReplicas();
            ZoneNativeTerrainRetirement.Retire(_world, _module.Delete, _module.DeleteArray,
                ref _retirementInProgress, _engineLease is null ? null : _ownedReplicas);
            _callbackRoots.Clear();
            _callbackError = null;
            _fault = null;
        }
        catch (Exception error)
        {
            _fault = error;
            if (_engineLease is not null) NativeInvocationFailed(error);
            throw;
        }
    }

    private void SetForeignDataId(Entity entity, short id)
    {
        using var operation = Enter();
        RequireInstalled();
        if (entity.Generation != _generation || !ReferenceEquals(_entity, entity) ||
            entity.Pointer != Marshal.ReadIntPtr(_world + 0x620))
            throw new InvalidOperationException("The native terrain entity has retired.");
        var parameters = new ForeignData
        {
            Type = 11, Pointer = -1, Id = id, Flags = int.MinValue, FlagsAnd = -1, FlagsOr = 0
        };
        try { Virtual<ParametersCall>(entity.Pointer, 0x38)(entity.Pointer, &parameters, 0); }
        catch (Exception error) { NativeInvocationFailed(error); throw; }
        CompleteCall();
    }

    private Operation Enter(bool allowFaulted = false)
    {
        if (!ReferenceEquals(Thread.CurrentThread, _ownerManagedThread) || GetCurrentThreadId() != _ownerThread)
            throw new InvalidOperationException("Native physics operations require their creating thread.");
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_releaseStarted || _nativeInvocationFailed)
            throw new InvalidOperationException("A native invocation did not complete; its ownership is retained and cannot be retried.", _fault);
        if (_busy || _insideCallback)
            throw new InvalidOperationException("Native physics operations cannot reenter an active call or callback.");
        if (!allowFaulted && _fault is not null)
            throw new InvalidOperationException("Native physics is faulted; retire owned bodies and the heightfield before reuse.", _fault);
        var engineOperation = _engineLease?.Enter();
        try
        {
            ValidateBorrowedReplicas();
            _busy = true;
            return new Operation(this, engineOperation);
        }
        catch
        {
            engineOperation?.Dispose();
            throw;
        }
    }

    private void NativeInvocationFailed(Exception error)
    {
        _nativeInvocationFailed = true;
        _fault = error;
        _engineLease?.Poison(error);
    }
    private void CompleteCall()
    {
        try
        {
            _module.ThrowHostError();
            _engineLease?.ThrowIfFaulted();
            if (_callbackError is not null) ExceptionDispatchInfo.Capture(_callbackError).Throw();
        }
        catch (Exception error) { _fault = error; throw; }
    }
    private void RequireInstalled()
    {
        if (!_installed) throw new InvalidOperationException("The physical heightfield has not been installed.");
    }
    private static int[] Snapshot(IReadOnlyList<int> materials)
    {
        ArgumentNullException.ThrowIfNull(materials);
        if (materials.Count != ZoneTerrainPhysicsDescriptor.MaterialCount)
            throw new ArgumentException("Terrain physics requires exactly 2048 material entries.", nameof(materials));
        var result = new int[ZoneTerrainPhysicsDescriptor.MaterialCount];
        for (var i = 0; i < result.Length; i++) result[i] = materials[i];
        return result;
    }
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private sealed class Entity(ZoneNativeTerrainPhysicsWorld owner, nint pointer, long generation) : IZoneTerrainPhysicsEntity
    {
        internal nint Pointer { get; } = pointer;
        internal long Generation { get; } = generation;
        public void SetForeignDataId(short id) => owner.SetForeignDataId(this, id);
    }
    private readonly struct Operation(ZoneNativeTerrainPhysicsWorld owner, IDisposable? engineOperation) : IDisposable
    {
        public void Dispose()
        {
            owner._busy = false;
            engineOperation?.Dispose();
        }
    }
    private readonly struct NativeGuard : IDisposable
    {
        private readonly nint _address;
        internal NativeGuard(nint address)
        {
            if (Interlocked.CompareExchange(ref *(int*)address, 0x10000, 0) != 0)
                throw new InvalidOperationException("Native physics has an unexpected in-flight guard owner.");
            _address = address;
        }
        public void Dispose() => Interlocked.Add(ref *(int*)_address, -0x10000);
    }
    private sealed class Callbacks
    {
        internal readonly HeightCall Height;
        internal readonly SurfaceCall Surface;
        internal Callbacks(ZoneNativeTerrainPhysicsWorld owner, ZoneTerrainPhysicsDescriptor descriptor)
        {
            Height = (x, y) =>
            {
                owner._insideCallback = true;
                try { return descriptor.HeightCallback(x, y); }
                catch (Exception error) { owner._callbackError ??= error; return 0; }
                finally { owner._insideCallback = false; }
            };
            Surface = (x, y) =>
            {
                owner._insideCallback = true;
                try { return descriptor.SurfaceCallback(x, y); }
                catch (Exception error) { owner._callbackError ??= error; return 2047; }
                finally { owner._insideCallback = false; }
            };
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x78)]
    private struct Heightfield
    {
        [FieldOffset(0)] internal Vector3 BasisX;
        [FieldOffset(0x0c)] internal Vector3 BasisY;
        [FieldOffset(0x18)] internal Vector3 BasisZ;
        [FieldOffset(0x28)] internal Vector3 Origin;
        [FieldOffset(0x34)] internal Vector2 Step;
        [FieldOffset(0x3c)] internal Vector2 InverseStep;
        [FieldOffset(0x44)] internal int SizeX;
        [FieldOffset(0x48)] internal int SizeY;
        [FieldOffset(0x4c)] internal int StrideX;
        [FieldOffset(0x50)] internal int StrideY;
        [FieldOffset(0x54)] internal float HeightScale;
        [FieldOffset(0x58)] internal ushort SurfaceMask;
        [FieldOffset(0x5a)] internal ushort HeightMask;
        [FieldOffset(0x5c)] internal int HoleSurface;
        [FieldOffset(0x60)] internal int SurfaceShift;
        [FieldOffset(0x68)] internal nint HeightCallback;
        [FieldOffset(0x70)] internal nint SurfaceCallback;
        internal static Heightfield From(ZoneTerrainPhysicsDescriptor value, Callbacks callbacks) => new()
        {
            BasisX = value.BasisX, BasisY = value.BasisY, BasisZ = value.BasisZ, Origin = value.Origin,
            Step = value.Step, SizeX = value.SizeX, SizeY = value.SizeY, StrideX = value.StrideX,
            StrideY = value.StrideY, HeightScale = value.HeightScale, SurfaceMask = value.SurfaceMask,
            HeightMask = value.HeightMask, HoleSurface = value.HoleSurfaceIndex,
            HeightCallback = Marshal.GetFunctionPointerForDelegate(callbacks.Height),
            SurfaceCallback = Marshal.GetFunctionPointerForDelegate(callbacks.Surface)
        };
    }
    [StructLayout(LayoutKind.Explicit, Size = 0x20)]
    private struct ForeignData
    {
        [FieldOffset(0)] internal int Type;
        [FieldOffset(8)] internal nint Pointer;
        [FieldOffset(0x10)] internal short Id;
        [FieldOffset(0x14)] internal int Flags;
        [FieldOffset(0x18)] internal int FlagsAnd;
        [FieldOffset(0x1c)] internal int FlagsOr;
    }
    [StructLayout(LayoutKind.Explicit, Size = 0x50)]
    private struct RayHit
    {
        [FieldOffset(0)] internal float Distance;
        [FieldOffset(8)] internal nint Collider;
        [FieldOffset(0x18)] internal ushort PhysicalMaterial;
        [FieldOffset(0x1a)] internal ushort SurfaceIndex;
        [FieldOffset(0x24)] internal Vector3 Position;
        [FieldOffset(0x30)] internal Vector3 Normal;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int SetSurfaceCall(nint world, int id, float elasticity, float friction, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint InstallCall(nint world, Heightfield* descriptor, int* materials, uint count);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void MaterialsCall(nint world, int* materials, uint count);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void RayCall(nint world, Vector3* origin, Vector3* direction, RayHit* hit, byte flags, int replica);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int ParametersCall(nint entity, ForeignData* parameters, int threadSafe);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate float HeightCall(int x, int y);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint SurfaceCall(int x, int y);
}
