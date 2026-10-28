using System.Numerics;

namespace AAEmu.Zone.Terrain;

public sealed partial class ZoneTerrainHeightField
{
    /// <summary>
    /// Reads one current authored two-meter quad without the compatibility callbacks' native
    /// caches. Those caches intentionally survive cell unload; a managed collision world must
    /// instead stop colliding with an unloaded cell immediately. Zero heights are valid samples.
    /// Surface slot 31 is an authored hole, including leaves without any samples.
    /// </summary>
    internal bool TryReadManagedPhysicsQuad(int gridX, int gridY, out Vector4 heights, out int surfaceIndex)
    {
        heights = default;
        surfaceIndex = 0;
        if ((uint)gridX >= 4096 || (uint)gridY >= 4096) return false;
        lock (_gate)
        {
            var leaf = GetPhysicsLeaf(gridX, gridY);
            if (leaf is null || leaf.Samples.Length == 0) return false;
            var localX = gridX & 31;
            var localY = gridY & 31;
            var slot = leaf.GetPhysicsSurface(localX, localY);
            if (slot == 31) return false;
            heights = leaf.GetHeightQuad(localX, localY);
            if (!float.IsFinite(heights.X) || !float.IsFinite(heights.Y) ||
                !float.IsFinite(heights.Z) || !float.IsFinite(heights.W)) return false;
            surfaceIndex = (((gridX >> 9) * 8 + (gridY >> 9)) * 32) + slot;
            return true;
        }
    }
}
