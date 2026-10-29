using AAEmu.Zone.Combat;
using AAEmu.Zone.Content;
using AAEmu.Zone.Protocol.Units;

namespace AAEmu.Zone.Units;

public readonly record struct ZoneUnitMovementStatusSnapshot(byte StunCounter, byte SleepCounter, byte RootCounter,
    bool Stunned, bool Sleeping, bool Rooted, byte PsychokinesisCounter = 0, byte ImpossibleRotateCounter = 0,
    bool Psychokinesis = false, bool ImpossibleRotate = false);

public enum ZoneUnitBuffEffectKind { Apply, Remove }

/// <summary>The actual effect call boundary, separately from cache contribution or current buff presence.</summary>
public sealed record ZoneUnitBuffEffect(ZoneUnitBuffEffectKind Kind, UnitBuff Buff, BuffStatTemplate Template,
    bool IsRefresh = false, bool IsInitialRestore = false, ulong Sequence = 0);

/// <summary>
/// Exact Unit allocation's copied movement/turn status flags plus their separate BuffMan byte counters.
/// Death/resurrection clear only the flags; a subsequent successful buff mutation republishes
///the retained counters. Expiry, stack changes and passive changes do not infer status removal.
/// </summary>
public sealed class ZoneUnitMovementStatus
{
    private ZoneBuffMovementStatusCounters _counters;
    private bool _stunned, _sleeping, _rooted, _psychokinesis, _impossibleRotate;
    private string? _refusal;
    public bool IsKnown => _refusal is null;

    internal ZoneUnitMovementStatus(UnitBuffs initial, ZoneBuffContent? content)
    {
        if (content is null && initial.Good.Count + initial.Bad.Count + initial.Hidden.Count != 0)
            _refusal = "Unit movement status requires the actual buff descriptor catalog for its restored buff instances.";
        if (content is not null) _counters = ZoneBuffMovementStatus.Restore(initial, content);
        Publish();
    }

    public ZoneUnitMovementStatusSnapshot CopySnapshot()
    {
        if (_refusal is { } refusal) throw new NotSupportedException(refusal);
        return new(_counters.Stun, _counters.Sleep, _counters.Root, _stunned, _sleeping, _rooted,
            _counters.Psychokinesis, _counters.ImpossibleRotate, _psychokinesis, _impossibleRotate);
    }

    internal void Add(BuffStatTemplate template) { _counters = ZoneBuffMovementStatus.Add(_counters, template); Publish(); }
    internal void Remove(BuffStatTemplate template) { _counters = ZoneBuffMovementStatus.Remove(_counters, template); Publish(); }
    //3925A270 republishes all flags even for refresh/update, whose counters do not change.
    internal void Publish() => (_stunned, _sleeping, _rooted, _psychokinesis, _impossibleRotate) =
        (_counters.Stun != 0, _counters.Sleep != 0, _counters.Root != 0, _counters.Psychokinesis != 0, _counters.ImpossibleRotate != 0);
    internal void ClearFlags() => (_stunned, _sleeping, _rooted, _psychokinesis, _impossibleRotate) = (false, false, false, false, false);
    internal void RefuseInterruptedMutation() => _refusal ??=
        "Unit movement status cannot admit a reentrant or failed model-effect mutation before the original status cache completed.";
}
