using System.Buffers.Binary;
using System.Numerics;
using AAEmu.Zone.Content;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Scene;

/// <summary>Actual source configuration inputs. This is not a caller-supplied collision-coverage certificate.</summary>
public sealed record ZoneManagedVegetationPolicy(int VegetationEnabled, int ObjectQuality,
    float MinimumVegetationSize, int CreateCollisionOnly, float? DetailViewDistanceRatio = null);
public enum ZoneManagedVegetationPlacementStatus { Published, SkippedByConfiguration, SkippedByExclusion, Unsupported }
public sealed record ZoneManagedVegetationPlacementReport(string Source, int SourceOrder, int SourceOffset,
    short GroupId, string? AssetPath, ZoneManagedVegetationPlacementStatus Status, string Diagnostic,
    Matrix4x4? Transform, uint GeometryFlags, uint ColliderFlags, int CharacterInstances, int RayInstances);

/// <summary>302221D0 and 30102840/30106900's explicit-angle, unaligned transform profile.</summary>
public static class ZoneManagedVegetationPlacementMath
{
    public static Matrix4x4 Transform(ReadOnlySpan<byte> payload, Vector3 translation, bool alignToTerrain)
    {
        if (payload.Length != 64) throw new ArgumentException("Vegetation placement requires its exact 64-byte authored payload.", nameof(payload));
        if (!Finite(translation)) throw new ArgumentException("Vegetation cell/sector translation must be finite.", nameof(translation));
        if (alignToTerrain && payload[0x39] == 0 && payload[0x3a] == 0)
            throw new NotSupportedException("Vegetation terrain alignment requires the original 300CD590 terrain-normal owner.");
        var rawScale = Float(payload, 0x33);
        if (!float.IsFinite(rawScale) || rawScale < 0)
            throw new NotSupportedException("Negative/nonfinite vegetation scale requires the original model-radius/minimum-size gate.");
        var scale = rawScale;
        if (scale >= .1f) { if (scale > 5) scale = 5; } else scale = .1f;
        var x = Radians(payload[0x39]); var y = Radians(payload[0x3a]); var z = Radians(payload[0x3c]);
        var sx = MathF.Sin(x); var cx = MathF.Cos(x); var sy = MathF.Sin(y); var cy = MathF.Cos(y);
        var sz = MathF.Sin(z); var cz = MathF.Cos(z);
        //Transpose the original Matrix33 into System.Numerics row-vector convention.
        var m00 = cz * cy; var m01 = (cz * sy) * sx - sz * cx; var m02 = sz * sx + (cz * sy) * cx;
        var m10 = sz * cy; var m11 = (sz * sy) * sx + cz * cx; var m12 = (sz * sy) * cx - cz * sx;
        var m20 = -sy; var m21 = cy * sx; var m22 = cy * cx;
        if (x == 0 && y == 0 && z == 0)
        { m00 = m11 = m22 = 1; m01 = m02 = m10 = m12 = m20 = m21 = 0; }
        if (scale != 1)
        {
            var a00 = m00; var a01 = m01; var a02 = m02; var a10 = m10; var a11 = m11; var a12 = m12;
            var a20 = m20; var a21 = m21; var a22 = m22;
            //30106AC0 includes these zero products/additions; preserve its float and signed-zero order.
            m00 = (a00 * scale + a10 * 0) + 0 * a20;
            m01 = (a11 * 0 + a01 * scale) + a21 * 0;
            m02 = (a02 * scale + a12 * 0) + a22 * 0;
            m10 = (0 * a00 + scale * a10) + 0 * a20;
            m11 = (a01 * 0 + a11 * scale) + a21 * 0;
            m12 = (a02 * 0 + a12 * scale) + a22 * 0;
            m20 = (0 * a10 + 0 * a00) + scale * a20;
            m21 = (a01 * 0 + a11 * 0) + a21 * scale;
            m22 = (a02 * 0 + a12 * 0) + a22 * scale;
        }
        var position = new Vector3(translation.X + Float(payload, 0x27), translation.Y + Float(payload, 0x2b),
            translation.Z + Float(payload, 0x2f));
        if (!Finite(position)) throw new InvalidDataException("Vegetation authored position is nonfinite.");
        return new(m00, m10, m20, 0, m01, m11, m21, 0,
            m02, m12, m22, 0, position.X, position.Y, position.Z, 1);
    }
    private static float Radians(byte value) => value == 0 ? 0 : (value * 1.4117647f) * .017453292f;
    private static float Float(ReadOnlySpan<byte> bytes, int offset) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]));
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

/// <summary>
/// Shared immutable direct 4096 vegetation resources and generation-owned publication into the scene's existing
/// character/ray geometries. This first profile refuses compound/helper/special/foliage publication; it certifies
/// no other cell family. Eager supported-proxy retention replaces source on-demand allocation scheduling only.
/// </summary>
public sealed class ZoneManagedVegetationResources : IDisposable
{
    private readonly IZoneAssetSource _assets;
    private readonly ZoneTerrainMaterialResolver _materials;
    private readonly ZoneWorldMapLayout _layout;
    private readonly ZoneWorldVegetationContent _catalog;
    private readonly ZoneManagedStaticGeometry _characters, _rays;
    private readonly ZoneManagedVegetationExclusions _exclusions;
    private readonly Dictionary<string, Resource> _resources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ZoneMapCoordinate, ZoneManagedVegetationCellLease> _cells = [];
    private ulong _nextGeneration;
    private bool _publishing;
    public ZoneManagedVegetationPolicy Policy { get; }
    public bool IsDisposed { get; private set; }
    public int SharedResourceCount => _resources.Count;
    public ZoneManagedVegetationResources(IZoneAssetSource assets, ZoneTerrainMaterialResolver materials,
        ZoneWorldMapLayout layout, ZoneWorldVegetationContent catalog, ZoneManagedStaticGeometry characters,
        ZoneManagedStaticGeometry rays, ZoneManagedVegetationPolicy policy, ZoneManagedVegetationExclusions exclusions)
    {
        _assets = assets ?? throw new ArgumentNullException(nameof(assets)); _materials = materials ?? throw new ArgumentNullException(nameof(materials));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout)); _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _characters = characters ?? throw new ArgumentNullException(nameof(characters)); _rays = rays ?? throw new ArgumentNullException(nameof(rays));
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _exclusions = exclusions ?? throw new ArgumentNullException(nameof(exclusions));
        if (!string.Equals(layout.World.Header.Name, catalog.WorldName, StringComparison.Ordinal))
            throw new ArgumentException("Vegetation groups and cell placements must belong to the same authored world.");
        if (ReferenceEquals(characters, rays)) throw new ArgumentException("Vegetation requires the scene's distinct character/ray query inventories.");
        if (!float.IsFinite(policy.MinimumVegetationSize) || policy.DetailViewDistanceRatio is { } detail && !float.IsFinite(detail))
            throw new ArgumentException("Vegetation configuration scalars must be finite.");
    }
    public ZoneManagedVegetationCellLease BeginCell(ZoneWorldMapCellPlacement placement)
    {
        Check(); ArgumentNullException.ThrowIfNull(placement);
        if (!_layout.Cells.Any(cell => ReferenceEquals(cell, placement)) || placement.InvalidLocalX || placement.InvalidLocalY ||
            (uint)placement.EngineCell.X >= 8 || (uint)placement.EngineCell.Y >= 8)
            throw new ArgumentException("Vegetation requires this exact layout's resolved engine-local cell.", nameof(placement));
        if (_cells.TryGetValue(placement.EngineCell, out var old)) old.Dispose(); //Invalidation precedes replacement publication.
        var generation = checked(++_nextGeneration);
        var cell = new ZoneManagedVegetationCellLease(this, placement, generation); _cells.Add(placement.EngineCell, cell); return cell;
    }
    internal bool Current(ZoneManagedVegetationCellLease cell) => !IsDisposed && !cell.IsDisposed &&
        _cells.TryGetValue(cell.Placement.EngineCell, out var existing) && ReferenceEquals(existing, cell);
    internal void Retire(ZoneManagedVegetationCellLease cell)
    { if (_cells.TryGetValue(cell.Placement.EngineCell, out var existing) && ReferenceEquals(existing, cell)) _cells.Remove(cell.Placement.EngineCell); }

    internal ZoneManagedVegetationPlacementLease Publish(ZoneManagedVegetationCellLease cell, string source,
        int order, int offset, ReadOnlySpan<byte> payload, Vector3 translation)
    {
        Require(cell);
        if (payload.Length != 64) throw new InvalidDataException("Vegetation source record has an invalid length.");
        if (_publishing) throw new InvalidOperationException("Vegetation publication cannot reenter an asset callback on the same scene owner.");
        var groupId = BinaryPrimitives.ReadInt16LittleEndian(payload[0x37..]);
        var characterHandles = new List<ZoneManagedStaticInstanceHandle>(); var rayHandles = new List<ZoneManagedStaticInstanceHandle>();
        Matrix4x4? matrix = null; string? asset = null; uint flags = 0;
        _publishing = true;
        try
        {
            if (groupId < 0 || !_catalog.Groups.TryGetValue(groupId, out var group))
                throw new NotSupportedException($"Vegetation serialized group {groupId} has no actual world catalog definition.");
            if (!QualityAllowed(group) || Policy.VegetationEnabled == 0)
                return Finish(ZoneManagedVegetationPlacementStatus.SkippedByConfiguration, "Original vegetation/quality configuration excludes this placement.");
            asset = AssetPath(group.ModelFileName);
            if (Policy.MinimumVegetationSize != 0)
                throw new NotSupportedException("A nonzero vegetation minimum size requires the actual loaded CStatObj radius gate.");
            if (!string.IsNullOrEmpty(group.MaterialName))
                throw new NotSupportedException("Vegetation group material overrides require their independently verified physical material publication.");
            matrix = ZoneManagedVegetationPlacementMath.Transform(payload, translation, group.AlignToTerrain);
            if (!_resources.TryGetValue(asset, out var resource))
            { resource = LoadResource(asset); Require(cell); _resources.Add(asset, resource); }
            Require(cell); //Asset callbacks may have retired/replaced this exact cell.
            if (resource.Refusal is { } refusal) throw new NotSupportedException(refusal);
            var authoredBounds = new ZonePhysicsQueryBounds(translation + Vector(payload, 0), translation + Vector(payload, 12));
            if (_exclusions.Excludes(authoredBounds))
                return Finish(ZoneManagedVegetationPlacementStatus.SkippedByExclusion, "Original 301597D0 excludes this translated authored bounding box.");
            //create_collision_only's original 4096 test is true for this admitted direct-default resource.
            flags = 0x04018fffu;
            if ((BinaryPrimitives.ReadUInt32LittleEndian(payload[0x20..]) & 2) == 0 && resource.HasBreakEnergy)
                flags |= 0x800000;
            var bounds = resource.Bounds.Transform(matrix.Value);
            var admission = new ZonePhysicsQueryBounds(bounds.Minimum, bounds.Maximum);
            foreach (var part in resource.Parts)
            {
                characterHandles.Add(part.Add(_characters, matrix.Value, admission));
                rayHandles.Add(part.Add(_rays, matrix.Value, admission));
            }
            Require(cell);
            return Finish(ZoneManagedVegetationPlacementStatus.Published, "Direct 4096 vegetation proxy published for player and ray masks; no brush picking policy applied.");
        }
        catch (Exception error)
        {
            foreach (var handle in characterHandles) _characters.RemoveInstance(handle);
            foreach (var handle in rayHandles) _rays.RemoveInstance(handle);
            characterHandles.Clear(); rayHandles.Clear();
            if (!ContentFailure(error)) throw;
            Require(cell);
            return Finish(ZoneManagedVegetationPlacementStatus.Unsupported, error.Message);
        }
        finally { _publishing = false; }
        ZoneManagedVegetationPlacementLease Finish(ZoneManagedVegetationPlacementStatus status, string diagnostic) => new(this, cell,
            new(source, order, offset, groupId, asset, status, diagnostic, matrix, flags,
                status == ZoneManagedVegetationPlacementStatus.Published ? 1u : 0u, characterHandles.Count, rayHandles.Count),
            characterHandles.ToArray(), rayHandles.ToArray());
    }
    private bool QualityAllowed(ZoneWorldVegetationGroup group)
    {
        var spec = (uint)group.MinimumConfigSpec & 7;
        if (spec == 7)
        {
            if (Policy.DetailViewDistanceRatio is not { } detail)
                throw new NotSupportedException("Vegetation detail-spec 7 requires actual e_view_dist_ratio_detail.");
            if (detail == 0) return false;
        }
        else if (spec == 0) return true;
        return Policy.ObjectQuality == 0 || spec <= unchecked((uint)Policy.ObjectQuality);
    }
    private Resource LoadResource(string asset)
    {
        try
        {
            using var input = _assets.OpenRead(asset) ?? throw new FileNotFoundException("The vegetation model is absent; native fallback geometry is not owned.", asset);
            var cgf = ZoneCgfContent.Read(input);
            if (!cgf.HasOrdinaryMesh || !cgf.IsCollisionComplete)
                throw new NotSupportedException("Vegetation resource has incomplete or unsupported authored physical data: " + string.Join("; ", cgf.Diagnostics));
            if (cgf.IsCompound) throw new NotSupportedException("Compound vegetation requires the original child default/independent physical publication owner.");
            if (cgf.Chunks.Any(chunk => chunk.Type == 0xaafc0005))
                throw new NotSupportedException("Vegetation foliage-info chunk 0xAAFC0005 requires its actual physical foliage/bending publication owner.");
            if (cgf.Nodes.Any(node => node.IsHelperMesh && node.IsCollisionSource && node.PhysicsParts.Count != 0))
                throw new NotSupportedException("An independent vegetation helper physics child requires 300442F0 publication and masks.");
            var root = cgf.DirectPhysicsSource ?? throw new NotSupportedException("Vegetation has no selected direct physical root.");
            if (root.Diagnostics.Count != 0 || root.PhysicsParts.Any(part => part.PhysicsSlot != 0))
                throw new NotSupportedException("Vegetation root special slots/diagnostics require their separate physical publication profile.");
            var source = root.PhysicsParts.SingleOrDefault(part => part.PhysicsSlot == 0)?.Geometry ??
                throw new NotSupportedException("Vegetation has no decoded native default4096 geometry.");
            var definition = cgf.Materials.FirstOrDefault(material => material.ChunkId == root.MaterialChunkId);
            if (definition is null || string.IsNullOrEmpty(definition.Name))
                throw new NotSupportedException("Vegetation physical root has no original material identity.");
            var material = _materials.Load(definition.ResolveAssetName(asset));
            if (material is null || ReferenceEquals(material, _materials.ErrorMaterial))
                throw new NotSupportedException("Vegetation physical material graph is unresolved.");
            var surfaces = new HashSet<ushort>();
            ushort Surface(int slot)
            {
                if (slot < 0) slot = source.DefaultMaterialSlot;
                var selected = material;
                if (selected.SubMaterialCount != 0 && selected is ZoneTerrainMaterial { IsMultiMaterial: true })
                {
                    if ((uint)slot >= (uint)selected.SubMaterialCount) slot = 0;
                    selected = selected.GetSubMaterial(slot) ?? throw new NotSupportedException("Vegetation physical submaterial graph has an unresolved slot.");
                    if (ReferenceEquals(selected, _materials.ErrorMaterial)) throw new NotSupportedException("Vegetation physical submaterial dependency is unresolved.");
                }
                if ((uint)selected.PhysicalSurfaceId > ushort.MaxValue) throw new InvalidDataException("Vegetation physical material ID exceeds the copied query contract.");
                var id = (ushort)selected.PhysicalSurfaceId; surfaces.Add(id); return id;
            }
            Part part;
            if (source.Primitive is { } primitive)
            {
                var shape = new ZoneManagedPrimitiveGeometry(primitive.Kind switch
                {
                    ZoneCgfPhysicsGeometryKind.Sphere => ZoneManagedPrimitiveKind.Sphere,
                    ZoneCgfPhysicsGeometryKind.Cylinder => ZoneManagedPrimitiveKind.Cylinder,
                    ZoneCgfPhysicsGeometryKind.Capsule => ZoneManagedPrimitiveKind.Capsule,
                    _ => throw new NotSupportedException("Unknown vegetation analytic proxy kind.")
                }, primitive.Center, primitive.Axis, primitive.Radius, primitive.HalfHeight, Surface(source.DefaultMaterialSlot));
                part = new(null, shape);
            }
            else
            {
                var indices = source.TriangleIndices.ToArray();
                if (indices.Length == 0) throw new NotSupportedException("Vegetation default geometry has no authored triangles.");
                var triangleMaterials = Enumerable.Range(0, indices.Length / 3).Select(index =>
                    Surface(source.MaterialSlots.Count == 0 ? source.DefaultMaterialSlot : source.MaterialSlots[index])).ToArray();
                part = new(new(source.Vertices.ToArray(), indices, triangleMaterials,
                    solidConvex: source.Kind == ZoneCgfPhysicsGeometryKind.Box), null);
            }
            if (_materials.SurfaceContent is null) throw new NotSupportedException("Vegetation compiled physical flags require the actual authored surface-physics table.");
            var hasBreakEnergy = (source.Kind == ZoneCgfPhysicsGeometryKind.TriangleMesh || source.DefaultMaterialSlot >= 0) &&
                surfaces.Any(id => _materials.SurfaceContent.Surfaces.Any(surface => surface.Id == id && surface.Physics.BreakEnergy != 0));
            return new([part], part.Bounds, hasBreakEnergy, null);
        }
        catch (Exception error) when (ContentFailure(error)) { return new([], default, false, error.Message); }
    }
    internal void Remove(ZoneManagedVegetationPlacementLease lease)
    {
        foreach (var handle in lease.CharacterHandles) _characters.RemoveInstance(handle);
        foreach (var handle in lease.RayHandles) _rays.RemoveInstance(handle);
    }
    internal void Require(ZoneManagedVegetationCellLease cell)
    { Check(); if (!Current(cell)) throw new ObjectDisposedException(nameof(ZoneManagedVegetationCellLease), "The exact vegetation cell generation retired."); }
    private static string AssetPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new NotSupportedException("Vegetation has no authored model resource.");
        var path = name.Replace('\\', '/'); if (!path.StartsWith("game/", StringComparison.OrdinalIgnoreCase)) path = "game/" + path;
        return ZoneAssetPath.Validate(path.ToLowerInvariant());
    }
    private static Vector3 Vector(ReadOnlySpan<byte> bytes, int offset) => new(
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..])),
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes[(offset + 4)..])),
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes[(offset + 8)..])));
    private static bool ContentFailure(Exception error) => error is IOException or InvalidDataException or ArgumentException or NotSupportedException;
    private void Check() => ObjectDisposedException.ThrowIf(IsDisposed, this);
    public void Dispose()
    { if (IsDisposed) return; foreach (var cell in _cells.Values.ToArray()) cell.Dispose(); IsDisposed = true; _resources.Clear(); }
    private sealed record Resource(Part[] Parts, ZoneManagedBounds Bounds, bool HasBreakEnergy, string? Refusal);
    private sealed record Part(ZoneManagedTriangleMesh? Mesh, ZoneManagedPrimitiveGeometry? Primitive)
    {
        internal ZoneManagedBounds Bounds => Primitive?.Bounds ?? new(Mesh!.BoundsMinimum, Mesh.BoundsMaximum);
        internal ZoneManagedStaticInstanceHandle Add(ZoneManagedStaticGeometry target, Matrix4x4 transform, ZonePhysicsQueryBounds bounds) =>
            Primitive is { } shape ? target.AddInstance(shape, transform, bounds) : target.AddInstance(Mesh!, transform, bounds);
    }
}

public sealed class ZoneManagedVegetationCellLease : IDisposable
{
    private readonly ZoneManagedVegetationResources _owner;
    private readonly List<ZoneManagedVegetationPlacementLease> _placements = [];
    private readonly HashSet<ZoneCellObjectFileKind> _taggedInputs = [];
    private bool _sectorsAttempted;
    public ZoneWorldMapCellPlacement Placement { get; }
    public ulong Generation { get; }
    public bool IsDisposed { get; private set; }
    public bool IsCurrent => _owner.Current(this);
    public IReadOnlyList<ZoneManagedVegetationPlacementReport> Reports => _placements.Select(lease => lease.Report).ToArray();
    internal ZoneManagedVegetationCellLease(ZoneManagedVegetationResources owner, ZoneWorldMapCellPlacement placement, ulong generation)
    { _owner = owner; Placement = placement; Generation = generation; }
    public void LoadTaggedObjects(ZoneCellObjectContent content)
    {
        _owner.Require(this); ArgumentNullException.ThrowIfNull(content);
        if (!_taggedInputs.Add(content.Kind)) throw new InvalidOperationException("This cell's tagged vegetation input was already attempted.");
        var translation = new Vector3(Placement.EngineCell.X << 10, Placement.EngineCell.Y << 10, 0);
        foreach (var row in content.Objects.OfType<ZoneCellVegetationObject>())
            _placements.Add(_owner.Publish(this, content.Kind.ToString(), row.SourceOrder, row.SourceOffset, row.RawPayload, translation));
    }
    public void LoadSectors(ZoneCellVegetationContent content)
    {
        _owner.Require(this); ArgumentNullException.ThrowIfNull(content);
        if (_sectorsAttempted) throw new InvalidOperationException("This cell's sector vegetation input was already attempted.");
        _sectorsAttempted = true;
        foreach (var sector in content.Sectors)
        {
            var translation = new Vector3((Placement.EngineCell.X * 4 + sector.LocalX) << 8,
                (Placement.EngineCell.Y * 4 + sector.LocalY) << 8, 0);
            foreach (var row in sector.Instances)
                _placements.Add(_owner.Publish(this, $"vegetation.dat/sector{sector.SourceIndex}", row.SourceOrder, row.SourceOffset, row.RawPayload, translation));
        }
    }
    public void Dispose()
    {
        if (IsDisposed) return; IsDisposed = true;
        _owner.Retire(this); //Coverage/generation invalidation precedes any instance removal.
        foreach (var placement in _placements) placement.Dispose();
    }
}
public sealed class ZoneManagedVegetationPlacementLease : IDisposable
{
    private readonly ZoneManagedVegetationResources _owner;
    private readonly ZoneManagedVegetationCellLease _cell;
    internal ZoneManagedStaticInstanceHandle[] CharacterHandles { get; }
    internal ZoneManagedStaticInstanceHandle[] RayHandles { get; }
    public ZoneManagedVegetationPlacementReport Report { get; }
    public bool IsDisposed { get; private set; }
    public bool IsCurrent => !IsDisposed && _cell.IsCurrent;
    internal ZoneManagedVegetationPlacementLease(ZoneManagedVegetationResources owner, ZoneManagedVegetationCellLease cell,
        ZoneManagedVegetationPlacementReport report, ZoneManagedStaticInstanceHandle[] characters, ZoneManagedStaticInstanceHandle[] rays)
    { _owner = owner; _cell = cell; Report = report; CharacterHandles = characters; RayHandles = rays; }
    public void Dispose() { if (IsDisposed) return; IsDisposed = true; _owner.Remove(this); }
}
