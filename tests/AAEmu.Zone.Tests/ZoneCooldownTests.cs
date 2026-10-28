using AAEmu.Zone.Combat;
using AAEmu.Zone.Content;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Units;
using AAEmu.Zone.WorldLink;
using Microsoft.Data.Sqlite;

namespace AAEmu.Zone.Tests;

public sealed class ZoneCooldownTests
{
    private static ZoneSkillContent Content() => new(
    [
        new(1, 0),
        new(2, 0) { CooldownTagId = 10, SecondCooldownTagId = 11 },
        new(3, 0) { CooldownTagId = 10, SwitchToSkillCooldown = true },
        new(4, 0) { CooldownTagId = 11 },
        new(5, 0) { ChargeCount = 3, ChargeCooldownMilliseconds = 1000 },
        new(6, 0) { CooldownTagId = 12 },
        new(7, 0) { CooldownTagId = 12 }
    ], [], [new(2, 10), new(2, 11), new(3, 10), new(3, 11), new(4, 11), new(6, 12), new(7, 12), new(6, 99), new(7, 99)]);

    [Test]
    public async Task DecodesWorldBodiesAndRejectsEveryTruncationAndTrailingByte()
    {
        // Field order from World's WZCombatPackets/WZZoneControlPackets; signed values
        // exercise the native interpretation of World's uint adjustment parameters.
        var reset = Convert.FromHexString("563412040302010807060501000101");
        var reduce = Convert.FromHexString("5634120403020108070605e7ffffff02000000d4feffff010001");
        var charge = Convert.FromHexString("5634120403020100000000feffffffd4feffff");
        await Assert.That(SkillCooldownReset.Read(reset)).IsEqualTo(new SkillCooldownReset(0x123456, 0x01020304, 0x05060708, true, false, true, true));
        await Assert.That(SkillCooldownReduce.Read(reduce)).IsEqualTo(new SkillCooldownReduce(0x123456, 0x01020304, 0x05060708, new(-25, 2, -300), true, false, true));
        await Assert.That(ChargeSkillCooldownChange.Read(charge)).IsEqualTo(new ChargeSkillCooldownChange(0x123456, 0x01020304, new(0, -2, -300)));
        await RejectInvalid(reset, b => SkillCooldownReset.Read(b));
        await RejectInvalid(reduce, b => SkillCooldownReduce.Read(b));
        await RejectInvalid(charge, b => ChargeSkillCooldownChange.Read(b));
    }

    private static async Task RejectInvalid(byte[] body, Action<byte[]> read)
    {
        for (var length = 0; length < body.Length; length++)
        {
            var truncated = body[..length];
            await Assert.That(() => read(truncated)).Throws<Exception>();
        }
        await Assert.That(() => read([.. body, 0])).Throws<Exception>();
    }

    [Test]
    public async Task SharedAndSwitchedSkillsUseTheirNativeTimerMaps()
    {
        var timers = new ZoneCooldownManager(Content());
        await Assert.That(timers.StartSkill(2, 10000)).IsTrue();
        await Assert.That(timers.Tags.Keys.Order().SequenceEqual(new uint[] { 10, 11 })).IsTrue();
        await Assert.That(timers.Skills.Keys.SequenceEqual(new uint[] { 3 })).IsTrue();
        await Assert.That(timers.GetRemaining(4)).IsEqualTo(10000u);
        timers.Advance(1000);
        timers.StartSkill(2, 5000); // shorter starts do not replace an existing longer timer.
        await Assert.That(timers.GetRemaining(2)).IsEqualTo(9000u);
        timers.Reset(new(0, 3, 0, false, false, false, false));
        await Assert.That(timers.GetRemaining(3)).IsEqualTo(0u); // switched skill ignores tag cooldowns.
        await Assert.That(timers.GetRemaining(4)).IsEqualTo(9000u);
        timers.StartGlobal(12000);
        await Assert.That(timers.GetRemaining(4)).IsEqualTo(12000u);
        await Assert.That(timers.GetRemaining(4, includeGlobal: false)).IsEqualTo(9000u);
    }

    [Test]
    public async Task PercentUsesOriginalDurationAndSignedIncreasesAreClamped()
    {
        var timers = new ZoneCooldownManager(Content());
        timers.StartSkill(1, 10000);
        timers.Advance(2000);
        timers.Reduce(new(0, 1, 0, new(25, 9, 1), false, false, false));
        await Assert.That(timers.GetRemaining(1)).IsEqualTo(5500u); // original 10000 * 25%, not remaining 8000.
        timers.Reduce(new(0, 1, 0, new(0, 0, -20000), false, false, false));
        await Assert.That(timers.GetRemaining(1)).IsEqualTo(10000u);
        timers.Reduce(new(0, 1, 0, new(0, 0, 20000), false, false, false));
        await Assert.That(timers.GetRemaining(1)).IsEqualTo(0u);
        await Assert.That(timers.Skills.ContainsKey(1)).IsTrue(); // reduction leaves zero until the next timer tick.
        timers.Advance(0);
        await Assert.That(timers.Skills.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RelatedTagReductionDeduplicatesTagsAndHonorsFlagDependencies()
    {
        var timers = new ZoneCooldownManager(Content());
        timers.StartSkill(6, 10000);
        timers.Reduce(new(0, 0, 99, new(0, 0, 1000), false, true, true));
        await Assert.That(timers.Tags[12].RemainingMilliseconds).IsEqualTo(9000u); // both members share tag 12.
        timers.Reset(new(0, 0, 99, false, false, false, true));
        await Assert.That(timers.Tags.ContainsKey(12)).IsTrue(); // final flag is conditional on TaggedSkills.
        timers.Reset(new(0, 0, 99, false, false, true, true));
        await Assert.That(timers.Tags.Count).IsEqualTo(0);
        timers.StartSkill(2, 10000);
        timers.Reset(new(0, 2, 0, false, true, false, false));
        await Assert.That(timers.Tags.Count).IsEqualTo(0); // reset tags belonging to the selected skill.
        await Assert.That(timers.Skills.ContainsKey(3)).IsTrue();
    }

    [Test]
    public async Task ChargeChangesUseCountBeforePercentAndRecoverOneChargePerUpdate()
    {
        var timers = new ZoneCooldownManager(Content());
        timers.StartChargeRecovery(5, 3000, 3000, 3);
        await Assert.That(timers.GetAvailableCharges(5)).IsEqualTo(0u);
        timers.ChangeCharge(new(0, 5, new(99, 1, 99999))); // count takes precedence for charge timers.
        await Assert.That(timers.Charges[5]).IsEqualTo(new ZoneCooldownTimer(2000, 2000, 2));
        await Assert.That(timers.GetAvailableCharges(5)).IsEqualTo(1u);
        timers.Reset(new(0, 5, 0, true, true, true, true));
        await Assert.That(timers.Charges.ContainsKey(5)).IsTrue(); // reset does not clear charges.
        timers.ChangeCharge(new(0, 5, new(0, -1, 0)));
        await Assert.That(timers.Charges[5]).IsEqualTo(new ZoneCooldownTimer(3000, 3000, 3));
        timers.Advance(2500); // native discards overshoot and recovers one charge, even with a large elapsed delta.
        await Assert.That(timers.Charges[5]).IsEqualTo(new ZoneCooldownTimer(2000, 2000, 2));
        await Assert.That(timers.GetChargeRecoveryRemaining(5)).IsEqualTo(1000u);
        timers.ChangeCharge(new(0, 5, new(25, 0, 99999)));
        await Assert.That(timers.Charges[5].RemainingMilliseconds).IsEqualTo(1500u);
        timers.Advance(500);
        await Assert.That(timers.Charges[5]).IsEqualTo(new ZoneCooldownTimer(1000, 1000, 1));
        timers.Advance(1000);
        await Assert.That(timers.Charges.Count).IsEqualTo(0);
        await Assert.That(timers.GetAvailableCharges(5)).IsEqualTo(3u);
    }

    [Test]
    public async Task SessionChangesLiveTimersAndReusedObjectIdsGetFreshState()
    {
        var session = ZoneSession.CreateDetached(new MemoryStream());
        var snapshot = UnitStateSnapshot.Read(Convert.FromHexString(UnitStateSnapshotTests.PlayerEnterBody));
        var unit = new ZoneUnit(snapshot, null, skillContent: Content());
        session.Registry.AddPrepared(unit);
        unit.Cooldowns.StartSkill(1, 10000);
        var writer = new ZoneWriter();
        writer.WriteBc(snapshot.ObjectId); writer.WriteUInt32(1); writer.WriteUInt32(0);
        writer.WriteInt32(25); writer.WriteInt32(0); writer.WriteInt32(0);
        writer.WriteBoolean(false); writer.WriteBoolean(false); writer.WriteBoolean(false);
        var body = writer.ToArray();
        session.ReceiveForTest(new(WzOpcodes.SkillCooldownReduce, body));
        await Assert.That(unit.Cooldowns.GetRemaining(1)).IsEqualTo(7500u);
        session.ReceiveForTest(new(WzOpcodes.SkillCooldownReduce, body[..^1]));
        await Assert.That(unit.Cooldowns.GetRemaining(1)).IsEqualTo(7500u); // whole decode precedes mutation.
        await Assert.That(session.HandlerFaults[WzOpcodes.SkillCooldownReduce]).IsEqualTo(1);
        new ZoneSceneUnitUpdateServices(session.Registry, session.Startup, new()).UpdateSkills(unit, 2500);
        await Assert.That(unit.Cooldowns.GetRemaining(1)).IsEqualTo(5000u);
        writer = new ZoneWriter(); writer.WriteBc(snapshot.ObjectId);
        session.ReceiveForTest(new(WzOpcodes.UnitRemoved, writer.ToArray()));
        var replacement = new ZoneUnit(snapshot, null, skillContent: Content());
        session.Registry.AddPrepared(replacement);
        await Assert.That(replacement.Cooldowns.GetRemaining(1)).IsEqualTo(0u);
        new ZoneSceneUnitUpdateServices(session.Registry, session.Startup, new()).UpdateSkills(unit, 1000);
        await Assert.That(unit.Cooldowns.GetRemaining(1)).IsEqualTo(5000u); // retired allocation cannot tick.
        session.ReceiveForTest(new(WzOpcodes.SkillCooldownReduce, body));
        await Assert.That(session.UnimplementedOpcodes.Count).IsEqualTo(0);
        replacement.Cooldowns.StartChargeRecovery(5, 3000, 3000, 3);
        writer = new ZoneWriter(); writer.WriteBc(snapshot.ObjectId); writer.WriteUInt32(5);
        writer.WriteInt32(0); writer.WriteInt32(1); writer.WriteInt32(0);
        session.ReceiveForTest(new(WzOpcodes.ChangeChargeSkillCooldown, writer.ToArray()));
        await Assert.That(replacement.Cooldowns.GetAvailableCharges(5)).IsEqualTo(1u);
        replacement.Cooldowns.StartSkill(1, 10000);
        writer = new ZoneWriter(); writer.WriteBc(snapshot.ObjectId); writer.WriteUInt32(1); writer.WriteUInt32(0);
        writer.WriteBoolean(false); writer.WriteBoolean(false); writer.WriteBoolean(false); writer.WriteBoolean(false);
        session.ReceiveForTest(new(WzOpcodes.SkillCooldownReset, writer.ToArray()));
        await Assert.That(replacement.Cooldowns.GetRemaining(1)).IsEqualTo(0u);
        // Missing-unit packets decode and return without a fault or fabricated timer state.
        var absent = writer.ToArray(); absent[0] ^= 0x80;
        session.ReceiveForTest(new(WzOpcodes.SkillCooldownReset, absent));
        await Assert.That(session.UnimplementedOpcodes.Count).IsEqualTo(0);
        await Assert.That(session.HandlerFaults.Count).IsEqualTo(1);
    }

    [Test]
    public async Task MatchingCastingStopStartsTheWorldResolvedDuration()
    {
        var session = ZoneSession.CreateDetached(new MemoryStream());
        var snapshot = UnitStateSnapshot.Read(Convert.FromHexString(UnitStateSnapshotTests.PlayerEnterBody));
        var unit = new ZoneUnit(snapshot, null, skillContent: Content());
        session.Registry.AddPrepared(unit);
        session.Skills.Start(new(1, 17, new(0, snapshot.ObjectId, 0, 0, 0, 0), new(0, snapshot.ObjectId), 0));
        byte[] Body(short timeline)
        {
            var writer = new ZoneWriter(); writer.WriteBc(snapshot.ObjectId); writer.WriteInt16(timeline);
            writer.WriteInt32(1); writer.WriteInt32(4000); return writer.ToArray();
        }
        session.ReceiveForTest(new(WzOpcodes.CastingStopped, Body(18)));
        await Assert.That(unit.Cooldowns.GetRemaining(1)).IsEqualTo(0u);
        session.ReceiveForTest(new(WzOpcodes.CastingStopped, Body(17)));
        await Assert.That(unit.Cooldowns.GetRemaining(1)).IsEqualTo(4000u);
        await Assert.That(session.Skills.Casts.ContainsKey(snapshot.ObjectId)).IsFalse();
    }

    [Test]
    public async Task ContentLoaderHandlesNativeNullTagsAndBuildsTagMembership()
    {
        using var connection = new SqliteConnection("Data Source=:memory:"); connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE skills (id, ability_id, cooldown_time, cooldown_tag_id, second_cooldown_tag_id,
                third_cooldown_tag_id, charge_count, charge_cooldown_time, switch_to_skill_cooldown);
            CREATE TABLE levels (id, total_exp);
            CREATE TABLE tagged_skills (skill_id, tag_id);
            INSERT INTO skills VALUES (1, 2, 1500, '--- :null', NULL, NULL, 3, 1000, 't');
            INSERT INTO skills VALUES (2, 3, 5000, 10, 11, 12, 0, 0, 'f');
            INSERT INTO tagged_skills VALUES (1, 10), (1, 10), (2, 10), (NULL, 10), (99, 10);
            """;
        command.ExecuteNonQuery();
        var content = ZoneSkillContent.Load(connection);
        await Assert.That(content.Skills[1].CooldownTagId).IsEqualTo(0u);
        await Assert.That(content.Skills[1].SwitchToSkillCooldown).IsTrue();
        await Assert.That(content.Skills[1].ChargeCount).IsEqualTo(3u);
        await Assert.That(content.Skills[2].ThirdCooldownTagId).IsEqualTo(12u);
        await Assert.That(content.SkillsWithTag(10).Order().SequenceEqual(new uint[] { 1, 2 })).IsTrue();
        await Assert.That(content.TagsForSkill(1).Count).IsEqualTo(1);
    }
}
