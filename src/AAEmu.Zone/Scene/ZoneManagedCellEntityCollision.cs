using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using AAEmu.Zone.Content;

namespace AAEmu.Zone.Scene;

public enum ZoneCellEntityCollisionOutcome { NoPhysicalContribution, MissingData, Unsupported, InvalidData }
public enum ZoneCellEntityCollisionDependencyKind { CellDocument, EntityClass, Script, ParticleLibrary }

/// <summary>An exact retained resource snapshot, not an assertion that a mutable asset path is current.</summary>
public sealed record ZoneCellEntityCollisionDependency(ZoneCellEntityCollisionDependencyKind Kind,
    string AssetPath, int ByteLength, string Sha256);
public sealed record ZoneCellEntityCollisionDiagnostic(string SourcePath, string Message);
public sealed record ZoneCellEntityCollisionLink(ulong EntityGuid, int TargetSourceChildIndex,
    string TargetClassName, string TargetCanonicalXmlSha256);

/// <summary>One immutable authored row result; its XML fingerprint is canonical UTF-8 XML, not a byte slice of the file.</summary>
public sealed class ZoneCellEntityCollisionRow
{
    public int SourceChildIndex { get; }
    public ZoneCellEntityRow? InputRow { get; }
    public ulong EntityGuid => InputRow?.EntityGuid ?? 0;
    public string ClassName => InputRow?.ClassName ?? "";
    public string CanonicalXmlSha256 { get; }
    public ZoneCellEntityCollisionOutcome Outcome { get; }
    /// <summary>Native part category bits, not world entity-selection bits. Zero on every refusal.</summary>
    public uint CertifiedPartFlags => Outcome == ZoneCellEntityCollisionOutcome.NoPhysicalContribution ? 0x8002u : 0;
    public IReadOnlyList<ZoneCellEntityCollisionDependency> Dependencies { get; }
    public IReadOnlyList<ZoneCellEntityCollisionLink> AreaLinks { get; }
    public ZoneParticleCollisionCertificate? ParticleGraph { get; }
    public IReadOnlyList<ZoneCellEntityCollisionDiagnostic> Diagnostics { get; }

    internal ZoneCellEntityCollisionRow(int index, ZoneCellEntityRow? input, string xmlHash,
        ZoneCellEntityCollisionOutcome outcome, IEnumerable<ZoneCellEntityCollisionDependency> dependencies,
        IEnumerable<ZoneCellEntityCollisionLink> links, ZoneParticleCollisionCertificate? particle,
        IEnumerable<ZoneCellEntityCollisionDiagnostic> diagnostics)
    {
        SourceChildIndex = index; InputRow = input; CanonicalXmlSha256 = xmlHash; Outcome = outcome;
        Dependencies = Array.AsReadOnly(dependencies.Distinct().ToArray());
        AreaLinks = Array.AsReadOnly(links.ToArray()); ParticleGraph = particle;
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }
}

/// <summary>
/// Frozen classification of this exact input object and its original bytes. The scene must separately
/// own the current cell generation, ordinary class-registration profile, and complete physical inventory.
/// </summary>
public sealed class ZoneCellEntityCollisionSnapshot
{
    public ZoneManagedCellEntityCollision Owner { get; }
    public ZoneCellEntityContent Input { get; }
    public ZoneCellEntityCollisionDependency Document { get; }
    /// <summary>Every direct Objects child, including unsupported non-Entity children.</summary>
    public IReadOnlyList<ZoneCellEntityCollisionRow> Rows { get; }
    public IReadOnlyList<ZoneCellEntityCollisionDiagnostic> DocumentDiagnostics { get; }
    public IReadOnlyList<ZoneCellEntityCollisionDependency> Dependencies { get; }
    public int NoPhysicalContributionCount => Rows.Count(row => row.Outcome == ZoneCellEntityCollisionOutcome.NoPhysicalContribution);
    public int RefusedCount => Rows.Count - NoPhysicalContributionCount;

    internal ZoneCellEntityCollisionSnapshot(ZoneManagedCellEntityCollision owner, ZoneCellEntityContent input,
        ZoneCellEntityCollisionDependency document, IEnumerable<ZoneCellEntityCollisionRow> rows,
        IEnumerable<ZoneCellEntityCollisionDiagnostic> diagnostics)
    {
        Owner = owner; Input = input; Document = document; Rows = Array.AsReadOnly(rows.ToArray());
        DocumentDiagnostics = Array.AsReadOnly(diagnostics.ToArray());
        Dependencies = Array.AsReadOnly(Rows.SelectMany(row => row.Dependencies).Prepend(document).Distinct().ToArray());
    }
}

/// <summary>
/// Source-backed normal-authored-load profiles for four sound scripts, Light, FogVolume,
/// polygon AreaShape and ParticleEffect. Exact reviewed script identities are required; this is not general Lua analysis
/// or execution. The borrowed source must remain the scene's immutable resource snapshot. A replacement
/// source/registration or dynamic entity change requires a new classifier/cell certificate.
/// </summary>
public sealed class ZoneManagedCellEntityCollision : IDisposable
{
    public const int MaximumDependencyBytes = 1024 * 1024;
    public const int MaximumDocumentNodes = 1_000_000;
    public const int MaximumObjectRows = 65_536;
    public const int MaximumAreaPoints = 4096;
    public const int MaximumAreaLinks = 4096;
    private readonly object _gate = new();
    private readonly IZoneAssetSource _assets;
    private readonly ZoneParticleCollisionContent _particles;
    private readonly Dictionary<string, ClassSource> _classes = new(StringComparer.Ordinal);
    private bool _disposed, _classifying;
    public bool IsDisposed { get { lock (_gate) return _disposed; } }

    public ZoneManagedCellEntityCollision(IZoneAssetSource assets)
    {
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _particles = new(assets);
    }

    /// <summary>
    /// Hashes Input.RawDocument without reopening assetPath. The path is a provenance label, never a
    /// success key. No cell identity, expected row count, query result or caller completeness flag is trusted.
    /// I/O errors throw; missing resources and unsupported/malformed content remain explicit row results.
    /// </summary>
    public ZoneCellEntityCollisionSnapshot Classify(ZoneCellEntityContent input, string assetPath)
    {
        ArgumentNullException.ThrowIfNull(input);
        assetPath = ZoneAssetPath.Validate(assetPath);
        lock (_gate)
        {
            Check();
            if (_classifying) throw new InvalidOperationException("Cell entity classification cannot reenter an asset callback on the same owner.");
            _classifying = true;
            try { return ClassifyCore(input, assetPath); }
            finally { _classifying = false; }
        }
    }

    private ZoneCellEntityCollisionSnapshot ClassifyCore(ZoneCellEntityContent input, string path)
    {
        var document = new ZoneCellEntityCollisionDependency(ZoneCellEntityCollisionDependencyKind.CellDocument,
            path, input.RawDocument.Length, Hash(input.RawDocument));
        var diagnostics = new List<ZoneCellEntityCollisionDiagnostic>();
        try
        {
            var xml = ReadXml(input.RawDocument.ToArray(), ZoneCellEntityContent.MaximumDocumentBytes);
            Require(xml.Root is { Name: var name } && name == "Mission", "Only the reviewed literal Mission document is admitted.");
            Attributes(xml.Root!, Map(("CGFCount", ValueKind.NonNegativeInteger)), false);
            Require(xml.Root!.Elements().Count() == 1 && xml.Root.Element("Objects") is not null,
                "The Mission must contain exactly one Objects container and no unowned sibling data.");
            Attributes(xml.Root.Element("Objects")!, EmptyMap, true);
        }
        catch (ProfileFailure failure) { diagnostics.Add(new(path, failure.Message)); }
        catch (XmlException failure) { diagnostics.Add(new(path, failure.Message)); }
        var objects = input.Objects?.Children ?? [];
        if (input.Objects is null) diagnostics.Add(new(path, "The authored Objects container is absent."));
        if (objects.Count > MaximumObjectRows)
        {
            diagnostics.Add(new(path, "The cell entity replacement row-work bound was exceeded; no partial row table was certified."));
            return new(this, input, document, [], diagnostics);
        }
        var originals = input.Entities.ToDictionary(row => row.SourceChildIndex);
        var rows = new List<RowStage>(objects.Count);
        for (var index = 0; index < objects.Count; index++)
        {
            originals.TryGetValue(index, out var row);
            var stage = new RowStage(index, row, Hash(Encoding.UTF8.GetBytes(objects[index].Xml)));
            rows.Add(stage);
            try
            {
                Require(diagnostics.Count == 0, "The containing document is outside the reviewed ordinary-load profile.");
                Require(row is not null && objects[index].Name == "Entity", "Unknown Objects child cannot be omitted from entity collision accounting.");
                ValidateRow(stage);
            }
            catch (ProfileFailure failure) { stage.Refuse(failure.Outcome, failure.Message); }
        }
        var byGuid = rows.Where(row => row.Input is not null).GroupBy(row => row.Input!.EntityGuid)
            .ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var duplicate in byGuid.Values.Where(group => group.Length > 1))
            foreach (var row in duplicate) row.Refuse(ZoneCellEntityCollisionOutcome.Unsupported, "Duplicate entity GUID makes the ordinary same-cell link owner ambiguous.");
        foreach (var area in rows.Where(row => row.Outcome == ZoneCellEntityCollisionOutcome.NoPhysicalContribution && row.AreaTargets is not null))
            foreach (var guid in area.AreaTargets!)
            {
                if (!byGuid.TryGetValue(guid, out var targets) || targets.Length != 1 ||
                    targets[0].Outcome != ZoneCellEntityCollisionOutcome.NoPhysicalContribution ||
                    !SoundClasses.Contains(targets[0].Input!.ClassName))
                {
                    area.Refuse(ZoneCellEntityCollisionOutcome.Unsupported,
                        $"Area link {guid:x16} has no unique, dependency-certified sound row in this exact input.");
                    continue;
                }
                var target = targets[0];
                area.Links.Add(new(guid, target.Index, target.Input!.ClassName, target.XmlHash));
                area.Dependencies.AddRange(target.Dependencies);
            }
        Check(); // Borrowed asset callbacks may dispose this owner during classification.
        return new(this, input, document, rows.Select(row => row.Freeze()), diagnostics);
    }

    private void ValidateRow(RowStage stage)
    {
        var row = stage.Input!;
        Require(Profiles.TryGetValue(row.ClassName, out var profile), $"Entity class '{row.ClassName}' has no reviewed ordinary-load collision profile.");
        var node = ParseElement(row.Xml.Xml);
        Attributes(node, RowAttributes, false);
        foreach (var required in new[] { "Name", "EntityClass", "EntityGuid", "EntityId", "Layer", "Pos" })
            Require(node.Attribute(required) is not null, $"Entity attribute {required} is required by this profile.");
        Require(row.EntityGuid != 0, "This profile requires an unambiguous nonzero authored entity GUID.");
        Require(node.Elements().Count() == 1, "Exactly one reviewed Properties or Area child is required; other proxy, flow, parent, geometry and instance data are unowned.");
        var source = Class(profile!);
        stage.Dependencies.AddRange(source.Dependencies);
        if (source.Failure is { } error) throw new ProfileFailure(source.Outcome, error);
        if (profile!.Name == "AreaShape")
        {
            var area = node.Element("Area");
            Require(area is not null, "The scriptless AreaShape profile requires its actual polygon Area child.");
            stage.AreaTargets = ValidateArea(area!);
        }
        else
        {
            var properties = node.Element("Properties");
            Require(properties is not null, "The reviewed scripted class requires one Properties child.");
            if (profile.Name == "Light") ValidateLight(properties!);
            else if (profile.Name == "FogVolume") ValidateFog(properties!);
            else
            {
                Attributes(properties!, profile.Properties!, true);
                Require(!properties!.HasElements, "The reviewed property table has no nested resource or script data.");
            }
            if (profile.Name == "ParticleEffect")
            {
                stage.Particle = _particles.Classify(properties!.Attribute("ParticleEffect")!.Value);
                var resource = stage.Particle.Resource;
                stage.Dependencies.Add(new(ZoneCellEntityCollisionDependencyKind.ParticleLibrary,
                    resource.AssetPath, resource.ByteLength, resource.Sha256));
                if (!stage.Particle.HasNoPhysicalContribution)
                    throw new ProfileFailure(stage.Particle.Outcome switch
                    {
                        ZoneParticleCollisionOutcome.MissingData => ZoneCellEntityCollisionOutcome.MissingData,
                        ZoneParticleCollisionOutcome.InvalidData => ZoneCellEntityCollisionOutcome.InvalidData,
                        _ => ZoneCellEntityCollisionOutcome.Unsupported
                    }, "The actual particle graph is not certified: " + string.Join("; ", stage.Particle.Diagnostics.Select(item => item.SourcePath + ": " + item.Message)));
            }
        }
    }

    private ClassSource Class(Profile profile)
    {
        if (_classes.TryGetValue(profile.Name, out var cached)) return cached;
        var dependencies = new List<ZoneCellEntityCollisionDependency>();
        ClassSource result;
        try
        {
            var description = Resource(profile.ClassPath, ZoneCellEntityCollisionDependencyKind.EntityClass, dependencies);
            var root = ReadXml(description, MaximumDependencyBytes).Root!;
            Require(root.Name == "Entity", "The ordinary class description must have the literal Entity root.");
            var allowed = profile.ScriptPath is null
                ? Map(("Name", ValueKind.Text), ("Invisible", ValueKind.One))
                : Map(("Name", ValueKind.Text), ("Script", ValueKind.Text));
            Attributes(root, allowed, true);
            Require(!root.HasElements, "Class children/custom creation or external registration data are unowned.");
            Require(root.Attribute("Name")!.Value == profile.Name, "The actual class description Name does not match the requested class.");
            if (profile.ScriptPath is not null)
            {
                var script = root.Attribute("Script")!.Value.Replace('\\', '/');
                Require(string.Equals("game/" + script, profile.ScriptPath, StringComparison.OrdinalIgnoreCase),
                    "The class description selects an unreviewed script dependency.");
                _ = Resource(profile.ScriptPath, ZoneCellEntityCollisionDependencyKind.Script, dependencies);
                Require(string.Equals(dependencies[^1].Sha256, profile.ScriptHash, StringComparison.OrdinalIgnoreCase),
                    "The actual script bytes differ from the reviewed standalone original profile; includes, physical callbacks and helper changes require a new source proof.");
            }
            result = new(dependencies.ToArray(), ZoneCellEntityCollisionOutcome.NoPhysicalContribution, null);
        }
        catch (ProfileFailure failure) { result = new(dependencies.ToArray(), failure.Outcome, failure.Message); }
        catch (XmlException failure) { result = new(dependencies.ToArray(), ZoneCellEntityCollisionOutcome.InvalidData, failure.Message); }
        Check();
        _classes.Add(profile.Name, result);
        return result;
    }

    private byte[] Resource(string path, ZoneCellEntityCollisionDependencyKind kind,
        List<ZoneCellEntityCollisionDependency> dependencies)
    {
        using var stream = _assets.OpenRead(path);
        Check();
        if (stream is null)
        {
            dependencies.Add(new(kind, path, 0, ""));
            throw new ProfileFailure(ZoneCellEntityCollisionOutcome.MissingData, "The actual required resource is absent: " + path);
        }
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = stream.Read(buffer)) != 0)
        {
            if (output.Length + count > MaximumDependencyBytes)
                throw new ProfileFailure(ZoneCellEntityCollisionOutcome.InvalidData, "The class/script replacement byte bound was exceeded: " + path);
            output.Write(buffer, 0, count);
        }
        var bytes = output.ToArray();
        dependencies.Add(new(kind, path, bytes.Length, Hash(bytes)));
        Check();
        Require(bytes.Length != 0, "The required class/script resource is empty: " + path);
        return bytes;
    }

    private static void ValidateLight(XElement properties)
    {
        Attributes(properties, Map(("bActive", ValueKind.Boolean), ("Radius", ValueKind.NonNegative)), true);
        Require(properties.Elements().Count() == LightProperties.Count, "Light properties contain missing, duplicate or unreviewed tables.");
        foreach (var (name, schema) in LightProperties)
        {
            var nodes = properties.Elements(name).ToArray();
            Require(nodes.Length == 1, "Light requires one actual " + name + " property table.");
            Attributes(nodes[0], schema, true);
            Require(!nodes[0].HasElements, "Nested Light property/dependency data is unowned.");
        }
    }

    private static void ValidateFog(XElement properties)
    {
        //3203C390 ->CEntity primary320CD998+864/3200F5B0 ->320897E0
        //requests render-node type7, not neighboring type5. CFogVolume table30287198
        //has a full-null GetPhysics, ignored SetPhysics and empty physicalization slots.
        Attributes(properties, Map(("bActive", ValueKind.Boolean), ("color_Color", ValueKind.Vector),
            ("FallOffDirLati", ValueKind.Scalar), ("FallOffDirLong", ValueKind.Scalar),
            ("FallOffScale", ValueKind.Scalar), ("FallOffShift", ValueKind.Scalar),
            ("GlobalDensity", ValueKind.NonNegative), ("fHDRDynamic", ValueKind.Scalar),
            ("SoftEdgeFactor", ValueKind.Zero), ("SoftEdges", ValueKind.NonNegative),
            ("bUseGlobalFogColor", ValueKind.Boolean), ("VolumeType", ValueKind.Zero)), true);
        Require(properties.Elements().Count() == 1 && properties.Element("Size") is not null,
            "The reviewed FogVolume requires exactly one complete Size table and no other resource/physical data.");
        var dimensions = properties.Element("Size")!;
        Attributes(dimensions, Map(("x", ValueKind.Positive), ("y", ValueKind.Positive), ("z", ValueKind.Positive)), true);
        Require(!dimensions.HasElements, "Nested FogVolume Size data is unowned.");
    }

    private static ulong[] ValidateArea(XElement area)
    {
        Attributes(area, Map(("Id", ValueKind.Zero), ("Group", ValueKind.Zero), ("Proximity", ValueKind.Zero),
            ("Priority", ValueKind.Zero), ("value1", ValueKind.Zero), ("value2", ValueKind.Zero),
            ("flags", ValueKind.Zero), ("Height", ValueKind.NonNegative)), true);
        Require(area.Elements().Count() == 4 && new[] { "Points", "Entities", "TimeOfDay", "SkyBox" }
                .All(name => area.Elements(name).Count() == 1),
            "Only the polygon Points branch, Entities and empty TimeOfDay/SkyBox are admitted; gravity/Bezier/volume branches are unowned.");
        var points = area.Element("Points")!;
        Attributes(points, EmptyMap, true);
        Require(points.Elements().Count() is >= 3 and <= MaximumAreaPoints, "Area polygon point count exceeds the reviewed replacement profile.");
        foreach (var point in points.Elements())
        {
            Require(point.Name == "Point" && !point.HasElements, "Unknown polygon point data is unowned.");
            Attributes(point, Map(("Pos", ValueKind.Vector)), true);
        }
        foreach (var name in new[] { "TimeOfDay", "SkyBox" })
        {
            var environment = area.Element(name)!;
            Attributes(environment, EmptyMap, true);
            Require(!environment.HasElements, "Nonempty area environment dependencies are unowned.");
        }
        var entities = area.Element("Entities")!;
        Attributes(entities, EmptyMap, true);
        Require(entities.Elements().Count() <= MaximumAreaLinks, "Area link count exceeds the replacement work bound.");
        var links = new List<ulong>();
        foreach (var target in entities.Elements())
        {
            Require(target.Name == "Entity" && !target.HasElements, "An area link requires a plain authored Entity GUID reference.");
            Attributes(target, Map(("EntityGuid", ValueKind.Guid)), true);
            links.Add(ulong.Parse(target.Attribute("EntityGuid")!.Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
        }
        return links.ToArray();
    }

    private static XDocument ReadXml(byte[] bytes, int maximum)
    {
        using var stream = new MemoryStream(bytes, false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = maximum });
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        Require(document.Root is not null, "The XML resource has no root.");
        var count = 0;
        foreach (var node in document.DescendantNodes())
        {
            Require(++count <= MaximumDocumentNodes, "The XML replacement node-work bound was exceeded.");
            Require(node is not XProcessingInstruction && node is not XDocumentType,
                "XML processing instructions/external directives are unowned.");
            if (node is XText text) Require(string.IsNullOrWhiteSpace(text.Value), "Nonempty XML text/CDATA is outside the reviewed schema.");
            if (node is XElement element)
            {
                Require(element.Ancestors().Take(ZoneCellEntityContent.MaximumElementDepth).Count() < ZoneCellEntityContent.MaximumElementDepth,
                    "The XML replacement element-depth bound was exceeded.");
                Require(element.Name.NamespaceName.Length == 0 && !element.Attributes().Any(attribute =>
                    attribute.IsNamespaceDeclaration || attribute.Name.NamespaceName.Length != 0), "Namespaced XML is outside the reviewed literal-name profile.");
            }
        }
        return document;
    }
    private static XElement ParseElement(string xml) => ReadXml(Encoding.UTF8.GetBytes(xml), ZoneCellEntityContent.MaximumDocumentBytes).Root!;
    private static void Attributes(XElement element, IReadOnlyDictionary<string, ValueKind> schema, bool requireAll)
    {
        var attributes = element.Attributes().ToArray();
        Require(attributes.Select(attribute => attribute.Name.LocalName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == attributes.Length,
            "Ambiguous case-aliased XML attributes are unowned.");
        foreach (var attribute in attributes)
        {
            Require(attribute.Name.NamespaceName.Length == 0 && schema.TryGetValue(attribute.Name.LocalName, out _),
                "Unreviewed " + element.Name + " attribute: " + attribute.Name);
            var kind = schema[attribute.Name.LocalName];
            Require(Value(attribute.Value, kind), "Unsupported or malformed " + element.Name + "." + attribute.Name + " value.");
        }
        if (requireAll) Require(attributes.Length == schema.Count, "Missing required " + element.Name + " schema attributes.");
    }
    private static bool Value(string value, ValueKind kind)
    {
        if (kind == ValueKind.Text) return value.Length is > 0 and <= 4096;
        if (kind == ValueKind.Empty) return value.Length == 0;
        if (kind == ValueKind.Surface) return value == "Surface";
        if (kind == ValueKind.Zero) return value == "0";
        if (kind == ValueKind.One) return value == "1";
        if (kind == ValueKind.Boolean) return value is "0" or "1";
        if (kind == ValueKind.Guid) return value.Length is > 0 and <= 16 && ulong.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var guid) && guid != 0;
        if (kind is ValueKind.Integer or ValueKind.NonNegativeInteger)
            return int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer) && (kind == ValueKind.Integer || integer >= 0);
        if (kind is ValueKind.Vector or ValueKind.Scale or ValueKind.Rotation)
        {
            var components = value.Split(',');
            if (components.Length != (kind == ValueKind.Rotation ? 4 : 3)) return false;
            var nonzero = false;
            foreach (var component in components)
            {
                if (!float.TryParse(component, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !float.IsFinite(number) || kind == ValueKind.Scale && number <= 0) return false;
                nonzero |= number != 0;
            }
            return kind != ValueKind.Rotation || nonzero;
        }
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var scalar) && float.IsFinite(scalar) &&
            (kind != ValueKind.NonNegative || scalar >= 0) && (kind != ValueKind.Positive || scalar > 0);
    }
    private static void Require(bool admitted, string message)
    { if (!admitted) throw new ProfileFailure(ZoneCellEntityCollisionOutcome.Unsupported, message); }
    private void Check() => ObjectDisposedException.ThrowIf(_disposed, this);
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _classes.Clear(); _particles.Dispose();
        }
    }

    private enum ValueKind { Text, Empty, Boolean, Zero, One, Surface, Scalar, NonNegative, Positive, Integer, NonNegativeInteger, Guid, Vector, Scale, Rotation }
    private sealed class ProfileFailure(ZoneCellEntityCollisionOutcome outcome, string message) : Exception(message)
    { internal ZoneCellEntityCollisionOutcome Outcome { get; } = outcome; }
    private sealed record Profile(string Name, string ClassPath, string? ScriptPath, string? ScriptHash,
        IReadOnlyDictionary<string, ValueKind>? Properties);
    private sealed record ClassSource(ZoneCellEntityCollisionDependency[] Dependencies, ZoneCellEntityCollisionOutcome Outcome, string? Failure);
    private sealed class RowStage(int index, ZoneCellEntityRow? input, string hash)
    {
        internal int Index { get; } = index;
        internal ZoneCellEntityRow? Input { get; } = input;
        internal string XmlHash { get; } = hash;
        internal ZoneCellEntityCollisionOutcome Outcome = ZoneCellEntityCollisionOutcome.NoPhysicalContribution;
        internal List<ZoneCellEntityCollisionDependency> Dependencies { get; } = [];
        internal List<ZoneCellEntityCollisionDiagnostic> Diagnostics { get; } = [];
        internal List<ZoneCellEntityCollisionLink> Links { get; } = [];
        internal ulong[]? AreaTargets;
        internal ZoneParticleCollisionCertificate? Particle;
        internal void Refuse(ZoneCellEntityCollisionOutcome outcome, string message)
        { Outcome = outcome; Diagnostics.Add(new("Objects[" + Index + "]", message)); }
        internal ZoneCellEntityCollisionRow Freeze() => new(Index, Input, XmlHash, Outcome, Dependencies, Links, Particle, Diagnostics);
    }
    private static FrozenDictionary<string, ValueKind> Map(params (string Name, ValueKind Kind)[] values) =>
        values.ToFrozenDictionary(pair => pair.Name, pair => pair.Kind, StringComparer.Ordinal);
    private static readonly FrozenDictionary<string, ValueKind> EmptyMap = Map();
    private static readonly FrozenSet<string> SoundClasses = new[] { "AmbientVolume", "RandomSoundVolume", "ReverbVolume" }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenDictionary<string, ValueKind> RowAttributes = Map(
        ("Name", ValueKind.Text), ("EntityClass", ValueKind.Text), ("EntityGuid", ValueKind.Guid),
        ("EntityId", ValueKind.NonNegativeInteger), ("Layer", ValueKind.Text), ("Pos", ValueKind.Vector),
        ("Rotate", ValueKind.Rotation), ("Scale", ValueKind.Scale), ("MinSpec", ValueKind.Integer),
        ("OutdoorOnly", ValueKind.Boolean), ("CastShadow", ValueKind.Boolean), ("ViewDistRatio", ValueKind.NonNegative));
    private static readonly FrozenDictionary<string, IReadOnlyDictionary<string, ValueKind>> LightProperties =
        new Dictionary<string, IReadOnlyDictionary<string, ValueKind>>(StringComparer.Ordinal)
        {
            ["Color"] = Map(("clrDiffuse", ValueKind.Vector), ("fDiffuseMultiplier", ValueKind.Scalar), ("fHDRDynamic", ValueKind.Scalar), ("fSpecularMultiplier", ValueKind.Scalar)),
            ["Options"] = Map(("bAffectsThisAreaOnly", ValueKind.Boolean), ("bAmbientLight", ValueKind.Boolean), ("bCastShadow", ValueKind.Boolean),
                ("nCastShadows", ValueKind.NonNegativeInteger), ("bDeferredClipBounds", ValueKind.Zero), ("bDeferredLight", ValueKind.Boolean),
                ("bFakeLight", ValueKind.Boolean), ("bIgnoreMirrorCull", ValueKind.Boolean), ("bIrradianceVolumes", ValueKind.Boolean),
                ("fShadowBias", ValueKind.Scalar), ("fShadowSlopeBias", ValueKind.Scalar), ("fShadowUpdateMinRadius", ValueKind.Scalar), ("fShadowUpdateRatio", ValueKind.Scalar)),
            ["Projector"] = Map(("bProjectInAllDirs", ValueKind.Boolean), ("fProjectorFov", ValueKind.Scalar), ("fProjectorNearPlane", ValueKind.Scalar), ("texture_Texture", ValueKind.Empty)),
            ["Style"] = Map(("fCoronaDistIntensityFactor", ValueKind.Scalar), ("fCoronaDistSizeFactor", ValueKind.Scalar), ("fCoronaScale", ValueKind.Scalar), ("nLightStyle", ValueKind.NonNegativeInteger)),
            ["Test"] = Map(("bNegativeLight", ValueKind.Boolean))
        }.ToFrozenDictionary(StringComparer.Ordinal);
    private static readonly FrozenDictionary<string, Profile> Profiles = new[]
    {
        new Profile("AmbientVolume", "game/entities/ambientvolume.ent", "game/scripts/entities/sound/ambientvolume.lua",
            "CE93065B0370546E4083166F260CA55A48A4BB20737482DDF2C01CB90F7B0BD4",
            Map(("bEnabled", ValueKind.Boolean), ("bIgnoreCulling", ValueKind.Boolean), ("bIgnoreObstruction", ValueKind.Boolean),
                ("bLogBattleValue", ValueKind.Boolean), ("soundName", ValueKind.Text), ("OuterRadius", ValueKind.NonNegative), ("bSensitiveToBattle", ValueKind.Boolean))),
        new Profile("RandomSoundVolume", "game/entities/randomsoundvolume.ent", "game/scripts/entities/sound/randomsoundvolume.lua",
            "0A5338532FCFEB48A5BF55730A34091A3EC73D41FAE6D4EF9DBE06714ADFB613",
            Map(("DiscRadius", ValueKind.NonNegative), ("bEnabled", ValueKind.Boolean), ("bIgnoreCulling", ValueKind.Boolean), ("bIgnoreObstruction", ValueKind.Boolean),
                ("bLogBattleValue", ValueKind.Boolean), ("MaxWaitTime", ValueKind.NonNegative), ("MinWaitTime", ValueKind.NonNegative),
                ("soundName", ValueKind.Text), ("bRandomPosition", ValueKind.Boolean), ("bSensitiveToBattle", ValueKind.Boolean))),
        new Profile("ReverbVolume", "game/entities/reverbvolume.ent", "game/scripts/entities/sound/reverbvolume.lua",
            "532F7ED0232E6DA5DC135A99A04D2D9D633EBD4BFEEF9898D263DCD97EAD5A9C",
            Map(("bEnabled", ValueKind.Boolean), ("Environment", ValueKind.NonNegativeInteger), ("bFullEffectWhenInside", ValueKind.Boolean),
                ("OuterRadius", ValueKind.NonNegative), ("reverbpresetReverbPreset", ValueKind.Text))),
        new Profile("SoundEventSpot", "game/entities/soundeventspot.ent", "game/scripts/entities/sound/soundeventspot.lua",
            "7C1436A0824B4A576A8BA529C154768AA8C881B32B30AB47F7A8B5F6E0BD6F3A",
            Map(("bEnabled", ValueKind.Boolean), ("bIgnoreCulling", ValueKind.Boolean), ("bIgnoreObstruction", ValueKind.Boolean),
                ("soundName", ValueKind.Text), ("bOnce", ValueKind.Boolean), ("bPlay", ValueKind.Boolean))),
        new Profile("Light", "game/entities/light.ent", "game/scripts/entities/lights/light.lua",
            "DF237EFD736E699B24EAA40F1F0201194728B0E5C2C2E0700141DC6D0749EC45", null),
        new Profile("FogVolume", "game/entities/fogvolume.ent", "game/scripts/entities/render/fogvolume.lua",
            "B5E0BB832DF4BE185FD80B800843F7D17426149990A08B4159F627DB7E20D8DF", null),
        new Profile("AreaShape", "game/entities/areashape.ent", null, null, null),
        new Profile("ParticleEffect", "game/entities/particleeffect.ent", "game/scripts/entities/particle/particleeffect.lua",
            "9AA8598ACF5902BEA52DE8A33AEEC2521FFF02E60FF29D9D79CDD570D7FCFDC3",
            Map(("bActive", ValueKind.Boolean), ("AttachForm", ValueKind.Surface), ("AttachType", ValueKind.Empty), ("bCountPerUnit", ValueKind.Boolean),
                ("CountScale", ValueKind.NonNegative), ("ParticleEffect", ValueKind.Text), ("bPrime", ValueKind.Boolean), ("PulsePeriod", ValueKind.NonNegative),
                ("Scale", ValueKind.NonNegative), ("soclasses_SmartObjectClass", ValueKind.Empty), ("SpeedScale", ValueKind.NonNegative)))
    }.ToFrozenDictionary(profile => profile.Name, StringComparer.Ordinal);
}
