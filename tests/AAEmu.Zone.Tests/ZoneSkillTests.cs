using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Protocol.Units;
using AAEmu.Zone.Units;
using AAEmu.Zone.WorldLink;

namespace AAEmu.Zone.Tests;

public sealed class ZoneSkillTests
{
    // Real WZ bodies from D:\aaemu-runtime\captures\native-baseline.bin.
    private static readonly byte[] StartedBody = Bytes("032e00000b0000b3010000b301000000000000");
    private static readonly byte[] FiredBody = Bytes("0f450000100000dd010000dd01000000");
    private static readonly byte[] EndedBody = Bytes("0d00001e0200");
    private static readonly byte[] PlotBody = Bytes("0d00fd880000d59f0000011e0200011e0200000000000000000000000000000000000000000000000100010000001e0200");
    private static readonly byte[] PlotEndedBody = Bytes("0d00");
    private static readonly byte[] HealedBody = Bytes("00ef2c0000350000230300230300000d600900000000000000000000000000000000000000000000000001230300");

    private static byte[] Bytes(string hex) => Convert.FromHexString(hex);

    private static ZoneSession NewSession() => ZoneSession.CreateDetached(new MemoryStream());

    [Test]
    public async Task BodyLengthsMatchTheCapture()
    {
        await Assert.That(StartedBody.Length).IsEqualTo(19);
        await Assert.That(FiredBody.Length).IsEqualTo(16);
        await Assert.That(EndedBody.Length).IsEqualTo(6);
        await Assert.That(PlotBody.Length).IsEqualTo(49);
        await Assert.That(HealedBody.Length).IsEqualTo(46);
    }

    [Test]
    public async Task DecodesCapturedSkillStartedFiredEnded()
    {
        var started = SkillStarted.Read(StartedBody);
        await Assert.That(started.SkillId).IsEqualTo(0x2E03u);
        await Assert.That(started.Timeline).IsEqualTo((ushort)0x0B);
        await Assert.That(started.Caster.ObjectId).IsEqualTo(0x01B3u);
        await Assert.That(started.Target).IsEqualTo(new SkillTargetRef(0, 0x01B3));
        await Assert.That(started.CastTime).IsEqualTo(0u);

        var fired = SkillFired.Read(FiredBody);
        await Assert.That(fired.SkillId).IsEqualTo(0x450Fu);
        await Assert.That(fired.Timeline).IsEqualTo((ushort)0x10);
        await Assert.That(fired.Target.ObjectId).IsEqualTo(0x01DDu);
        await Assert.That(fired.Flag).IsFalse();

        var ended = SkillEnded.Read(EndedBody);
        await Assert.That(ended.Timeline).IsEqualTo((ushort)0x0D);
        await Assert.That(ended.Caster.ObjectId).IsEqualTo(0x021Eu);
    }

    [Test]
    public async Task DecodesCapturedPlotAndHeal()
    {
        var plot = PlotEvent.Read(PlotBody);
        await Assert.That(plot.Timeline).IsEqualTo((ushort)0x0D);
        await Assert.That(plot.EventId).IsEqualTo(0x88FDu);
        await Assert.That(plot.SkillId).IsEqualTo(0x9FD5u);
        await Assert.That(plot.Caster).IsEqualTo(new PlotObjectRef(1, 0x021E));
        await Assert.That(plot.ConditionOk).IsTrue();
        await Assert.That(plot.Last).IsFalse();
        await Assert.That(plot.TargetUnitIds).IsEquivalentTo(new uint[] { 0x021E });
        await Assert.That(PlotEnded.Read(PlotEndedBody).Timeline).IsEqualTo((short)13);

        var healed = UnitHealed.Read(HealedBody);
        await Assert.That(healed.Action).IsEqualTo(new UnitSkillCast(0x2CEF, 0x35));
        await Assert.That(healed.TargetId).IsEqualTo(0x0323u);
        await Assert.That(healed.HitType).IsEqualTo((byte)0x0D);
        await Assert.That(healed.Amount).IsEqualTo(0x960L);
        await Assert.That(healed.UnitInCharge).IsEqualTo(0x0323u);
    }

    [Test]
    public async Task RejectsShortAndTrailingBodies()
    {
        await Assert.That(() => SkillStarted.Read(StartedBody[..^1])).Throws<Exception>();
        await Assert.That(() => SkillStarted.Read([.. StartedBody, 0])).Throws<Exception>();
        await Assert.That(() => SkillFired.Read(FiredBody[..^1])).Throws<Exception>();
        await Assert.That(() => SkillEnded.Read([.. EndedBody, 0])).Throws<Exception>();
        await Assert.That(() => PlotEvent.Read(PlotBody[..^1])).Throws<Exception>();
        await Assert.That(() => PlotEnded.Read([1])).Throws<Exception>();
        await Assert.That(() => UnitHealed.Read([.. HealedBody, 0])).Throws<Exception>();
        await Assert.That(() => SkillEnded.Read(Bytes("0d0009000000"))).Throws<Exception>(); // unknown caster type 9
    }

    [Test]
    public async Task TracksCastLifecycleAndCountsUnknownUnits()
    {
        var known = new HashSet<uint> { 0x01B3, 0x021E, 0x0323 };
        var state = new ZoneSkillState(known.Contains);
        state.Start(SkillStarted.Read(StartedBody));
        await Assert.That(state.ActiveSkills[0x0B].SkillId).IsEqualTo(0x2E03u);
        await Assert.That(state.Casts[0x01B3]).IsEqualTo(new ZoneUnitCast(0x0B, 0x2E03, true));

        state.Stop(new SkillStopped(0x01B3, 0x0B)); // a timeline-shaped value is not the skill-type-2 control.
        await Assert.That(state.Casts[0x01B3].Casting).IsTrue();
        state.Stop(new SkillStopped(0x01B3, 2));
        await Assert.That(state.Casts[0x01B3].Casting).IsFalse();
        state.StopCasting(new CastingStopped(0x01B3, 0x0B, 0x2E03, 0));
        await Assert.That(state.Casts.ContainsKey(0x01B3)).IsFalse();

        state.Start(SkillStarted.Read(StartedBody));
        state.End(new SkillEnded(0x0B, new SkillCasterRef(0, 0x01B3, 0, 0, 0, 0)));
        await Assert.That(state.ActiveSkills.ContainsKey(0x0B)).IsFalse();
        await Assert.That(state.Casts.ContainsKey(0x01B3)).IsFalse();

        state.Fire(SkillFired.Read(FiredBody)); // caster 0x01DD is not a known unit
        await Assert.That(state.ActiveSkills[0x10].FireCount).IsEqualTo(1);
        await Assert.That(state.UnknownUnitEvents).IsEqualTo(1);
    }

    [Test]
    public async Task TracksPlotAndHealFeedback()
    {
        var state = new ZoneSkillState(id => id == 0x0323);
        state.Plot(PlotEvent.Read(PlotBody));
        await Assert.That(state.Plots[0x0D].LastEventId).IsEqualTo(0x88FDu);
        state.PlotCastingStop(new PlotStopped(13, 0, false));
        await Assert.That(state.Plots[0x0D].CastingStopped).IsTrue();
        state.PlotEnd(PlotEnded.Read(PlotEndedBody));
        await Assert.That(state.Plots.Count).IsEqualTo(0);
        state.PlotEnd(new PlotEnded(99)); // unknown timeline is a no-op

        state.Heal(UnitHealed.Read(HealedBody));
        await Assert.That(state.LastHeal[0x0323]).IsEqualTo(new ZoneCombatFeedback(0x0323, 0x2CEF, 0x960, false));
        await Assert.That(state.HealCount).IsEqualTo(1);
    }

    [Test]
    public async Task SessionDispatchesSkillFramesWithoutCountingThemUnimplemented()
    {
        var session = NewSession();
        session.ReceiveForTest(new ZoneFrame(WzOpcodes.SkillStarted, StartedBody));
        session.ReceiveForTest(new ZoneFrame(WzOpcodes.SkillFired, FiredBody));
        session.ReceiveForTest(new ZoneFrame(WzOpcodes.SkillEnded, EndedBody));
        session.ReceiveForTest(new ZoneFrame(WzOpcodes.PlotEvent, PlotBody));
        session.ReceiveForTest(new ZoneFrame(WzOpcodes.PlotEnded, PlotEndedBody));
        session.ReceiveForTest(new ZoneFrame(WzOpcodes.UnitHealed, HealedBody));
        foreach (var opcode in new[] { WzOpcodes.SkillStarted, WzOpcodes.SkillFired, WzOpcodes.SkillEnded, WzOpcodes.PlotEvent, WzOpcodes.PlotEnded, WzOpcodes.UnitHealed })
            await Assert.That(session.UnimplementedOpcodes.ContainsKey(opcode)).IsFalse();
        await Assert.That(session.Skills.ActiveSkills.ContainsKey(0x0B)).IsTrue();
        await Assert.That(session.Skills.HealCount).IsEqualTo(1);
        await Assert.That(session.Skills.UnknownUnitEvents).IsGreaterThan(0);
        session.ReceiveForTest(new ZoneFrame(WzOpcodes.SkillEnded, new byte[] { 1, 2 }));
        await Assert.That(session.HandlerFaults[WzOpcodes.SkillEnded]).IsEqualTo(1);
        await Assert.That(session.IsClosed).IsFalse();
    }
}
