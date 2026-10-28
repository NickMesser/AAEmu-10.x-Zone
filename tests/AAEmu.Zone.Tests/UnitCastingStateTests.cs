using AAEmu.Zone.Combat;
using AAEmu.Zone.Content;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Protocol.Units;
using AAEmu.Zone.Units;
using AAEmu.Zone.WorldLink;

namespace AAEmu.Zone.Tests;

public sealed class UnitCastingStateTests
{
    private const uint WorldId = 0x123456;
    private const uint NpcId = 0xABCDEF;
    private static readonly SkillCasterRef Caster = new(0, WorldId, 0, 0, 0, 0);
    private static readonly SkillTargetRef Target = new(0, WorldId);
    private static ZoneSkillContent Content() => new(
        [new(2, 0), new(40917, 0) { TimingId = 1, ChannelingMilliseconds = 3000 }, new(99, 0)], [],
        plotIds: [7], plotEvents: [new(80, 7), new(81, 8)]);

    [Test]
    public async Task AdmissionCopiesFourWireFieldsWithoutDerivingThemFromTheCastingByte()
    {
        var snapshot = UnitStateSnapshot.Read(UnitBody([65535, 40149, 42, 19], casting: false));
        var unit = new ZoneUnit(snapshot, null);
        await Assert.That(snapshot.CastingFields).IsEqualTo(new UnitCastingFields(65535, 40149, 42, 19));
        await Assert.That(unit.Casting.CopySnapshot()).IsEqualTo(new ZoneUnitCastingSnapshot(65535, 40149, 42, 19, false));
        await Assert.That(unit.Snapshot.Identity.NpcObjectId).IsEqualTo(NpcId);
        var independent = new ZoneUnit(UnitStateSnapshot.Read(UnitBody([], casting: true)), null);
        await Assert.That(independent.Casting.CopySnapshot()).IsEqualTo(new ZoneUnitCastingSnapshot(0, 0, 0, 0, true));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AdmissionAndWorldFlyingChangesKeepTheirIndependentPackedBit(bool initiallyFlying)
    {
        var session = ZoneSession.CreateDetached(new MemoryStream());
        var flags = 1 | (initiallyFlying ? 1 << 9 : 0);
        var unit = new ZoneUnit(UnitStateSnapshot.Read(UnitBody([], casting: true, extraFlags: flags)), null);
        session.Registry.AddPrepared(unit);
        await Assert.That(unit.IsFlying).IsEqualTo(initiallyFlying);
        await Assert.That(unit.Flags).IsEqualTo((ushort)(flags | 2));
        session.ReceiveForTest(new UnitFlyingStateChanged(WorldId, !initiallyFlying).ToFrame());
        await Assert.That(unit.IsFlying).IsEqualTo(!initiallyFlying);
        await Assert.That((unit.Flags & (1 << 9)) != 0).IsEqualTo(!initiallyFlying);
        await Assert.That(unit.Casting.Casting).IsTrue();
        await Assert.That(unit.IsInCombat).IsTrue();
        await Assert.That(session.HandlerFaults.Count).IsEqualTo(0);
    }

    [Test]
    public async Task StartedUsesTimingAndCastTimeWhileSkillTypeTwoControlsOnlyTheCastingByte()
    {
        var (registry, unit) = Owner([11, 12, 13, 14]);
        registry.ApplySkillStarted(new(40917, 20, Caster, Target, 0));
        registry.ApplySkillStarted(new(99, 21, Caster, Target, 1000));
        await Assert.That(unit.Casting.CopySnapshot()).IsEqualTo(new ZoneUnitCastingSnapshot(11, 12, 13, 14, false));
        registry.ApplySkillStarted(new(40917, 22, Caster, Target, 1000));
        await Assert.That(unit.Casting.CopySnapshot()).IsEqualTo(new ZoneUnitCastingSnapshot(22, 12, 13, 14, false));
        registry.ApplySkillStarted(new(2, 0, Caster, Target, 0));
        registry.ApplySkillStopped(new(WorldId, 65538)); //No truncation to skill type2.
        await Assert.That(unit.Casting.Casting).IsTrue();
        await Assert.That((unit.Flags & 2) != 0).IsTrue();
        registry.ApplySkillStopped(new(WorldId, 22)); //Not a timeline comparison.
        await Assert.That(unit.Casting.Casting).IsTrue();
        registry.ApplySkillStopped(new(WorldId, 2));
        await Assert.That(unit.Casting.CopySnapshot()).IsEqualTo(new ZoneUnitCastingSnapshot(22, 12, 13, 14, false));
    }

    [Test]
    public async Task FireAndEndExposeTheOriginalCallbackPhasesAndKeepIndependentSkillFields()
    {
        var (registry, unit) = Owner([11, 12, 13, 14], casting: true);
        var seen = new List<(ZoneUnitSkillAcknowledgement Ack, ZoneUnitCastingSnapshot State)>();
        registry.SkillAcknowledged += (owner, ack) => seen.Add((ack, owner.Casting.CopySnapshot()));
        registry.ApplySkillStarted(new(40917, 21, Caster, Target, 1000));
        registry.ApplySkillFired(new(40917, 21, Caster, Target, false));
        await Assert.That(seen[0].State).IsEqualTo(new ZoneUnitCastingSnapshot(21, 12, 13, 14, true));
        await Assert.That(seen[1].State).IsEqualTo(new ZoneUnitCastingSnapshot(0, 0, 13, 14, true));
        await Assert.That(unit.Casting.CopySnapshot()).IsEqualTo(new ZoneUnitCastingSnapshot(0, 0, 21, 14, true));
        registry.ApplySkillStarted(new(40917, 22, Caster, Target, 1000));
        registry.ApplySkillEnded(new(22, Caster));
        await Assert.That(unit.Casting.ChannelTimeline).IsEqualTo(21u);
        registry.ApplySkillEnded(new(21, Caster));
        await Assert.That(unit.Casting.CopySnapshot()).IsEqualTo(new ZoneUnitCastingSnapshot(0, 0, 0, 14, true));
        await Assert.That(seen[^1].Ack.SkillId).IsEqualTo(40917u);
        await Assert.That(seen[^1].Ack.Kind).IsEqualTo(ZoneUnitSkillAcknowledgementKind.Ended);
    }

    [Test]
    public async Task FailureAcknowledgmentsPreserveAllFieldsAndMissingFireSynthesizesStarted()
    {
        var (registry, unit) = Owner([11, 12, 13, 14]);
        var original = unit.Casting.CopySnapshot();
        var result = new SkillCastResult(7, 42, 0x12345678, false);
        var acks = new List<ZoneUnitSkillAcknowledgement>();
        registry.SkillAcknowledged += (_, ack) => acks.Add(ack);
        registry.ApplySkillStarted(new(40917, 20, Caster, Target, 1000) { Result = result });
        await Assert.That(unit.Casting.CopySnapshot()).IsEqualTo(original);
        registry.ApplySkillFired(new(40917, 21, Caster, Target, false) { Result = result });
        await Assert.That(unit.Casting.CopySnapshot()).IsEqualTo(original);
        await Assert.That(acks.Select(v => v.Kind).SequenceEqual(new[]
            { ZoneUnitSkillAcknowledgementKind.Started, ZoneUnitSkillAcknowledgementKind.Started, ZoneUnitSkillAcknowledgementKind.Fired })).IsTrue();
        await Assert.That(acks[1].Result).IsEqualTo(SkillCastResult.Success);
        await Assert.That(acks[2].Result).IsEqualTo(result);
        await Assert.That(registry.ApplySkillStarted(new(123456, 22, Caster, Target, 1000))).IsFalse();
        await Assert.That(acks.Count).IsEqualTo(3);
    }

    [Test]
    public async Task PlotIdsComeFromValidatedPlotTimelinesAndChannelOwnershipSurvivesPlotEnded()
    {
        var (registry, unit) = Owner([11, 12, 13, 14]);
        await Assert.That(registry.ApplyPlotEvent(Plot(31, eventId: 81))).IsFalse(); //Missing parent plot.
        await Assert.That(registry.ApplyPlotEvent(Plot(31, eventId: 82))).IsFalse();
        await Assert.That(unit.Casting.CurrentSkill).IsEqualTo(12u);
        registry.ApplyPlotEvent(Plot(31));
        await Assert.That(unit.Casting.CopySnapshot()).IsEqualTo(new ZoneUnitCastingSnapshot(11, 31, 13, 31, false));
        registry.ApplyPlotEnded(new(31));
        await Assert.That(unit.Casting.CopySnapshot()).IsEqualTo(new ZoneUnitCastingSnapshot(0, 0, 13, 31, false));
        registry.ApplyPlotChannelingStopped(new(31, 0, false));
        await Assert.That(unit.Casting.CopySnapshot()).IsEqualTo(default(ZoneUnitCastingSnapshot));
    }

    [Test]
    public async Task OlderPlotStopsAndEndZeroRespectEachIndependentComparison()
    {
        var (registry, unit) = Owner([11, 12, 13, 14]);
        registry.ApplyPlotEvent(Plot(31));
        registry.ApplyPlotEvent(Plot(32));
        await Assert.That(registry.ApplyPlotCastingStopped(new(31, 0, false))).IsFalse();
        await Assert.That(registry.ApplyPlotChannelingStopped(new(31, 0, false))).IsFalse();
        registry.ApplySkillEnded(new(0, Caster)); //Native helpers treat zero as wildcard, independently.
        await Assert.That(unit.Casting.CopySnapshot()).IsEqualTo(new ZoneUnitCastingSnapshot(0, 32, 0, 32, false));
    }

    [Test]
    public async Task PlotUpdateUsesTheOriginalRetainedUnitLookupForItsSeparateChannelGuard()
    {
        var (registry, unit) = Owner([11, 21, 13, 21]);
        //Original executed controls prove a nonzero channel guard does not replace the a9 lookup ID.
        var update = Plot(21) with { CastTimeMs = 0, ChannelingTimeMs = 0, ChannelingObjectId = WorldId };
        registry.ApplyPlotEvent(update);
        await Assert.That(unit.Casting.CopySnapshot()).IsEqualTo(new ZoneUnitCastingSnapshot(11, 21, 13, 21, false));
        registry.ApplyPlotEvent(update with { ObjectId = WorldId });
        await Assert.That(unit.Casting.CopySnapshot()).IsEqualTo(new ZoneUnitCastingSnapshot(11, 0, 13, 0, false));
    }

    [Test]
    public async Task WorldCancellationUsesUnsignedWireTimelineAndClearsBeforeResolvedCooldown()
    {
        var session = ZoneSession.CreateDetached(new MemoryStream());
        var unit = new ZoneUnit(UnitStateSnapshot.Read(UnitBody([65535, 31, 11, 12], casting: true)), null, skillContent: Content());
        session.Registry.AddPrepared(unit);
        session.ReceiveForTest(new(WzOpcodes.CastingStopped, Cancellation(65534)));
        await Assert.That(unit.Cooldowns.GetRemaining(40917)).IsEqualTo(0u);
        await Assert.That(unit.Casting.CurrentSkill).IsEqualTo(31u);
        session.ReceiveForTest(new(WzOpcodes.CastingStopped, Cancellation(65535)));
        await Assert.That(unit.Casting.CopySnapshot()).IsEqualTo(new ZoneUnitCastingSnapshot(0, 0, 11, 12, true));
        await Assert.That(unit.Cooldowns.GetRemaining(40917)).IsEqualTo(4000u);
        await Assert.That(session.HandlerFaults.Count).IsEqualTo(0);
    }

    [Test]
    public async Task OnlyUnitItemOwnerAndMountCasterVariantsAffectTheWorldUnit()
    {
        var (registry, unit) = Owner();
        foreach (var type in new byte[] { 1, 4 })
            registry.ApplySkillStarted(new(40917, 21, Caster with { Type = type }, Target, 1000));
        registry.ApplySkillStarted(new(40917, 21, Caster with { ObjectId = NpcId }, Target, 1000));
        await Assert.That(unit.Casting.CastTimeline).IsEqualTo(0u);
        registry.ApplySkillStarted(new(40917, 22, Caster with { Type = 2, ItemId = 987654321 }, Target, 1000));
        await Assert.That(unit.Casting.CastTimeline).IsEqualTo(22u);
        registry.ApplySkillStarted(new(40917, 23, Caster with { Type = 3, Template = 78 }, Target, 1000));
        await Assert.That(unit.Casting.CastTimeline).IsEqualTo(23u);
    }

    [Test]
    public async Task DeathClearsCastingBeforeCombatCallbackAndIdentifiersAfterwardAndResurrectionResetsAll()
    {
        var unit = new ZoneUnit(UnitStateSnapshot.Read(UnitBody([11, 12, 13, 14], casting: true, extraFlags: 1 << 10)), null);
        unit.SetCombat(true);
        var seen = default(ZoneUnitCastingSnapshot);
        ushort observedFlags = ushort.MaxValue;
        unit.CombatTransitioned += (owner, engaged) =>
        {
            if (engaged) return;
            seen = owner.Casting.CopySnapshot(); observedFlags = owner.Flags;
        };
        unit.Die(null);
        await Assert.That(seen).IsEqualTo(new ZoneUnitCastingSnapshot(11, 12, 13, 14, false));
        await Assert.That(observedFlags & ((1 << 1) | (1 << 10))).IsEqualTo(0);
        await Assert.That(unit.Casting.CopySnapshot()).IsEqualTo(default(ZoneUnitCastingSnapshot));
        var (_, live) = Owner([11, 12, 13, 14], casting: true);
        live.Resurrect(new(WorldId, new(0, 0, 0), 0), new ZoneVitalStats([], 100, 100, []));
        await Assert.That(live.Casting.CopySnapshot()).IsEqualTo(default(ZoneUnitCastingSnapshot));
    }

    [Test]
    public async Task RetiredPlotCallbacksAndAFireCallbackCannotWriteAReplacementAllocation()
    {
        var (registry, unit) = Owner();
        registry.ApplyPlotEvent(Plot(31));
        var replacement = new ZoneUnit(UnitStateSnapshot.Read(UnitBody([40, 31, 42, 31])), null, skillContent: Content());
        registry.RemovePrepared(unit); registry.AddPrepared(replacement);
        await Assert.That(registry.ApplyPlotChannelingStopped(new(31, 0, false))).IsFalse();
        await Assert.That(replacement.Casting.ChannelTimeline).IsEqualTo(42u);
        registry.ApplySkillStarted(new(40917, 25, Caster, Target, 1000));
        registry.SkillAcknowledged += (owner, ack) =>
        {
            if (ack.Kind != ZoneUnitSkillAcknowledgementKind.Fired) return;
            registry.RemovePrepared(owner);
            registry.AddPrepared(new ZoneUnit(UnitStateSnapshot.Read(UnitBody([50, 51, 52, 53])), null, skillContent: Content()));
        };
        registry.ApplySkillFired(new(40917, 25, Caster, Target, false));
        await Assert.That(registry.Units[WorldId].Casting.CopySnapshot()).IsEqualTo(new ZoneUnitCastingSnapshot(50, 51, 52, 53, false));
        await Assert.That(replacement.Casting.ChannelTimeline).IsEqualTo(42u); //Retired owner did not finish the channel write either.
    }

    [Test]
    public async Task CompletePacketDecodePrecedesMutationAndFailedResultsReachTheSynchronousConsumer()
    {
        var session = ZoneSession.CreateDetached(new MemoryStream());
        var unit = new ZoneUnit(UnitStateSnapshot.Read(UnitBody([11, 12, 13, 14])), null, skillContent: Content());
        session.Registry.AddPrepared(unit);
        var observed = new List<ZoneUnitSkillAcknowledgement>();
        session.Registry.SkillAcknowledged += (_, ack) => observed.Add(ack);
        var started = new ZoneWriter(); started.WriteUInt32(40917); started.WriteUInt16(21);
        started.WriteByte(0); started.WriteBc(WorldId); started.WriteByte(0); started.WriteBc(WorldId);
        started.WriteUInt32(1000); started.WriteByte(15); started.WriteByte(7); started.WriteUInt16(42);
        started.WriteUInt32(0x12345678); started.WriteBoolean(false);
        var body = started.ToArray();
        session.ReceiveForTest(new(WzOpcodes.SkillStarted, body[..^1]));
        await Assert.That(observed.Count).IsEqualTo(0);
        session.ReceiveForTest(new(WzOpcodes.SkillStarted, body));
        await Assert.That(observed[0].Result).IsEqualTo(new SkillCastResult(7, 42, 0x12345678, false));
        await Assert.That(unit.Casting.CopySnapshot()).IsEqualTo(new ZoneUnitCastingSnapshot(11, 12, 13, 14, false));
        await Assert.That(session.HandlerFaults[WzOpcodes.SkillStarted]).IsEqualTo(1);
    }

    private static (ZoneUnitRegistry Registry, ZoneUnit Unit) Owner(ushort[]? fields = null, bool casting = false)
    {
        var unit = new ZoneUnit(UnitStateSnapshot.Read(UnitBody(fields ?? [], casting)), null, skillContent: Content());
        var registry = new ZoneUnitRegistry(); registry.AddPrepared(unit); return (registry, unit);
    }

    private static PlotEvent Plot(ushort timeline, uint eventId = 80) =>
        new(timeline, eventId, 40917, new(1, WorldId), new(1, WorldId), 0, 0, 1000, 2000, false, false, []);

    private static byte[] Cancellation(ushort timeline)
    {
        var writer = new ZoneWriter(); writer.WriteBc(WorldId); writer.WriteUInt16(timeline);
        writer.WriteUInt32(40917); writer.WriteUInt32(4000); return writer.ToArray();
    }

    private static byte[] UnitBody(ushort[] fields, bool casting = false, int extraFlags = 0)
    {
        var writer = new ZoneWriter(); writer.WriteBc(WorldId); writer.WriteString("casting-control");
        writer.WriteByte(0); writer.WriteByte(0); writer.WriteBoolean(false);
        new UnitIdentity(ZoneUnitKind.Npc, NpcObjectId: NpcId, TemplateId: 11).Write(writer);
        writer.WriteString(""); writer.WriteUInt32(0); writer.WriteUInt32(0); writer.WriteUInt16(0); writer.WriteByte(0);
        writer.WriteSingle(1); writer.WriteSByte(1); writer.WriteSByte(0); writer.WriteSByte(0); writer.WriteSByte(0);
        writer.WriteBytes(new byte[4]); writer.WriteUInt32(1); writer.WriteUInt64(0); writer.WriteByte(0);
        writer.WriteBc(0); writer.WriteInt64(100); writer.WriteInt64(100);
        writer.WriteSByte(-1); writer.WriteSByte(-1); writer.WriteByte(0); writer.WriteBoolean(false); writer.WriteSByte(0);
        writer.WriteByte(0); writer.WriteByte(0); writer.WriteUInt32(0); writer.WriteInt32(0); writer.WriteUInt32(0); writer.WriteBoolean(false);
        writer.WriteSByte(0); writer.WriteSByte(0); writer.WriteSByte(0); writer.WriteByte(0);
        writer.WritePisc(0, 0, 0, 0); writer.WritePisc(0, 0, 0); writer.WritePisc(0, 0, 0, 0);
        writer.WriteUInt16((ushort)((fields.Length == 4 ? 0x80 : 0) | (casting ? 2 : 0) | extraFlags)); writer.WriteByte(0);
        foreach (var field in fields) writer.WriteUInt16(field);
        writer.WriteByte(0); writer.WriteByte(0); writer.WriteByte(0); writer.WriteInt32(0);
        return writer.ToArray();
    }
}
