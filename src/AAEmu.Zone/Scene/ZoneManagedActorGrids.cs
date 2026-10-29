using System.Numerics;
using AAEmu.Zone.Ai;

namespace AAEmu.Zone.Scene;

/// <summary>39332730 -> Seamless.currentZone bounds ->31049668: two buckets,64m cells.</summary>
public sealed record ZoneManagedActorGridConfiguration
{
    public int MinimumWorldCellX { get; }
    public int MinimumWorldCellY { get; }
    public int MaximumWorldCellX { get; }
    public int MaximumWorldCellY { get; }
    public int Columns { get; }
    public int Rows { get; }
    public const int CellSize = 64;
    public ZoneManagedActorGridConfiguration(int minimumWorldCellX, int minimumWorldCellY,
        int maximumWorldCellX, int maximumWorldCellY)
    {
        var columns = ((long)maximumWorldCellX - minimumWorldCellX + 1) * 1024 / CellSize;
        var rows = ((long)maximumWorldCellY - minimumWorldCellY + 1) * 1024 / CellSize;
        if (columns <= 0 || rows <= 0 || columns > short.MaxValue || rows > short.MaxValue)
            throw new NotSupportedException("Actor grids require positive source dimensions fitting their signed16-bit cell handles.");
        MinimumWorldCellX = minimumWorldCellX; MinimumWorldCellY = minimumWorldCellY;
        MaximumWorldCellX = maximumWorldCellX; MaximumWorldCellY = maximumWorldCellY;
        Columns = (int)columns; Rows = (int)rows;
    }
    public static ZoneManagedActorGridConfiguration FromLayout(ZoneWorldMapLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return new(layout.Origin.X, layout.Origin.Y, layout.Zone.LastCell.X, layout.Zone.LastCell.Y);
    }
}

public enum ZoneManagedActorGridKind { Puppet, Player, NonActor }
public readonly record struct ZoneManagedActorGridEntry(ZoneAiObjectRegistry.Registration Registration,
    ZoneAiObjectState? Object, ZoneManagedActorGridKind Kind, ulong ActorAllocationOrderKey,
    bool IsMember, Vector3 CachedPosition, int Column, int Row, int Slot);
public sealed class ZoneManagedActorGridQuery
{
    public ulong Revision { get; }
    public int Bucket { get; }
    public Vector3 Center { get; }
    public float Radius { get; }
    public IReadOnlyList<ZoneManagedActorGridEntry> Entries { get; }
    internal ZoneManagedActorGrids Owner { get; }
    internal ZoneManagedActorGridQuery(ZoneManagedActorGrids owner, ulong revision, int bucket, Vector3 center,
        float radius, IReadOnlyList<ZoneManagedActorGridEntry> entries)
    { Owner = owner; Revision = revision; Bucket = bucket; Center = center; Radius = radius; Entries = entries; }
}
public sealed record ZoneManagedActorGridAdvance(ulong Revision, uint PuppetCursor, uint PlayerCursor,
    IReadOnlyList<ZoneManagedActorGridEntry> UpdatedEntries);

/// <summary>
/// The actual two actor grids: independent insertion-time/rotating cached positions, native cell-slot ordering,
/// enabled Puppet membership and player membership. The nearest-object type index is an inventory only.
/// Registration classification and the source AI frame boundary belong to the composing scene.
/// </summary>
public sealed class ZoneManagedActorGrids : IDisposable
{
    private readonly ZoneAiObjectRegistry _registry;
    private readonly Dictionary<ZoneAiObjectRegistry.Registration, ZoneManagedActorGridLease> _bindings = [];
    private readonly Dictionary<ZoneAiObjectState, ZoneManagedActorGridLease> _objects = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ulong, ZoneManagedActorGridLease> _keys = [];
    private readonly List<ZoneManagedActorGridLease> _puppets = [], _players = [];
    private readonly Dictionary<(int Bucket, int Column, int Row), List<ZoneManagedActorGridLease>> _cells = [];
    private uint _puppetCursor, _playerCursor;
    public ZoneManagedActorGridConfiguration Configuration { get; }
    public bool IsDisposed { get; private set; }
    public ulong Revision { get; private set; }
    public int Count { get { Check(); PruneRetired(); return _bindings.Count; } }

    public ZoneManagedActorGrids(ZoneAiObjectRegistry registry, ZoneManagedActorGridConfiguration configuration)
    { _registry = registry ?? throw new ArgumentNullException(nameof(registry)); Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration)); }

    public ZoneManagedActorGridLease BindPuppet(ZoneAiObjectRegistry.Registration registration, ZoneAiObjectState aiObject,
        ulong actorAllocationOrderKey, Vector3 initialCachedPosition, bool enabled) =>
        Bind(registration, aiObject, actorAllocationOrderKey, initialCachedPosition, enabled, ZoneManagedActorGridKind.Puppet);
    public ZoneManagedActorGridLease BindPlayer(ZoneAiObjectRegistry.Registration registration, ZoneAiObjectState aiObject,
        ulong actorAllocationOrderKey, Vector3 initialCachedPosition) =>
        Bind(registration, aiObject, actorAllocationOrderKey, initialCachedPosition, true, ZoneManagedActorGridKind.Player);

    /// <summary>Explicit proof of a source non-grid allocation. ObjectType alone does not supply that proof.</summary>
    public ZoneManagedActorGridLease BindNonActor(ZoneAiObjectRegistry.Registration registration) =>
        Bind(registration, null, 0, default, false, ZoneManagedActorGridKind.NonActor);

    private ZoneManagedActorGridLease Bind(ZoneAiObjectRegistry.Registration registration, ZoneAiObjectState? aiObject,
        ulong key, Vector3 position, bool enabled, ZoneManagedActorGridKind kind)
    {
        Check(); ArgumentNullException.ThrowIfNull(registration); PruneRetired();
        if (!ReferenceEquals(registration.Owner, _registry) || !registration.IsRegistered)
            throw new ArgumentException("Actor grid classification requires this index's exact live registration.", nameof(registration));
        if (_bindings.ContainsKey(registration)) throw new InvalidOperationException("This exact registration already has a grid classification.");
        if (kind != ZoneManagedActorGridKind.NonActor)
        {
            ArgumentNullException.ThrowIfNull(aiObject);
            if (!ReferenceEquals(registration.Target, aiObject) || !ReferenceEquals(registration.Candidate, aiObject))
                throw new ArgumentException("Actor grid identity must match both indexed target and candidate.", nameof(aiObject));
            if (key == 0 || _keys.ContainsKey(key) || _objects.ContainsKey(aiObject))
                throw new InvalidOperationException("Actor grid allocation identity must be nonzero and unique among live actors.");
            ValidatePosition(position);
        }
        var lease = new ZoneManagedActorGridLease(this, registration, aiObject, kind, key) { Enabled = enabled };
        _bindings.Add(registration, lease);
        if (aiObject is not null)
        {
            _objects.Add(aiObject, lease); _keys.Add(key, lease);
            if (enabled)
            {
                AddSorted(kind == ZoneManagedActorGridKind.Player ? _players : _puppets, lease);
                Insert(lease, position);
            }
        }
        Bump(); return lease;
    }

    public IReadOnlyList<ZoneAiObjectRegistry.Registration> CopyUnknownRegistrations()
    { Check(); PruneRetired(); return Array.AsReadOnly(_registry.CopyRegistrations().Where(registration => !_bindings.ContainsKey(registration)).ToArray()); }

    /// <summary>
    /// 31098E78 cache prefix only, once at the accepted AI-system update boundary before manager actor updates.
    /// Both sorted vectors retain independent cursors; no active Puppets means neither bucket refreshes.
    /// The following original perception work is outside this cache owner.
    /// </summary>
    public ZoneManagedActorGridAdvance AdvanceCacheSlice()
    {
        CheckComplete();
        var selected = new List<ZoneManagedActorGridLease>();
        var nextPlayer = _playerCursor; var nextPuppet = _puppetCursor;
        if (_puppets.Count != 0)
        {
            Select(_players, _playerCursor, selected, out nextPlayer);
            Select(_puppets, _puppetCursor, selected, out nextPuppet);
        }
        //SetPos rejects NaNs. Direct public field mutation must not partially refresh the grid before failing.
        var positions = selected.Select(lease => lease.Object!.Position).ToArray();
        foreach (var position in positions) ValidatePosition(position);
        for (var index = 0; index < selected.Count; index++) UpdateCachedPosition(selected[index], positions[index]);
        _playerCursor = nextPlayer; _puppetCursor = nextPuppet;
        if (selected.Count != 0) Bump();
        return new(Revision, _puppetCursor, _playerCursor, Array.AsReadOnly(selected.Select(Snapshot).ToArray()));
    }

    public ZoneManagedActorGridQuery QueryBucket(int bucket, Vector3 position, float radius)
    {
        CheckComplete();
        if ((uint)bucket > 1) throw new ArgumentOutOfRangeException(nameof(bucket));
        ValidatePosition(position);
        if (!float.IsFinite(radius) || radius < 0) throw new ArgumentOutOfRangeException(nameof(radius));
        var minColumn = Coordinate(position.X - radius, Configuration.Columns);
        var maxColumn = Coordinate(position.X + radius, Configuration.Columns);
        var minRow = Coordinate(position.Y - radius, Configuration.Rows);
        var maxRow = Coordinate(position.Y + radius, Configuration.Rows);
        var squaredRadius = radius * radius;
        if (!float.IsFinite(squaredRadius)) throw new NotSupportedException("Actor grid query radius exceeds finite native scalar geometry.");
        var entries = new List<ZoneManagedActorGridEntry>();
        for (var column = minColumn; column <= maxColumn; column++)
            for (var row = minRow; row <= maxRow; row++)
                if (_cells.TryGetValue((bucket, column, row), out var cell))
                    foreach (var lease in cell)
                    {
                        var difference = lease.CachedPosition - position;
                        var squared = (difference.X * difference.X + difference.Y * difference.Y) + difference.Z * difference.Z;
                        if (squaredRadius > squared) entries.Add(Snapshot(lease));
                    }
        return new(this, Revision, bucket, position, radius, entries.AsReadOnly());
    }

    public bool IsCurrent(ZoneManagedActorGridQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (IsDisposed || !ReferenceEquals(query.Owner, this)) return false;
        PruneRetired();
        return Revision == query.Revision && _registry.CopyRegistrations().All(_bindings.ContainsKey);
    }
    public IReadOnlyList<ZoneManagedActorGridEntry> CopyEntries()
    { CheckComplete(); return Array.AsReadOnly(_bindings.Values.Select(Snapshot).ToArray()); }

    internal bool Current(ZoneManagedActorGridLease lease) => !IsDisposed && !lease.IsDisposed &&
        _bindings.TryGetValue(lease.Registration, out var existing) && ReferenceEquals(existing, lease) &&
        ReferenceEquals(lease.Registration.Owner, _registry) && lease.Registration.IsRegistered;
    internal void SetEnabled(ZoneManagedActorGridLease lease, bool enabled)
    {
        Check(); if (!Current(lease)) throw new ObjectDisposedException(nameof(ZoneManagedActorGridLease));
        if (lease.Kind != ZoneManagedActorGridKind.Puppet)
            throw new NotSupportedException("Only the original Puppet3105A7E8 enable/remove profile is owned.");
        if (lease.Enabled == enabled) return; //Native sorted insertion and handle insertion are idempotent.
        if (enabled)
        {
            var position = lease.Object!.Position; ValidatePosition(position);
            AddSorted(_puppets, lease); Insert(lease, position);
        }
        else { _puppets.Remove(lease); RemoveCell(lease); }
        lease.Enabled = enabled; Bump();
    }
    internal void Retire(ZoneManagedActorGridLease lease)
    {
        if (!_bindings.TryGetValue(lease.Registration, out var existing) || !ReferenceEquals(existing, lease)) return;
        _bindings.Remove(lease.Registration);
        if (lease.Object is not null)
        { _objects.Remove(lease.Object); _keys.Remove(lease.ActorAllocationOrderKey); }
        _puppets.Remove(lease); _players.Remove(lease); RemoveCell(lease); Bump();
    }
    internal ZoneManagedActorGridEntry Copy(ZoneManagedActorGridLease lease)
    { Check(); if (!Current(lease)) throw new ObjectDisposedException(nameof(ZoneManagedActorGridLease)); return Snapshot(lease); }

    private void Insert(ZoneManagedActorGridLease lease, Vector3 position)
    {
        lease.Column = Coordinate(position.X, Configuration.Columns); lease.Row = Coordinate(position.Y, Configuration.Rows);
        var key = (lease.Bucket, lease.Column, lease.Row);
        if (!_cells.TryGetValue(key, out var cell)) _cells.Add(key, cell = []);
        lease.Slot = cell.Count; lease.CachedPosition = position; cell.Add(lease);
    }
    private void RemoveCell(ZoneManagedActorGridLease lease)
    {
        if (lease.Slot < 0) return;
        var key = (lease.Bucket, lease.Column, lease.Row); var cell = _cells[key]; var last = cell[^1];
        cell[lease.Slot] = last; last.Slot = lease.Slot; cell.RemoveAt(cell.Count - 1);
        if (cell.Count == 0) _cells.Remove(key);
        lease.Column = lease.Row = lease.Slot = -1;
    }
    private void UpdateCachedPosition(ZoneManagedActorGridLease lease, Vector3 position)
    {
        if (lease.Slot < 0) return;
        if (lease.Column == Coordinate(position.X, Configuration.Columns) && lease.Row == Coordinate(position.Y, Configuration.Rows))
            lease.CachedPosition = position;
        else { RemoveCell(lease); Insert(lease, position); }
    }
    private static void AddSorted(List<ZoneManagedActorGridLease> list, ZoneManagedActorGridLease lease)
    {
        var index = list.BinarySearch(lease, Comparer<ZoneManagedActorGridLease>.Create((left, right) =>
            left.ActorAllocationOrderKey.CompareTo(right.ActorAllocationOrderKey)));
        list.Insert(index < 0 ? ~index : index, lease);
    }
    private static void Select(List<ZoneManagedActorGridLease> list, uint cursor,
        List<ZoneManagedActorGridLease> selected, out uint next)
    {
        if (list.Count == 0) { next = cursor; return; }
        var end = unchecked(cursor + (uint)(list.Count / 7) + 1);
        next = (uint)((int)end % list.Count); //The source stores the signed division remainder.
        for (var index = cursor; index < end; index++) selected.Add(list[(int)(index % (uint)list.Count)]);
    }
    private static ZoneManagedActorGridEntry Snapshot(ZoneManagedActorGridLease lease) => new(lease.Registration,
        lease.Object, lease.Kind, lease.ActorAllocationOrderKey, lease.Slot >= 0, lease.CachedPosition, lease.Column, lease.Row, lease.Slot);
    private static int Coordinate(float value, int count)
    {
        var scaled = value * (1f / ZoneManagedActorGridConfiguration.CellSize);
        if (!float.IsFinite(scaled) || scaled < int.MinValue || scaled >= 2147483648f)
            throw new NotSupportedException("Actor grid coordinate exceeds the admitted signed32-bit native conversion profile.");
        return Math.Clamp((int)scaled, 0, count - 1);
    }
    private void CheckComplete()
    {
        Check(); PruneRetired();
        if (_registry.CopyRegistrations().Any(registration => !_bindings.ContainsKey(registration)))
            throw new NotSupportedException("The complete AI type index contains an unclassified registration; actor-grid census coverage is not owned.");
    }
    private void PruneRetired()
    {
        foreach (var lease in _bindings.Values.Where(lease => !lease.Registration.IsRegistered ||
            !ReferenceEquals(lease.Registration.Owner, _registry)).ToArray()) lease.Dispose();
    }
    private static void ValidatePosition(Vector3 position)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
            throw new InvalidDataException("Actor grid positions require the actual finite AI-object position.");
        _ = Coordinate(position.X, 1); _ = Coordinate(position.Y, 1);
    }
    private void Bump() { if (Revision == ulong.MaxValue) throw new InvalidOperationException("Actor grid revision exhausted."); Revision++; }
    private void Check() => ObjectDisposedException.ThrowIf(IsDisposed, this);
    public void Dispose()
    { if (IsDisposed) return; IsDisposed = true; foreach (var lease in _bindings.Values.ToArray()) lease.Dispose(); }
}

public sealed class ZoneManagedActorGridLease : IDisposable
{
    private readonly ZoneManagedActorGrids _owner;
    public ZoneAiObjectRegistry.Registration Registration { get; }
    public ZoneAiObjectState? Object { get; }
    public ZoneManagedActorGridKind Kind { get; }
    public ulong ActorAllocationOrderKey { get; }
    public bool IsDisposed { get; private set; }
    public bool IsCurrent => _owner.Current(this);
    public bool Enabled { get; internal set; }
    internal int Bucket => Kind == ZoneManagedActorGridKind.Player ? 1 : 0;
    internal Vector3 CachedPosition;
    internal int Column = -1, Row = -1, Slot = -1;
    internal ZoneManagedActorGridLease(ZoneManagedActorGrids owner, ZoneAiObjectRegistry.Registration registration,
        ZoneAiObjectState? aiObject, ZoneManagedActorGridKind kind, ulong key)
    { _owner = owner; Registration = registration; Object = aiObject; Kind = kind; ActorAllocationOrderKey = key; }
    public ZoneManagedActorGridEntry CopyState() => _owner.Copy(this);
    /// <summary>3105A7E8 grid effects. The composing actor-event owner separately writes actual actor104.</summary>
    public void SetEnabled(bool enabled) => _owner.SetEnabled(this, enabled);
    public void Dispose() { if (IsDisposed) return; IsDisposed = true; _owner.Retire(this); }
}
