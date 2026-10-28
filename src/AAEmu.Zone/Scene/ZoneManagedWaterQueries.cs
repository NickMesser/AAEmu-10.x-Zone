using System.Buffers.Binary;
using System.Numerics;
using AAEmu.Zone.Content;

namespace AAEmu.Zone.Scene;

/// <summary>Explicit server flags2 profile; active ocean vortex/dynamic-area processors are not admitted.</summary>
public enum ZoneManagedWaterQueryProfile { StaticServerWithoutOceanVortex = 1 }

public sealed record ZoneManagedWaterVolumeReport(ZoneMapCoordinate EngineCell, int SourceOrder, ulong VolumeId,
    byte VolumeType, bool HasPhysicalArea, string? Reason, Vector3? BoundsMin, Vector3? BoundsMax, float? SurfaceHeight);

public sealed record ZoneManagedWaterInventoryReport(int AuthoredRows, int LoadedPhysicalAreas, int UnsupportedRows,
    IReadOnlyList<ZoneManagedWaterVolumeReport> Volumes, IReadOnlyList<string> CoverageErrors)
{
    public bool Complete => UnsupportedRows == 0 && CoverageErrors.Count == 0;
}

/// <summary>
/// Actual ocean/static-water inventory for a managed scene. Resolves serialized lake and river
/// physical contours, ID deduplication, finite area/polygon eligibility and the server level query.
/// Render bounds, missing assets or a missing water owner never imply a dry result.
/// </summary>
public sealed class ZoneManagedWaterQueries : IDisposable
{
    public const float NoWaterLevel = -1e10f;
    private sealed record Volume(ZoneMapCoordinate Cell, ZoneManagedWaterVolumeGeometry Geometry);
    private readonly List<Volume> _volumes = [];
    private readonly HashSet<ZoneMapCoordinate> _liveCells = [];
    private readonly List<ZoneManagedWaterVolumeReport> _rows = [];
    private readonly List<string> _coverageErrors = [];
    private readonly List<string> _refusals = [];
    private int _unsupported;
    public float OceanLevel { get; }
    public bool IsDisposed { get; private set; }
    public ZoneManagedWaterInventoryReport Report => new(_rows.Count, _volumes.Count, _unsupported,
        _rows.AsReadOnly(), _coverageErrors.AsReadOnly());

    public ZoneManagedWaterQueries(float resolvedOceanLevel, IReadOnlyList<ZoneManagedSceneCellLoadReport> cells,
        ZoneManagedWaterQueryProfile profile)
    {
        ArgumentNullException.ThrowIfNull(cells);
        if (profile != ZoneManagedWaterQueryProfile.StaticServerWithoutOceanVortex)
            throw new NotSupportedException("Water queries require an explicit static server profile without an active ocean vortex or dynamic areas.");
        if (!float.IsFinite(resolvedOceanLevel)) throw new NotSupportedException("The world's resolved ocean level is not finite.");
        OceanLevel = resolvedOceanLevel;
        var ids = new HashSet<ulong>();
        foreach (var cell in cells)
        {
            var local = cell.Placement.EngineCell;
            if (cell.Placement.InvalidLocalX || cell.Placement.InvalidLocalY || (uint)local.X >= 8 || (uint)local.Y >= 8 ||
                !_liveCells.Add(local))
            { _coverageErrors.Add($"Water cell ({local.X},{local.Y}) has no unique valid loaded placement."); continue; }
            Load(cell.Objects.Objects, ZoneCellObjectAsset.Objects);
            Load(cell.Objects.BigObjects, ZoneCellObjectAsset.BigObjects);

            void Load(ZoneCellObjectContent? objects, ZoneCellObjectAsset kind)
            {
                if (objects is null)
                {
                    if (!cell.Objects.Diagnostics.Contains($"{kind}: authored asset is absent."))
                        _coverageErrors.Add($"Water inventory cannot read complete {kind} for cell ({local.X},{local.Y}).");
                    return;
                }
                foreach (var row in objects.Objects.OfType<ZoneCellWaterVolumeObject>())
                {
                    if (ids.Contains(row.VolumeId))
                    { Add(false, "Native volume ID already registered; no additional physical area."); continue; }
                    try
                    {
                        if (((row.Flags20 >> 24) & 7) != 0)
                            throw new NotSupportedException("Nonzero water object quality requires its actual configured quality producer.");
                        if (row.VolumeType is not (2 or 3))
                            throw new NotSupportedException("This water family requires its own native geometry/area processor.");
                        if (row.VolumeType == 2) ids.Add(row.VolumeId); //lake registers its ID even without an area
                        if (row.PhysicsVertices.Count <= 3 || row.VolumeType == 3 && (row.PhysicsVertices.Count & 1) != 0)
                        { Add(false, "Native physical-contour count does not create an area."); continue; }
                        var geometry = Geometry(row, local);
                        _volumes.Add(new(local, geometry)); ids.Add(row.VolumeId);
                        _rows.Add(new(local, row.SourceOrder, row.VolumeId, row.VolumeType, true, null,
                            geometry.BoundsMin, geometry.BoundsMax, geometry.SurfaceHeight));
                    }
                    catch (Exception error) when (error is NotSupportedException or InvalidDataException)
                    { _unsupported++; _refusals.Add(error.Message); Add(false, error.Message); }
                    void Add(bool physical, string reason) => _rows.Add(new(local, row.SourceOrder, row.VolumeId,
                        row.VolumeType, physical, reason, null, null, null));
                }
            }
        }
    }

    /// <summary>
    /// Certified ceiling for the complete horizontal inventory, including native float projection
    /// rounding. It is not a sampled water cache. Area/range/max4 eligibility can only remove
    /// candidate surfaces, making this useful for proving a character is above all possible water.
    /// </summary>
    public float MaximumPotentialWaterLevel
    {
        get
        {
            RequireComplete();
            var level = OceanLevel <= -1_000_000 ? NoWaterLevel : OceanLevel;
            foreach (var volume in _volumes) level = MathF.Max(level, volume.Geometry.MaximumPotentialLevel);
            return level;
        }
    }

    /// <summary>
    /// 3013FCB0 with the server's flag2. The actor passes its full entity-world AABB
    /// diagonal; the area spawner passes the resolved NPC brief radius instead.
    /// Zero range refuses any potentially eligible river, whose exact native mesh-grid
    /// boundary classification is not owned. Lake/ocean zero-range queries remain admitted.
    /// </summary>
    public float GetWaterLevel(Vector3 actualQueryPoint, float areaQueryRange)
    {
        RequireComplete(); Validate(actualQueryPoint);
        if (!float.IsFinite(areaQueryRange) || areaQueryRange < 0)
            throw new ArgumentOutOfRangeException(nameof(areaQueryRange));
        RequirePoint(actualQueryPoint);
        if (areaQueryRange == 0 && _volumes.Any(volume => volume.Geometry.NeedsUnownedZeroRangeRiverMesh(actualQueryPoint)))
            throw new NotSupportedException("Zero-range water queries in a river area require the original river triangle-mesh grid and boundary predicates.");
        var level = OceanLevel; var contacts = 0;
        //Original medium0 insertion is at the head: newest registered physical areas first.
        for (var i = _volumes.Count - 1; i >= 0 && contacts < 4; i--)
        {
            var geometry = _volumes[i].Geometry;
            if (!geometry.Contains(actualQueryPoint, areaQueryRange)) continue;
            contacts++;
            //3013FCB0 consumes at most four buoyancy records, including records it later
            //discards because this original plane-origin test is zero.
            if (geometry.PlaneOrigin.X + geometry.PlaneOrigin.Y == 0) continue;
            var candidate = geometry.LevelAt(actualQueryPoint);
            if (candidate > level) level = candidate;
        }
        return level <= -1_000_000 ? NoWaterLevel : level;
    }

    /// <summary>
    /// A query-point certificate for any nonnegative body range. It uses the actual area
    /// world AABB and conservatively ignores polygon, mesh, range and max4 admission.
    /// It never substitutes an invented entity-world-bounds diagonal.
    /// </summary>
    public float MaximumPotentialWaterLevelAt(Vector3 actualQueryPoint)
    {
        RequireComplete(); Validate(actualQueryPoint); RequirePoint(actualQueryPoint);
        var level = OceanLevel <= -1_000_000 ? NoWaterLevel : OceanLevel;
        foreach (var volume in _volumes)
            if (volume.Geometry.OverlapsWorldBounds(actualQueryPoint))
                level = MathF.Max(level, volume.Geometry.MaximumPotentialLevel);
        return level;
    }

    private void RequirePoint(Vector3 point)
    {
        var cell = new ZoneMapCoordinate((int)MathF.Floor(point.X / 1024), (int)MathF.Floor(point.Y / 1024));
        if (!_liveCells.Contains(cell))
            throw new NotSupportedException("The water query point has no live complete scene-cell inventory.");
    }

    public void RetireCell(ZoneMapCoordinate engineCell)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (!_liveCells.Remove(engineCell)) return;
        _volumes.RemoveAll(volume => volume.Cell == engineCell);
    }
    public void Dispose() { if (IsDisposed) return; IsDisposed = true; _liveCells.Clear(); _volumes.Clear(); }
    private void RequireComplete()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (_unsupported != 0 || _coverageErrors.Count != 0)
            throw new NotSupportedException("Water level requires complete admitted ocean/static-area geometry: " +
                (_coverageErrors.FirstOrDefault() ?? _refusals[0]));
    }

    private static ZoneManagedWaterVolumeGeometry Geometry(ZoneCellWaterVolumeObject row, ZoneMapCoordinate cell)
    {
        var plane = row.AuthoredPlane;
        if (plane.X != 0 || plane.Y != 0 || plane.Z != 1 || !float.IsFinite(plane.W))
            throw new NotSupportedException("Tilted or invalid water planes require their actual area mesh/query processor.");
        if (row.VolumeType == 2 && row.Vertices.Count < 3 || row.VolumeType == 3 && row.Vertices.Count != 4)
            throw new NotSupportedException("Water render setup did not establish the native lake/river physicalization family.");
        var translation = new Vector3(cell.X * 1024f, cell.Y * 1024f, 0);
        if (row.VolumeType == 3)
        {
            plane.W = -((row.BoundsMax.X * plane.X + row.BoundsMax.Y * plane.Y) + row.BoundsMax.Z * plane.Z);
            var maximum = 0f;
            foreach (var vertex in row.Vertices) maximum = MathF.Max(maximum, Project(vertex, plane).Z);
            if (maximum != row.BoundsMax.Z && plane.Z > 0) plane.W -= (row.BoundsMax.Z - maximum) * plane.Z;
        }
        else plane.W = -(row.Vertices[0].Z + 0f);
        var localPlane = plane;
        plane.W -= (plane.X * translation.X + translation.Y * plane.Y) + plane.Z * 0f;
        var vertices = row.PhysicsVertices.Select(vertex =>
        {
            if (row.VolumeType == 3) vertex = Project(vertex, localPlane);
            vertex = new(translation.X + vertex.X, translation.Y + vertex.Y, vertex.Z + 0f);
            return Project(vertex, plane);
        }).ToArray();
        var depth = BinaryPrimitives.ReadSingleLittleEndian(row.RawPayload[0x6b..]);
        var flow = BinaryPrimitives.ReadSingleLittleEndian(row.RawPayload[0x6f..]);
        if (!float.IsFinite(flow)) throw new NotSupportedException("Non-finite river-flow inputs have no admitted area query processor.");
        return new(vertices, depth, row.VolumeType == 3);
    }

    private static Vector3 Project(Vector3 point, Vector4 plane)
    {
        Validate(point);
        var distance = (((point.X * plane.X + point.Y * plane.Y) + point.Z * plane.Z) + plane.W) /
            ((plane.X * 0f + plane.Y * 0f) + plane.Z);
        var offset = -distance * 0f;
        var result = new Vector3(offset + point.X, offset + point.Y, point.Z - distance); Validate(result); return result;
    }
    private static void Validate(Vector3 point)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z))
            throw new InvalidDataException("Water query coordinates must be finite.");
    }
}
