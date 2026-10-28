using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Scripting;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.Tests;

public sealed class ManagedNpcCombatScriptServicesTests
{
    [Test]
    public async Task DeathPublishesItsRealCombatTransitionAfterDeathAndPreservesAttackFlags()
    {
        var unit = CombatUnit();
        var events = new List<(bool Combat, bool Dead, byte Attack)>();
        unit.CombatTransitioned += (current, engaged) => events.Add((engaged, current.IsDead, current.AttackFactionFlags));
        unit.Die(null);
        await Assert.That(events.Single()).IsEqualTo((false, true, (byte)0xA5));
        await Assert.That(unit.Flags).IsEqualTo((ushort)0x2000);
        await Assert.That(unit.AttackFactionFlags).IsEqualTo((byte)0xA5);
        unit.Die(null);
        await Assert.That(events.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ScriptCombatClearPreservesAttackFlagsAndWzClearsThemAfterTheActualTransition()
    {
        var unit = CombatUnit(); var registry = new ZoneUnitRegistry(); registry.AddPrepared(unit);
        var observations = new List<(bool Event, bool StoredCombat, byte Attack)>();
        unit.CombatTransitioned += (current, engaged) => observations.Add((engaged, current.IsInCombat, current.AttackFactionFlags));
        unit.ClearCombatFromScript();
        await Assert.That(observations.Single()).IsEqualTo((false, false, (byte)0xA5));
        await Assert.That(unit.AttackFactionFlags).IsEqualTo((byte)0xA5);
        await Assert.That(unit.Flags).IsEqualTo((ushort)0x2400);
        unit.ClearCombatFromScript();
        await Assert.That(observations.Count).IsEqualTo(1);
        unit.SetCombat(true);
        await Assert.That(observations[1]).IsEqualTo((true, true, (byte)0xA5));
        var frame = new CombatCleared(unit.Snapshot.ObjectId).ToFrame();
        await Assert.That(registry.ClearCombat(frame.Body.Span)).IsTrue();
        await Assert.That(observations[2]).IsEqualTo((false, false, (byte)0xA5));
        await Assert.That(unit.AttackFactionFlags).IsEqualTo((byte)0);
        await Assert.That(unit.Flags).IsEqualTo((ushort)0x2400);
    }

    [Test]
    public async Task OriginalCombatTransitionSelectsTheScriptProxyIntervalWithoutInitialPolling()
    {
        using var fixture = new ManagedNpcScriptEffectsTests.Fixture(); using var actor = fixture.Admit();
        var unit = actor.Actor.Npc.Unit; unit.SetCombat(false);
        fixture.Effects.SetScriptUpdateRate(actor.Actor, 1, 2);
        void Changed(ZoneUnit current, bool engaged) => fixture.Effects.SelectScriptUpdateInterval(actor.Actor, engaged);
        unit.CombatTransitioned += Changed;
        try
        {
            await Assert.That(fixture.Effects.CopyState(actor.Actor).SelectedInterval).IsEqualTo(1f / 3f);
            unit.SetCombat(true);
            await Assert.That(fixture.Effects.CopyState(actor.Actor).SelectedInterval).IsEqualTo(1f);
            unit.ClearCombatFromScript();
            await Assert.That(fixture.Effects.CopyState(actor.Actor).SelectedInterval).IsEqualTo(2f);
        }
        finally { unit.CombatTransitioned -= Changed; }
    }

    [Test]
    public async Task FreshNoTargetClearUsesOwnedAggroAndEmitsOnlyAfterTheCombatEvent()
    {
        using var fixture = new ManagedNpcScriptEffectsTests.Fixture(); using var actor = fixture.Admit();
        using var attention = fixture.Admit(); actor.Actor.SetTarget(attention.Actor.Handle);
        var frames = new List<ZoneFrame>(); var eventFrameCounts = new List<int>();
        using var services = new ZoneManagedNpcCombatScriptServices(fixture.Admission.Registry, fixture.Admission.Catalog, frames.Add);
        using var owner = services.Bind(actor.Actor, new());
        actor.Actor.Npc.Unit.SetCombat(true);
        void Changed(ZoneUnit unit, bool engaged) { if (!engaged) eventFrameCounts.Add(frames.Count); }
        actor.Actor.Npc.Unit.CombatTransitioned += Changed;
        try
        {
            services.ClearCombat(actor.Actor);
            await Assert.That(eventFrameCounts.Single()).IsEqualTo(0);
            await Assert.That(actor.Actor.Npc.Unit.IsInCombat).IsFalse();
            await Assert.That(frames.Single().Opcode).IsEqualTo(ZwOpcodes.ClearCombat);
            await Assert.That(ReadUnit(frames.Single())).IsEqualTo(actor.Actor.Npc.ObjectId);
            await Assert.That(owner.Attributes.Mode).IsEqualTo(3);
            await Assert.That(owner.Attributes.RestorePending).IsFalse();
            services.ClearCombat(actor.Actor);
            await Assert.That(frames.Count).IsEqualTo(1);
            await Assert.That(ReferenceEquals(actor.Actor.AttentionTarget, attention.Actor.AiObject)).IsTrue();
        }
        finally { actor.Actor.Npc.Unit.CombatTransitioned -= Changed; }
    }

    [Test]
    public async Task SavedFlyingAndRetiredActorRefuseBeforeUnsupportedMutations()
    {
        using var fixture = new ManagedNpcScriptEffectsTests.Fixture(); using var actor = fixture.Admit();
        var frames = new List<ZoneFrame>(); var attributes = new ZoneNpcAiUnitAttributeRestoreState();
        using var services = new ZoneManagedNpcCombatScriptServices(fixture.Admission.Registry, fixture.Admission.Catalog, frames.Add);
        using var owner = services.Bind(actor.Actor, attributes);
        attributes.CopyFields(3, true, true); actor.Actor.Npc.Unit.SetCombat(true);
        await Assert.That(() => services.ClearCombat(actor.Actor)).Throws<NotSupportedException>();
        await Assert.That(attributes.RestorePending).IsTrue();
        await Assert.That(actor.Actor.Npc.Unit.IsInCombat).IsTrue();
        await Assert.That(frames.Count).IsEqualTo(0);
        attributes.CopyFields(3, false, false); fixture.Scene.Remove(actor.Actor.Handle);
        await Assert.That(owner.IsCurrent).IsFalse();
        await Assert.That(() => services.ClearCombat(actor.Actor)).Throws<InvalidOperationException>();
    }

    private static uint ReadUnit(ZoneFrame frame)
    { var reader = new ZoneReader(frame.Body.Span); var value = reader.ReadBc(); reader.EnsureFullyConsumed(); return value; }

    private static ZoneUnit CombatUnit()
    {
        var source = UnitStateSnapshot.Read(Convert.FromHexString(UnitStateSnapshotTests.PlayerEnterBody));
        return new(new UnitStateSnapshot
        {
            ObjectId = source.ObjectId, Name = source.Name, WorldId = source.WorldId, RegionId = source.RegionId, GlobalWorld = source.GlobalWorld,
            Identity = source.Identity, MasterName = source.MasterName, Position = source.Position, Scale = source.Scale,
            Level = source.Level, HeirLevel = source.HeirLevel, SecondaryLevel = source.SecondaryLevel, SecondaryHeirLevel = source.SecondaryHeirLevel,
            SlotSelectors = source.SlotSelectors, ModelId = source.ModelId, Equipment = source.Equipment, Appearance = source.Appearance,
            TargetId = source.TargetId, PreciseHealth = source.PreciseHealth, PreciseMana = source.PreciseMana, Attachment = source.Attachment,
            Bonding = source.Bonding, Posture = source.Posture, ActiveWeapon = source.ActiveWeapon, StateType = source.StateType,
            AppellationStamp = source.AppellationStamp, VehicleDyeing = source.VehicleDyeing, TemporaryFaction = source.TemporaryFaction,
            Skills = source.Skills, PassiveBuffs = source.PassiveBuffs, Heading = source.Heading, RaceGender = source.RaceGender,
            PackedState = source.PackedState, Flags = 0x2401, AttackFactionFlags = 0xA5, FirstHitter = source.FirstHitter,
            HighAbilities = source.HighAbilities, GmModes = source.GmModes, Character = source.Character, Buffs = source.Buffs, Action = source.Action
        }, null);
    }
}
