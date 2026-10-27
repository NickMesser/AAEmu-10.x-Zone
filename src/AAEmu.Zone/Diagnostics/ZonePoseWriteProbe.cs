using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

using AAEmu.Zone.Actors;
using AAEmu.Zone.Content;
using AAEmu.Zone.Native.Entities;
using AAEmu.Zone.Native.Physics;

namespace AAEmu.Zone.Diagnostics;

/// <summary>
/// Pose-write ABI probe (evidence mode, not a runtime service). Answers ONE question with
/// runtime evidence: how does a native living body's position move? Nothing here is assumed -
/// every reported hit is an observed change of the vtable +0x48 read-back across a real world step.
///
/// RE basis (cryphysics_dedicate.dll, image base 0x35000000; Ghidra 12.1.2 headless decompiles in
/// AAEmuResearch/ZoneAuthority/pose-write/decompiled and decompiled-vtable):
///   * The living proxy vtable recovered at runtime: +0x38 = 35028BF0 SetParams, +0x40 = 35025040
///     GetParams, +0x48 = 35029A30 GetStatus, +0x58/+0x60 = physical disable/enable,
///     +0x70 = simulation class, +0x78 = state export (serializes "velRequested" from +0x1C0).
///   * FUN_3502A300 at vtable +0x50 is the living action handler - its own literal reads
///     "CLivingEntity:Action(action_move)". Signature: (entity, pe_action*, threadSafe), where
///     pe_action = { int type; float x; float y; float z; int mode; }.
///       - type 1 = move. mode 0/3 = ABSOLUTE (writes the position triple at +0x1CC/+0x38/+0x1D4),
///         mode 2 = RELATIVE (adds the delta to the same triple); both set the dirty flag +0x77 |= 2.
///   * The queued producer (FUN_35023200/FUN_35023650) carries BOTH parameter and action records;
///     a sweeping SetParams probe over every registered parameter type moved nothing, because a
///     pose change is an action record, not a parameter.
///
/// The probe requires a body-capable world: a bare dedicated CSystem publishes a null living
/// factory at world vtable +0xb0, so it must run inside the composed framework engine (the
/// --probe-pose-write mode, next to --check-scene).
/// </summary>
internal static unsafe class ZonePoseWriteProbe
{
    private const int TableEntries = 64;
    private const int PayloadBytes = 0x400;
    private const long SizeTableRva = 0x37c640;
    private const long ClassTableRva = 0x37c500;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int SetParamsCall(nint entity, void* parameters, int threadSafe);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int ActionCall(nint entity, void* action, int threadSafe);

    /// <summary>pe_action as the action handler reads it: type, x, y, z, mode.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ActionRecord
    {
        public int Type;
        public float X;
        public float Y;
        public float Z;
        public int Mode;
    }

    /// <summary>One observed type application; only a real read-back delta is recorded here.</summary>
    internal readonly record struct Hit(int Type, int PayloadSize, int MarkerOffset, string Detail);

    /// <summary>
    /// Sweeps the registered parameter types against a fresh body each time and returns the
    /// observed hits. Only a read-back that EQUALS the marker counts as a write: a blind
    /// application that merely changes (or NaNs) the body is recorded as a refusal, never as an
    /// ABI finding. Bodies retire inside the sweep.
    /// </summary>
    internal static IReadOnlyList<Hit> Run(ZoneNativeTerrainPhysicsWorld world,
        ZoneNativeEntityServices? entities, ZoneActorModel? actorModel,
        ZoneActorPhysicalizationRequest request, Action<string> trace, Vector3 spawn, float groundZ)
    {
        var hits = new List<Hit>();
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(trace);

        var module = GetModuleHandleW("cryphysics_dedicate.dll");
        if (module == 0) throw new InvalidOperationException("cryphysics_dedicate.dll is not loaded in this process.");
        trace($"Pose-write probe: cryphysics_dedicate.dll base=0x{module:X}, size table +0x{SizeTableRva:X}, class table +0x{ClassTableRva:X}.");

        var sizes = new int[TableEntries];
        for (var i = 0; i < TableEntries; i++) sizes[i] = Marshal.ReadInt32((nint)(module + (long)SizeTableRva + i * 4));
        // The table is dense from type 0; the first type whose size is 0 ends the registered set
        // (a later non-zero entry is unrelated data, as the first run's garbage tail showed).
        var registered = new List<int>();
        for (var type = 0; type < TableEntries; type++)
        {
            if (sizes[type] == 0) break;
            registered.Add(type);
        }
        trace($"Pose-write probe: {registered.Count} registered parameter types " +
            $"({string.Join(",", registered.Select(type => $"{type}:{sizes[type]}B"))}).");

        // The action record is the write ABI; the parameter table stays in the evidence because it
        // is what proved the producer indexes payload sizes by the type word.
        //
        // Only the unbound body runs here. The BOUND body (the class the game moves) cannot exist
        // in this isolated scene by design: entity creation calls the IGame+0xE8 service the
        // isolated engine does not publish, and the engine terminates the process by name
        // ("Unsupported native IGame callback +0xE8. This isolated engine must terminate"), see
        // ZoneNativeEngine.GameServices. That observation belongs to a live --host session.
        ushort nextId = 40000;
        RunBody(world, null, null, request, trace, spawn, nextId, hits);
        return hits;
    }

    private static void RunBody(ZoneNativeTerrainPhysicsWorld world, ZoneNativeEntityServices? entities,
        ZoneActorModel? actorModel, ZoneActorPhysicalizationRequest request, Action<string> trace,
        Vector3 spawn, ushort nextId, List<Hit> hits)
    {
        ZoneNativeEntityServices.Entity? entity = null;
        ZoneNativeTerrainPhysicsWorld.LivingEntity? body = null;
        try
        {
            if (entities is not null && actorModel is not null)
            {
                // The production admission shape: the real entity is allocated and initialized first,
                // and the body is created bound to it (ZonePlayerActorFactory.CreateLivingBody :311-329).
                entity = entities.CreateInitialCharacter(actorModel, "pose-write-probe", spawn, Quaternion.Identity, 1f);
                body = world.CreateLivingEntity(request, spawn, Quaternion.Identity, entity.Id, entity);
            }
            else
                body = world.CreateLivingEntity(request, spawn, Quaternion.Identity, nextId++, foreignData: null);
            var vtable = Marshal.ReadIntPtr(body.Pointer);
            Record(trace, $"Pose-write probe: {(entities is null ? "unbound" : "bound")} body=0x{body.Pointer:X} vtable=0x{vtable:X}, one vtable dump per run:");
            for (var slotIndex = 0; slotIndex <= 0x78; slotIndex += 8)
            {
                var address = vtable == 0 ? 0 : Marshal.ReadIntPtr(vtable + slotIndex);
                Record(trace, $"Pose-write vtable +0x{slotIndex:X} = 0x{address:X}" +
                    (address >= 0x35000000 && address < 0x35200000 ? $" (cryphysics +0x{address - 0x35000000:X})" : ""));
            }
            var actionSlot = vtable == 0 ? 0 : Marshal.ReadIntPtr(vtable + 0x50);
            if (actionSlot == 0) throw new InvalidOperationException("The living proxy has a null action vtable at +0x50.");
            var action = Marshal.GetDelegateForFunctionPointer<ActionCall>(actionSlot);
            // FUN_35023650 takes the INLINE branch (apply now, no queue) only when body+0x47 > 6
            // and threadSafe > -1; otherwise the record is queued for a world update. The type byte
            // therefore decides which path this body can take at all.
            Record(trace, $"Pose-write probe: action entry 0x{actionSlot:X} (expected 0x3502A300, CLivingEntity:Action); " +
                $"body+0x47=0x{Marshal.ReadByte(body.Pointer + 0x47):X2} (inline path needs > 0x06), " +
                $"body+0x40=0x{Marshal.ReadInt32(body.Pointer + 0x40):X} (queue counter)");

            // (1) absolute move: mode 0 writes the position triple outright. The target is offset
            // from the current position so an unchanged body can never masquerade as a hit.
            var beforeAbsolute = body.GetStatus();
            var target = beforeAbsolute.Position + new Vector3(2f, 0f, 0f);
            var absolute = new ActionRecord { Type = 1, X = target.X, Y = target.Y, Z = target.Z, Mode = 0 };
            int absoluteResult = action(body.Pointer, &absolute, 0);
            Record(trace, $"ACTION submitted type=1 mode=0 target=({target.X},{target.Y},{target.Z}) result={absoluteResult} " +
                $"body+0x40=0x{Marshal.ReadInt32(body.Pointer + 0x40):X}");
            // The action is queued by FUN_35023650 and applied by a world update; the observation
            // spans several frames so a one-frame latency is visible rather than assumed.
            for (var frame = 0; frame < 4; frame++)
            {
                world.Step(TimeSpan.FromMilliseconds(33));
                var observed = body.GetStatus();
                Record(trace, $"ACTION absolute frame={frame} pos={observed.Position} match={Matches(observed.Position, target)}");
                if (Matches(observed.Position, target)) break;
            }
            var afterAbsolute = body.GetStatus();
            Record(trace, $"ACTION absolute type=1 mode=0 target=({target.X},{target.Y},{target.Z}) result={absoluteResult} " +
                $"beforePos={beforeAbsolute.Position} afterPos={afterAbsolute.Position} match={Matches(afterAbsolute.Position, target)}");
            // The production seam itself: apply-and-verify. On a body that defers actions it must
            // refuse by name with the observed evidence, never report a move.
            var seamTarget = body.GetStatus().Position + new Vector3(3f, 0f, 0f);
            var landed = world.TryApplyLivingMove(body, seamTarget, absolute: true, out var seamObserved, out var seamRefusal);
            Record(trace, $"SEAM TryApplyLivingMove landed={landed} requested={seamTarget} observed={seamObserved.Position} " +
                $"refusal={(seamRefusal.Length == 0 ? "<none>" : seamRefusal)}");
            if (!Matches(afterAbsolute.Position, target))
            {
                Record(trace, "Pose-write probe: the absolute action did not land the body on the requested position; " +
                    "the gates stay refused and no ABI is claimed.");
                return;
            }

            // (2) relative move: mode 2 adds the delta to the same triple - the per-frame walking seam.
            var delta = new Vector3(1.5f, 0f, 0f);
            var expected = afterAbsolute.Position + delta;
            var beforeRelative = body.GetStatus();
            var relative = new ActionRecord { Type = 1, X = delta.X, Y = delta.Y, Z = delta.Z, Mode = 2 };
            int relativeResult = action(body.Pointer, &relative, 0);
            for (var frame = 0; frame < 4; frame++)
            {
                world.Step(TimeSpan.FromMilliseconds(33));
                var observed = body.GetStatus();
                Record(trace, $"ACTION relative frame={frame} pos={observed.Position} match={Matches(observed.Position, expected)}");
                if (Matches(observed.Position, expected)) break;
            }
            var afterRelative = body.GetStatus();
            Record(trace, $"ACTION relative type=1 mode=2 delta=({delta.X},{delta.Y},{delta.Z}) result={relativeResult} " +
                $"beforePos={beforeRelative.Position} afterPos={afterRelative.Position} expected=({expected.X},{expected.Y},{expected.Z}) " +
                $"match={Matches(afterRelative.Position, expected)}");
            if (Matches(afterRelative.Position, expected))
            {
                var detail = $"action type=1 mode=2 result={relativeResult} beforePos={beforeRelative.Position} " +
                    $"afterPos={afterRelative.Position} expected={expected} vel={afterRelative.Velocity}";
                Record(trace, $"Pose-write HIT {detail}");
                hits.Add(new Hit(1, 20, 0x4, detail));
            }
        }
        catch (Exception error)
        {
            Record(trace, $"Pose-write probe: action probe failed: {error.GetType().Name}: {error.Message}");
        }
        finally
        {
            try { body?.Dispose(); } catch (Exception error) { Record(trace, $"Pose-write probe: body retirement: {error.Message}"); }
            try { entity?.Dispose(); } catch (Exception error) { Record(trace, $"Pose-write probe: entity retirement: {error.Message}"); }
        }
    }

    /// <summary>Evidence target for the per-type lines; the caller owns the directory. Flushed per
    /// line so an application that corrupts the world cannot erase the types that already ran.</summary>
    internal static string EvidencePath { get; set; } = string.Empty;

    private static void Record(Action<string> trace, string line)
    {
        trace(line);
        if (EvidencePath.Length == 0) return;
        try { File.AppendAllText(EvidencePath, line + Environment.NewLine); } catch { /* mirror is best effort */ }
    }

    private static bool Matches(Vector3 observed, Vector3 marker) =>
        Math.Abs(observed.X - marker.X) < 0.001f && Math.Abs(observed.Y - marker.Y) < 0.001f &&
        Math.Abs(observed.Z - marker.Z) < 0.001f;

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);
}
