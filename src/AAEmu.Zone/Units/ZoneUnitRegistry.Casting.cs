using AAEmu.Zone.Content;
using AAEmu.Zone.Protocol.Messages;

namespace AAEmu.Zone.Units;

public sealed partial class ZoneUnitRegistry
{
    //Native39456D70/39457170 active timeline types are independent of the fields on each unit.
    private readonly Dictionary<uint, uint> _castingTimelineTypes = [];
    private readonly Dictionary<uint, CastingPlotOwners> _castingPlotOwners = [];
    private sealed record CastingPlotOwners(ZoneUnit? Casting, ZoneUnit? Channeling);

    /// <summary>
    /// Synchronous copied acknowledgments for the exact current unit allocation. Failure Code!=0
    /// is retained. Fire is observed after clearing cast/current, before assigning its channel;
    /// End is observed after clearing matching timelines. This does not execute plot effects.
    /// </summary>
    public event Action<ZoneUnit, ZoneUnitSkillAcknowledgement>? SkillAcknowledged;

    private ZoneUnit? CastingUnit(SkillCasterRef caster) =>
        caster.Type is 0 or 2 or 3 ? _units.GetValueOrDefault(caster.ObjectId) : null;

    private ZoneSkillContent CastingContent(ZoneUnit? unit) => _content?.Skills ?? unit?.SkillContent ?? ZoneSkillContent.Empty;

    public bool ApplySkillStarted(SkillStarted message)
    {
        var unit = CastingUnit(message.Caster);
        if (!CastingContent(unit).Skills.TryGetValue(message.SkillId, out var skill)) return false;
        if (message.Result.Code != 0 && unit is not null)
        {
            Acknowledge(unit, ZoneUnitSkillAcknowledgementKind.Started,
                message.SkillId, message.Timeline, message.Result);
            return true;
        }
        if (unit is not null)
        {
            unit.Casting.Start(message.Timeline, message.SkillId, skill.TimingId, message.CastTime);
            if (message.Timeline != 0)
                Acknowledge(unit, ZoneUnitSkillAcknowledgementKind.Started, message.SkillId, message.Timeline, message.Result);
        }
        if (message.Timeline != 0 || unit is null) _castingTimelineTypes[message.Timeline] = message.SkillId;
        return true;
    }

    public bool ApplySkillFired(SkillFired message)
    {
        //39457170 synthesizes a successful zero-cast-time Start before resolving a missing timeline.
        if (!_castingTimelineTypes.ContainsKey(message.Timeline))
            ApplySkillStarted(new(message.SkillId, message.Timeline, message.Caster, message.Target, 0));
        var unit = CastingUnit(message.Caster);
        if (!CastingContent(unit).Skills.TryGetValue(message.SkillId, out var skill)) return false;
        if (unit is not null)
        {
            if (message.Result.Code == 0) unit.Casting.BeginFire();
            Acknowledge(unit, ZoneUnitSkillAcknowledgementKind.Fired, message.SkillId, message.Timeline, message.Result);
            //A synchronous consumer may retire this allocation. Never mutate its successor on return.
            if (message.Result.Code == 0 && IsCurrent(unit))
                unit.Casting.CompleteFire(message.Timeline, skill.ChannelingMilliseconds);
        }
        if (message.Result.Code == 0 || unit is null) _castingTimelineTypes[message.Timeline] = message.SkillId;
        return true;
    }

    public bool ApplySkillEnded(SkillEnded message)
    {
        _castingTimelineTypes.Remove(message.Timeline, out var skillId);
        var unit = CastingUnit(message.Caster);
        if (unit is null) return false;
        unit.Casting.End(message.Timeline);
        Acknowledge(unit, ZoneUnitSkillAcknowledgementKind.Ended, skillId, message.Timeline, SkillCastResult.Success);
        return true;
    }

    public bool ApplySkillStopped(SkillStopped message)
    {
        if (!_units.TryGetValue(message.ObjectId, out var unit)) return false;
        unit.Casting.StopSkill(message.SkillId);
        return true;
    }

    /// <summary>39456630: compare actual cast timeline, clear cast/current, then start World's resolved cooldown.</summary>
    public bool ApplyCastingStopped(CastingStopped message)
    {
        if (!_units.TryGetValue(message.ObjectId, out var unit) || !unit.Casting.StopCasting(message.TimelineId)) return false;
        if (message.Duration != 0)
            unit.Cooldowns.StartSkill(unchecked((uint)message.SkillId), unchecked((uint)message.Duration));
        return true;
    }

    public bool ApplyPlotEvent(PlotEvent message)
    {
        var caster = message.Caster.Type == 1 ? _units.GetValueOrDefault(message.Caster.ObjectId) : null;
        var content = CastingContent(caster);
        if (!content.Skills.ContainsKey(message.SkillId) || !content.PlotEvents.TryGetValue(message.EventId, out var plotEvent)
            || !content.PlotIds.Contains(plotEvent.PlotId)) return false;
        var owners = _castingPlotOwners.GetValueOrDefault(message.Timeline) ?? new(null, null);
        if (message.ObjectId != 0 && _units.TryGetValue(message.ObjectId, out var previousCast))
        {
            previousCast.Casting.ClearCurrentPlot(message.Timeline);
            owners = owners with { Casting = null };
        }
        //39455730 uses its retained a9 unit ID in this branch too, not the nonzero a11 guard.
        if (message.ChannelingObjectId != 0 && _units.TryGetValue(message.ObjectId, out var previousChannel))
        {
            previousChannel.Casting.ClearChannelPlot(message.Timeline);
            owners = owners with { Channeling = null };
        }
        if (caster is not null)
        {
            caster.Casting.StartPlot(message.Timeline, message.CastTimeMs, message.ChannelingTimeMs);
            if (message.CastTimeMs != 0) owners = owners with { Casting = caster };
            if (message.ChannelingTimeMs != 0) owners = owners with { Channeling = caster };
        }
        StorePlotOwners(message.Timeline, owners);
        return true;
    }

    public bool ApplyPlotCastingStopped(PlotStopped message) => StopPlotCasting(unchecked((ushort)message.Timeline));

    private bool StopPlotCasting(uint timeline)
    {
        if (!_castingPlotOwners.TryGetValue(timeline, out var owners) || owners.Casting is not { } unit || !IsCurrent(unit)
            || !unit.Casting.StopPlotCasting(timeline)) return false;
        StorePlotOwners(timeline, owners with { Casting = null });
        return true;
    }

    public bool ApplyPlotChannelingStopped(PlotStopped message)
    {
        var timeline = (uint)unchecked((ushort)message.Timeline);
        if (!_castingPlotOwners.TryGetValue(timeline, out var owners) || owners.Channeling is not { } unit || !IsCurrent(unit)
            || !unit.Casting.StopPlotChanneling(timeline)) return false;
        StorePlotOwners(timeline, owners with { Channeling = null });
        return true;
    }

    //39455D20 stops an owned casting phase; it does not stop a retained channel phase.
    public bool ApplyPlotEnded(PlotEnded message) => StopPlotCasting(unchecked((ushort)message.Timeline));

    private void Acknowledge(ZoneUnit unit, ZoneUnitSkillAcknowledgementKind kind, uint skillId, uint timeline,
        SkillCastResult result) => SkillAcknowledged?.Invoke(unit, new(kind, skillId, timeline, result));

    private void StorePlotOwners(uint timeline, CastingPlotOwners owners)
    {
        if (owners.Casting is null && owners.Channeling is null) _castingPlotOwners.Remove(timeline);
        else _castingPlotOwners[timeline] = owners;
    }

    private void RetireCastingUnit(ZoneUnit unit)
    {
        foreach (var (timeline, owners) in _castingPlotOwners.ToArray())
            if (ReferenceEquals(owners.Casting, unit) || ReferenceEquals(owners.Channeling, unit))
                StorePlotOwners(timeline, new(ReferenceEquals(owners.Casting, unit) ? null : owners.Casting,
                    ReferenceEquals(owners.Channeling, unit) ? null : owners.Channeling));
    }
}
