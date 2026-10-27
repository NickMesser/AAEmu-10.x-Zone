namespace AAEmu.Zone.Terrain;

public sealed partial class ZoneTerrainHeightField
{
    private readonly PhysicsHeightEntry[] _physicsHeights = new PhysicsHeightEntry[1024];
    private readonly PhysicsSurfaceEntry[] _physicsSurfaces = new PhysicsSurfaceEntry[1024];

    /// <summary>
    /// 300D5EF0: height at signed integer two-meter grid coordinates, clamped to 0..4095 for
    /// sampling. Packed heights retain negative/zero values and use bilinear coarse expansion.
    /// This callback shares the current seam samples but has its own native direct-mapped cache.
    /// </summary>
    public float GetPhysicsHeight(int gridX, int gridY)
    {
        lock (_gate)
        {
            ref var entry = ref _physicsHeights[(gridX & 31) * 32 + (gridY & 31)];
            // Native stores ushort keys, then compares them with the complete uint input.
            if (entry.X == unchecked((uint)gridX) && entry.Y == unchecked((uint)gridY))
                return entry.Value;
            entry.X = unchecked((ushort)gridX);
            entry.Y = unchecked((ushort)gridY);
            var x = Math.Clamp(gridX, 0, 4095);
            var y = Math.Clamp(gridY, 0, 4095);
            var leaf = GetPhysicsLeaf(x, y);
            entry.Value = leaf is null || leaf.Samples.Length == 0 ? 0 : leaf.GetHeightQuad(x & 31, y & 31).X;
            return entry.Value;
        }
    }

    /// <summary>
    /// 300D5FF0: packed surface selects an entry in the 2048-element physical material map;
    /// surface 31 and leaves without samples return the hole marker 2047. Native world creation
    /// preallocates every leaf (300D7910/300E6FD0), including cells that have never been loaded.
    /// Sampling clamps signed two-meter grid coordinates, while material indexing uses the original
    /// coordinates with int32 multiplication and truncation. The return preserves native uint bits.
    /// </summary>
    public uint GetPhysicsSurfaceIndex(int gridX, int gridY)
    {
        lock (_gate)
        {
            ref var entry = ref _physicsSurfaces[(gridX & 31) * 32 + (gridY & 31)];
            if ((entry.Key & 0x3fff) == unchecked((uint)gridX) &&
                ((entry.Key >> 14) & 0x3fff) == unchecked((uint)gridY))
                return entry.Value;
            entry.Key = (unchecked((uint)gridX) & 0x3fff) | ((unchecked((uint)gridY) & 0x3fff) << 14);
            var x = Math.Clamp(gridX, 0, 4095);
            var y = Math.Clamp(gridY, 0, 4095);
            var leaf = GetPhysicsLeaf(x, y);
            var surface = leaf is null || leaf.Samples.Length == 0 ? 31 : leaf.GetPhysicsSurface(x & 31, y & 31);
            var cellX = unchecked(gridX * 2) / 1024;
            var cellY = unchecked(gridY * 2) / 1024;
            entry.Value = surface == 31 ? 2047u : unchecked((uint)((cellX * 8 + cellY) * 32 + surface));
            return entry.Value;
        }
    }

    /// <summary>
    /// 300D4060 clears both native caches before a nonempty cell import. Unloading samples and
    /// empty chunks do not clear them. Zeroed keys intentionally make the first (0,0) query return
    /// zero until another coordinate evicts its entry, even when terrain samples are present.
    /// </summary>
    public void ResetPhysicsCache()
    {
        lock (_gate) ResetPhysicsCacheCore();
    }

    private void ResetPhysicsCacheCore()
    {
        Array.Clear(_physicsHeights);
        Array.Clear(_physicsSurfaces);
    }

    private NodeSamples? GetPhysicsLeaf(int x, int y) =>
        _cells[(x >> 9) * 8 + (y >> 9)]?.Leaves[((x >> 5) & 15) * 16 + ((y >> 5) & 15)];

    private struct PhysicsHeightEntry
    {
        public ushort X;
        public ushort Y;
        public float Value;
    }

    private struct PhysicsSurfaceEntry
    {
        public uint Key;
        public uint Value;
    }

    internal sealed partial class NodeSamples
    {
        // Surface IDs use the lower coarse sample, without the height interpolation.
        public int GetPhysicsSurface(int x, int y) => Samples[(x >> _shift) * _size + (y >> _shift)] & 31;
    }
}
