using System.Collections.ObjectModel;
using AAEmu.Zone.Content;
using AAEmu.Zone.Protocol.Messages;

namespace AAEmu.Zone.Combat;

/// <summary>Native cooldown entry: remaining time, original duration, and (for charge recovery) missing charges.</summary>
public readonly record struct ZoneCooldownTimer(uint RemainingMilliseconds, uint DurationMilliseconds, uint MissingCharges = 0);

/// <summary>
/// One unit's cooldown maps (unit+31912). Mirrors 39BF7A90, 39BF82E0..85C0 and 39BF9120..A2A0.
/// Starts take an already resolved duration: equipment/buff skill modifiers remain the caller's responsibility.
/// Owned by the unit allocation, so removal and reuse of an object ID cannot retain its timers.
/// </summary>
public sealed class ZoneCooldownManager
{
    public const int MaxTimersPerKind = 150;
    private readonly ZoneSkillContent _content;
    private readonly Dictionary<uint, ZoneCooldownTimer> _skills = [];
    private readonly Dictionary<uint, ZoneCooldownTimer> _tags = [];
    private readonly Dictionary<uint, ZoneCooldownTimer> _charges = [];
    public IReadOnlyDictionary<uint, ZoneCooldownTimer> Skills { get; }
    public IReadOnlyDictionary<uint, ZoneCooldownTimer> Tags { get; }
    public IReadOnlyDictionary<uint, ZoneCooldownTimer> Charges { get; }
    public ZoneCooldownTimer Global { get; private set; }

    public ZoneCooldownManager(ZoneSkillContent content)
    {
        _content = content ?? throw new ArgumentNullException(nameof(content));
        Skills = new ReadOnlyDictionary<uint, ZoneCooldownTimer>(_skills);
        Tags = new ReadOnlyDictionary<uint, ZoneCooldownTimer>(_tags);
        Charges = new ReadOnlyDictionary<uint, ZoneCooldownTimer>(_charges);
    }

    /// <summary>39BFA070: skills without cooldown tags use their own timer; tagged skills share the tag timers.</summary>
    public bool StartSkill(uint skillId, uint durationMilliseconds)
    {
        if (!_content.Skills.TryGetValue(skillId, out var skill)) return false;
        if (durationMilliseconds == 0) return true;
        var tags = CooldownTags(skill).ToArray();
        if (tags.Length == 0)
        {
            if (_skills.Count >= MaxTimersPerKind) return false;
            Extend(_skills, skillId, durationMilliseconds);
            return true;
        }
        if (_tags.Count >= MaxTimersPerKind) return false;

        // 39BF9C90: switched skills receive individual timers; only an unswitched member
        // enables the shared tag timers. Deduplicate membership across all three tags.
        var shared = false;
        foreach (var taggedId in tags.SelectMany(_content.SkillsWithTag).Distinct())
            if (_content.Skills[taggedId].SwitchToSkillCooldown)
                Extend(_skills, taggedId, durationMilliseconds);
            else
                shared = true;
        if (shared)
            foreach (var tag in tags) Extend(_tags, tag, durationMilliseconds);
        return true;
    }

    public void StartGlobal(uint durationMilliseconds) => Global = new(durationMilliseconds, durationMilliseconds);

    /// <summary>Installs resolved charge-recovery state; ordinary cooldown resets deliberately leave this map alone.</summary>
    public bool StartChargeRecovery(uint skillId, uint remainingMilliseconds, uint durationMilliseconds, uint missingCharges)
    {
        if (!_content.Skills.TryGetValue(skillId, out var skill) || skill.ChargeCount == 0 ||
            (!_charges.ContainsKey(skillId) && _charges.Count >= MaxTimersPerKind)) return false;
        if (remainingMilliseconds > durationMilliseconds || missingCharges > skill.ChargeCount)
            throw new ArgumentOutOfRangeException(nameof(remainingMilliseconds), "Invalid charge-recovery state.");
        _charges[skillId] = new(remainingMilliseconds, durationMilliseconds, missingCharges);
        return true;
    }

    public uint GetRemaining(uint skillId, bool includeGlobal = true)
    {
        var remaining = _skills.GetValueOrDefault(skillId).RemainingMilliseconds;
        if (_content.Skills.TryGetValue(skillId, out var skill))
        {
            if (!skill.SwitchToSkillCooldown)
                foreach (var tag in _content.TagsForSkill(skillId))
                    remaining = Math.Max(remaining, _tags.GetValueOrDefault(tag).RemainingMilliseconds);
        }
        return includeGlobal ? Math.Max(remaining, Global.RemainingMilliseconds) : remaining;
    }

    /// <summary>39BF8180: charge availability is separate from the ordinary skill/tag/GCD query.</summary>
    public uint GetAvailableCharges(uint skillId) => _content.Skills.TryGetValue(skillId, out var skill)
        ? skill.ChargeCount - Math.Min(skill.ChargeCount, _charges.GetValueOrDefault(skillId).MissingCharges) : 0;

    /// <summary>39BF8110: time to recover the next charge rather than the entire pending charge queue.</summary>
    public uint GetChargeRecoveryRemaining(uint skillId) => _charges.TryGetValue(skillId, out var timer) &&
        _content.Skills.TryGetValue(skillId, out var skill)
            ? unchecked(skill.ChargeCooldownMilliseconds + timer.RemainingMilliseconds - timer.DurationMilliseconds) : 0;

    public void Reset(SkillCooldownReset message)
    {
        _skills.Remove(message.SkillId); // skill/tag zero are exact keys, never a reset-all request.
        _tags.Remove(message.TagId);
        if (message.GlobalCooldown) Global = default;
        if (message.SkillTags)
            foreach (var tag in _content.TagsForSkill(message.SkillId)) _tags.Remove(tag);
        if (message.TaggedSkills)
            foreach (var skill in _content.SkillsWithTag(message.TagId))
            {
                _skills.Remove(skill);
                if (message.TaggedSkillTags)
                    foreach (var tag in _content.TagsForSkill(skill)) _tags.Remove(tag);
            }
    }

    public void Reduce(SkillCooldownReduce message)
    {
        Adjust(_skills, message.SkillId, message.Adjustment);
        Adjust(_tags, message.TagId, message.Adjustment);
        if (message.SkillTags)
            foreach (var tag in _content.TagsForSkill(message.SkillId)) Adjust(_tags, tag, message.Adjustment);
        if (!message.TaggedSkills) return;
        var relatedTags = new HashSet<uint>();
        foreach (var skill in _content.SkillsWithTag(message.TagId))
        {
            Adjust(_skills, skill, message.Adjustment);
            if (message.TaggedSkillTags) relatedTags.UnionWith(_content.TagsForSkill(skill));
        }
        // 39BFA2A0 collects a set, so two tagged skills sharing a tag reduce that timer once.
        foreach (var tag in relatedTags) Adjust(_tags, tag, message.Adjustment);
    }

    public void ChangeCharge(ChargeSkillCooldownChange message)
    {
        if (!_charges.TryGetValue(message.SkillId, out var timer) || !_content.Skills.TryGetValue(message.SkillId, out var skill)) return;
        var adjustment = message.Adjustment;
        if (adjustment.Count == 0)
        {
            Adjust(_charges, message.SkillId, adjustment);
            return;
        }
        var count = (uint)Math.Abs((long)adjustment.Count);
        var time = unchecked(count * skill.ChargeCooldownMilliseconds);
        if (adjustment.Count > 0)
            timer = new(timer.RemainingMilliseconds - Math.Min(time, timer.RemainingMilliseconds),
                timer.DurationMilliseconds - Math.Min(time, timer.DurationMilliseconds),
                timer.MissingCharges - Math.Min(count, timer.MissingCharges));
        else
        {
            // 39BF85C0 caps the added count/time by the skill's capacity, not by the free capacity.
            var addedTime = Math.Min(time, unchecked(skill.ChargeCooldownMilliseconds * skill.ChargeCount));
            timer = new(unchecked(timer.RemainingMilliseconds + addedTime), unchecked(timer.DurationMilliseconds + addedTime),
                unchecked(timer.MissingCharges + Math.Min(count, skill.ChargeCount)));
        }
        _charges[message.SkillId] = timer;
    }

    public void Advance(uint elapsedMilliseconds)
    {
        Advance(_skills, elapsedMilliseconds);
        Advance(_tags, elapsedMilliseconds);
        foreach (var (id, original) in _charges.ToArray())
        {
            var timer = original with { RemainingMilliseconds = original.RemainingMilliseconds - Math.Min(elapsedMilliseconds, original.RemainingMilliseconds) };
            if (timer.RemainingMilliseconds == 0 && timer.MissingCharges == 0) { _charges.Remove(id); continue; }
            if (!_content.Skills.TryGetValue(id, out var skill)) { _charges[id] = timer; continue; }
            // 39BF7A90 restores at most one charge per update and restarts the remaining
            // recovery interval from the reduced count, discarding overshoot for that tick.
            if (timer.RemainingMilliseconds <= unchecked(timer.DurationMilliseconds - skill.ChargeCooldownMilliseconds))
            {
                if (timer.MissingCharges <= 1) { _charges.Remove(id); continue; }
                var missing = timer.MissingCharges - 1;
                var duration = unchecked(missing * skill.ChargeCooldownMilliseconds);
                timer = new(duration, duration, missing);
            }
            _charges[id] = timer;
        }
        Global = Global with { RemainingMilliseconds = Global.RemainingMilliseconds - Math.Min(elapsedMilliseconds, Global.RemainingMilliseconds) };
    }

    private static IEnumerable<uint> CooldownTags(SkillDefinition skill) =>
        new[] { skill.CooldownTagId, skill.SecondCooldownTagId, skill.ThirdCooldownTagId }.Where(id => id != 0).Distinct();

    private static void Extend(Dictionary<uint, ZoneCooldownTimer> timers, uint id, uint duration)
    {
        if (timers.GetValueOrDefault(id).RemainingMilliseconds < duration) timers[id] = new(duration, duration);
    }

    private static void Adjust(Dictionary<uint, ZoneCooldownTimer> timers, uint id, SkillCooldownAdjustment adjustment)
    {
        if (!timers.TryGetValue(id, out var timer)) return;
        var remaining = adjustment.Percent != 0
            ? unchecked((int)((float)(int)timer.RemainingMilliseconds - (float)adjustment.Percent * .01f * (float)(int)timer.DurationMilliseconds))
            : unchecked((int)timer.RemainingMilliseconds - adjustment.Milliseconds);
        timers[id] = timer with { RemainingMilliseconds = remaining < 0 ? 0 : Math.Min((uint)remaining, timer.DurationMilliseconds) };
    }

    private static void Advance(Dictionary<uint, ZoneCooldownTimer> timers, uint elapsed)
    {
        foreach (var (id, timer) in timers.ToArray())
            if (elapsed >= timer.RemainingMilliseconds) timers.Remove(id);
            else timers[id] = timer with { RemainingMilliseconds = timer.RemainingMilliseconds - elapsed };
    }
}
