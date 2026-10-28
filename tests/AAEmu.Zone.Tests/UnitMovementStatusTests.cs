using AAEmu.Zone.Combat;
using AAEmu.Zone.Content;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Protocol.Units;
using AAEmu.Zone.Units;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace AAEmu.Zone.Tests;

public sealed class UnitMovementStatusTests
{
    private const uint ObjectId = 0x1234;
    private static BuffStatTemplate Stun(uint id = 100, byte rule = 1) => new(id, 1, rule, 255, 255, 0, false, false, Stun: true);
    private static BuffStatTemplate Sleep() => new(200, 2, 1, 255, 255, 0, false, false, Sleep: true);
    private static BuffStatTemplate Root() => new(300, 3, 1, 255, 255, 0, false, false, Root: true);
    private static UnitBuff Buff(uint id, uint index) => new(index, new(ZoneCasterKind.Unit, ObjectId), 1, 1, 1, 100, 0, 10, 0, id, 0, 0, 0);
    private static BuffCreated Created(uint id, uint index) => new(new(ZoneCasterKind.Unit, ObjectId), 1, ObjectId, index, id, 1, 1, 0, 1, 1, 100, 0, 10);

    [Test]
    public async Task ContentReadsTheOriginalBooleanColumnsAndPsychokinesisFloat()
    {
        using var db = new SqliteConnection("Data Source=:memory:"); db.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE buffs(id,kind_id,stack_rule_id,max_stack,max_charge,max_life_time,per_unit_creation,one_time,
              off_passive,off_passive_exection_tag_id,remove_on_land,gliding,scale,scaleDuration,stun,sleep,root,psychokinesis,psychokinesis_speed);
            INSERT INTO buffs VALUES(100,1,1,1,1,0,'f','f','f',0,'f','f',1,1,'T','t','1','t',2.75);
            CREATE TABLE passive_buffs(id,buff_id,active,ability_id,level);
            CREATE TABLE buff_unit_modifiers(id,owner_id,buff_id,tag_id,owner_type,enable);
            CREATE TABLE tagged_buffs(id,buff_id,tag_id);
            CREATE TABLE buff_passive_buffs(buff_id,passive_buff_id);
            CREATE TABLE dynamic_unit_modifiers(id,buff_id,unit_attribute_id,unit_modifier_type_id,func_type,func_id,enable);
            CREATE TABLE linear_funcs(id,start_value,end_value);
            CREATE TABLE manual_funcs(id,value_list);
            CREATE TABLE dynamic_funcs(id,attribute_id,value);
            CREATE TABLE formula_funcs(id,formula);
            """;
        command.ExecuteNonQuery();
        var template = ZoneBuffContent.Load(db).Template(100);
        await Assert.That(template.Stun && template.Sleep && template.Root && template.Psychokinesis).IsTrue();
        await Assert.That(template.PsychokinesisSpeed).IsEqualTo(2.75f);
    }

    [Test]
    public async Task RestoredGoodBadHiddenInstancesContributeOnceRegardlessOfStackAndCharge()
    {
        var (registry, unit) = Owner(Content([Stun(), Sleep(), Root()]), new([Buff(100, 1)], [Buff(200, 2)], [Buff(300, 3)]));
        await Assert.That(unit.MovementStatus.CopySnapshot()).IsEqualTo(new ZoneUnitMovementStatusSnapshot(1, 1, 1, true, true, true));
        await Assert.That(unit.CopyInitialBuffEffects().Select(e => e.Buff.TemplateId).SequenceEqual(new uint[] { 100, 200, 300 })).IsTrue();
        await Assert.That(unit.CopyInitialBuffEffects().All(e => e.IsInitialRestore && e.Kind == ZoneUnitBuffEffectKind.Apply)).IsTrue();
        await Assert.That(registry.Units[ObjectId]).IsEqualTo(unit);
    }

    [Test]
    public async Task RefreshAndZeroStackUpdateDoNotIncrementOrRemoveTheInstanceCounter()
    {
        var (registry, unit) = Owner(Content([Stun(rule: 4)]));
        registry.CreateBuff(Created(100, 1).ToFrame().Body.Span);
        registry.CreateBuff(Created(100, 2).ToFrame().Body.Span);
        await Assert.That(unit.Buffs.Good.Count).IsEqualTo(1);
        await Assert.That(unit.Buffs.Good[0].Stack).IsEqualTo(2u);
        await Assert.That(unit.MovementStatus.CopySnapshot().StunCounter).IsEqualTo((byte)1);
        registry.UpdateBuff(new BuffUpdated(ObjectId, 2, 0, 0, 200, 3).ToFrame().Body.Span);
        await Assert.That(unit.MovementStatus.CopySnapshot().Stunned).IsTrue();
        registry.RemoveBuff(new BuffRemoved(ObjectId, 2).ToFrame().Body.Span);
        await Assert.That(unit.MovementStatus.CopySnapshot()).IsEqualTo(default(ZoneUnitMovementStatusSnapshot));
    }

    [Test]
    public async Task IndependentInstancesRetainStunUntilTheLastActualRemoval()
    {
        var (registry, unit) = Owner(Content([Stun(rule: 6)]));
        registry.CreateBuff(Created(100, 1).ToFrame().Body.Span); registry.CreateBuff(Created(100, 2).ToFrame().Body.Span);
        await Assert.That(unit.MovementStatus.CopySnapshot().StunCounter).IsEqualTo((byte)2);
        registry.RemoveBuff(new BuffRemoved(ObjectId, 1).ToFrame().Body.Span);
        await Assert.That(unit.MovementStatus.CopySnapshot().Stunned).IsTrue();
        await Assert.That(unit.MovementStatus.CopySnapshot().StunCounter).IsEqualTo((byte)1);
        registry.RemoveBuff(new BuffRemoved(ObjectId, 2).ToFrame().Body.Span);
        await Assert.That(unit.MovementStatus.CopySnapshot().Stunned).IsFalse();
    }

    [Test]
    public async Task PassiveLearnResetAndActualBuffGrantsDoNotFeedTheActiveBuffManagerCounters()
    {
        var grant = new BuffStatTemplate(400, 1, 1, 1, 1, 0, false, false);
        var content = Content([Stun(), grant], [new(700, 100, false, 0, 1)], [(400, 700)]);
        var (registry, unit) = Owner(content);
        registry.LearnBuff(new BuffLearned(ObjectId, 700).ToFrame().Body.Span);
        await Assert.That(unit.Progression.Passives.Current.Contains(700u)).IsTrue();
        await Assert.That(unit.MovementStatus.CopySnapshot().Stunned).IsFalse();
        registry.ResetSkills(new SkillsReset(ObjectId, 0).ToFrame().Body.Span);
        registry.CreateBuff(Created(400, 1).ToFrame().Body.Span);
        await Assert.That(unit.Progression.Passives.Current.Contains(700u)).IsTrue();
        await Assert.That(unit.MovementStatus.CopySnapshot().Stunned).IsFalse();
        registry.RemoveBuff(new BuffRemoved(ObjectId, 1).ToFrame().Body.Span);
        await Assert.That(unit.Progression.Passives.Current.Contains(700u)).IsFalse();
    }

    [Test]
    public async Task DeathAndResurrectionClearOnlyCopiedFlagsAndLaterMutationRepublishesTheCache()
    {
        var (registry, unit) = Owner(Content([Stun(), Sleep(), Root()]), new([Buff(100, 1)], [Buff(200, 2)], [Buff(300, 3)]));
        ZoneUnitMovementStatusSnapshot? duringCombatTransition = null;
        unit.CombatTransitioned += (_, _) => duringCombatTransition = unit.MovementStatus.CopySnapshot();
        registry.Kill(new UnitDeath(ObjectId).ToFrame().Body.Span);
        await Assert.That(duringCombatTransition!.Value.Stunned).IsTrue(); //Clear follows native combat event44.
        await Assert.That(unit.MovementStatus.CopySnapshot()).IsEqualTo(new ZoneUnitMovementStatusSnapshot(1, 1, 1, false, false, false));
        registry.UpdateBuff(new BuffUpdated(ObjectId, 1, 0, 0, 1, 0).ToFrame().Body.Span);
        await Assert.That(unit.IsDead).IsTrue();
        await Assert.That(unit.MovementStatus.CopySnapshot().Stunned).IsTrue();
        registry.Resurrect(new UnitResurrection(ObjectId, new(0, 0, 10), 0).ToFrame().Body.Span);
        await Assert.That(unit.IsDead).IsFalse();
        await Assert.That(unit.MovementStatus.CopySnapshot()).IsEqualTo(new ZoneUnitMovementStatusSnapshot(1, 1, 1, false, false, false));
        registry.RemoveBuff(new BuffRemoved(ObjectId, 1).ToFrame().Body.Span);
        await Assert.That(unit.MovementStatus.CopySnapshot()).IsEqualTo(new ZoneUnitMovementStatusSnapshot(0, 1, 1, false, true, true));
    }

    [Test]
    public async Task ZeroHealthAdmissionStillPublishesItsRestoredStatuses()
    {
        var (_, unit) = Owner(Content([Stun()]), new([Buff(100, 1)], [], []), health: 0);
        await Assert.That(unit.IsDead).IsTrue();
        await Assert.That(unit.MovementStatus.CopySnapshot().Stunned).IsTrue();
    }

    [Test]
    public async Task AdvancingPastDurationDoesNotInventAWorldRemoval()
    {
        var (registry, unit) = Owner(Content([Stun() with { RemoveOnLand = true }]), new([Buff(100, 1)], [], []));
        registry.AdvanceBuffTiming(ObjectId, 1000);
        await Assert.That(unit.Buffs.Good.Count).IsEqualTo(1);
        await Assert.That(unit.MovementStatus.CopySnapshot().Stunned).IsTrue();
    }

    [Test]
    public async Task ModelEffectEventsExposeOriginalListAndStatusPhasesAndExactRefreshPayload()
    {
        var template = Stun() with { Psychokinesis = true, PsychokinesisSpeed = 2.75f };
        var (registry, unit) = Owner(Content([template]));
        var seen = new List<(ZoneUnitBuffEffect Effect, int Instances, bool Stunned)>();
        unit.BuffEffect += (owner, effect) => seen.Add((effect, owner.Buffs.Good.Count, owner.MovementStatus.CopySnapshot().Stunned));
        registry.CreateBuff(Created(100, 1).ToFrame().Body.Span);
        registry.UpdateBuff(new BuffUpdated(ObjectId, 1, 0, 0, 0, 0).ToFrame().Body.Span);
        registry.CreateBuff(Created(100, 2).ToFrame().Body.Span);
        registry.RemoveBuff(new BuffRemoved(ObjectId, 2).ToFrame().Body.Span);
        await Assert.That(seen.Count).IsEqualTo(3);
        await Assert.That(seen.Select(x => x.Instances).SequenceEqual(new[] { 1, 1, 1 })).IsTrue();
        await Assert.That(seen.Select(x => x.Stunned).SequenceEqual(new[] { false, true, true })).IsTrue();
        await Assert.That(seen[1].Effect.IsRefresh).IsTrue();
        await Assert.That(seen[1].Effect.Buff.Index).IsEqualTo(2u);
        await Assert.That(seen[2].Effect.Kind).IsEqualTo(ZoneUnitBuffEffectKind.Remove);
        await Assert.That(unit.CopyBuffModelEffectJournal().Select(x => x.Sequence).SequenceEqual(new ulong[] { 1, 2, 3 })).IsTrue();
        await Assert.That(unit.MovementStatus.CopySnapshot().Stunned).IsFalse();
    }

    [Test]
    public async Task PendingModelJournalRetainsApplyRemoveApplyAndSeparatesInitialInventory()
    {
        var template = Stun() with { Psychokinesis = true };
        var (registry, unit) = Owner(Content([template]), new([Buff(100, 1)], [], []));
        registry.CreateBuff(Created(100, 2).ToFrame().Body.Span);
        registry.RemoveBuff(new BuffRemoved(ObjectId, 2).ToFrame().Body.Span);
        registry.CreateBuff(Created(100, 3).ToFrame().Body.Span);
        var journal = unit.CopyBuffModelEffectJournal();
        await Assert.That(unit.CopyInitialBuffEffects()[0].Buff.Index).IsEqualTo(1u);
        await Assert.That(journal.Select(x => x.Kind).SequenceEqual(new[] { ZoneUnitBuffEffectKind.Apply, ZoneUnitBuffEffectKind.Remove, ZoneUnitBuffEffectKind.Apply })).IsTrue();
        await Assert.That(journal.Select(x => x.Buff.Index).SequenceEqual(new uint[] { 2, 2, 3 })).IsTrue();
        await Assert.That(journal[0].IsRefresh).IsTrue();
        await Assert.That(journal[2].IsRefresh).IsFalse();
    }

    [Test]
    public async Task ReentrantSameUnitCollectionMutationRefusesRatherThanReturningFalseClearStatus()
    {
        var (registry, unit) = Owner(Content([Stun(rule: 6)]));
        unit.BuffEffect += (_, effect) => { if (effect.Buff.Index == 1) registry.CreateBuff(Created(100, 2).ToFrame().Body.Span); };
        registry.CreateBuff(Created(100, 1).ToFrame().Body.Span);
        await Assert.That(unit.Buffs.Good.Count).IsEqualTo(2);
        await Assert.That(unit.MovementStatus.IsKnown).IsFalse();
        await Assert.That(() => unit.MovementStatus.CopySnapshot()).Throws<NotSupportedException>();
    }

    [Test]
    public async Task RetiredAllocationCannotWriteItsRemainingCounterWorkIntoAReusedNumericId()
    {
        var (registry, unit) = Owner(Content([Stun()]));
        ZoneUnit? replacement = null;
        unit.BuffEffect += (_, _) => { registry.RemovePrepared(unit); replacement = registry.Prepare(Snapshot(new([], [], []))); registry.AddPrepared(replacement); };
        registry.CreateBuff(Created(100, 1).ToFrame().Body.Span);
        await Assert.That(registry.Units[ObjectId]).IsEqualTo(replacement);
        await Assert.That(replacement!.MovementStatus.CopySnapshot()).IsEqualTo(default(ZoneUnitMovementStatusSnapshot));
        await Assert.That(replacement.CopyBuffModelEffectJournal().Count).IsEqualTo(0);
    }

    [Test]
    public async Task MissingMetadataRefusesOnlyActualUnknownInstancesAndNativeMissingDescriptorsAreSkipped()
    {
        var empty = new ZoneUnit(Snapshot(new([], [], [])), null);
        await Assert.That(empty.MovementStatus.CopySnapshot()).IsEqualTo(default(ZoneUnitMovementStatusSnapshot));
        await Assert.That(empty.CopyInitialBuffEffects().Count).IsEqualTo(0);
        var unknown = new ZoneUnit(Snapshot(new([Buff(100, 1)], [], [])), null);
        await Assert.That(() => unknown.MovementStatus.CopySnapshot()).Throws<NotSupportedException>();
        await Assert.That(() => unknown.CopyInitialBuffEffects()).Throws<NotSupportedException>();
        var invalid = new ZoneUnit(unknown.Snapshot, null, buffContent: ZoneBuffContent.Empty);
        await Assert.That(invalid.MovementStatus.CopySnapshot()).IsEqualTo(default(ZoneUnitMovementStatusSnapshot));
        await Assert.That(invalid.CopyInitialBuffEffects().Count).IsEqualTo(0);
    }

    [Test]
    public async Task JournalOverflowRefusesLateReplayWithoutDroppingLiveNotifications()
    {
        var unit = new ZoneUnit(Snapshot(new([], [], [])), null);
        var template = Stun() with { Psychokinesis = true };
        ulong last = 0; unit.BuffEffect += (_, effect) => last = effect.Sequence;
        for (var i = 0; i <= ZoneUnit.BuffModelEffectJournalCapacity; i++) unit.ApplyBuffEffect(new(ZoneUnitBuffEffectKind.Apply, Buff(100, (uint)i), template));
        await Assert.That(last).IsEqualTo((ulong)ZoneUnit.BuffModelEffectJournalCapacity + 1);
        await Assert.That(() => unit.CopyBuffModelEffectJournal()).Throws<NotSupportedException>();
    }

    [Test]
    public async Task CounterByteBoundsRetainTheirPreviousValueInsteadOfWrapping()
    {
        var all = Stun() with { Sleep = true, Root = true };
        await Assert.That(ZoneBuffMovementStatus.Add(new(255, 255, 255), all)).IsEqualTo(new ZoneBuffMovementStatusCounters(255, 255, 255));
        await Assert.That(ZoneBuffMovementStatus.Remove(default, all)).IsEqualTo(default(ZoneBuffMovementStatusCounters));
    }

    [Test]
    public async Task TheCacheMatchesAllOriginalMachineCodeFlagModeAndCounterControls()
    {
        using var stream = typeof(UnitMovementStatusTests).Assembly.GetManifestResourceStream("AAEmu.Zone.Tests.Fixtures.npc-buff-status-native-controls.json")!;
        using var fixture = JsonDocument.Parse(stream);
        var count = 0;
        foreach (var row in fixture.RootElement.GetProperty("rows").EnumerateArray())
        {
            var flags = row.GetProperty("flags").EnumerateArray().Select(v => v.GetBoolean()).ToArray();
            var before = row.GetProperty("before").EnumerateArray().Select(v => v.GetByte()).ToArray();
            var after = row.GetProperty("after").EnumerateArray().Select(v => v.GetByte()).ToArray();
            var template = Stun() with { Stun = flags[0], Sleep = flags[1], Root = flags[2] };
            var current = new ZoneBuffMovementStatusCounters(before[0], before[1], before[2]);
            var actual = row.GetProperty("nativeMode").GetInt32() switch
            { 1 => ZoneBuffMovementStatus.Add(current, template), 3 => ZoneBuffMovementStatus.Remove(current, template), _ => current };
            await Assert.That(actual).IsEqualTo(new ZoneBuffMovementStatusCounters(after[0], after[1], after[2]));
            count++;
        }
        await Assert.That(count).IsEqualTo(96);
    }

    [Test]
    public async Task FullCategoryRefusesANewInstanceButStillRefreshesWithoutAnExtraContribution()
    {
        var templates = Enumerable.Range(100, 33).Select(id => Stun((uint)id)).ToArray();
        var (registry, unit) = Owner(Content(templates), new(Enumerable.Range(100, 32).Select(id => Buff((uint)id, (uint)id)).ToArray(), [], []));
        await Assert.That(registry.CreateBuff(Created(132, 900).ToFrame().Body.Span)).IsFalse();
        await Assert.That(registry.CreateBuff(Created(100, 901).ToFrame().Body.Span)).IsTrue();
        await Assert.That(unit.MovementStatus.CopySnapshot().StunCounter).IsEqualTo((byte)32);
    }

    [Test]
    public async Task FailedModelEffectCannotLeaveTheCommittedBuffWithAFalseClearStatus()
    {
        var (registry, unit) = Owner(Content([Stun()]));
        unit.BuffEffect += (_, _) => throw new NotSupportedException("Unowned original model reset");
        await Assert.That(() => registry.CreateBuff(Created(100, 1).ToFrame().Body.Span)).Throws<NotSupportedException>();
        await Assert.That(unit.Buffs.Good.Count).IsEqualTo(1);
        await Assert.That(() => unit.MovementStatus.CopySnapshot()).Throws<NotSupportedException>();
    }

    private static ZoneBuffContent Content(IEnumerable<BuffStatTemplate> templates, IEnumerable<PassiveBuffTemplate>? passives = null,
        IEnumerable<(uint BuffId, uint PassiveId)>? granted = null) => new(templates, passives ?? [], [], [], [], granted ?? []);

    private static (ZoneUnitRegistry Registry, ZoneUnit Unit) Owner(ZoneBuffContent buffs, UnitBuffs? initial = null, long health = 10000)
    {
        var stats = new ZoneStatContent(new uint[] { 14, 15, 22, 23, 24, 25, 26, 27 }.Select(kind =>
            new UnitFormulaDefinition(kind, ZoneUnitKind.Npc, kind, new ContentFormula("100"))), [], [new(11, 0, 0, 0, 0)], [], [], [], [], [], [], buffs);
        var registry = new ZoneUnitRegistry(statContent: stats);
        var unit = registry.Prepare(Snapshot(initial ?? new([], [], []), health)); registry.AddPrepared(unit);
        return (registry, unit);
    }

    private static UnitStateSnapshot Snapshot(UnitBuffs buffs, long health = 10000)
    {
        var source = UnitStateSnapshot.Read(Convert.FromHexString(UnitStateSnapshotTests.PlayerEnterBody));
        return new()
        {
            ObjectId = ObjectId, Name = "movement-status-control", WorldId = source.WorldId, RegionId = source.RegionId, GlobalWorld = source.GlobalWorld,
            Identity = new(ZoneUnitKind.Npc, NpcObjectId: 77, TemplateId: 11), MasterName = source.MasterName, Position = source.Position, Scale = source.Scale,
            Level = source.Level, HeirLevel = source.HeirLevel, SecondaryLevel = source.SecondaryLevel, SecondaryHeirLevel = source.SecondaryHeirLevel,
            SlotSelectors = source.SlotSelectors, ModelId = source.ModelId, Equipment = source.Equipment, Appearance = source.Appearance,
            TargetId = source.TargetId, PreciseHealth = health, PreciseMana = source.PreciseMana, Attachment = source.Attachment,
            Bonding = source.Bonding, Posture = source.Posture, ActiveWeapon = source.ActiveWeapon, StateType = source.StateType,
            AppellationStamp = source.AppellationStamp, VehicleDyeing = source.VehicleDyeing, TemporaryFaction = source.TemporaryFaction,
            Skills = [], PassiveBuffs = [], Heading = source.Heading, RaceGender = source.RaceGender,
            PackedState = source.PackedState, Flags = 1, AttackFactionFlags = source.AttackFactionFlags, FirstHitter = source.FirstHitter,
            HighAbilities = [], GmModes = source.GmModes, Character = null, Buffs = buffs, Action = source.Action
        };
    }
}
