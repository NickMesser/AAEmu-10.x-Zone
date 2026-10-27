namespace AAEmu.Zone.Ai;

/// <summary>310D8C88 / 310D6028: COPWait counts and retires the actor's grouped active operations.</summary>
public sealed class ZoneWaitGoal(int waitType, int blockCount, ZoneGoalExecutor executor) : IZoneGoalOperation
{
    private readonly ZoneGoalExecutor _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    private bool _disposed;
    public int Execute()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var count = _executor.CountGroupedGoals();
        var complete = waitType switch { 0 => count == 0, 1 => count < blockCount, 2 => unchecked(count + 1) < blockCount, _ => true };
        if (!complete) return 1;
        _executor.ClearGroupedGoals();
        return 4;
    }
    public void Reset() => ObjectDisposedException.ThrowIf(_disposed, this);
    public void Dispose() => _disposed = true;
}
