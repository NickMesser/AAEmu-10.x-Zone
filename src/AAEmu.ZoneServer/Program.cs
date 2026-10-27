using System.Runtime.InteropServices;

using AAEmu.Zone.Hosting;
using AAEmu.Zone.WorldLink;

namespace AAEmu.ZoneServer;

/// <summary>
/// AAEmu 10.x zone server: the process World runs in place of the native dedicated zone
/// (dedicated host + x2game-dev_dedicate.dll). Point World's <c>ZoneHost.Executable</c> at this
/// executable, start one zone by hand with <c>--config</c>/<c>+zone</c>, or start every continent
/// zone with <c>--zones</c>.
/// </summary>
public static class Program
{
    /// <summary>Process exit codes. World only distinguishes "running" from "exited".</summary>
    public const int ExitRetired = 0, ExitFailed = 1, ExitCancelled = 130;
    public const int ExitWorldDisconnected = ZoneWorldLifecycle.WorldDisconnectedExitCode;

    /// <summary>Time a launcher child gets to retire after shutdown before its tree is killed.</summary>
    private static readonly TimeSpan ChildStopGrace = TimeSpan.FromSeconds(30);

    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("AAEmu 10.x Zone server (replacement for the native dedicated zone)");
            Console.WriteLine("  One zone:  AAEmu.ZoneServer --config <zone.json> [+zone <map>] [--spawner-file <npc_spawners.g>]");
            Console.WriteLine("  Continent: AAEmu.ZoneServer --zones <map>[,<map>...] [--config <zone.json>] [+world_ip <ip> +world_port <port> ...]");
            Console.WriteLine("             Starts one zone server per map with +instance 0 and restarts it with backoff if it exits.");
            Console.WriteLine("  By World:  AAEmu.ZoneServer -dedicated +world_ip <ip> +world_port <port> +zone <map> +instance <id> +sv_port <port> +db_location <path> ...");
            Console.WriteLine($"             The base configuration comes from +zone_config, {ZoneServerLaunch.ConfigEnvironment} or {ZoneServerLaunch.DefaultConfigFileName} beside the executable.");
            Console.WriteLine($"             {ZoneServerLaunch.SaveDirectoryEnvironment} and {ZoneServerLaunch.LogNameEnvironment} select the log location World expects.");
            Console.WriteLine("  Diagnostic checks (--check-*, --probe-*) live in tools/AAEmu.Zone.Diagnostics.");
            Console.WriteLine($"Exit codes: {ExitRetired} retired in order, {ExitFailed} failed, {ExitWorldDisconnected} World disconnected, {ExitCancelled} cancelled. Gameplay simulation is under construction; see TASKS.md.");
            return ExitRetired;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Console.Error.WriteLine($"Zone fatal: unhandled {(e.IsTerminating ? "terminating " : "")}exception: {e.ExceptionObject}");
        using var shutdown = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; shutdown.Cancel(); };
        Console.CancelKeyPress += cancel;
        // SIGTERM (and console close/logoff/shutdown on Windows) request the same ordered retirement
        // as Ctrl+C. World's supervisor uses Kill(entireProcessTree), which no handler can observe.
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Retire);
        using var hangup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, Retire);
        try
        {
            if (ZoneLauncher.TryParse(args) is { } launcher)
            {
                Console.WriteLine($"Zone launcher: {launcher.Zones.Count} continent zone(s): {string.Join(", ", launcher.Zones)}.");
                return await launcher.RunAsync(ZoneServerProcessFactory.ForCurrentProcess(), ZoneRestartPolicy.Default,
                    ChildStopGrace, message => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}"), shutdown.Token).ConfigureAwait(false);
            }
            var launch = ZoneServerLaunch.Parse(args, Environment.GetEnvironmentVariable);
            return await ZoneServerStartup.RunAsync(launch, shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return ExitCancelled;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Zone failed: {ex.Message}");
            // Argument errors are named by their message; anything else keeps its stack for the log.
            if (ex is not ArgumentException)
                Console.Error.WriteLine(ex);
            return ExitFailed;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }

        void Retire(PosixSignalContext context)
        {
            // Cancel the default termination so the scene and engine retire in their recorded order.
            context.Cancel = true;
            Console.Error.WriteLine($"Zone received {context.Signal}; retiring.");
            shutdown.Cancel();
        }
    }
}
