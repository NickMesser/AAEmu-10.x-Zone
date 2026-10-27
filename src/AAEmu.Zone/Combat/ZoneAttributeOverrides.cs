using System.Collections.Frozen;
using AAEmu.Zone.Protocol.Units;

namespace AAEmu.Zone.Combat;

/// <summary>Connection-owned GM overrides. Absence and an explicit zero are distinct native states.</summary>
public sealed class ZoneAttributeOverrides
{
    public static ZoneAttributeOverrides Empty { get; } = new([]);
    public IReadOnlyDictionary<uint, long> Values { get; }

    public ZoneAttributeOverrides(IEnumerable<KeyValuePair<uint, long>> values)
    {
        var owned = values.ToDictionary();
        foreach (var attribute in owned.Keys) ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(attribute, NativeAttributeNames.Capacity);
        Values = owned.ToFrozenDictionary();
    }

    public ZoneAttributeOverrides Change(uint attribute, long? value)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(attribute, NativeAttributeNames.Capacity);
        var next = Values.ToDictionary();
        if (value is { } enabledValue) next[attribute] = enabledValue;
        else next.Remove(attribute);
        return new(next);
    }
}
