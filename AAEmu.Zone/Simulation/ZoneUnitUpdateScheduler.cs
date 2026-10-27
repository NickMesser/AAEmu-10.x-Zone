using System.Collections.ObjectModel;
using AAEmu.Zone.Protocol.Units;

namespace AAEmu.Zone.Simulation;

/// <summary>Required scene phases surrounding ServerUnit's rolling update. No missing phase is replaced by a no-op.</summary>
public interface IZoneUnitUpdateServices
{
    uint TimeMilliseconds { get; }
    /// <summary>
    /// Stable, unique live allocation key. Native removal rebuilds the vector in unsigned pointer order;
    /// a managed scene must explicitly choose its allocation ordering instead of substituting object IDs.
    /// </summary>
    ulong GetAllocationKey(ZoneUnit unit);
    void UpdateScaleTransition(ZoneUnit unit); // 3925A3D0
    bool UpdateUnitModels { get; }
    void UpdateUnitModel(ZoneUnit unit); // unit model virtual +570
    void UpdateSkills(ZoneUnit unit, uint elapsedMilliseconds); // 39BF7A90
    void UpdateBuffs(ZoneUnit unit, uint elapsedMilliseconds); // 3944FC90, including per-buff landing decisions
    bool IsSpatiallyExcluded(ZoneUnit unit); // object-ID vector 3A638F90
    void UpdateSpatialMembership(ZoneUnit unit); // 392589F0 -> 39D2DD60
    void UpdateShipMass(ZoneUnit unit); // ServerUnit virtual +C8 -> 39261A50
    void UpdateShipSubmergeDepth(ZoneUnit unit); // ServerUnit virtual +D8 -> 39258EB0
}

/// <summary>
/// Connection-owned scheduling vector and per-unit clocks from 3935DF30/3935DC20.
/// Registration appends; removal rebuilds by the scene's allocation key. The cursor is retained on removal.
/// Appended registrations join a later batch. Synchronous managed removals retire the remaining batch,
/// preserving completed work and timestamps.
/// That callback policy avoids native freed-slot access; it is not a claim about native mutation safety.
/// </summary>
public sealed class ZoneUnitUpdateScheduler : IDisposable
{
    private sealed class Entry(ZoneUnit unit, ulong key, uint time)
    {
        public ZoneUnit Unit { get; } = unit;
        public ulong Key { get; } = key;
        public uint LastUpdate { get; set; } = time;
    }

    private readonly IZoneUnitUpdateServices _services;
    private readonly List<Entry> _entries = [];
    private readonly Dictionary<ZoneUnit, Entry> _byUnit = [];
    private readonly HashSet<ulong> _keys = [];
    private bool _updating;
    private uint _revision;
    public bool IsDisposed { get; private set; }
    public uint Cursor { get; private set; }
    public int Count => _entries.Count;

    public ZoneUnitUpdateScheduler(IZoneUnitUpdateServices services) =>
        _services = services ?? throw new ArgumentNullException(nameof(services));

    public IReadOnlyList<ZoneUnit> GetUpdateOrder() =>
        new ReadOnlyCollection<ZoneUnit>(_entries.Select(entry => entry.Unit).ToArray());

    public uint? GetLastUpdateTime(ZoneUnit unit) =>
        _byUnit.TryGetValue(unit, out var entry) ? entry.LastUpdate : null;

    public void Register(ZoneUnit unit)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(unit);
        if (_byUnit.ContainsKey(unit)) throw new InvalidOperationException("The unit already has an update registration.");
        var key = _services.GetAllocationKey(unit);
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (key == 0 || _keys.Contains(key)) throw new InvalidOperationException("The scene must supply a unique nonzero live allocation key.");
        // 3935D260 samples the engine clock at the end of initialization, before 39360590 appends the owner.
        var now = _services.TimeMilliseconds;
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (_byUnit.ContainsKey(unit) || _keys.Contains(key))
            throw new InvalidOperationException("The unit or allocation key was registered during initialization.");
        var entry = new Entry(unit, key, now);
        _byUnit.Add(unit, entry); _keys.Add(key); _entries.Add(entry);
        // The native loop reloads the vector base but retains its selected end. Append does not
        // disturb existing slots, including when allocation grows the backing vector.
    }

    public bool Remove(ZoneUnit unit)
    {
        if (!_byUnit.Remove(unit, out var entry)) return false;
        _keys.Remove(entry.Key);
        _entries.Remove(entry);
        _entries.Sort((left, right) => left.Key.CompareTo(right.Key)); // 3935FB70 rebuilds from the ordered pointer set.
        _revision = unchecked(_revision + 1);
        return true;
    }

    /// <summary>3935DF30: one non-wrapping batch, min(count / 8 + 3, count - cursor).</summary>
    public void Update()
    {
        BeginUpdate();
        try
        {
            var count = checked((uint)_entries.Count);
            if (Cursor >= count) Cursor = 0;
            var start = Cursor;
            Cursor += Math.Min((count >> 3) + 3, count - Cursor); // Advance before the first clock or scene callback.
            var revision = _revision;
            for (var index = start; index < Cursor && IsCurrent(revision); index++)
                UpdateEntry(_entries[checked((int)index)], revision);
        }
        finally { _updating = false; }
    }

    /// <summary>3935DC20: explicit update of one registered unit, retaining the rolling cursor.</summary>
    public bool UpdateUnit(ZoneUnit unit)
    {
        BeginUpdate();
        try
        {
            if (!_byUnit.TryGetValue(unit, out var entry)) return false;
            UpdateEntry(entry, _revision);
            return true;
        }
        finally { _updating = false; }
    }

    private void BeginUpdate()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (_updating) throw new InvalidOperationException("Unit updates cannot run recursively.");
        _updating = true;
    }

    private bool IsCurrent(uint revision) => !IsDisposed && revision == _revision;

    private void UpdateEntry(Entry entry, uint revision)
    {
        var unit = entry.Unit;
        var now = _services.TimeMilliseconds;
        if (!IsCurrent(revision)) return;
        var elapsed = unchecked(now - entry.LastUpdate);
        entry.LastUpdate = now; // Advance even when model/buff/spatial gates are disabled or a later phase fails.
        _services.UpdateScaleTransition(unit);
        if (!IsCurrent(revision)) return;
        var updateModel = _services.UpdateUnitModels;
        if (!IsCurrent(revision)) return;
        if (updateModel)
        {
            _services.UpdateUnitModel(unit);
            if (!IsCurrent(revision)) return;
        }
        _services.UpdateSkills(unit, elapsed);
        if (!IsCurrent(revision)) return;
        if (unit.BuffTimingEnabled)
        {
            _services.UpdateBuffs(unit, elapsed);
            if (!IsCurrent(revision)) return;
        }
        if (unit.Snapshot.Identity.Kind is ZoneUnitKind.Character or ZoneUnitKind.Npc)
        {
            var excluded = _services.IsSpatiallyExcluded(unit);
            if (!IsCurrent(revision)) return;
            if (!excluded)
            {
                _services.UpdateSpatialMembership(unit);
                if (!IsCurrent(revision)) return;
            }
        }
        _services.UpdateShipMass(unit);
        if (IsCurrent(revision)) _services.UpdateShipSubmergeDepth(unit);
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        _entries.Clear(); _byUnit.Clear(); _keys.Clear();
        _revision = unchecked(_revision + 1);
    }
}
