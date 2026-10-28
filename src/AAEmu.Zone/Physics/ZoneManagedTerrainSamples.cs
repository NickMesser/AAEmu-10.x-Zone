using System.Numerics;

namespace AAEmu.Zone.Terrain;

public sealed partial class ZoneTerrainHeightField
{
    /// <summary>
    /// Reads one current authored two-meter quad without the compatibility callbacks' native
    /// caches. Those caches intentionally survive cell unload; a managed collision world must
    /// instead stop colliding with an unloaded cell immediately. Zero heights are valid samples.
    /// Surface slot 31 is an authored hole, including leaves without any samples.
    /// Original heightfield mesh/ray preparation (35155180/35158080) calls the scalar height
    /// callback separately at all four global vertices. Each call selects its own coarse quad;
    /// this deliberately differs from the renderer's single GetHeightQuad expansion.
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
            heights = new(CurrentHeight(gridX, gridY), CurrentHeight(gridX + 1, gridY),
                CurrentHeight(gridX, gridY + 1), CurrentHeight(gridX + 1, gridY + 1));
            if (!float.IsFinite(heights.X) || !float.IsFinite(heights.Y) ||
                !float.IsFinite(heights.Z) || !float.IsFinite(heights.W)) return false;
            surfaceIndex = (((gridX >> 9) * 8 + (gridY >> 9)) * 32) + slot;
            return true;
        }

        float CurrentHeight(int x, int y)
        {
            x = Math.Clamp(x, 0, 4095); y = Math.Clamp(y, 0, 4095);
            var current = GetPhysicsLeaf(x, y);
            return current is null || current.Samples.Length == 0 ? 0 : current.GetHeightQuad(x & 31, y & 31).X;
        }
    }
}
