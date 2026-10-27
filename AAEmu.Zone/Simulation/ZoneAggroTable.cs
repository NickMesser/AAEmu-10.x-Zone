using System.Numerics;
using AAEmu.Zone.Content;

namespace AAEmu.Zone.Simulation;

/// <summary>
/// Native NPC threat storage and selection. The world supplies actor eligibility, group
/// membership, physical cancellation and notification delivery through IZoneAggroWorld.
/// This component does not establish NPC simulation readiness by itself.
/// </summary>
public sealed class ZoneAggroTable
{
    public const int Capacity = 100;
    private readonly IZoneAggroWorld _world;
    private readonly List<ZoneAggroEntry> _entries = [];
    private readonly Dictionary<uint, ZoneAggroAmounts> _sleepAmounts = [];
    private readonly SortedSet<uint>[] _abusers = [[], [], []];
    private readonly uint[] _componentTargets = new uint[3];
    private uint _lastAbuserCheck;
    public uint OwnerObjectId { get; }
    public uint NpcId { get; }
    public IReadOnlyList<ZoneAggroEntry> Entries { get; }
    public uint TargetId { get; private set; }
    public bool IsDirty { get; private set; }
    public bool AdditionsBlocked { get; private set; }
    public Vector3 EncounterOrigin { get; private set; }
    public float MinimumScore { get; set; } = -1f;
    public float ReturnDistanceSquared { get; }
    public float AbsoluteReturnDistance { get; }
    private readonly float _absoluteReturnDistanceSquared;
    public int LastClearReason { get; private set; }

    public ZoneAggroTable(uint ownerObjectId, uint npcId, IZoneAggroWorld world,
        float returnDistance = 0, float absoluteReturnDistance = 0)
    {
        OwnerObjectId = ownerObjectId;
        NpcId = npcId;
        _world = world;
        // 3931EF20 stores the normal radius squared and both forms of the absolute radius.
        ReturnDistanceSquared = returnDistance * returnDistance;
        AbsoluteReturnDistance = absoluteReturnDistance;
        _absoluteReturnDistanceSquared = absoluteReturnDistance * absoluteReturnDistance;
        Entries = _entries.AsReadOnly();
    }

    public ZoneAggroEntry? Find(uint targetId) => _entries.FirstOrDefault(entry => entry.TargetId == targetId);
    public bool IsAbuser(uint targetId) => _abusers.Any(category => category.Contains(targetId));

    public bool Add(uint targetId, ZoneAggroAmounts amounts)
    {
        // 39321340: existing entries are not rechecked for acquisition eligibility.
        if (AdditionsBlocked || IsAbuser(targetId) || _world.FindTarget(targetId) is not { } target) return false;
        var entry = Find(targetId);
        var created = entry is null;
        var multiplier = Multiplier(target.OutgoingModifier);
        entry ??= Create(target);
        if (entry is null) return false;
        entry.Amounts = entry.Amounts.Add(amounts.Scale(multiplier));
        if (created)
        {
            if (entry.ScoreAt(_world.TimeMilliseconds) < MinimumScore) Remove(targetId);
        }
        else entry.UpdatedAt = _world.TimeMilliseconds;
        if (amounts.Total != 0)
        {
            // Native notifies even after the threshold removes a newly created entry.
            // Keep that ordering without accessing an erased native vector element.
            entry.DistanceRatio = DistanceRatio(target);
            Changed(targetId);
            CheckAbusers(target);
        }
        return Find(targetId) is not null;
    }

    public bool UpdateIncomingMultiplier(uint targetId)
    {
        // 3931FD30 uses the owning NPC's current attribute, not the target's.
        if (AdditionsBlocked || IsAbuser(targetId) || _world.FindTarget(targetId) is not { } target) return false;
        var entry = Find(targetId) ?? Create(target);
        if (entry is null) return false;
        var multiplier = Multiplier(_world.GetIncomingModifier(this));
        if (entry.IncomingMultiplier != multiplier)
        {
            entry.IncomingMultiplier = multiplier;
            Changed(targetId);
            CheckAbusers(target);
        }
        return true;
    }

    public bool CopyTop(uint targetId)
    {
        if (AdditionsBlocked || IsAbuser(targetId) || _world.FindTarget(targetId) is not { } target) return false;
        var entry = Find(targetId) ?? Create(target);
        if (entry is null) return false;
        var maximum = _entries.Max(value => value.Amounts.Total);
        // 3931FE00 copies the largest raw total, putting the difference in direct aggro.
        entry.Amounts = entry.Amounts with { Direct = unchecked(entry.Amounts.Direct + maximum - entry.Amounts.Total) };
        Changed(targetId);
        CheckAbusers(target);
        return true;
    }

    public bool Taunt(uint targetId, bool copyTop)
    {
        // 39320FE0/3931F710 bypass the abuser filter; Create still checks acquisition.
        if (AdditionsBlocked || _world.FindTarget(targetId) is not { } target) return false;
        var entry = Find(targetId) ?? Create(target);
        if (entry is null) return false;
        entry.TauntedAt = _world.TimeMilliseconds;
        IsDirty = true;
        if (copyTop) CopyTop(targetId);
        return true;
    }

    public bool CancelTaunt(uint targetId)
    {
        if (AdditionsBlocked || _world.FindTarget(targetId) is not { } target) return false;
        var entry = Find(targetId) ?? Create(target);
        if (entry is null) return false;
        entry.TauntedAt = 0;
        IsDirty = true;
        return true;
    }

    public void Reset(int damage, int healing, int direct, int value)
    {
        // 39320E60: selectors are truth values; replacement retains the signed value's bits.
        if (damage == 0 && healing == 0 && direct == 0 && value == 0) { Clear(17); return; }
        foreach (var entry in _entries)
        {
            entry.Amounts = new(damage != 0 ? unchecked((uint)value) : entry.Amounts.Damage,
                healing != 0 ? unchecked((uint)value) : entry.Amounts.Healing,
                direct != 0 ? unchecked((uint)value) : entry.Amounts.Direct);
            Changed(entry.TargetId);
        }
    }

    public bool Remove(uint targetId)
    {
        var index = _entries.FindIndex(entry => entry.TargetId == targetId);
        if (index < 0) return false;
        Erase(index);
        if (_entries.Count == 0) AdditionsBlocked = true;
        return true;
    }

    public void Clear() => Clear(18);

    public void Clear(int reason)
    {
        // 393200D0: an empty table or a reason >= 18 preserves the previous reason.
        if (_entries.Count != 0 && reason < 18) LastClearReason = reason;
        while (_entries.Count != 0) Remove(_entries[0].TargetId);
        _sleepAmounts.Clear();
        var hadAbusers = _abusers.Any(category => category.Count != 0);
        foreach (var category in _abusers) category.Clear();
        if (hadAbusers) _world.AbusersCleared(this);
    }

    public void RegisterAbuser(uint targetId, int category)
    {
        if ((uint)category >= 3) throw new ArgumentOutOfRangeException(nameof(category));
        _abusers[category].Add(targetId);
        _world.AbuserRegistered(this, targetId, category);
    }

    public IReadOnlyList<uint> GetAbusers(int category)
    {
        if ((uint)category >= 3) throw new ArgumentOutOfRangeException(nameof(category));
        return _abusers[category].ToArray();
    }

    public void ProcessReturn(NpcTemplateDefinition? template)
    {
        // 393211E0: special NPC kinds enforce twice the absolute radius around spawn,
        // even without aggro or with ordinary navigation return checks disabled.
        if (template?.NpcKindId is 3 or 4 or 5 or 8)
        {
            var position = _world.GetOwnerPosition(this);
            var distance = AbsoluteReturnDistance * 2f;
            var squared = distance * distance;
            if (squared > 0.1f && DistanceSquared(position, _world.GetSpawnPosition(this)) > squared)
            {
                // Native reads spawnPos again immediately before relocation.
                _world.RelocateOwner(this, _world.GetSpawnPosition(this));
                Clear(2);
                return;
            }
        }
        if (_entries.Count == 0 || !_world.ReturnChecksEnabled) return;
        var current = _world.GetOwnerPosition(this);
        if (_absoluteReturnDistanceSquared > 0.1f && DistanceSquared(current, EncounterOrigin) > _absoluteReturnDistanceSquared)
            Clear(2);
        // All three helpers run even when an earlier helper emptied the table. Clear
        // preserves that first reason, while still performing later environment queries.
        if (template?.ReturnWhenEnterHousingArea == true && _world.IsInHousingArea(current)) Clear(4);
        if (_world.IsOutsideTerritory(this, current)) Clear(1);
    }

    public void RefreshAbusers()
    {
        // 3931E570 visits categories 0 and 2 in unsigned ID order. Category 1 persists
        // until Clear. Erasure advances the native iterator twice, skipping its successor.
        var empty = _entries.Count == 0;
        var removed = new SortedSet<uint>();
        foreach (var category in new[] { 0, 2 })
        {
            var ids = _abusers[category].ToArray();
            for (var index = 0; index < ids.Length; index++)
            {
                var id = ids[index];
                var target = _world.FindTarget(id); // Native resolves even when empty.
                var remove = empty || target is null;
                if (!remove)
                    remove = category == 0
                        ? Find(id) is null || !_world.IsNavigationAbuser(target!)
                        : _world.GetTerrainHeight(target!.Position) - 1f <= target.Position.Z;
                if (!remove) continue;
                _abusers[category].Remove(id);
                removed.Add(id);
                index++;
            }
        }
        // An ID removed from one category is reported even if another retains it.
        if (removed.Count != 0) _world.AbusersUnregistered(this, removed.ToArray());
    }

    public void RefreshTargets()
    {
        // 393204B0: the group/actor service must supply actual eligibility and physics.
        var now = _world.TimeMilliseconds;
        for (var index = 0; index < _entries.Count;)
        {
            var entry = _entries[index];
            if (_world.FindTarget(entry.TargetId) is not { } target || !_world.CanRetain(this, target))
            {
                Remove(entry.TargetId);
                continue;
            }
            if (!target.Sleeping)
            {
                if (_sleepAmounts.Remove(entry.TargetId, out var saved))
                {
                    entry.Amounts = entry.Amounts.Add(saved);
                    Changed(entry.TargetId); // original flags are NOT restored
                }
            }
            else if (entry.Amounts.Total > 1)
            {
                _sleepAmounts.TryAdd(entry.TargetId, entry.Amounts);
                entry.Amounts = new(0, 0, 1);
                entry.Flags = 0;
                Changed(entry.TargetId);
            }
            if (!(MinimumScore <= entry.ScoreAt(_world.TimeMilliseconds)) || _world.ShouldCancel(this, entry, target, now))
            {
                Remove(entry.TargetId);
                continue;
            }
            index++;
        }
    }

    public void UpdateSelection()
    {
        // 3931F790: sorting occurs only after a mutation, not simply because time elapsed.
        if (_entries.Count != 0 && IsDirty)
        {
            SortAndSelectComponents();
            IsDirty = false;
        }
        var selected = _entries.Count == 0 ? 0 : _entries[0].TargetId;
        if (selected != TargetId)
        {
            var previous = TargetId;
            TargetId = selected;
            _world.TargetChanged(this, previous);
        }
        AdditionsBlocked = false;
    }

    public uint GetComponentTarget(uint component, int rank = 0)
    {
        // 3931F3C0: positive ranks exclude the component's currently flagged entry.
        if (component >= 3 || _entries.Count == 0) return 0;
        var index = (int)component;
        if (rank > 0)
        {
            var candidates = _entries.Where(entry => (entry.Flags & (1 << index)) == 0 && entry.Amounts[index] != 0).ToList();
            if (candidates.Count == 0) return _componentTargets[index];
            ZoneAggroOrdering.Sort(candidates, 0, candidates.Count, entry => entry.Amounts[index]);
            var targetId = candidates[Math.Min(rank, candidates.Count) - 1].TargetId;
            return _world.FindTarget(targetId) is null ? 0 : targetId;
        }
        var target = _componentTargets[index];
        return target == 0 && _entries[0].Amounts[index] != 0 ? _entries[0].TargetId : target;
    }

    public void CopyFrom(ZoneAggroTable source)
    {
        // 39321A10: empty source is a no-op; self-copy erases the table. Copy uses Add,
        // reapplying outgoing modifiers and admission/threshold rules to source amounts.
        if (source._entries.Count == 0) return;
        var previous = _entries.Select(entry => entry.Copy()).ToArray();
        var origin = EncounterOrigin;
        while (_entries.Count != 0) Erase(0); // low-level erasure does not block additions
        foreach (var entry in source._entries)
        {
            Add(entry.TargetId, entry.Amounts);
            if (Find(entry.TargetId) is not { } copied) continue;
            if (source._sleepAmounts.TryGetValue(entry.TargetId, out var saved)) _sleepAmounts.TryAdd(entry.TargetId, saved);
            copied.Flags = entry.Flags;
        }
        if (previous.Length == 0) return;
        EncounterOrigin = origin;
        foreach (var old in previous)
        {
            if (Find(old.TargetId) is not { } entry) continue;
            entry.OriginAtCreation = old.OriginAtCreation;
            entry.TargetPositionAtCreation = old.TargetPositionAtCreation;
            entry.CreatedAt = old.CreatedAt;
            entry.UpdatedAt = old.UpdatedAt;
            break; // native jumps out after the first overlap, not after every match
        }
    }

    public void MirrorFrom(ZoneAggroTable source, uint targetId)
    {
        // 3931FA10 group replication copies all defined entry fields without rescaling.
        // The caller owns the native group reentry guard and member traversal order.
        var original = source.Find(targetId);
        if (original is null) { Remove(targetId); return; }
        var current = Find(targetId);
        if (current is null)
        {
            if (_world.FindTarget(targetId) is not { } target) return;
            current = Create(target);
            if (current is null) return;
        }
        _entries[_entries.IndexOf(current)] = original.Copy();
        Changed(targetId);
    }

    private ZoneAggroEntry? Create(ZoneAggroTarget target)
    {
        if (target.ObjectId == OwnerObjectId || !_world.CanAcquire(this, target)) return null;
        var origin = _world.GetEncounterOrigin(this);
        var now = _world.TimeMilliseconds;
        var entry = new ZoneAggroEntry
        {
            TargetId = target.ObjectId, IncomingMultiplier = Multiplier(_world.GetIncomingModifier(this)),
            DistanceRatio = DistanceRatio(target), OriginAtCreation = origin,
            TargetPositionAtCreation = target.Position, CreatedAt = now, UpdatedAt = now
        };
        if (_entries.Count == Capacity) Erase(_entries.Count - 1);
        if (_entries.Count == 0)
        {
            EncounterOrigin = origin;
            entry.Flags = 0x87; // 39F6BE90 initializes 3A6062A3 = 0x80 | 1 | 2 | 4
        }
        _entries.Add(entry);
        _world.EntryCreated(this, entry);
        Changed(target.ObjectId);
        return entry;
    }

    private void Erase(int index)
    {
        var targetId = _entries[index].TargetId;
        _sleepAmounts.Remove(targetId);
        _entries.RemoveAt(index);
        _world.EntryRemoved(this, targetId);
        Changed(targetId);
    }

    private void Changed(uint targetId)
    {
        IsDirty = true;
        _world.EntryChanged(this, targetId, true);
    }

    private void CheckAbusers(ZoneAggroTarget target)
    {
        var now = _world.TimeMilliseconds;
        if (unchecked(now - _lastAbuserCheck) <= 1000) return;
        _lastAbuserCheck = now;
        _world.CheckAbusers(this, target);
    }

    private float DistanceRatio(ZoneAggroTarget target)
    {
        var origin = _world.GetEncounterOrigin(this);
        var x = target.Position.X - origin.X;
        var y = target.Position.Y - origin.Y;
        var z = target.Position.Z - origin.Z;
        // Keep the scalar operation order used by 3931D6F0.
        return x * x + y * y + z * z < 100f ? 0.9090908765792847f : 0.8333333134651184f;
    }

    private static float Multiplier(int modifier) => ((float)modifier + 100f) / 100f;

    private static float DistanceSquared(Vector3 first, Vector3 second)
    {
        var x = first.X - second.X; var y = first.Y - second.Y; var z = first.Z - second.Z;
        return x * x + y * y + z * z;
    }

    private void SortAndSelectComponents()
    {
        if (_entries.Count < 2) return;
        var first = _entries[0];
        for (var index = 1; index < _entries.Count; index++)
        {
            var candidate = _entries[index];
            if (!(first.TauntedAt < candidate.TauntedAt || first.TauntedAt == 0 &&
                (float)first.WeightedTotal < (float)candidate.WeightedTotal * candidate.DistanceRatio)) continue;
            candidate.Flags |= 0x80;
            _world.EntryChanged(this, candidate.TargetId, false);
            (_entries[0], _entries[index]) = (candidate, first);
            first.Flags &= 0x7f;
            _world.EntryChanged(this, first.TargetId, false);
            break; // one challenger before sorting the remainder, even if another is larger
        }
        ZoneAggroOrdering.Sort(_entries, 1, _entries.Count, entry => entry.WeightedTotal);
        var incumbents = Enumerable.Range(0, 3).Select(component =>
            _entries.FirstOrDefault(entry => (entry.Flags & (1 << component)) != 0) ?? _entries[0]).ToArray();
        // Native visits healing, direct and then damage. It selects the first qualifying entry.
        foreach (var component in new[] { 1, 2, 0 })
        {
            var incumbent = incumbents[component];
            foreach (var candidate in _entries)
            {
                var exceeds = component == 0
                    ? (float)incumbent.Amounts.Damage < (float)candidate.Amounts.Damage * candidate.DistanceRatio
                    : (float)incumbent.Amounts[component] * 1.1f < (float)candidate.Amounts[component];
                if (!exceeds) continue;
                var bit = (byte)(1 << component);
                candidate.Flags |= bit;
                incumbent.Flags &= (byte)~bit;
                _componentTargets[component] = candidate.TargetId;
                _world.EntryChanged(this, candidate.TargetId, false);
                _world.EntryChanged(this, incumbent.TargetId, false);
                break;
            }
        }
    }
}
