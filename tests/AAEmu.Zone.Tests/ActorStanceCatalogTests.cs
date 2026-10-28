using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Content;

namespace AAEmu.Zone.Tests;

public sealed class ActorStanceCatalogTests
{
    [Test]
    public async Task WireSlotsSelectContentIdPlusOneAndLastSourceDuplicateWins()
    {
        var first = Row(1, 1, .4f) with { MaxSpeed = 8, NormalSpeed = 4 };
        var last = Row(3, 1, .3f) with { HeightPivot = .15f, HeightCollider = .6f,
            SizeY = .7f, SizeZ = .2f, ViewOffsetX = 1, ModelOffsetZ = 2,
            MaxSpeed = 5, NormalSpeed = 3, UseCapsule = true, IgnoreCollision = true };
        var relaxed = Row(2, 2, .5f);
        var catalog = Catalog(2, first, relaxed, last);

        var selected = catalog.ForSlot(0);
        await Assert.That(ReferenceEquals(selected.AuthoredRow, last)).IsTrue();
        await Assert.That(selected.Physicalization.Size).IsEqualTo(new Vector3(.6f, 1.4f, .4f));
        await Assert.That(selected.Physicalization.HeightCollider).IsEqualTo(1.2f);
        await Assert.That(selected.Physicalization.HeightPivot).IsEqualTo(.3f);
        await Assert.That(selected.Physicalization.ViewOffset).IsEqualTo(new Vector3(2, 0, 0));
        await Assert.That(selected.Physicalization.ModelOffset).IsEqualTo(new Vector3(0, 0, 4));
        await Assert.That(selected.Physicalization.MaximumSpeed).IsEqualTo(5f);
        await Assert.That(selected.NormalSpeed).IsEqualTo(3f);
        await Assert.That(selected.Physicalization.UseCapsule).IsTrue();
        await Assert.That(selected.Physicalization.IgnoreCollision).IsTrue();
        await Assert.That(ReferenceEquals(catalog.ForSlot(1).AuthoredRow, relaxed)).IsTrue();
        await Assert.That(ReferenceEquals(catalog.Default, catalog.ForSlot(1))).IsTrue();
    }

    [Test]
    public async Task MissingSlotsKeepUnscaledConstructorFieldsAndIdZeroCannotReplaceDefault()
    {
        var catalog = Catalog(4, Row(1, 0, 8), Row(2, 1, .3f), Row(3, 10, 9));
        await Assert.That(catalog.Default.AuthoredRow).IsNull();
        await Assert.That(catalog.Default.Physicalization.Size).IsEqualTo(new Vector3(.5f));
        await Assert.That(catalog.Default.Physicalization.HeightCollider).IsEqualTo(0f);
        await Assert.That(catalog.Default.Physicalization.HeightPivot).IsEqualTo(0f);
        await Assert.That(catalog.Default.Physicalization.MaximumSpeed).IsEqualTo(0f);
        await Assert.That(catalog.Default.NormalSpeed).IsEqualTo(0f);
        await Assert.That(catalog.Default.Physicalization.UseCapsule).IsFalse();
        await Assert.That(ReferenceEquals(catalog.ForSlot(8), catalog.Default)).IsTrue();
        await Assert.That(ReferenceEquals(catalog.ForSlot(-2), catalog.Default)).IsTrue();
        await Assert.That(ReferenceEquals(catalog.ForSlot(10), catalog.Default)).IsTrue();
        await Assert.That(ReferenceEquals(catalog.ForSlot(int.MaxValue), catalog.Default)).IsTrue();
        await Assert.That(() => catalog.ForSlot(9)).Throws<NotSupportedException>();
    }

    [Test]
    public async Task InitialPhysicalizationUsesTheSameDefaultAsTheInstalledSlotTable()
    {
        var row = Row(5, 2, .45f) with { HeightPivot = .2f, HeightCollider = 1.3f,
            SizeZ = .28f, MaxSpeed = 7, UseCapsule = true };
        var model = Model(Row(1, 2, 8), Row(4, 0, 9), row);
        var catalog = new ZoneActorStanceCatalog(model, 1.5f);
        var initial = ZoneActorPhysicalizationRequest.BuildInitial(model, 1.5f, false, _ => null);
        await Assert.That(initial.Stance).IsEqualTo(catalog.Default.Physicalization);
        await Assert.That(ReferenceEquals(catalog.Default.AuthoredRow, row)).IsTrue();
    }

    private static ZoneGameStanceDefinition Row(uint id, int stance, float radius) => new()
    { Id = id, ActorModelId = 1, StanceId = stance, SizeX = radius, SizeY = radius, SizeZ = radius };

    private static ZoneActorStanceCatalog Catalog(float scale, params ZoneGameStanceDefinition[] rows) =>
        new(Model(rows), scale);

    private static ZoneActorModel Model(params ZoneGameStanceDefinition[] rows) =>
        new ZoneActorModelContent([new() { Id = 1 }], rows).Models[1];
}
