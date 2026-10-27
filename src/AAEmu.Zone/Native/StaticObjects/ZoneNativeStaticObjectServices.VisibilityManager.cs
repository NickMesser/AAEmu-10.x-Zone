using System.Runtime.InteropServices;

namespace AAEmu.Zone.Native.StaticObjects;

public sealed unsafe partial class ZoneNativeStaticObjectServices
{
    private VisibilityManager? _visibilityManager;

    /// <summary>
    /// Acquires this engine generation's real CVisAreaManager, constructing it with the native
    /// allocator/constructor when absent. This establishes manager ownership only; cell loading,
    /// imported static-body completion and scene readiness remain separate operations.
    /// </summary>
    public VisibilityManager InitializeVisibilityManager()
    {
        using var operation = Enter();
        return InitializeVisibilityManagerCore();
    }

    private VisibilityManager InitializeVisibilityManagerCore()
    {
        _lease.RequireActiveOperation();
        if (_visibilityManager is { Retired: false } current)
        {
            ValidateVisibilityManager(current);
            return current;
        }
        RequireIdleVegetationWorld();
        var pointer = Marshal.ReadIntPtr(_lease.TerrainModule + 0x2fa908);
        var manager = new VisibilityManager(this, pointer == 0)
        {
            Pointer = pointer,
            EngineListener = Marshal.ReadIntPtr(_lease.TerrainModule + 0x2fa8e8),
            Renderer = Marshal.ReadIntPtr(_lease.TerrainModule + 0x2fa888)
        };
        _visibilityManager = manager; // Root native allocations before their first native call.
        try
        {
            if (pointer != 0)
            {
                ValidateVisibilityManager(manager);
                return manager; // Existing native ownership is borrowed, never silently replaced.
            }

            // The exact native 3015C910/30158A80 producer allocates 0x188, calls 30192C00, then
            // publishes the returned pointer. 3018F9F0 only stores the optional engine listener;
            // the constructor requires neither an IWorld argument nor a fabricated interface.
            Native(() => manager.Allocation = Terrain<VisibilityAllocateCall>(0x26b896)(0x188));
            if (manager.Allocation == 0) throw new OutOfMemoryException("Native CVisAreaManager allocation failed.");
            Native(() =>
            {
                manager.Pointer = Terrain<VisibilityConstructCall>(0x192c00)(manager.Allocation);
                manager.Constructed = true;
            });
            Require(manager.Pointer == manager.Allocation,
                "The native visibility constructor returned a different allocation.");
            RequireTable(manager.Pointer, 0x291c98);
            Require(Marshal.ReadIntPtr(_lease.TerrainModule + 0x2fa908) == 0,
                "Another visibility manager was published during native construction.");
            Require(Marshal.ReadIntPtr(_lease.TerrainModule + 0x2fa8e8) == manager.EngineListener &&
                Marshal.ReadIntPtr(_lease.TerrainModule + 0x2fa888) == manager.Renderer,
                "The native visibility constructor's engine dependencies changed.");
            RequireVisibilityPrimaryListsEmpty(manager.Pointer);
            Native(() =>
            {
                Marshal.WriteIntPtr(_lease.TerrainModule + 0x2fa908, manager.Pointer);
                manager.Published = true;
            });
            ValidateVisibilityManager(manager);
            return manager;
        }
        catch (Exception error)
        {
            // Any uncertain native call or callback poisons the engine and retains this owner.
            // Do not invoke a destructor against a potentially partial native constructor.
            if (_poisoned) throw;
            try { ReleaseUnpublishedVisibilityManager(manager); }
            catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
            throw;
        }
    }

    private nint RequireVisibilityManagerPointer()
    {
        _lease.RequireActiveOperation();
        var manager = _visibilityManager;
        if (manager is null || manager.Retired)
            throw new InvalidOperationException("Initialize the engine's visibility manager before admitting a visibility cell.");
        ValidateVisibilityManager(manager);
        return manager.Pointer;
    }

    private void ValidateVisibilityManager(VisibilityManager manager)
    {
        _lease.RequireActiveOperation();
        Require(ReferenceEquals(_visibilityManager, manager) && !manager.Retired && manager.Pointer != 0 &&
            Marshal.ReadIntPtr(_lease.TerrainModule + 0x2fa908) == manager.Pointer,
            "The visibility manager no longer belongs to this retained engine generation.");
        // Verify current global identity before dereferencing any captured native pointer.
        RequireTable(manager.Pointer, 0x291c98);
        Require(Marshal.ReadIntPtr(_lease.TerrainModule + 0x2fa8e8) == manager.EngineListener &&
            Marshal.ReadIntPtr(_lease.TerrainModule + 0x2fa888) == manager.Renderer,
            "The visibility manager's retained engine dependencies were replaced.");
    }

    private void RequireVisibilityPrimaryListsEmpty(nint manager)
    {
        foreach (var offset in new[] { 0x28, 0x48, 0x58 })
        {
            var count = Marshal.ReadInt32(manager + offset + 8);
            var capacity = Marshal.ReadInt32(manager + offset + 12);
            var items = Marshal.ReadIntPtr(manager + offset);
            Require(count >= 0 && capacity >= count && (capacity == 0 || items != 0),
                "The native visibility manager has an invalid primary array.");
            if (count != 0)
                throw new InvalidOperationException("Retire all native visibility areas, portals and occlusion areas before their owned manager.");
        }
        Require(Marshal.ReadIntPtr(manager + 8) == 0 && Marshal.ReadIntPtr(manager + 0x10) == 0,
            "The empty visibility manager retains an active native area pointer.");
    }

    private void ReleaseUnpublishedVisibilityManager(VisibilityManager manager)
    {
        _lease.RequireActiveOperation();
        if (manager.Published || !manager.CreatedByThisService)
            throw new InvalidOperationException("Published or borrowed visibility ownership requires ordinary retirement.");
        if (manager.Constructed)
        {
            RequireVisibilityPrimaryListsEmpty(manager.Pointer);
            Native(() => Terrain<VoidCall>(0x192e80)(manager.Pointer));
            manager.Constructed = false;
        }
        if (manager.Allocation != 0)
        {
            Native(() => Terrain<VoidCall>(0x26b87e)(manager.Allocation));
            manager.Allocation = 0;
        }
        manager.Pointer = 0;
        manager.Retired = true;
        _visibilityManager = null;
    }

    private void RetireVisibilityManager(VisibilityManager manager)
    {
        if (manager.Retired) return;
        ValidateVisibilityManager(manager);
        if (manager.Cells.Count != 0)
            throw new InvalidOperationException("Retire every admitted visibility cell and its imported-node ownership before releasing the manager.");
        RequireIdleVegetationWorld();
        if (manager.CreatedByThisService)
        {
            RequireVisibilityPrimaryListsEmpty(manager.Pointer);
            // Native 3DEngine retirement performs this destructor, allocator release, and global
            // clear in this order. Clearing the exact owned publication prevents a second delete
            // by eventual engine shutdown. The real renderer/listener providers remain alive.
            Native(() => Terrain<VoidCall>(0x192e80)(manager.Pointer));
            manager.Constructed = false;
            Require(Marshal.ReadIntPtr(_lease.TerrainModule + 0x2fa908) == manager.Pointer,
                "The manager publication changed during its native destructor.");
            Native(() =>
            {
                Terrain<VoidCall>(0x26b87e)(manager.Allocation);
                manager.Allocation = 0;
                Marshal.WriteIntPtr(_lease.TerrainModule + 0x2fa908, 0);
                manager.Published = false;
            });
        }
        // A borrowed manager stays published and remains owned by the original native engine.
        manager.Pointer = 0;
        manager.Retired = true;
        _visibilityManager = null;
    }

    // Root integrates this into service Dispose after cell and queued imported-node retirement.
    private void RetireVisibilityManagerResources()
    {
        if (_visibilityManager is not null) RetireVisibilityManager(_visibilityManager);
    }

    private void DisposeVisibilityManager(VisibilityManager manager)
    {
        if (manager.Retired) return;
        using var operation = Enter();
        RetireVisibilityManager(manager);
    }

    // Cell loading reserves an exact token BEFORE entering 30198BF0. An exception cannot turn an
    // unknown native allocation into an apparently empty manager that is safe to destroy.
    private VisibilityCellManagerOwnership BeginVisibilityCellManagerOwnership(int cellX, int cellY)
    {
        _lease.RequireActiveOperation();
        ValidateTerrainCell(cellX, cellY);
        var manager = InitializeVisibilityManagerCore();
        var key = (cellX, cellY);
        if (manager.Cells.ContainsKey(key))
            throw new InvalidOperationException("This engine-cell coordinate already has a visibility ownership generation.");
        var ownership = new VisibilityCellManagerOwnership(manager, cellX, cellY);
        manager.Cells.Add(key, ownership);
        return ownership;
    }

    private void ValidateVisibilityCellManagerOwnership(VisibilityCellManagerOwnership ownership)
    {
        ValidateVisibilityManager(ownership.Manager);
        Require(!ownership.Retired && ownership.Manager.Cells.TryGetValue((ownership.CellX, ownership.CellY), out var current) &&
            ReferenceEquals(current, ownership), "The visibility cell no longer owns its exact manager admission.");
    }

    private void EndVisibilityCellManagerOwnership(VisibilityCellManagerOwnership ownership)
    {
        ValidateVisibilityCellManagerOwnership(ownership);
        if (ownership.NativeLoadStarted && !ownership.NativeRetirementCompleted)
            throw new InvalidOperationException("Native visibility mutation has not completed its area and imported-node retirement.");
        ownership.Manager.Cells.Remove((ownership.CellX, ownership.CellY));
        ownership.Retired = true;
    }

    /// <summary>
    /// One retained real manager generation. Dispose releases a borrowed generation without
    /// deleting it, or destroys a newly created empty manager using the actual native allocator.
    /// Admitted cells must finish their native area and imported-node retirement first.
    /// </summary>
    public sealed class VisibilityManager : IDisposable
    {
        private readonly ZoneNativeStaticObjectServices _owner;
        internal nint Pointer, Allocation, EngineListener, Renderer;
        internal bool Constructed, Published, Retired;
        internal readonly Dictionary<(int X, int Y), VisibilityCellManagerOwnership> Cells = [];
        public bool CreatedByThisService { get; }
        public bool IsDisposed => Retired;
        internal VisibilityManager(ZoneNativeStaticObjectServices owner, bool created)
        { _owner = owner; CreatedByThisService = created; }
        public void Dispose() => _owner.DisposeVisibilityManager(this);
    }

    internal sealed class VisibilityCellManagerOwnership(VisibilityManager manager, int cellX, int cellY)
    {
        internal readonly VisibilityManager Manager = manager;
        internal readonly int CellX = cellX, CellY = cellY;
        internal bool NativeLoadStarted, NativeRetirementCompleted, Retired;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint VisibilityAllocateCall(nuint bytes);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint VisibilityConstructCall(nint allocation);
}
