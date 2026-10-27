using System.Buffers.Binary;
using AAEmu.Zone.Content;

namespace AAEmu.Zone.Simulation;

/// <summary>
/// A resolved material reference, corresponding to IMaterial. Implementations must preserve identity
/// for shared materials. The scale and axis are mutable native material fields; submaterial topology
/// and physical surface IDs must be ready before the terrain catalog is constructed.
/// </summary>
public interface IZoneTerrainMaterial
{
    string Name { get; }
    int SubMaterialCount { get; }
    IZoneTerrainMaterial? GetSubMaterial(int index);
    int PhysicalSurfaceId { get; }
    float TerrainScale { get; set; }
    int TerrainProjectionAxis { get; set; }
}

/// <summary>
/// Required material service. Load implements the native material-manager load with arguments
/// (name, 1, 0, 0). ErrorMaterial is the manager's
/// error sentinel (+58); DefaultMaterial is its terrain fallback (+60). No numeric-ID fallback is
/// supplied here: resolving .mtl content and physical surface definitions remains the provider's job.
/// </summary>
public interface IZoneTerrainMaterialResolver
{
    IZoneTerrainMaterial? Load(string name);
    IZoneTerrainMaterial? ErrorMaterial { get; }
    IZoneTerrainMaterial DefaultMaterial { get; }
}

public sealed record ZoneTerrainSurfaceBinding(ZoneTerrainSurfaceRecord Surface,
    IZoneTerrainMaterial? Material, byte CellSurfaceId);

/// <summary>
/// Native world records and 8x8x32 loaded-window cell records (300DAF10/300D16F0). Record assignment
/// copies value fields while retaining the same material reference (300D32B0); managed strong
/// references provide ownership. Surface bindings survive terrain-sample unload (300D0030).
/// This does not construct renderer shader resources or a physical-world heightfield entity.
/// </summary>
public sealed class ZoneTerrainSurfaceBindings
{
    private readonly object _gate = new();
    private readonly ZoneTerrainSurfaceBinding[] _world;
    private readonly ZoneTerrainSurfaceBinding[] _cells = new ZoneTerrainSurfaceBinding[2048];
    public ZoneTerrainSurfaceContent Content { get; }

    public ZoneTerrainSurfaceBindings(ZoneTerrainSurfaceContent content, IZoneTerrainMaterialResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(resolver);
        Content = content;
        var empty = new ZoneTerrainSurfaceBinding(ZoneTerrainSurfaceRecord.Empty, null, 0);
        Array.Fill(_cells, empty);
        _world = new ZoneTerrainSurfaceBinding[content.Entries.Count];
        Array.Fill(_world, empty);
        // Even overwritten rows load and normalize their material before the next row is processed.
        foreach (var row in content.Rows)
        {
            var material = row.DetailMaterial.Length == 0 ? null : resolver.Load(row.DetailMaterial);
            if (material is null || ReferenceEquals(material, resolver.ErrorMaterial))
                material = RequireDefault(resolver);
            for (var i = 0; i < material.SubMaterialCount; i++)
                if (material.GetSubMaterial(i) is null)
                {
                    material = RequireDefault(resolver);
                    break;
                }
            material.TerrainScale = row.DetailScaleX;
            NormalizeMaterial(material);
            _world[row.Id] = new(row, material, 0);
        }
    }

    public ZoneTerrainSurfaceBinding GetBinding(int cellX, int cellY, int slot)
    {
        var index = CellIndex(cellX, cellY);
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, 32);
        lock (_gate) return _cells[index + slot];
    }

    /// <summary>300D1D60 runtime branch: ordinal-to-Id assignment; invalid IDs retain prior records.</summary>
    public void ApplyCellSurfaceTypes(int cellX, int cellY, ZoneTerrainCellSurfaceContent content)
    {
        var index = CellIndex(cellX, cellY);
        ArgumentNullException.ThrowIfNull(content);
        lock (_gate)
            for (var slot = 0; slot < Math.Min(content.WorldSurfaceIds.Count, 32); slot++)
                Assign(index, slot, content.WorldSurfaceIds[slot]);
    }

    /// <summary>
    /// 300D0890: after cell XML and node loading, version 24 applies all 32 header IDs. Version 23
    /// leaves XML bindings intact. A zero-length chunk never reaches this loader (3015D4A0).
    /// </summary>
    public void ApplyHeightmapSurfaceIds(int cellX, int cellY, ZoneTerrainHeightmapContent content)
    {
        var index = CellIndex(cellX, cellY);
        ArgumentNullException.ThrowIfNull(content);
        if (content.Header.Version != 24 || content.Header.ChunkSize == 0) return;
        lock (_gate)
            for (var slot = 0; slot < 32; slot++)
                Assign(index, slot, BinaryPrimitives.ReadInt32LittleEndian(content.SurfaceIdBytes[(slot * 4)..]));
    }

    /// <summary>
    /// 300D9CC0/300D9960: cell-major 2048-entry physical material map. A material with more than two
    /// submaterials uses submaterial 2; otherwise it uses the root. Unbound records map to zero.
    /// Constructing this table does not install physics callbacks or create a heightfield entity.
    /// </summary>
    public int[] CreatePhysicalMaterialMap()
    {
        var result = new int[2048];
        lock (_gate)
            for (var slot = 0; slot < 32; slot++)
                for (var cell = 0; cell < 64; cell++)
                {
                    var index = cell * 32 + slot;
                    var material = _cells[index].Material;
                    if (material is null) continue;
                    if (material.SubMaterialCount > 2)
                        material = material.GetSubMaterial(2) ??
                            throw new InvalidOperationException("Resolved terrain material lost submaterial 2.");
                    result[index] = material.PhysicalSurfaceId;
                }
        return result;
    }

    private void Assign(int cellIndex, int slot, int worldId)
    {
        if ((uint)worldId >= (uint)_world.Length) return;
        _cells[cellIndex + slot] = _world[worldId] with { CellSurfaceId = (byte)slot };
    }

    private static IZoneTerrainMaterial RequireDefault(IZoneTerrainMaterialResolver resolver) =>
        resolver.DefaultMaterial ?? throw new InvalidOperationException("The terrain material resolver has no default material.");

    private static void NormalizeMaterial(IZoneTerrainMaterial material)
    {
        // 30137920 overwrites the scale just assigned by 300D2510, and only touches six children.
        material.TerrainScale = 1;
        material.TerrainProjectionAxis = 0;
        for (var i = 0; i < Math.Min(material.SubMaterialCount, 6); i++)
        {
            var child = material.GetSubMaterial(i) ??
                throw new InvalidOperationException("The default terrain material has an empty submaterial slot.");
            child.TerrainScale = material.TerrainScale;
            var axis = child.Name.ToUpperInvariant() switch
            {
                "X" or "XP" => 1, "Y" or "YP" => 2, "Z" or "ZP" => 3,
                "XN" => 4, "YN" => 5, "ZN" => 6, _ => 0
            };
            // Unknown names retain the child's previous axis, rather than clearing it.
            if (axis != 0) child.TerrainProjectionAxis = axis;
        }
    }

    private static int CellIndex(int x, int y)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(x, 8);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, 8);
        return (x * 8 + y) * 32;
    }
}
