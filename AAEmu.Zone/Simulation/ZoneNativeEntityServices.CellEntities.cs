using System.Numerics;
using System.Runtime.InteropServices;
using AAEmu.Zone.Content;

namespace AAEmu.Zone.Simulation;

public enum ZoneCellEntityBatchStatus { Created, NativeLoadingDisabled, Loaded, Failed, Retired }

/// <summary>Actual native batch results. A native skip has no inferred reason or replacement entity.</summary>
public sealed record ZoneCellEntityBatchResult(int AuthoredEntityCount, int NativeLoadedCount,
    int NativeSkippedCount, int OwnedEntityCount, int DirectProxyBodyCount);

public sealed unsafe partial class ZoneNativeEntityServices
{
    private readonly List<CellEntityBatch> _cellEntityBatches = [];
    internal ZoneNativeEngine Engine => _lease.Engine;
    internal void VerifyResourceOwner() { ObjectDisposedException.ThrowIf(_disposed, this); _lease.ThrowIfFaulted(); }

    /// <summary>
    /// Reserves one authored-world/runtime-cell generation before loading XML or invoking scripts.
    /// The caller retains this owner before Load. No Default-class substitution is performed.
    /// </summary>
    public CellEntityBatch CreateCellEntityBatch(ZoneWorldMapContent world, ZoneWorldMapCellPlacement placement)
    {
        using var operation = Enter();
        ArgumentNullException.ThrowIfNull(world); ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(placement.Cell);
        var coordinate = placement.Cell.Coordinate;
        if (!ReferenceEquals(world.GetCell(coordinate.X, coordinate.Y), placement.Cell))
            throw new ArgumentException("The entity cell belongs to another world.", nameof(placement));
        if ((uint)placement.EngineCell.X >= 8 || (uint)placement.EngineCell.Y >= 8)
            throw new ArgumentOutOfRangeException(nameof(placement), "The entity cell is outside the native eight-cell window.");
        if (_cellEntityBatches.Any(batch => !batch.Retired && batch.Placement.EngineCell == placement.EngineCell))
            throw new InvalidOperationException("This engine cell already has a retained XML entity batch.");
        var batch = new CellEntityBatch(this, world, placement);
        _cellEntityBatches.Add(batch);
        return batch;
    }

    private ZoneCellEntityBatchResult LoadCellEntityBatch(CellEntityBatch batch, ZoneCellEntityContent content)
    {
        using var operation = Enter();
        ValidateCellEntityBatch(batch);
        ArgumentNullException.ThrowIfNull(content);
        if (batch.LoadAttempted) throw new InvalidOperationException("The native cell entity batch is single-use.");
        if (content.Entities.Count > ZoneCellEntityContent.NativeCapturedEntityCapacity)
            throw new NotSupportedException("The authored entity batch exceeds the native caller's 4096 captured-pointer capacity.");
        batch.LoadAttempted = true;
        batch.Content = content;
        var configuration = Marshal.ReadIntPtr(_lease.TerrainModule + 0x2fa8d0);
        Require(configuration != 0, "The real terrain entity-loading configuration is absent.");
        if (Marshal.ReadInt32(configuration + 0x174) == 0) // Native3015EEC0 early gate.
        {
            batch.Status = ZoneCellEntityBatchStatus.NativeLoadingDisabled;
            return batch.Result = new(content.Entities.Count, 0, content.Entities.Count, 0, 0);
        }
        RequireCellEntityIdleState();
        RequireCellEntityDeletionQueueEmpty();
        batch.Baseline = SnapshotCellEntityRegistrations();
        if (batch.Baseline.Values.Any(entity => EntityBelongsToCell(entity.Pointer, batch.Placement.EngineCell)))
            throw new InvalidOperationException("The native cell already contains entities outside this batch owner.");
        try
        {
            // Retain the actual physics baseline before any class/script or entity callback.
            CreateCellPhysicsState(batch);
            // Class lookup/script admission uses the real registry and preserves all authored dependencies.
            AcquireCellEntityClasses(content, out batch.Classes);
            CaptureCellEntityChanges(batch);
            ValidateCellEntityClasses(batch.Classes);
            batch.XmlBytes = CellEntityAllocate(content.RawDocument);
            batch.XmlHandles = CellEntityAllocate(new byte[16]);
            batch.OutputPointers = CellEntityAllocate(new byte[ZoneCellEntityContent.NativeCapturedEntityCapacity * 8]);
            batch.Event = CellEntityAllocate(new byte[0x38]);
            Marshal.WriteInt32(batch.Event, 0x1f);
            // CSystem+390/365C8F60 -> XmlUtils+18/365901A0 -> actual binary/text parser365954A0.
            var parse = BindCellSystem<ParseCellXmlCall>(_lease.System, 0x390, 0xc8f60);
            Native(() => parse(_lease.System, batch.XmlHandles, batch.XmlBytes, content.RawDocument.Length));
            var root = Marshal.ReadIntPtr(batch.XmlHandles);
            if (root == 0) throw new InvalidDataException("The real CSystem XML parser rejected the entity document.");
            WithCellEntityUtf8("Objects", name => Native(() => Virtual<FindCellXmlCall>(root, 0x130)(root, batch.XmlHandles + 8, name)));
            var objects = Marshal.ReadIntPtr(batch.XmlHandles + 8);
            Require((objects != 0) == (content.Objects is not null), "The native XML Objects selection differs from the admitted document.");
            var loaded = 0;
            if (objects != 0)
            {
                var count = Native(() => Virtual<CellXmlCountCall>(objects, 0x120)(objects));
                Require(count == content.Objects!.Children.Count, "The native XML child count differs from the admitted document.");
                WithCellEntityUtf8("cellX", name => Native(() => { Virtual<SetCellXmlIntegerCall>(objects, 0x1f0)(objects, name, batch.Placement.EngineCell.X); return 0; }));
                WithCellEntityUtf8("cellY", name => Native(() => { Virtual<SetCellXmlIntegerCall>(objects, 0x1f0)(objects, name, batch.Placement.EngineCell.Y); return 0; }));
                BeginCellEntityClassLoads(batch.Classes);
                batch.ClassLoadInterval = true;
                batch.NativeLoadStarted = true;
                // The actual local load-manager destructor binds GUID parents, then FlowGraph/Area proxies.
                loaded = Native(() => Bind<LoadCellEntitiesCall>(_lease.EntitySystem, 0x120, 0x1c750)(
                    _lease.EntitySystem, batch.XmlHandles + 8, 1, batch.OutputPointers, ZoneCellEntityContent.NativeCapturedEntityCapacity));
                CaptureCellEntityChanges(batch);
                CompleteCellEntityClassLoads(batch.Classes);
                batch.ClassLoadInterval = false;
                Require(loaded >= 0 && loaded <= content.Entities.Count && loaded <= ZoneCellEntityContent.NativeCapturedEntityCapacity,
                    "The native entity batch returned an invalid captured count.");
                var output = new List<CellEntityRegistration>(loaded);
                var liveByPointer = SnapshotCellEntityRegistrations().Values
                    .Where(entity => batch.Owned.ContainsKey(entity.Id)).ToDictionary(entity => entity.Pointer);
                for (var index = 0; index < loaded; index++)
                {
                    var pointer = Marshal.ReadIntPtr(batch.OutputPointers + index * 8);
                    Require(pointer != 0 && liveByPointer.ContainsKey(pointer),
                        "A returned native entity is absent or belongs to a pre-existing registration.");
                    output.Add(liveByPointer[pointer]);
                }
                Require(output.Select(entity => entity.Id).Distinct().Count() == output.Count,
                    "The native batch returned one entity more than once.");
                batch.LoadedOrder = output.ToArray();
            }
            // Native3015EEC0 runs this global area completion even when Objects is absent.
            BeginCellEntityClassLoads(batch.Classes);
            batch.ClassLoadInterval = true;
            Native(() => { Bind<CellEntityVoidCall>(_lease.EntitySystem, 0x210, 0x1c8e0)(_lease.EntitySystem); return 0; });
            CaptureCellEntityChanges(batch);
            SendCellEntityLoadedEvents(batch);
            CaptureCellEntityChanges(batch);
            CompleteCellEntityClassLoads(batch.Classes);
            batch.ClassLoadInterval = false;
            ValidateCellEntityClasses(batch.Classes);
            var bodies = CaptureCellEntityDirectBodies(batch);
            batch.Status = ZoneCellEntityBatchStatus.Loaded;
            return batch.Result = new(content.Entities.Count, loaded, content.Entities.Count - loaded, batch.Owned.Count, bodies.Count);
        }
        catch (Exception error)
        {
            batch.Status = ZoneCellEntityBatchStatus.Failed;
            // Native invocation faults retain all roots without dereferencing uncertain output.
            // A normally returned managed admission failure can still have created native children.
            if (!_poisoned && batch.Baseline is not null)
            {
                try
                {
                    CaptureCellEntityChanges(batch);
                    if (batch.ClassLoadInterval)
                    {
                        CompleteCellEntityClassLoads(batch.Classes!);
                        batch.ClassLoadInterval = false;
                    }
                }
                catch (Exception capture) { throw new AggregateException(error, capture); }
            }
            throw;
        }
    }

    private void SendCellEntityLoadedEvents(CellEntityBatch batch)
    {
        // Match3015EEC0's swap-removal order, not a stable partition of the authored rows.
        var remaining = batch.LoadedOrder.ToList();
        for (var index = 0; index < remaining.Count;)
        {
            var entity = remaining[index];
            if (!CellEntityIsCurrent(entity))
            {
                remaining[index] = remaining[^1]; remaining.RemoveAt(remaining.Count - 1);
                continue;
            }
            var entityClass = Marshal.ReadIntPtr(entity.Pointer + 0x30);
            Require(entityClass == entity.ClassPointer, "The native entity class changed before its load event.");
            var className = ReadCellClassString(Marshal.ReadIntPtr(entityClass + 0x10));
            if (ZoneCellEntityValues.NameEquals(className, "Constraint")) { index++; continue; }
            SendCellEntityLoadedEvent(batch, entity);
            remaining[index] = remaining[^1]; remaining.RemoveAt(remaining.Count - 1);
        }
        foreach (var entity in remaining)
            if (CellEntityIsCurrent(entity)) SendCellEntityLoadedEvent(batch, entity);
    }

    private void SendCellEntityLoadedEvent(CellEntityBatch batch, CellEntityRegistration entity)
    {
        Native(() => Bind<CellEntityEventCall>(entity.Pointer, 0x178, 0x142e0)(entity.Pointer, batch.Event));
        CaptureCellEntityChanges(batch); // Capture script-created children and exact retirement after every callback.
    }

    private Dictionary<uint, CellEntityRegistration> SnapshotCellEntityRegistrations()
    {
        _lease.RequireActiveOperation();
        RequireTable(_lease.EntitySystem, 0xcfd48);
        var slots = Marshal.ReadIntPtr(_lease.EntitySystem + 0x28);
        var last = unchecked((ushort)Marshal.ReadInt16(_lease.EntitySystem + 0x4012e));
        Require(slots != 0, "The native entity slot table is absent.");
        var result = new Dictionary<uint, CellEntityRegistration>();
        var pointers = new HashSet<nint>();
        // Exact bounds and slot array from native iterator320238F0 and cell unload3201C1E0.
        for (var index = 0; index <= last; index++)
        {
            var pointer = Marshal.ReadIntPtr(slots + index * 8);
            if (pointer == 0) continue;
            RequireTable(pointer, 0xcd998);
            var id = unchecked((uint)Marshal.ReadInt32(pointer + 0x14));
            Require(id != 0 && (id & 0xffff) == (uint)index && pointers.Add(pointer) &&
                Native(() => _lookup(_lease.EntitySystem, id)) == pointer,
                "The native entity slot has an inconsistent full identity.");
            result.Add(id, new(id, pointer, Marshal.ReadIntPtr(pointer + 0x30)));
        }
        return result;
    }

    private void CaptureCellEntityChanges(CellEntityBatch batch)
    {
        var current = SnapshotCellEntityRegistrations();
        // The single engine operation owns this mutation interval. Pre-existing/sibling registrations
        // are never adopted by numeric ID and may not disappear under this batch's callbacks.
        foreach (var before in batch.Baseline!.Values)
            Require(current.TryGetValue(before.Id, out var value) && value.Pointer == before.Pointer,
                "Cell loading changed a pre-existing native entity registration.");
        foreach (var value in current.Values)
        {
            if (batch.Baseline.ContainsKey(value.Id)) continue;
            if (batch.Owned.TryGetValue(value.Id, out var old))
                Require(old.Pointer == value.Pointer && old.ClassPointer == value.ClassPointer,
                    "A captured cell entity registration was replaced.");
            else batch.Owned.Add(value.Id, value); // Root before subsequent identity/proxy checks.
        }
        CaptureCellPhysics(batch);
    }

    private bool CellEntityIsCurrent(CellEntityRegistration entity)
    {
        var current = Native(() => _lookup(_lease.EntitySystem, entity.Id));
        if (current == 0) return false;
        Require(current == entity.Pointer, "A native cell entity full ID resolves to a replacement.");
        RequireTable(current, 0xcd998);
        Require(Marshal.ReadIntPtr(current + 0x30) == entity.ClassPointer, "The native entity class generation changed.");
        return true;
    }

    private HashSet<nint> CaptureCellEntityDirectBodies(CellEntityBatch batch)
    {
        var bodies = new HashSet<nint>();
        foreach (var entity in batch.Owned.Values)
        {
            if (!CellEntityIsCurrent(entity)) continue;
            var body = Native(() => Bind<PointerCall>(entity.Pointer, 0x218, 0xe450)(entity.Pointer));
            if (body != 0) bodies.Add(body);
        }
        // This is direct IEntity-proxy reporting. It is not a claim that skeleton, particle,
        // area or scripted physical resources are included in static brush completion.
        return bodies;
    }

    private void RetireCellEntityBatch(CellEntityBatch batch)
    {
        if (batch.Retired) return;
        ValidateCellEntityBatch(batch);
        if (batch.Owned.Count == 0 && !batch.NativeLoadStarted)
        {
            if (batch.Physics is not null)
            {
                PrepareCellPhysicsRetirement(batch);
                CompleteCellPhysicsRetirement(batch);
            }
            CompleteCellEntityRetirement(batch);
            return;
        }
        RequireCellEntityIdleState();
        PrepareCellPhysicsRetirement(batch);
        var current = SnapshotCellEntityRegistrations();
        var live = batch.Owned.Values.Where(CellEntityIsCurrent).ToArray();
        var ownedIds = live.Select(entity => entity.Id).ToHashSet();
        foreach (var entity in current.Values)
            if (EntityBelongsToCell(entity.Pointer, batch.Placement.EngineCell))
                Require(ownedIds.Contains(entity.Id), "Native cell unload would remove an entity outside the retained batch.");
        var existingQueue = SnapshotCellEntityDeletionQueue();
        Require(existingQueue.IsSubsetOf(live.Select(entity => entity.Pointer).ToHashSet()),
            "Native entity deletion contains another owner's registration.");
        foreach (var entity in live)
            Require(Marshal.ReadInt32(entity.Pointer + 0xd8) == 0 &&
                (Marshal.ReadInt32(entity.Pointer + 0x20) & 4) == 0 &&
                ((Marshal.ReadInt32(entity.Pointer + 0x10) & 0x20) == 0 || existingQueue.Contains(entity.Pointer)),
                "Cell entity retirement has an external keep-alive or an unowned deferred removal.");
        if (live.Length != 0)
        {
            // Native+10 ignores the outer caller's additional flag and invokes Remove(id,false).
            Native(() => { Bind<UnloadCellEntitiesCall>(_lease.EntitySystem, 0x10, 0x1c1e0)(
                _lease.EntitySystem, batch.Placement.EngineCell.X, batch.Placement.EngineCell.Y); return 0; });
            // Script-created children may carry no cell assignment; their exact registrations still
            // belong to this batch. Queue those remaining children individually through real Remove.
            foreach (var entity in live)
                if (CellEntityIsCurrent(entity) && (Marshal.ReadInt32(entity.Pointer + 0x10) & 0x20) == 0)
                    Native(() => { _remove(_lease.EntitySystem, entity.Id, 0); return 0; });
            var queued = SnapshotCellEntityDeletionQueue();
            Require(queued.SetEquals(live.Select(entity => entity.Pointer)),
                "Native cell removal was vetoed or queued entities outside this exact batch.");
            // Actual blocking native wrapper3201F960 ->3201F590(0,INT_MAX), not EntitySystem Update.
            Native(() => { Marshal.GetDelegateForFunctionPointer<CellEntityVoidCall>(
                _lease.EntityModule + 0x1f960)(_lease.EntitySystem); return 0; });
            RequireCellEntityDeletionQueueEmpty();
            foreach (var entity in live)
                Require(Native(() => _lookup(_lease.EntitySystem, entity.Id)) == 0,
                    "Native cell entity shutdown did not retire the exact full ID.");
            var after = SnapshotCellEntityRegistrations();
            Require(after.Count == current.Count - live.Length && current.Values.Where(entity => !ownedIds.Contains(entity.Id))
                .All(entity => after.TryGetValue(entity.Id, out var retained) && retained.Pointer == entity.Pointer),
                "Native cell entity retirement changed another owner or created untracked registrations.");
        }
        CompleteCellPhysicsRetirement(batch);
        CompleteCellEntityRetirement(batch);
    }

    private void CompleteCellEntityRetirement(CellEntityBatch batch)
    {
        if (batch.Classes is not null) { ReleaseCellEntityClasses(batch.Classes); batch.Classes = null; }
        ReleaseCellEntityXml(batch.XmlHandles, 8); ReleaseCellEntityXml(batch.XmlHandles, 0);
        CellEntityFree(ref batch.XmlHandles); CellEntityFree(ref batch.XmlBytes);
        CellEntityFree(ref batch.OutputPointers); CellEntityFree(ref batch.Event);
        batch.Retired = true; batch.Status = ZoneCellEntityBatchStatus.Retired;
        _cellEntityBatches.Remove(batch);
    }

    private HashSet<nint> SnapshotCellEntityDeletionQueue()
    {
        var sentinel = Marshal.ReadIntPtr(_lease.EntitySystem + 0x48);
        var count = Marshal.ReadInt64(_lease.EntitySystem + 0x50);
        Require(sentinel != 0 && count >= 0 && count <= 65536, "The native entity deletion list is invalid.");
        var result = new HashSet<nint>();
        var nodes = new HashSet<nint>();
        var previous = sentinel;
        var node = Marshal.ReadIntPtr(sentinel);
        for (long index = 0; index < count; index++)
        {
            Require(node != 0 && node != sentinel && nodes.Add(node) && Marshal.ReadIntPtr(node + 8) == previous,
                "The native entity deletion list linkage changed.");
            var entity = Marshal.ReadIntPtr(node + 0x10);
            Require(entity != 0 && result.Add(entity), "The native deletion list has a null or repeated entity.");
            previous = node; node = Marshal.ReadIntPtr(node);
        }
        Require(node == sentinel && Marshal.ReadIntPtr(sentinel + 8) == previous,
            "The native entity deletion list count does not match its links.");
        return result;
    }

    private void RequireCellEntityDeletionQueueEmpty()
    {
        if (SnapshotCellEntityDeletionQueue().Count != 0)
            throw new InvalidOperationException("The engine has native entity deletions outside this batch operation.");
    }

    private void RequireCellEntityIdleState()
    {
        _lease.RequireActiveOperation();
        var physics = Marshal.ReadIntPtr(_lease.Environment + 0x70);
        Require(physics != 0 && Marshal.ReadIntPtr(physics) == _lease.PhysicsModule + 0x1f0af8 &&
            Marshal.ReadIntPtr(_lease.PhysicsModule + 0x37c6d8) == _lease.Environment,
            "Cell entity resources require this engine's actual physical world.");
        if (Marshal.ReadInt32(physics + 0x5798) != 0 || Marshal.ReadByte(_lease.EntitySystem + 0x407cc) != 0)
            throw new NotSupportedException("Cell entity resources require the idle owner-thread engine with no native workers or entity update lock.");
    }

    private static bool EntityBelongsToCell(nint entity, ZoneMapCoordinate cell) =>
        Marshal.ReadInt32(entity + 0xc0) == cell.X && Marshal.ReadInt32(entity + 0xc4) == cell.Y;
    private void ValidateCellEntityBatch(CellEntityBatch batch)
    {
        if (!ReferenceEquals(batch.Owner, this) || batch.Retired || !_cellEntityBatches.Contains(batch))
            throw new ObjectDisposedException(nameof(CellEntityBatch));
        _lease.RequireActiveOperation();
    }
    // Root's service Dispose calls this while already holding the shared engine operation.
    private void RetireCellEntityBatches() { foreach (var batch in _cellEntityBatches.ToArray()) RetireCellEntityBatch(batch); }
    private T BindCellSystem<T>(nint pointer, int slot, int relativeAddress) where T : Delegate
    {
        var target = Marshal.ReadIntPtr(Marshal.ReadIntPtr(pointer) + slot);
        Require(target == _lease.SystemModule + relativeAddress, "The real CSystem XML API identity changed.");
        return Marshal.GetDelegateForFunctionPointer<T>(target);
    }
    private static nint CellEntityAllocate(ReadOnlySpan<byte> bytes)
    {
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        bytes.CopyTo(new Span<byte>((void*)pointer, bytes.Length));
        return pointer;
    }
    private static void CellEntityFree(ref nint pointer) { if (pointer != 0) { Marshal.FreeHGlobal(pointer); pointer = 0; } }
    private void ReleaseCellEntityXml(nint handles, int offset)
    {
        if (handles == 0) return;
        var pointer = Marshal.ReadIntPtr(handles + offset);
        if (pointer == 0) return;
        Native(() => { Virtual<CellEntityVoidCall>(pointer, 8)(pointer); Marshal.WriteIntPtr(handles + offset, 0); return 0; });
    }
    private static void WithCellEntityUtf8(string value, Action<nint> action)
    {
        var pointer = Marshal.StringToCoTaskMemUTF8(value);
        try { action(pointer); } finally { Marshal.FreeCoTaskMem(pointer); }
    }

    internal sealed record CellEntityRegistration(uint Id, nint Pointer, nint ClassPointer);
    public sealed class CellEntityBatch : IDisposable
    {
        internal readonly ZoneNativeEntityServices Owner;
        public ZoneWorldMapContent World { get; }
        public ZoneWorldMapCellPlacement Placement { get; }
        public ZoneCellEntityContent? Content { get; internal set; }
        public ZoneCellEntityBatchStatus Status { get; internal set; }
        public ZoneCellEntityBatchResult? Result { get; internal set; }
        public bool IsDisposed => Retired;
        internal bool Retired, LoadAttempted, NativeLoadStarted, ClassLoadInterval;
        internal CellEntityClasses? Classes;
        internal CellPhysicsState? Physics;
        internal nint XmlBytes, XmlHandles, OutputPointers, Event;
        internal Dictionary<uint, CellEntityRegistration>? Baseline;
        internal readonly Dictionary<uint, CellEntityRegistration> Owned = [];
        internal CellEntityRegistration[] LoadedOrder = [];
        internal CellEntityBatch(ZoneNativeEntityServices owner, ZoneWorldMapContent world, ZoneWorldMapCellPlacement placement)
        { Owner = owner; World = world; Placement = placement; }
        public ZoneCellEntityBatchResult Load(ZoneCellEntityContent content) => Owner.LoadCellEntityBatch(this, content);
        /// <summary>
        /// Completes the engine's loading queue using its retained cell batches and static-resource
        /// owners. Returns the total completed requests; this is not an actor simulation frame.
        /// </summary>
        public int CompletePendingPhysics() => Owner.CompletePendingCellPhysics(this);
        public void Dispose() { if (Retired) return; using var operation = Owner.Enter(); Owner.RetireCellEntityBatch(this); }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint ParseCellXmlCall(nint system, nint result, nint bytes, int byteCount);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint FindCellXmlCall(nint node, nint result, nint name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CellXmlCountCall(nint node);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetCellXmlIntegerCall(nint node, nint name, int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int LoadCellEntitiesCall(nint system, nint xmlRef, byte loadProgress, nint entities, int capacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void UnloadCellEntitiesCall(nint system, int cellX, int cellY);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte CellEntityEventCall(nint entity, nint entityEvent);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CellEntityVoidCall(nint pointer);
}
