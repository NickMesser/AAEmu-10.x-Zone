using System.Numerics;
using AAEmu.Zone.Ai;
using AAEmu.Zone.Content;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Scripting;

namespace AAEmu.Zone.Tests;

public sealed class ManagedNpcProfileFactoryTests
{
    private static readonly ZoneModelDefinition ActorModel = new() { Id = 27, Name = "elf_male", Subtype = "ActorModel", SubtypeId = 7 };

    [Test]
    public async Task InitialPuppetProfileOwnsTheExactEntryAndItsAllocationCadence()
    {
        using var fixture = new ManagedNpcAdmissionTests.Fixture();
        var npc = fixture.Npc(model: ActorModel); fixture.Publish(npc);
        var handle = fixture.Catalog.Register(npc); var backend = new object();
        await Assert.That(ZoneAiBackendBindings.TryClaim(handle, backend)).IsTrue();
        try
        {
            using var factory = new ZoneManagedNpcProfileFactory(fixture.Registry, fixture.Catalog, _ => new(4, 5, 6));
            using var lua = new ZoneManagedLuaRuntime();
            using var scene = new ZoneManagedNpcActorScene(fixture.Registry, fixture.Catalog, lua, new(),
                _ => new(4, 5, 6), () => 1000, () => 1, _ => { }, TimeSpan.FromMilliseconds(20));
            var profile = factory.Create(handle, npc, 713);
            using var owner = (ZoneManagedNpcProxyOwner)profile.Lifetime!;
            await Assert.That(profile.Object.EntityId).IsEqualTo(713u);
            await Assert.That(profile.Object.Position).IsEqualTo(new Vector3(4, 5, 6));
            await Assert.That(profile.Object.Radius).IsEqualTo(0f);
            await Assert.That(profile.ObjectType).IsEqualTo((short)5);
            await Assert.That(profile.IsPuppet).IsTrue();
            await Assert.That(profile.Controls.MovementUrgency).IsEqualTo(.4f);
            await Assert.That(profile.Controls.DelayedStance).IsEqualTo(-1);
            await Assert.That(owner.Entry).IsNull();
            var actor = scene.Adopt(handle, profile.Object, profile.ObjectType, profile.ProxyAllocationKey,
                profile.Controls, profile.IsPuppet, null);
            owner.Attach(actor);
            await Assert.That(ReferenceEquals(owner.Entry, actor.Entry)).IsTrue();
            await Assert.That(actor.Entry.Activation.SkippedUpdates).IsEqualTo((int)(((owner.AllocationKey >> 8) & 0xffffff) % 9));
            await Assert.That(() => owner.Attach(actor)).Throws<InvalidOperationException>();
            owner.Dispose();
            await Assert.That(owner.IsDisposed).IsTrue();
            await Assert.That(actor.HasActor).IsFalse();
            await Assert.That(factory.LiveCount).IsEqualTo(0);
            await Assert.That(factory.RetiredCount).IsEqualTo(1ul);
            await Assert.That(scene.Objects.Count).IsEqualTo(0);
        }
        finally { ZoneAiBackendBindings.Release(handle, backend); }
    }

    [Test]
    public async Task BackendClaimExactUnitAndModelAdmissionPrecedeProxyAllocation()
    {
        using var fixture = new ManagedNpcAdmissionTests.Fixture();
        var npc = fixture.Npc(model: ActorModel); fixture.Publish(npc);
        var handle = fixture.Catalog.Register(npc);
        using var factory = new ZoneManagedNpcProfileFactory(fixture.Registry, fixture.Catalog, _ => Vector3.Zero);
        await Assert.That(() => factory.Create(handle, npc, 714)).Throws<InvalidOperationException>();
        var backend = new object(); ZoneAiBackendBindings.TryClaim(handle, backend);
        try
        {
            await Assert.That(() => factory.Create(handle, npc, 0)).Throws<ArgumentOutOfRangeException>();
            var profile = factory.Create(handle, npc, 715);
            using var owner = profile.Lifetime!;
            await Assert.That(() => factory.Create(handle, npc, 716)).Throws<InvalidOperationException>();
            await Assert.That(factory.LiveCount).IsEqualTo(1);
            fixture.Catalog.Retire(npc);
            await Assert.That(() => factory.Create(handle, npc, 717)).Throws<InvalidOperationException>();
        }
        finally { ZoneAiBackendBindings.Release(handle, backend); }
    }

    [Test]
    public async Task PositionCallbackRetirementCannotPublishAProxy()
    {
        using var fixture = new ManagedNpcAdmissionTests.Fixture();
        var npc = fixture.Npc(model: ActorModel); fixture.Publish(npc);
        var handle = fixture.Catalog.Register(npc); var backend = new object();
        ZoneAiBackendBindings.TryClaim(handle, backend);
        try
        {
            using var factory = new ZoneManagedNpcProfileFactory(fixture.Registry, fixture.Catalog, _ =>
            { fixture.Catalog.Retire(npc); return Vector3.Zero; });
            await Assert.That(() => factory.Create(handle, npc, 718)).Throws<InvalidOperationException>();
            await Assert.That(factory.LiveCount).IsEqualTo(0);
            await Assert.That(factory.RetiredCount).IsEqualTo(0ul);
        }
        finally { ZoneAiBackendBindings.Release(handle, backend); }
    }

    [Test]
    public async Task FailedAdmissionReleasesItsOwnerAndFreshRetryGetsFreshStateAndKey()
    {
        using var fixture = new ManagedNpcAdmissionTests.Fixture();
        var npc = fixture.Npc(model: ActorModel); fixture.Publish(npc);
        var handle = fixture.Catalog.Register(npc); var backend = new object();
        ZoneAiBackendBindings.TryClaim(handle, backend);
        try
        {
            using var factory = new ZoneManagedNpcProfileFactory(fixture.Registry, fixture.Catalog, _ => Vector3.Zero);
            var first = factory.Create(handle, npc, 719);
            first.Lifetime!.Dispose();
            var retry = factory.Create(handle, npc, 720);
            await Assert.That(retry.ProxyAllocationKey).IsGreaterThan(first.ProxyAllocationKey);
            await Assert.That(ReferenceEquals(retry.Object, first.Object)).IsFalse();
            await Assert.That(ReferenceEquals(retry.Controls, first.Controls)).IsFalse();
            factory.Dispose();
            await Assert.That(((ZoneManagedNpcProxyOwner)retry.Lifetime!).IsDisposed).IsTrue();
            await Assert.That(factory.LiveCount).IsEqualTo(0);
            await Assert.That(factory.RetiredCount).IsEqualTo(2ul);
        }
        finally { ZoneAiBackendBindings.Release(handle, backend); }
    }
}
