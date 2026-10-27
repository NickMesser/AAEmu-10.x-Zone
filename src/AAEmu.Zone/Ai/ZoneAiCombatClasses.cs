using System.Text;

namespace AAEmu.Zone.Ai;

/// <summary>Native AI assessment factors and per-class perception signals.</summary>
public sealed class ZoneAiCombatClasses
{
    private readonly object _gate = new();
    private readonly Dictionary<int, Entry> _entries = [];
    private int _count;
    private sealed record Entry(IReadOnlyList<float> Factors, string? Signal);

    public int Count { get { lock (_gate) return _count; } }

    // 31079428: an empty row resets the entire vector, independently of the supplied class ID.
    public void SetClass(int classId, ReadOnlySpan<float> factors, string? signal = null)
    {
        lock (_gate)
        {
            if (factors.Length == 0) { _entries.Clear(); _count = 0; return; }
            ArgumentOutOfRangeException.ThrowIfNegative(classId);
            var count = checked(classId + 1);
            signal = signal is null ? null : ZoneAiSignalName.CString(signal);
            if (signal is not null && Encoding.UTF8.GetByteCount(signal) <= 1) signal = null;
            _entries[classId] = new(Array.AsReadOnly(factors.ToArray()), signal);
            _count = Math.Max(_count, count);
        }
    }

    public void Clear() => SetClass(0, []);

    public IReadOnlyList<float> GetFactors(int classId)
    {
        lock (_gate) return _entries.TryGetValue(classId, out var entry) ? entry.Factors : Array.Empty<float>();
    }

    // 3104ABD8: missing rows, negative IDs and columns beyond a row all use scale 1.
    public float GetScale(int classId, int targetClassId)
    {
        lock (_gate)
            return _entries.TryGetValue(classId, out var entry) && targetClassId >= 0 && targetClassId < entry.Factors.Count
                ? entry.Factors[targetClassId] : 1f;
    }

    // 3104E988: empty/default entries and invalid IDs have no signal.
    public string? GetSignal(int classId)
    {
        lock (_gate) return _entries.TryGetValue(classId, out var entry) ? entry.Signal : null;
    }
}
