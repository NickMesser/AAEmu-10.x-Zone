using System.Diagnostics;

namespace AAEmu.Zone.Hosting;

/// <summary>
/// Static (continent) zone launcher. World's ZoneHostSupervisor starts only instance zones; the
/// native Zone Manager that started continent zones is not part of AAEmu-10.x. This launcher fills
/// that role: <c>--zones a,b,...</c> starts one zone server process per map with
/// <c>+zone/+sv_map &lt;map&gt; +instance 0</c> (native is one zone per process) and forwards every
/// other argument unchanged. A child that exits while the launcher is running is restarted with
/// backoff, because World sends a dropped continent zone's players back to character select and
/// nothing else restarts it.
/// </summary>
public sealed class ZoneLauncher
{
    public const string ZonesOption = "--zones";

    /// <summary>CVars the launcher owns per child; forwarding them would make two hosts claim one zone.</summary>
    private static readonly string[] PerZoneCVars = ["+zone", "+sv_map", "+instance"];

    public IReadOnlyList<string> Zones { get; }
    public IReadOnlyList<string> ForwardedArguments { get; }

    private ZoneLauncher(IReadOnlyList<string> zones, IReadOnlyList<string> forwarded)
    {
        Zones = zones;
        ForwardedArguments = forwarded;
    }

    /// <summary>Null when the arguments do not select launcher mode.</summary>
    public static ZoneLauncher? TryParse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var index = -1;
        for (var i = 0; i < args.Count; i++)
            if (args[i] == ZonesOption)
                index = index < 0 ? i : throw new ArgumentException($"Duplicate option {ZonesOption}.");
        if (index < 0)
            return null;
        if (index + 1 == args.Count || args[index + 1].StartsWith('-') || args[index + 1].StartsWith('+'))
            throw new ArgumentException($"Missing value for {ZonesOption}; expected a comma-separated list of zone map names.");
        var zones = args[index + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (zones.Length == 0)
            throw new ArgumentException($"{ZonesOption} names no zone.");
        // World's registry keys hosts by (zoneKey, iid): two continent hosts for one zone replace each other.
        var duplicate = zones.GroupBy(zone => zone, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"{ZonesOption} names zone '{duplicate.Key}' more than once.");
        var forwarded = args.Where((_, i) => i != index && i != index + 1).ToArray();
        foreach (var owned in PerZoneCVars)
            if (forwarded.Any(arg => arg.Equals(owned, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"{owned} cannot be combined with {ZonesOption}; the launcher sets it per zone.");
        return new(zones, forwarded);
    }

    /// <summary>The exact arguments one zone's server process receives.</summary>
    public IReadOnlyList<string> ChildArguments(string zone) =>
        [.. ForwardedArguments, "+zone", zone, "+sv_map", zone, "+instance", "0"];

    /// <summary>
    /// Supervise every zone until <paramref name="shutdown"/>. On shutdown each child gets
    /// <paramref name="stopGrace"/> to retire (children share the console, so Ctrl+C reaches them
    /// directly) before its process tree is killed. Returns 0 after every child is gone.
    /// </summary>
    public async Task<int> RunAsync(IZoneChildProcessFactory factory, ZoneRestartPolicy policy,
        TimeSpan stopGrace, Action<string> log, CancellationToken shutdown)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(log);
        await Task.WhenAll(Zones.Select(zone =>
            SuperviseAsync(zone, factory, policy, stopGrace, log, shutdown))).ConfigureAwait(false);
        return 0;
    }

    private async Task SuperviseAsync(string zone, IZoneChildProcessFactory factory, ZoneRestartPolicy policy,
        TimeSpan stopGrace, Action<string> log, CancellationToken shutdown)
    {
        var failures = 0;
        while (!shutdown.IsCancellationRequested)
        {
            var started = policy.Clock();
            IZoneChildProcess child;
            try
            {
                child = factory.Start(zone, ChildArguments(zone));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                log($"[{zone}] failed to start: {error.Message}");
                if (!await DelayAsync(policy.Backoff(++failures), shutdown).ConfigureAwait(false)) return;
                continue;
            }
            log($"[{zone}] started pid {child.Id}.");
            int exitCode;
            using (child)
            {
                try
                {
                    exitCode = await child.WaitForExitAsync(shutdown).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                    await StopAsync(zone, child, stopGrace, log).ConfigureAwait(false);
                    return;
                }
            }
            if (shutdown.IsCancellationRequested)
            {
                log($"[{zone}] exited with code {exitCode} during shutdown.");
                return;
            }
            failures = policy.Clock() - started >= policy.HealthyRun ? 1 : failures + 1;
            var delay = policy.Backoff(failures);
            log($"[{zone}] exited with code {exitCode}; restarting in {delay.TotalSeconds:0.#} s.");
            if (!await DelayAsync(delay, shutdown).ConfigureAwait(false)) return;
        }
    }

    private static async Task StopAsync(string zone, IZoneChildProcess child, TimeSpan grace, Action<string> log)
    {
        using var deadline = new CancellationTokenSource(grace);
        try
        {
            var code = await child.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            log($"[{zone}] retired with code {code}.");
        }
        catch (OperationCanceledException)
        {
            log($"[{zone}] did not retire within {grace.TotalSeconds:0.#} s; killing its process tree.");
            child.Kill();
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken shutdown)
    {
        try
        {
            await Task.Delay(delay, shutdown).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>Restart backoff: doubles from <see cref="Initial"/> up to <see cref="Maximum"/>; a run of at least <see cref="HealthyRun"/> resets it.</summary>
public sealed record ZoneRestartPolicy(TimeSpan Initial, TimeSpan Maximum, TimeSpan HealthyRun, Func<TimeSpan> Clock)
{
    public static ZoneRestartPolicy Default { get; } = new(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5), () => TimeSpan.FromMilliseconds(Environment.TickCount64));

    public TimeSpan Backoff(int failures)
    {
        var factor = Math.Pow(2, Math.Clamp(failures - 1, 0, 30));
        return TimeSpan.FromTicks((long)Math.Min(Maximum.Ticks, Initial.Ticks * factor));
    }
}

public interface IZoneChildProcess : IDisposable
{
    int Id { get; }
    Task<int> WaitForExitAsync(CancellationToken cancellation);
    void Kill();
}

public interface IZoneChildProcessFactory
{
    IZoneChildProcess Start(string zone, IReadOnlyList<string> arguments);
}

/// <summary>
/// Starts zone server processes from this executable. Output is prefixed with the zone name;
/// children stay on the launcher's console so Ctrl+C reaches them directly.
/// </summary>
public sealed class ZoneServerProcessFactory(string fileName, IReadOnlyList<string> leadingArguments) : IZoneChildProcessFactory
{
    /// <summary>
    /// This process's own command: the apphost, or <c>dotnet &lt;entry.dll&gt;</c> when run through the muxer.
    /// </summary>
    public static ZoneServerProcessFactory ForCurrentProcess()
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("The current process path is unknown.");
        var name = Path.GetFileNameWithoutExtension(processPath);
        if (!name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return new(processPath, []);
        var entry = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        return string.IsNullOrEmpty(entry)
            ? throw new InvalidOperationException("The entry assembly path is unknown.")
            : new(processPath, [entry]);
    }

    public IZoneChildProcess Start(string zone, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in leadingArguments.Concat(arguments))
            start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new InvalidOperationException($"Process.Start returned no process for zone {zone}.");
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) Console.Out.WriteLine($"[{zone}] {e.Data}"); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine($"[{zone}] {e.Data}"); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new Child(process);
    }

    private sealed class Child(Process process) : IZoneChildProcess
    {
        public int Id => process.Id;

        public async Task<int> WaitForExitAsync(CancellationToken cancellation)
        {
            await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
            return process.ExitCode;
        }

        public void Kill()
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* already exited */ }
        }

        public void Dispose() => process.Dispose();
    }
}
