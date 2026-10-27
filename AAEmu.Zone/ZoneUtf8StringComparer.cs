using System.Text;

namespace AAEmu.Zone;

internal sealed class ZoneUtf8StringComparer : IComparer<string>
{
    public static readonly ZoneUtf8StringComparer Instance = new();
    public int Compare(string? left, string? right) => Encoding.UTF8.GetBytes(left ?? "").AsSpan()
        .SequenceCompareTo(Encoding.UTF8.GetBytes(right ?? ""));
}
