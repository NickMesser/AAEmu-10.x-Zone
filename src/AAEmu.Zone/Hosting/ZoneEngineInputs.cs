using AAEmu.Zone.Content;
using AAEmu.Zone.Native.Engine;
using AAEmu.Zone.Scene;

namespace AAEmu.Zone.Hosting;

/// <summary>
/// Shared composed-engine inputs. The server and every diagnostic check must create the engine
/// the same way, so the options, the auxiliary content and the reported input summary live here
/// once. Callers still create the engine through the single
/// ZoneNativeEngine.CreateWithFramework composition entry point.
/// </summary>
public static class ZoneEngineInputs
{
    public static (ZoneNativeEngineOptions Options, ZoneModelAuxiliaryContent Auxiliary) Create(
        ZoneRuntimeConfiguration? configuration, ZoneWorldMapLayout? layout, ZoneContentDatabase content,
        ZoneModelContent models, string logName)
    {
        if (configuration is null)
            throw new ArgumentException("Composed framework startup requires a configuration so the engine directory, asset sources and log directory are explicit.");
        if (layout is null)
            throw new ArgumentException("Composed framework startup requires a configuration so the configured World map can be read from its asset source.");
        if (configuration.Backends.Engine != ZoneEngineBackendKind.Native || configuration.EngineDirectory is null)
            throw new ArgumentException("Native framework inputs require engineBackend=native and engineDirectory.");
        var workingDirectory = SingleAssetSource(configuration, "directory");
        var gamePak = SingleAssetSource(configuration, "archive");
        var auxiliary = content.LoadModelAuxiliaryContent();
        var options = new ZoneNativeEngineOptions(configuration.EngineDirectory, workingDirectory,
            configuration.LogDirectory, gamePak, logName, $"\"{Environment.ProcessPath}\"");
        Console.WriteLine($"Framework startup inputs: engine {configuration.EngineDirectory}, working {workingDirectory}, game_pak {gamePak}.");
        Console.WriteLine($"Framework model content: {models.Models.Count} scalar definitions, {models.ActorModels.Rows.Count} actor rows, {models.ActorModels.StanceRows.Count} stance rows, {auxiliary.BindingRows.Count} binding rows, {auxiliary.SlaveExtendModelLinkRows.Count} extension links.");
        return (options, auxiliary);
    }

    private static string SingleAssetSource(ZoneRuntimeConfiguration configuration, string kind)
    {
        var matches = configuration.AssetSources.Where(source => source.Kind == kind).ToArray();
        return matches.Length == 1 ? matches[0].Path
            : throw new ArgumentException($"Framework startup requires exactly one {kind} asset source; the configuration supplies {matches.Length}.");
    }
}
