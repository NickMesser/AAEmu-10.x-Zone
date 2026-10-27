using System.Text;

namespace AAEmu.Zone.Protocol.Messages;

/// <summary>
/// An owned binary name with C-locale default equality. Filter containers use their
/// explicit native locale comparer. UTF-8 decoding and NUL termination do not apply.
/// </summary>
public sealed class WorldContentName : IEquatable<WorldContentName>, IComparable<WorldContentName>
{
    private readonly byte[] _bytes;
    public ReadOnlySpan<byte> Bytes => _bytes;

    public WorldContentName(ReadOnlySpan<byte> bytes) => _bytes = bytes.ToArray();
    public static WorldContentName FromUtf8(string name) => new(Encoding.UTF8.GetBytes(name));

    public int CompareTo(WorldContentName? other) => WorldContentNameComparer.C.Compare(this, other);
    public bool Equals(WorldContentName? other) => other != null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is WorldContentName other && Equals(other);
    public override int GetHashCode() => WorldContentNameComparer.C.GetHashCode(this);
}
