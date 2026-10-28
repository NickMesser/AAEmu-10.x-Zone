using AAEmu.Zone.Protocol.Messages;

namespace AAEmu.Zone.Tests;

public sealed class ManagedNpcActorHealthTests
{
    [Test]
    public async Task NativeHealthBindingsKeepValuesAboveDoubleIntegerPrecisionAsDecimalStrings()
    {
        using var fixture = new ManagedNpcScriptEffectsTests.Fixture(); using var actor = fixture.Admit();
        var unit = actor.Actor.Npc.Unit;
        const long maximum = 9007199254740993;
        unit.CommitProgression(unit.Progression, new([], maximum, 100, []));
        actor.Effects.Health.AssociateServerUnit(unit);
        var points = new UnitPoints(unit.Snapshot.ObjectId, 900719925474099201, 0).ToFrame();
        await Assert.That(fixture.Admission.Registry.UpdatePoints(points.Body.Span)).IsTrue();
        using var values = fixture.Lua.Execute("return type(entity.actor:GetHealth()),entity.actor:GetHealth(),type(entity.actor:GetMaxHealth()),entity.actor:GetMaxHealth()");
        await Assert.That(values[0]).IsEqualTo("string");
        await Assert.That(values[1]).IsEqualTo("9007199254740993");
        await Assert.That(values[2]).IsEqualTo("string");
        await Assert.That(values[3]).IsEqualTo("9007199254740993");
        await Assert.That(fixture.Effects.GetActorHealth(actor.Actor)).IsEqualTo(maximum);
        await Assert.That(fixture.Effects.GetActorMaximumHealth(actor.Actor)).IsEqualTo(maximum);
        using var comparison = fixture.Lua.Execute("""
            local ok1,error1=pcall(function() return entity.actor:GetHealth()<=0 end)
            local ok2,error2=pcall(function() return entity.actor:GetMaxHealth()<=0 end)
            return ok1,type(error1),ok2,type(error2)
            """);
        await Assert.That((bool)comparison[0]!).IsFalse();
        await Assert.That(comparison[1]).IsEqualTo("string");
        await Assert.That((bool)comparison[2]!).IsFalse();
        await Assert.That(comparison[3]).IsEqualTo("string");
    }

    [Test]
    public async Task WorldPointsAndCurrentStatPublicationChangeTheOriginalAssociatedUnitRead()
    {
        using var fixture = new ManagedNpcScriptEffectsTests.Fixture(); using var actor = fixture.Admit();
        var unit = actor.Actor.Npc.Unit;
        unit.CommitProgression(unit.Progression, new([], 1000, 100, []));
        actor.Effects.Health.AssociateServerUnit(unit);
        foreach (var (precise, expected) in new (long, string)[] { (0, "0"), (1, "1"), (99, "1"), (100, "1"), (101, "2"), (199, "2") })
        {
            var points = new UnitPoints(unit.Snapshot.ObjectId, precise, 0).ToFrame();
            await Assert.That(fixture.Admission.Registry.UpdatePoints(points.Body.Span)).IsTrue();
            using var values = fixture.Lua.Execute("return entity.actor:GetHealth(),entity.actor:GetMaxHealth()");
            await Assert.That(values[0]).IsEqualTo(expected);
            await Assert.That(values[1]).IsEqualTo("1000");
        }
        unit.CommitProgression(unit.Progression, new([], 431, 100, []));
        using var changed = fixture.Lua.Execute("return entity.actor:GetMaxHealth()");
        await Assert.That(changed[0]).IsEqualTo("431");
    }

    [Test]
    public async Task NoUnitSourceHasNativeFallbackButAssociatedMissingOrPartialStatsRefuse()
    {
        using var fixture = new ManagedNpcScriptEffectsTests.Fixture(); using var actor = fixture.Admit();
        await Assert.That(fixture.Effects.GetActorHealth(actor.Actor)).IsEqualTo(100L);
        await Assert.That(fixture.Effects.GetActorMaximumHealth(actor.Actor)).IsEqualTo(100L);
        actor.Effects.Health.AssociateServerUnit(actor.Actor.Npc.Unit);
        await Assert.That(() => fixture.Effects.GetActorMaximumHealth(actor.Actor)).Throws<NotSupportedException>();
        actor.Actor.Npc.Unit.CommitProgression(actor.Actor.Npc.Unit.Progression, new([], 17, 100, ["unowned buff modifier"]));
        await Assert.That(() => fixture.Effects.GetActorMaximumHealth(actor.Actor)).Throws<NotSupportedException>();
        //Current health is its independent raw point-field read, requiring no maximum lookup.
        await Assert.That(fixture.Effects.GetActorHealth(actor.Actor)).IsEqualTo(
            Scripting.ZoneManagedNpcActorHealth.ReadCurrent(actor.Actor.Npc.Unit.PreciseHealth));
    }

    [Test]
    public async Task AssociationRequiresExactUnitAllocationAndRetirementRevokesTheHealthView()
    {
        using var fixture = new ManagedNpcScriptEffectsTests.Fixture(); using var first = fixture.Admit(); using var other = fixture.Admit();
        await Assert.That(() => first.Effects.Health.AssociateServerUnit(other.Actor.Npc.Unit)).Throws<InvalidOperationException>();
        await Assert.That(first.Effects.Health.HasServerUnit).IsFalse();
        first.Effects.Health.AssociateServerUnit(first.Actor.Npc.Unit);
        fixture.Scene.Remove(first.Actor.Handle);
        await Assert.That(() => first.Effects.Health.CurrentHealth).Throws<ObjectDisposedException>();
        await Assert.That(() => fixture.Effects.GetActorHealth(first.Actor)).Throws<InvalidOperationException>();
        await Assert.That(fixture.Effects.GetActorHealth(other.Actor)).IsEqualTo(100L);
    }

    [Test]
    public async Task SignedNativeRawFieldRoundingIsPreservedWithoutFloatConversion()
    {
        foreach (var (precise, expected) in new (long, long)[]
        {
            (long.MinValue, -92233720368547757), (-199, 0), (-101, 0), (-100, -1), (-1, 1),
            (0, 0), (1, 1), (100, 1), (101, 2), (900719925474099201, 9007199254740993),
            (long.MaxValue, 92233720368547759)
        })
            await Assert.That(Scripting.ZoneManagedNpcActorHealth.ReadCurrent(precise)).IsEqualTo(expected);
    }
}
