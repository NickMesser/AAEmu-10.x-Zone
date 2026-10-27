using System.Runtime.InteropServices;
using static AAEmu.Zone.Native.Engine.ZoneNativePhysicsModule;

namespace AAEmu.Zone.Native.Physics;

/// <summary>
/// The existing corrected3508B860 five-replica retirement sequence, shared without creating a
/// competing terrain importer. This internal primitive does not establish ownership from its
/// pointer/delegate arguments. Its caller must already hold the concrete owner generation gate,
/// validate the exact world/module/environment and captured installation, exclude native workers
/// and in-flight producers, and finish every body/foreign association that can retain a collider.
/// Keep terrain, services, module and callback roots alive until this operation and the caller's
/// subsequent lifetime transition finish. A failure must retain the owner and progress checkpoint;
/// do not retry an uncertain native invocation or fall through to populated native world Release.
/// </summary>
internal static unsafe class ZoneNativeTerrainRetirement
{
    /// <summary>
    /// Retires slots world+620..640 in order under world57C4 then query5804 guards. Preserves
    /// geometry virtual18, scalar holder delete, array material-map delete and entity virtual0
    /// cleanup. The entity48 guard is released before the deleting destructor, fixing the proven
    /// native post-delete guard write without patching the DLL. A supplied ledger must contain
    /// all five exact captured slot identities; entries clear only after complete entity deletion.
    /// A null ledger is reserved for the existing exclusively owned standalone physical world,
    /// whose module claim supplies exclusivity. An engine-owned scene must supply its captured
    /// ledger; merely snapshotting unknown current pointers does not prove ownership.
    /// </summary>
    internal static void Retire(nint world, FreeCall delete, FreeCall deleteArray,
        ref nint inProgress, nint[]? ownedReplicas)
    {
        ArgumentNullException.ThrowIfNull(delete);
        ArgumentNullException.ThrowIfNull(deleteArray);
        if (world == 0) throw new ArgumentException("Terrain retirement requires its validated physical world.", nameof(world));
        if (inProgress != 0)
            throw new InvalidOperationException("A previous native terrain retirement remains unresolved; retain its owner and checkpoint.");
        if (ownedReplicas is not null)
        {
            if (ownedReplicas.Length != 5)
                throw new ArgumentException("Terrain retirement requires all five captured replica identities.", nameof(ownedReplicas));
            for (var i = 0; i < ownedReplicas.Length; i++)
                if (Marshal.ReadIntPtr(world + 0x620 + i * 8) != ownedReplicas[i])
                    throw new InvalidOperationException("The physical terrain installation no longer matches the caller's captured ownership.");
        }

        using var worldGuard = new NativeGuard(world + 0x57c4);
        using var queryGuard = new NativeGuard(world + 0x5804);
        for (var i = 0; i < 5; i++)
        {
            var slot = world + 0x620 + i * 8;
            var entity = Marshal.ReadIntPtr(slot);
            if (entity == 0) continue;
            using (var entityGuard = new NativeGuard(entity + 0x48))
            {
                var part = Marshal.ReadIntPtr(entity + 0x118);
                if (part == 0 || Marshal.ReadIntPtr(part + 0x38) == 0)
                    throw new InvalidOperationException("Native terrain replica has incomplete geometry ownership.");
                var holder = Marshal.ReadIntPtr(part + 0x38);
                var geometry = Marshal.ReadIntPtr(holder);
                inProgress = entity;
                Marshal.WriteIntPtr(slot, 0);
                if (geometry != 0)
                {
                    Virtual<FreeCall>(geometry, 0x18)(geometry);
                    Marshal.WriteIntPtr(holder, 0);
                }
                delete(holder);
                Marshal.WriteIntPtr(part + 0x38, 0);
                var map = Marshal.ReadIntPtr(part + 0x68);
                if (map != 0)
                {
                    deleteArray(map);
                    Marshal.WriteIntPtr(part + 0x68, 0);
                }
            }
            Virtual<DeleteEntityCall>(entity, 0)(entity, 1);
            inProgress = 0;
            if (ownedReplicas is not null) ownedReplicas[i] = 0;
        }
    }

    // Same interlocked guard implementation as the existing importer; no wait, worker stop,
    // queued-query cancellation or extra lock acquisition is inferred by this extraction.
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

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint DeleteEntityCall(nint entity, uint flags);
}
