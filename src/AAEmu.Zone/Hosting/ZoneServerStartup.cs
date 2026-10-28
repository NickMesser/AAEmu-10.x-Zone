using System.Net.Sockets;

using AAEmu.Zone.Content;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Scene;
using AAEmu.Zone.WorldLink;

namespace AAEmu.Zone.Hosting;

/// <summary>
/// The zone server's production path: resolve configuration (applying World's launch CVars),
/// load content, connect and synchronize with World once, then hand everything to
/// ZoneSceneHost.RunAsync (load → readiness → adopt → receive → ordered retirement).
/// Returns 0 only after the scene and connection fully retired.
/// </summary>
public static class ZoneServerStartup
{
    public static async Task<int> RunAsync(ZoneServerLaunch launch, CancellationToken shutdown)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var configPath = launch.ConfigPath ?? throw new ArgumentException(
            $"No zone configuration: pass --config, +zone_config, set {ZoneServerLaunch.ConfigEnvironment} or place {ZoneServerLaunch.DefaultConfigFileName} beside the executable.");
        var configuration = ZoneRuntimeConfiguration.LoadFile(configPath);
        using var content = new ZoneContentDatabase(launch.ContentDatabasePath ?? configuration.ContentDatabasePath);
        // World names the map; the key always comes from the content database, never from the launcher.
        var launchedZone = content.ResolveZone(launch.ZoneName ?? configuration.ZoneName);
        configuration = configuration.WithLaunch(launch.WorldHost, launch.WorldPort, launchedZone.Name,
            launchedZone.Key, launch.ContentDatabasePath, launch.SaveDirectory);
        using var log = new ZoneConsoleLog(configuration.LogDirectory, launch.LogName);
        Console.WriteLine($"Run log: {log.Path}");
        if (launch.LaunchedByWorld)
        {
            Console.WriteLine($"Launched by World: zone {launchedZone.Name}, instance {launch.InstanceId ?? 0}, sv_port {launch.ServerPort?.ToString() ?? "(none)"}, World {configuration.WorldHost}:{configuration.WorldPort}.");
            if (launch.NativeDll is not null)
                Console.WriteLine($"{ZoneServerLaunch.NativeDllEnvironment}={launch.NativeDll} is ignored: this process replaces the native zone module.");
            foreach (var name in launch.IgnoredCVars)
                Console.WriteLine($"CVar +{name} {launch.CVars[name]} has no replacement owner yet and is ignored.");
        }
        Console.WriteLine($"Configured engine directory: {configuration.EngineDirectory}.");
        Console.WriteLine($"Configured asset sources: {string.Join(" -> ", configuration.AssetSources.Select(source => $"{source.Kind}:{source.Path}"))}");

        var contentNameComparer = WorldContentNameComparer.ForLocale(configuration.Locale);
        var zone = configuration.ResolveZone(content);
        using var assets = configuration.OpenAssets();
        var layout = assets.LoadLayout(zone);
        var loaded = ZoneHostContent.Load(content, content.LoadStatContent(), content.LoadModelContent());
        var battlefield = content.IsBattlefield(zone.Key);
        loaded.Print(zone, battlefield);
        var placements = LoadPlacements(launch.SpawnerFile ?? configuration.SpawnerFile, configuration, zone, assets);
        loaded.PrintPlacements(placements);
        Console.WriteLine($"World map loaded: {layout.World.Header.Name}, {layout.World.Zones.Count} Zone rows, {layout.World.Cells.Count} world cells; zone cells {layout.Cells.Count}.");

        using var client = new TcpClient { NoDelay = true };
        using (var connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown))
        {
            connectDeadline.CancelAfter(TimeSpan.FromSeconds(30));
            await client.ConnectAsync(configuration.WorldHost, configuration.WorldPort, connectDeadline.Token).ConfigureAwait(false);
        }
        var join = new ZoneJoin(ZoneJoin.NativeProtocolVersion, ZoneJoin.NativeProtocolVersion,
            zone.Key, 0, launch.ServerPort ?? ZoneServerLaunch.DefaultServerPort, 0, launch.InstanceId ?? 0, false);
        var startup = await ZoneWorldClient.SynchronizeAsync(client.GetStream(), join,
            TimeSpan.FromSeconds(30), shutdown, contentNameComparer).ConfigureAwait(false);
        Console.WriteLine($"Startup synchronized: {startup.Factions.Count} factions, {startup.Relations.Count} relations, {startup.Spawners.Count} saved spawners, {startup.ContentFilter!.Categories.Count} content filter categories.");

        startup.WorldName = configuration.WorldName;
        startup.TimeOfDayState.EngineUpdateEnabled =
            ZoneEngineCVars.Load(assets, launch.CVars).GetBool(ZoneEngineCVars.TimeOfDayEngineUpdate, true);
        Console.WriteLine($"{ZoneEngineCVars.TimeOfDayEngineUpdate} = {(startup.TimeOfDayState.EngineUpdateEnabled ? 1 : 0)}; server type {ZoneServerType.For(startup.WorldName)}.");

        var engineInputs = ZoneEngineInputs.Create(configuration, layout, content, loaded.Models, "zone-host.log");
        Action<string> trace = message => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
        var hostInputs = new ZoneSceneHost.ZoneHostInputs(
            engineInputs.Options, loaded.Models, engineInputs.Auxiliary, layout, assets, loaded.Stats, loaded.Npcs,
            loaded.SpawnerControllers, loaded.World, loaded.Achievements, loaded.Loot, loaded.Scene, zone, battlefield,
            startup, configuration, configuration.Backends, placements, trace);
        await ZoneSceneHost.RunAsync(hostInputs, client.GetStream(), shutdown).ConfigureAwait(false);
        trace("Host session retired in order (frame -> loading -> tick -> spawners -> activation -> engine).");
        // The receive loop only returns without cancellation when World closed the connection (ZW-03).
        // Dedicated server types 2 and 3 run console "quit" natively (3936D7E0): orderly exit, distinct code.
        var serverType = ZoneServerType.For(startup.WorldName);
        if (!shutdown.IsCancellationRequested && ZoneWorldLifecycle.QuitsOnDisconnect(serverType))
        {
            trace($"World disconnected; server type {serverType} quits (native 3936D7E0).");
            return ZoneWorldLifecycle.WorldDisconnectedExitCode;
        }
        return 0;
    }

    /// <summary>
    /// Authored spawner placements: an explicit file wins; otherwise the zone's own
    /// <c>npc_spawners.g</c> from the configured assets, at the same world-relative location World's
    /// ZoneGameDataRootResolver.TryResolveNpcSpawnersFile uses.
    /// </summary>
    private static ZoneNpcSpawnerPlacementContent LoadPlacements(string? explicitFile,
        ZoneRuntimeConfiguration configuration, ZoneContent zone, ZoneConfiguredAssets assets)
    {
        if (explicitFile is not null)
            return ZoneNpcSpawnerPlacementContent.LoadFile(explicitFile);
        var assetPath = NpcSpawnersAsset(configuration.WorldName, zone.Key);
        using var stream = assets.OpenRead(assetPath) ?? throw new FileNotFoundException(
            "No spawner placements: the configured assets lack the zone's npc_spawners.g; set spawnerFile or pass --spawner-file.", assetPath);
        Console.WriteLine($"Authored NPC spawners: {assetPath}");
        return ZoneNpcSpawnerPlacementContent.Load(stream);
    }

    public static string NpcSpawnersAsset(string worldName, int zoneKey) =>
        $"game/worlds/{worldName}/level_design/zone/{zoneKey.ToString(System.Globalization.CultureInfo.InvariantCulture)}/zone_server/npc_spawners.g";
}
