using AAEmu.Zone.Protocol.Messages;

namespace AAEmu.Zone.Units;

/// <summary>A skill timeline World started for a caster (native map keyed by timeline id, StartSkill 39456D70).</summary>
public sealed record ZoneActiveSkill(ushort Timeline, uint SkillId, uint CasterId, SkillTargetRef Target, uint CastTime, int FireCount);

/// <summary>Legacy observed Start packet, not the authoritative independent fields in ZoneUnit.Casting.</summary>
public sealed record ZoneUnitCast(ushort Timeline, uint SkillId, bool Casting);

/// <summary>A plot timeline World is playing (ZonePlotMan state, PlayEvent 39455730).</summary>
public sealed record ZonePlotState(
    ushort Timeline, uint SkillId, uint LastEventId, uint CasterId, uint TargetId, uint CastTimeMs, uint ChannelingTimeMs,
    bool Last, uint[] TargetUnitIds, bool CastingStopped, bool ChannelingStopped);

/// <summary>Latest heal or damage World reported against a target; the input native NPC aggro would consume.</summary>
public sealed record ZoneCombatFeedback(uint SourceId, uint SkillId, long Amount, bool Critical);

/// <summary>
/// Mirror of the WZ skill, plot and combat-feedback packets (0x002B-0x0031, 0x003A-0x003D). Native behaviour
/// is recorded as state only: StartSkill/FireSkill/EndSkill drive skill controllers and cooldown starts, and
/// UnitDamaged/UnitHealed feed NPC aggro, none of which exist in this zone yet (docs/tasks/combat.md CMB-05/06/18).
/// Events for units this zone does not know are counted, never thrown. Sequential dispatch only.
/// </summary>
public sealed class ZoneSkillState(Func<uint, bool> isKnownUnit)
{
    private readonly Dictionary<ushort, ZoneActiveSkill> _active = [];
    private readonly Dictionary<uint, ZoneUnitCast> _casts = [];
    private readonly Dictionary<ushort, ZonePlotState> _plots = [];
    private readonly Dictionary<uint, ZoneCombatFeedback> _lastHeal = [];
    private readonly Dictionary<uint, ZoneCombatFeedback> _lastDamage = [];

    public IReadOnlyDictionary<ushort, ZoneActiveSkill> ActiveSkills => _active;
    public IReadOnlyDictionary<uint, ZoneUnitCast> Casts => _casts;
    public IReadOnlyDictionary<ushort, ZonePlotState> Plots => _plots;
    public IReadOnlyDictionary<uint, ZoneCombatFeedback> LastHeal => _lastHeal;
    public IReadOnlyDictionary<uint, ZoneCombatFeedback> LastDamage => _lastDamage;
    /// <summary>Packets that referenced a unit absent from this zone (accepted and counted).</summary>
    public int UnknownUnitEvents { get; private set; }
    /// <summary>Heal and damage packets received, including those for units this zone does not know.</summary>
    public int HealCount { get; private set; }
    public int DamageCount { get; private set; }

    private bool Known(uint unitId)
    {
        if (isKnownUnit(unitId)) return true;
        UnknownUnitEvents++;
        return false;
    }

    public void Start(SkillStarted message)
    {
        if (message.Result.Code != 0) { Known(message.Caster.ObjectId); return; }
        _active[message.Timeline] = new(message.Timeline, message.SkillId, message.Caster.ObjectId, message.Target, message.CastTime, 0);
        if (Known(message.Caster.ObjectId))
            _casts[message.Caster.ObjectId] = new(message.Timeline, message.SkillId, true);
        Prune(); // removed units are not announced to this mirror; drop their entries on the next cast
    }

    public void Fire(SkillFired message)
    {
        if (message.Result.Code != 0) { Known(message.Caster.ObjectId); return; }
        // Native FireSkill also resolves the timeline from the map; a fire for an unknown timeline is kept as a fresh entry.
        _active.TryGetValue(message.Timeline, out var current);
        _active[message.Timeline] = (current ?? new(message.Timeline, message.SkillId, message.Caster.ObjectId, message.Target, 0, 0))
            with { FireCount = (current?.FireCount ?? 0) + 1 };
        Known(message.Caster.ObjectId);
    }

    public void End(SkillEnded message)
    {
        _active.Remove(message.Timeline);
        if (_casts.TryGetValue(message.Caster.ObjectId, out var cast) && cast.Timeline == message.Timeline)
            _casts.Remove(message.Caster.ObjectId);
        Known(message.Caster.ObjectId);
    }

    /// <summary>Native39456600 clears the independent casting byte only for skill type2; this view records that observation.</summary>
    public void Stop(SkillStopped message)
    {
        if (!Known(message.ObjectId)) return;
        if (message.SkillId == 2 && _casts.TryGetValue(message.ObjectId, out var cast))
            _casts[message.ObjectId] = cast with { Casting = false };
    }

    /// <summary>Native 39456630: resets the unit's current skill (+32280/+32284) when the timeline matches.</summary>
    public void StopCasting(CastingStopped message)
    {
        if (!Known(message.ObjectId)) return;
        if (_casts.TryGetValue(message.ObjectId, out var cast) && cast.Timeline == (ushort)message.Timeline)
            _casts.Remove(message.ObjectId);
    }

    public void Plot(PlotEvent message)
    {
        _plots[message.Timeline] = new(message.Timeline, message.SkillId, message.EventId, message.Caster.ObjectId, message.Target.ObjectId,
            message.CastTimeMs, message.ChannelingTimeMs, message.Last, message.TargetUnitIds, false, false);
        Known(message.Caster.ObjectId);
    }

    public void PlotEnd(PlotEnded message) => _plots.Remove((ushort)message.Timeline);

    public void PlotCastingStop(PlotStopped message)
    {
        if (_plots.TryGetValue((ushort)message.Timeline, out var plot)) _plots[(ushort)message.Timeline] = plot with { CastingStopped = true };
    }

    public void PlotChannelingStop(PlotStopped message)
    {
        if (_plots.TryGetValue((ushort)message.Timeline, out var plot)) _plots[(ushort)message.Timeline] = plot with { ChannelingStopped = true };
    }

    public void Heal(UnitHealed message)
    {
        HealCount++;
        if (!Known(message.TargetId)) return;
        var skill = message.Action is Protocol.Units.UnitSkillCast cast ? cast.SkillId : 0;
        _lastHeal[message.TargetId] = new(message.UnitInCharge != 0 ? message.UnitInCharge : message.Caster.ObjectId, skill, message.Amount, message.Critical);
    }

    public void Damage(UnitDamaged message)
    {
        DamageCount++;
        if (!Known(message.TargetId)) return;
        var skill = message.Action is Protocol.Units.UnitSkillCast cast ? cast.SkillId : 0;
        _lastDamage[message.TargetId] = new(message.CasterId, skill, message.Damage, false); // hit-type crit bits not recovered
    }

    private void Prune()
    {
        foreach (var unit in _casts.Keys.Where(id => !isKnownUnit(id)).ToArray()) _casts.Remove(unit);
        foreach (var unit in _lastHeal.Keys.Where(id => !isKnownUnit(id)).ToArray()) _lastHeal.Remove(unit);
        foreach (var unit in _lastDamage.Keys.Where(id => !isKnownUnit(id)).ToArray()) _lastDamage.Remove(unit);
    }
}
