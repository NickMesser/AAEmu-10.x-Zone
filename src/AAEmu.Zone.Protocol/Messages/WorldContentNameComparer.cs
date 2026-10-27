namespace AAEmu.Zone.Protocol.Messages;

/// <summary>
/// Byte case tables from MSVCR100 LC_CTYPE and xlcommon XlInitStringUtil (3301FB80).
/// These preserve native code-page byte folding without changing process/thread locale.
/// </summary>
public sealed class WorldContentNameComparer : IEqualityComparer<WorldContentName>, IComparer<WorldContentName>
{
    private readonly byte[] _lower;
    public static WorldContentNameComparer C { get; } = new(0);
    private static readonly WorldContentNameComparer Western = new(1252);
    private static readonly WorldContentNameComparer Cyrillic = new(1251);

    public static WorldContentNameComparer ForLocale(string locale) => locale.ToLowerInvariant() switch
    {
        "c" or "ko" or "zh_cn" or "ja" or "zh_tw" or "th" => C,
        "en_us" or "en_sg" or "de" or "fr" or "ind" => Western,
        "ru" => Cyrillic,
        _ => throw new ArgumentException("Unknown native World content locale.", nameof(locale))
    };

    private WorldContentNameComparer(int codePage)
    {
        _lower = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
        for (var value = 0x41; value <= 0x5A; value++) _lower[value] = (byte)(value + 32);
        if (codePage == 0) return;
        for (var value = 0xC0; value <= (codePage == 1251 ? 0xDF : 0xDE); value++)
            if (codePage == 1251 || value != 0xD7) _lower[value] = (byte)(value + 32);
        (byte Upper, byte Lower)[] pairs = codePage == 1252
            ? [(0x8A, 0x9A), (0x8C, 0x9C), (0x8E, 0x9E), (0x9F, 0xFF)]
            : [(0x80, 0x90), (0x81, 0x83), (0x8A, 0x9A), (0x8C, 0x9C), (0x8D, 0x9D), (0x8E, 0x9E),
                (0x8F, 0x9F), (0xA1, 0xA2), (0xA3, 0xBC), (0xA5, 0xB4), (0xA8, 0xB8), (0xAA, 0xBA),
                (0xAF, 0xBF), (0xB2, 0xB3), (0xBD, 0xBE)];
        foreach (var (upper, lower) in pairs) _lower[upper] = lower;
    }

    public int Compare(WorldContentName? first, WorldContentName? second)
    {
        if (ReferenceEquals(first, second)) return 0;
        if (first == null) return -1;
        if (second == null) return 1;
        var a = first.Bytes; var b = second.Bytes;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var comparison = _lower[a[i]].CompareTo(_lower[b[i]]);
            if (comparison != 0) return comparison;
        }
        return a.Length.CompareTo(b.Length);
    }

    public bool Equals(WorldContentName? first, WorldContentName? second) => Compare(first, second) == 0;
    public int GetHashCode(WorldContentName name)
    {
        var hash = new HashCode();
        foreach (var value in name.Bytes) hash.Add(_lower[value]);
        return hash.ToHashCode();
    }
}
