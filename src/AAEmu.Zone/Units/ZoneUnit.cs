using AAEmu.Zone.Actors;
using AAEmu.Zone.Combat;
using AAEmu.Zone.Content;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Protocol.Units;

namespace AAEmu.Zone.Units;

public sealed class ZoneUnit(UnitStateSnapshot snapshot, ZoneVitalStats? stats, ZoneProgressionState? progression = null,
    ZoneSkillContent? skillContent = null, ZoneBuffContent? buffContent = null)
{
    public UnitStateSnapshot Snapshot { get; } = snapshot;
    public ZoneVitalStats? Stats { get; private set; } = stats;
    public UnitBuffs Buffs { get; private set; } = snapshot.Buffs;
    private readonly ZoneBuffContent? _buffContent = buffContent;
    public ZoneUnitMovementStatus MovementStatus { get; } = new(snapshot.Buffs, buffContent);
    /// <summary>3944FEB0/39450420 effect boundary, independently of the later status-cache publication.</summary>
    public event Action<ZoneUnit, ZoneUnitBuffEffect>? BuffEffect;
    //Replacement resource bound for deferred managed model admission; overflow refuses late
    //replay explicitly instead of collapsing overlapping/reset effects into current presence.
    internal const int BuffModelEffectJournalCapacity = 1024;
    private readonly List<ZoneUnitBuffEffect> _buffModelEffects = [];
    private ulong _buffModelEffectSequence;
    private bool _buffModelEffectJournalOverflow;
    /// <summary>
    ///39450870's original restored Good/Bad/Hidden order. A new model owner replays this
    ///once, before its final initialization, rather than deriving a latch from current presence.
    /// </summary>
    public IReadOnlyList<ZoneUnitBuffEffect> CopyInitialBuffEffects()
    {
        var original = Snapshot.Buffs.Good.Concat(Snapshot.Buffs.Bad).Concat(Snapshot.Buffs.Hidden).ToArray();
        if (original.Length == 0) return [];
        if (_buffContent is null) throw new NotSupportedException("Restored Unit buff effects require their actual descriptor catalog.");
        return Array.AsReadOnly(original.Where(buff => _buffContent.Templates.ContainsKey(buff.TemplateId))
            .Select(buff => new ZoneUnitBuffEffect(ZoneUnitBuffEffectKind.Apply, buff, _buffContent.Template(buff.TemplateId), IsInitialRestore: true)).ToArray());
    }
    /// <summary>Ordered post-enter model effects, separately from the initial restored inventory.</summary>
    public IReadOnlyList<ZoneUnitBuffEffect> CopyBuffModelEffectJournal()
    {
        if (_buffModelEffectJournalOverflow)
            throw new NotSupportedException($"Deferred Unit model buff-effect journal exceeded {BuffModelEffectJournalCapacity} psychokinesis events; exact late model replay is unavailable.");
        return Array.AsReadOnly(_buffModelEffects.ToArray());
    }
    internal void ApplyBuffEffect(ZoneUnitBuffEffect effect)
    {
        if (effect.Template.Psychokinesis)
        {
            effect = effect with { Sequence = checked(++_buffModelEffectSequence) };
            if (_buffModelEffects.Count < BuffModelEffectJournalCapacity) _buffModelEffects.Add(effect);
            else _buffModelEffectJournalOverflow = true;
        }
        BuffEffect?.Invoke(this, effect);
    }
    public bool BuffTimingEnabled { get; private set; }
    public UnitEquipment Equipment { get; private set; } = snapshot.Equipment;
    public ZoneAttributeOverrides AttributeOverrides { get; private set; } = ZoneAttributeOverrides.Empty;
    public IReadOnlyList<sbyte> GmModes { get; private set; } =
        Array.AsReadOnly(snapshot.GmModes.Count == 0 ? new sbyte[9] : snapshot.GmModes.ToArray());
    public ZoneProgressionState Progression { get; private set; } = progression ?? ZoneProgressionState.FromSnapshot(snapshot);
    public ZoneCooldownManager Cooldowns { get; } = new(skillContent ?? ZoneSkillContent.Empty);
    internal ZoneSkillContent SkillContent { get; } = skillContent ?? ZoneSkillContent.Empty;
    public ZoneUnitCastingState Casting { get; } = new(snapshot);
    public long PreciseHealth { get; private set; } = snapshot.PreciseHealth;
    public long PreciseMana { get; private set; } = snapshot.PreciseMana;
    public ZoneUnitPose Pose { get; private set; } = ZoneUnitPose.FromSnapshot(snapshot);
    public IZoneUnitMovementModel? MovementModel { get; internal set; }
    // Native server unit +A59C, set by EscapeSlave and initialized to zero on admission.
    public uint MovementBlockedUntil { get; internal set; }
    // Native server unit +8009 (32777), written by WZUnitFlyingStateChanged 393636A0 via 39258C90.
    public bool IsFlying { get; internal set; } = (snapshot.Flags & (1 << 9)) != 0;
    public ZoneUnitScaleState Scale { get; } = new(snapshot.Scale);
    private ushort _flags = snapshot.Flags;
    public ushort Flags
    {
        get => (ushort)((_flags & ~(2 | (1 << 9))) | (Casting.Casting ? 2 : 0) | (IsFlying ? 1 << 9 : 0));
        private set => _flags = value;
    }
    public byte AttackFactionFlags { get; private set; } = snapshot.AttackFactionFlags;
    public uint FactionId { get; private set; } = snapshot.PackedState.FactionId;
    public uint ExpeditionId { get; private set; } = snapshot.PackedState.ExpeditionId;
    public uint FamilyId => Snapshot.PackedState.FamilyId;
    public bool TemporaryFaction { get; private set; } = snapshot.TemporaryFaction;
    public uint ExpeditionWarFactionId { get; private set; } = snapshot.PackedState.ExpeditionWarFactionId;
    public uint ExpeditionWarSide { get; private set; } = snapshot.Character?.ExpeditionWarSide ?? 0u;
    public bool IsInCombat => (Flags & 1) != 0;
    //392587B0/39258800 ->39259530: entity event44 observes the changed combat state.
    //The scene subscribes its actual model/script-proxy consumer for this exact generation.
    internal event Action<ZoneUnit, bool>? CombatTransitioned;
    public bool ForceAttack => (Flags & 8) != 0;
    public uint TargetId { get; private set; } = snapshot.TargetId;
    public uint DuelObjectId { get; private set; } = snapshot.Character?.DuelObjectId ?? 0;
    public uint DuelTeam { get; private set; } = snapshot.Character is { } character ? unchecked((byte)character.DuelTeam) : uint.MaxValue;
    // Native 3935D260 marks an initially zero-health unit dead. UnitPoints does not resurrect it.
    public bool IsDead { get; private set; } = snapshot.PreciseHealth == 0;

    internal void SetFaction(uint factionId, bool temporary)
    {
        FactionId = factionId;
        TemporaryFaction = temporary;
    }

    internal void SetExpedition(uint expeditionId)
    {
        ExpeditionId = expeditionId;
        SetExpeditionWar(0, 0);
    }

    internal void SetExpeditionWar(uint factionId, uint side)
    {
        ExpeditionWarFactionId = factionId;
        ExpeditionWarSide = side;
    }

    internal void SetCombat(bool engaged)
    {
        SetCombatFlag(engaged);
        // WZCombatCleared 39364E70 clears the eight attack-faction booleans at 80BC.
        // The death path invokes only the combat setter, so it retains these flags.
        if (!engaged) AttackFactionFlags = 0;
    }

    //393534D0 invokes only the combat setter. WZ39364E70 clears attack-faction flags
    //separately after that setter and its entity event; script requests preserve those flags.
    internal void ClearCombatFromScript() => SetCombatFlag(false);

    private void SetCombatFlag(bool engaged)
    {
        if (IsInCombat == engaged) return;
        Flags = engaged ? (ushort)(Flags | 1) : (ushort)(Flags & ~1);
        CombatTransitioned?.Invoke(this, engaged);
    }

    internal void SetForceAttack(bool enabled) => Flags = enabled ? (ushort)(Flags | 8) : (ushort)(Flags & ~8);
    internal void SetGmMode(int mode, sbyte value)
    {
        var modes = GmModes.ToArray();
        modes[mode] = value;
        GmModes = Array.AsReadOnly(modes);
        // 390B2FB0 writes this byte only: no stat invalidation or actor callback.
    }

    internal void CommitAttributeOverrides(ZoneAttributeOverrides overrides, ZoneVitalStats? stats)
    {
        AttributeOverrides = overrides;
        Stats = stats;
        // 39BF3480 / 39BF34B0 invalidate attribute calculation without setting points.
    }
    internal void SetTarget(uint targetId) => TargetId = targetId;
    internal void SetDuel(UnitDuelState message)
    {
        DuelObjectId = message.DuelObjectId;
        DuelTeam = message.Team;
    }

    internal void Die(ZoneVitalStats? stats)
    {
        //3935D490 first marks dead through3925B2C0, then invokes the actual combat
        //setter+184/39258800 (and entity44), preserving attack-faction flags.
        IsDead = true;
        Casting.ClearCastingFlag(); //3935D490 clears this byte before combat's entity event44.
        Flags = (ushort)(Flags & ~(1 << 10)); //The adjacent byte is cleared by the same native WORD write.
        SetCombatFlag(false);
        MovementStatus.ClearFlags(); //3935D490 clears Unit32080..32093, retaining BuffMan's cache.
        Casting.ClearIdentifiers(); //The four identifiers clear after that callback, independently.
        Flags = (ushort)(Flags & ~((1 << 1) | (1 << 10)));
        if (Snapshot.Identity.Kind != ZoneUnitKind.Character) TargetId = 0;
        Stats = stats;
    }

    internal void Resurrect(UnitResurrection message, ZoneVitalStats? stats)
    {
        // 3935D970 -> 3925E8E0: invalidate, refill from current maxima, clear target,
        // then mark alive. No guard on the previous death state or unit kind.
        if (stats is not { IsComplete: true })
            throw new NotSupportedException("Resurrection requires complete content-derived vital stats.");
        var health = Math.Max(0, unchecked(stats.MaximumHealth * 100));
        var mana = Math.Max(0, unchecked(stats.MaximumMana * 100));
        var pose = ZoneUnitPose.FromResurrection(message);
        MovementStatus.ClearFlags(); //3935D970 clears copied flags before3925E8E0; no cache rebuild.
        Casting.Reset(); //3925E8E0 clears all four identifiers and the casting byte before refilling.
        Flags = (ushort)(Flags & ~(1 << 10));
        Stats = stats;
        PreciseHealth = health;
        PreciseMana = mana;
        Pose = pose;
        TargetId = 0;
        Flags = (ushort)(Flags & ~((1 << 1) | (1 << 10)));
        IsDead = false;
    }

    /// <summary>
    /// Movement-authoritative pose publication: the single write surface the movement chain
    /// uses after each accepted physics frame (post-step status copy → commit → spatial
    /// refresh → entity mirror → outgoing encode, PLAN section 4). The setter stays private —
    /// only <see cref="Resurrect"/> and this seam move <see cref="Pose"/>.
    /// CLEARANCE: this formerly-flagged touch on an unlisted file was CLEARED by the
    /// orchestrator for implementation turn 2 (ZoneUnit pose publication is movement-authority
    /// territory, Agent 3); recorded in IMPLEMENTATION_OWNERSHIP.md section 4 + change log
    /// 2026-09-23. The edit is this seam ONLY.
    /// </summary>
    internal void CommitPose(ZoneUnitPose pose) => Pose = pose;

    internal void CommitBuffs(UnitBuffs buffs, ZoneProgressionState progression, ZoneVitalStats stats)
    {
        Buffs = buffs;
        Progression = progression;
        Stats = stats;
        // Native buff changes invalidate stat caches; they do not run the HP/MP setters.
    }

    internal void SetBuffTimingEnabled(bool enabled) => BuffTimingEnabled = enabled;

    internal void CommitBuffTiming(UnitBuffs buffs, ZoneVitalStats? stats)
    {
        Buffs = buffs;
        Stats = stats;
        // Elapsed/tick updates preserve passive ownership and current HP/MP, including at the duration boundary.
    }

    internal void CommitProgression(ZoneProgressionState progression, ZoneVitalStats? stats)
    {
        Progression = progression;
        Stats = stats;
    }

    internal void Apply(UnitPoints points)
    {
        if (Stats is not { IsComplete: true })
            throw new NotSupportedException("UnitPoints requires complete content-derived vital stats.");
        var health = Clamp(points.PreciseHealth, Stats.MaximumHealth);
        var mana = Clamp(points.PreciseMana, Stats.MaximumMana);
        PreciseHealth = health;
        PreciseMana = mana;
    }

    internal void CommitEquipment(UnitEquipment equipment, ZoneVitalStats? stats)
    {
        Equipment = equipment;
        Stats = stats;
        // 3935CDC0 / 3935CF10 invalidate stats without clamping precise points.
    }

    private static long Clamp(long value, long maximum) =>
        value < 0 ? 0 : Math.Min(value, unchecked(maximum * 100)); // native 3925AB00 / 3925AC60
}
