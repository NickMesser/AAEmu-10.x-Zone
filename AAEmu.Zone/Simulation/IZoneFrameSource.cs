namespace AAEmu.Zone.Simulation;

/// <summary>
/// Engine frame input. One outstanding read is allowed. Reads must honor cancellation;
/// null ends the simulation. The caller owns the source and the frame/time policy.
/// </summary>
public interface IZoneFrameSource
{
    ValueTask<uint?> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Explicit replacement frame clock. The supplied period is a runtime configuration,
/// not a recovered native tick rate. Slow consumers coalesce ticks instead of catching up.
/// </summary>
public sealed class PeriodicZoneFrameSource(TimeSpan period, uint initialFrame = 0) : IZoneFrameSource, IDisposable
{
    private readonly PeriodicTimer _timer = new(period);
    private uint _frame = initialFrame;

    public async ValueTask<uint?> ReadAsync(CancellationToken cancellationToken) =>
        await _timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false) ? unchecked(++_frame) : null;

    public void Dispose() => _timer.Dispose();
}
