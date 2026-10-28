using System.Globalization;

namespace AAEmu.Zone.Hosting;

/// <summary>
/// Zone server process arguments. Two launch styles are accepted so the same executable runs
/// both by hand and as World's zone host:
/// <list type="bullet">
/// <item>Manual: <c>--config zone.json [--spawner-file npc_spawners.g]</c>.</item>
/// <item>World ZoneHost supervisor (AAEmu-10.x <c>ZoneHostCommandLine.Build</c>): <c>-dedicated</c>
/// followed by native-style <c>+name value</c> CVars (<c>world_ip</c>, <c>world_port</c>, <c>zone</c>,
/// <c>sv_map</c>, <c>instance</c>, <c>sv_port</c>, <c>db_location</c>, ...) and the
/// <c>AAEMU_ZONE_DLL</c>/<c>AAEMU_ZONE_SAVE_DIR</c>/<c>AAEMU_ZONE_LOG_NAME</c> environment.</item>
/// </list>
/// The base configuration is still required for inputs World does not pass (engine directory,
/// asset sources, world map, frame period). It is found from <c>--config</c>, the
/// <c>+zone_config</c> CVar, <c>AAEMU_ZONE_CONFIG</c> or <c>zone.json</c> beside the executable.
/// </summary>
public sealed class ZoneServerLaunch
{
    public const string ConfigEnvironment = "AAEMU_ZONE_CONFIG";
    public const string NativeDllEnvironment = "AAEMU_ZONE_DLL";
    public const string SaveDirectoryEnvironment = "AAEMU_ZONE_SAVE_DIR";
    public const string LogNameEnvironment = "AAEMU_ZONE_LOG_NAME";
    public const string DefaultConfigFileName = "zone.json";

    /// <summary>CVars the World supervisor passes that this server consumes.</summary>
    public static readonly IReadOnlySet<string> ConsumedCVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "world_ip", "world_port", "world_serveraddr", "world_serverport", "zone", "sv_map", "instance",
        "sv_port", "db_location", "zone_config"
    };

    public string? ConfigPath { get; private init; }
    public string? SpawnerFile { get; private init; }
    public bool Dedicated { get; private init; }
    public IReadOnlyDictionary<string, string> CVars { get; private init; } = new Dictionary<string, string>();
    /// <summary>World's <c>AAEMU_ZONE_SAVE_DIR</c>: the per-host log directory World created.</summary>
    public string? SaveDirectory { get; private init; }
    /// <summary>World's <c>AAEMU_ZONE_LOG_NAME</c>: the log name World expects for this host.</summary>
    public string? LogName { get; private init; }
    /// <summary>World's <c>AAEMU_ZONE_DLL</c>. Recorded only: this server replaces that native module.</summary>
    public string? NativeDll { get; private init; }

    public bool LaunchedByWorld => Dedicated || CVars.Count != 0;
    public string? WorldHost => CVar("world_ip") ?? CVar("world_serveraddr");
    public ushort? WorldPort => Port(CVar("world_port") ?? CVar("world_serverport"), "world_port");
    public string? ZoneName => CVar("zone") ?? CVar("sv_map");
    public uint? InstanceId => CVar("instance") is { } value ? ParseUInt32(value, "instance") : null;
    /// <summary>World's unique <c>+sv_port</c> for this host; reported in ZWJoin.</summary>
    public ushort? ServerPort => Port(CVar("sv_port"), "sv_port");

    /// <summary>
    /// The engine's sv_port default. The native zone joins with it when World passes no +sv_port
    /// (captured ZWJoin from a hand-started native zone carries port 64100).
    /// </summary>
    public const ushort DefaultServerPort = 64100;
    /// <summary>World's <c>+db_location</c>, relative to the working directory World sets.</summary>
    public string? ContentDatabasePath => CVar("db_location") is { } path ? Path.GetFullPath(path) : null;
    public IEnumerable<string> IgnoredCVars => CVars.Keys.Where(name => !ConsumedCVars.Contains(name));

    public static ZoneServerLaunch Parse(IReadOnlyList<string> args, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);
        string? config = null, spawners = null;
        var dedicated = false;
        var cvars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Count; i++)
        {
            var key = args[i];
            if (key == "-dedicated")
            {
                dedicated = true;
                continue;
            }
            if (key.Length > 1 && key[0] == '+')
            {
                if (++i == args.Count)
                    throw new ArgumentException($"Missing value for CVar {key}.");
                // Native console semantics: a repeated CVar keeps the last value.
                cvars[key[1..]] = args[i];
                continue;
            }
            if (key is not ("--config" or "--spawner-file"))
                throw new ArgumentException($"Unknown option {key}.");
            if (++i == args.Count || args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Missing value for {key}.");
            if (key == "--config")
                config = config is null ? args[i] : throw new ArgumentException("Duplicate option --config.");
            else
                spawners = spawners is null ? args[i] : throw new ArgumentException("Duplicate option --spawner-file.");
        }
        if (config is null && cvars.TryGetValue("zone_config", out var cvarConfig))
            config = cvarConfig;
        config ??= NonEmpty(environment(ConfigEnvironment));
        if (config is null)
        {
            var beside = Path.Combine(AppContext.BaseDirectory, DefaultConfigFileName);
            if (File.Exists(beside))
                config = beside;
        }
        return new ZoneServerLaunch
        {
            ConfigPath = config,
            SpawnerFile = spawners,
            Dedicated = dedicated,
            CVars = cvars,
            SaveDirectory = NonEmpty(environment(SaveDirectoryEnvironment)),
            LogName = NonEmpty(environment(LogNameEnvironment)),
            NativeDll = NonEmpty(environment(NativeDllEnvironment)),
        };
    }

    private string? CVar(string name) => CVars.TryGetValue(name, out var value) ? NonEmpty(value) : null;

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ushort? Port(string? value, string name) => value is null ? null
        : ushort.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port != 0 ? port
        : throw new ArgumentException($"+{name} must be between 1 and 65535.");

    private static uint ParseUInt32(string value, string name) =>
        uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed
            : throw new ArgumentException($"+{name} must be an unsigned 32-bit integer.");
}
