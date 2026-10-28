using AAEmu.Zone.Content;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Protocol.Units;

namespace AAEmu.Zone.Combat;

/// <summary>Native ordered buff arrays and 39D28760 stacking; returns new state before any unit mutation.</summary>
public static class ZoneBuffLifecycle
{
    public static UnitBuffs? Create(UnitBuffs current, BuffCreated message, BuffStatTemplate template)
        => Create(current, message, template, out _, out _);

    public static UnitBuffs? Create(UnitBuffs current, BuffCreated message, BuffStatTemplate template,
        out UnitBuff? changed, out bool refreshed)
    {
        changed = null; refreshed = false;
        var source = Select(current, template.Kind);
        var index = template.StackRule == 6 ? -1 : Find(source, buff => buff.TemplateId == message.TemplateId &&
            (!template.PerUnitCreation || buff.CasterPersistentId == message.CasterPersistentId));
        if (index < 0 && source.Count == Capacity(template.Kind))
            return null; // 3944EDE0: a full category cannot allocate another buff
        refreshed = index >= 0;
        var next = message.ToUnitBuff();
        if (index >= 0)
        {
            var previous = source[index];
            var duration = next.Duration;
            if (template.StackRule == 5)
            {
                var remaining = unchecked(next.Duration - previous.Elapsed + previous.Duration);
                duration = template.MaximumLifetime == 0 || remaining <= template.MaximumLifetime
                    ? unchecked(previous.Duration + next.Duration) : unchecked(previous.Elapsed + template.MaximumLifetime);
            }
            var elapsed = template.StackRule == 5 ? Math.Max(previous.Elapsed, next.Elapsed) : Math.Min(previous.Elapsed, next.Elapsed);
            next = next with
            {
                Duration = duration, Elapsed = elapsed, TickIndex = next.Tick == 0 ? 0 : elapsed / next.Tick,
                Stack = template.StackRule is 4 or 7 ? unchecked(previous.Stack + next.Stack) : previous.Stack,
                Charge = template.StackRule switch { 2 => next.Charge, 3 => unchecked(previous.Charge + next.Charge), _ => previous.Charge },
                SourceSkill = previous.SourceSkill // native refresh does not replace the original toggle skill
            };
        }
        next = next with { Stack = SignedCap(next.Stack, template.MaximumStack) };
        if (template.StackRule == 3)
            next = next with { Charge = SignedCap(next.Charge, template.MaximumCharge) };
        var result = source.ToList();
        if (index < 0) result.Add(next); else result[index] = next;
        changed = next;
        return Replace(current, template.Kind, result.AsReadOnly());
    }

    public static UnitBuffs? Update(UnitBuffs current, BuffUpdated message, ZoneBuffContent content)
    {
        for (byte kind = 1; kind <= 3; kind++)
        {
            var list = Select(current, kind);
            var index = Find(list, buff => buff.Index == message.Index);
            if (index < 0) continue;
            var previous = list[index];
            if (!content.Templates.TryGetValue(previous.TemplateId, out var template)) return null;
            var resetTicks = previous.Stack != message.Stack && template.StackRule == 7 && message.Reason == 3;
            var next = previous with
            {
                Stack = message.Stack, Charge = message.Charge, Elapsed = resetTicks ? 0 : message.Elapsed,
                TickIndex = resetTicks ? 0 : previous.TickIndex
            };
            var result = list.ToArray();
            result[index] = next;
            return Replace(current, kind, Array.AsReadOnly(result));
        }
        return null;
    }

    public static UnitBuffs? Remove(UnitBuffs current, uint index)
    {
        for (byte kind = 1; kind <= 3; kind++)
        {
            var list = Select(current, kind);
            var offset = Find(list, buff => buff.Index == index);
            if (offset < 0) continue;
            var result = list.ToList();
            result.RemoveAt(offset); // 3944EEC0 shifts following entries, preserving order
            return Replace(current, kind, result.AsReadOnly());
        }
        return null;
    }

    private static uint SignedCap(uint value, int maximum) => unchecked((int)value) > maximum ? unchecked((uint)maximum) : value;
    private static int Find(IReadOnlyList<UnitBuff> values, Func<UnitBuff, bool> match)
    {
        for (var i = 0; i < values.Count; i++) if (match(values[i])) return i;
        return -1;
    }
    private static int Capacity(byte kind) => kind switch { 1 => 32, 2 => 20, 3 => 28, _ => throw new InvalidDataException("Invalid buff category.") };
    private static IReadOnlyList<UnitBuff> Select(UnitBuffs buffs, byte kind) => kind switch
    { 1 => buffs.Good, 2 => buffs.Bad, 3 => buffs.Hidden, _ => throw new InvalidDataException("Invalid buff category.") };
    private static UnitBuffs Replace(UnitBuffs buffs, byte kind, IReadOnlyList<UnitBuff> values) => kind switch
    { 1 => buffs with { Good = values }, 2 => buffs with { Bad = values }, 3 => buffs with { Hidden = values }, _ => throw new InvalidDataException("Invalid buff category.") };
}
