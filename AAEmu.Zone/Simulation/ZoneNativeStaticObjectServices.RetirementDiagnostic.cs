using AAEmu.Zone.Content;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AAEmu.Zone.Simulation;

public sealed unsafe partial class ZoneNativeStaticObjectServices
{
    /// <summary>
    /// TEMPORARY diagnosis scaffolding. Delete this nested type and the tagged call sites to revert.
    /// Writes newline-delimited JSON events to the file named by AAEMU_ZONE_RETIREMENT_DIAGNOSTIC and
    /// is a complete no-op when that variable is unset or empty. Reads only: raw pointer vector
    /// storage and managed fields are inspected, never written, and no native function is invoked.
    /// </summary>
    internal static class RetirementDiagnostic
    {
        private const string Variable = "AAEMU_ZONE_RETIREMENT_DIAGNOSTIC";

        /// <summary>Bound copied from ReadImportedVisibilityPointerVector; not a claimed native limit.</summary>
        private const int MaximumPointers = ZoneCellObjectContent.MaximumFileBytes / 8;

        private static readonly bool Enabled;
        private static readonly string? Target;
        private static readonly object Gate = new();

        private static int _identity;
        private static int _entered;

        static RetirementDiagnostic()
        {
            var value = Environment.GetEnvironmentVariable(Variable);
            Enabled = !string.IsNullOrWhiteSpace(value);
            Target = value;
            if (!Enabled) return;
            try
            {
                System.IO.Path.GetFullPath(Target!);
            }
            catch (Exception error)
            {
                Enabled = false;
                Console.Error.WriteLine($"Retirement diagnostic disabled: {error.Message}");
            }
        }

        internal static void Emit(string kind, string path, object payload)
        {
            if (!Enabled) return;
            try
            {
                var identity = Interlocked.Increment(ref _identity);
                var line = JsonSerializer.Serialize(new DiagEvent(identity, kind, path,
                    Environment.CurrentManagedThreadId, payload));
                lock (Gate) File.AppendAllText(Target!, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"Retirement diagnostic write failed: {error.Message}");
            }
        }

        /// <summary>
        /// One-line sample of the three queues at a load boundary, so the entry that arrives before
        /// the first retirement prepare can be attributed to a load phase. Read-only.
        /// </summary>
        internal static void QueueSample(ZoneNativeStaticObjectServices service, string pathTag)
        {
            if (!Enabled) return;
            try
            {
                var engine = service._lease.ThreeDEngine;
                var known = familiesOf(service._lease);
                var samples = new List<object>();
                foreach (var offset in new[] { 0x528, 0x538, 0x548 })
                {
                    var (pointer, capacity, count, items) = ReadVector(engine + offset);
                    var families = new List<string>(items.Length);
                    for (var index = 0; index < items.Length; index++)
                        families.Add(familyOf(ReadPointer(items[index]), known));
                    samples.Add(new { offset, pointer = Hex(pointer), capacity, count, families });
                }
                Emit("visibility-queue-sample", pathTag, new { engine = Hex(engine), samples });
            }
            catch (Exception error)
            {
                Emit("visibility-queue-sample-error", pathTag,
                    new { error = error.GetType().Name, message = error.Message });
            }
        }

        /// <summary>State of the three native deferred-node queues and attribution of every item.</summary>
        internal static void VisibilityQueueGuard(ZoneNativeStaticObjectServices service,
            VisibilityCell? cell, string pathTag)
        {
            if (!Enabled) return;
            try
            {
                var engine = service._lease.ThreeDEngine;
                var families = familiesOf(service._lease);
                var owned = ownedBrushPointers(service);
                var vectors = new List<object>();
                foreach (var offset in new[] { 0x528, 0x538, 0x548 })
                {
                    var (pointer, capacity, count, items) = ReadVector(engine + offset);
                    var entries = new List<object>();
                    for (var index = 0; index < items.Length; index++)
                    {
                        var item = items[index];
                        entries.Add(new
                        {
                            item = Hex(item),
                            vtable = item == 0 ? "null" : Hex(ReadPointer(item)),
                            family = item == 0 ? "null" : familyOf(ReadPointer(item), families),
                            plus68 = item == 0 ? "null" : Hex(ReadPointer(item + 0x68)),
                            owner = owned.TryGetValue(item, out var owner) ? owner : "UNOWNED",
                        });
                    }
                    vectors.Add(new { offset, pointer = Hex(pointer), capacity, count, items = entries });
                }
                Emit("visibility-queue-guard", pathTag, new
                {
                    engine = Hex(engine),
                    terrainModule = Hex(service._lease.TerrainModule),
                    vegetationPhysicalizationInFlight = ReadInt32(service._lease.TerrainModule + 0x2fa6c0),
                    cell = cell is null ? null : new
                    {
                        cellIndex = cell.CellX * 8 + cell.CellY,
                        cell.CellX,
                        cell.CellY,
                        cell.NativeLoadingEnabled,
                        cell.NativeLoadCompleted,
                        cell.Retired,
                    },
                    batches = batchesOf(service),
                    vectors,
                });
            }
            catch (Exception error)
            {
                Emit("visibility-queue-guard-error", pathTag,
                    new { error = error.GetType().Name, message = error.Message });
            }
        }

        /// <summary>State that makes the "instances before their group definitions" guard fire.</summary>
        internal static void VegetationGroupGuard(ZoneNativeStaticObjectServices service,
            VegetationGroups groups, string pathTag)
        {
            if (!Enabled) return;
            try
            {
                var entries = new List<object>();
                foreach (var value in service._vegetation)
                {
                    var sameGroups = ReferenceEquals(value.Groups, groups);
                    entries.Add(new
                    {
                        identityHash = Identity(value),
                        groupsHash = Identity(value.Groups),
                        sameGroups,
                        value.Retired,
                        blocksGuard = !value.Retired && sameGroups,
                        pointer = Hex(value.Pointer),
                        physicalPointer = Hex(value.PhysicalPointer),
                        recordPointer = Hex(value.RecordPointer),
                        translationPointer = Hex(value.TranslationPointer),
                        cursorPointer = Hex(value.CursorPointer),
                        value.GroupId,
                        modelPointer = Hex(value.ModelPointer),
                        value.PhysicalSimulationClass,
                        value.IsCellPublicationComplete,
                    });
                }
                Emit("vegetation-group-guard", pathTag, new
                {
                    groupsIdentityHash = Identity(groups),
                    groupsRetired = groups.Retired,
                    groupsLoaded = groups.Loaded,
                    groupsVectorCount = groups.VectorCount,
                    groupsManager = Hex(groups.Manager),
                    groupsSameAsField = ReferenceEquals(groups, service._vegetationGroups),
                    vegetationGroupsFieldNull = service._vegetationGroups is null,
                    vegetationGroupsFieldHash = service._vegetationGroups is null ? 0 : Identity(service._vegetationGroups),
                    vegetationCount = service._vegetation.Count,
                    liveCount = service._vegetation.Count(value => !value.Retired),
                    blockingCount = service._vegetation.Count(value => !value.Retired && ReferenceEquals(value.Groups, groups)),
                    otherGroupsCount = service._vegetation.Count(value => !ReferenceEquals(value.Groups, groups)),
                    entries,
                });
            }
            catch (Exception error)
            {
                Emit("vegetation-group-guard-error", pathTag,
                    new { error = error.GetType().Name, message = error.Message });
            }
        }

        /// <summary>Enter/exit pairing around one retirement entry point.</summary>
        internal static int Enter(string pathTag)
        {
            var id = Interlocked.Increment(ref _entered);
            Emit("enter", pathTag, new { id });
            return id;
        }

        internal static void Exit(int id, string pathTag, Exception? error) =>
            Emit("exit", pathTag, new
            {
                id,
                status = error is null ? "ok" : "failed",
                error = error?.GetType().Name,
                message = error?.Message,
            });

        /// <summary>
        /// One line per Dispose() step, including the ones a failing earlier step prevented from
        /// running at all.
        /// </summary>
        internal static void StaticsDisposeSteps(ZoneNativeStaticObjectServices service,
            List<(string Name, Exception? Error)> steps)
        {
            if (!Enabled) return;
            try
            {
                Emit("statics-dispose-steps", "ZoneNativeStaticObjectServices.Dispose", new
                {
                    stepsTotal = steps.Count,
                    stepsRun = steps.Count,
                    failedCount = steps.Count(step => step.Error is not null),
                    steps = steps.Select(step => new
                    {
                        name = step.Name,
                        status = step.Error is null ? "ok" : "FAILED",
                        error = step.Error?.GetType().Name,
                        message = step.Error?.Message,
                    }),
                    visibilityCellsLeft = service._visibilityCells.Count,
                    visibilityManagerLeft = service._visibilityManager is null ? "absent" : "present",
                    importedBatchesLeft = service._importedVisibilityBrushes.Count,
                    vegetationLeft = service._vegetation.Count,
                    vegetationGroupsLeft = service._vegetationGroups is null ? "absent" : "present",
                    fileBrushesLeft = service._brushes.Count,
                    materialsLeft = service._loadedMaterials.Count,
                    modelsLeft = service._models.Count,
                });
            }
            catch (Exception error)
            {
                Emit("statics-dispose-steps-error", "ZoneNativeStaticObjectServices.Dispose",
                    new { error = error.GetType().Name, message = error.Message });
            }
        }

        /// <summary>One line per per-cell vegetation-owner disposal attempt.</summary>
        internal static void VegetationCellDispose(ZoneCellVegetationResources resources, int remaining,
            int attempts, int retired, Exception? error) =>
            Emit("vegetation-cell-dispose", "ZoneCellVegetationResources.Dispose", new
            {
                cell = new { resources.Placement.EngineCell.X, resources.Placement.EngineCell.Y },
                instances = resources.Instances.Count,
                remaining,
                attempts,
                retired,
                status = error is null ? "ok" : "failed",
                error = error?.GetType().Name,
                message = error?.Message,
            });

        private static List<object> batchesOf(ZoneNativeStaticObjectServices service) =>
            service._importedVisibilityBrushes.Select(batch => (object)new
            {
                cell = new { batch.Cell.CellX, batch.Cell.CellY },
                batch.Captured,
                batch.Prepared,
                batch.Retired,
                areas = batch.Areas.Count,
                brushes = batch.Brushes.Count,
                bodies = batch.PhysicalBodyCount,
                identityHash = Identity(batch),
            }).ToList();

        private static Dictionary<nint, string> ownedBrushPointers(ZoneNativeStaticObjectServices service)
        {
            var result = new Dictionary<nint, string>();
            foreach (var batch in service._importedVisibilityBrushes)
                foreach (var brush in batch.Brushes)
                    if (brush.Pointer != 0)
                        result[brush.Pointer] = $"imported-visibility-brush cell({batch.Cell.CellX},{batch.Cell.CellY}) captured={batch.Captured} prepared={batch.Prepared}";
            foreach (var brush in service._brushes)
                if (brush.Pointer != 0 && !result.ContainsKey(brush.Pointer))
                    result[brush.Pointer] = "owned-file-brush";
            return result;
        }

        private static (nint Pointer, int Capacity, int Count, nint[] Items) ReadVector(nint address)
        {
            try
            {
                var pointer = Marshal.ReadIntPtr(address);
                var count = Marshal.ReadInt32(address + 8);
                var capacity = Marshal.ReadInt32(address + 12);
                if (count < 0 || capacity < count || capacity > MaximumPointers)
                    return (pointer, capacity, count, []);
                var items = new nint[count];
                if (count != 0 && pointer != 0) Marshal.Copy(pointer, items, 0, count);
                return (pointer, capacity, count, items);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"Retirement diagnostic vector read failed: {error.Message}");
                return (0, 0, 0, []);
            }
        }

        private static nint ReadPointer(nint address)
        {
            try { return Marshal.ReadIntPtr(address); }
            catch { return 0; }
        }

        private static int ReadInt32(nint address)
        {
            try { return Marshal.ReadInt32(address); }
            catch { return int.MinValue; }
        }

        private static int Identity(object? value) => value is null ? 0 : RuntimeHelpers.GetHashCode(value);

        /// <summary>Names the engine-relative vtable of a queued node against the families this code asserts.</summary>
        private static Dictionary<nint, string> familiesOf(ZoneNativeEngine.StaticObjectLease lease)
        {
            var families = new Dictionary<nint, string>();
            foreach (var field in typeof(ZoneNativeEngine.StaticObjectLease)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (field.FieldType != typeof(nint) || field.GetValue(lease) is not nint value || value == 0) continue;
                if (!field.Name.EndsWith("Module", StringComparison.Ordinal) &&
                    field.Name != "ThreeDEngine" && field.Name != "System" && field.Name != "Environment") continue;
                families[value] = field.Name;
            }
            return families;
        }

        private static string familyOf(nint vtable, Dictionary<nint, string> families) =>
            families.TryGetValue(vtable, out var name) ? name : $"unknown-module(0x{vtable:x})";

        /// <summary>
        /// Pointers must go out as text: System.Text.Json refuses to serialize System.IntPtr,
        /// which silently dropped every visibility-queue payload until this was added.
        /// </summary>
        private static string Hex(nint value) => $"0x{(ulong)value:x}";

        private sealed record DiagEvent(int Id, string Kind, string Path, int Thread, object Payload);
    }
}
