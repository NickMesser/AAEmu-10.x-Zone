using System.Text.Json;
using System.Text.Json.Serialization;

using AAEmu.Zone.Content;
using AAEmu.Zone.Scene;

namespace AAEmu.Zone.Hosting;

public sealed record ZoneConfiguredAsset(string Kind, string Path);

/// <summary>
/// Explicit replacement-host inputs. Relative filesystem paths are anchored to the configuration
/// file, independently of the launcher's working directory. Reading performs no engine/network
/// startup and does not create directories. Source order is the asset search priority.
/// </summary>
public sealed class ZoneRuntimeConfiguration
{
    public string ContentDatabasePath { get; private set; }
    /// <summary>Required only by the native comparison host; unused by the managed host.</summary>
    public string? EngineDirectory { get; }
    public string LogDirectory { get; private set; }
    public string ZoneName { get; private set; }
    public int ZoneKey { get; private set; }
    public string WorldName { get; }
    public string WorldMapAsset { get; }
    public string WorldHost { get; private set; }
    public ushort WorldPort { get; private set; }
    public string Locale { get; }
    public IReadOnlyList<ZoneConfiguredAsset> AssetSources { get; }
    public int? TerrainTexturePoolSize { get; }
    public TimeSpan? FramePeriod { get; }
    /// <summary>
    /// Scene backend selection. engineBackend=managed defaults to managed terrain and AI,
    /// and requires neither engineDirectory nor a Lua DLL. Legacy owned physics remains hybrid.
    /// </summary>
    public ZoneSceneBackendSelection Backends { get; }
    /// <summary>
    /// Optional explicit script root, or null to keep the historical rule (the single directory
    /// asset source). The retail layout puts the script tree one level below the asset root
    /// (<c>C:\AA\game\scripts\ai\lookup.xml</c>) while terrain and object assets are addressed from
    /// the asset root, so the two roots are named independently instead of moving any asset.
    /// </summary>
    public string? ScriptRoot { get; }

    private ZoneRuntimeConfiguration(Document input, string directory)
    {
        ContentDatabasePath = Resolve(input.ContentDatabasePath, directory, "contentDatabasePath");
        if (Path.GetFileName(ContentDatabasePath).Equals("compact.sqlite", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Use the configured decrypted game.compact export, not compact.sqlite.");
        Backends = ParseBackends(input.EngineBackend, input.PhysicsBackend, input.AiBackend, input.AiDependencyGated);
        EngineDirectory = Backends.Engine == ZoneEngineBackendKind.Native
            ? Resolve(input.EngineDirectory, directory, "engineDirectory")
            : string.IsNullOrWhiteSpace(input.EngineDirectory) ? null : Resolve(input.EngineDirectory, directory, "engineDirectory");
        LogDirectory = Resolve(input.LogDirectory, directory, "logDirectory");
        ZoneName = Required(input.ZoneName, "zoneName");
        if (input.ZoneKey <= 0) throw new InvalidDataException("zoneKey must be positive.");
        ZoneKey = input.ZoneKey;
        WorldName = Required(input.WorldName, "worldName");
        WorldMapAsset = ZoneAssetPath.Validate(Required(input.WorldMapAsset, "worldMapAsset"));
        WorldHost = Required(input.WorldHost, "worldHost");
        if (Uri.CheckHostName(WorldHost) == UriHostNameType.Unknown)
            throw new InvalidDataException("worldHost must be a hostname or IP address without a scheme or port.");
        if (input.WorldPort is < 1 or > 65535) throw new InvalidDataException("worldPort must be between 1 and 65535.");
        WorldPort = (ushort)input.WorldPort;
        Locale = Required(input.Locale, "locale");
        if (input.AssetSources is null || input.AssetSources.Length == 0)
            throw new InvalidDataException("assetSources must contain at least one directory or archive source.");
        AssetSources = Array.AsReadOnly(input.AssetSources.Select(source =>
        {
            if (source is null || source.Kind is not ("directory" or "archive"))
                throw new InvalidDataException("Asset source kind must be directory or archive.");
            return new ZoneConfiguredAsset(source.Kind, Resolve(source.Path, directory, "assetSources.path"));
        }).ToArray());
        if (input.TerrainTexturePoolSize is < 0 or > int.MaxValue / sizeof(int))
            throw new InvalidDataException("terrainTexturePoolSize exceeds supported handle storage.");
        TerrainTexturePoolSize = input.TerrainTexturePoolSize;
        if (input.FramePeriodMilliseconds is { } milliseconds)
        {
            // PeriodicTimer accepts at most uint.MaxValue-1 milliseconds. This is replacement
            // scheduling configuration, not a recovered native frame rate.
            if (!double.IsFinite(milliseconds) || milliseconds < 1 || milliseconds > uint.MaxValue - 1d)
                throw new InvalidDataException("framePeriodMilliseconds must be between 1 and 4294967294.");
            FramePeriod = TimeSpan.FromMilliseconds(milliseconds);
        }
        ScriptRoot = string.IsNullOrWhiteSpace(input.ScriptRoot) ? null : Resolve(input.ScriptRoot, directory, "scriptRoot");
        SpawnerFile = string.IsNullOrWhiteSpace(input.SpawnerFile) ? null : Resolve(input.SpawnerFile, directory, "spawnerFile");
        LuaLibraryPath = string.IsNullOrWhiteSpace(input.LuaLibraryPath) ? null : Resolve(input.LuaLibraryPath, directory, "luaLibraryPath");
    }

    /// <summary>Backend strings → selection record, each refusal named at the config boundary (before any engine/network work).</summary>
    private static ZoneSceneBackendSelection ParseBackends(string? engine, string? physics, string? ai, bool? dependencyGated)
    {
        var selection = engine switch
        {
            null or "native" => ZoneSceneBackendSelection.Default,
            "managed" => ZoneSceneBackendSelection.NativeFree,
            _ => throw new InvalidDataException($"Unknown engineBackend '{engine}'; expected native or managed."),
        };
        if (physics is not null)
            selection = selection with
            {
                Physics = physics switch
                {
                    "native-terrain" or "native" => ZonePhysicsBackendKind.NativeTerrain,
                    // Agent 8: the owned implementation exists (ZoneOwnedPhysicsBackend). It becomes the
                    // movement authority; the ground still comes from the native terrain raycast.
                    "managed" or "owned" => ZonePhysicsBackendKind.Managed,
                    "managed-terrain" => ZonePhysicsBackendKind.ManagedTerrain,
                    var other => throw new InvalidDataException(
                        $"Unknown physicsBackend '{other}'; expected native-terrain, owned or managed-terrain.")
                }
            };
        if (ai is not null)
            selection = selection with
            {
                Ai = ai switch
                {
                    "native" => ZoneAiBackendKind.Native,
                    "managed" => ZoneAiBackendKind.Managed,
                    var other => throw new InvalidDataException(
                        $"Unknown aiBackend '{other}'; expected native or managed.")
                }
            };
        if (dependencyGated == false)
            throw new InvalidDataException(
                "aiDependencyGated=false is not supported: the native AI provider is dependency-gated by design " +
                "(ZoneNativeAiProvider.cs:44-47); omit the key or set it true.");
        if (selection.Engine == ZoneEngineBackendKind.Managed &&
            (selection.Physics != ZonePhysicsBackendKind.ManagedTerrain || selection.Ai != ZoneAiBackendKind.Managed))
            throw new InvalidDataException("engineBackend=managed requires physicsBackend=managed-terrain and aiBackend=managed; omit those keys to use these defaults.");
        if (selection.Engine == ZoneEngineBackendKind.Native &&
            (selection.Physics == ZonePhysicsBackendKind.ManagedTerrain || selection.Ai == ZoneAiBackendKind.Managed))
            throw new InvalidDataException("Managed terrain and AI require engineBackend=managed; the native comparison host retains its own providers.");
        return selection;
    }

    public static ZoneRuntimeConfiguration LoadFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return Read(File.ReadAllText(fullPath), Path.GetDirectoryName(fullPath)!);
    }

    public static ZoneRuntimeConfiguration Read(string json, string baseDirectory)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        RejectDuplicates(document.RootElement);
        var input = document.RootElement.Deserialize<Document>(new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        }) ?? throw new InvalidDataException("Runtime configuration must be a JSON object.");
        return new(input, Path.GetFullPath(baseDirectory));
    }

    /// <summary>Resolve content identity before opening assets, loading native modules or connecting.</summary>
    public ZoneContent ResolveZone(ZoneContentDatabase content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var zone = content.ResolveZone(ZoneName);
        if (zone.Key != ZoneKey)
            throw new InvalidDataException($"Configured Zone '{ZoneName}' resolves to key {zone.Key}, not {ZoneKey}.");
        return zone;
    }

    public ZoneConfiguredAssets OpenAssets() => new(this);

    /// <summary>
    /// Optional explicit <c>npc_spawners.g</c> (camelCase <c>spawnerFile</c>). Absent = the zone's
    /// own file from the configured assets (ZoneServerStartup.NpcSpawnersAsset).
    /// </summary>
    public string? SpawnerFile { get; }

    /// <summary>
    /// The 64-bit Lua 5.1 LNUM double+int64 library (camelCase <c>luaLibraryPath</c>), relative to
    /// the configuration file. AAEMU_ZONE_LUA_LIBRARY overrides it (ZoneSceneHost.ResolveLuaLibrary).
    /// </summary>
    public string? LuaLibraryPath { get; }

    /// <summary>
    /// Overlay World's launch values (ZoneServerLaunch) on this base configuration. The zone key
    /// is supplied by the caller from the content database, never taken from the launcher.
    /// Null arguments keep the configured value.
    /// </summary>
    public ZoneRuntimeConfiguration WithLaunch(string? worldHost, ushort? worldPort, string zoneName, int zoneKey,
        string? contentDatabasePath, string? logDirectory)
    {
        if (worldHost is not null && Uri.CheckHostName(worldHost) == UriHostNameType.Unknown)
            throw new ArgumentException("+world_ip must be a hostname or IP address without a scheme or port.");
        if (zoneKey <= 0) throw new ArgumentOutOfRangeException(nameof(zoneKey), "Zone key must be positive.");
        var copy = (ZoneRuntimeConfiguration)MemberwiseClone();
        copy.WorldHost = worldHost ?? WorldHost;
        copy.WorldPort = worldPort ?? WorldPort;
        copy.ZoneName = Required(zoneName, "zone");
        copy.ZoneKey = zoneKey;
        copy.ContentDatabasePath = contentDatabasePath is null ? ContentDatabasePath : Path.GetFullPath(contentDatabasePath);
        if (Path.GetFileName(copy.ContentDatabasePath).Equals("compact.sqlite", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Use the configured decrypted game.compact export, not compact.sqlite.");
        copy.LogDirectory = logDirectory is null ? LogDirectory : Path.GetFullPath(logDirectory);
        return copy;
    }

    private static string Required(string? value, string name) =>
        !string.IsNullOrWhiteSpace(value) && !value.Contains('\0') ? value :
            throw new InvalidDataException($"Runtime configuration requires a nonempty {name}.");

    private static string Resolve(string? path, string directory, string name)
    {
        var value = Required(path, name);
        if (Path.IsPathRooted(value) && !Path.IsPathFullyQualified(value))
            throw new InvalidDataException($"{name} must be fully qualified or relative to the configuration directory.");
        return Path.GetFullPath(value, directory);
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException($"Duplicate configuration property {property.Name}.");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }

    private sealed class Document
    {
        public required string ContentDatabasePath { get; init; }
        public string? EngineDirectory { get; init; }
        public required string LogDirectory { get; init; }
        public required string ZoneName { get; init; }
        public required int ZoneKey { get; init; }
        public required string WorldName { get; init; }
        public required string WorldMapAsset { get; init; }
        public required string WorldHost { get; init; }
        public required int WorldPort { get; init; }
        public required string Locale { get; init; }
        public required AssetDocument?[] AssetSources { get; init; }
        public int? TerrainTexturePoolSize { get; init; }
        public double? FramePeriodMilliseconds { get; init; }
        public string? EngineBackend { get; init; }
        public string? PhysicsBackend { get; init; }
        public string? AiBackend { get; init; }
        public bool? AiDependencyGated { get; init; }
        /// <summary>
        /// Optional script root (camelCase <c>scriptRoot</c>). Absent = the single directory asset
        /// source, which is the historical behaviour. It exists because the retail layout keeps the
        /// script tree one level below the asset root (<c>C:\AA\game\scripts\ai\lookup.xml</c>),
        /// while terrain/object assets are addressed from the asset root itself; naming the script
        /// root keeps both resolvable without moving any asset.
        /// </summary>
        public string? ScriptRoot { get; init; }
        public string? SpawnerFile { get; init; }
        public string? LuaLibraryPath { get; init; }
    }

    private sealed class AssetDocument
    {
        public required string Kind { get; init; }
        public required string Path { get; init; }
    }
}
