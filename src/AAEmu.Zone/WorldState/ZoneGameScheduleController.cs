using AAEmu.Zone.Content;
using AAEmu.Zone.Npcs;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Protocol.Units;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.WorldState;

/// <summary>
/// Native393353B0/39335760/393357D0: World owns calendar edges. Zone applies Start/Continue/End
/// to the actual physical spawners after39335100's post-load gate. The running ID set survives
/// authored reloads; a fresh replacement scene/session owns a fresh set. This readiness does
/// not assert gameplay readiness or send ZoneLoaded.
/// Callback generation retirement, temporary reload gating and recursive same-schedule rejection
/// are replacement ownership policies; native transport does not define such synchronous reentry.
/// </summary>
public sealed class ZoneGameScheduleController : IDisposable
{
    private readonly ZoneNpcSpawnerFactory _factory;
    private readonly ZoneNpcSpawnerRegistry _registry;
    private readonly HashSet<uint> _running = [];
    private readonly Dictionary<uint, object> _operations = [];
    private IReadOnlyDictionary<uint, ZoneNpcSpawnerGameSchedule>? _schedules;
    private object? _readyGeneration;
    public bool IsDisposed { get; private set; }
    public bool IsReady => !IsDisposed && _readyGeneration is { } generation && _factory.IsCurrentGeneration(generation);
    public IReadOnlyList<uint> RunningScheduleIds => _running.Order().ToArray();

    internal ZoneGameScheduleController(ZoneNpcSpawnerFactory factory, ZoneNpcSpawnerRegistry registry)
    { _factory = factory; _registry = registry; }

    internal bool BelongsTo(ZoneUnitRegistry registry) => ReferenceEquals(_registry.Registry, registry);

    internal void BeginReload(ZoneNpcSpawnerControllerContent? content)
    {
        _readyGeneration = null;
        _operations.Clear();
        if (content is not null) _schedules = content.Schedules.ToDictionary(row => row.Definition.Id);
    }

    internal void CompletePostLoad(object generation)
    {
        if (!IsDisposed && _schedules is not null && _factory.IsCurrentGeneration(generation)) _readyGeneration = generation;
    }

    internal void RetireGeneration(object generation)
    {
        if (ReferenceEquals(_readyGeneration, generation)) _readyGeneration = null;
    }

    public bool Handle(GameScheduleMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Operation is not (GameScheduleOperation.Start or GameScheduleOperation.Continue or GameScheduleOperation.End))
            throw new ArgumentOutOfRangeException(nameof(message), "Unknown game-schedule operation.");
        if (!IsReady || !_schedules!.TryGetValue(message.ScheduleId, out var schedule)) return false;
        var ending = message.Operation == GameScheduleOperation.End;
        if (_running.Contains(message.ScheduleId) != ending || _operations.ContainsKey(message.ScheduleId)) return false;

        var generation = _readyGeneration!;
        var operation = new object();
        _operations.Add(message.ScheduleId, operation);
        bool Current() => IsReady && ReferenceEquals(_readyGeneration, generation) &&
            _operations.TryGetValue(message.ScheduleId, out var active) && ReferenceEquals(active, operation);
        try
        {
            // Native uses a separate type multimap, whose equal-key order remains admission
            // order after the physical update vector is sorted by camera distance.
            foreach (var link in schedule.Spawners)
            {
                if (!Current()) return false;
                foreach (var owner in _factory.GetTypeOwners(generation, link.SpawnerType))
                {
                    if (!Current()) return false;
                    if (!_registry.IsAccepting(owner) || owner.Events is null) continue;
                    owner.Events.Handle(new(owner.Id, ending ? (byte)6 : (byte)1, new(2, message.ScheduleId),
                        new(ZoneUnitKind.Character), 0, false, false, 0));
                    if (!Current()) return false;
                }
            }
            // Native commits the running set after all callbacks, including schedules with
            // no matching physical owners and events rejected by another controller gate.
            if (!Current()) return false;
            if (ending) _running.Remove(message.ScheduleId); else _running.Add(message.ScheduleId);
            return true;
        }
        finally
        {
            if (_operations.TryGetValue(message.ScheduleId, out var active) && ReferenceEquals(active, operation))
                _operations.Remove(message.ScheduleId);
        }
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true; _readyGeneration = null; _operations.Clear(); _running.Clear();
    }
}
