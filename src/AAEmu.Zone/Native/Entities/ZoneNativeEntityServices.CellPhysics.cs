using System.Runtime.InteropServices;
using System.Diagnostics.CodeAnalysis;

namespace AAEmu.Zone.Native.Entities;

public sealed unsafe partial class ZoneNativeEntityServices
{
    private void RequireCellPhysics([DoesNotReturnIf(false)] bool condition, string message) =>
        Require(condition, message);

    // Retained by the batch before any native callback. Full-body allocations survive native
    // mode-zero destruction on the world's delayed-free list until a later simulation frame.
    internal sealed class CellPhysicsState
    {
        internal nint World;
        internal Dictionary<nint, CellPhysicsBody>? Baseline;
        internal Dictionary<nint, CellPhysicsBody>? RetirementBaseline;
        internal readonly Dictionary<nint, CellPhysicsBody> Owned = [];
        internal readonly List<nint> RetainedIterators = [];
        internal bool Retired;
    }

    internal sealed record CellPhysicsBody(nint Pointer, nint Table, uint Identity,
        nint Foreign, short ForeignKind, byte SimulationClass);

    private void CreateCellPhysicsState(CellEntityBatch batch)
    {
        RequireCellPhysics(batch.Physics is null, "The cell physics ownership interval has already started.");
        var state = new CellPhysicsState();
        batch.Physics = state;
        state.World = Marshal.ReadIntPtr(_lease.Environment + 0x70);
        _lease.Engine.RequireCellEntityPhysicsCaptureState(this);
        state.Baseline = SnapshotCellPhysicsBodies(state);
    }

    private Dictionary<nint, CellPhysicsBody> SnapshotCellPhysicsBodies(CellPhysicsState state)
    {
        _lease.RequireActiveOperation();
        RequireCellPhysics(state.World != 0 && Marshal.ReadIntPtr(_lease.Environment + 0x70) == state.World &&
            Marshal.ReadIntPtr(state.World) == _lease.PhysicsModule + 0x1f0af8,
            "The cell's physical world generation changed.");
        _lease.Engine.RequireCellEntityPhysicsCaptureState(this);
        var table = Marshal.ReadIntPtr(state.World);
        RequireCellPhysics(Marshal.ReadIntPtr(table + 0x188) == _lease.PhysicsModule + 0x7db70,
            "The native physical-entity iterator factory changed.");
        var iterator = Native(() => Marshal.GetDelegateForFunctionPointer<CellPhysicsPointerCall>(
            _lease.PhysicsModule + 0x7db70)(state.World));
        if (iterator != 0) state.RetainedIterators.Add(iterator);
        RequireCellPhysics(iterator != 0, "The native physical-entity iterator allocation failed.");
        // Actual native caller pattern: factory's initial reference is zero, then AddRef/Release.
        // The batch retains it before validation. An uncertain native call pins the iterator
        // and world generation; never make a second native call while unwinding that fault.
        var held = false;
        Exception? enumerationFailure = null;
        try
        {
            RequireCellPhysics(Marshal.ReadIntPtr(iterator) == _lease.PhysicsModule + 0x1f0798,
                "The physical-entity iterator implementation changed.");
            Native(() => { Marshal.GetDelegateForFunctionPointer<CellPhysicsVoidCall>(
                _lease.PhysicsModule + 0x96070)(iterator); return 0; });
            held = true;
            var result = new Dictionary<nint, CellPhysicsBody>();
            var pointer = Native(() => Marshal.GetDelegateForFunctionPointer<CellPhysicsPointerCall>(
                _lease.PhysicsModule + 0x76e60)(iterator));
            while (pointer != 0)
            {
                RequireCellPhysics(result.Count < 1_048_576 && !result.ContainsKey(pointer),
                    "The native physical-entity iterator contains a cycle or exceeds the managed ownership bound.");
                result.Add(pointer, new(pointer, Marshal.ReadIntPtr(pointer),
                    unchecked((uint)Marshal.ReadInt32(pointer + 0x44)),
                    Marshal.ReadIntPtr(pointer + 0x20), Marshal.ReadInt16(pointer + 0x28),
                    Marshal.ReadByte(pointer + 0x47)));
                pointer = Native(() => Marshal.GetDelegateForFunctionPointer<CellPhysicsPointerCall>(
                    _lease.PhysicsModule + 0x76da0)(iterator));
            }
            return result;
        }
        catch (Exception error)
        {
            enumerationFailure = error;
            throw;
        }
        finally
        {
            if (held && !_poisoned)
            {
                try
                {
                    Native(() => { Marshal.GetDelegateForFunctionPointer<CellPhysicsVoidCall>(
                        _lease.PhysicsModule + 0x96080)(iterator); return 0; });
                    state.RetainedIterators.Remove(iterator);
                }
                catch (Exception releaseError) when (enumerationFailure is not null)
                {
                    throw new AggregateException(enumerationFailure, releaseError);
                }
            }
        }
    }

    private void CaptureCellPhysics(CellEntityBatch batch)
    {
        // Initial profile rejection precedes every native class/load callback. The batch
        // catch still captures entity deltas; an empty unchanged interval needs no physics read.
        if (batch.Physics is { Baseline: null } && !batch.NativeLoadStarted && batch.Owned.Count == 0) return;
        CaptureCurrentCellPhysics(batch, true);
    }

    private void CaptureCurrentCellPhysics(CellEntityBatch batch, bool loadingInterval)
    {
        var state = batch.Physics ?? throw new InvalidOperationException("Capture cell physics before native entity loading.");
        RequireCellPhysics(!state.Retired && state.Baseline is not null, "The cell physics baseline did not complete.");
        var current = SnapshotCellPhysicsBodies(state);
        if (loadingInterval)
        {
            foreach (var prior in state.Baseline.Values)
            {
                // Native physical ids are not stable: a body physicalized during this entity load can
                // claim an id an incumbent already holds, and the native world then renumbers the
                // incumbent to the next free id, leaving its allocation, class table, foreign owner
                // and foreign kind untouched. Observed on the S1 first-entry load of cell (11,15):
                // the pre-existing static-object bodies 257 -> 1449 and 220 -> 1448 while the two new
                // IEntity-owned bodies took exactly the vacated 257 and 220; baseline 1447 -> current
                // 1449 with zero vanished (C:\AAEmuRuntime\ZoneRemakeFirstEntry\s1-diag\
                // cell-physics-mismatch.json). Only that exact hand-off is tolerated: the body must
                // still exist with the same table, foreign owner and foreign kind, and the vacated
                // id must be held by a body this baseline did not contain that is an engine IEntity
                // lease (ForeignLease.LeaseKind) rather than a natively created static-object body.
                // Every other difference fails closed with the changed fields named. The batch's own
                // bodies below keep strict identity: their ids are entity-derived and cannot be
                // re-requested while the entity lives.
                var intact = current.TryGetValue(prior.Pointer, out var value) &&
                    (SameCellPhysicsIdentity(prior, value) ||
                        IsVerifiedCellPhysicsRenumber(state.Baseline, current, prior, value));
                if (intact) continue;
                RequireCellPhysics(false, DescribeCellPhysicsBaselineChange(state.Baseline, current, prior, value));
            }
            foreach (var value in current.Values)
            {
                if (state.Baseline.ContainsKey(value.Pointer)) continue;
                if (state.Owned.TryGetValue(value.Pointer, out var prior))
                    RequireCellPhysics(prior.Table == value.Table && (prior.Identity & 0x00ffffff) == (value.Identity & 0x00ffffff),
                        "A captured cell physics allocation was replaced.");
                else state.Owned.Add(value.Pointer, value); // Retain before subclass or owner admission.
            }
        }
        foreach (var prior in state.Owned.Values)
        {
            RequireCellPhysics(current.TryGetValue(prior.Pointer, out var value),
                "A captured full cell body disappeared before its native retirement was verified.");
            RequireCellPhysics(prior.Table == value.Table && (prior.Identity & 0x00ffffff) == (value.Identity & 0x00ffffff),
                "A captured cell physical allocation changed its native identity.");
            ValidateCellPhysicsBody(batch, value);
        }
        // The real getter can expose a parent body's proxy. Never silently adopt a baseline or
        // another batch's body just because a currently owned IEntity reports that pointer.
        foreach (var entity in batch.Owned.Values)
        {
            if (!CellEntityIsCurrent(entity)) continue;
            var body = Native(() => Bind<PointerCall>(entity.Pointer, 0x218, 0xe450)(entity.Pointer));
            RequireCellPhysics(body == 0 || state.Owned.ContainsKey(body),
                "A cell entity reports a physical proxy owned outside this captured batch.");
        }
    }

    private static bool SameCellPhysicsIdentity(CellPhysicsBody first, CellPhysicsBody second) =>
        first.Table == second.Table && (first.Identity & 0x00ffffff) == (second.Identity & 0x00ffffff) &&
        first.Foreign == second.Foreign && first.ForeignKind == second.ForeignKind;

    // Narrower than SameCellPhysicsIdentity and used only for the loading-interval baseline: the
    // allocation pointer, table, foreign owner and foreign kind must be untouched, and a moved
    // low-24 identity is admitted only when the vacated number is now held by a body absent from
    // the baseline that is an engine IEntity lease. Retirement and completion intervals, where a
    // body's id cannot be re-requested, keep the strict comparison.
    private static bool IsVerifiedCellPhysicsRenumber(Dictionary<nint, CellPhysicsBody> baseline,
        Dictionary<nint, CellPhysicsBody> current, CellPhysicsBody prior, CellPhysicsBody value)
    {
        if (prior.Table != value.Table || prior.Foreign != value.Foreign || prior.ForeignKind != value.ForeignKind)
            return false;
        var vacated = prior.Identity & 0x00ffffffu;
        if ((value.Identity & 0x00ffffffu) == vacated) return false;
        var holder = FindCellPhysicsRenumberHolder(baseline, current, vacated);
        return holder is not null && holder.ForeignKind == ForeignLease.LeaseKind;
    }

    /// <summary>The body created during this loading interval that took over a vacated physical id.</summary>
    private static CellPhysicsBody? FindCellPhysicsRenumberHolder(Dictionary<nint, CellPhysicsBody> baseline,
        Dictionary<nint, CellPhysicsBody> current, uint vacated)
    {
        foreach (var candidate in current.Values)
            if (!baseline.ContainsKey(candidate.Pointer) && (candidate.Identity & 0x00ffffffu) == vacated)
                return candidate;
        return null;
    }

    /// <summary>Names every differing field so a failure is diagnosable without a further witness run.</summary>
    private static string DescribeCellPhysicsBaselineChange(Dictionary<nint, CellPhysicsBody> baseline,
        Dictionary<nint, CellPhysicsBody> current, CellPhysicsBody prior, CellPhysicsBody? value)
    {
        var pointer = FormatCellPhysicsPointer(prior.Pointer);
        if (value is null)
            return $"Cell callbacks removed the pre-existing native physics registration at {pointer} " +
                $"(table {FormatCellPhysicsPointer(prior.Table)}, identity {prior.Identity & 0x00ffffffu}, " +
                $"foreign kind {prior.ForeignKind}, simulation class {prior.SimulationClass}).";
        var fields = new List<string>();
        if (prior.Table != value.Table)
            fields.Add($"Table {FormatCellPhysicsPointer(prior.Table)} -> {FormatCellPhysicsPointer(value.Table)}");
        if ((prior.Identity & 0x00ffffffu) != (value.Identity & 0x00ffffffu))
        {
            var vacated = prior.Identity & 0x00ffffffu;
            var holder = FindCellPhysicsRenumberHolder(baseline, current, vacated);
            var reason = holder is null
                ? $"no body outside the baseline holds the vacated id {vacated}"
                : $"the body holding the vacated id {vacated} has foreign kind {holder.ForeignKind}, " +
                    $"not the engine IEntity lease kind {ForeignLease.LeaseKind}";
            fields.Add($"Identity low24 {vacated} -> {value.Identity & 0x00ffffffu} is not a verified renumber: {reason}");
        }
        if ((prior.Identity >> 24) != (value.Identity >> 24))
            fields.Add($"Identity bits 24-31 {prior.Identity >> 24} -> {value.Identity >> 24}");
        if (prior.Foreign != value.Foreign)
            fields.Add($"Foreign {FormatCellPhysicsPointer(prior.Foreign)} -> {FormatCellPhysicsPointer(value.Foreign)}");
        if (prior.ForeignKind != value.ForeignKind)
            fields.Add($"ForeignKind {prior.ForeignKind} -> {value.ForeignKind}");
        if (prior.SimulationClass != value.SimulationClass)
            fields.Add($"SimulationClass {prior.SimulationClass} -> {value.SimulationClass}");
        return $"Cell callbacks changed a pre-existing native physics registration or foreign owner at {pointer}: " +
            $"{string.Join("; ", fields)}.";
    }

    private static string FormatCellPhysicsPointer(nint value) => $"0x{(ulong)value:x16}";

    private void ValidateCellPhysicsBody(CellEntityBatch batch, CellPhysicsBody body)
    {
        var state = batch.Physics!;
        var kind = body.Table == _lease.PhysicsModule + 0x1efe38 ? 1 :
            body.Table == _lease.PhysicsModule + 0x1f14c8 ? 2 : 0;
        if (kind == 0)
            throw new NotSupportedException("Cell physics requires a proven full static or rigid body; retain this batch for its native subclass owner.");
        RequireCellPhysics(Marshal.ReadIntPtr(body.Pointer + 0x78) == state.World &&
            Native(() => Marshal.GetDelegateForFunctionPointer<CellPhysicsPlaceholderCall>(
                _lease.PhysicsModule + 0x760c0)(state.World, body.Pointer)) == 0,
            "Cell physics requires the exact full allocation in this physical world.");
        if (body.SimulationClass == 7)
        {
            RequireCellPhysics(body.Foreign == 0 && body.ForeignKind == -1 &&
                Marshal.ReadInt32(body.Pointer + 0x50) != 0,
                "A retired cell body still retains its native foreign owner.");
            return;
        }
        RequireCellPhysics(body.SimulationClass < 7 && body.SimulationClass != 5 && body.ForeignKind == 2,
            "A cell body is not a live full IEntity-owned static or rigid allocation.");
        var owner = batch.Owned.Values.FirstOrDefault(value => value.Pointer == body.Foreign);
        RequireCellPhysics(owner is not null && CellEntityIsCurrent(owner),
            "A cell physics body has no exact current IEntity owner in the captured batch.");
    }

    private int CompletePendingCellPhysics(CellEntityBatch batch)
    {
        using var operation = Enter();
        ValidateCellEntityBatch(batch);
        RequireCellPhysics(batch.Physics is not null && batch.LoadAttempted,
            "Load the cell batch before completing its native physics requests.");
        return _lease.Engine.CompleteCellEntityLoadingPhysics(this, batch);
    }


    internal Dictionary<nint, CellPhysicsBody> BeginCellPhysicsCompletionInterval(CellEntityBatch batch)
    {
        ValidateCellEntityBatch(batch);
        return SnapshotCellPhysicsBodies(batch.Physics ??
            throw new InvalidOperationException("The native cell physics ownership interval was not captured."));
    }

    internal void EndCellPhysicsCompletionInterval(CellEntityBatch batch,
        Dictionary<nint, CellPhysicsBody> before)
    {
        var current = SnapshotCellPhysicsBodies(batch.Physics!);
        foreach (var value in current.Values)
        {
            if (before.ContainsKey(value.Pointer)) continue;
            var target = _cellEntityBatches.FirstOrDefault(candidate => candidate.Physics is { Retired: false } &&
                candidate.Owned.Values.Any(entity => entity.Pointer == value.Foreign)) ?? batch;
            target.Physics!.Owned.TryAdd(value.Pointer, value); // Retain even an unsupported auxiliary allocation.
        }
        RequireCellPhysics(current.Count == before.Count && before.Values.All(previous =>
            current.TryGetValue(previous.Pointer, out var value) && SameCellPhysicsIdentity(previous, value)),
            "Native loading completion created or replaced a physics owner; retain the captured cell resources.");
    }

    internal HashSet<nint> CaptureCellPhysicsCompletionBodies()
    {
        _lease.RequireActiveOperation();
        var result = new HashSet<nint>();
        foreach (var batch in _cellEntityBatches)
        {
            if (batch.Physics is null || batch.Physics.Retired) continue;
            if (batch.Physics.Baseline is null && !batch.NativeLoadStarted && batch.Owned.Count == 0) continue;
            CaptureCurrentCellPhysics(batch, false);
            foreach (var pointer in batch.Physics.Owned.Keys)
                RequireCellPhysics(result.Add(pointer), "Two cell batches claim the same physical allocation.");
        }
        return result;
    }

    private void PrepareCellPhysicsRetirement(CellEntityBatch batch)
    {
        RequireCellPhysics(batch.Physics is not null, "The cell physics retirement interval was not captured.");
        if (batch.Physics.Baseline is null)
        {
            RequireCellPhysics(!batch.NativeLoadStarted && batch.Owned.Count == 0,
                "An incomplete cell physics baseline must retain its native owners.");
            return;
        }
        CaptureCurrentCellPhysics(batch, false);
        _lease.Engine.CompleteCellEntityLoadingPhysics(this, batch);
        batch.Physics.RetirementBaseline = SnapshotCellPhysicsBodies(batch.Physics)
            .Where(value => !batch.Physics.Owned.ContainsKey(value.Key))
            .ToDictionary(value => value.Key, value => value.Value);
        if (Marshal.ReadInt32(batch.Physics.World + 0x434) != 0 &&
            batch.Physics.Owned.Keys.Any(pointer =>
                (unchecked((uint)Marshal.ReadInt32(pointer + 0x44)) & 0xff000000) is > 0 and < 0x80000000))
            throw new NotSupportedException("Native physics currently refuses mode-zero destruction of this cell body class.");
        // Native proxy Shutdown calls body Release and DestroyPhysicalEntity(mode0,thread0).
        // With the loading guards and zero queued reference units, Destroy cannot defer.
        foreach (var pointer in batch.Physics.Owned.Keys)
            RequireCellPhysics(unchecked((uint)Marshal.ReadInt32(pointer + 0x40)) <= 0x1ffff,
                "Complete native requests before releasing an entity's physical proxy.");
    }

    private void CompleteCellPhysicsRetirement(CellEntityBatch batch)
    {
        if (batch.Physics is not { } state || state.Retired) return;
        if (state.Baseline is null)
        {
            RequireCellPhysics(!batch.NativeLoadStarted && batch.Owned.Count == 0,
                "An incomplete cell physics baseline must retain its native owners.");
            state.Retired = true;
            return;
        }
        var current = SnapshotCellPhysicsBodies(state);
        RequireCellPhysics(state.RetirementBaseline is not null, "Prepare cell physics before native entity shutdown.");
        foreach (var before in state.RetirementBaseline.Values)
            RequireCellPhysics(current.TryGetValue(before.Pointer, out var value) && SameCellPhysicsIdentity(before, value),
                "Native cell shutdown changed another physics owner's registration.");
        foreach (var body in state.Owned.Values)
        {
            RequireCellPhysics(current.TryGetValue(body.Pointer, out var value) &&
                body.Table == value.Table && (body.Identity & 0x00ffffff) == (value.Identity & 0x00ffffff) &&
                value.SimulationClass == 7 && value.Foreign == 0 && value.ForeignKind == -1 &&
                Marshal.ReadInt32(value.Pointer + 0x50) != 0 &&
                unchecked((uint)Marshal.ReadInt32(value.Pointer + 0x40)) <= 0x1ffff,
                "Native IEntity/proxy shutdown did not retire a captured full physics allocation.");
        }
        RequireCellPhysics(current.Count == state.RetirementBaseline.Count + state.Owned.Count,
            "Native entity shutdown created an uncaptured physics allocation.");
        _lease.Engine.RequireCellEntityPhysicsRequestsEmpty(this);
        // 35079B30 dispatches no callbacks. It removes logged events with retired direct
        // participants while preserving events belonging only to still-live sibling bodies.
        Native(() => { Marshal.GetDelegateForFunctionPointer<CellPhysicsVoidCall>(
            _lease.PhysicsModule + 0x79b30)(state.World); return 0; });
        VerifyNoRetiredCellPhysicsEvents(state);
        state.Retired = true;
    }

    private void VerifyNoRetiredCellPhysicsEvents(CellPhysicsState state)
    {
        var visited = new HashSet<nint>();
        for (var node = Marshal.ReadIntPtr(state.World + 0x5260); node != 0; node = Marshal.ReadIntPtr(node))
        {
            RequireCellPhysics(visited.Count < 1_048_576 && visited.Add(node), "Native logged physics events contain a cycle.");
            var kind = Marshal.ReadInt32(node + 8);
            RequireCellPhysics(kind >= 0 && !state.Owned.ContainsKey(Marshal.ReadIntPtr(node + 0x10)) &&
                (kind >= 2 || !state.Owned.ContainsKey(Marshal.ReadIntPtr(node + 0x18))),
                "Native physics events still retain a retired cell body.");
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint CellPhysicsPointerCall(nint pointer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CellPhysicsVoidCall(nint pointer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CellPhysicsPlaceholderCall(nint world, nint body);
}
