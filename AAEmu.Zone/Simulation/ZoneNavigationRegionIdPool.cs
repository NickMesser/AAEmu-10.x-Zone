namespace AAEmu.Zone.Simulation;

/// <summary>
/// NavigationManager's CBuildingIDManager at +0x308. These IDs belong to the
/// runtime region owner, independently of authored row indices and graph node IDs.
/// </summary>
public sealed class ZoneNavigationRegionIdPool
{
    private readonly HashSet<int> _allocated = [];
    private readonly Stack<int> _available = new(64);

    public int AllocatedCount => _allocated.Count;
    public int AvailableCount => _available.Count;

    public bool IsAllocated(int id) => _allocated.Contains(id);

    /// <summary>
    /// Allocates before modifier name admission. A rejected duplicate modifier
    /// still consumes its allocation until the owner explicitly releases or resets it.
    /// </summary>
    public int Allocate()
    {
        // 31048EB8 / 31048F18: refill only when empty, using the active set's count.
        // Native has no fixed capacity or exhaustion result; its addition is 32-bit.
        if (_available.Count == 0)
        {
            var first = _allocated.Count;
            for (var offset = 63; offset >= 0; offset--)
                _available.Push(unchecked(first + offset));
        }

        var id = _available.Pop();
        _allocated.Add(id);
        return id;
    }

    /// <summary>
    /// 31048F88 ignores negative IDs. Missing nonnegative IDs leave the pool
    /// unchanged and return the outcome for which native emits its diagnostic.
    /// Released IDs are reused before the remainder of the current batch, last first.
    /// </summary>
    public ZoneNavigationRegionIdReleaseResult Release(int id)
    {
        if (id < 0)
            return ZoneNavigationRegionIdReleaseResult.IgnoredNegative;
        if (!_allocated.Remove(id))
            return ZoneNavigationRegionIdReleaseResult.NotAllocated;

        _available.Push(id);
        return ZoneNavigationRegionIdReleaseResult.Released;
    }

    /// <summary>
    /// 31049028 clears both collections. The owning manager clears its modifier
    /// records before calling this; the next allocation is zero (3113AA6C).
    /// </summary>
    public void Reset()
    {
        _allocated.Clear();
        _available.Clear();
    }
}

public enum ZoneNavigationRegionIdReleaseResult
{
    Released,
    IgnoredNegative,
    NotAllocated
}
