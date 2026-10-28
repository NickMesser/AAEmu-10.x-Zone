using System.Numerics;
using AAEmu.Zone.Protocol.Units;

namespace AAEmu.Zone.Units;

public enum ZoneSpatialCategory : short { Character = 0, Npc = 1 }

/// <summary>Inclusive terrain region bounds passed to native 39D2D990; each region spans 1024 units.</summary>
public readonly record struct ZoneSpatialRegionBounds(int MinimumX, int MinimumY, int MaximumX, int MaximumY);

/// <summary>A snapshot of an owned spatial tag and its cached query position.</summary>
public readonly record struct ZoneSpatialMembership(ZoneSpatialCategory Category, int CellX, int CellY, int Index, Vector3 Position);

/// <summary>
/// The two unit categories of the native spatial grid at 3A607A68. Registration appends to a cell;
/// removal swaps its last entry into the vacated slot; movement across cells removes then appends.
/// Positions must come from the live model (392589F0), in the same coordinate space as queries.
/// Terrain minima size the grid; native indexing does not subtract them from positions.
/// </summary>
public sealed class ZoneUnitSpatialIndex : IDisposable
{
    public const int LoadedWorldCellSize = 64; // Native world initialization, 39332730 -> 39D2D990.
    private sealed class Entry(ZoneUnit unit, ZoneSpatialCategory category, int x, int y, int index, Vector3 position)
    {
        public ZoneUnit Unit { get; } = unit;
        public ZoneSpatialCategory Category { get; } = category;
        public int X { get; set; } = x;
        public int Y { get; set; } = y;
        public int Index { get; set; } = index;
        public Vector3 Position { get; set; } = position;
    }

    private readonly Dictionary<ZoneUnit, Entry> _members = [];
    private readonly Dictionary<(ZoneSpatialCategory, int, int), List<Entry>> _cells = [];
    private readonly float _inverseCellSize;
    private ZoneUnitRegistry? _registry;
    public int CellSize { get; }
    public int CellCountX { get; }
    public int CellCountY { get; }
    public int Count => _members.Count;
    public bool IsDisposed { get; private set; }

    public ZoneUnitSpatialIndex(int cellSize, ZoneSpatialRegionBounds bounds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cellSize, 1);
        CellSize = cellSize;
        CellCountX = Dimension(bounds.MinimumX, bounds.MaximumX, cellSize);
        CellCountY = Dimension(bounds.MinimumY, bounds.MaximumY, cellSize);
        if ((long)CellCountX * CellCountY * 2 > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(bounds), "Spatial bounds overflow the native cell allocation count.");
        _inverseCellSize = 1f / cellSize;
    }

    // Managed connection ownership: clearing an index does not make it shareable with another registry.
    internal void Claim(ZoneUnitRegistry registry)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (_registry is not null) throw new InvalidOperationException("The spatial index already belongs to a unit registry.");
        _registry = registry;
    }

    private static int Dimension(int minimum, int maximum, int cellSize)
    {
        var size = ((long)maximum - minimum + 1) * 1024;
        var cells = size / cellSize;
        // Replacement validation: reject native allocation overflow and signed-short tag corruption.
        if (size <= 0 || size > int.MaxValue || cells is < 1 or > 32768)
            throw new ArgumentOutOfRangeException(nameof(maximum), "Spatial bounds must produce 1 through 32768 cells per axis.");
        return (int)cells;
    }

    /// <summary>39D2DBC0. Repeated registration leaves both order and cached position unchanged.</summary>
    public bool Register(ZoneUnit unit, Vector3 position)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(unit);
        if (_members.ContainsKey(unit)) return false;
        var category = unit.Snapshot.Identity.Kind switch
        {
            ZoneUnitKind.Character => ZoneSpatialCategory.Character,
            ZoneUnitKind.Npc => ZoneSpatialCategory.Npc,
            _ => (ZoneSpatialCategory)(-1)
        };
        if ((short)category < 0) return false; // Only these two creation paths register native unit tags.
        var x = Cell(position.X, CellCountX); var y = Cell(position.Y, CellCountY);
        var entries = GetCell(category, x, y);
        CheckCapacity(entries);
        var entry = new Entry(unit, category, x, y, entries.Count, position);
        entries.Add(entry); _members.Add(unit, entry);
        return true;
    }

    /// <summary>39D2DD60. An unregistered unit remains absent; updates do not register it implicitly.</summary>
    public bool Update(ZoneUnit unit, Vector3 position)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (!_members.TryGetValue(unit, out var entry)) return false;
        var x = Cell(position.X, CellCountX); var y = Cell(position.Y, CellCountY);
        if (x != entry.X || y != entry.Y)
        {
            var destination = GetCell(entry.Category, x, y);
            CheckCapacity(destination); // Validate before changing membership.
            RemoveFromCell(entry);
            entry.X = x; entry.Y = y; entry.Index = destination.Count;
            destination.Add(entry);
        }
        entry.Position = position;
        return true;
    }

    /// <summary>Validate a model publication before an ordered physical relocation commits.</summary>
    internal void ValidateUpdate(ZoneUnit unit, Vector3 position)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (!_members.TryGetValue(unit, out var entry))
            throw new InvalidOperationException("Relocation requires the exact Unit's existing spatial membership.");
        var x = Cell(position.X, CellCountX); var y = Cell(position.Y, CellCountY);
        if ((x != entry.X || y != entry.Y) && _cells.TryGetValue((entry.Category, x, y), out var destination))
            CheckCapacity(destination);
    }

    /// <summary>39D2D8C0. Exact instance ownership prevents an old unit from removing its replacement.</summary>
    public bool Remove(ZoneUnit unit)
    {
        if (!_members.Remove(unit, out var entry)) return false;
        RemoveFromCell(entry);
        return true;
    }

    public ZoneSpatialMembership? GetMembership(ZoneUnit unit) => _members.TryGetValue(unit, out var entry)
        ? new(entry.Category, entry.X, entry.Y, entry.Index, entry.Position) : null;

    public IReadOnlyList<ZoneUnit> Query(ZoneSpatialCategory category, Vector3 position, float radius)
    {
        var result = new List<ZoneUnit>();
        AppendQuery(category, position, radius, result);
        return result.AsReadOnly();
    }

    /// <summary>39325250. Append strict 3D matches in X, Y, then cell-entry order, without other filters.</summary>
    public void AppendQuery(ZoneSpatialCategory category, Vector3 position, float radius, List<ZoneUnit> result)
    {
        ValidateQuery(category);
        ArgumentNullException.ThrowIfNull(result);
        var (minimumX, minimumY, maximumX, maximumY) = Range(position, radius);
        var squaredRadius = radius * radius;
        for (var x = minimumX; x <= maximumX; x++)
        for (var y = minimumY; y <= maximumY; y++)
            if (_cells.TryGetValue((category, x, y), out var entries))
                foreach (var entry in entries)
                    if (SquaredDistance(entry.Position, position) < squaredRadius) result.Add(entry.Unit);
    }

    /// <summary>
    /// 39D2D580. Return the best distance within radius, stopping at the first improvement strictly
    /// below stopDistance. This early result is order-dependent and need not be the nearest unit.
    /// With no improvement, return sqrt(radius squared), including native nonfinite behavior.
    /// </summary>
    public float QueryDistance(ZoneSpatialCategory category, Vector3 position, float stopDistance, float radius)
    {
        ValidateQuery(category);
        var (minimumX, minimumY, maximumX, maximumY) = Range(position, radius);
        var best = radius * radius; var stop = stopDistance * stopDistance;
        for (var x = minimumX; x <= maximumX; x++)
        for (var y = minimumY; y <= maximumY; y++)
            if (_cells.TryGetValue((category, x, y), out var entries))
                foreach (var entry in entries)
                {
                    var distance = SquaredDistance(entry.Position, position);
                    // COMISS/JAE skips ordered >= only. Unordered values enter the update branch.
                    if (distance >= best) continue;
                    best = distance;
                    if (!(distance >= stop)) return MathF.Sqrt(best); // COMISS/JB also stops on unordered.
                }
        return MathF.Sqrt(best);
    }

    private void ValidateQuery(ZoneSpatialCategory category)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (category is not (ZoneSpatialCategory.Character or ZoneSpatialCategory.Npc))
            throw new ArgumentOutOfRangeException(nameof(category));
    }

    private (int, int, int, int) Range(Vector3 position, float radius) =>
        (Cell(position.X - radius, CellCountX), Cell(position.Y - radius, CellCountY),
            Cell(position.X + radius, CellCountX), Cell(position.Y + radius, CellCountY));

    private int Cell(float value, int count)
    {
        var scaled = value * _inverseCellSize;
        var index = !float.IsFinite(scaled) || scaled >= 2147483648f || scaled < -2147483648f
            ? int.MinValue : (int)scaled; // CVTTSS2SI32, followed by signed clamps.
        return Math.Clamp(index, 0, count - 1);
    }

    private static float SquaredDistance(Vector3 left, Vector3 right)
    {
        var x = left.X - right.X; var y = left.Y - right.Y; var z = left.Z - right.Z;
        return (x * x + y * y) + z * z; // Scalar native sum order; do not use a SIMD dot product.
    }

    private List<Entry> GetCell(ZoneSpatialCategory category, int x, int y)
    {
        if (!_cells.TryGetValue((category, x, y), out var entries)) _cells[(category, x, y)] = entries = [];
        return entries;
    }

    private static void CheckCapacity(List<Entry> entries)
    {
        if (entries.Count >= 32768)
            throw new InvalidOperationException("A spatial cell cannot exceed the native signed-short membership index.");
    }

    private void RemoveFromCell(Entry entry)
    {
        var entries = _cells[(entry.Category, entry.X, entry.Y)];
        var last = entries[^1];
        entries[entry.Index] = last; last.Index = entry.Index;
        entries.RemoveAt(entries.Count - 1);
        if (entries.Count == 0) _cells.Remove((entry.Category, entry.X, entry.Y));
    }

    /// <summary>Retire every live tag as in 39D2D7E0. The managed empty grid retains its geometry for reuse.</summary>
    public void Clear() { _members.Clear(); _cells.Clear(); }

    public void Dispose()
    {
        if (IsDisposed) return;
        Clear(); IsDisposed = true; _registry = null;
    }
}
