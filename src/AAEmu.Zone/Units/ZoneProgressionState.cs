using AAEmu.Zone.Content;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Protocol.Units;

namespace AAEmu.Zone.Units;

public sealed record ZoneProgressionState(byte Level, IReadOnlyList<uint> Skills, ZonePassiveState Passives)
{
    public static ZoneProgressionState FromSnapshot(UnitStateSnapshot snapshot, ZoneStatContent? content = null,
        UnitBuffs? buffs = null)
    {
        var level = unchecked((byte)snapshot.Level);
        var passives = ZonePassiveState.FromSnapshot(snapshot.PassiveBuffs);
        if (content is not null)
        {
            buffs ??= snapshot.Buffs;
            // 3935D260 -> 39450870 applies initial callbacks in good/bad/hidden order.
            foreach (var buff in buffs.Good.Concat(buffs.Bad).Concat(buffs.Hidden))
                if (content.Buffs.Templates.TryGetValue(buff.TemplateId, out var template))
                    passives = passives.Grant(template, content, snapshot, level);
        }
        return new(level, Array.AsReadOnly(snapshot.Skills.Distinct().Order().ToArray()), passives);
    }

    public ZoneProgressionState Reset(byte ability, ZoneStatContent content) => this with
    {
        Skills = Array.AsReadOnly(Skills.Where(id => content.Skills.Skills.TryGetValue(id, out var skill) && skill.AbilityId != ability).ToArray()),
        Passives = Passives.Reset(ability, content.Buffs)
    };
}

/// <summary>Native passive manager: current sorted vector, saved/granted vector, and one grant owner.</summary>
public sealed record ZonePassiveState(IReadOnlyList<uint> Current, IReadOnlyList<uint> Swap, uint GrantOwner)
{
    public static ZonePassiveState FromSnapshot(IEnumerable<uint> ids) => new(Unique(ids), [], 0);

    public ZonePassiveState Learn(uint passiveId, ZoneBuffContent content) =>
        !content.Passives.ContainsKey(passiveId) || Current.Contains(passiveId) ? this : this with { Current = Ordered(Current.Append(passiveId)) };

    public ZonePassiveState Reset(byte ability, ZoneBuffContent content) => this with
    {
        // 39D2AD10 removes unknown IDs and the selected ability from the current vector only.
        Current = Ordered(Current.Where(id => content.Passives.TryGetValue(id, out var passive) && passive.AbilityId != ability))
    };

    public ZonePassiveState Grant(BuffStatTemplate template, ZoneStatContent content, UnitStateSnapshot snapshot, byte level)
    {
        if (GrantOwner != 0 || content.Buffs.GrantedPassives(template.Id).Count == 0) return this;
        var granted = Eligible(template.Id, content, snapshot, level);
        if (!template.OffPassive)
            // 39D2B3A0 appends and sorts without deduplication; removal deletes one copy per grant.
            return new(Ordered(Current.Concat(granted)), granted, template.Id);
        var exceptions = Current.Where(id => content.Buffs.Passives.TryGetValue(id, out var passive) &&
            template.PassiveExceptionTagId != 0 && content.Buffs.Templates.ContainsKey(passive.BuffId) &&
            content.Buffs.HasTag(passive.BuffId, template.PassiveExceptionTagId));
        return new(Unique(granted.Concat(exceptions)), Current, template.Id);
    }

    public ZonePassiveState RemoveGrant(BuffStatTemplate removedTemplate, ZoneBuffContent content)
    {
        // 39D2BE90 checks the removed template's relation, not equality with the current owner.
        if (content.GrantedPassives(removedTemplate.Id).Count == 0) return this;
        if (removedTemplate.OffPassive) return new(Swap, [], 0);
        var remaining = Current.ToList();
        foreach (var id in Swap) remaining.Remove(id);
        return new(remaining.AsReadOnly(), [], 0);
    }

    public ZonePassiveState AddSwap(uint templateId, ZoneStatContent content, UnitStateSnapshot snapshot, byte level)
    {
        if (GrantOwner == 0) return this;
        var extra = Eligible(templateId, content, snapshot, level).Where(id => !Current.Contains(id)).ToArray();
        // 39D2B7F0 leaves the saved vector unchanged, so OffPassive restoration discards these extras.
        return extra.Length == 0 ? this : this with { Current = Ordered(Current.Concat(extra)) };
    }

    private static IReadOnlyList<uint> Eligible(uint templateId, ZoneStatContent content, UnitStateSnapshot snapshot, byte level) =>
        Unique(content.Buffs.GrantedPassives(templateId).Where(id => content.Buffs.Passives.TryGetValue(id, out var passive) &&
            content.Skills.AbilityLevel(snapshot, passive.AbilityId, level) >= passive.RequiredLevel));
    private static System.Collections.ObjectModel.ReadOnlyCollection<uint> Unique(IEnumerable<uint> ids) => Ordered(ids.Distinct());
    private static System.Collections.ObjectModel.ReadOnlyCollection<uint> Ordered(IEnumerable<uint> ids) => Array.AsReadOnly(ids.Order().ToArray());
}
