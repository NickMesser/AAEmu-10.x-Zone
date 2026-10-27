using System.Collections.ObjectModel;

namespace AAEmu.Zone.Startup;

/// <summary>Collects an initial World snapshot by its native identity, including replacement records.</summary>
internal sealed class ChunkedSnapshot<TKey, TValue> where TKey : notnull
{
    // Host resource policy, not a native wire limit. No allocation is based on an untrusted total.
    internal const int MaxRecords = 1_000_000;
    private readonly Dictionary<TKey, TValue> _entries = [];
    private readonly Func<TValue, TKey> _keySelector;
    private uint? _total;

    public bool IsComplete => _total.HasValue && _entries.Count == _total.Value;
    public IReadOnlyDictionary<TKey, TValue> Entries { get; }

    public ChunkedSnapshot(Func<TValue, TKey> keySelector)
    {
        _keySelector = keySelector;
        Entries = new ReadOnlyDictionary<TKey, TValue>(_entries);
    }

    public void Add(uint total, IReadOnlyList<TValue> entries)
    {
        if (total > MaxRecords || (_total.HasValue && total != _total.Value))
            throw new InvalidDataException("World changed a snapshot total or exceeded the Zone snapshot capacity.");
        var incoming = new Dictionary<TKey, TValue>();
        foreach (var entry in entries)
            incoming[_keySelector(entry)] = entry;
        var added = incoming.Keys.Count(key => !_entries.ContainsKey(key));
        if (_entries.Count + added > total)
            throw new InvalidDataException("World snapshot contains more distinct records than its declared total.");
        foreach (var (key, value) in incoming)
            _entries[key] = value;
        _total = total;
    }
}
