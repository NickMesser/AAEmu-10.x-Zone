using System.Numerics;

namespace AAEmu.Zone.Simulation;

/// <summary>
/// The fixed terrain heightfield supplied by 300D9CC0 to IPhysicalWorld+88. Coordinates and
/// callbacks belong to one engine-local 8192-meter window. This describes the physical shape;
/// the required physics backend must construct and own its actual geometry and entities.
/// </summary>
public sealed class ZoneTerrainPhysicsDescriptor
{
    public const int MaterialCount = 2048;
    public Vector3 BasisX => Vector3.UnitX;
    public Vector3 BasisY => Vector3.UnitY;
    public Vector3 BasisZ => Vector3.UnitZ;
    public Vector3 Origin => Vector3.Zero;
    public Vector2 Step => new(2, 2);
    /// <summary>35154F40 computes these reciprocals before copying the shape.</summary>
    public Vector2 InverseStep => new(.5f, .5f);
    public int SizeX => 4096;
    public int SizeY => 4096;
    public int StrideX => 4097;
    public int StrideY => 1;
    public float HeightScale => 1;
    public ushort SurfaceMask => 0x7ff;
    public ushort HeightMask => 0xffe0;
    public int HoleSurfaceIndex => 0x7ff;
    /// <summary>35154F40 derives this shift from the lowest set bit of SurfaceMask.</summary>
    public int SurfaceBitShift => 0;
    public Func<int, int, float> HeightCallback { get; }
    public Func<int, int, uint> SurfaceCallback { get; }

    public ZoneTerrainPhysicsDescriptor(ZoneTerrainHeightField heights)
    {
        ArgumentNullException.ThrowIfNull(heights);
        HeightCallback = heights.GetPhysicsHeight;
        SurfaceCallback = heights.GetPhysicsSurfaceIndex;
    }
}
