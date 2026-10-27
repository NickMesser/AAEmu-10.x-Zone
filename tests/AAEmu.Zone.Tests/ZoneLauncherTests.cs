using System.Collections.Concurrent;

using AAEmu.Zone.Hosting;

namespace AAEmu.Zone.Tests;

public sealed class ZoneLauncherTests
{
    [Test]
    public async Task ZonesAreSplitAndEachChildGetsItsOwnContinentIdentity()
    {
        var launcher = ZoneLauncher.TryParse(["--config", "zone.json", "--zones", "w_gweonid_forest_1, w_solzreed_1", "+world_ip", "10.0.0.2"])!;
        await Assert.That(launcher.Zones.SequenceEqual(new[] { "w_gweonid_forest_1", "w_solzreed_1" })).IsTrue();
        await Assert.That(launcher.ChildArguments("w_solzreed_1").SequenceEqual(new[]
        {
            "--config", "zone.json", "+world_ip", "10.0.0.2",
            "+zone", "w_solzreed_1", "+sv_map", "w_solzreed_1", "+instance", "0",
        })).IsTrue();
        await Assert.That(ZoneLauncher.TryParse(["--config", "zone.json", "+zone", "x"])).IsNull();
    }

    [Test]
    public async Task ConflictingOrEmptyZoneListsAreRefusedByName()
    {
        await Assert.That(() => ZoneLauncher.TryParse(["--zones"])).Throws<ArgumentException>();
        await Assert.That(() => ZoneLauncher.TryParse(["--zones", ","])).Throws<ArgumentException>();
        await Assert.That(() => ZoneLauncher.TryParse(["--zones", "a,A"])).Throws<ArgumentException>();
        await Assert.That(() => ZoneLauncher.TryParse(["--zones", "a", "--zones", "b"])).Throws<ArgumentException>();
        await Assert.That(() => ZoneLauncher.TryParse(["--zones", "a", "+instance", "3"])).Throws<ArgumentException>();
        await Assert.That(() => ZoneLauncher.TryParse(["--zones", "a", "+zone", "b"])).Throws<ArgumentException>();
    }

    [Test]
    public async Task ExitedChildIsRestartedWithBackoffUntilShutdown()
    {
        var factory = new FakeFactory();
        using var shutdown = new CancellationTokenSource();
        var launcher = ZoneLauncher.TryParse(["--zones", "a"])!;
        var run = launcher.RunAsync(factory, Policy, TimeSpan.FromSeconds(5), _ => { }, shutdown.Token);

        var first = await factory.Next("a");
        first.Exit(1);
        var second = await factory.Next("a");
        await Assert.That(second.Id).IsNotEqualTo(first.Id);
        second.Exit(0);
        var third = await factory.Next("a");

        shutdown.Cancel();
        third.Exit(130);
        await Assert.That(await run.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo(0);
        await Assert.That(third.Killed).IsFalse();
        await Assert.That(factory.Started("a")).IsEqualTo(3);
    }

    [Test]
    public async Task ChildThatIgnoresShutdownIsKilledAfterTheGrace()
    {
        var factory = new FakeFactory();
        using var shutdown = new CancellationTokenSource();
        var run = ZoneLauncher.TryParse(["--zones", "a,b"])!
            .RunAsync(factory, Policy, TimeSpan.FromMilliseconds(50), _ => { }, shutdown.Token);
        var a = await factory.Next("a");
        var b = await factory.Next("b");
        shutdown.Cancel();
        b.Exit(130);
        await Assert.That(await run.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo(0);
        await Assert.That(a.Killed).IsTrue();
        await Assert.That(b.Killed).IsFalse();
    }

    [Test]
    public async Task BackoffDoublesToTheCap()
    {
        var policy = new ZoneRestartPolicy(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), () => TimeSpan.Zero);
        await Assert.That(policy.Backoff(1)).IsEqualTo(TimeSpan.FromSeconds(5));
        await Assert.That(policy.Backoff(2)).IsEqualTo(TimeSpan.FromSeconds(10));
        await Assert.That(policy.Backoff(4)).IsEqualTo(TimeSpan.FromSeconds(40));
        await Assert.That(policy.Backoff(5)).IsEqualTo(TimeSpan.FromMinutes(1));
        await Assert.That(policy.Backoff(500)).IsEqualTo(TimeSpan.FromMinutes(1));
    }

    private static readonly ZoneRestartPolicy Policy = new(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(5),
        TimeSpan.FromHours(1), () => TimeSpan.Zero);

    private sealed class FakeFactory : IZoneChildProcessFactory
    {
        private readonly ConcurrentDictionary<string, System.Threading.Channels.Channel<FakeChild>> _started = new();
        private readonly ConcurrentDictionary<string, int> _counts = new();
        private int _nextId = 100;

        public IZoneChildProcess Start(string zone, IReadOnlyList<string> arguments)
        {
            var child = new FakeChild(Interlocked.Increment(ref _nextId));
            _counts.AddOrUpdate(zone, 1, (_, count) => count + 1);
            Channel(zone).Writer.TryWrite(child);
            return child;
        }

        public async Task<FakeChild> Next(string zone) =>
            await Channel(zone).Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        public int Started(string zone) => _counts.GetValueOrDefault(zone);

        private System.Threading.Channels.Channel<FakeChild> Channel(string zone) =>
            _started.GetOrAdd(zone, _ => System.Threading.Channels.Channel.CreateUnbounded<FakeChild>());
    }

    private sealed class FakeChild(int id) : IZoneChildProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Id { get; } = id;
        public bool Killed { get; private set; }

        public void Exit(int code) => _exit.TrySetResult(code);

        public Task<int> WaitForExitAsync(CancellationToken cancellation) => _exit.Task.WaitAsync(cancellation);

        public void Kill()
        {
            Killed = true;
            _exit.TrySetResult(-1);
        }

        public void Dispose() { }
    }
}
