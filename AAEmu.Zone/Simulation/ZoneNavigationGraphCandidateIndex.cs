using System.Numerics;
namespace AAEmu.Zone.Simulation;

/// <summary>
/// Native graph's 8192-bucket candidate index (3110A494/3110A570/3109366C).
/// Cell coordinates use float multiplication by1/7, x86 integer conversion and packed
/// 12/12/8-bit keys. This index owns graph admission, independently of raw pool allocation.
/// </summary>
internal sealed class ZoneNavigationGraphCandidateIndex(ZoneNavigationGraphNodePool pool)
{
    private readonly record struct Entry(uint Key, uint Id);
    private readonly Dictionary<uint, ZoneNavigationGraphNode> _admitted = [];
    private readonly List<Entry>?[] _buckets = new List<Entry>?[8192];
    private const float InverseCellWidth = 1f / 7f;
    public void Add(ZoneNavigationGraphNode node)
    {
        _admitted[node.RuntimeId] = node;
        var x = Cell(node.Position.X); var y = Cell(node.Position.Y); var z = Cell(node.Position.Z);
        (_buckets[Bucket(x, y, z)] ??= []).Add(new Entry(Key(x, y, z), node.RuntimeId));
    }
    public void Remove(ZoneNavigationGraphNode node)
    {
        if (_admitted.TryGetValue(node.RuntimeId, out var admitted) && ReferenceEquals(admitted, node))
            _admitted.Remove(node.RuntimeId);
        // The native removal helper converts to signed64-bit first, unlike insertion/query.
        var bucket = _buckets[Bucket(RemovalCell(node.Position.X), RemovalCell(node.Position.Y), RemovalCell(node.Position.Z))];
        if (bucket is null) return;
        for (var i = 0; i < bucket.Count;)
        {
            if (bucket[i].Id != node.RuntimeId) { i++; continue; }
            bucket[i] = bucket[^1]; bucket.RemoveAt(bucket.Count - 1);
        }
    }
    public ZoneNavigationGraphNode? FindReusable(Vector3 position, uint navigationType)
    {
        var candidates = new List<(float Distance, ZoneNavigationGraphNode Node)>();
        var minX = Cell(position.X - 0.1f); var maxX = Cell(position.X + 0.1f);
        var minY = Cell(position.Y - 0.1f); var maxY = Cell(position.Y + 0.1f);
        var minZ = Cell(position.Z - 0.1f); var maxZ = Cell(position.Z + 0.1f);
        for (var x = minX; x <= maxX; x++)
        for (var y = minY; y <= maxY; y++)
        for (var z = minZ; z <= maxZ; z++)
        {
            var bucket = _buckets[Bucket(x, y, z)];
            if (bucket is null) continue;
            var key = Key(x, y, z);
            foreach (var entry in bucket)
            {
                if (entry.Key != key) continue;
                // Native extreme-coordinate removal can leave a stale bucket entry. Reject
                // released/unregistered storage here; native can return an already freed slot
                // or a raw pool replacement that has not been admitted to the graph.
                var node = pool.Get(entry.Id);
                if (node is null || !_admitted.TryGetValue(entry.Id, out var admitted) ||
                    !ReferenceEquals(node, admitted) || (node.NavigationType & 14) == 0) continue;
                var dx = position.X - node.Position.X;
                var dy = position.Y - node.Position.Y;
                var dz = position.Z - node.Position.Z;
                var squared = dy * dy + dx * dx;
                squared += dz * dz;
                if (squared < 0.1f * 0.1f) candidates.Add((squared, node));
            }
        }
        candidates.Sort((a, b) => a.Distance < b.Distance ? -1 : a.Distance > b.Distance ? 1 : a.Node.RuntimeId.CompareTo(b.Node.RuntimeId));
        foreach (var (_, node) in candidates)
        {
            var x = node.Position.X - position.X; var y = node.Position.Y - position.Y;
            if ((double)(y * y + x * x) < 1e-8 && node.NavigationType == navigationType) return node;
        }
        return null;
    }
    private static int Cell(float value)
    {
        var scaled = MathF.Floor(value * InverseCellWidth);
        return float.IsNaN(scaled) || scaled >= 2147483648f || scaled < -2147483648f ? int.MinValue : (int)scaled;
    }
    private static int RemovalCell(float value)
    {
        var scaled = MathF.Floor(value * InverseCellWidth);
        return float.IsNaN(scaled) || scaled >= 9223372036854775808f || scaled < -9223372036854775808f
            ? 0 : unchecked((int)(long)scaled);
    }
    private static int Bucket(int x, int y, int z) => unchecked(y * -0x27e9c7bf - x * 0x72594cbd - z * 0x34e54ce1) & 8191;
    private static uint Key(int x, int y, int z) => unchecked((uint)(((z << 12 | y & 0xfff) << 12) | x & 0xfff));
}
