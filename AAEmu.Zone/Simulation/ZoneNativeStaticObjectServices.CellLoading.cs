using System.Runtime.InteropServices;

namespace AAEmu.Zone.Simulation;

public sealed unsafe partial class ZoneNativeStaticObjectServices
{
    // These are nonowning identities. C3DEngine owns its published root and recursively destroys
    // it during engine teardown, after this service's wrapped render nodes have retired.
    private nint _cellObjectRoot;
    private readonly Dictionary<nint, CellObjectNodeIdentity> _cellObjectNodes = [];
    private CellObjectNodeAllocation? _pendingCellObjectNode;
    private nint _cellObjectLoadingSystemTable, _cellObjectLoadingCallback;

    /// <summary>
    /// Obtains or creates the real engine-owned outdoor octree. Follows3015C910/3013EDE0:
    /// native allocation, actual engine+450 bounds,3021BC80 (including its depth-three spatial
    /// hierarchy), engine+5DC0 publication, then300EA870 root-list append. A null area is the
    /// native outdoor association; no IWorld, visibility area or CTerrain is manufactured.
    /// Native failure pins the partial allocation and engine; no partial-constructor rollback
    /// or independent root teardown is attempted. Must precede the serialized file wrappers.
    /// </summary>
    internal nint EnsureOutdoorCellObjectRoot()
    {
        using var operation = Enter();
        var root = Marshal.ReadIntPtr(_lease.ThreeDEngine + 0x5dc0);
        if (_cellObjectRoot != 0)
        {
            ValidateCellObjectRoot();
            return _cellObjectRoot;
        }
        if (root != 0)
        {
            CheckCellObjectNodeFields(root, 0, 0, 0);
            ValidateCellObjectRootList(root);
            _cellObjectNodes.Add(root, new(0, 0, 0));
            _cellObjectRoot = root;
            return root;
        }

        // Do not overwrite a previous outdoor producer's global registrations. An initialized
        // root is admitted above; a missing root with surviving registrations is not a blank
        // engine generation, even though native first-world setup simply resets the list count.
        ValidateCellObjectRootList(0);
        if (Marshal.ReadInt32(_lease.ThreeDEngine + 0x5dd0) != 0)
            throw new InvalidOperationException("Outdoor root creation requires an empty engine root list.");
        for (var i = 0; i < 512; i++)
            if (Marshal.ReadIntPtr(_lease.TerrainModule + 0x2fc270 + i * 8) != 0)
                throw new InvalidOperationException("Outdoor root creation cannot replace an existing native spatial-table owner.");

        var allocation = PrepareCellObjectNodeAllocation();
        // Everything after native allocation starts is retained on uncertainty, including
        // managed bookkeeping failure. The constructor can publish children before returning.
        Native(() =>
        {
            allocation.Pointer = Terrain<AllocateCellObjectNodeCall>(0x26b896)(0x208);
            Require(allocation.Pointer != 0, "Native outdoor octree allocation returned null.");
            var extent = Bind<ReferenceCall>(_lease.ThreeDEngine, 0x450, 0x138b10);
            var maximumZ = extent(_lease.ThreeDEngine);
            var maximumY = extent(_lease.ThreeDEngine);
            var maximumX = extent(_lease.ThreeDEngine);
            // Preserve three separate calls and native Z/Y/X acquisition order.
            var bounds = (float*)allocation.Bounds;
            bounds[0] = bounds[1] = bounds[2] = 0;
            bounds[3] = maximumX; bounds[4] = maximumY; bounds[5] = maximumZ;
            var created = Terrain<ConstructCellObjectNodeCall>(0x21bc80)(allocation.Pointer,
                0, allocation.Bounds, 0, 0, 0, 0);
            Require(created == allocation.Pointer, "Native octree construction returned a different allocation.");
            Marshal.WriteIntPtr(_lease.ThreeDEngine + 0x5dc0, created);
            Marshal.WriteInt32(_lease.ThreeDEngine + 0x5dd0, 0);
            Terrain<AppendCellObjectRootCall>(0xea870)(_lease.ThreeDEngine + 0x5dc8,
                _lease.ThreeDEngine + 0x5dc0);
            _cellObjectRoot = created;
            _cellObjectNodes.Add(created, new(0, 0, 0));
            ValidateCellObjectRoot();
            CompleteCellObjectNodeAllocation(allocation);
        });
        return _cellObjectRoot;
    }

    /// <summary>
    /// Reads30166870's original DAT302FA949 snapshot, taken before resource-table reads. The
    /// file owner retains this result when deciding whether to resolve its model/material tables.
    /// It is deliberately separate from the live per-node dispatch flag.
    /// </summary>
    internal bool ShouldLoadMainCellObjectResources()
    {
        using var operation = Enter();
        ValidateCellObjectRoot();
        return Marshal.ReadByte(_lease.TerrainModule + 0x2fa949) == 0;
    }

    /// <summary>
    /// Reads the live node-entry gate at configuration+37C. False means native subtree return0,
    /// before its header, loading callback or children; the caller must propagate failure.
    /// </summary>
    internal bool ShouldReadMainCellObjectNode()
    {
        using var operation = Enter();
        ValidateCellObjectRoot();
        var configuration = Marshal.ReadIntPtr(_lease.TerrainModule + 0x2fa8d0);
        Require(configuration != 0, "The real terrain configuration is no longer available.");
        return Marshal.ReadInt32(configuration + 0x37c) != 0;
    }

    /// <summary>
    /// Reads the live DAT302FA949 flag after admitting a valid node. False skips only its own
    /// object block; the valid-node loading callback and authored children still execute.
    /// </summary>
    internal bool ShouldDispatchMainCellObjectNode()
    {
        using var operation = Enter();
        ValidateCellObjectRoot();
        return Marshal.ReadByte(_lease.TerrainModule + 0x2fa949) == 0;
    }

    /// <summary>
    /// Calls the actual captured CSystem+560 once after a valid main node's object block has
    /// finished, including an empty or dispatch-disabled block, and before child creation.
    /// This is not a big-object or per-record callback. Later gates must be reread after it.
    /// </summary>
    internal void CompleteMainCellObjectNodeLoad()
    {
        using var operation = Enter();
        ValidateCellObjectRoot();
        var table = Marshal.ReadIntPtr(_lease.System);
        Require(table != 0, "The captured CSystem has no native loading callback table.");
        var callback = Marshal.ReadIntPtr(table + 0x560);
        Require(callback != 0, "The captured CSystem has no native loading callback.");
        if (_cellObjectLoadingCallback == 0)
        {
            _cellObjectLoadingSystemTable = table;
            _cellObjectLoadingCallback = callback;
        }
        Require(table == _cellObjectLoadingSystemTable && callback == _cellObjectLoadingCallback,
            "The CSystem loading callback changed during cell-object loading.");
        Native(() => Marshal.GetDelegateForFunctionPointer<VoidCall>(callback)(_lease.System));
        ValidateCellObjectRoot();
    }

    /// <summary>
    /// Returns the actual child at parent+20+8*octant, constructing it with30218570 bounds and
    ///3021BC80 only if missing. Invoke before evaluating the child's live entry gate, in authored
    /// octant order. The pointer is internal and valid only until child completion or engine
    /// teardown; only this service's captured root/child identities are accepted as parents.
    /// </summary>
    internal nint EnsureMainCellObjectChild(nint parent, int octant)
    {
        using var operation = Enter();
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)octant, 8u);
        var parentIdentity = ValidateCellObjectNode(parent);
        if (parentIdentity.Depth >= 64)
            throw new NotSupportedException("Native cell-object traversal exceeds the admitted serialized depth.");
        var child = Marshal.ReadIntPtr(parent + 0x20 + octant * 8);
        if (child != 0)
        {
            CaptureCellObjectChild(parent, octant, child, parentIdentity.Depth + 1);
            return child;
        }
        var allocation = PrepareCellObjectNodeAllocation();
        Native(() =>
        {
            allocation.Pointer = Terrain<AllocateCellObjectNodeCall>(0x26b896)(0x208);
            Require(allocation.Pointer != 0, "Native child octree allocation returned null.");
            var bounds = Terrain<CellObjectChildBoundsCall>(0x218570)(parent, allocation.Bounds, octant);
            Require(bounds == allocation.Bounds, "Native child bounds did not use the supplied output buffer.");
            child = Terrain<ConstructCellObjectNodeCall>(0x21bc80)(allocation.Pointer,
                Marshal.ReadInt32(parent + 0x1ac), bounds, Marshal.ReadIntPtr(parent), parent,
                parentIdentity.Depth + 1, octant);
            Require(child == allocation.Pointer, "Native child construction returned a different allocation.");
            Marshal.WriteIntPtr(parent + 0x20 + octant * 8, child);
            CaptureCellObjectChild(parent, octant, child, parentIdentity.Depth + 1);
            CompleteCellObjectNodeAllocation(allocation);
        });
        return child;
    }

    /// <summary>
    /// Performs30229200's post-child retirement. Returned count0 is failure: only an entirely
    /// resource-free subtree may be destroyed here, and the caller must stop siblings and
    /// propagate0. Populated failure subtrees are rejected before native destruction so owned
    /// render-node wrappers remain usable for cleanup. A successful depth>3 leaf is pruned only
    /// when30213A90 permits it and its parent is absent from the native exemption list. The
    /// caller still adds its returned count even when this operation prunes the native child.
    /// </summary>
    internal void CompleteMainCellObjectChild(nint parent, int octant, nint child, int returnedCount)
    {
        using var operation = Enter();
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)octant, 8u);
        ArgumentOutOfRangeException.ThrowIfNegative(returnedCount);
        var parentIdentity = ValidateCellObjectNode(parent);
        var identity = ValidateCellObjectNode(child);
        Require(identity.Parent == parent && identity.Octant == octant &&
            Marshal.ReadIntPtr(parent + 0x20 + octant * 8) == child,
            "The completed child is no longer the parent's captured octant.");
        if (returnedCount != 0)
        {
            if (parentIdentity.Depth + 1 <= 3 ||
                Native(() => Terrain<ByteCall>(0x213a90)(child)) == 0 ||
                CellObjectParentPreventsPruning(parent)) return;
        }

        //30213A90 is leaf-only, not recursive emptiness. The separate failure guard permits
        // empty fixed depth1/2 hierarchies, but never deletes registered native resources behind
        // the collection's retained wrappers. It runs fully before the first destructive call.
        var retiredIdentities = CollectResourceFreeCellObjectSubtree(child, identity);
        Native(() =>
        {
            Terrain<VoidCall>(0x21a730)(child);
            Terrain<VoidCall>(0x26b87e)(child);
            Marshal.WriteIntPtr(parent + 0x20 + octant * 8, 0);
            foreach (var pointer in retiredIdentities) _cellObjectNodes.Remove(pointer);
            ValidateCellObjectNode(parent);
        });
    }

    private CellObjectNodeAllocation PrepareCellObjectNodeAllocation()
    {
        Require(_pendingCellObjectNode is null, "A previous native octree allocation remains unresolved.");
        var allocation = new CellObjectNodeAllocation();
        allocation.Bounds = Marshal.AllocHGlobal(24);
        _pendingCellObjectNode = allocation;
        return allocation;
    }

    private void CompleteCellObjectNodeAllocation(CellObjectNodeAllocation allocation)
    {
        // The native constructor copied all24 bounds bytes. Native root/parent publication now
        // owns the node; freeing this call scratch does not retire its native tree allocation.
        Marshal.FreeHGlobal(allocation.Bounds);
        allocation.Bounds = allocation.Pointer = 0;
        _pendingCellObjectNode = null;
    }

    private void ValidateCellObjectRoot()
    {
        if (_cellObjectRoot == 0)
            throw new InvalidOperationException("Initialize the engine's outdoor octree before loading cell objects.");
        Require(Marshal.ReadIntPtr(_lease.ThreeDEngine + 0x5dc0) == _cellObjectRoot,
            "The engine-owned outdoor octree changed outside this cell-object service.");
        CheckCellObjectNodeFields(_cellObjectRoot, 0, 0, 0);
        ValidateCellObjectRootList(_cellObjectRoot);
    }

    private void ValidateCellObjectRootList(nint root)
    {
        var begin = Marshal.ReadIntPtr(_lease.ThreeDEngine + 0x5dc8);
        var count = Marshal.ReadInt32(_lease.ThreeDEngine + 0x5dd0);
        var capacity = Marshal.ReadInt32(_lease.ThreeDEngine + 0x5dd4);
        Require(count >= 0 && capacity >= count && capacity <= int.MaxValue / 8 &&
            (capacity == 0 || begin != 0), "The native engine octree root list is invalid.");
        if (root == 0) return;
        var matches = 0;
        for (var i = 0; i < count; i++)
            if (Marshal.ReadIntPtr(begin + i * 8) == root) matches++;
        Require(matches == 1, "The captured outdoor root must occur exactly once in the engine root list.");
    }

    private CellObjectNodeIdentity ValidateCellObjectNode(nint node)
    {
        ValidateCellObjectRoot();
        if (!_cellObjectNodes.TryGetValue(node, out var identity))
            throw new InvalidOperationException("Cell-object traversal requires a live node captured by this service.");
        // Validate the path from the root before dereferencing a potentially retired child.
        if (identity.Parent != 0)
        {
            ValidateCellObjectNode(identity.Parent);
            Require(Marshal.ReadIntPtr(identity.Parent + 0x20 + identity.Octant * 8) == node,
                "The captured octree child was removed or replaced outside this owner.");
        }
        CheckCellObjectNodeFields(node, identity.Parent, identity.Octant, identity.Depth);
        return identity;
    }

    private void CaptureCellObjectChild(nint parent, int octant, nint child, int depth)
    {
        CheckCellObjectNodeFields(child, parent, octant, depth);
        var identity = new CellObjectNodeIdentity(parent, octant, depth);
        if (_cellObjectNodes.TryGetValue(child, out var previous))
            Require(previous == identity, "An octree child pointer aliases a different captured node.");
        else _cellObjectNodes.Add(child, identity);
    }

    private void CheckCellObjectNodeFields(nint node, nint parent, int octant, int depth)
    {
        Require(node != 0 && Marshal.ReadIntPtr(node) == 0 &&
            Marshal.ReadIntPtr(node + 0x18) == parent && Marshal.ReadInt32(node + 0x60) == octant &&
            Marshal.ReadInt32(node + 0x64) == depth && Marshal.ReadInt32(node + 0x1ac) == 0,
            "The native octree node no longer matches its outdoor parent/depth/segment identity.");
    }

    private bool CellObjectParentPreventsPruning(nint parent)
    {
        var begin = Marshal.ReadIntPtr(_lease.TerrainModule + 0x2fd288);
        var count = Marshal.ReadInt32(_lease.TerrainModule + 0x2fd290);
        Require(count >= 0 && count <= int.MaxValue / 8 && (count == 0 || begin != 0),
            "The native octree pruning exemption list is invalid.");
        for (var i = 0; i < count; i++)
            if (Marshal.ReadIntPtr(begin + i * 8) == parent) return true;
        return false;
    }

    private HashSet<nint> CollectResourceFreeCellObjectSubtree(nint node, CellObjectNodeIdentity identity)
    {
        var found = new HashSet<nint>();
        var pending = new Stack<(nint Pointer, CellObjectNodeIdentity Identity)>();
        pending.Push((node, identity));
        while (pending.TryPop(out var entry))
        {
            Require(found.Add(entry.Pointer) && entry.Identity.Depth <= 64,
                "The native octree subtree contains a cycle or exceeds the admitted depth.");
            CheckCellObjectNodeFields(entry.Pointer, entry.Identity.Parent,
                entry.Identity.Octant, entry.Identity.Depth);
            for (var offset = 0x70; offset <= 0xa0; offset += 0x10)
                if (Marshal.ReadIntPtr(entry.Pointer + offset) != 0)
                    throw new InvalidOperationException("Native subtree deletion requires its registered render-node owners to retire first; the loaded collection remains retained.");
            // Destructor3021A730 invokes external service/release hooks for these two fields.
            // They are zero in the admitted constructor/loading phase; no producer for either
            // hook is owned here. Reject them instead of assuming their callbacks are harmless.
            if (Marshal.ReadIntPtr(entry.Pointer + 0x1e0) != 0 ||
                Marshal.ReadIntPtr(entry.Pointer + 0x1f8) != 0)
                throw new NotSupportedException("Native subtree deletion requires the owner of its additional render/service resources.");
            for (var octant = 0; octant < 8; octant++)
            {
                var child = Marshal.ReadIntPtr(entry.Pointer + 0x20 + octant * 8);
                if (child != 0)
                    pending.Push((child, new(entry.Pointer, octant, entry.Identity.Depth + 1)));
            }
        }
        return found;
    }

    private sealed class CellObjectNodeAllocation
    {
        internal nint Pointer, Bounds;
    }
    private readonly record struct CellObjectNodeIdentity(nint Parent, int Octant, int Depth);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint AllocateCellObjectNodeCall(nuint bytes);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint ConstructCellObjectNodeCall(nint allocation, int segment, nint bounds,
        nint visibilityArea, nint parent, int depth, int octant);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint CellObjectChildBoundsCall(nint parent, nint bounds, int octant);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AppendCellObjectRootCall(nint vector, nint rootPointer);
}
