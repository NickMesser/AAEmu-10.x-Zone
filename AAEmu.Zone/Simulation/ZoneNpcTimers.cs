using System.Collections.ObjectModel;

namespace AAEmu.Zone.Simulation;

/// <summary>Native AI time event and ignored-target deadlines, driven by the NPC frame scheduler.</summary>
public sealed class ZoneNpcTimers
{
    private readonly Func<uint> _time;
    private readonly SortedDictionary<uint, uint> _ignored = [];
    public bool AiTimeEventEnabled { get; private set; }
    public uint AiTimeEventInterval { get; private set; }
    public uint LastAiTimeEvent { get; private set; }
    public IReadOnlyDictionary<uint, uint> IgnoredTargets { get; }

    internal ZoneNpcTimers(Func<uint> time)
    {
        _time = time;
        LastAiTimeEvent = time();
        IgnoredTargets = new ReadOnlyDictionary<uint, uint>(_ignored);
    }

    public void ActivateAiTimeEvent(bool enabled, uint intervalMilliseconds)
    {
        // 3933B3B0: intervals below 50 only produce a native warning, not a clamp.
        AiTimeEventEnabled = enabled;
        AiTimeEventInterval = intervalMilliseconds;
        LastAiTimeEvent = _time();
    }

    internal void UpdateAiTimeEvent(Action callback)
    {
        if (!AiTimeEventEnabled) return;
        var now = _time();
        if (unchecked(now - LastAiTimeEvent) < AiTimeEventInterval) return;
        callback();
        // 3933BE40 writes the sampled timestamp after the callback, even if it reconfigures the timer.
        LastAiTimeEvent = now;
    }

    public void IgnoreTarget(uint objectId, float seconds)
    {
        var now = _time();
        // 39342FE0 evicts only when adding a new ID to a map already larger than 20.
        // The resulting capacity is 21. Earliest unsigned deadline wins; ties use lowest ID.
        if (!_ignored.ContainsKey(objectId) && _ignored.Count > 20)
        {
            var oldest = _ignored.First();
            foreach (var entry in _ignored)
                if (entry.Value < oldest.Value) oldest = entry;
            _ignored.Remove(oldest.Key);
        }
        _ignored[objectId] = unchecked(now + (uint)ZoneStatMath.ToInt64(seconds * 1000f));
    }

    internal void ExpireIgnoredTargets()
    {
        if (_ignored.Count == 0) return;
        var now = _time();
        // 3933FB20 compares absolute unsigned values; wrapped deadlines can expire early.
        foreach (var id in _ignored.Where(entry => now >= entry.Value).Select(entry => entry.Key).ToArray())
            _ignored.Remove(id);
    }
}
