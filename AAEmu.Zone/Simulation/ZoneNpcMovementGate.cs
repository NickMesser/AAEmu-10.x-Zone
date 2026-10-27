namespace AAEmu.Zone.Simulation;

/// <summary>NpcManager's movement replication gate (+D8) and retained clock (+DC), 39347390/3934A170.</summary>
public sealed class ZoneNpcMovementGate
{
    public bool CanReplicate { get; private set; } = true;
    public uint LastReplicationTime { get; private set; }

    /// <summary>
    /// Sample once before the manager's NPC update pass. The interval is the unchecked unsigned value
    /// of the native configuration integer; elapsed time must be strictly greater than that interval.
    /// Disabling rate limiting enables replication without sampling the clock or resetting its anchor.
    /// </summary>
    public bool Update(bool rateLimitEnabled, uint intervalMilliseconds, Func<uint> readTimeMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(readTimeMilliseconds);
        if (rateLimitEnabled)
        {
            var now = readTimeMilliseconds();
            if (unchecked(now - LastReplicationTime) <= intervalMilliseconds)
            {
                CanReplicate = false;
                return false;
            }
            LastReplicationTime = now;
        }
        CanReplicate = true;
        return true;
    }
}
