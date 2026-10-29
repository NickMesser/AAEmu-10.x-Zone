using System.Numerics;
using AAEmu.Zone.Content;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Scene;

public sealed record ZoneManagedCollisionResourceReport(string AssetPath, string? MaterialOverride,
    int RequestedBrushes, int LoadedBrushes, int RayOnlyBrushes, int FailedPlacements, int DefaultPhysicsParts, int MeshInstances,
    int PrimitiveInstances, int SkippedNonDefaultParts, int RayInstances, int SkippedNonUniformBrushes,
    string? NonBlockingReason, IReadOnlyList<string> Diagnostics)
{
    public int CollisionInstances => MeshInstances + PrimitiveInstances;
}

public sealed record ZoneManagedSceneCollisionReport(int BrushCount, int LoadedBrushCount,
    int RayOnlyBrushCount, int RenderOnlyBrushCount, int UnsupportedBrushCount, int OtherPlacementCount,
    IReadOnlyList<ZoneManagedCollisionResourceReport> Resources)
{
    /// <summary>Only the default physical proxies of the brush catalog; other entity systems are separate.</summary>
    public bool BrushCoverageComplete => UnsupportedBrushCount == 0;
    /// <summary>Other collision owners and geometry profiles remain unimplemented even in an empty fixture.</summary>
    public bool FullSceneCollisionReady => false;
}

/// <summary>
/// Instantiates decoded default CGF collision proxies at authored brush transforms. Render bounds
/// are never used as collision geometry. Meshes/BVHs and analytic primitives are shared by resource and material identity;
/// missing or unsupported physical data remains in the coverage report.
/// </summary>
public sealed class ZoneManagedSceneCollision : IDisposable
{
    public ZoneManagedStaticGeometry Geometry { get; } = new();
    /// <summary>Native picking rays use slot1 when present, otherwise slot0; living bodies use slot0.</summary>
    public ZoneManagedStaticGeometry RayGeometry { get; } = new();
    public ZoneManagedSceneCollisionReport Report { get; }
    public bool IsDisposed => _disposed;
    private bool _disposed;

    public ZoneManagedSceneCollision(IZoneAssetSource assets, ZoneTerrainMaterialResolver materials,
        IReadOnlyList<ZoneManagedSceneCellLoadReport> cells, Action<string>? trace = null)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(materials);
        ArgumentNullException.ThrowIfNull(cells);
        var content = new Dictionary<string, ZoneCgfContent>(StringComparer.OrdinalIgnoreCase);
        var resources = new Dictionary<(string Asset, string Material), Resource>();
        var vehicleOnlySurfaces = materials.SurfaceContent?.Surfaces
            .Where(surface => surface.Flags.HasFlag(ZonePhysicalSurfaceFlags.VehicleOnlyCollisions))
            .Select(surface => surface.Id).ToHashSet() ?? [];
        var brushes = 0; var loaded = 0; var rayOnly = 0; var renderOnly = 0; var unsupported = 0; var other = 0;
        foreach (var cell in cells)
        {
            if (cell.Placement.InvalidLocalX || cell.Placement.InvalidLocalY) continue;
            LoadObjects(cell.Objects.Objects, cell.Placement);
            LoadObjects(cell.Objects.BigObjects, cell.Placement);
            other += cell.Objects.Vegetation?.Instances.Count ?? 0;
            other += cell.Objects.Entities?.Entities.Count ?? 0;
        }
        Report = new(brushes, loaded, rayOnly, renderOnly, unsupported, other,
            resources.Values.Select(resource => resource.Snapshot()).ToArray());
        trace?.Invoke($"Managed static collision: {loaded}/{brushes} brushes block characters, {rayOnly} are ray-only, {renderOnly} authored render-only, " +
            $"{unsupported} unsupported, {Geometry.InstanceCount} proxy instances, {resources.Count} resource/material variants; " +
            $"{other} other placements require their own collision owners.");
        foreach (var resource in Report.Resources.Where(row => row.Diagnostics.Count != 0).Take(12))
            trace?.Invoke($"Static collision coverage {resource.AssetPath}: {string.Join("; ", resource.Diagnostics.Take(3))}");

        void LoadObjects(ZoneCellObjectContent? objects, ZoneWorldMapCellPlacement placement)
        {
            if (objects is null) return;
            foreach (var row in objects.Objects)
            {
                if (row is not ZoneCellBrushObject brush)
                {
                    if (row.Type != ZoneCellObjectType.Decal) other++;
                    continue;
                }
                brushes++;
                var model = objects.GetModelResource(brush.ModelIndex)?.Name;
                if (string.IsNullOrWhiteSpace(model) || model.Equals("NOFILE", StringComparison.OrdinalIgnoreCase))
                { renderOnly++; continue; }
                var material = objects.GetMaterialResource(brush.MaterialIndex)?.Name;
                Resource resource;
                try
                {
                    var path = AssetPath(model);
                    var key = (path, material ?? "");
                    if (!resources.TryGetValue(key, out resource!))
                    {
                        resource = LoadResource(path, material);
                        resources.Add(key, resource);
                    }
                }
                catch (Exception error) when (AssetFailure(error))
                {
                    var key = (model, material ?? "");
                    if (!resources.TryGetValue(key, out resource!))
                    {
                        resource = new(model, material);
                        resource.Diagnostics.Add(error.Message);
                        resource.HasContentFailure = true;
                        resources.Add(key, resource);
                    }
                }
                resource.Requested++;
                var matrix = Matrix(brush.AuthoredTransform.Translate(new(
                    placement.EngineCell.X * 1024f, placement.EngineCell.Y * 1024f, 0)));
                var handles = new List<ZoneManagedStaticInstanceHandle>();
                var rayHandles = new List<ZoneManagedStaticInstanceHandle>();
                var primitiveInstances = 0;
                var placementFailed = false;
                try
                {
                    if (resource.RequiresUniformScale && !HasNativeUniformScale(matrix))
                    {
                        // CBrush30111970 compares axis lengths only for a direct root phys_geom;
                        // compound statobjs take the multipart physicalization path instead.
                        renderOnly++;
                        resource.SkippedNonUniformBrushes++;
                        continue;
                    }
                    ZoneManagedBounds? entityBounds = null;
                    foreach (var part in resource.Parts)
                    {
                        // Original ComputeBBox35054080 unions every published physical part,
                        // regardless of its player/ray/vehicle collision flags. Render bounds
                        // are not part of that calculation. The stance query admits the entity
                        // first, then tests all of its eligible parts against the moving shape.
                        var bounds = part.Bounds(matrix);
                        entityBounds = entityBounds is { } previous ? ZoneManagedBounds.Union(previous, bounds) : bounds;
                    }
                    var admissionBounds = entityBounds is { } union
                        ? new ZonePhysicsQueryBounds(union.Minimum, union.Maximum) : (ZonePhysicsQueryBounds?)null;
                    foreach (var part in resource.Parts)
                    {
                        if (part.BlocksCharacters)
                        {
                            handles.Add(part.AddInstance(Geometry, matrix, admissionBounds));
                            if (part.Primitive is not null) primitiveInstances++;
                        }
                        if (part.CollidesRay) rayHandles.Add(part.AddInstance(RayGeometry, matrix, admissionBounds));
                    }
                }
                catch (Exception error) when (AssetFailure(error))
                {
                    foreach (var handle in handles) Geometry.RemoveInstance(handle);
                    foreach (var handle in rayHandles) RayGeometry.RemoveInstance(handle);
                    handles.Clear();
                    rayHandles.Clear();
                    placementFailed = true;
                    resource.FailedPlacements++;
                    if (!resource.Diagnostics.Contains(error.Message)) resource.Diagnostics.Add(error.Message);
                }
                if (handles.Count != 0)
                {
                    loaded++; resource.Loaded++;
                    resource.MeshInstances += handles.Count - primitiveInstances;
                    resource.PrimitiveInstances += primitiveInstances;
                }
                else if (rayHandles.Count != 0) { rayOnly++; resource.RayOnly++; }
                else if (!resource.HasContentFailure && !placementFailed) renderOnly++;
                resource.RayInstances += rayHandles.Count;
                if (resource.HasContentFailure || placementFailed) unsupported++;
            }
        }

        Resource LoadResource(string path, string? materialOverride)
        {
            var resource = new Resource(path, materialOverride);
            try
            {
                if (!string.IsNullOrEmpty(materialOverride) &&
                    HasVehicleOnlySurface(materials.Load(materialOverride) ?? materials.ErrorMaterial))
                {
                    // CBrush 3010F9E0 uses any override mapping entry with surface flag 8,
                    // including an unused submaterial, to remove ordinary living/ray flags.
                    resource.NonBlockingReason = "The authored brush material override restricts the whole brush to vehicle collisions.";
                    return resource;
                }
                if (!content.TryGetValue(path, out var cgf))
                {
                    using var input = assets.OpenRead(path) ?? throw new FileNotFoundException("The authored geometry resource is absent.", path);
                    cgf = ZoneCgfContent.Read(input);
                    content.Add(path, cgf);
                }
                if (!cgf.HasOrdinaryMesh)
                    throw new NotSupportedException("The original CGF loader refuses a resource without an ordinary mesh node.");
                resource.RequiresUniformScale = cgf.HasDirectPhysicsGeometry;
                resource.Diagnostics.AddRange(cgf.Diagnostics);
                foreach (var node in cgf.Nodes)
                {
                    // Direct mode registers one raw root before helper/visual admission. Its
                    // ordinary nodes never become child statobjs. Helpers are independently
                    // published, so a selected helper can supply both root and child geometry.
                    var publishRoot = !cgf.IsCompound && cgf.DirectPhysicsNodeId == node.ChunkId;
                    var publishChild = node.IsCollisionSource &&
                        (node.IsHelperMesh || cgf.IsCompound && node.IsOrdinaryMesh);
                    if (!publishRoot && !publishChild) continue;
                    resource.Diagnostics.AddRange(node.Diagnostics);
                    var material = ResolveMaterial(path, materialOverride, cgf, node.MaterialChunkId);
                    // Native direct statobjs register the selected blob on the root with an
                    // identity local transform. Its node TM belongs to the render hierarchy;
                    // compound subobjects retain their composed authored transforms instead.
                    Matrix4x4[] physicsTransforms = publishRoot
                        ? publishChild ? [Matrix4x4.Identity, node.WorldTransform] : [Matrix4x4.Identity]
                        : [node.WorldTransform];
                    var firstPart = resource.Parts.Count;
                    var nodeVehicleOnly = false;
                    var hasPickingProxy = node.PhysicsParts.Any(part => part.PhysicsSlot == 1);
                    foreach (var part in node.PhysicsParts)
                    {
                        // Native CStatObj maps these slots to 4096=default, 4097=nocollide,
                        // 4098=obstruct and 4099. Slot1 is a ray proxy, despite its legacy name;
                        // when it exists the default part loses geom_colltype_ray (0x8000).
                        if (part.PhysicsSlot > 1)
                        {
                            resource.Skipped++;
                            // CBrush30111970 publishes root4098 as a vehicle/obstruct part on
                            // the same entity. It affects entity admission bounds even though
                            // it is not a player or picking target. Child300442F0 publishes only
                            // slots0/1; neither path publishes slot3 as an ordinary brush part.
                            if (part.PhysicsSlot != 2 || !publishRoot) continue;
                        }
                        if (part.PhysicsSlot == 0) resource.DefaultParts++;
                        if (part.Geometry is not { } geometry)
                        {
                            resource.Diagnostics.Add(part.Diagnostic ?? $"Unsupported default physics chunk {part.PhysicsChunkId}.");
                            continue;
                        }
                        var partTransforms = part.PhysicsSlot == 2 ? new[] { Matrix4x4.Identity } : physicsTransforms;
                        if (geometry.Primitive is { } primitive)
                        {
                            var surface = Surface(geometry.DefaultMaterialSlot);
                            var kind = primitive.Kind switch
                            {
                                ZoneCgfPhysicsGeometryKind.Sphere => ZoneManagedPrimitiveKind.Sphere,
                                ZoneCgfPhysicsGeometryKind.Cylinder => ZoneManagedPrimitiveKind.Cylinder,
                                ZoneCgfPhysicsGeometryKind.Capsule => ZoneManagedPrimitiveKind.Capsule,
                                _ => throw new NotSupportedException($"Unknown analytic physical primitive {primitive.Kind}.")
                            };
                            var shape = new ZoneManagedPrimitiveGeometry(kind, primitive.Center, primitive.Axis,
                                primitive.Radius, primitive.HalfHeight, surface);
                            foreach (var physicsTransform in partTransforms)
                                resource.Parts.Add(new(null, shape, physicsTransform, part.PhysicsSlot == 0,
                                    part.PhysicsSlot == 1 || part.PhysicsSlot == 0 && !hasPickingProxy, part.PhysicsSlot));
                        }
                        else
                        {
                            var indices = geometry.TriangleIndices.ToArray();
                            if (indices.Length == 0) continue;
                            var triangleMaterials = new ushort[indices.Length / 3];
                            for (var triangle = 0; triangle < triangleMaterials.Length; triangle++)
                                triangleMaterials[triangle] = Surface(geometry.MaterialSlots.Count == 0
                                    ? geometry.DefaultMaterialSlot : geometry.MaterialSlots[triangle]);
                            var mesh = new ZoneManagedTriangleMesh(geometry.Vertices.ToArray(), indices, triangleMaterials,
                                solidConvex: geometry.Kind == ZoneCgfPhysicsGeometryKind.Box);
                            foreach (var physicsTransform in partTransforms)
                                resource.Parts.Add(new(mesh, null, physicsTransform, part.PhysicsSlot == 0,
                                    part.PhysicsSlot == 1 || part.PhysicsSlot == 0 && !hasPickingProxy, part.PhysicsSlot));
                        }

                        ushort Surface(int slot)
                        {
                            // Signed triangle ids use surface_idx; out-of-range mapping ids use
                            // submaterial zero. Analytic proxies use the same wrapper surface_idx.
                            if (slot < 0) slot = geometry.DefaultMaterialSlot;
                            var selected = material;
                            if (IsMultiMaterial(selected))
                            {
                                if ((uint)slot >= (uint)selected.SubMaterialCount) slot = 0;
                                if (selected.GetSubMaterial(slot) is not { } child) return 0;
                                selected = child;
                            }
                            if ((uint)selected.PhysicalSurfaceId > ushort.MaxValue)
                                throw new InvalidDataException("A collision material id does not fit the World physics contract.");
                            if (part.PhysicsSlot == 0 && VehicleOnly(selected.PhysicalSurfaceId)) nodeVehicleOnly = true;
                            return (ushort)selected.PhysicalSurfaceId;
                        }
                    }
                    if (nodeVehicleOnly)
                    {
                        resource.NonBlockingReason = "One or more authored child proxies are restricted to vehicle collisions.";
                        // 300442F0 applies the compiled-material restriction to this child only.
                        // A separately authored picking proxy retains its own ray flags.
                        for (var index = firstPart; index < resource.Parts.Count; index++)
                            if (resource.Parts[index].PhysicsSlot == 0)
                                resource.Parts[index] = resource.Parts[index] with { BlocksCharacters = false, CollidesRay = false };
                    }
                }
            }
            catch (Exception error) when (AssetFailure(error))
            {
                resource.Diagnostics.Add(error.Message);
            }
            resource.HasContentFailure = resource.Diagnostics.Count != 0;
            return resource;
        }

        bool VehicleOnly(int id) => vehicleOnlySurfaces.Contains(id);

        bool HasVehicleOnlySurface(IZoneTerrainMaterial material)
        {
            if (!IsMultiMaterial(material)) return VehicleOnly(material.PhysicalSurfaceId);
            for (var index = 0; index < material.SubMaterialCount; index++)
                if (material.GetSubMaterial(index) is { } child && VehicleOnly(child.PhysicalSurfaceId)) return true;
            return false;
        }

        static bool IsMultiMaterial(IZoneTerrainMaterial material) => material.SubMaterialCount != 0 &&
            material is ZoneTerrainMaterial { IsMultiMaterial: true };

        IZoneTerrainMaterial ResolveMaterial(string path, string? materialOverride, ZoneCgfContent cgf, int materialId)
        {
            if (!string.IsNullOrEmpty(materialOverride)) return materials.Load(materialOverride) ?? materials.ErrorMaterial;
            var definition = cgf.Materials.FirstOrDefault(row => row.ChunkId == materialId);
            if (definition is null || string.IsNullOrEmpty(definition.Name)) return materials.ErrorMaterial;
            // Native CMatMan 301C4750: qualified material names are used directly; a bare
            // compiled name is relative to the CGF directory. LoadMaterial adds the extension.
            return materials.Load(definition.ResolveAssetName(path)) ?? materials.ErrorMaterial;
        }
    }

    public static Matrix4x4 Matrix(ZoneCellObjectTransform value) => new(
        value.M00, value.M10, value.M20, 0, value.M01, value.M11, value.M21, 0,
        value.M02, value.M12, value.M22, 0, value.M03, value.M13, value.M23, 1);

    private static bool HasNativeUniformScale(Matrix4x4 matrix)
    {
        var x = new Vector3(matrix.M11, matrix.M12, matrix.M13).Length();
        var y = new Vector3(matrix.M21, matrix.M22, matrix.M23).Length();
        var z = new Vector3(matrix.M31, matrix.M32, matrix.M33).Length();
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) ||
            x <= 0 || y <= 0 || z <= 0 || !Matrix4x4.Invert(matrix, out _) ||
            !float.IsFinite(matrix.M41) || !float.IsFinite(matrix.M42) || !float.IsFinite(matrix.M43))
            throw new ArgumentException("A direct collision placement requires a finite, invertible transform with nonzero scale.");
        // Native compares both other axes to X; it does not compare Y against Z.
        return MathF.Abs(x - y) <= .01f && MathF.Abs(x - z) <= .01f;
    }

    private static string AssetPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (!normalized.StartsWith("game/", StringComparison.OrdinalIgnoreCase)) normalized = "game/" + normalized;
        return ZoneAssetPath.Validate(normalized.ToLowerInvariant());
    }

    private static bool AssetFailure(Exception error) =>
        error is IOException or InvalidDataException or ArgumentException or NotSupportedException or UnauthorizedAccessException;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Geometry.Clear();
        RayGeometry.Clear();
    }

    private sealed record Part(ZoneManagedTriangleMesh? Mesh, ZoneManagedPrimitiveGeometry? Primitive,
        Matrix4x4 Transform, bool BlocksCharacters, bool CollidesRay, int PhysicsSlot)
    {
        public ZoneManagedBounds Bounds(Matrix4x4 placement) =>
            (Primitive?.Bounds ?? new ZoneManagedBounds(Mesh!.BoundsMinimum, Mesh.BoundsMaximum)).Transform(Transform * placement);

        public ZoneManagedStaticInstanceHandle AddInstance(ZoneManagedStaticGeometry target, Matrix4x4 placement,
            ZonePhysicsQueryBounds? admissionBounds)
            => Primitive is { } primitive ? target.AddInstance(primitive, Transform * placement, admissionBounds)
                : target.AddInstance(Mesh!, Transform * placement, admissionBounds);
    }
    private sealed class Resource(string path, string? material)
    {
        public string Path { get; } = path;
        public string? Material { get; } = material;
        public List<Part> Parts { get; } = [];
        public List<string> Diagnostics { get; } = [];
        public string? NonBlockingReason;
        public bool RequiresUniformScale, HasContentFailure;
        public int Requested, Loaded, RayOnly, FailedPlacements, DefaultParts, MeshInstances, PrimitiveInstances, Skipped, RayInstances, SkippedNonUniformBrushes;
        public ZoneManagedCollisionResourceReport Snapshot() => new(Path, Material, Requested, Loaded, RayOnly, FailedPlacements,
            DefaultParts, MeshInstances, PrimitiveInstances, Skipped, RayInstances, SkippedNonUniformBrushes,
            NonBlockingReason, Diagnostics.Distinct().ToArray());
    }
}
