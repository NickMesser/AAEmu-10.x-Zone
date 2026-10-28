using System.Numerics;

namespace AAEmu.Zone.Physics;

public sealed partial class ZoneManagedStaticGeometry
{
    /// <summary>
    /// Copies each initially intersecting target's fixed +Z exit and its separate normal fallback.
    /// The optional envelope admits an ENTITY union AABB, never clips an admitted part's triangles.
    /// Supply the same entity union as AddInstance admissionBounds for multipart brushes; the
    /// default standalone instance is one entity. Enumeration is not native contact ordering.
    /// </summary>
    public IReadOnlyList<ZonePhysicsDirectedUnprojection> GetUpwardUnprojections(ZonePhysicsShape shape,
        Vector3 position, ZonePhysicsQueryBounds? candidateBounds = null)
        => GetUpwardUnprojectionsCore(shape, position, candidateBounds, false);

    internal IReadOnlyList<ZonePhysicsDirectedUnprojection> GetUpwardUnprojectionsForStancePreflight(ZonePhysicsShape shape,
        Vector3 position, ZonePhysicsQueryBounds? candidateBounds = null)
        => GetUpwardUnprojectionsCore(shape, position, candidateBounds, true);

    private IReadOnlyList<ZonePhysicsDirectedUnprojection> GetUpwardUnprojectionsCore(ZonePhysicsShape shape,
        Vector3 position, ZonePhysicsQueryBounds? candidateBounds, bool stancePreflight)
    {
        if (stancePreflight) ZoneManagedTerrainGeometry.ValidateStancePreflightShape(shape); else ZoneManagedTerrainGeometry.ValidateShape(shape);
        ZoneManagedTerrainGeometry.ValidateFinite(position, nameof(position));
        var bounds = new ZoneManagedBounds(position - shape.BoundingHalfExtents, position + shape.BoundingHalfExtents);
        ZoneManagedTerrainGeometry.ValidateFinite(bounds.Minimum, nameof(shape)); ZoneManagedTerrainGeometry.ValidateFinite(bounds.Maximum, nameof(shape));
        lock (_gate)
        {
            CollectCandidates(bounds, out var boundsTests);
            var contacts = new List<ZonePhysicsDirectedUnprojection>(); var triangleTests = 0;
            foreach (var instance in _queryInstances)
            {
                if (candidateBounds is { } envelope && !instance.AdmissionBounds.Overlaps(envelope.Managed)) continue;
                if (instance.Primitive is { } primitive)
                {
                    if (ZoneManagedDirectedUnprojectionQueries.Try(ZoneManagedConvexTarget.ForPrimitive(primitive, instance.LocalToWorld),
                        shape, position, primitive.PhysicalMaterialId, false, out var contact)) contacts.Add(contact);
                    continue;
                }
                var mesh = instance.Mesh!;
                if (mesh.SolidConvex)
                {
                    if (ZoneManagedDirectedUnprojectionQueries.Try(ZoneManagedConvexTarget.ForSolidMesh(mesh, instance.LocalToWorld),
                        shape, position, 0, false, out var contact))
                    {
                        var material = SolidContactMaterial(instance, contact.Position, Vector3.UnitZ, ref triangleTests);
                        var fallbackMaterial = SolidContactMaterial(instance, contact.NormalFallback, ref triangleTests);
                        var fallback = contact.NormalFallback;
                        fallback = new(fallback.Depth, fallback.Normal, fallback.Position, fallbackMaterial, false, fallback.IsAmbiguous);
                        contacts.Add(new(contact.Depth, contact.Position, material, false, contact.SeparationTolerance, fallback));
                    }
                    continue;
                }
                mesh.QueryBounds(bounds.Transform(instance.WorldToLocal), _queryTriangles);
                foreach (var triangle in _queryTriangles)
                {
                    triangleTests++;
                    WorldTriangle(instance, triangle, out var a, out var b, out var c, out var material);
                    if (Vector3.Cross(b - a, c - a).LengthSquared() < 1e-18f) continue;
                    var target = ZoneManagedConvexTarget.ForTriangle(a, b, c) with
                        { Mesh = mesh, Transform = instance.LocalToWorld, SourceTriangle = triangle };
                    if (ZoneManagedDirectedUnprojectionQueries.Try(target, shape, position, material, true, out var contact)) contacts.Add(contact);
                }
            }
            _lastQuery = new(boundsTests, _queryInstances.Count, triangleTests);
            return contacts.Count == 0 ? Array.Empty<ZonePhysicsDirectedUnprojection>() : Array.AsReadOnly(contacts.ToArray());
        }
    }
}
