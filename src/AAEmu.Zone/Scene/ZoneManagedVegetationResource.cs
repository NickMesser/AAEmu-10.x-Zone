using System.Buffers.Binary;
using System.Numerics;
using AAEmu.Zone.Content;
using AAEmu.Zone.Hosting;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Scene;

/// <summary>Actual source configuration inputs. This is not a caller-supplied collision-coverage certificate.</summary>
public sealed record ZoneManagedVegetationPolicy(int VegetationEnabled, int ObjectQuality,
    float MinimumVegetationSize, float CreateCollisionOnly, float? DetailViewDistanceRatio = null)
{
    public int PhysicalFoliage { get; init; } = 2;
    public int VegetationBending { get; init; } = 2;
    public float FoliageStiffness { get; init; } = 3.2f;
    /// <summary>Original CVar registration inputs, then actual dedicated/command-line overrides.</summary>
    public static ZoneManagedVegetationPolicy FromCVars(ZoneEngineCVars cvars)
    {
        ArgumentNullException.ThrowIfNull(cvars);
        return new(cvars.GetInt("e_vegetation", 1), cvars.GetInt("e_obj_quality", 0),
            cvars.GetFloat("e_vegetation_min_size", 0), cvars.GetFloat("e_vegetation_create_collision_only", 0),
            cvars.GetFloat("e_view_dist_ratio_detail", 30))
        {
            PhysicalFoliage = cvars.GetInt("e_phys_foliage", 2), VegetationBending = cvars.GetInt("e_vegetation_bending", 2),
            FoliageStiffness = cvars.GetFloat("e_foliage_stiffness", 3.2f)
        };
    }
}
public enum ZoneManagedVegetationPlacementStatus { Published, SkippedByConfiguration, SkippedByExclusion, Unsupported, ClassifiedNonBlocking }
public sealed record ZoneManagedVegetationPartPublication(int NodeChunkId, int PhysicsSlot,
    uint GeometryFlags, uint ColliderFlags, Matrix4x4 LocalTransform)
{
    public bool BlocksCharacters => (GeometryFlags & 2) != 0;
    public bool CollidesRay => (GeometryFlags & 0x8000) != 0;
    public ZonePhysicsQueryBounds LocalBounds { get; init; }
}
public sealed record ZoneManagedVegetationPlacementReport(string Source, int SourceOrder, int SourceOffset,
    short GroupId, string? AssetPath, ZoneManagedVegetationPlacementStatus Status, string Diagnostic,
    Matrix4x4? Transform, uint GeometryFlags, uint ColliderFlags, int CharacterInstances, int RayInstances)
{
    public IReadOnlyList<ZoneManagedVegetationPartPublication> Publications { get; init; } = [];
    public ZoneManagedVegetationFoliage? Foliage { get; init; }
    public float? ModelRadius { get; init; }
}
public sealed record ZoneManagedVegetationFoliage(int SerializedSpines, int CompiledSpines, int SpineVertices,
    int SkinnedVertices, int BoneIds);

internal static class ZoneManagedVegetationPublicationMath
{
    internal static float ModelRadius(ZoneCgfContent cgf)
    {
        var ordinary = cgf.Nodes.Where(node => node.IsOrdinaryMesh).ToArray();
        if (ordinary.Length == 0) throw new NotSupportedException("A native model radius requires an ordinary render mesh.");
        ZoneManagedBounds? union = null;
        foreach (var node in cgf.IsCompound ? ordinary : ordinary.Take(1))
        {
            var chunk = cgf.Chunks.Single(c => c.Id == node.ObjectChunkId);
            if (chunk.Type != 0xcccc0000 || chunk.Version != 0x800 || chunk.Length < 148)
                throw new NotSupportedException("Vegetation model radius requires the source Mesh0800 bbox fields.");
            var data = chunk.Data;
            static Vector3 Read(ReadOnlySpan<byte> bytes, int o) => new(BinaryPrimitives.ReadSingleLittleEndian(bytes[o..]),
                BinaryPrimitives.ReadSingleLittleEndian(bytes[(o + 4)..]), BinaryPrimitives.ReadSingleLittleEndian(bytes[(o + 8)..]));
            var bounds = new ZoneManagedBounds(Read(data, 124), Read(data, 136));
            if (!Finite(bounds.Minimum) || !Finite(bounds.Maximum) || Vector3.Min(bounds.Minimum, bounds.Maximum) != bounds.Minimum)
                throw new NotSupportedException("Vegetation model radius requires finite ordered authored render bounds.");
            if (cgf.IsCompound) bounds = TransformModelBounds(bounds, node.WorldTransform);
            union = union is { } previous ? new(Vector3.Min(previous.Minimum, bounds.Minimum), Vector3.Max(previous.Maximum, bounds.Maximum)) : bounds;
        }
        return Radius(union!.Value);
    }
    internal static float Radius(ZoneManagedBounds bounds)
    {
        var d = bounds.Minimum - bounds.Maximum;
        var radius = MathF.Sqrt((d.X * d.X + d.Y * d.Y) + d.Z * d.Z) * .5f;
        if (!float.IsFinite(radius)) throw new NotSupportedException("Vegetation model radius exceeds the finite source-math profile.");
        return radius;
    }
    //300212E0's unpadded center/extents transform. These render bounds feed ONLY the load-size gate.
    internal static ZoneManagedBounds TransformModelBounds(ZoneManagedBounds bounds, Matrix4x4 m)
    {
        var h = (bounds.Maximum - bounds.Minimum) * .5f; var c = (bounds.Maximum + bounds.Minimum) * .5f;
        var e = new Vector3((h.Y * MathF.Abs(m.M21) + h.X * MathF.Abs(m.M11)) + h.Z * MathF.Abs(m.M31),
            (h.Y * MathF.Abs(m.M22) + h.X * MathF.Abs(m.M12)) + h.Z * MathF.Abs(m.M32),
            (h.Y * MathF.Abs(m.M23) + h.X * MathF.Abs(m.M13)) + h.Z * MathF.Abs(m.M33));
        var p = new Vector3(((m.M21 * c.Y + m.M11 * c.X) + m.M31 * c.Z) + m.M41,
            ((m.M22 * c.Y + m.M12 * c.X) + m.M32 * c.Z) + m.M42,
            ((m.M23 * c.Y + m.M13 * c.X) + m.M33 * c.Z) + m.M43);
        if (!Finite(p) || !Finite(e)) throw new NotSupportedException("Vegetation render-bound transform exceeds finite source math.");
        return new(p - e, p + e);
    }
    internal static bool MinimumSizeAllows(float minimum, float radius, float rawScale) => minimum <= radius * rawScale;
    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    //30100E10's fresh node76 is the image's zero-initialized302FA6C8. Picking helpers are separate.
    internal static IReadOnlyList<ZoneManagedVegetationPartPublication> DirectRoot(int node, IReadOnlyCollection<int> slots,
        bool foliage, int physicalFoliage, bool breakEnergy, uint placementFlags)
    {
        var parts = new List<ZoneManagedVegetationPartPublication>();
        var hasDefault = slots.Contains(0);
        if (slots.Count == 0 || !hasDefault && !foliage) return parts;
        if (hasDefault) parts.Add(new(node, 0, 0x04018fff | (breakEnergy && (placementFlags & 2) == 0 ? 0x800000u : 0), 1, Matrix4x4.Identity));
        var first = slots.Contains(1) && physicalFoliage >= 2; var second = slots.Contains(2);
        if (first && second)
        {
            parts.Add(new(node, 2, 0x104000, 1, Matrix4x4.Identity));
            parts.Add(new(node, 1, 0x200000u | (foliage ? 0x2000u : 0), 0, Matrix4x4.Identity));
        }
        else if (first || second)
            parts.Add(new(node, first ? 1 : 2, 0x300000u | (second ? 0x4000u : 0) | (first && foliage ? 0x2000u : 0), 0, Matrix4x4.Identity));
        return parts;
    }

    //301D21F0 copies the exact24-byte spine entries and following arrays;3003EA10 compiles the64-bone limit.
    internal static ZoneManagedVegetationFoliage Foliage(ZoneCgfContent cgf)
    {
        var chunks = cgf.Chunks.Where(c => c.Type == 0xaafc0005).ToArray();
        if (chunks.Length == 0) return new(0, 0, 0, 0, 0);
        if (chunks.Length != 1) throw new NotSupportedException("Multiple foliage chunks require original source-order replacement ownership.");
        var bytes = chunks[0].Data;
        if (bytes.Length < 32) throw new InvalidDataException("Truncated foliage metadata header.");
        var n = BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]); var vertices = BinaryPrimitives.ReadInt32LittleEndian(bytes[20..]);
        var skinned = BinaryPrimitives.ReadInt32LittleEndian(bytes[24..]); var bones = BinaryPrimitives.ReadInt32LittleEndian(bytes[28..]);
        if (n < 0 || vertices < 0 || skinned < 0 || bones < 0) throw new InvalidDataException("Negative foliage metadata counts.");
        if (n == 0) return new(0, 0, vertices, skinned, bones); //Original loader does not dereference arrays on this branch.
        var end = 32L + 24L * n + 28L * vertices + 8L * skinned + 2L * bones;
        if (end > bytes.Length) throw new InvalidDataException("Foliage spine/vertex/skin/bone arrays exceed the authored chunk.");
        var sum = 0; var runningBones = 1; var lastAccepted = 0;
        for (var i = 0; i < n; i++)
        {
            var offset = 32 + i * 24; var count = bytes[offset];
            if (count > vertices - sum) throw new InvalidDataException("Foliage spine ranges exceed the copied vertex arrays.");
            sum += count;
            for (var j = 4; j < 20; j += 4)
                if (!float.IsFinite(BinaryPrimitives.ReadSingleLittleEndian(bytes[(offset + j)..])))
                    throw new InvalidDataException("Foliage spine coefficients must be finite.");
            if (count + runningBones - 1 <= 64) { lastAccepted = i; runningBones += count - 1; }
        }
        if (sum != vertices) throw new NotSupportedException("Foliage spine vertex ranges do not cover the copied vertex arrays exactly.");
        for (var offset = 32 + n * 24; offset < 32 + n * 24 + vertices * 28; offset += 4)
            if (!float.IsFinite(BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..])))
                throw new InvalidDataException("Foliage vertices/segment dimensions must be finite.");
        return new(n, n > lastAccepted + 1 ? 0 : n, vertices, skinned, bones);
    }
}

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
        if (!float.IsFinite(rawScale))
            throw new NotSupportedException("Vegetation transforms require a finite raw scale after the original model-radius/minimum-size gate.");
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
/// Shared immutable vegetation resources and generation-owned, mask-specific static publication into the scene's
/// existing character/ray geometries. Root specials, foliage metadata and selected default compound children retain
/// native publication and entity bounds; unowned dynamic/helper branches remain explicit. No other cell family is certified.
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
    private ulong _publicationRevision;
    /// <summary>Managed ownership token, not a native counter; includes bodyless placement lifetimes.</summary>
    public ulong PublicationRevision => _publicationRevision;
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
        if (!float.IsFinite(policy.MinimumVegetationSize) || !float.IsFinite(policy.CreateCollisionOnly) || !float.IsFinite(policy.FoliageStiffness) ||
            policy.DetailViewDistanceRatio is { } detail && !float.IsFinite(detail))
            throw new ArgumentException("Vegetation configuration scalars must be finite.");
    }
    public ZoneManagedVegetationCellLease BeginCell(ZoneWorldMapCellPlacement placement)
    {
        Check(); ArgumentNullException.ThrowIfNull(placement);
        if (!_layout.Cells.Any(cell => ReferenceEquals(cell, placement)) || placement.InvalidLocalX || placement.InvalidLocalY ||
            (uint)placement.EngineCell.X >= 8 || (uint)placement.EngineCell.Y >= 8)
            throw new ArgumentException("Vegetation requires this exact layout's resolved engine-local cell.", nameof(placement));
        AdvancePublication();
        if (_cells.TryGetValue(placement.EngineCell, out var old)) old.Dispose(); //Invalidation precedes replacement publication.
        var generation = checked(++_nextGeneration);
        var cell = new ZoneManagedVegetationCellLease(this, placement, generation); _cells.Add(placement.EngineCell, cell); return cell;
    }
    internal bool Current(ZoneManagedVegetationCellLease cell) => !IsDisposed && !cell.IsDisposed &&
        _cells.TryGetValue(cell.Placement.EngineCell, out var existing) && ReferenceEquals(existing, cell);
    internal void Retire(ZoneManagedVegetationCellLease cell)
    {
        if (_cells.TryGetValue(cell.Placement.EngineCell, out var existing) && ReferenceEquals(existing, cell))
        { AdvancePublication(); _cells.Remove(cell.Placement.EngineCell); }
    }
    private void AdvancePublication()
    {
        RequirePublicationCapacity(1);
        _publicationRevision++;
    }
    private void RequirePublicationCapacity(ulong count)
    {
        if (count > ulong.MaxValue - _publicationRevision)
            throw new InvalidOperationException("Vegetation publication revisions are exhausted.");
    }
    internal void PreflightRetirement(IEnumerable<ZoneManagedVegetationCellLease> cells)
    {
        ulong count = 0;
        foreach (var cell in cells)
        {
            if (cell.IsDisposed) continue;
            if (Current(cell)) count = checked(count + 1);
            count = checked(count + (ulong)cell.CopyPlacementLeases().Count(placement => !placement.IsDisposed));
        }
        RequirePublicationCapacity(count);
    }
    internal void InputPublicationChanged() => AdvancePublication();

    internal ZoneManagedVegetationPlacementLease Publish(ZoneManagedVegetationCellLease cell, string source,
        int order, int offset, ReadOnlySpan<byte> payload, Vector3 translation)
    {
        Require(cell);
        if (payload.Length != 64) throw new InvalidDataException("Vegetation source record has an invalid length.");
        if (_publishing) throw new InvalidOperationException("Vegetation publication cannot reenter an asset callback on the same scene owner.");
        var groupId = BinaryPrimitives.ReadInt16LittleEndian(payload[0x37..]);
        var characterHandles = new List<ZoneManagedStaticInstanceHandle>(); var rayHandles = new List<ZoneManagedStaticInstanceHandle>();
        Matrix4x4? matrix = null; string? asset = null; uint flags = 0; uint colliderFlags = 0;
        IReadOnlyList<ZoneManagedVegetationPartPublication> publications = []; ZoneManagedVegetationFoliage? foliage = null;
        AdvancePublication();
        _publishing = true;
        try
        {
            if (groupId < 0 || !_catalog.Groups.TryGetValue(groupId, out var group))
                throw new NotSupportedException($"Vegetation serialized group {groupId} has no actual world catalog definition.");
            if (!QualityAllowed(group) || Policy.VegetationEnabled == 0)
                return Finish(ZoneManagedVegetationPlacementStatus.SkippedByConfiguration, "Original vegetation/quality configuration excludes this placement.");
            asset = AssetPath(group.ModelFileName);
            if (!_resources.TryGetValue(asset, out var resource))
            { resource = LoadResource(asset); Require(cell); _resources.Add(asset, resource); }
            Require(cell); //Asset callbacks may have retired/replaced this exact cell.
            if (resource.Refusal is { } refusal) throw new NotSupportedException(refusal);
            foliage = resource.Foliage;
            var rawScale = BinaryPrimitives.ReadSingleLittleEndian(payload[0x33..]);
            if (!ZoneManagedVegetationPublicationMath.MinimumSizeAllows(Policy.MinimumVegetationSize, resource.ModelRadius, rawScale))
                return Finish(ZoneManagedVegetationPlacementStatus.SkippedByConfiguration, "Original minimum-size gate rejects model radius multiplied by RAW authored scale before clamping.");
            if (Policy.CreateCollisionOnly != 0 && !resource.HasDefault)
                return Finish(ZoneManagedVegetationPlacementStatus.SkippedByConfiguration, "Original collision-only gate requires recursive default4096 geometry.");
            var authoredBounds = new ZonePhysicsQueryBounds(translation + Vector(payload, 0), translation + Vector(payload, 12));
            if (_exclusions.Excludes(authoredBounds))
                return Finish(ZoneManagedVegetationPlacementStatus.SkippedByExclusion, "Original 301597D0 excludes this translated authored bounding box.");
            var suppressBreak = (BinaryPrimitives.ReadUInt32LittleEndian(payload[0x20..]) & 2) != 0;
            publications = Array.AsReadOnly(resource.Parts.Select(part => suppressBreak && part.RootDefault
                ? part.Publication with { GeometryFlags = part.Publication.GeometryFlags & ~0x800000u } : part.Publication).ToArray());
            foreach (var part in publications) { flags |= part.GeometryFlags; colliderFlags |= part.ColliderFlags; }
            if (!publications.Any(part => part.BlocksCharacters || part.CollidesRay))
                return Finish(ZoneManagedVegetationPlacementStatus.ClassifiedNonBlocking,
                    "Complete source publication has neither player bit2 nor ray bit0x8000; no alignment/material stand-in is used.");
            matrix = ZoneManagedVegetationPlacementMath.Transform(payload, translation, group.AlignToTerrain);
            var bounds = resource.Bounds.Transform(matrix.Value);
            var admission = new ZonePhysicsQueryBounds(bounds.Minimum, bounds.Maximum);
            foreach (var part in resource.Parts)
            {
                if (part.Publication.BlocksCharacters) characterHandles.Add(part.Add(_characters, matrix.Value, admission));
                if (part.Publication.CollidesRay) rayHandles.Add(part.Add(_rays, matrix.Value, admission));
            }
            Require(cell);
            return Finish(ZoneManagedVegetationPlacementStatus.Published, "Source vegetation proxies published for their player/ray masks; no brush picking policy applied.");
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
                colliderFlags, characterHandles.Count, rayHandles.Count) { Publications = publications, Foliage = foliage,
                    ModelRadius = asset is not null && _resources.TryGetValue(asset, out var copiedResource) && copiedResource.Refusal is null ? copiedResource.ModelRadius : null },
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
            var radius = ZoneManagedVegetationPublicationMath.ModelRadius(cgf);
            if (cgf.Nodes.Any(node => node.IsHelperMesh && node.IsCollisionSource && node.PhysicsParts.Count != 0))
                throw new NotSupportedException("Independent vegetation helper physics requires its original selection and publication plan.");
            var foliage = ZoneManagedVegetationPublicationMath.Foliage(cgf);
            if (cgf.Nodes.Any(node => node.Name.StartsWith("$joint", StringComparison.Ordinal) || node.Name.StartsWith("$cutdown", StringComparison.Ordinal)))
                throw new NotSupportedException("Vegetation joint/cutdown publication requires the original mutable joint owner.");
            if (cgf.IsCompound) return LoadCompoundResource(asset, cgf, foliage) with { ModelRadius = radius };
            var root = cgf.DirectPhysicsSource ?? throw new NotSupportedException("Vegetation has no selected direct physical root.");
            if (root.Diagnostics.Count != 0 || root.PhysicsParts.Any(part => part.Geometry is null))
                throw new NotSupportedException("Vegetation physical-root references are not completely decoded.");
            RequireStaticProperties(cgf, root);
            var source = root.PhysicsParts.SingleOrDefault(part => part.PhysicsSlot == 0)?.Geometry;
            var compiled = source is null ? null : LoadDefaultResource(asset, cgf, root, source);
            if (compiled?.Refusal is { } error) throw new NotSupportedException(error);
            var plan = ZoneManagedVegetationPublicationMath.DirectRoot(root.ChunkId, root.PhysicsParts.Select(p => p.PhysicsSlot).ToArray(),
                foliage.CompiledSpines != 0, Policy.PhysicalFoliage, compiled?.HasBreakEnergy == true, 0);
            var parts = plan.Select(publication =>
            {
                var geometry = root.PhysicsParts.Single(p => p.PhysicsSlot == publication.PhysicsSlot).Geometry!;
                var bounds = publication.BlocksCharacters || publication.CollidesRay ? compiled!.Parts[0].Bounds : BoundsFor(geometry);
                return (publication.BlocksCharacters || publication.CollidesRay ? compiled!.Parts[0] : new Part(null, null)) with
                {
                    Publication = publication with { LocalBounds = new(bounds.Minimum, bounds.Maximum) },
                    AuthoredBounds = bounds, RootDefault = publication.PhysicsSlot == 0
                };
            }).ToArray();
            var union = parts.Length == 0 ? default : parts.Select(p => p.Bounds.Transform(p.Publication.LocalTransform))
                .Aggregate((a, b) => new ZoneManagedBounds(Vector3.Min(a.Minimum, b.Minimum), Vector3.Max(a.Maximum, b.Maximum)));
            return new(parts, union, compiled?.HasBreakEnergy == true, null) { HasDefault = source is not null, Foliage = foliage, ModelRadius = radius };
        }
        catch (Exception error) when (ContentFailure(error)) { return new([], default, false, error.Message); }
    }

    private Resource LoadCompoundResource(string asset, ZoneCgfContent cgf, ZoneManagedVegetationFoliage foliage)
    {
        if (foliage.CompiledSpines != 0)
            throw new NotSupportedException("Active compound foliage requires its per-subobject spine localization/selection owner.");
        var nodes = cgf.Nodes.Where(node => node.IsOrdinaryMesh).ToArray();
        //30103DC0's prefix visits physical child statobjs before300442F0's flag2 admission. It selects each
        //default into the root list and records that child in the exclusion map passed to the final child pass.
        var parts = new List<Part>();
        foreach (var node in nodes)
        {
            RequireStaticProperties(cgf, node);
            if (node.Diagnostics.Count != 0 || node.PhysicsParts.Any(part => part.Geometry is null))
                throw new NotSupportedException("Compound vegetation contains an incompletely decoded selected child.");
            if (node.PhysicsParts.Any(part => part.PhysicsSlot != 0))
                throw new NotSupportedException("Compound special-child aggregation requires its complete selected-child map owner.");
            if (node.PhysicsParts.SingleOrDefault(part => part.PhysicsSlot == 0)?.Geometry is not { } source) continue;
            var compiled = LoadDefaultResource(asset, cgf, node, source);
            if (compiled.Refusal is { } error) throw new NotSupportedException(error);
            if (compiled.HasBreakEnergy)
                throw new NotSupportedException("Compound root compiled break-flag propagation requires independent source ownership.");
            var bounds = BoundsFor(source);
            parts.Add(compiled.Parts[0] with
            {
                Publication = new(node.ChunkId, 0, 0x04018fff, 1, Matrix4x4.Identity) { LocalBounds = new(bounds.Minimum, bounds.Maximum) },
                AuthoredBounds = bounds, RootDefault = true
            });
        }
        var union = parts.Count == 0 ? default : parts.Select(part => part.Bounds)
            .Aggregate((a, b) => new ZoneManagedBounds(Vector3.Min(a.Minimum, b.Minimum), Vector3.Max(a.Maximum, b.Maximum)));
        return new(parts.ToArray(), union, false, null) { HasDefault = parts.Count != 0, Foliage = foliage };
    }

    private static void RequireStaticProperties(ZoneCgfContent cgf, ZoneCgfNode node)
    {
        var data = cgf.Chunks.Single(chunk => chunk.Id == node.ChunkId).Data;
        var length = BinaryPrimitives.ReadInt32LittleEndian(data[216..]);
        if (length < 0 || length > data.Length - 220) throw new InvalidDataException("Vegetation node properties exceed their authored chunk.");
        var bytes = data.Slice(220, length); var end = bytes.IndexOf((byte)0); if (end >= 0) bytes = bytes[..end];
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        if (text.Length != 0 && !text.Equals("LowSpecLod0", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Vegetation static publication does not own node properties: " + text);
    }

    private Resource LoadDefaultResource(string asset, ZoneCgfContent cgf, ZoneCgfNode root, ZoneCgfPhysicsGeometry source)
    {
        try
        {
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
    private static ZoneManagedBounds BoundsFor(ZoneCgfPhysicsGeometry geometry)
    {
        if (geometry.Primitive is { } p)
        {
            var a = Vector3.Abs(p.Axis);
            var extent = p.Kind switch
            {
                ZoneCgfPhysicsGeometryKind.Sphere => new Vector3(p.Radius),
                ZoneCgfPhysicsGeometryKind.Capsule => a * p.HalfHeight + new Vector3(p.Radius),
                ZoneCgfPhysicsGeometryKind.Cylinder => a * p.HalfHeight + p.Radius * new Vector3(
                    MathF.Sqrt(MathF.Max(0, 1 - a.X * a.X)), MathF.Sqrt(MathF.Max(0, 1 - a.Y * a.Y)), MathF.Sqrt(MathF.Max(0, 1 - a.Z * a.Z))),
                _ => throw new NotSupportedException("Unknown vegetation primitive bounds.")
            };
            return new(p.Center - extent, p.Center + extent);
        }
        if (geometry.Vertices.Count == 0) throw new NotSupportedException("Vegetation proxy has no authored physical bounds.");
        return new(geometry.Vertices.Aggregate(Vector3.Min), geometry.Vertices.Aggregate(Vector3.Max));
    }
    internal void Remove(ZoneManagedVegetationPlacementLease lease)
    {
        AdvancePublication();
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
    {
        if (IsDisposed) return;
        PreflightRetirement(_cells.Values);
        foreach (var cell in _cells.Values.ToArray()) cell.Dispose();
        IsDisposed = true; _resources.Clear();
    }
    private sealed record Resource(Part[] Parts, ZoneManagedBounds Bounds, bool HasBreakEnergy, string? Refusal)
    {
        public bool HasDefault { get; init; }
        public float ModelRadius { get; init; }
        public ZoneManagedVegetationFoliage Foliage { get; init; } = new(0, 0, 0, 0, 0);
    }
    private sealed record Part(ZoneManagedTriangleMesh? Mesh, ZoneManagedPrimitiveGeometry? Primitive)
    {
        internal ZoneManagedVegetationPartPublication Publication { get; init; } = new(-1, 0, 0x04018fff, 1, Matrix4x4.Identity);
        internal bool RootDefault { get; init; } = true;
        internal ZoneManagedBounds? AuthoredBounds { get; init; }
        internal ZoneManagedBounds Bounds => AuthoredBounds ?? Primitive?.Bounds ?? new(Mesh!.BoundsMinimum, Mesh.BoundsMaximum);
        internal ZoneManagedStaticInstanceHandle Add(ZoneManagedStaticGeometry target, Matrix4x4 transform, ZonePhysicsQueryBounds bounds) =>
            Primitive is { } shape ? target.AddInstance(shape, Publication.LocalTransform * transform, bounds)
                : target.AddInstance(Mesh!, Publication.LocalTransform * transform, bounds);
    }
}

internal sealed record ZoneManagedVegetationTaggedInput(ZoneCellObjectContent Content, bool Completed);
internal sealed record ZoneManagedVegetationSectorInput(ZoneCellVegetationContent Content, bool Completed);
public sealed class ZoneManagedVegetationCellLease : IDisposable
{
    private readonly ZoneManagedVegetationResources _owner;
    private readonly List<ZoneManagedVegetationPlacementLease> _placements = [];
    private readonly Dictionary<ZoneCellObjectFileKind, ZoneManagedVegetationTaggedInput> _taggedInputs = [];
    internal ZoneManagedVegetationSectorInput? SectorInput { get; private set; }
    internal IReadOnlyList<ZoneManagedVegetationTaggedInput> CopyTaggedInputs() => _taggedInputs.Values.ToArray();
    internal IReadOnlyList<ZoneManagedVegetationPlacementLease> CopyPlacementLeases() => _placements.ToArray();
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
        if (_taggedInputs.ContainsKey(content.Kind)) throw new InvalidOperationException("This cell's tagged vegetation input was already attempted.");
        _owner.InputPublicationChanged();
        _taggedInputs.Add(content.Kind, new(content, false));
        var translation = new Vector3(Placement.EngineCell.X << 10, Placement.EngineCell.Y << 10, 0);
        foreach (var row in content.Objects.OfType<ZoneCellVegetationObject>())
            _placements.Add(_owner.Publish(this, content.Kind.ToString(), row.SourceOrder, row.SourceOffset, row.RawPayload, translation));
        _owner.Require(this);
        _owner.InputPublicationChanged();
        _taggedInputs[content.Kind] = new(content, true);
    }
    public void LoadSectors(ZoneCellVegetationContent content)
    {
        _owner.Require(this); ArgumentNullException.ThrowIfNull(content);
        if (SectorInput is not null) throw new InvalidOperationException("This cell's sector vegetation input was already attempted.");
        _owner.InputPublicationChanged();
        SectorInput = new(content, false);
        foreach (var sector in content.Sectors)
        {
            var translation = new Vector3((Placement.EngineCell.X * 4 + sector.LocalX) << 8,
                (Placement.EngineCell.Y * 4 + sector.LocalY) << 8, 0);
            foreach (var row in sector.Instances)
                _placements.Add(_owner.Publish(this, $"vegetation.dat/sector{sector.SourceIndex}", row.SourceOrder, row.SourceOffset, row.RawPayload, translation));
        }
        _owner.Require(this);
        _owner.InputPublicationChanged();
        SectorInput = new(content, true);
    }
    public void Dispose()
    {
        if (IsDisposed) return;
        _owner.PreflightRetirement([this]);
        _owner.Retire(this); //Checked lifetime advance precedes state/handle changes.
        IsDisposed = true;
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
    public void Dispose() { if (IsDisposed) return; _owner.Remove(this); IsDisposed = true; }
}
