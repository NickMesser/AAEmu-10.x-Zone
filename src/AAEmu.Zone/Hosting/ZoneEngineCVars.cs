using System.Globalization;

namespace AAEmu.Zone.Hosting;

/// <summary>
/// Engine CVars the zone honours, read the way the native dedicated server gets them: the
/// <c>game/config/dedicated.cfg</c> file first, then World's <c>+name value</c> command-line CVars,
/// which override the file. Lines are <c>name = value</c> (value optionally quoted); a leading
/// <c>--</c> or <c>#</c>/<c>;</c>/<c>//</c> comments the line out. Unknown names are kept and ignored.
/// </summary>
public sealed class ZoneEngineCVars
{
    /// <summary>Asset path of the native dedicated server's console config.</summary>
    public const string DedicatedConfigAsset = "game/config/dedicated.cfg";

    /// <summary>
    /// The engine's per-frame time-of-day update (3D engine, outside x2game). The engine default is on; the
    /// shipped dedicated.cfg sets it to 0, which freezes the hour until World sets it.
    /// </summary>
    public const string TimeOfDayEngineUpdate = "e_time_of_day_engine_update";

    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public static ZoneEngineCVars Empty => new();

    /// <summary>Parses cfg text, then applies <paramref name="overrides"/> (command-line CVars) on top.</summary>
    public static ZoneEngineCVars Parse(string? configText, IReadOnlyDictionary<string, string>? overrides = null)
    {
        var cvars = new ZoneEngineCVars();
        if (configText is not null)
            foreach (var raw in configText.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] is '#' or ';' || line.StartsWith("//", StringComparison.Ordinal)
                    || line.StartsWith("--", StringComparison.Ordinal))
                    continue;
                var equals = line.IndexOf('=');
                if (equals <= 0) continue;
                cvars._values[line[..equals].Trim()] = line[(equals + 1)..].Trim().Trim('"');
            }
        if (overrides is not null)
            foreach (var (name, value) in overrides)
                cvars._values[name] = value;
        return cvars;
    }

    /// <summary>Reads dedicated.cfg through the configured assets (absent file = engine defaults).</summary>
    public static ZoneEngineCVars Load(ZoneConfiguredAssets assets, IReadOnlyDictionary<string, string>? overrides)
    {
        ArgumentNullException.ThrowIfNull(assets);
        using var stream = assets.OpenRead(DedicatedConfigAsset);
        string? text = null;
        if (stream is not null)
        {
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        return Parse(text, overrides);
    }

    public bool GetBool(string name, bool engineDefault) =>
        _values.TryGetValue(name, out var value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number != 0
            : engineDefault;
}
