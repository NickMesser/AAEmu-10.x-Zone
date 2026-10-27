using System.Runtime.InteropServices;

namespace AAEmu.Zone.Native.StaticObjects;

public sealed unsafe partial class ZoneNativeStaticObjectServices
{
    private TerrainRenderNodeQuery? _terrainRenderNodeQuery;

    /// <summary>
    /// 3015D4A0's post-load notification, including an empty successful terrain chunk. Queries
    /// the scene octree for runtime type-13 nodes overlapping [cell*1024, cell*1024+1024] × [0,3000]
    /// and calls their recovered terrain update. This owner admits zero render meshes; nonzero
    /// meshes require a native CTerrain height producer before their vertex update can run.
    /// The returned count describes matching nodes, not complete cell or collision readiness.
    /// </summary>
    public int FinishTerrainLoad(int cellX, int cellY)
    {
        ValidateTerrainCell(cellX, cellY);
        using var operation = Enter();
        RequireIdleBrushWorld();
        var octree = Marshal.ReadIntPtr(_lease.ThreeDEngine + 0x5dc0);
        if (octree == 0) return 0; // Exact enclosing native branch, based on actual engine state.
        if (_terrainRenderNodeQuery is not null)
            throw new InvalidOperationException("A previous native terrain query still owns its storage.");

        var query = new TerrainRenderNodeQuery();
        _terrainRenderNodeQuery = query; // Root native-written storage before invoking the octree query.
        int count;
        try
        {
            query.Vector = Allocate(new byte[16]); // native pointer, int count, int capacity
            query.Bounds = Marshal.AllocHGlobal(24);
            var bounds = (float*)query.Bounds;
            bounds[0] = cellX * 1024;
            bounds[1] = cellY * 1024;
            bounds[2] = 0;
            bounds[3] = cellX * 1024 + 1024;
            bounds[4] = cellY * 1024 + 1024;
            bounds[5] = 3000; // 302844B0
            query.FreePointer = Marshal.ReadIntPtr(_lease.TerrainModule + 0x2733a0);
            Require(query.FreePointer != 0, "The terrain module's native CRT free import is unavailable.");
            query.Free = Marshal.GetDelegateForFunctionPointer<VoidCall>(query.FreePointer);
            var collect = Marshal.GetDelegateForFunctionPointer<TerrainRenderNodeQueryCall>(_lease.TerrainModule + 0x213560);
            Native(() => collect(octree, query.Vector, 13, query.Bounds, -1f)); // 3027E318

            Require(Marshal.ReadIntPtr(_lease.ThreeDEngine + 0x5dc0) == octree,
                "The scene octree changed during its synchronous terrain query.");
            var items = Marshal.ReadIntPtr(query.Vector);
            count = Marshal.ReadInt32(query.Vector + 8);
            var capacity = Marshal.ReadInt32(query.Vector + 12);
            Require(count >= 0 && capacity >= count && (count == 0 || items != 0),
                "The scene octree returned an invalid native render-node vector.");

            // Preflight the entire result before native updates. 30238A70 otherwise samples
            // DAT302FA8A8's CTerrain; the managed callback heightfield is not that native object.
            for (var i = 0; i < count; i++)
            {
                var node = Marshal.ReadIntPtr(items + (nint)((long)i * 8));
                Require(node != 0, "The scene octree returned a null render node.");
                if (Marshal.ReadIntPtr(node + 0x58) != 0)
                    throw new NotSupportedException("Render-node mesh updates require a matching native CTerrain height owner; the current terrain producer admits zero meshes.");
            }
            var update = Marshal.GetDelegateForFunctionPointer<VoidCall>(_lease.TerrainModule + 0x238a70);
            for (var i = 0; i < count; i++)
            {
                var node = Marshal.ReadIntPtr(items + (nint)((long)i * 8));
                Native(() => update(node));
            }
        }
        catch (Exception error)
        {
            // A native fault retains all roots. On ordinary admission failure, preserve both
            // the original error and any cleanup failure; uncertain cleanup also retains roots.
            if (!_poisoned)
                try { RetireTerrainRenderNodeQuery(query); }
                catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
            throw;
        }
        RetireTerrainRenderNodeQuery(query);
        return count;
    }

    /// <summary>
    /// 30156FC0's first operation: invoke the real cell scheduler's 30174BB0 with the engine-cell
    /// coordinates before node/sample retirement. Native code visits its queue and retires only
    /// the first eligible matching task. This does not claim that all cell object work has drained.
    /// </summary>
    public void BeginTerrainUnload(int cellX, int cellY)
    {
        ValidateTerrainCell(cellX, cellY);
        using var operation = Enter();
        RequireIdleBrushWorld();
        var scheduler = Marshal.ReadIntPtr(_lease.ThreeDEngine + 0x558);
        if (scheduler == 0)
            throw new NotSupportedException("Terrain unload requires the actual engine cell scheduler.");
        var begin = Marshal.ReadIntPtr(scheduler + 0x10);
        var end = Marshal.ReadIntPtr(scheduler + 0x18);
        Require(end >= begin && (end - begin) % 8 == 0 && (begin != 0 || end == 0),
            "The native terrain scheduler has an invalid task vector.");
        var cancel = Marshal.GetDelegateForFunctionPointer<TerrainCellCancelCall>(_lease.TerrainModule + 0x174bb0);
        Native(() => cancel(scheduler, cellX, cellY));
        Require(Marshal.ReadIntPtr(_lease.ThreeDEngine + 0x558) == scheduler,
            "The native terrain scheduler changed during cell cancellation.");
    }

    private void RetireTerrainRenderNodeQuery(TerrainRenderNodeQuery query)
    {
        if (query.Vector != 0 && Marshal.ReadIntPtr(query.Vector) is var items && items != 0)
        {
            Require(query.Free is not null && query.FreePointer != 0 &&
                Marshal.ReadIntPtr(_lease.TerrainModule + 0x2733a0) == query.FreePointer,
                "The terrain query's native CRT allocator changed before vector retirement.");
            Native(() => query.Free!(items)); // MSVCR100.dll!free, never Marshal.FreeHGlobal.
            Marshal.WriteIntPtr(query.Vector, 0);
        }
        Marshal.FreeHGlobal(query.Vector);
        Marshal.FreeHGlobal(query.Bounds);
        query.Vector = query.Bounds = 0;
        _terrainRenderNodeQuery = null;
    }

    private static void ValidateTerrainCell(int x, int y)
    {
        if ((uint)x >= 8 || (uint)y >= 8)
            throw new ArgumentOutOfRangeException(nameof(x), "Terrain notifications require a cell in the native 8 by 8 window.");
    }

    private sealed class TerrainRenderNodeQuery
    {
        internal nint Vector, Bounds, FreePointer;
        internal VoidCall? Free;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void TerrainRenderNodeQueryCall(nint octree, nint vector, int type, nint bounds, float viewDistance);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void TerrainCellCancelCall(nint scheduler, int cellX, int cellY);
}
