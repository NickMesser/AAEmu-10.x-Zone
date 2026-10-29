using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace AAEmu.Zone.Content;

public enum ZoneParticleCollisionOutcome { NoPhysicalContribution, MissingData, Unsupported, InvalidData }
public enum ZoneParticlePhysicsKind { None, SimpleCollision, SimplePhysics, RigidBody }
public enum ZoneParticleForceKind { None, Wind, Gravity, Target }

/// <summary>One owned source snapshot. This is content provenance, not a current-cell certificate.</summary>
public sealed record ZoneParticleCollisionResource(string AssetPath, int ByteLength, string Sha256,
    string? LibraryName, int? ParticleVersion);
public sealed record ZoneParticleCollisionDiagnostic(string SourcePath, string Message);

/// <summary>An authored emitter node, including disabled descendants; not a count of live native emitters.</summary>
public sealed class ZoneParticleCollisionEmitter
{
    public int Index { get; }
    public int? ParentIndex { get; }
    public string SourcePath { get; }
    public string AuthoredName { get; }
    public string FullName { get; }
    public bool? Enabled { get; }
    public ZoneParticlePhysicsKind? PhysicsType { get; }
    public ZoneParticleForceKind? ForceGeneration { get; }
    public string? Geometry { get; }
    public IReadOnlyDictionary<string, string> AuthoredParameters { get; }

    internal ZoneParticleCollisionEmitter(int index, int? parent, string path, string name, string fullName,
        bool? enabled, ZoneParticlePhysicsKind? physics, ZoneParticleForceKind? force, string? geometry,
        IReadOnlyDictionary<string, string> parameters)
    {
        Index = index; ParentIndex = parent; SourcePath = path; AuthoredName = name; FullName = fullName;
        Enabled = enabled; PhysicsType = physics; ForceGeneration = force; Geometry = geometry;
        AuthoredParameters = parameters.ToFrozenDictionary(StringComparer.Ordinal);
    }
}

/// <summary>
/// Copied proof for a frozen source effect graph only. The entity class/script, row, cell generation,
/// physical mask admission and lifetime must be certified separately by the scene classifier.
/// </summary>
public sealed class ZoneParticleCollisionCertificate
{
    public ZoneParticleCollisionOutcome Outcome { get; }
    public bool HasNoPhysicalContribution => Outcome == ZoneParticleCollisionOutcome.NoPhysicalContribution;
    public string RequestedEffectName { get; }
    public string? ResolvedEffectName { get; }
    public ZoneParticleCollisionResource Resource { get; }
    public IReadOnlyList<ZoneParticleCollisionEmitter> Emitters { get; }
    public IReadOnlyList<ZoneParticleCollisionDiagnostic> Diagnostics { get; }

    internal ZoneParticleCollisionCertificate(ZoneParticleCollisionOutcome outcome, string requested, string? resolved,
        ZoneParticleCollisionResource resource, IEnumerable<ZoneParticleCollisionEmitter>? emitters = null,
        IEnumerable<ZoneParticleCollisionDiagnostic>? diagnostics = null)
    {
        Outcome = outcome; RequestedEffectName = requested; ResolvedEffectName = resolved; Resource = resource;
        Emitters = Array.AsReadOnly((emitters ?? []).ToArray());
        Diagnostics = Array.AsReadOnly((diagnostics ?? []).ToArray());
    }
}

/// <summary>
/// Bounded version20, no-physics/no-force particle source reader. Borrows its asset source and freezes
/// each opened library until disposal. No renderer, script execution, geometry substitute or native call.
/// </summary>
public sealed class ZoneParticleCollisionContent : IDisposable
{
    // Replacement resource/work bounds, not recovered native limits.
    public const int MaximumLibraryBytes = 16 * 1024 * 1024;
    public const int MaximumXmlNodes = 1_000_000;
    public const int MaximumXmlDepth = 128;
    public const int MaximumEmitterDepth = 48;
    public const int MaximumEmitterCount = 4096;
    public const int MaximumParameterCount = 128;
    public const int MaximumNameLength = 1024;
    public const int MaximumLibraries = 128;
    public const int MaximumCertificates = 65_536;

    private static readonly FrozenSet<string> RootAttributes = new[] { "Name", "SandboxVersion", "ParticleVersion" }
        .ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> EmitterAttributes = new[] { "Name", "Id" }.ToFrozenSet(StringComparer.Ordinal);
    // These actual version20 XML parameters cannot select physics, geometry, force generation or
    // external emitter inheritance. Unowned parameters are not silently treated as cosmetic.
    private static readonly FrozenSet<string> NonPhysicalParameters = new[]
    {
        "Acceleration", "AirResistance", "Alpha", "AttachType", "BindToEmitter", "BlendType", "CameraMaxDistance",
        "Color", "ConfigMax", "Continuous", "Count", "DiffuseBacklighting", "DiffuseLighting", "DrawLast",
        "DynamicFriction", "EmissiveLighting", "EmitAngle", "Enabled", "FillRateCost", "FocusAngle", "FocusGravityDir",
        "GravityScale", "LightHDRDynamic", "LightSourceIntensity", "LightSourceRadius", "Material", "NoOffset",
        "ParticleLifeTime", "PosRandomOffset", "PositionOffset", "PulsePeriod", "RandomAngles", "RandomOffset",
        "RandomRotationRate", "RotateInitAngle", "RotateLength", "RotatePosition", "RotateSpeed", "RotationRate",
        "Size", "SizeX", "SizeY", "SoftParticle", "SortOldestFirst", "Sound", "Speed", "Stretch", "Texture",
        "TextureTiling", "Turbulence3DSpeed", "TurbulenceSize", "TurbulenceSpeed"
    }.ToFrozenSet(StringComparer.Ordinal);
    private readonly IZoneAssetSource _assets;
    private readonly object _gate = new();
    private readonly Dictionary<string, Library> _libraries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ZoneParticleCollisionCertificate> _certificates = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loading = new(StringComparer.Ordinal);
    private bool _disposed;
    public bool IsDisposed { get { lock (_gate) return _disposed; } }
    public int LoadedLibraryCount { get { lock (_gate) return _libraries.Count; } }

    public ZoneParticleCollisionContent(IZoneAssetSource assets) => _assets = assets ?? throw new ArgumentNullException(nameof(assets));

    /// <summary>
    ///30098EB0/30097670 cold lookup: the first dot selects Libs/Particles/&lt;library&gt;.xml.
    ///The remaining dotted name is matched as one complete registered top-level name, case-insensitively.
    ///There is no guessed filename, longest-library fallback or unowned preloaded registry profile.
    /// </summary>
    public ZoneParticleCollisionCertificate Classify(string fullEffectName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullEffectName);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_certificates.TryGetValue(fullEffectName, out var existing)) return existing;
            var empty = new ZoneParticleCollisionResource("", 0, "", null, null);
            if (!TryName(fullEffectName, out var libraryName))
                return Failure(ZoneParticleCollisionOutcome.Unsupported, fullEffectName, empty,
                    "effect-name", "The cold particle profile requires a bounded ASCII library.effect name without path, wildcard or external directives.");
            var path = "game/libs/particles/" + libraryName.ToLowerInvariant() + ".xml";
            if (_certificates.Count >= MaximumCertificates)
                return Failure(ZoneParticleCollisionOutcome.Unsupported, fullEffectName, empty with { AssetPath = path },
                    path, "The particle certificate replacement work bound was exceeded.");
            if (!_libraries.TryGetValue(path, out var library))
            {
                if (_libraries.Count + _loading.Count >= MaximumLibraries)
                    return Failure(ZoneParticleCollisionOutcome.Unsupported, fullEffectName, empty with { AssetPath = path },
                        path, "The particle library replacement resource bound was exceeded.");
                if (!_loading.Add(path)) throw new InvalidOperationException("Particle source loading cannot reenter the same library.");
                try
                {
                    library = LoadLibrary(path);
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    _libraries.Add(path, library);
                }
                finally { _loading.Remove(path); }
            }
            //Opening a borrowed source can reenter this owner for other already
            //loaded libraries. Its completed classifications still consume this
            //owner's shared result bound before the outer call may publish.
            if (_certificates.Count >= MaximumCertificates)
                return Failure(ZoneParticleCollisionOutcome.Unsupported, fullEffectName, library.Resource,
                    path, "The particle certificate replacement work bound was exceeded.");
            var certificate = ClassifyLibrary(fullEffectName, libraryName, library);
            _certificates.Add(fullEffectName, certificate);
            return certificate;
        }
    }

    private static ZoneParticleCollisionCertificate ClassifyLibrary(string requested, string libraryName, Library library)
    {
        if (library.Failure is { } failure)
            return Failure(library.Outcome, requested, library.Resource, failure.SourcePath, failure.Message);
        if (!string.Equals(libraryName, library.Resource.LibraryName, StringComparison.OrdinalIgnoreCase))
            return Failure(ZoneParticleCollisionOutcome.Unsupported, requested, library.Resource, library.Resource.AssetPath,
                "The authored library header does not match the native cold library prefix; a different/preloaded registry contract is not admitted.");
        if (!library.Effects!.TryGetValue(requested, out var root))
            return Failure(ZoneParticleCollisionOutcome.MissingData, requested, library.Resource, library.Resource.AssetPath,
                "The full registered top-level effect name is absent. Dotted names are not guessed as child paths or other library filenames.");
        var emitters = new List<ZoneParticleCollisionEmitter>();
        var diagnostics = new List<ZoneParticleCollisionDiagnostic>();
        var resolved = library.Resource.LibraryName + "." + root.Attribute("Name")!.Value;
        Inspect(root, null, resolved, "Particles[" + root.Attribute("Name")!.Value + "]", 0);
        return new(diagnostics.Count == 0 ? ZoneParticleCollisionOutcome.NoPhysicalContribution : ZoneParticleCollisionOutcome.Unsupported,
            requested, resolved, library.Resource, emitters, diagnostics);

        void Inspect(XElement element, int? parent, string fullName, string sourcePath, int depth)
        {
            if (depth >= MaximumEmitterDepth || emitters.Count >= MaximumEmitterCount)
            { Refuse(sourcePath, "The particle emitter replacement depth/count bound was exceeded."); return; }
            if (!Plain(element, "Particles") || !Attributes(element, EmitterAttributes))
            { Refuse(sourcePath, "Unknown emitter structure/attribute requires its original inheritance or external-resource semantics."); return; }
            var name = element.Attribute("Name")?.Value;
            if (!SafeEffectPart(name))
            { Refuse(sourcePath, "An emitter has no supported bounded authored Name."); return; }
            var parameters = element.Elements("Params").ToArray();
            var children = element.Elements("Childs").ToArray();
            if (parameters.Length != 1 || children.Length > 1 || element.Elements().Any(child => child.Name != "Params" && child.Name != "Childs"))
            { Refuse(sourcePath, "An emitter requires exactly one Params and at most one explicit Childs container; external/inherited structures are unowned."); return; }
            var values = ReadParameters(parameters[0], sourcePath);
            var physics = Physics(values, sourcePath);
            var force = Force(values, sourcePath);
            var geometry = Alias(values, "Geometry", "sGeometry", sourcePath);
            if (geometry is not null)
                Refuse(sourcePath, "An explicit Geometry/sGeometry parameter requires the original resource/physical descendant owner.");
            bool? enabled = true;
            if (values.TryGetValue("Enabled", out var enabledText))
                enabled = enabledText switch { "true" => true, "false" => false, _ => null };
            if (enabled is null) Refuse(sourcePath, "The Enabled value requires its unowned native boolean conversion.");
            var index = emitters.Count;
            emitters.Add(new(index, parent, sourcePath, name!, fullName, enabled, physics, force, geometry, values));
            if (children.Length == 0) return;
            var container = children[0];
            if (!Plain(container, "Childs") || container.HasAttributes)
            { Refuse(sourcePath + "/Childs", "Unknown child-container attributes require original inheritance/external semantics."); return; }
            var childNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in container.Elements())
            {
                if (emitters.Count >= MaximumEmitterCount)
                { Refuse(sourcePath, "The particle emitter replacement count bound was reached before all descendants were classified."); break; }
                var childName = child.Attribute("Name")?.Value;
                if (!Plain(child, "Particles") || !SafeEffectPart(childName))
                { Refuse(sourcePath + "/Childs", "Unknown or unnamed child emitter is not admitted."); continue; }
                //30078E80 uses the last component when the explicit parent pointer exists.
                var authoredChildName = childName!;
                var shortName = authoredChildName[(authoredChildName.LastIndexOf('.') + 1)..];
                if (!childNames.Add(shortName))
                { Refuse(sourcePath + "/Childs", "Duplicate child names require native reuse/automatic-renaming semantics."); continue; }
                Inspect(child, index, fullName + "." + shortName, sourcePath + "/Childs/Particles[" + childName + "]", depth + 1);
            }
        }

        Dictionary<string, string> ReadParameters(XElement element, string sourcePath)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!Plain(element, "Params") || element.HasElements || element.Attributes().Count() > MaximumParameterCount)
            { Refuse(sourcePath, "Params contains an unsupported structure or exceeds its replacement attribute bound."); return values; }
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var attribute in element.Attributes())
            {
                var name = attribute.Name.LocalName;
                if (attribute.Name.Namespace != XNamespace.None || !names.Add(name))
                { Refuse(sourcePath, "Ambiguous/namespaced parameter attributes are not admitted."); continue; }
                values.Add(name, attribute.Value);
                if (!NonPhysicalParameters.Contains(name) && name is not ("PhysicsType" or "ePhysicsType" or
                    "ForceGeneration" or "eForceGeneration" or "Geometry" or "sGeometry"))
                    Refuse(sourcePath, "Unowned particle parameter '" + name + "' cannot be assumed nonphysical.");
            }
            return values;
        }

        ZoneParticlePhysicsKind? Physics(Dictionary<string, string> values, string sourcePath)
        {
            var value = Alias(values, "PhysicsType", "ePhysicsType", sourcePath);
            if (value is null) return ZoneParticlePhysicsKind.None; //30087450 ->30086910 resets params976 before each version20 load.
            ZoneParticlePhysicsKind? kind = value switch
            { "None" => ZoneParticlePhysicsKind.None, "SimpleCollision" => ZoneParticlePhysicsKind.SimpleCollision,
                "SimplePhysics" => ZoneParticlePhysicsKind.SimplePhysics, "RigidBody" => ZoneParticlePhysicsKind.RigidBody, _ => null };
            Refuse(sourcePath, kind is null ? "Unknown PhysicsType/ePhysicsType conversion is not admitted: '" + value + "'." :
                "Explicit PhysicsType/ePhysicsType '" + value + "' requires its original physical parameter semantics; only reset-default absence is certified.");
            return kind;
        }
        ZoneParticleForceKind? Force(Dictionary<string, string> values, string sourcePath)
        {
            var value = Alias(values, "ForceGeneration", "eForceGeneration", sourcePath);
            if (value is null) return ZoneParticleForceKind.None;
            ZoneParticleForceKind? kind = value switch
            { "None" => ZoneParticleForceKind.None, "Wind" => ZoneParticleForceKind.Wind,
                "Gravity" => ZoneParticleForceKind.Gravity, "Target" => ZoneParticleForceKind.Target, _ => null };
            Refuse(sourcePath, kind is null ? "Unknown ForceGeneration/eForceGeneration conversion is not admitted: '" + value + "'." :
                "Explicit ForceGeneration/eForceGeneration '" + value + "' requires its original force consumer; only reset-default absence is certified.");
            return kind;
        }
        string? Alias(Dictionary<string, string> values, string xmlName, string nativeName, string sourcePath)
        {
            var first = values.TryGetValue(xmlName, out var xml);
            var second = values.TryGetValue(nativeName, out var native);
            if (first && second) Refuse(sourcePath, "Multiple authored aliases for '" + xmlName + "' require native precedence semantics.");
            return first ? xml : second ? native : null;
        }
        void Refuse(string sourcePath, string message) => diagnostics.Add(new(sourcePath, message));
    }

    private Library LoadLibrary(string path)
    {
        using var input = _assets.OpenRead(path);
        if (input is null) return new(new(path, 0, "", null, null), ZoneParticleCollisionOutcome.MissingData,
            new(path, "The native cold particle library asset is absent."), null);
        byte[] bytes;
        try { bytes = ReadBounded(input); }
        catch (InvalidDataException error)
        { return new(new(path, 0, "", null, null), ZoneParticleCollisionOutcome.InvalidData, new(path, error.Message), null); }
        var resource = new ZoneParticleCollisionResource(path, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), null, null);
        try
        {
            var settings = new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumLibraryBytes };
            using (var checkInput = new MemoryStream(bytes, false))
            using (var check = XmlReader.Create(checkInput, settings))
            {
                var nodes = 0;
                while (check.Read())
                {
                    if (check.NodeType == XmlNodeType.ProcessingInstruction)
                        throw new InvalidDataException("Particle XML contains an unowned processing instruction.");
                    if (++nodes > MaximumXmlNodes || check.Depth > MaximumXmlDepth)
                        throw new InvalidDataException("Particle XML exceeds the replacement node/depth bound.");
                }
            }
            using var documentInput = new MemoryStream(bytes, false);
            using var reader = XmlReader.Create(documentInput, settings);
            var document = XDocument.Load(reader, LoadOptions.None);
            var root = document.Root ?? throw new InvalidDataException("Particle XML has no document element.");
            var versionText = root.Attribute("ParticleVersion")?.Value;
            int? version = int.TryParse(versionText, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
            resource = resource with { LibraryName = root.Attribute("Name")?.Value, ParticleVersion = version };
            if (!Plain(root, "ParticleLibrary") || !Attributes(root, RootAttributes) || version != 20 ||
                string.IsNullOrEmpty(resource.LibraryName) || resource.LibraryName.Length > MaximumNameLength)
                return Unsupported("Only the original bounded ParticleLibrary/version20 reset/default source profile is admitted.");
            var effects = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in root.Elements())
            {
                var name = element.Attribute("Name")?.Value;
                //30097670 registers each complete authored Name. Unrelated actual library entries
                //include repeated dots and punctuation; these are dictionary keys, not asset paths.
                //The selected graph still passes the stricter supported-profile name check in Inspect.
                if (!Plain(element, "Particles") || !IndexableRegisteredName(name))
                    return Unsupported("The library contains an unowned top-level directive or unnamed effect.");
                if (!effects.TryAdd(resource.LibraryName + "." + name, element))
                    return Unsupported("Ambiguous duplicate registered effect names require native existing-registry publication/reuse semantics.");
            }
            return new(resource, ZoneParticleCollisionOutcome.NoPhysicalContribution, null, effects);
        }
        catch (Exception error) when (error is XmlException or InvalidDataException)
        { return new(resource, ZoneParticleCollisionOutcome.InvalidData, new(path, error.Message), null); }

        Library Unsupported(string message) => new(resource, ZoneParticleCollisionOutcome.Unsupported, new(path, message), null);
    }

    private static byte[] ReadBounded(Stream input)
    {
        if (input.CanSeek && input.Length > MaximumLibraryBytes) throw new InvalidDataException("Particle library exceeds the replacement byte bound.");
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) return output.ToArray();
            if (read > MaximumLibraryBytes - output.Length) throw new InvalidDataException("Particle library exceeds the replacement byte bound.");
            output.Write(buffer, 0, read);
        }
    }
    private static bool TryName(string name, out string library)
    {
        library = "";
        var dot = name.IndexOf('.');
        if (name.Length > MaximumNameLength || dot < 1 || dot == name.Length - 1 || name.Split('.').Any(part => part.Length == 0)) return false;
        var prefix = name[..dot];
        if (prefix.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')) || !SafeEffectPart(name[(dot + 1)..])) return false;
        library = prefix; return true;
    }
    private static bool SafeEffectPart(string? name) => !string.IsNullOrEmpty(name) && name.Length <= MaximumNameLength &&
        name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or ' ') &&
        name[0] != '.' && name[^1] != '.' && !name.Contains("..", StringComparison.Ordinal);
    private static bool IndexableRegisteredName(string? name) => !string.IsNullOrEmpty(name) && name.Length <= MaximumNameLength &&
        name.All(character => character is >= ' ' and <= '~');
    private static bool Plain(XElement element, string tag) => element.Name.Namespace == XNamespace.None && element.Name.LocalName == tag &&
        element.Nodes().All(node => node is XElement or XComment || node is XText text && string.IsNullOrWhiteSpace(text.Value));
    private static bool Attributes(XElement element, FrozenSet<string> allowed) => element.Attributes().All(attribute =>
        attribute.Name.Namespace == XNamespace.None && allowed.Contains(attribute.Name.LocalName));
    private static ZoneParticleCollisionCertificate Failure(ZoneParticleCollisionOutcome outcome, string requested,
        ZoneParticleCollisionResource resource, string path, string message) => new(outcome, requested, null, resource,
            diagnostics: [new(path, message)]);
    public void Dispose()
    {
        lock (_gate)
        { if (_disposed) return; _disposed = true; _certificates.Clear(); _libraries.Clear(); }
    }
    private sealed record Library(ZoneParticleCollisionResource Resource, ZoneParticleCollisionOutcome Outcome,
        ZoneParticleCollisionDiagnostic? Failure, Dictionary<string, XElement>? Effects);
}
