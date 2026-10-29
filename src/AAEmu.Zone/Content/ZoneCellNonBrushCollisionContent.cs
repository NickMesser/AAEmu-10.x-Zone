using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Content;

public enum ZoneCellNonBrushCollisionOutcome
{
    NoPhysicalContribution, RequiresAnotherOwner, MissingDependency, Unsupported, InvalidData
}

/// <summary>Part flags, distinct from the world's entity-selection mask.</summary>
[Flags]
public enum ZoneCellNonBrushCollisionMask { None = 0, Player = 2, Ray = 0x8000 }

public enum ZoneCellNonBrushLoadProfile { NormalAuthoredFactoryAndCompile }

public sealed record ZoneCellNonBrushDependency(string AssetPath, int ByteLength, string Sha256,
    ZoneCellNonBrushCollisionOutcome Outcome, string? Diagnostic);

/// <summary>Copied normal-load parameters; these vertices are not collision geometry or a compiled road mesh.</summary>
public sealed class ZoneCellRoadLoadParameters
{
    public Vector4 Parameters { get; }
    public byte SortPriority { get; }
    public IReadOnlyList<Vector3> Vertices { get; }
    internal ZoneCellRoadLoadParameters(Vector4 parameters, byte priority, IEnumerable<Vector3> vertices)
    { Parameters = parameters; SortPriority = priority; Vertices = Array.AsReadOnly(vertices.ToArray()); }
}

public sealed record ZoneCellDecalLoadParameters(byte AuthoredProjectionType, Vector3 Position, Vector3 Direction,
    Vector3 OrientationRow0, Vector3 OrientationRow1, Vector3 OrientationRow2, float Radius, byte SortPriority);

/// <summary>
/// Source-only proof for this exact parsed row under the normal authored factory/compile lifecycle.
/// Cell generation, current coverage and exclusion of external road/body mutation belong to the scene owner.
/// </summary>
public sealed class ZoneCellNonBrushCollisionCertificate
{
    public ZoneCellObjectContent SourceContent { get; }
    public ZoneCellObjectRecord SourceRow { get; }
    public ZoneCellObjectFileKind FileKind => SourceContent.Kind;
    public ZoneCellObjectType Type => SourceRow.Type;
    public int SourceOrder => SourceRow.SourceOrder;
    public int SourceOffset => SourceRow.SourceOffset;
    public int? OctreeNodeIndex => SourceRow.OctreeNodeIndex;
    public string PayloadSha256 { get; }
    public int EngineCellX { get; }
    public int EngineCellY { get; }
    public ZoneCellNonBrushLoadProfile Profile => ZoneCellNonBrushLoadProfile.NormalAuthoredFactoryAndCompile;
    public ZoneCellNonBrushCollisionOutcome Outcome { get; }
    public bool HasNoPhysicalContribution => Outcome == ZoneCellNonBrushCollisionOutcome.NoPhysicalContribution;
    public ZoneCellNonBrushCollisionMask CertifiedMask => HasNoPhysicalContribution
        ? ZoneCellNonBrushCollisionMask.Player | ZoneCellNonBrushCollisionMask.Ray : ZoneCellNonBrushCollisionMask.None;
    public Vector3 TranslatedAuthoredBoundsMin { get; }
    public Vector3 TranslatedAuthoredBoundsMax { get; }
    public ZoneCellObjectResource? MaterialResource { get; }
    public string? NormalizedMaterialName { get; }
    public ZoneCellRoadLoadParameters? Road { get; }
    public ZoneCellDecalLoadParameters? Decal { get; }
    public IReadOnlyList<ZoneCellNonBrushDependency> Dependencies { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    /// <summary>The certificate never asserts that renderer meshes, textures or decal projection are ready.</summary>
    public bool RenderingReady => false;

    internal ZoneCellNonBrushCollisionCertificate(ZoneCellObjectContent content, ZoneCellObjectRecord row,
        int x, int y, ZoneCellNonBrushCollisionOutcome outcome, ZoneCellObjectResource? material = null,
        string? materialName = null, ZoneCellRoadLoadParameters? road = null, ZoneCellDecalLoadParameters? decal = null,
        IEnumerable<ZoneCellNonBrushDependency>? dependencies = null, IEnumerable<string>? diagnostics = null)
    {
        SourceContent = content; SourceRow = row; EngineCellX = x; EngineCellY = y; Outcome = outcome;
        PayloadSha256 = Convert.ToHexString(SHA256.HashData(row.RawPayload)).ToLowerInvariant();
        var offset = new Vector3(x << 10, y << 10, 0);
        TranslatedAuthoredBoundsMin = row.BoundsMin + offset; TranslatedAuthoredBoundsMax = row.BoundsMax + offset;
        MaterialResource = material; NormalizedMaterialName = materialName; Road = road; Decal = decal;
        Dependencies = Array.AsReadOnly((dependencies ?? []).ToArray());
        Diagnostics = Array.AsReadOnly((diagnostics ?? []).ToArray());
    }
}

/// <summary>
/// Bounded road/decal source certificates. Does not instantiate render nodes or physical geometry,
/// certify vegetation/brush/water, invoke Native code, or turn metadata into scene coverage.
/// The asset source is borrowed; material definitions are frozen per owner, not per filename globally.
/// </summary>
public sealed class ZoneCellNonBrushCollisionContent : IDisposable
{
    //Replacement work limits, not recovered engine allocation limits.
    public const int MaximumMaterialBytes = 4 * 1024 * 1024;
    public const int MaximumMaterialXmlNodes = 100_000;
    public const int MaximumMaterialXmlDepth = 64;
    public const int MaximumMaterials = 512;
    public const int MaximumCertificates = 65_536;
    private readonly IZoneAssetSource _assets;
    private readonly object _gate = new();
    private readonly Dictionary<Key, ZoneCellNonBrushCollisionCertificate> _certificates = [];
    private readonly HashSet<Key> _classifying = [];
    private readonly Dictionary<string, Snapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reading = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MaterialResult> _materials = new(StringComparer.Ordinal);
    private readonly HashSet<string> _resolving = new(StringComparer.Ordinal);
    private bool _disposed;
    public bool IsDisposed { get { lock (_gate) return _disposed; } }
    public int MaterialSnapshotCount { get { lock (_gate) return _snapshots.Count; } }

    public ZoneCellNonBrushCollisionContent(IZoneAssetSource assets) =>
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));

    public ZoneCellNonBrushCollisionCertificate Classify(ZoneCellObjectContent content, ZoneCellObjectRecord row,
        int engineCellX, int engineCellY)
    {
        ArgumentNullException.ThrowIfNull(content); ArgumentNullException.ThrowIfNull(row);
        if ((uint)engineCellX >= 8 || (uint)engineCellY >= 8)
            throw new ArgumentOutOfRangeException(nameof(engineCellX), "The normal loaded engine window is eight by eight cells.");
        if ((uint)row.SourceOrder >= content.Objects.Count || !ReferenceEquals(content.Objects[row.SourceOrder], row))
            throw new ArgumentException("A row certificate requires the exact record in its supplied parsed source content.", nameof(row));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var key = new Key(content, row, engineCellX, engineCellY);
            if (_certificates.TryGetValue(key, out var cached)) return cached;
            if (_certificates.Count + _classifying.Count >= MaximumCertificates)
                return Refuse(ZoneCellNonBrushCollisionOutcome.Unsupported, "The replacement row-certificate work bound was exceeded.");
            if (!_classifying.Add(key)) throw new InvalidOperationException("Non-brush source classification cannot reenter the same exact row.");
            try
            {
                var result = ClassifyCore(content, row, engineCellX, engineCellY);
                ObjectDisposedException.ThrowIf(_disposed, this);
                //OpenRead is caller code and can reenter through another already loaded material.
                if (_certificates.Count >= MaximumCertificates)
                    return Refuse(ZoneCellNonBrushCollisionOutcome.Unsupported, "The shared row-certificate work bound was reached during source loading.");
                _certificates.Add(key, result);
                return result;
            }
            finally { _classifying.Remove(key); }
            ZoneCellNonBrushCollisionCertificate Refuse(ZoneCellNonBrushCollisionOutcome outcome, string message) =>
                new(content, row, engineCellX, engineCellY, outcome, diagnostics: [message]);
        }
    }

    private ZoneCellNonBrushCollisionCertificate ClassifyCore(ZoneCellObjectContent content, ZoneCellObjectRecord row, int x, int y)
    {
        ZoneCellRoadLoadParameters? roadParameters = null;
        ZoneCellDecalLoadParameters? decalParameters = null;
        ZoneCellObjectResource? selectedMaterial = null;
        if (row is ZoneCellBrushObject or ZoneCellVegetationObject or ZoneCellWaterVolumeObject)
            return Result(ZoneCellNonBrushCollisionOutcome.RequiresAnotherOwner, "This authored family requires its concrete brush, vegetation or water owner.");
        if (row is not (ZoneCellRoadObject or ZoneCellDecalObject))
            return Result(ZoneCellNonBrushCollisionOutcome.Unsupported, "No verified normal-load physical publication profile exists for this record family.");
        if (!Finite(row.BoundsMin) || !Finite(row.BoundsMax) || row.BoundsMin.X > row.BoundsMax.X ||
            row.BoundsMin.Y > row.BoundsMax.Y || row.BoundsMin.Z > row.BoundsMax.Z)
            return Result(ZoneCellNonBrushCollisionOutcome.InvalidData, "The authored render-node bounds are not finite and ordered.");

        var offset = new Vector3(x << 10, y << 10, 0);
        int materialIndex;
        if (row is ZoneCellRoadObject road)
        {
            if (road.Vertices.Count < 4 || (road.Vertices.Count & 1) != 0 ||
                !Finite(road.Parameters) || road.Vertices.Any(vertex => !Finite(vertex)))
                return Result(ZoneCellNonBrushCollisionOutcome.Unsupported,
                    "The normal road compile profile requires at least four paired finite authored vertices and finite parameters.");
            roadParameters = new(road.Parameters, road.MaterialHighByte, road.Vertices.Select(vertex => vertex + offset));
            materialIndex = road.MaterialIndex;
        }
        else
        {
            var decal = (ZoneCellDecalObject)row;
            if (decal.ProjectionType is not (0 or 2))
                return Result(ZoneCellNonBrushCollisionOutcome.Unsupported, "Only the verified target's ordinary decal projection types 0 and 2 are admitted.");
            if (!Finite(decal.Position) || !Finite(decal.Direction) || !Finite(decal.OrientationRow0) ||
                !Finite(decal.OrientationRow1) || !Finite(decal.OrientationRow2) || !float.IsFinite(decal.Float67) || decal.Float67 <= 0)
                return Result(ZoneCellNonBrushCollisionOutcome.InvalidData, "The ordinary decal load parameters require finite values and positive radius.");
            decalParameters = new(decal.ProjectionType, decal.Position + offset, decal.Direction, decal.OrientationRow0,
                decal.OrientationRow1, decal.OrientationRow2, decal.Float67, decal.MaterialHighByte);
            materialIndex = decal.MaterialIndex;
        }
        var material = selectedMaterial = content.GetMaterialResource(materialIndex);
        if (material is null || string.IsNullOrWhiteSpace(material.Name))
            return Result(ZoneCellNonBrushCollisionOutcome.MissingDependency, "The selected authored material table entry has no resource name.");
        MaterialResult loaded;
        try { loaded = LoadMaterial(material.Name); }
        catch (ArgumentException error)
        { return Result(ZoneCellNonBrushCollisionOutcome.Unsupported, "The material reference has no supported virtual-path normalization: " + error.Message); }
        return new(content, row, x, y, loaded.Outcome, material, loaded.Name, roadParameters, decalParameters,
            loaded.Dependencies, loaded.Diagnostics);

        ZoneCellNonBrushCollisionCertificate Result(ZoneCellNonBrushCollisionOutcome outcome, string message) =>
            new(content, row, x, y, outcome, material: selectedMaterial, road: roadParameters, decal: decalParameters, diagnostics: [message]);
    }

    private MaterialResult LoadMaterial(string name)
    {
        var key = ZoneTerrainMaterialResolver.GetMaterialKey(name);
        if (_materials.TryGetValue(key, out var cached)) return cached;
        if (_materials.Count + _resolving.Count >= MaximumMaterials)
            return new(key, ZoneCellNonBrushCollisionOutcome.Unsupported, [], ["The replacement material work bound was exceeded."]);
        if (!_resolving.Add(key)) throw new InvalidOperationException("Material source loading cannot reenter the same normalized reference.");
        try
        {
            var dependencies = new List<ZoneCellNonBrushDependency>();
            var resolver = new ZoneTerrainMaterialResolver(new MaterialSource(this, dependencies), ZonePhysicalSurfaceCatalog.DefaultOnly);
            var resolved = resolver.Load(name);
            ObjectDisposedException.ThrowIf(_disposed, this);
            var diagnostics = dependencies.Where(row => row.Outcome != ZoneCellNonBrushCollisionOutcome.NoPhysicalContribution)
                .Select(row => row.AssetPath + ": " + row.Diagnostic).ToArray();
            var outcome = dependencies.FirstOrDefault(row => row.Outcome != ZoneCellNonBrushCollisionOutcome.NoPhysicalContribution)?.Outcome
                ?? ZoneCellNonBrushCollisionOutcome.NoPhysicalContribution;
            if (dependencies.Count == 0 || ReferenceEquals(resolved, resolver.ErrorMaterial))
            {
                if (outcome == ZoneCellNonBrushCollisionOutcome.NoPhysicalContribution) outcome = ZoneCellNonBrushCollisionOutcome.Unsupported;
                diagnostics = diagnostics.Append("The ordinary material must resolve from an admitted authored text definition; built-in/error fallback is not this certificate profile.").ToArray();
            }
            var result = new MaterialResult(key, outcome, dependencies.ToArray(), diagnostics);
            _materials.Add(key, result); return result;
        }
        finally { _resolving.Remove(key); }
    }

    private Snapshot ReadMaterialSnapshot(string path)
    {
        if (_snapshots.TryGetValue(path, out var cached)) return cached;
        if (_snapshots.Count + _reading.Count >= MaximumMaterials)
            return Failure(ZoneCellNonBrushCollisionOutcome.Unsupported, "The replacement material snapshot bound was exceeded.");
        if (!_reading.Add(path)) throw new InvalidOperationException("A material source cannot reenter its own asset read.");
        try
        {
            Snapshot result;
            using (var input = _assets.OpenRead(path))
            {
                if (input is null) result = Failure(ZoneCellNonBrushCollisionOutcome.MissingDependency, "The authored material definition is absent.");
                else
                {
                    byte[] bytes;
                    try { bytes = ReadBounded(input); }
                    catch (InvalidDataException error) { result = Failure(ZoneCellNonBrushCollisionOutcome.InvalidData, error.Message); goto Complete; }
                    var dependency = new ZoneCellNonBrushDependency(path, bytes.Length,
                        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), ZoneCellNonBrushCollisionOutcome.NoPhysicalContribution, null);
                    try
                    {
                        using var reader = XmlReader.Create(new MemoryStream(bytes, false), new XmlReaderSettings
                        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumMaterialBytes });
                        var nodes = 0;
                        while (reader.Read())
                            if (++nodes > MaximumMaterialXmlNodes || reader.Depth > MaximumMaterialXmlDepth || reader.NodeType == XmlNodeType.ProcessingInstruction)
                                throw new InvalidDataException("The material XML exceeds the bounded ordinary source structure.");
                        using var xmlInput = new MemoryStream(bytes, false);
                        var document = XDocument.Load(xmlInput);
                        var root = document.Root;
                        if (root is null || root.Name.Namespace != XNamespace.None || !root.Name.LocalName.Equals("Material", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("The dependency is not an ordinary text Material definition.");
                        var flags = root.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName.Equals("MtlFlags", StringComparison.OrdinalIgnoreCase))?.Value ?? "0";
                        if (!uint.TryParse(flags, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedFlags))
                            throw new InvalidDataException("The material flags do not use the admitted finite decimal profile.");
                        if ((parsedFlags & 0x100) != 0 || root.Elements().Any(child => child.Name.Namespace != XNamespace.None ||
                            child.Name.LocalName is not ("Textures" or "PublicParams")) || !OrdinaryMaterialStructure(root))
                            dependency = dependency with { Outcome = ZoneCellNonBrushCollisionOutcome.Unsupported,
                                Diagnostic = "Only ordinary single-material Textures/PublicParams definitions are admitted; graph/external directives require their own dependency profile." };
                        result = new(bytes, dependency);
                    }
                    catch (Exception error) when (error is XmlException or InvalidDataException)
                    { result = new(bytes, dependency with { Outcome = ZoneCellNonBrushCollisionOutcome.InvalidData, Diagnostic = error.Message }); }
                }
            }
        Complete:
            ObjectDisposedException.ThrowIf(_disposed, this);
            _snapshots.Add(path, result); return result;
        }
        finally { _reading.Remove(path); }
        Snapshot Failure(ZoneCellNonBrushCollisionOutcome outcome, string diagnostic) => new(null, new(path, 0, "", outcome, diagnostic));
    }

    private static byte[] ReadBounded(Stream input)
    {
        if (input.CanSeek && input.Length > MaximumMaterialBytes) throw new InvalidDataException("The material exceeds the replacement source byte bound.");
        using var output = new MemoryStream(); var buffer = new byte[8192];
        int count;
        while ((count = input.Read(buffer)) != 0)
        {
            if (output.Length + count > MaximumMaterialBytes) throw new InvalidDataException("The material exceeds the replacement source byte bound.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool Finite(Vector4 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
    private static bool OrdinaryMaterialStructure(XElement root)
    {
        foreach (var element in root.DescendantsAndSelf())
        {
            if (element.Name.Namespace != XNamespace.None || element.Attributes().Any(attribute => attribute.Name.Namespace != XNamespace.None) ||
                element.Attributes().Select(attribute => attribute.Name.LocalName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != element.Attributes().Count() ||
                element.Nodes().OfType<XText>().Any(text => !string.IsNullOrWhiteSpace(text.Value))) return false;
        }
        return root.Elements().All(child => child.Name.LocalName switch
        {
            "PublicParams" => !child.HasElements,
            "Textures" => child.Elements().All(texture => texture.Name.LocalName == "Texture" &&
                texture.Elements().All(modifier => modifier.Name.LocalName == "TexMod" && !modifier.HasElements)),
            _ => false
        });
    }
    public void Dispose()
    {
        lock (_gate)
        { if (_disposed) return; _disposed = true; _certificates.Clear(); _snapshots.Clear(); _materials.Clear(); }
    }
    private readonly record struct Key(ZoneCellObjectContent Content, ZoneCellObjectRecord Row, int X, int Y);
    private sealed record Snapshot(byte[]? Bytes, ZoneCellNonBrushDependency Dependency);
    private sealed record MaterialResult(string Name, ZoneCellNonBrushCollisionOutcome Outcome,
        IReadOnlyList<ZoneCellNonBrushDependency> Dependencies, IReadOnlyList<string> Diagnostics);
    private sealed class MaterialSource(ZoneCellNonBrushCollisionContent owner, List<ZoneCellNonBrushDependency> dependencies) : IZoneAssetSource
    {
        public Stream? OpenRead(string virtualPath)
        {
            Snapshot snapshot;
            try { snapshot = owner.ReadMaterialSnapshot(virtualPath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                //The ordinary resolver uses its error material; retain the actual failed source
                //instead of allowing that fallback to become a successful row certificate.
                dependencies.Add(new(virtualPath, 0, "", ZoneCellNonBrushCollisionOutcome.InvalidData, error.Message));
                throw;
            }
            dependencies.Add(snapshot.Dependency);
            return snapshot.Dependency.Outcome == ZoneCellNonBrushCollisionOutcome.NoPhysicalContribution
                ? new MemoryStream(snapshot.Bytes!, false) : null;
        }
    }
}
