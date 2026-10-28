using AAEmu.Zone.Protocol.Messages;

namespace AAEmu.Zone.Units;

/// <summary>Copied unit +32280/+32284/+32288/+32292 and the independent casting byte +32297.</summary>
/// <remarks>
/// CurrentSkill and ChannelSkill retain the native names used by the request consumer. Their plot
/// producers store a plot timeline/control ID, not the skill descriptor's type. None of these fields
/// can be reconstructed from Casting or from the active skill map.
/// </remarks>
public readonly record struct ZoneUnitCastingSnapshot(
    uint CastTimeline, uint CurrentSkill, uint ChannelTimeline, uint ChannelSkill, bool Casting);

public enum ZoneUnitSkillAcknowledgementKind { Started, Fired, Ended }

/// <summary>The copied arguments delivered at native model proxy +24/+32/+40.</summary>
public readonly record struct ZoneUnitSkillAcknowledgement(
    ZoneUnitSkillAcknowledgementKind Kind, uint SkillId, uint Timeline, SkillCastResult Result);

/// <summary>One unit allocation's independent casting fields. Only its sequential World consumer writes them.</summary>
public sealed class ZoneUnitCastingState
{
    private ZoneUnitCastingSnapshot _state;

    internal ZoneUnitCastingState(UnitStateSnapshot admission)
    {
        var fields = admission.CastingFields;
        _state = new(fields.CastTimeline, fields.CurrentSkill, fields.ChannelTimeline, fields.ChannelSkill,
            (admission.Flags & 2) != 0);
    }

    public uint CastTimeline => _state.CastTimeline;
    public uint CurrentSkill => _state.CurrentSkill;
    public uint ChannelTimeline => _state.ChannelTimeline;
    public uint ChannelSkill => _state.ChannelSkill;
    public bool Casting => _state.Casting;
    public ZoneUnitCastingSnapshot CopySnapshot() => _state;

    internal void Start(uint timeline, uint skillId, uint timingId, uint castTime)
    {
        //39456D70: timeline zero has the independent skill-type-2 casting-byte path.
        if (timeline == 0)
        {
            if (skillId == 2) _state = _state with { Casting = true };
        }
        else if (timingId == 1 && castTime != 0)
            _state = _state with { CastTimeline = timeline };
    }

    internal void BeginFire() => _state = _state with { CastTimeline = 0, CurrentSkill = 0 };
    internal void CompleteFire(uint timeline, int channelingTime)
    {
        if (channelingTime > 0) _state = _state with { ChannelTimeline = timeline };
    }

    internal void End(uint timeline)
    {
        //3925A1B0/3925A210 accept zero as "the current one" independently for each field.
        _state = _state with
        {
            CastTimeline = timeline == 0 || timeline == _state.CastTimeline ? 0 : _state.CastTimeline,
            ChannelTimeline = timeline == 0 || timeline == _state.ChannelTimeline ? 0 : _state.ChannelTimeline
        };
    }

    internal void StopSkill(int skillId)
    {
        if (skillId == 2) ClearCastingFlag(); //39456600 compares the full skill type to original constant 2.
    }

    internal bool StopCasting(uint timeline)
    {
        if (_state.CastTimeline != timeline) return false;
        BeginFire(); //39456630 clears exactly these two fields before resolving a cooldown.
        return true;
    }

    internal void StartPlot(uint timeline, uint castTime, uint channelingTime)
    {
        if (castTime != 0) _state = _state with { CurrentSkill = timeline };
        if (channelingTime != 0) _state = _state with { ChannelSkill = timeline };
    }

    internal void ClearCurrentPlot(uint timeline)
    {
        if (timeline == 0 || timeline == _state.CurrentSkill) _state = _state with { CurrentSkill = 0 };
    }

    internal void ClearChannelPlot(uint timeline)
    {
        if (timeline == 0 || timeline == _state.ChannelSkill) _state = _state with { ChannelSkill = 0 };
    }

    internal bool StopPlotCasting(uint timeline)
    {
        if (_state.CurrentSkill != timeline) return false;
        BeginFire();
        return true;
    }

    internal bool StopPlotChanneling(uint timeline)
    {
        if (_state.ChannelSkill != timeline) return false;
        _state = _state with { ChannelTimeline = 0, ChannelSkill = 0 };
        return true;
    }

    internal void ClearCastingFlag() => _state = _state with { Casting = false };
    internal void ClearIdentifiers() => _state = _state with
        { CastTimeline = 0, CurrentSkill = 0, ChannelTimeline = 0, ChannelSkill = 0 };
    internal void Reset() => _state = default;
}
