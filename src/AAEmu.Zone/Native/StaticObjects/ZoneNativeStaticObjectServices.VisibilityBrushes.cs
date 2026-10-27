using System.Runtime.InteropServices;

using AAEmu.Zone.Content;

namespace AAEmu.Zone.Native.StaticObjects;

public sealed unsafe partial class ZoneNativeStaticObjectServices
{
    private readonly List<VisibilityBrushOwnership> _importedVisibilityBrushes = [];
    // A managed traversal/allocation bound, not a claimed native container limit.
    private const int MaximumVisibilityOwnedPointers = ZoneCellObjectContent.MaximumFileBytes / 8;

    // Called only inside the owning cell loader's shared engine operation. These pointers come
    // from the real manager's newly published areas, never a public arbitrary-pointer adapter.
    private VisibilityBrushOwnership CaptureImportedVisibilityBrushes(
        VisibilityCellManagerOwnership ownership, IReadOnlyList<nint> exactNewAreas)
    {
        _lease.RequireActiveOperation();
        var batch = new VisibilityBrushOwnership(ownership);
        _importedVisibilityBrushes.Add(batch); // Pin partial native ownership before validation.
        try
        {
            ValidateVisibilityCellManagerOwnership(ownership);
            Require(!_importedVisibilityBrushes.Any(other => !ReferenceEquals(other, batch) &&
                ReferenceEquals(other.Cell, ownership)), "The visibility cell already has imported-node ownership.");
            Require(exactNewAreas.Count <= MaximumVisibilityOwnedPointers,
                "The visibility area snapshot exceeds the managed allocation bound.");
            foreach (var pointer in exactNewAreas) batch.Areas.Add(new ImportedVisibilityArea(pointer));
            var currentAreas = CaptureImportedVisibilityManagerAreas(ownership.Manager.Pointer);
            var seenAreas = new HashSet<nint>();
            var seenNodes = new HashSet<nint>();
            var seenBodies = new HashSet<nint>();
            foreach (var other in _importedVisibilityBrushes)
            {
                if (ReferenceEquals(other, batch)) continue;
                foreach (var area in other.Areas) seenAreas.Add(area.Pointer);
                foreach (var brush in other.Brushes)
                {
                    seenNodes.Add(brush.Pointer);
                    if (brush.PhysicalPointer != 0) seenBodies.Add(brush.PhysicalPointer);
                }
            }
            foreach (var brush in _brushes)
            {
                if (brush.Retired) continue;
                seenNodes.Add(brush.Pointer);
                if (brush.PhysicalPointer != 0) seenBodies.Add(brush.PhysicalPointer);
            }
            foreach (var vegetation in _vegetation)
                if (!vegetation.Retired && vegetation.PhysicalPointer != 0) seenBodies.Add(vegetation.PhysicalPointer);
            foreach (var area in batch.Areas)
            {
                Require(currentAreas.Contains(area.Pointer) && seenAreas.Add(area.Pointer),
                    "An imported visibility area is absent or already belongs to another cell owner.");
                RequireTable(area.Pointer, 0x291e88); // CVisArea constructor 3019D390.
                var vector = ReadImportedVisibilityPointerVector(area.Pointer + 0x10);
                area.VectorPointer = vector.Pointer;
                area.VectorCapacity = vector.Capacity;
                area.Nodes = vector.Items;
                Require(area.Nodes.Length == 0 || Marshal.ReadIntPtr(area.Pointer + 8) != 0,
                    "Imported visibility nodes require the actual owning area octree for native retirement.");
                Require(batch.Brushes.Count <= MaximumVisibilityOwnedPointers - area.Nodes.Length,
                    "The imported brush snapshot exceeds the managed allocation bound.");
                foreach (var node in area.Nodes)
                {
                    Require(node != 0 && seenNodes.Add(node), "Two resources claim the same imported visibility node.");
                    var brush = new ImportedVisibilityBrush(node);
                    batch.Brushes.Add(brush);
                    // Only the actual configured imported CBrush family is admitted here.
                    // Type19 and other node families have different native unload semantics.
                    RequireTable(node, 0x28aaa8);
                    brush.ModelPointer = Marshal.ReadIntPtr(node + 0x58);
                    brush.MaterialPointer = Marshal.ReadIntPtr(node + 0x60);
                    brush.PhysicalPointer = Marshal.ReadIntPtr(node + 0x68);
                    ValidateImportedVisibilityBrush(brush);
                    if (brush.PhysicalPointer != 0)
                        Require(seenBodies.Add(brush.PhysicalPointer), "Two resources claim the same imported static body.");
                }
            }
            batch.Captured = true;
            ValidateImportedVisibilityBatch(batch);
            return batch;
        }
        catch (Exception error)
        {
            // The native manager has already created the nodes. An incomplete snapshot cannot
            // safely claim an empty cell or free resources whose construction may be queued.
            _poisoned = true;
            _lease.Poison(error);
            throw;
        }
    }

    // Root calls this once from CapturePhysicsCompletionBodies under the same shared gate.
    // Model/material references stay owned by CVisArea's CBrush, independently of table refs.
    private void AppendImportedVisibilityPhysicsBodies(HashSet<nint> bodies)
    {
        _lease.RequireActiveOperation();
        foreach (var batch in _importedVisibilityBrushes)
        {
            ValidateImportedVisibilityBatch(batch);
            foreach (var brush in batch.Brushes)
                if (brush.PhysicalPointer != 0)
                    Require(bodies.Add(brush.PhysicalPointer), "Two static resources claim the same imported native physical body.");
        }
    }

    // This preflight is also called before native cell loading. Never flush an unknown owner's
    // node queue just to make a visibility operation appear complete.
    private void RequireImportedVisibilityDeletionQueuesEmpty()
    {
        _lease.RequireActiveOperation();
        foreach (var offset in new[] { 0x528, 0x538, 0x548 })
            if (ReadImportedVisibilityPointerVector(_lease.ThreeDEngine + offset).Items.Length != 0)
            {
                RetirementDiagnostic.VisibilityQueueGuard(this, null, "RequireImportedVisibilityDeletionQueuesEmpty");
                throw new InvalidOperationException("Visibility mutation requires empty native deferred-node queues; an unowned deletion is pending.");
            }
    }

    private void PrepareImportedVisibilityBrushDeletion(VisibilityBrushOwnership batch)
    {
        _lease.RequireActiveOperation();
        ValidateImportedVisibilityBatch(batch);
        if (batch.Prepared) throw new InvalidOperationException("This visibility batch is already prepared for native area deletion.");
        RequireIdleVegetationWorld();
        try { RetirementDiagnostic.QueueSample(this, "at-PrepareImportedVisibilityBrushDeletion"); } catch { }
        try { RequireImportedVisibilityDeletionQueuesEmpty(); }
        catch { RetirementDiagnostic.VisibilityQueueGuard(this, null, "PrepareImportedVisibilityBrushDeletion"); throw; }
        // Admit every body before the first mutation. CompletePendingStaticPhysics must have
        // consumed queued construction; the actual +40 high-reference guard is not weakened.
        foreach (var brush in batch.Brushes)
            if (brush.PhysicalPointer != 0 && unchecked((uint)Marshal.ReadInt32(brush.PhysicalPointer + 0x40)) > 0x1ffff)
                throw new InvalidOperationException("An imported brush has pending native physics ownership; complete construction before visibility retirement.");
        foreach (var brush in batch.Brushes)
        {
            var body = brush.PhysicalPointer;
            if (body == 0) continue;
            ValidateVisibilityCellManagerOwnership(batch.Cell);
            ValidateImportedVisibilityBrush(brush);
            // Native 3010EB00 clears CBrush+68. Ordinary mode 0 is synchronous only in the
            // admitted idle world. Do not hold writer guards around the native destroy call.
            Native(() => Bind<DephysicalizeCall>(brush.Pointer, 0x208, 0x10eb00)(brush.Pointer, 0));
            Require(Marshal.ReadIntPtr(brush.Pointer + 0x68) == 0 &&
                Marshal.ReadIntPtr(body + 0x20) == 0 && Marshal.ReadInt16(body + 0x28) == -1 &&
                Marshal.ReadByte(body + 0x47) == 7,
                "Imported brush physics did not synchronously detach its foreign node owner.");
            try
            {
                using var entities = new NativeGuard(_lease.PhysicsWorld + 0x57dc);
                using var world = new NativeGuard(_lease.PhysicsWorld + 0x57c4);
                using var queries = new NativeGuard(_lease.PhysicsWorld + 0x5804);
                Native(() => _purgeRetiredEvents(_lease.PhysicsWorld));
            }
            catch (Exception error)
            {
                _poisoned = true;
                _lease.Poison(error);
                throw;
            }
            brush.PhysicalPointer = 0;
        }
        ValidateImportedVisibilityBatch(batch);
        RequireImportedVisibilityDeletionQueuesEmpty();
        batch.Prepared = true;
    }

    // Called immediately after the owning manager's real 30197BF0 cell unload, within the SAME
    // operation. The area records are now freed; do not dereference their captured addresses.
    private void CompleteImportedVisibilityBrushDeletion(VisibilityBrushOwnership batch)
    {
        _lease.RequireActiveOperation();
        try { CompleteImportedVisibilityBrushDeletionCore(batch); }
        catch (Exception error)
        {
            // Native area deletion already happened. Even an ordinary managed allocation or
            // admission failure here must retain the batch instead of retrying a freed area.
            _poisoned = true;
            _lease.Poison(error);
            throw;
        }
    }

    private void CompleteImportedVisibilityBrushDeletionCore(VisibilityBrushOwnership batch)
    {
        _lease.RequireActiveOperation();
        ValidateVisibilityCellManagerOwnership(batch.Cell);
        Require(_importedVisibilityBrushes.Contains(batch) && batch.Captured && batch.Prepared && !batch.Retired,
            "Visibility completion requires the exact prepared imported-node batch.");
        var currentAreas = CaptureImportedVisibilityManagerAreas(batch.Cell.Manager.Pointer);
        Require(batch.Areas.All(area => !currentAreas.Contains(area.Pointer)),
            "Native visibility unload left an owned area published; its imported nodes remain retained.");
        var queuedNodes = new HashSet<nint>();
        foreach (var offset in new[] { 0x528, 0x538, 0x548 })
            foreach (var pointer in ReadImportedVisibilityPointerVector(_lease.ThreeDEngine + offset).Items)
                Require(pointer != 0 && queuedNodes.Add(pointer), "The native visibility deletion queue contains a null or duplicate node.");
        Require(queuedNodes.SetEquals(batch.Brushes.Select(brush => brush.Pointer)),
            "Native visibility deletion queued nodes outside the exact prepared cell ownership.");
        foreach (var brush in batch.Brushes)
        {
            RequireTable(brush.Pointer, 0x28aaa8);
            // 30137320 -> 30158140 invokes CBrush+A8=3010EA30, releasing its model and
            // retaining the node in engine+528. A0=301EBC80 is its actual deleting destructor.
            Require(brush.PhysicalPointer == 0 && Marshal.ReadIntPtr(brush.Pointer + 0x68) == 0 &&
                Marshal.ReadIntPtr(brush.Pointer + 0x58) == 0 &&
                Marshal.ReadIntPtr(brush.Pointer + 0x60) == brush.MaterialPointer &&
                (Marshal.ReadByte(brush.Pointer + 0x39) & 1) == 0,
                "A queued imported brush has not completed native resource detachment.");
            _ = Bind<VoidCall>(brush.Pointer, 0xa0, 0x1ebc80);
            _ = Bind<DeleteBrushCall>(brush.Pointer, 0, 0x112470);
        }
        RequireIdleVegetationWorld();
        // Actual 301581F0(flushAll=1) releases all three verified queues in native order. Never
        // write queue counts or directly delete the manager-owned nodes from their borrowed list.
        Native(() => Terrain<VisibilityNodeQueueDrainCall>(0x1581f0)(_lease.ThreeDEngine, 1));
        ValidateVisibilityCellManagerOwnership(batch.Cell);
        foreach (var offset in new[] { 0x528, 0x538, 0x548 })
            Require(ReadImportedVisibilityPointerVector(_lease.ThreeDEngine + offset).Items.Length == 0,
                "Native visibility node retirement left a residual deferred deletion queue.");
        // Node addresses are invalid after the native drain. Only managed ownership changes now.
        batch.Retired = true;
        _importedVisibilityBrushes.Remove(batch);
    }

    private void ValidateImportedVisibilityBatch(VisibilityBrushOwnership batch)
    {
        ValidateVisibilityCellManagerOwnership(batch.Cell);
        Require(_importedVisibilityBrushes.Contains(batch) && batch.Captured && !batch.Retired,
            "The imported visibility batch is incomplete or no longer owned.");
        var currentAreas = CaptureImportedVisibilityManagerAreas(batch.Cell.Manager.Pointer);
        foreach (var area in batch.Areas)
        {
            // Prove current manager membership before dereferencing any captured area pointer.
            Require(currentAreas.Contains(area.Pointer), "An imported visibility area was retired outside its cell owner.");
            RequireTable(area.Pointer, 0x291e88);
            var vector = ReadImportedVisibilityPointerVector(area.Pointer + 0x10);
            Require(vector.Pointer == area.VectorPointer && vector.Capacity == area.VectorCapacity &&
                vector.Items.AsSpan().SequenceEqual(area.Nodes), "An imported visibility ownership vector changed outside its cell owner.");
            Require(area.Nodes.Length == 0 || Marshal.ReadIntPtr(area.Pointer + 8) != 0,
                "The owning visibility area lost its native imported-node octree.");
        }
        foreach (var brush in batch.Brushes) ValidateImportedVisibilityBrush(brush);
    }

    private void ValidateImportedVisibilityBrush(ImportedVisibilityBrush brush)
    {
        RequireTable(brush.Pointer, 0x28aaa8);
        Require(Marshal.ReadIntPtr(brush.Pointer + 0x58) == brush.ModelPointer &&
            Marshal.ReadIntPtr(brush.Pointer + 0x60) == brush.MaterialPointer &&
            Marshal.ReadIntPtr(brush.Pointer + 0x68) == brush.PhysicalPointer &&
            Marshal.ReadInt32(brush.Pointer + 0x44) == 0 && (Marshal.ReadByte(brush.Pointer + 0x39) & 1) != 0,
            "The imported brush changed native references or serialized ownership outside its cell owner.");
        if (brush.ModelPointer != 0) RequireTable(brush.ModelPointer, 0x27ee78);
        if (brush.MaterialPointer != 0) RequireTable(brush.MaterialPointer, 0x297378);
        var body = brush.PhysicalPointer;
        if (body == 0) return;
        Require(Marshal.ReadIntPtr(body) == _lease.PhysicsModule + 0x1efe38 &&
            Native(() => _isPlaceholder(_lease.PhysicsWorld, body)) == 0 &&
            Marshal.ReadIntPtr(body + 0x78) == _lease.PhysicsWorld &&
            Marshal.ReadIntPtr(body + 0x20) == brush.Pointer && Marshal.ReadInt16(body + 0x28) == 1 &&
            Marshal.ReadByte(body + 0x47) == 0,
            "The imported brush no longer owns its exact full class-zero native static body.");
    }

    private HashSet<nint> CaptureImportedVisibilityManagerAreas(nint manager)
    {
        Require(RequireVisibilityManagerPointer() == manager, "The imported visibility manager generation changed.");
        var areas = new HashSet<nint>();
        foreach (var offset in new[] { 0x28, 0x48, 0x58 })
        {
            var vector = ReadImportedVisibilityPointerVector(manager + offset);
            Require(areas.Count <= MaximumVisibilityOwnedPointers - vector.Items.Length,
                "The current visibility manager exceeds the managed traversal bound.");
            foreach (var pointer in vector.Items)
                Require(pointer != 0 && areas.Add(pointer), "The native visibility manager has a null or duplicate primary area.");
        }
        return areas;
    }

    private (nint Pointer, int Capacity, nint[] Items) ReadImportedVisibilityPointerVector(nint address)
    {
        var pointer = Marshal.ReadIntPtr(address);
        var count = Marshal.ReadInt32(address + 8);
        var capacity = Marshal.ReadInt32(address + 12);
        Require(count >= 0 && capacity >= count && capacity <= MaximumVisibilityOwnedPointers &&
            (capacity == 0 ? pointer == 0 : pointer != 0), "A native visibility pointer vector has invalid storage or exceeds the managed bound.");
        var items = new nint[count];
        if (count != 0) Marshal.Copy(pointer, items, 0, count);
        return (pointer, capacity, items);
    }

    internal sealed class VisibilityBrushOwnership(VisibilityCellManagerOwnership cell)
    {
        internal readonly VisibilityCellManagerOwnership Cell = cell;
        internal readonly List<ImportedVisibilityArea> Areas = [];
        internal readonly List<ImportedVisibilityBrush> Brushes = [];
        internal bool Captured, Prepared, Retired;
        internal int ImportedBrushCount => Brushes.Count;
        internal int PhysicalBodyCount => Brushes.Count(brush => brush.PhysicalPointer != 0);
    }

    internal sealed class ImportedVisibilityArea(nint pointer)
    {
        internal readonly nint Pointer = pointer;
        internal nint VectorPointer;
        internal int VectorCapacity;
        internal nint[] Nodes = [];
    }

    internal sealed class ImportedVisibilityBrush(nint pointer)
    {
        internal readonly nint Pointer = pointer;
        internal nint ModelPointer, MaterialPointer, PhysicalPointer;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VisibilityNodeQueueDrainCall(nint engine, byte flushAll);
}
