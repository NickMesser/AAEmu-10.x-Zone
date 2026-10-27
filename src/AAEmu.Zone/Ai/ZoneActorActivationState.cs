namespace AAEmu.Zone.Ai;

/// <summary>
/// CAIProxy's activation countdown and distance-based update cadence, 30516370/30514DA0/30519920.
/// Each actual actor proxy owns a fresh instance; replacing its NPC's actor must not reuse this state.
/// Entity policy changes and the global AI override remain required scene services.
/// </summary>
public sealed class ZoneActorActivationState
{
    private static readonly float[] DistanceThresholds = [30, 50, 70, 90];
    private static readonly int[] SkipLimits = [0, 1, 2, 4, 8];
    public short UpdateAlwaysRemaining { get; private set; } = 1024;
    public bool AlwaysUpdate => UpdateAlwaysRemaining != 0;
    public int DistanceBand { get; private set; }
    public int SkippedUpdates { get; private set; }

    /// <summary>
    /// 30516370 seeds the cadence from bits 8 through 31 of the proxy allocation address modulo 9.
    /// A managed scene supplies its explicit allocation key; object IDs are not substituted here.
    /// </summary>
    public ZoneActorActivationState(ulong allocationKey) =>
        SkippedUpdates = (int)(((allocationKey >> 8) & 0xffffff) % 9);

    /// <summary>305198F0: equal thresholds enter the farther band; COMISS/JB leaves NaN in band 0.</summary>
    public void SetPlayerDistance(float distance)
    {
        DistanceBand = 0;
        while (DistanceBand < DistanceThresholds.Length && distance >= DistanceThresholds[DistanceBand])
            DistanceBand++;
    }

    /// <summary>30519920: nonzero actor alertness updates immediately and resets the skipped-update count.</summary>
    public bool ShouldUpdate(Func<int> readAlertness)
    {
        ArgumentNullException.ThrowIfNull(readAlertness);
        if (readAlertness() == 0 && SkippedUpdates < SkipLimits[DistanceBand])
        {
            SkippedUpdates++;
            return false;
        }
        SkippedUpdates = 0;
        return true;
    }

    /// <summary>
    /// 30514D60: a zero request cannot cancel a positive countdown. Accepted values immediately run
    /// the policy update, so a newly supplied positive value is decremented before the scene callback.
    /// </summary>
    public bool SetUpdateAlways(short count, Func<bool> readGlobalOverride, Action<int> setEntityUpdatePolicy)
    {
        ArgumentNullException.ThrowIfNull(readGlobalOverride);
        ArgumentNullException.ThrowIfNull(setEntityUpdatePolicy);
        if (count == 0 && UpdateAlwaysRemaining > 0) return false;
        UpdateAlwaysRemaining = count;
        UpdateEntityPolicy(readGlobalOverride, setEntityUpdatePolicy);
        return true;
    }

    /// <summary>30514DA0: positive countdowns decrease; negative values persist. Entity policies are 1 or 2.</summary>
    public void UpdateEntityPolicy(Func<bool> readGlobalOverride, Action<int> setEntityUpdatePolicy)
    {
        ArgumentNullException.ThrowIfNull(readGlobalOverride);
        ArgumentNullException.ThrowIfNull(setEntityUpdatePolicy);
        if (UpdateAlwaysRemaining > 0) UpdateAlwaysRemaining--;
        // Native reads the countdown again after the global getter, including any callback mutation.
        var always = readGlobalOverride() || UpdateAlwaysRemaining != 0;
        setEntityUpdatePolicy(always ? 1 : 2);
    }
}
