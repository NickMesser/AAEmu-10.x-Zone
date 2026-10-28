using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Hosting;
using AAEmu.Zone.WorldState;

namespace AAEmu.Zone.WorldLink;

/// <summary>
/// Connection-scoped initial state. Native 0x39363220 opens gate bit 1 when relations are
/// complete; 0x39362DF0 opens bit 2 on the final spawner chunk. Local simulation loading
/// must finish separately before ZWZoneLoaded (sender 0x39332730).
/// </summary>
public sealed class ZoneStartupState
{
    private readonly WorldContentNameComparer _contentNameComparer;
    private readonly ChunkedSnapshot<uint, ZoneFaction> _factions = new(faction => faction.Id);
    private readonly ChunkedSnapshot<(uint, uint), ZoneFactionRelation> _relations = new(
        relation => (Math.Min(relation.FirstId, relation.SecondId), Math.Max(relation.FirstId, relation.SecondId)));
    private readonly List<PersistentSpawner> _spawners = [];
    private readonly Stream? _connection;
    private int _sceneClaimed;

    /// <summary>The original ZWJoin, retained only by a real synchronization. JoinResponse carries no Zone key or IID.</summary>
    public ZoneJoin? JoinRequest { get; }

    public ZoneJoinResponse? JoinResponse { get; private set; }
    public WorldContentFilter? ContentFilter { get; private set; }
    public bool SpawnersComplete { get; private set; }
    public bool FactionsComplete => _factions.IsComplete;
    public bool RelationsComplete => _relations.IsComplete;
    public bool NativeGateOpen => JoinResponse is { Reason: 0 } && RelationsComplete && SpawnersComplete;
    public bool IsComplete => NativeGateOpen && FactionsComplete;
    public IReadOnlyDictionary<uint, ZoneFaction> Factions => _factions.Entries;
    public IReadOnlyDictionary<(uint, uint), ZoneFactionRelation> Relations => _relations.Entries;
    public IReadOnlyList<PersistentSpawner> Spawners => _spawners.AsReadOnly();
    public float? TimeOfDay { get; private set; }
    public uint? WorldGameTime { get; private set; }
    public ZoneTimeOfDayState TimeOfDayState { get; } = new();
    public ZoneWorldGameClock WorldGameClock { get; }
    public ZoneEngineCVars EngineCVars { get; set; } = ZoneEngineCVars.Empty;

    public ZoneStartupState(WorldContentNameComparer? contentNameComparer = null, Func<uint>? localGameMilliseconds = null)
    {
        _contentNameComparer = contentNameComparer ?? WorldContentNameComparer.C;
        WorldGameClock = new(localGameMilliseconds);
    }

    internal ZoneStartupState(Stream connection, ZoneJoin join, WorldContentNameComparer? comparer, Func<uint>? localGameMilliseconds)
        : this(comparer, localGameMilliseconds)
    {
        _connection = connection;
        JoinRequest = join;
    }

    // Managed admission policy: one completed wire synchronization owns one scene lifetime.
    // Reuse failure must occur before that new caller takes ownership of stream or clocks.
    internal void ClaimScene(Stream connection, int zoneKey)
    {
        if (!IsComplete || JoinRequest is not { } join || !ReferenceEquals(_connection, connection))
            throw new ArgumentException("The scene requires this connection's completed World synchronization.", nameof(connection));
        if (join.ZoneKey != zoneKey)
            throw new ArgumentException("The selected layout does not match the Zone key sent in ZWJoin.", nameof(zoneKey));
        ObjectDisposedException.ThrowIf(TimeOfDayState.IsDisposed || WorldGameClock.IsDisposed, this);
        if (Interlocked.CompareExchange(ref _sceneClaimed, 1, 0) != 0)
            throw new InvalidOperationException("This World synchronization already belongs to a scene.");
    }

    public void CloseClocks()
    {
        TimeOfDayState.Dispose();
        WorldGameClock.Dispose();
    }

    /// <summary>Fully decodes each body before changing any startup state.</summary>
    public void Receive(ZoneFrame frame)
    {
        if (JoinResponse == null && frame.Opcode != WzOpcodes.JoinResponse)
            throw new InvalidDataException("World sent Zone state before accepting the join.");

        switch (frame.Opcode)
        {
            case WzOpcodes.JoinResponse:
                if (JoinResponse != null)
                    throw new InvalidDataException("World sent a duplicate join response.");
                var join = ZoneJoinResponse.Read(frame.Body.Span);
                if (join.Reason != 0)
                    throw new InvalidDataException(ZoneWorldLifecycle.JoinRefusalMessage(join.Reason));
                var filter = WorldContentFilter.Read(join.Filter.Span, _contentNameComparer);
                JoinResponse = join;
                ContentFilter = filter;
                break;
            case WzOpcodes.FactionList:
                var factions = FactionSnapshot.Read(frame.Body.Span);
                _factions.Add(factions.Total, factions.Entries);
                break;
            case WzOpcodes.FactionRelationList:
                var relations = FactionRelationSnapshot.Read(frame.Body.Span);
                _relations.Add(relations.Total, relations.Entries);
                break;
            case WzOpcodes.SpawnerList:
                var spawners = SpawnerSnapshot.Read(frame.Body.Span);
                if (SpawnersComplete || spawners.Entries.Count > ChunkedSnapshot<uint, ZoneFaction>.MaxRecords - _spawners.Count)
                    throw new InvalidDataException("Unexpected spawner chunk after completion or beyond the Zone snapshot capacity.");
                _spawners.AddRange(spawners.Entries);
                SpawnersComplete = spawners.Last;
                break;
            case WzOpcodes.TimeOfDay:
                var time = ZoneTimeOfDay.Read(frame.Body.Span);
                TimeOfDayState.Receive(time);
                if (!TimeOfDayState.IsDisposed) TimeOfDay = time.Hour;
                break;
            case WzOpcodes.DetailedTimeOfDay:
                var detailed = ZoneDetailedTimeOfDay.Read(frame.Body.Span);
                TimeOfDayState.Receive(detailed);
                if (!TimeOfDayState.IsDisposed) TimeOfDay = detailed.Hour;
                break;
            case WzOpcodes.WorldGameTime:
                var gameTimeReader = new ZoneReader(frame.Body.Span);
                var gameTime = gameTimeReader.ReadUInt32();
                gameTimeReader.EnsureFullyConsumed();
                WorldGameClock.Receive(gameTime);
                WorldGameTime = gameTime;
                break;
            default:
                throw new NotSupportedException($"Zone startup does not yet handle WZ opcode 0x{frame.Opcode:X4}.");
        }
    }

    /// <summary>
    /// The zone's world (configuration worldName), which selects the native server type
    /// (ZoneServerType.For). Set by the host after synchronization; null for detached sessions.
    /// </summary>
    public string? WorldName { get; set; }
}
