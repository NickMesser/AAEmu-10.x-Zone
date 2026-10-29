using AAEmu.Zone.Content;
using AAEmu.Zone.Protocol.Units;

namespace AAEmu.Zone.Combat;

/// <summary>The BuffMan counters consumed by AIUnitSystem::CanMove39212E70 and CanTurn39212F30.</summary>
public readonly record struct ZoneBuffMovementStatusCounters(byte Stun, byte Sleep, byte Root,
    byte Psychokinesis = 0, byte ImpossibleRotate = 0);

/// <summary>
///39D2CF70/39D2C030: one contribution per actual instance. A refresh (visitor2) returns
///before these counters, independently of stack/charge; invalid byte increments/decrements
///log and retain the previous count. PassiveManager32144 is a separate stat owner.
/// </summary>
public static class ZoneBuffMovementStatus
{
    public static ZoneBuffMovementStatusCounters Add(ZoneBuffMovementStatusCounters current, BuffStatTemplate template) =>
        Change(current, template, 1);
    public static ZoneBuffMovementStatusCounters Remove(ZoneBuffMovementStatusCounters current, BuffStatTemplate template) =>
        Change(current, template, -1);

    public static ZoneBuffMovementStatusCounters Restore(UnitBuffs buffs, ZoneBuffContent content)
    {
        ArgumentNullException.ThrowIfNull(buffs); ArgumentNullException.ThrowIfNull(content);
        var result = default(ZoneBuffMovementStatusCounters);
        foreach (var buff in buffs.Good.Concat(buffs.Bad).Concat(buffs.Hidden))
            //39D2C030 returns unchanged when the actual descriptor lookup fails.
            if (content.Templates.TryGetValue(buff.TemplateId, out var template)) result = Add(result, template);
        return result;
    }

    private static ZoneBuffMovementStatusCounters Change(ZoneBuffMovementStatusCounters current, BuffStatTemplate template, int delta)
    {
        ArgumentNullException.ThrowIfNull(template);
        return new(Next(current.Stun, template.Stun), Next(current.Sleep, template.Sleep), Next(current.Root, template.Root),
            Next(current.Psychokinesis, template.Psychokinesis), Next(current.ImpossibleRotate, template.ImpossibleRotate));
        byte Next(byte count, bool enabled) => !enabled || delta > 0 && count == byte.MaxValue || delta < 0 && count == 0
            ? count : (byte)(count + delta);
    }
}
