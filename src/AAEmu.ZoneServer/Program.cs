using AAEmu.Zone.Hosting;

namespace AAEmu.ZoneServer;

/// <summary>
/// AAEmu 10.x zone server: the process World runs in place of the native dedicated zone
/// (dedicated host + x2game-dev_dedicate.dll). Point World's <c>ZoneHost.Executable</c> at this
/// executable, or start it by hand with <c>--config</c>.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("AAEmu 10.x Zone server (replacement for the native dedicated zone)");
            Console.WriteLine("  Manual:   AAEmu.ZoneServer --config <zone.json> [--spawner-file <npc_spawners.g>]");
            Console.WriteLine("  By World: AAEmu.ZoneServer -dedicated +world_ip <ip> +world_port <port> +zone <map> +instance <id> +sv_port <port> +db_location <path> ...");
            Console.WriteLine($"            The base configuration comes from +zone_config, {ZoneServerLaunch.ConfigEnvironment} or {ZoneServerLaunch.DefaultConfigFileName} beside the executable.");
            Console.WriteLine($"            {ZoneServerLaunch.SaveDirectoryEnvironment} and {ZoneServerLaunch.LogNameEnvironment} select the log location World expects.");
            Console.WriteLine("  Diagnostic checks (--check-*, --probe-*) live in tools/AAEmu.Zone.Diagnostics.");
            Console.WriteLine("Gameplay simulation is under construction; see TASKS.md.");
            return 0;
        }

        using var shutdown = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; shutdown.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var launch = ZoneServerLaunch.Parse(args, Environment.GetEnvironmentVariable);
            return await ZoneServerStartup.RunAsync(launch, shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Zone failed: {ex.Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }
}
