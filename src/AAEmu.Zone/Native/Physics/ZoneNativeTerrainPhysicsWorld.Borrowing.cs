using System.Runtime.InteropServices;
using static AAEmu.Zone.Native.Engine.ZoneNativePhysicsModule;

using AAEmu.Zone.Native.Engine;

namespace AAEmu.Zone.Native.Physics;

public sealed unsafe partial class ZoneNativeTerrainPhysicsWorld
{
    private readonly ZoneNativeEngine.PhysicsLease? _engineLease;
    internal ZoneNativeEngine? Engine => _engineLease?.Engine;
    private readonly nint[] _ownedReplicas = new nint[5];

    // The concrete engine creates this lease while holding its gate. Do not enter it recursively.
    internal static ZoneNativeTerrainPhysicsWorld Borrow(ZoneNativeEngine.PhysicsLease lease) => new(lease);

    private ZoneNativeTerrainPhysicsWorld(ZoneNativeEngine.PhysicsLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        _ownerThread = ZoneNativePhysicsModule.GetCurrentThreadId();
        _engineLease = lease;
        _world = lease.World;
        _module = ZoneNativePhysicsModule.Borrow(lease);
        try
        {
            lease.ThrowIfFaulted();
            if (Marshal.ReadInt32(_world + 0x5798) != 0 || Marshal.ReadIntPtr(_world + 0x5260) != 0)
                throw new InvalidOperationException("Borrowed physics requires an idle world without physics workers or pending logged events.");
            ValidateBorrowedReplicas(); // Initial ownership requires all five slots to be empty.
        }
        catch
        {
            _module.ReleaseBorrowedIdentityLocks();
            throw;
        }
    }

    private void CaptureBorrowedReplicas()
    {
        if (_engineLease is null) return;
        // The sole native install is serialized under the engine gate. Record every returned
        // slot before validation so a partial, normally returned install retains its ownership.
        for (var i = 0; i < _ownedReplicas.Length; i++)
            _ownedReplicas[i] = Marshal.ReadIntPtr(_world + 0x620 + i * 8);
    }

    private void ValidateBorrowedReplicas()
    {
        if (_engineLease is null) return;
        for (var i = 0; i < _ownedReplicas.Length; i++)
        {
            if (Marshal.ReadIntPtr(_world + 0x620 + i * 8) == _ownedReplicas[i]) continue;
            var error = new InvalidOperationException("The engine's terrain replica ownership changed outside this adapter.");
            NativeInvocationFailed(error);
            throw error;
        }
    }

    private void ValidateBorrowedLivingEntity(LivingEntity body)
    {
        if (_engineLease is null) return;
        try
        {
            if (body.Pointer == 0 || Marshal.ReadIntPtr(body.Pointer) != _module.Handle + 0x1ee1d8 ||
                Marshal.ReadIntPtr(body.Pointer + 0x78) != _world || _module.IsPlaceholder(_world, body.Pointer) != 0 ||
                Marshal.ReadIntPtr(body.Pointer + 0x20) != body.ForeignPointer ||
                Marshal.ReadInt16(body.Pointer + 0x28) != body.ForeignKind ||
                Marshal.ReadByte(body.Pointer + 0x47) is 5 or 7)
                throw new InvalidOperationException("Borrowed physics requires its exact owned full living entity.");
        }
        catch (Exception error) { NativeInvocationFailed(error); throw; }
    }

    private void RetireBorrowedLivingEntity(LivingEntity body)
    {
        if (body.Pointer == 0)
        {
            ReleaseBorrowedForeignLease(body); // Includes a provider acquired before failed construction.
            return;
        }
        _callbackError = null; // Previously surfaced callback errors must not prevent safe cleanup.
        body.Configured = false;
        try
        {
            using var entities = new NativeGuard(_world + 0x57dc);
            using var world = new NativeGuard(_world + 0x57c4);
            using var queries = new NativeGuard(_world + 0x5804);
            ValidateBorrowedLivingEntity(body);
            if (body.ReferenceHeld && !body.ReferenceReleased)
            {
                Virtual<LivingReferenceCall>(body.Pointer, 0x18)(body.Pointer);
                body.ReferenceReleased = true;
            }
            // Native TimeStep replays this exact positive flag under its writer guards. It skips
            // the old-foreign-pointer event and applies destruction without the deferred queue.
            var pointer = body.Pointer;
            if (Virtual<DestroyLivingCall>(_world, 0xc0)(_world, pointer, 0, 1) == 0)
                throw new InvalidOperationException("Synchronous native living destruction was rejected.");
            // The validated full living allocation survives on the world's delayed-free list.
            // Destroy itself must sever foreign and ground references; never write substitutes.
            if (Marshal.ReadIntPtr(pointer + 0x20) != 0 || Marshal.ReadInt16(pointer + 0x28) != -1 ||
                Marshal.ReadByte(pointer + 0x47) != 7 || Marshal.ReadIntPtr(pointer + 0x3c0) != 0)
                throw new InvalidOperationException("Native living destruction did not detach its foreign owner and ground collider.");
            _module.PurgeRetiredEvents(_world); // 35079B30 removes retired direct participants, dispatching nothing.
            body.Pointer = 0;
        }
        catch (Exception error) { NativeInvocationFailed(error); throw; }
        // Native writer guards have now left scope; the outer engine operation still excludes
        // provider reentry and CSystem's earlier EntitySystem shutdown.
        CompleteCall();
        ReleaseBorrowedForeignLease(body);
    }

    private void ReleaseBorrowedForeignLease(LivingEntity body)
    {
        if (body.Lease is null) return;
        if (body.LeaseReleaseStarted)
            throw new InvalidOperationException("A foreign owner lease did not finish retirement; engine ownership remains retained.", _fault);
        body.LeaseReleaseStarted = true; // Dispose can partially retire its owner before throwing.
        try
        {
            body.Lease.Dispose();
            body.Lease = null;
        }
        catch (Exception error) { NativeInvocationFailed(error); throw; }
    }

    private void CompleteBorrowedDisposal()
    {
        // All owned bodies have detached and purged their stale events, and all five geometries
        // have released callbacks. CSystem alone owns native world Release and module teardown.
        try
        {
            foreach (var body in _livingEntities) ReleaseBorrowedForeignLease(body);
            _module.ReleaseBorrowedIdentityLocks();
            _engineLease!.Release(); // The engine operation gate is still held.
            _livingEntities.Clear();
            _world = 0;
            _disposed = true;
        }
        catch (Exception error) { NativeInvocationFailed(error); throw; }
    }
}
