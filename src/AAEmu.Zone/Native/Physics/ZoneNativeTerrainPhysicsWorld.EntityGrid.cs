using System.Numerics;
using System.Runtime.InteropServices;
using static AAEmu.Zone.Native.Engine.ZoneNativePhysicsModule;

namespace AAEmu.Zone.Native.Physics;

public sealed unsafe partial class ZoneNativeTerrainPhysicsWorld
{
    private readonly TerrainEntityGridState _standaloneEntityGrid = new();
    private TerrainEntityGridState EntityGridState => _engineLease?.EntityGridState ?? _standaloneEntityGrid;

    /// <summary>
    /// Applies CTerrain initialization's entity grid after the initialized heightfield registration
    /// and before scene bodies: Z-up, zero origin, 1024 by 1024 cells of 8 units, POD scale exponent 0.
    /// This is the native 8192-unit engine span, independent of the selected Zone's cell rectangle.
    /// </summary>
    /// <remarks>
    /// The first call requires an empty native entity registry. Native grid reconfiguration unlinks
    /// existing entities and frees grid storage, so this boundary never reconfigures an existing grid.
    /// Repeated calls validate the same world-owned grid without invoking native setup again. An
    /// engine borrower retains this initialization across adapter disposal/recreation; CSystem owns
    /// its storage until world release. Heightfield replicas are separate and may already be installed.
    /// Engine borrowers also require CTerrain's native initialization gate to permit grid setup.
    /// </remarks>
    public void ConfigureTerrainEntityGrid()
    {
        using var operation = Enter();
        RequireInstalled();
        var state = EntityGridState;
        if (state.Configured)
        {
            RequireTerrainEntityGrid();
            return;
        }
        if (state.Started)
            throw new InvalidOperationException("Native terrain entity-grid initialization did not complete and cannot be retried.");
        if (Marshal.ReadIntPtr(_world + 0x3d0) != 0)
            throw new InvalidOperationException("An existing native entity grid is not owned by this world initialization.");
        // 3507E5A0 waits on this lock itself. Reject contention before committing initialization;
        // acquiring it here would deadlock the native call.
        if (Marshal.ReadInt32(_world + 0x57c8) != 0)
            throw new InvalidOperationException("The native terrain entity-grid lock is already held.");
        // 35082F00 increments +42C for full bodies; 3507E990 increments +548 for placeholders.
        // 3508B860's five private heightfield replicas do not enter either registry.
        // 3507DE80 initializes the command queue at +51A0 to null and both cursors to
        // 0x2000; the cursor is not a zero-based pending-byte count.
        if (Marshal.ReadInt32(_world + 0x42c) != 0 || Marshal.ReadInt32(_world + 0x548) != 0 ||
            Marshal.ReadInt32(_world + 0x5798) != 0 ||
            Marshal.ReadIntPtr(_world + 0x51a0) != 0 ||
            Marshal.ReadInt32(_world + 0x51a8) != 0x2000 || Marshal.ReadInt32(_world + 0x51ac) != 0x2000 ||
            Marshal.ReadInt32(_world + 0x51bc) != 0 ||
            Marshal.ReadInt32(_world + 0x54d0) != 0)
            throw new InvalidOperationException("Configure the terrain entity grid before native scene bodies, workers or queued operations.");
        _engineLease?.RequireTerrainEntityGridSetupGate();

        var origin = Vector3.Zero;
        state.Started = true; // Native allocation may partially change the world before failing.
        try
        {
            // CTerrain300CFFA4..300D0007 -> IPhysicalWorld+58/3507E5A0. The return value is
            // the released native grid lock's old value, not a success/failure status.
            Virtual<ConfigureGridCall>(_world, 0x58)(_world, 2, &origin, 1024, 1024, 8f, 8f, 0);
            CompleteCall();
            state.Grid = Marshal.ReadIntPtr(_world + 0x3d0);
            ValidateTerrainEntityGrid(_world, state);
            state.Configured = true;
        }
        catch (Exception error) { NativeInvocationFailed(error); throw; }
    }

    private void RequireTerrainEntityGrid()
    {
        var state = EntityGridState;
        if (!state.Configured)
            throw new InvalidOperationException("ConfigureTerrainEntityGrid must complete before native static scene queries or brush creation.");
        try { ValidateTerrainEntityGrid(_world, state); }
        catch (Exception error) { NativeInvocationFailed(error); throw; }
    }

    // Call only under the owning world's/engine's operation gate. Grid nodes and the entity pool
    // may grow normally; the top-level paged-grid allocation and fixed spatial profile stay exact.
    internal static void ValidateTerrainEntityGrid(nint world, TerrainEntityGridState state)
    {
        if (state.Grid == 0 || Marshal.ReadIntPtr(world + 0x3d0) != state.Grid ||
            Marshal.ReadIntPtr(world + 0x3d8) == 0)
            throw new InvalidOperationException("The native terrain entity grid was retired or replaced outside its owner.");
        ReadOnlySpan<int> expected =
        [
            0x3f800000, 0, 0, 0, 0x3f800000, 0, 0, 0, 0x3f800000
        ];
        for (var i = 0; i < expected.Length; i++)
            if (Marshal.ReadInt32(world + 0x358 + i * 4) != expected[i])
                throw new InvalidOperationException("The native terrain entity-grid basis changed outside its owner.");
        if (Marshal.ReadInt32(world + 0x380) != 0 || Marshal.ReadInt32(world + 0x384) != 0 ||
            Marshal.ReadInt32(world + 0x388) != 0 ||
            Marshal.ReadInt32(world + 0x38c) != 0x41000000 || Marshal.ReadInt32(world + 0x390) != 0x41000000 ||
            Marshal.ReadInt32(world + 0x394) != 0x3e000000 || Marshal.ReadInt32(world + 0x398) != 0x3e000000 ||
            Marshal.ReadInt32(world + 0x39c) != 1024 || Marshal.ReadInt32(world + 0x3a0) != 1024 ||
            Marshal.ReadInt32(world + 0x3a4) != 1 || Marshal.ReadInt32(world + 0x3a8) != 1024 ||
            Marshal.ReadInt32(world + 0x3ac) != 0 || Marshal.ReadInt32(world + 0x3b0) != 1 ||
            Marshal.ReadInt32(world + 0x3b4) != 2 || Marshal.ReadInt32(world + 0x420) != 0 ||
            Marshal.ReadInt32(world + 0x3c0) != 10 || Marshal.ReadInt32(world + 0x3c4) != 1023 ||
            Marshal.ReadInt32(world + 0x3c8) != 8191)
            throw new InvalidOperationException("The native terrain entity-grid profile changed outside its owner.");
    }

    internal sealed class TerrainEntityGridState
    {
        internal bool Started;
        internal bool Configured;
        internal nint Grid;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int ConfigureGridCall(nint world, int upAxis, Vector3* origin, int countX,
        int countY, float stepX, float stepY, int podScaleExponent);
}
