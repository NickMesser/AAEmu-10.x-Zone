using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Content;

public enum ZoneCellWaterQueryClassOutcome { CertifiedClassUpperBound, Unsupported, InvalidData }
public enum ZoneCellWaterMaterialBranch { NegativeIndexUsesNull, NamedTableReference, EmptyNameMayUseNativeFallback }
public enum ZoneCellWaterQueryClassProfile { NormalAuthoredLakeRiverFactory }

/// <summary>
/// The complete normal factory can produce no area, or a class-five area. This is an entity
/// selection bound, not a physical instance count, universal nonphysical claim or water-level result.
/// </summary>
public sealed class ZoneCellWaterQueryClassCertificate
{
    public const int AreaSimulationClass = 5;
    public const uint AreaEntitySelectionClass = 1u << AreaSimulationClass;
    public ZoneCellObjectContent SourceContent { get; }
    public ZoneCellWaterVolumeObject SourceRow { get; }
    public ZoneCellObjectFileKind FileKind => SourceContent.Kind;
    public int SourceOrder => SourceRow.SourceOrder;
    public int SourceOffset => SourceRow.SourceOffset;
    public int? OctreeNodeIndex => SourceRow.OctreeNodeIndex;
    public string PayloadSha256 { get; }
    public ZoneCellWaterQueryClassProfile Profile => ZoneCellWaterQueryClassProfile.NormalAuthoredLakeRiverFactory;
    public ZoneCellWaterQueryClassOutcome Outcome { get; }
    public bool HasClassUpperBound => Outcome == ZoneCellWaterQueryClassOutcome.CertifiedClassUpperBound;
    public int? SimulationClass => HasClassUpperBound ? AreaSimulationClass : null;
    public uint? EntitySelectionClass => HasClassUpperBound ? AreaEntitySelectionClass : null;
    public ZoneCellObjectResource? MaterialResource { get; }
    /// <summary>A normalized reference only; no material, shader or texture readiness is claimed.</summary>
    public string? NormalizedMaterialName { get; }
    public ZoneCellWaterMaterialBranch MaterialBranch { get; }
    public IReadOnlyList<string> Diagnostics { get; }

    internal ZoneCellWaterQueryClassCertificate(ZoneCellObjectContent content, ZoneCellWaterVolumeObject row,
        ZoneCellWaterQueryClassOutcome outcome, ZoneCellObjectResource? material, string? name,
        ZoneCellWaterMaterialBranch branch, params string[] diagnostics)
    {
        SourceContent = content; SourceRow = row; Outcome = outcome; MaterialResource = material;
        NormalizedMaterialName = name; MaterialBranch = branch;
        PayloadSha256 = Convert.ToHexString(SHA256.HashData(row.RawPayload)).ToLowerInvariant();
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }

    /// <summary>
    /// True means the class bound is selected and an actual area/query owner is required.
    /// It does not assert that this row creates one area, intersects the query, or is currently live.
    /// </summary>
    public bool CanContributeTo(uint entitySelectionMask)
    {
        if (!HasClassUpperBound)
            throw new NotSupportedException("The water row has no admitted entity-class bound: " + Diagnostics.FirstOrDefault());
        return (entitySelectionMask & AreaEntitySelectionClass) != 0;
    }
}

/// <summary>
/// Exact-row source classification for 30223480 ->300F5E90 ->World.AddArea35047C80 ->
/// CPhysArea35042150. Material/fallback branches preserve this class bound. External SetPhysics,
/// water edits and arbitrary render-node mutation are outside this normal authored lifetime.
/// </summary>
public sealed class ZoneCellWaterQueryClassContent : IDisposable
{
    public const int MaximumCertificates = 65_536; //Replacement resource bound.
    private readonly object _gate = new();
    private readonly Dictionary<(ZoneCellObjectContent Content, ZoneCellWaterVolumeObject Row), ZoneCellWaterQueryClassCertificate> _cache = [];
    private bool _disposed;
    public bool IsDisposed { get { lock (_gate) return _disposed; } }

    public ZoneCellWaterQueryClassCertificate Classify(ZoneCellObjectContent content, ZoneCellWaterVolumeObject row)
    {
        ArgumentNullException.ThrowIfNull(content); ArgumentNullException.ThrowIfNull(row);
        if ((uint)row.SourceOrder >= content.Objects.Count || !ReferenceEquals(content.Objects[row.SourceOrder], row))
            throw new ArgumentException("Water selection classification requires the exact row in its parsed source content.", nameof(row));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cache.TryGetValue((content, row), out var cached)) return cached;
            if (_cache.Count >= MaximumCertificates)
                return new(content, row, ZoneCellWaterQueryClassOutcome.Unsupported, null, null, default,
                    "The replacement water class-certificate resource bound was exceeded.");
            var result = ClassifyCore(content, row);
            _cache.Add((content, row), result); return result;
        }
    }

    private static ZoneCellWaterQueryClassCertificate ClassifyCore(ZoneCellObjectContent content, ZoneCellWaterVolumeObject row)
    {
        var material = content.GetMaterialResource(row.MaterialIndex);
        var branch = row.MaterialIndex < 0 ? ZoneCellWaterMaterialBranch.NegativeIndexUsesNull :
            string.IsNullOrWhiteSpace(material?.Name) ? ZoneCellWaterMaterialBranch.EmptyNameMayUseNativeFallback : ZoneCellWaterMaterialBranch.NamedTableReference;
        string? normalizedName = null;
        if (branch == ZoneCellWaterMaterialBranch.NamedTableReference)
        {
            try { normalizedName = ZoneTerrainMaterialResolver.GetMaterialKey(material!.Name); }
            catch (ArgumentException error) { return Refuse(ZoneCellWaterQueryClassOutcome.Unsupported, error.Message); }
        }
        if (row.VolumeType is not (2 or 3))
            return Refuse(ZoneCellWaterQueryClassOutcome.Unsupported, "Only the audited normal lake/type2 and river/type3 factory families have this physical-class bound.");
        if (!Finite(row.BoundsMin) || !Finite(row.BoundsMax) || row.BoundsMin.X > row.BoundsMax.X ||
            row.BoundsMin.Y > row.BoundsMax.Y || row.BoundsMin.Z > row.BoundsMax.Z ||
            !Finite(row.AuthoredPlane) || row.Vertices.Any(vertex => !Finite(vertex)) || row.PhysicsVertices.Any(vertex => !Finite(vertex)))
            return Refuse(ZoneCellWaterQueryClassOutcome.InvalidData, "Water source bounds, plane and both complete vertex arrays must be finite and ordered.");
        if (row.AuthoredPlane.X != 0 || row.AuthoredPlane.Y != 0 || row.AuthoredPlane.Z != 1)
            return Refuse(ZoneCellWaterQueryClassOutcome.Unsupported, "The first class-bound certificate admits the verified horizontal lake/river source profile.");
        if (row.VolumeType == 2 && row.Vertices.Count < 3 || row.VolumeType == 3 && row.Vertices.Count != 4)
            return Refuse(ZoneCellWaterQueryClassOutcome.Unsupported, "The render setup does not satisfy the normal lake/river constructor prerequisites.");
        var depth = BinaryPrimitives.ReadSingleLittleEndian(row.RawPayload[0x6b..]);
        var flow = BinaryPrimitives.ReadSingleLittleEndian(row.RawPayload[0x6f..]);
        if (!float.IsFinite(depth) || !float.IsFinite(flow))
            return Refuse(ZoneCellWaterQueryClassOutcome.InvalidData, "Nonfinite authored physical depth or flow has no admitted normal source profile.");
        //Creation/quality/dedup/empty-contour failure can remove an area but cannot add another
        //class. This is not a claim that every serialized water row produces one physical body.
        return new(content, row, ZoneCellWaterQueryClassOutcome.CertifiedClassUpperBound, material, normalizedName, branch);

        ZoneCellWaterQueryClassCertificate Refuse(ZoneCellWaterQueryClassOutcome outcome, string message) =>
            new(content, row, outcome, material, normalizedName, branch, message);
    }
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool Finite(Vector4 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
    public void Dispose() { lock (_gate) { if (_disposed) return; _disposed = true; _cache.Clear(); } }
}
