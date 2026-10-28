using System.Numerics;

namespace AAEmu.Zone.Physics;

public sealed partial class ZoneManagedTerrainGeometry
{
    /// <summary>
    /// Each initially intersecting authored triangle supplies its exact +Z exit and independent
    /// surface-normal fallback. The heightfield is a whole entity: a stance envelope must not
    /// Z-clip its triangles after entity admission. Missing cells/holes supply no geometry.
    /// These per-surface copied constraints do not reproduce native mesh contact ordering.
    /// </summary>
    public IReadOnlyList<ZonePhysicsDirectedUnprojection> GetUpwardUnprojections(ZonePhysicsShape shape, Vector3 position)
        => GetUpwardUnprojectionsCore(shape, position, false);

    internal IReadOnlyList<ZonePhysicsDirectedUnprojection> GetUpwardUnprojectionsForStancePreflight(ZonePhysicsShape shape, Vector3 position)
        => GetUpwardUnprojectionsCore(shape, position, true);

    private IReadOnlyList<ZonePhysicsDirectedUnprojection> GetUpwardUnprojectionsCore(ZonePhysicsShape shape, Vector3 position, bool stancePreflight)
    {
        if (stancePreflight) ValidateStancePreflightShape(shape); else ValidateShape(shape);
        ValidateFinite(position, nameof(position));
        var contacts = new List<ZonePhysicsDirectedUnprojection>();
        foreach (var (x, y) in SweepCells(position, Vector3.Zero, shape.BoundingHalfExtents))
        {
            if (!_terrain.TryReadManagedPhysicsQuad(x, y, out var heights, out var surface)) continue;
            CreateTriangles(x, y, heights, out var a, out var b, out var c, out var d);
            var material = ResolveShapeMaterial(surface);
            Take(a, b, c); Take(b, d, c);
            void Take(Vector3 first, Vector3 second, Vector3 third)
            {
                if (ZoneManagedDirectedUnprojectionQueries.Try(ZoneManagedConvexTarget.ForTriangle(first, second, third),
                    shape, position, material, true, out var result)) contacts.Add(result);
            }
        }
        return contacts.Count == 0 ? Array.Empty<ZonePhysicsDirectedUnprojection>() : Array.AsReadOnly(contacts.ToArray());
    }
}
