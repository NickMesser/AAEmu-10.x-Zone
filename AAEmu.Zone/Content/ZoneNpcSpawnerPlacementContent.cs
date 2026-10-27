using System.Numerics;
using System.Text;
using AAEmu.Zone.Protocol.Messages;

namespace AAEmu.Zone.Content;

public enum ZoneNpcSpawnAreaType { Point, Area }
public sealed record ZoneNpcAuthoredPoint(Vector3 Position, float ZRotation);
public sealed record ZoneNpcAuthoredTriangle(Vector3 V1, Vector3 V2, Vector3 V3, float AreaRate);

// Native fields are100-byte C strings. Preserve the99 source bytes even if truncation splits UTF-8.
internal sealed class ZoneNpcAuthoredText
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private readonly byte[] _bytes;
    private readonly string? _text;
    internal ReadOnlySpan<byte> Bytes => _bytes;
    internal bool IsUtf8 => _text is not null;
    internal string Text => _text ?? throw new InvalidDataException("The authored name is not valid UTF-8; its native bytes remain available.");
    internal ZoneNpcAuthoredText(ReadOnlySpan<byte> value)
    {
        var zero = value.IndexOf((byte)0);
        if (zero >= 0) value = value[..zero];
        _bytes = value[..Math.Min(value.Length, 99)].ToArray();
        try { _text = Utf8.GetString(_bytes); }
        catch (DecoderFallbackException) { }
    }
}

public sealed class ZoneNpcAuthoredPath
{
    private readonly ZoneNpcAuthoredText _name;
    public string Name => _name.Text;
    public ReadOnlySpan<byte> NameBytes => _name.Bytes;
    public bool HasUtf8Name => _name.IsUtf8;
    public int PointNumber { get; }
    internal ZoneNpcAuthoredPath(ReadOnlySpan<byte> name, int pointNumber) { _name = new(name); PointNumber = pointNumber; }
}

public sealed class ZoneNpcAuthoredAnchor
{
    private readonly ZoneNpcAuthoredText _name;
    public string Name => _name.Text;
    public ReadOnlySpan<byte> NameBytes => _name.Bytes;
    public bool HasUtf8Name => _name.IsUtf8;
    public int AnchorType { get; }
    public Vector3 Position { get; }
    internal ZoneNpcAuthoredAnchor(ReadOnlySpan<byte> name, int anchorType, Vector3 position)
    { _name = new(name); AnchorType = anchorType; Position = position; }
}

public sealed class ZoneNpcAuthoredPlacement
{
    public IReadOnlyList<ZoneNpcAuthoredPoint> Points { get; }
    public IReadOnlyList<ZoneNpcAuthoredTriangle> Triangles { get; }
    internal ZoneNpcAuthoredPlacement(IEnumerable<ZoneNpcAuthoredPoint> points, IEnumerable<ZoneNpcAuthoredTriangle> triangles)
    { Points = Array.AsReadOnly(points.ToArray()); Triangles = Array.AsReadOnly(triangles.ToArray()); }
}

public sealed class ZoneNpcAuthoredInitialBehavior
{
    public IReadOnlyList<ZoneNpcAuthoredPath> Paths { get; }
    public IReadOnlyList<ZoneNpcAuthoredAnchor> Anchors { get; }
    internal ZoneNpcAuthoredInitialBehavior(IEnumerable<ZoneNpcAuthoredPath> paths, IEnumerable<ZoneNpcAuthoredAnchor> anchors)
    { Paths = Array.AsReadOnly(paths.ToArray()); Anchors = Array.AsReadOnly(anchors.ToArray()); }
}

public sealed class ZoneNpcSpawnerPlacementRow
{
    private readonly ZoneNpcAuthoredText _roaming, _combat;
    public int SourceIndex { get; }
    public uint SpawnerId { get; }
    public uint SpawnerType { get; }
    public ZoneNpcSpawnAreaType AreaType { get; }
    public string RoamingArea => _roaming.Text;
    public string CombatArea => _combat.Text;
    public ReadOnlySpan<byte> RoamingAreaBytes => _roaming.Bytes;
    public ReadOnlySpan<byte> CombatAreaBytes => _combat.Bytes;
    public bool HasUtf8AreaNames => _roaming.IsUtf8 && _combat.IsUtf8;
    public ZoneNpcAuthoredPlacement Placement { get; }
    public ZoneNpcAuthoredInitialBehavior InitialBehavior { get; }
    internal ZoneNpcSpawnerPlacementRow(int sourceIndex, uint spawnerId, uint spawnerType, ZoneNpcSpawnAreaType areaType,
        ReadOnlySpan<byte> roaming, ReadOnlySpan<byte> combat, ZoneNpcAuthoredPlacement placement, ZoneNpcAuthoredInitialBehavior behavior)
    {
        SourceIndex = sourceIndex; SpawnerId = spawnerId; SpawnerType = spawnerType; AreaType = areaType;
        _roaming = new(roaming); _combat = new(combat); Placement = placement; InitialBehavior = behavior;
    }
}

public enum ZoneNpcSpawnerPlacementRejection { UnknownAreaType, MissingPoints, MissingTriangles }
public sealed record ZoneNpcRejectedPlacement(int SourceIndex, uint SpawnerId, ZoneNpcSpawnerPlacementRejection Reason);

/// <summary>
/// Native3934F380 authored parameters in file order, using the actual Data grammar rather than XML.
/// Content resolution, persistence eligibility and runtime manager registration are separate phases.
/// String views require valid UTF-8; their original bounded native bytes are always retained.
/// </summary>
public sealed class ZoneNpcSpawnerPlacementContent
{
    public IReadOnlyList<ZoneNpcSpawnerPlacementRow> Rows { get; }
    public IReadOnlyList<ZoneNpcRejectedPlacement> RejectedRows { get; }
    private ZoneNpcSpawnerPlacementContent(List<ZoneNpcSpawnerPlacementRow> rows, List<ZoneNpcRejectedPlacement> rejected)
    { Rows = rows.AsReadOnly(); RejectedRows = rejected.AsReadOnly(); }

    public static ZoneNpcSpawnerPlacementContent LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static ZoneNpcSpawnerPlacementContent Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return Read(copy.GetBuffer().AsSpan(0, checked((int)copy.Length)));
    }

    public static ZoneNpcSpawnerPlacementContent Read(ReadOnlySpan<byte> document)
    {
        // Data::LoadFromFile rejects zero-byte files; its explicit-length parser does not strnlen.
        if (document.IsEmpty || !GmCommandData.TryReadDocument(document, out var root))
            throw new InvalidDataException("The authored NPC spawner file has invalid Data syntax or is empty.");
        var rows = new List<ZoneNpcSpawnerPlacementRow>();
        var rejected = new List<ZoneNpcRejectedPlacement>();
        for (var index = 0; index < root!.Children.Count; index++)
        {
            var node = root.Children[index];
            var id = unchecked((uint)Integer(node, "spawnerId"u8));
            var kind = Text(node, "spawnAreaType"u8);
            var point = EqualsIgnoreAsciiCase(kind, "point"u8);
            if (!point && !EqualsIgnoreAsciiCase(kind, "area"u8))
            { rejected.Add(new(index, id, ZoneNpcSpawnerPlacementRejection.UnknownAreaType)); continue; }
            var points = Find(node, "points"u8);
            if (points is null) { rejected.Add(new(index, id, ZoneNpcSpawnerPlacementRejection.MissingPoints)); continue; }
            var triangles = Find(node, "triInfos"u8);
            if (!point && triangles is null)
            { rejected.Add(new(index, id, ZoneNpcSpawnerPlacementRejection.MissingTriangles)); continue; }
            var placement = new ZoneNpcAuthoredPlacement(
                points.Children.Select(value => new ZoneNpcAuthoredPoint(Vector(Find(value, "pos"u8)), Single(value, "zRot"u8))),
                triangles?.Children.Select(value => new ZoneNpcAuthoredTriangle(Vector(Find(value, "v1"u8)),
                    Vector(Find(value, "v2"u8)), Vector(Find(value, "v3"u8)), Single(value, "areaRate"u8))) ?? []);
            var behavior = new ZoneNpcAuthoredInitialBehavior(
                Find(node, "paths"u8)?.Children.Select(value => new ZoneNpcAuthoredPath(Text(value, "pathName"u8), Integer(value, "pointNo"u8))) ?? [],
                Find(node, "anchors"u8)?.Children.Select(value => new ZoneNpcAuthoredAnchor(Text(value, "anchorName"u8),
                    Integer(value, "anchorType"u8), Vector(Find(value, "pos"u8)))) ?? []);
            rows.Add(new(index, id, unchecked((uint)Integer(node, "spawnerType"u8)),
                point ? ZoneNpcSpawnAreaType.Point : ZoneNpcSpawnAreaType.Area,
                Text(node, "roamingArea"u8), Text(node, "combatArea"u8), placement, behavior));
        }
        return new(rows, rejected);
    }

    // Data::Find330069A0 returns the first case-sensitive direct match for these literal keys.
    private static GmCommandData? Find(GmCommandData parent, ReadOnlySpan<byte> name)
    {
        foreach (var child in parent.Children) if (CString(child.Value).SequenceEqual(name)) return child;
        return null;
    }
    private static GmCommandData? Attribute(GmCommandData parent, ReadOnlySpan<byte> name) =>
        Find(parent, name) is { Children.Count: > 0 } attribute ? attribute.Children[0] : null;
    private static ReadOnlySpan<byte> Text(GmCommandData node, ReadOnlySpan<byte> name) =>
        Attribute(node, name) is { } value ? CString(value.Value) : [];
    private static int Integer(GmCommandData node, ReadOnlySpan<byte> name) => Attribute(node, name)?.Int32Value ?? 0;
    private static float Single(GmCommandData node, ReadOnlySpan<byte> name) => XlSingle(Text(node, name));
    private static Vector3 Vector(GmCommandData? node) => node is null ? Vector3.Zero : new(Single(node, "x"u8), Single(node, "y"u8), Single(node, "z"u8));
    private static ReadOnlySpan<byte> CString(ReadOnlySpan<byte> value)
    { var zero = value.IndexOf((byte)0); return zero < 0 ? value : value[..zero]; }
    private static bool EqualsIgnoreAsciiCase(ReadOnlySpan<byte> value, ReadOnlySpan<byte> expected)
    {
        if (value.Length != expected.Length) return false;
        for (var index = 0; index < value.Length; index++)
            if ((value[index] is >= (byte)'A' and <= (byte)'Z' ? value[index] + 32 : value[index]) != expected[index]) return false;
        return true;
    }

    // XlAtoF64 33020EE0 then XlAtoF32 330214E0. Decimal prefixes and exponent arithmetic
    // intentionally retain the native overflow/underflow and rounding behavior.
    private static float XlSingle(ReadOnlySpan<byte> value)
    {
        var index = 0;
        while (index < value.Length && value[index] is 9 or 10 or 11 or 12 or 13 or 32) index++;
        var negative = index < value.Length && value[index] == '-';
        if (index < value.Length && value[index] is (byte)'+' or (byte)'-') index++;
        var number = 0d;
        var exponent = 0;
        var limit = BitConverter.UInt64BitsToDouble(0x7FB9999999999999);
        while (index < value.Length && value[index] is >= (byte)'0' and <= (byte)'9')
        {
            if (number <= limit) number = (value[index] - '0') + number * 10d;
            else exponent = unchecked(exponent + 1);
            index++;
        }
        if (index < value.Length && value[index] == '.')
        {
            index++;
            while (index < value.Length && value[index] is >= (byte)'0' and <= (byte)'9')
            {
                if (number <= limit) { number = (value[index] - '0') + number * 10d; exponent = unchecked(exponent - 1); }
                index++;
            }
        }
        if (index < value.Length && value[index] is (byte)'e' or (byte)'E')
        {
            index++;
            var negativeExponent = index < value.Length && value[index] == '-';
            if (index < value.Length && value[index] is (byte)'+' or (byte)'-') index++;
            var explicitExponent = 0;
            while (index < value.Length && value[index] is >= (byte)'0' and <= (byte)'9')
                explicitExponent = unchecked(value[index++] - '0' + explicitExponent * 10);
            exponent = unchecked(exponent + (negativeExponent ? -explicitExponent : explicitExponent));
        }
        var remaining = unchecked((uint)(exponent < 0 ? -exponent : exponent));
        var power = 1d;
        var basis = 10d;
        while (true)
        {
            if ((remaining & 1) != 0) power *= basis;
            remaining >>= 1;
            if (remaining == 0) break;
            basis *= basis;
        }
        if (exponent < 0) power = 1d / power;
        var result = number * power;
        return (float)(negative ? -result : result);
    }
}
