using System.Numerics;
using System.Runtime.InteropServices;

using AAEmu.Zone.Content;
using AAEmu.Zone.Scene;

namespace AAEmu.Zone.Native.StaticObjects;

public sealed unsafe partial class ZoneNativeStaticObjectServices
{
    private readonly List<VisibilityCell> _visibilityCells = [];

    /// <summary>
    /// Loads one configured visareas.dat through the actual native manager and its indoor
    /// octree/object factories. Imported brushes join this service's physical completion owner
    /// before return. Native construction requests still require the engine's separate completion
    /// phase; this result establishes neither complete collision nor scene readiness. Coordinates
    /// are mapped engine-grid cells, not authored world-cell numbers. Indoor brush creation requires
    /// the real engine cell-publication service and the configured native physics entity grid.
    /// </summary>
    public VisibilityCell LoadCellVisibility(int cellX, int cellY, ZoneCellVisibilityContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        ValidateTerrainCell(cellX, cellY);
        VisibilityCell cell;
        using (var operation = Enter())
        {
            if (_visibilityCells.Any(value => !value.Retired && value.CellX == cellX && value.CellY == cellY))
                throw new InvalidOperationException("The cell already has an owned visibility generation.");
            var enabled = ReadVisibilityLoadGate();
            cell = new VisibilityCell(this, cellX, cellY, content, enabled);
            _visibilityCells.Add(cell);
        }
        try
        {
            //30161EF0 returns before file/resource work when this native config field is zero.
            // Keep the exact branch explicit rather than manufacturing an empty native area.
            if (!cell.NativeLoadingEnabled) return cell;
            using (var operation = Enter())
            {
                PreflightVisibilityContent(cell);
                RequireIdleVegetationWorld();
                RequireImportedVisibilityDeletionQueuesEmpty();
                cell.ManagerOwnership = BeginVisibilityCellManagerOwnership(cellX, cellY);
                RequireVisibilityCellSelectionEmpty(cell);
            }
            cell.Tables = ZoneCellObjectTables.Create(this, content.ResourceTables);
            using (var operation = Enter()) LoadVisibilityCellCore(cell);
            cell.Tables.Dispose(); // Native instances own their own model/material references.
            cell.Tables = null;
            return cell;
        }
        catch (Exception error)
        {
            if (_poisoned) throw; // Unknown native completion retains cell, inputs and manager.
            try { DisposeVisibilityCell(cell); }
            catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
            throw;
        }
    }

    private bool ReadVisibilityLoadGate()
    {
        _lease.RequireActiveOperation();
        var configuration = Marshal.ReadIntPtr(_lease.TerrainModule + 0x2fa8d0);
        Require(configuration != 0, "The native visibility configuration is unavailable.");
        return Marshal.ReadInt32(configuration + 0x2a4) != 0;
    }

    private void PreflightVisibilityContent(VisibilityCell cell)
    {
        foreach (var record in cell.Content.Records)
            if (record.EmbeddedObjects is { } objects && objects.Objects.Any(value => value.Type != ZoneCellObjectType.Brush))
                throw new NotSupportedException("The visibility resource owner currently admits only authored indoor brushes; other nested native families require their own retirement contract.");
        if (Marshal.ReadByte(_lease.TerrainModule + 0x2fa949) != 0)
            throw new NotSupportedException("Compiled visibility resource admission requires the native non-editor resource-loading mode.");
        if (cell.Content.Records.Any(record => record.EmbeddedObjects?.Objects.Count > 0))
        {
            RequireCellPublicationAvailable();
            ValidateCellPublicationService();
            _lease.RequireTerrainEntityGrid();
        }
        foreach (var record in cell.Content.Records)
        {
            // The actual native cell unloader selects compiled areas by translated bounds center.
            // Reject an unrepresentable/out-of-cell record BEFORE native publication so retirement
            // never needs to remove a sibling cell's area or silently leave this resource behind.
            var minimum = TranslateVisibility(record.BoundsMin, cell.CellX, cell.CellY);
            var maximum = TranslateVisibility(record.BoundsMax, cell.CellX, cell.CellY);
            if (!FiniteVisibilityVector(minimum) || !FiniteVisibilityVector(maximum) ||
                minimum.X > maximum.X || minimum.Y > maximum.Y || minimum.Z > maximum.Z ||
                !VisibilityCenterInCell(minimum, maximum, cell.CellX, cell.CellY))
                throw new InvalidDataException("Visibility bounds cannot be owned by their mapped native engine cell.");
            if (!FiniteVisibilityVector(TranslateVisibility(record.StaticBoundsMin, cell.CellX, cell.CellY)) ||
                !FiniteVisibilityVector(TranslateVisibility(record.StaticBoundsMax, cell.CellX, cell.CellY)) ||
                record.Points.Any(point => !FiniteVisibilityVector(TranslateVisibility(point, cell.CellX, cell.CellY))))
                throw new InvalidDataException("Visibility geometry contains a nonfinite translated coordinate.");
        }
    }

    private void LoadVisibilityCellCore(VisibilityCell cell)
    {
        var ownership = cell.ManagerOwnership!;
        ValidateVisibilityCellManagerOwnership(ownership);
        cell.Tables!.ValidateFor(this, cell.Content.ResourceTables);
        Require(ReadVisibilityLoadGate() && Marshal.ReadByte(_lease.TerrainModule + 0x2fa949) == 0,
            "The native visibility loading gates changed during resource acquisition.");
        RequireImportedVisibilityDeletionQueuesEmpty();
        RequireVisibilityCellSelectionEmpty(cell);
        var before = SnapshotVisibilityAreas(ownership.Manager.Pointer);
        var prior = before.Select(value => value.Pointer).ToHashSet();
        cell.Compiled = Allocate(cell.Content.RawCompiledData.ToArray());
        cell.Cursor = Allocate(new byte[IntPtr.Size]);
        Marshal.WriteIntPtr(cell.Cursor, cell.Compiled + 20);
        cell.ModelVector = CreateVisibilityPointerVector(cell.Tables.Models, model =>
        {
            if (model is null) return 0;
            Require(ReferenceEquals(model.Owner, this) && !model.Retired && model.ReferenceHeld,
                "A visibility model table no longer owns its native reference.");
            RequireTable(model.Pointer, 0x27ee78);
            return model.Pointer;
        });
        cell.MaterialVector = CreateVisibilityPointerVector(cell.Tables.Materials, material =>
        {
            if (material is null) return 0;
            Require(ReferenceEquals(material.Owner, this) && !material.Retired && material.ReferenceHeld,
                "A visibility material table no longer owns its native reference.");
            RequireTable(material.Pointer, 0x297378);
            return material.Pointer;
        });
        Require(Marshal.ReadIntPtr(_lease.TerrainModule + 0x2df678) == 0 &&
            Marshal.ReadInt64(_lease.TerrainModule + 0x2fd280) == 0,
            "Another native compiled-object reader still owns its scratch storage or cell coordinates.");
        ownership.NativeLoadStarted = true;
        Native(() => cell.NativeResult = Terrain<VisibilityLoadCall>(0x198bf0)(ownership.Manager.Pointer,
            cell.CellX, cell.CellY, cell.Cursor, cell.Compiled + 4, cell.Compiled,
            cell.ModelVector, cell.MaterialVector));
        ValidateVisibilityCellManagerOwnership(ownership);
        var after = SnapshotVisibilityAreas(ownership.Manager.Pointer);
        Require(prior.IsSubsetOf(after.Select(value => value.Pointer)),
            "Visibility loading removed a pre-existing area from the retained manager generation.");
        cell.Areas = after.Where(value => !prior.Contains(value.Pointer)).ToArray();
        try
        {
            // Capture roots its batch before any native pointer inspection. Even a false return
            // below must adopt every actually created brush before attempting ordinary rollback.
            cell.Brushes = CaptureImportedVisibilityBrushes(ownership, cell.Areas.Select(value => value.Pointer).ToArray());
        }
        catch (Exception error)
        {
            _poisoned = true;
            _lease.Poison(error);
            throw;
        }
        RequireVisibilityCellSelectionExact(cell);
        if (cell.NativeResult == 0 || Marshal.ReadInt32(cell.Compiled + 4) != 0 ||
            Marshal.ReadIntPtr(cell.Cursor) != cell.Compiled + cell.Content.RawCompiledData.Length ||
            cell.Areas.Count(value => value.Kind == ZoneCellVisibilityKind.Area) != cell.Content.AreaCount ||
            cell.Areas.Count(value => value.Kind == ZoneCellVisibilityKind.Portal) != cell.Content.PortalCount ||
            cell.Areas.Count(value => value.Kind == ZoneCellVisibilityKind.OcclusionArea) != cell.Content.OcclusionAreaCount)
            throw new InvalidDataException("The native visibility loader did not complete every bounded compiled record.");
        cell.NativeLoadCompleted = true;
        ReleaseVisibilityInputs(cell);
    }

    private nint CreateVisibilityPointerVector<T>(IReadOnlyList<T> values, Func<T, nint> pointer)
    {
        // One allocation contains native begin/end/capacity followed by the pointer slots; native
        // code borrows this vector and does not resize/free it. No temporary native reference is
        // transferred by merely constructing these pointer slots.
        var count = values.Count;
        var storage = Allocate(new byte[checked(24 + count * IntPtr.Size)]);
        try
        {
            if (count != 0)
            {
                var begin = storage + 24;
                Marshal.WriteIntPtr(storage, begin);
                Marshal.WriteIntPtr(storage + 8, begin + count * IntPtr.Size);
                Marshal.WriteIntPtr(storage + 16, begin + count * IntPtr.Size);
                for (var i = 0; i < count; i++) Marshal.WriteIntPtr(begin + i * IntPtr.Size, pointer(values[i]));
            }
            return storage;
        }
        catch { Marshal.FreeHGlobal(storage); throw; }
    }

    private NativeVisibilityArea[] SnapshotVisibilityAreas(nint manager)
    {
        Require(manager == RequireVisibilityManagerPointer(), "The visibility snapshot has a foreign manager.");
        var result = new List<NativeVisibilityArea>();
        var seen = new HashSet<nint>();
        foreach (var (offset, kind) in new[] { (0x28, ZoneCellVisibilityKind.Area),
            (0x48, ZoneCellVisibilityKind.Portal), (0x58, ZoneCellVisibilityKind.OcclusionArea) })
        {
            var items = Marshal.ReadIntPtr(manager + offset);
            var count = Marshal.ReadInt32(manager + offset + 8);
            var capacity = Marshal.ReadInt32(manager + offset + 12);
            Require(count >= 0 && capacity >= count && count <= ZoneCellVisibilityContent.MaximumFileBytes / 268 &&
                (capacity == 0 || items != 0), "The native visibility list has an invalid pointer/count/capacity.");
            for (var i = 0; i < count; i++)
            {
                var area = Marshal.ReadIntPtr(items + i * IntPtr.Size);
                RequireTable(area, 0x291e88);
                Require(Marshal.ReadIntPtr(area + 0x50) != 0,
                    "A native visibility area has no descriptor required by its destructor.");
                // Native30197BF0 excludes via < / <= comparisons; unordered NaNs could otherwise
                // pass its cell filter while failing managed >= / < ownership selection.
                Require(FiniteVisibilityVector(*(Vector3*)(area + 0x20)) &&
                    FiniteVisibilityVector(*(Vector3*)(area + 0x2c)),
                    "The native visibility manager contains nonfinite bounds that cannot be selected safely.");
                Require(seen.Add(area), "The native manager contains a duplicate primary area pointer.");
                result.Add(new(kind, area));
            }
        }
        return result.ToArray();
    }

    private HashSet<nint> SelectVisibilityCellAreas(nint manager, int cellX, int cellY) =>
        SnapshotVisibilityAreas(manager).Where(value => Marshal.ReadByte(value.Pointer + 0x63) != 0 &&
            VisibilityCenterInCell(*(Vector3*)(value.Pointer + 0x20), *(Vector3*)(value.Pointer + 0x2c), cellX, cellY))
        .Select(value => value.Pointer).ToHashSet();

    private void RequireVisibilityCellSelectionEmpty(VisibilityCell cell)
    {
        if (SelectVisibilityCellAreas(cell.ManagerOwnership!.Manager.Pointer, cell.CellX, cell.CellY).Count != 0)
            throw new InvalidOperationException("The native cell already contains visibility areas outside this new admission.");
    }

    private void RequireVisibilityCellSelectionExact(VisibilityCell cell)
    {
        Require(SelectVisibilityCellAreas(cell.ManagerOwnership!.Manager.Pointer, cell.CellX, cell.CellY)
            .SetEquals(cell.Areas.Select(value => value.Pointer)),
            "The native visibility unloader's selection no longer equals this cell's exact owned areas.");
    }

    /// <summary>
    /// Flush deferred visibility node deletions that earlier teardown in this same retirement left
    /// pending, before the mutation preflight demands empty queues.
    ///
    /// Measured cause (retire-diag-run, 2026-09-15): a cell retires as
    /// `Entities -> Visibility -> BigObjects -> Vegetation -> MainObjects`, and `Entities.Dispose`
    /// queues exactly 13 nodes into engine+0x528 (the next cell adds 11 more). The visibility
    /// prepare then refuses because the queue is non-empty, while the only drain lives in the
    /// completion step that never runs - so the queue grew monotonically to 127 and no cell's
    /// visibility was ever retired. Those entries are this retirement's own entity teardown residue,
    /// not an anonymous owner's work, which is the one case the prepare guard's warning does not
    /// cover.
    ///
    /// The native drain (301581F0, flushAll=1) releases all three queues in native order and is the
    /// same call the completion path already makes. This deliberately does **not** turn the
    /// preflight into "flush whenever non-empty": the drain runs once, at a point where a non-empty
    /// queue is expected, and the completion path still refuses to flush unless the queue is exactly
    /// the batch's own captured brushes.
    /// </summary>
    private void DrainPendingVisibilityDeletions(VisibilityCell cell)
    {
        var pending = false;
        foreach (var offset in new[] { 0x528, 0x538, 0x548 })
            if (ReadImportedVisibilityPointerVector(_lease.ThreeDEngine + offset).Items.Length != 0)
                pending = true;
        // Nothing pending means a healthy retirement does no native work here at all.
        if (!pending) return;
        Native(() => Terrain<VisibilityNodeQueueDrainCall>(0x1581f0)(_lease.ThreeDEngine, 1));
        foreach (var offset in new[] { 0x528, 0x538, 0x548 })
            Require(ReadImportedVisibilityPointerVector(_lease.ThreeDEngine + offset).Items.Length == 0,
                "The deferred visibility deletion drain left a residual queue before cell mutation.");
    }

    private void RetireVisibilityCellCore(VisibilityCell cell, string diagnosticPath)
    {
        if (cell.Retired) return;
        Require(ReferenceEquals(cell.Owner, this) && _visibilityCells.Contains(cell),
            "The visibility cell is not owned by this static-object service.");
        if (cell.Tables is { IsDisposed: false })
            throw new InvalidOperationException("Release temporary visibility tables outside the engine operation before native cell retirement.");
        if (cell.ManagerOwnership is { } ownership)
        {
            ValidateVisibilityCellManagerOwnership(ownership);
            if (ownership.NativeLoadStarted)
            {
                Require(cell.Brushes is not null, "The mutated visibility cell has no completed imported-node ownership capture.");
                RequireVisibilityCellSelectionExact(cell);
                DrainPendingVisibilityDeletions(cell);
                RetirementDiagnostic.QueueSample(this, $"cell({cell.CellX},{cell.CellY})-before-prepare");
                try { PrepareImportedVisibilityBrushDeletion(cell.Brushes!); }
                catch { RetirementDiagnostic.VisibilityQueueGuard(this, cell, diagnosticPath); throw; }
                RetirementDiagnostic.QueueSample(this, $"cell({cell.CellX},{cell.CellY})-after-prepare");
                Native(() => Terrain<VisibilityUnloadCall>(0x197bf0)(ownership.Manager.Pointer, cell.CellX, cell.CellY));
                RetirementDiagnostic.QueueSample(this, $"cell({cell.CellX},{cell.CellY})-after-unload");
                ValidateVisibilityCellManagerOwnership(ownership);
                // Includes the actual deferred-node queue flush, never just area pointer removal.
                CompleteImportedVisibilityBrushDeletion(cell.Brushes!);
                RetirementDiagnostic.QueueSample(this, $"cell({cell.CellX},{cell.CellY})-after-complete");
                ownership.NativeRetirementCompleted = true;
            }
            EndVisibilityCellManagerOwnership(ownership);
        }
        ReleaseVisibilityInputs(cell);
        cell.Retired = true;
        _visibilityCells.Remove(cell);
    }

    private void DisposeVisibilityCell(VisibilityCell cell)
    {
        if (cell.Retired) return;
        VerifyResourceOwner();
        cell.Tables?.Dispose();
        cell.Tables = null;
        using var operation = Enter();
        RetireVisibilityCellCore(cell, "DisposeVisibilityCell");
    }

    // Root integrates this before manager/model/material retirement in the static service owner.
    private void RetireVisibilityCellResources()
    {
        foreach (var cell in _visibilityCells.ToArray()) RetireVisibilityCellCore(cell, "RetireVisibilityCellResources");
    }

    private static void ReleaseVisibilityInputs(VisibilityCell cell)
    {
        Marshal.FreeHGlobal(cell.MaterialVector);
        Marshal.FreeHGlobal(cell.ModelVector);
        Marshal.FreeHGlobal(cell.Cursor);
        Marshal.FreeHGlobal(cell.Compiled);
        cell.MaterialVector = cell.ModelVector = cell.Cursor = cell.Compiled = 0;
    }

    private static Vector3 TranslateVisibility(Vector3 value, int x, int y) =>
        new((float)(x << 10) + value.X, (float)(y << 10) + value.Y, value.Z + 0f);
    private static bool FiniteVisibilityVector(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool VisibilityCenterInCell(Vector3 minimum, Vector3 maximum, int x, int y)
    {
        var centerX = (minimum.X + maximum.X) * .5f;
        var centerY = (minimum.Y + maximum.Y) * .5f;
        return centerX >= x * 1024 && centerX < x * 1024 + 1024 && centerY >= y * 1024 && centerY < y * 1024 + 1024;
    }

    internal readonly record struct NativeVisibilityArea(ZoneCellVisibilityKind Kind, nint Pointer);

    /// <summary>One exact manager/cell resource lifetime, including the native disabled-load outcome.</summary>
    public sealed class VisibilityCell : IDisposable
    {
        internal readonly ZoneNativeStaticObjectServices Owner;
        internal VisibilityCellManagerOwnership? ManagerOwnership;
        internal VisibilityBrushOwnership? Brushes;
        internal ZoneCellObjectTables? Tables;
        internal NativeVisibilityArea[] Areas = [];
        internal nint Compiled, Cursor, ModelVector, MaterialVector;
        internal byte NativeResult;
        internal bool NativeLoadCompleted, Retired;
        public int CellX { get; }
        public int CellY { get; }
        public ZoneCellVisibilityContent Content { get; }
        public bool NativeLoadingEnabled { get; }
        public int CreatedAreaCount => Areas.Length;
        public int ImportedBrushCount => Brushes?.ImportedBrushCount ?? 0;
        public int PhysicalBodyCount => Brushes?.PhysicalBodyCount ?? 0;
        public bool IsDisposed => Retired;
        internal VisibilityCell(ZoneNativeStaticObjectServices owner, int x, int y, ZoneCellVisibilityContent content, bool enabled)
        { Owner = owner; CellX = x; CellY = y; Content = content; NativeLoadingEnabled = enabled; }
        public void Dispose() => Owner.DisposeVisibilityCell(this);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte VisibilityLoadCall(nint manager, int x, int y, nint cursor, nint remaining,
        nint header, nint models, nint materials);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VisibilityUnloadCall(nint manager, int x, int y);
}
