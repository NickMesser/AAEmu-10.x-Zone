using System.Collections.ObjectModel;
using System.Text;
using AAEmu.Zone.Content;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;

namespace AAEmu.Zone.Simulation;

public readonly record struct NpcSignalCooldown(uint EventId, uint TargetObjectId, float Deadline);

/// <summary>39326B40: event requirements, temporary skill targets, dialogue selection and repeat suppression.</summary>
public sealed class ZoneNpcSignals
{
    private readonly ZoneNpc _npc;
    private readonly ZoneNpcSignalContent _content;
    private readonly IZoneNpcSignalRuntime _runtime;
    private readonly Func<uint, ZoneUnit?> _findUnit;
    private readonly Action<ZoneFrame> _notify;
    private readonly List<NpcSignalCooldown> _cooldowns = [];
    private bool _disposed;
    public IReadOnlyList<NpcSignalCooldown> Cooldowns { get; }

    internal ZoneNpcSignals(ZoneNpc npc, ZoneNpcSignalContent content, IZoneNpcSignalRuntime runtime,
        Func<uint, ZoneUnit?> findUnit, Action<ZoneFrame> notify)
    {
        _npc = npc; _content = content; _runtime = runtime; _findUnit = findUnit; _notify = notify;
        Cooldowns = new ReadOnlyCollection<NpcSignalCooldown>(_cooldowns);
    }

    internal void Process(uint requestedTargetId, string signal)
    {
        if (_disposed) return;
        var subject = _findUnit(requestedTargetId) ?? _npc.Unit;
        if (_runtime.DebugNpcObjectId == _npc.ObjectId)
        {
            var name = requestedTargetId == 0 ? "no target" : CString(_runtime.GetDebugUnitName(subject));
            _notify(new NpcSpeech(_npc.ObjectId, subject.Snapshot.ObjectId, 0, TruncateText($"{CString(signal)} => {name}")).ToFrame());
        }
        foreach (var rule in _content.ForNpc(_npc.TemplateId))
        {
            if (_disposed) return;
            var definition = rule.Event;
            if (!SameName(definition.Name, signal) || IsSuppressed(definition, requestedTargetId) || !MeetsRequirements(rule, subject)) continue;
            if (_disposed) return;
            var used = false;
            if (definition.SkillId != 0 && _runtime.CanUseSkill(_npc, definition.SkillId))
            {
                if (_disposed) return;
                var previousTargetId = _npc.Unit.TargetId;
                SetTarget(subject);
                used = _runtime.UseSkill(_npc, definition.SkillId);
                if (_disposed) return;
                // Native compares the previous ID with the resolved signal subject, not the post-skill target.
                if (previousTargetId != subject.Snapshot.ObjectId) SetTarget(_findUnit(previousTargetId));
            }
            if (_disposed) return;
            if (rule.Bubbles.Count != 0 && (used || definition.SkillId == 0))
            {
                var index = (int)(((ulong)_runtime.NextRandomUInt32() * (uint)rule.Bubbles.Count) >> 32);
                if (_disposed) return;
                _notify(new NpcSpeech(_npc.ObjectId, subject.Snapshot.ObjectId, 1, MessageId: rule.Bubbles[index].Id).ToFrame());
                used = true;
            }
            // 39345500: only a successful skill or selected bubble starts suppression.
            // Eligibility used the requested ID; insertion uses the resolved ID (self when missing).
            // COMISS zero,duration / JAE skips nonpositive values but retains unordered (NaN) durations.
            if (used && !(definition.IgnoreTime <= 0))
                _cooldowns.Add(new(definition.Id, subject.Snapshot.ObjectId, _runtime.TimeSeconds + definition.IgnoreTime));
        }
    }

    private bool IsSuppressed(NpcAiEventDefinition candidate, uint targetId)
    {
        // 3933BB90 consults the stored event's category, not the candidate's. No deadline check here.
        foreach (var cooldown in _cooldowns)
            if (_content.Events.TryGetValue(cooldown.EventId, out var rule) && SameName(rule.Event.Name, candidate.Name) &&
                (rule.Event.IgnoreCategory == NpcSignalIgnoreCategory.Npc ||
                 (rule.Event.IgnoreCategory == NpcSignalIgnoreCategory.Target && cooldown.TargetObjectId == targetId))) return true;
        return false;
    }

    private bool MeetsRequirements(NpcSignalDefinition rule, ZoneUnit subject)
    {
        // 39361840: an empty list passes even for OR. Evaluation short-circuits in source order.
        if (rule.Requirements.Count == 0) return true;
        foreach (var requirement in rule.Requirements)
        {
            // 39361590 bypasses these client/World-only checks on Zone.
            var passed = requirement.KindId is 0x14 or 0x18 or 0x1B or 0x27 or 0x67 or 0x6C or 0x6D or 0x6E ||
                _runtime.CheckRequirement(requirement, subject, _npc.Unit);
            if (_disposed) return false;
            if (passed && rule.Event.OrUnitRequirements) return true;
            if (!passed && !rule.Event.OrUnitRequirements) return false;
        }
        return !rule.Event.OrUnitRequirements;
    }

    private void SetTarget(ZoneUnit? target)
    {
        var id = target?.Snapshot.ObjectId ?? 0;
        // 3935CCC0 -> 392628D0 writes the ID and always emits ZWChangeTarget, including unchanged IDs.
        _npc.Unit.SetTarget(id);
        _notify(new ZoneTargetChanged(_npc.ObjectId, id).ToFrame());
    }

    internal void UpdateCooldowns()
    {
        for (var index = 0; index < _cooldowns.Count; index++)
        {
            var cooldown = _cooldowns[index];
            // 39343250 samples time separately for each resolvable record and erases at most one.
            // COMISS/JA means strict greater-than: equal or unordered (NaN) deadlines remain.
            if (_content.Events.ContainsKey(cooldown.EventId) && _runtime.TimeSeconds > cooldown.Deadline)
            { _cooldowns.RemoveAt(index); return; }
        }
    }

    private static bool SameName(string first, string second) => CString(first).AsSpan().SequenceEqual(CString(second));
    private static string CString(string value)
    {
        var end = value.IndexOf('\0');
        return end >= 0 ? value[..end] : value;
    }

    private static string TruncateText(string text)
    {
        Span<byte> bytes = stackalloc byte[NpcSpeech.MaximumTextBytes];
        Encoding.UTF8.GetEncoder().Convert(text.AsSpan(), bytes, true, out _, out var written, out _);
        return Encoding.UTF8.GetString(bytes[..written]);
    }

    internal void Dispose() { _disposed = true; _cooldowns.Clear(); }
}
