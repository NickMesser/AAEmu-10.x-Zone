using AAEmu.Zone.Content;
using AAEmu.Zone.Protocol.Units;

namespace AAEmu.Zone.Simulation;

public sealed record ZoneBuffTimingResult(UnitBuffs Buffs, IReadOnlyList<uint> InvalidatedAttributes);
public sealed record ZoneBuffInstanceTimingResult(UnitBuff Buff, IReadOnlyList<uint> InvalidatedAttributes);

/// <summary>
/// ZoneBuffMan's replicated elapsed-time and dynamic-stat tick advancement at 3944FC90.
/// World retains buff expiry and periodic combat effects; crossing the duration does not remove an instance here.
/// </summary>
public static class ZoneBuffTiming
{
    /// <summary>3944F850: characters update for dynamic modifiers; NPCs update for remove-on-land buffs.</summary>
    public static bool RequiresUpdate(ZoneUnitKind kind, UnitBuffs buffs, ZoneBuffContent content)
    {
        ArgumentNullException.ThrowIfNull(buffs);
        ArgumentNullException.ThrowIfNull(content);
        if (kind is not (ZoneUnitKind.Character or ZoneUnitKind.Npc)) return false;
        foreach (var buff in buffs.Good.Concat(buffs.Bad).Concat(buffs.Hidden))
        {
            if (!content.Templates.TryGetValue(buff.TemplateId, out var template)) continue;
            if (kind == ZoneUnitKind.Character ? content.Curves(buff.TemplateId).Count != 0 : template.RemoveOnLand)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Advance a manager already selected by the unit update gate. The caller supplies the unchecked
    /// millisecond clock difference used by 3935DC20. Missing template definitions remain untouched.
    /// </summary>
    public static ZoneBuffTimingResult Advance(UnitBuffs current, ZoneBuffContent content, uint elapsedMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(content);
        var invalidated = new List<uint>();
        // Native 39450A70 visits good, bad, then hidden arrays, preserving instance and modifier order.
        var good = AdvanceList(current.Good);
        var bad = AdvanceList(current.Bad);
        var hidden = AdvanceList(current.Hidden);
        return new(new(good, bad, hidden), invalidated.AsReadOnly());

        IReadOnlyList<UnitBuff> AdvanceList(IReadOnlyList<UnitBuff> buffs)
        {
            var result = new UnitBuff[buffs.Count];
            for (var index = 0; index < buffs.Count; index++)
            {
                result[index] = AdvanceInstance(buffs[index], content, elapsedMilliseconds, invalidated);
            }
            return Array.AsReadOnly(result);
        }
    }

    /// <summary>Advance one instance before its landing decision and the next instance's update.</summary>
    public static ZoneBuffInstanceTimingResult Advance(UnitBuff current, ZoneBuffContent content, uint elapsedMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(content);
        var invalidated = new List<uint>();
        return new(AdvanceInstance(current, content, elapsedMilliseconds, invalidated), invalidated.AsReadOnly());
    }

    private static UnitBuff AdvanceInstance(UnitBuff buff, ZoneBuffContent content, uint elapsedMilliseconds,
        List<uint> invalidated)
    {
        if (!content.Templates.ContainsKey(buff.TemplateId)) return buff;
        var elapsed = unchecked(buff.Elapsed + elapsedMilliseconds);
        var tickIndex = buff.TickIndex;
        if (buff.Tick != 0 &&
            (buff.Duration == 0 || unchecked((int)tickIndex) < unchecked((int)(buff.Duration / buff.Tick))) &&
            unchecked((tickIndex + 1) * buff.Tick) <= elapsed)
        {
            // One invalidation per modifier for this update, even when several tick boundaries were crossed.
            tickIndex = elapsed / buff.Tick;
            foreach (var curve in content.Curves(buff.TemplateId)) invalidated.Add(curve.Attribute);
        }
        return buff with { Elapsed = elapsed, TickIndex = tickIndex };
    }
}
