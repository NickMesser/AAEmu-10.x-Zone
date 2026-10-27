using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Protocol.Units;

namespace AAEmu.Zone.Simulation;

public enum ZoneNpcSpawnerEventDiagnostic
{
    UnknownEvent,
    MissingSchedule,
    PersistentSpawnFailed
}

public interface IZoneNpcSpawnerEventServices
{
    //39342920 queries the current entity AI object's group, which can differ from spawn metadata.
    NpcSpawnGroup? GetCurrentAiGroup(ZoneNpc npc);
    void Report(ZoneNpcSpawnerEventDiagnostic diagnostic, ZoneNpcSpawnerRegistration owner, byte eventId);
}

/// <summary>
///39349800 events and39349DC0 persistent restoration for one exact spawner lifetime.
/// Physical removal remains the scheduler's required scene operation; it need not synchronously
/// remove NPCs or lower population. Immediate spawn overrides live only for this event dispatch.
/// </summary>
public sealed class ZoneNpcSpawnerEvents
{
    private readonly ZoneNpcSpawnerRegistry _registry;
    private readonly Func<bool, ZoneNpcSpawnOverrides?, ZoneNpcSpawnAttempt> _attempt;
    private readonly IZoneNpcSpawnerEventServices _services;
    private bool _handling;
    private object? _handlingGeneration;
    public ZoneNpcSpawnerRegistration Owner { get; }
    public bool EventsEnabled { get; private set; }
    public bool TowerDefenseControlled { get; private set; }
    public bool GameScheduleControlled { get; private set; }

    public ZoneNpcSpawnerEvents(ZoneNpcSpawnerRegistry registry, ZoneNpcSpawnerRegistration owner,
        Func<bool, ZoneNpcSpawnOverrides?, ZoneNpcSpawnAttempt> attempt, IZoneNpcSpawnerEventServices services,
        bool eventsEnabled = true, bool towerDefenseControlled = false, bool gameScheduleControlled = false)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _attempt = attempt ?? throw new ArgumentNullException(nameof(attempt));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        if (!registry.IsAccepting(owner)) throw new ArgumentException("Events require a current accepting spawner registration.", nameof(owner));
        //3934A2D0 sets the packed default bytes to01,00,00,01 (enabled,tower,schedule,active).
        EventsEnabled = eventsEnabled;
        TowerDefenseControlled = towerDefenseControlled;
        GameScheduleControlled = gameScheduleControlled;
    }

    private bool Current() => _registry.IsAccepting(Owner) &&
        (!_handling || ReferenceEquals(_handlingGeneration, Owner.ContentGeneration));
    private bool Current(ZoneNpc npc) => Current() && !npc.Activation.IsDisposed &&
        ReferenceEquals(_registry.Npcs.FindByObjectId(npc.ObjectId), npc);

    // Native postload applies these flags to the existing event state. In particular, the
    // schedule controller dispatches event6 before marking its spawners as schedule controlled.
    public bool MarkTowerDefenseControlled()
    {
        if (!Current()) return false;
        TowerDefenseControlled = true;
        return true;
    }

    public bool MarkGameScheduleControlled()
    {
        if (!Current()) return false;
        GameScheduleControlled = true;
        return true;
    }

    /// <summary>
    /// True means a recognized event passed ownership and controller gates. Event3 can return
    /// true while its spawn fails. Unknown, disabled, foreign and retired events return false.
    /// Recursive managed dispatch is rejected before mutation; native transport does not define
    /// synchronous callbacks reentering this spawner's event switch.
    /// </summary>
    public bool Handle(NpcSpawnerEventMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!Current() || message.SpawnerId != Owner.Id || !EventsEnabled ||
            (TowerDefenseControlled && message.Reason.Kind != 1) ||
            (GameScheduleControlled && message.Reason.Kind != 2)) return false;
        if (_handling) throw new InvalidOperationException("Spawner events cannot run recursively.");
        _handling = true;
        _handlingGeneration = Owner.ContentGeneration;
        try
        {
            var scheduler = Owner.Scheduler;
            switch (message.Event)
            {
                case 1:
                    scheduler.Active = true;
                    scheduler.ClearPending();
                    scheduler.QueueAll(100, Current);
                    break;
                case 2:
                    scheduler.Active = false;
                    scheduler.ClearPending();
                    break;
                case 3:
                    scheduler.Active = true;
                    scheduler.ClearPending();
                    var accepted = scheduler.AttemptImmediately(_attempt, null, Current);
                    if (!Current()) break;
                    if (accepted) { scheduler.Active = false; EventsEnabled = false; }
                    else _services.Report(ZoneNpcSpawnerEventDiagnostic.PersistentSpawnFailed, Owner, message.Event);
                    break;
                case 4:
                    scheduler.Active = false;
                    scheduler.ClearPending();
                    EventsEnabled = false;
                    break;
                case 5:
                    scheduler.Active = true;
                    if (message.Reason.Kind != 4) scheduler.RemoveSpawnedNpcs(false, Current);
                    if (!Current()) break;
                    SpawnAllOnce(message);
                    if (Current()) scheduler.Active = false;
                    break;
                case 6:
                    scheduler.Active = false;
                    scheduler.RemoveSpawnedNpcs(message.Reason.Kind == 2, Current);
                    if (Current()) scheduler.ClearPending();
                    break;
                case 7:
                case 8:
                    scheduler.Active = true;
                    SpawnAllOnce(message);
                    if (Current() && message.Event == 8) scheduler.Active = false;
                    break;
                case 9:
                    RespawnGroups();
                    break;
                default:
                    _services.Report(ZoneNpcSpawnerEventDiagnostic.UnknownEvent, Owner, message.Event);
                    return false;
            }
            return true;
        }
        finally { _handling = false; }
    }

    private void SpawnAllOnce(NpcSpawnerEventMessage message)
    {
        var scheduler = Owner.Scheduler;
        scheduler.ClearPending();
        var schedule = scheduler.ReadSchedule();
        if (!Current()) return;
        if (schedule is null) { _services.Report(ZoneNpcSpawnerEventDiagnostic.MissingSchedule, Owner, message.Event); return; }
        var remaining = unchecked(schedule.MaximumPopulation - scheduler.Population);
        var overrides = new ZoneNpcSpawnOverrides(message.Reason, message.Creator, message.FactionId,
            message.DespawnOnCreatorDeath, message.UseSummonerAggroTarget, message.LifeTime);
        //39349730 captures the deficit once, and allows ten attempts for each captured slot.
        for (var slot = 0; slot < remaining && Current(); slot++)
            for (var retry = 0; retry < 10 && Current(); retry++)
                if (scheduler.AttemptImmediately(_attempt, overrides, Current)) break;
    }

    private void RespawnGroups()
    {
        // Follow live per-member order. A callback may retire the current entry; preserve the
        // successor at the same index instead of resolving an ID that now belongs to another NPC.
        foreach (var members in Owner.Members)
        {
            var index = 0;
            while (Current() && index < members.Count)
            {
                var npc = members[index];
                if (Current(npc))
                {
                    var metadata = _services.GetCurrentAiGroup(npc);
                    if (!Current()) return;
                    if (Current(npc) && members.Contains(npc))
                    {
                        var group = _registry.Groups.Find(metadata?.Id ?? 0);
                        if (group is not null) _registry.Groups.Respawn(group);
                    }
                }
                if (index < members.Count && ReferenceEquals(members[index], npc)) index++;
            }
        }
    }

    /// <summary>
    /// True means a matching current persistent row was processed, including an ignored event.
    ///39345F20 normalizes the wire state using state==1 before39349DC0 dispatches event3 or4.
    /// Inactive records leave the original template untouched; active records store its raw ID.
    /// </summary>
    public bool Restore(PersistentSpawner saved)
    {
        if (!Current() || saved.Id != Owner.Id || !Owner.Persistent) return false;
        if (_handling) throw new InvalidOperationException("Spawner restoration cannot run recursively.");
        if (saved.State == 1 && !_registry.TrySetOriginalTemplateId(Owner, saved.TemplateId)) return false;
        Handle(new(Owner.Id, saved.State == 1 ? (byte)3 : (byte)4, new(0), new(ZoneUnitKind.Character), 0, false, false, 0));
        return true;
    }
}
