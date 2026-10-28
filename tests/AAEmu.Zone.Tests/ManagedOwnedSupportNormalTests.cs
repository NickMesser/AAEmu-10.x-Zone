using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Physics;

namespace AAEmu.Zone.Tests;

public sealed class ManagedOwnedSupportNormalTests
{
    [Test]
    public async Task SupportNormalsAreCopiedAndDisappearAfterTeleportOrSupportLoss()
    {
        var normal = Vector3.Normalize(new Vector3(-.5f, 0, 1));
        var query = new SupportQuery { Support = new(Vector3.Zero, normal, 29) };
        var physics = new ZoneOwnedPlayerPhysics(query);
        var actor = new ZoneActorHandle(1301, 1);
        physics.AddActor(actor, Vector3.UnitZ, Quaternion.Identity, gravity: -4);
        physics.Step(TimeSpan.FromSeconds(.5));
        await Assert.That(physics.TryReadGroundNormal(actor, out var copied)).IsTrue();
        await Assert.That(copied).IsEqualTo(normal);
        await Assert.That(physics.TryReadGroundMaterial(actor, out var material)).IsTrue();
        await Assert.That(material).IsEqualTo((ushort)29);

        physics.Teleport(actor, new(0, 0, 5), Quaternion.Identity);
        await Assert.That(physics.TryReadGroundNormal(actor, out _)).IsFalse();
        query.Available = false;
        physics.Step(TimeSpan.FromMilliseconds(10));
        await Assert.That(physics.TryReadGroundNormal(actor, out _)).IsFalse();
        await Assert.That(copied).IsEqualTo(normal);
    }

    [Test]
    public async Task InvalidSupportCannotPartiallyApplyGravityOrReplaceTheValidatedContact()
    {
        var query = new SupportQuery { Support = new(Vector3.Zero, Vector3.UnitZ, 17) };
        var physics = new ZoneOwnedPlayerPhysics(query);
        var actor = new ZoneActorHandle(1302, 1);
        physics.AddActor(actor, Vector3.Zero, Quaternion.Identity, gravity: -10);
        physics.Step(TimeSpan.FromMilliseconds(10));
        physics.ApplyVelocity(actor, new(1, 2, 0));
        physics.TryReadPosition(actor, out var before);
        query.Support = new(new(0, 0, 10), Vector3.Zero, 99);
        await Assert.That(() => physics.Step(TimeSpan.FromMilliseconds(100))).Throws<InvalidOperationException>();
        physics.TryReadPosition(actor, out var after);
        physics.TryReadVelocity(actor, out var velocity);
        await Assert.That(after).IsEqualTo(before);
        await Assert.That(velocity).IsEqualTo(new Vector3(1, 2, 0));
        await Assert.That(physics.TryReadGroundNormal(actor, out var normal)).IsTrue();
        await Assert.That(normal).IsEqualTo(Vector3.UnitZ);
        await Assert.That(physics.TryReadGroundMaterial(actor, out var material)).IsTrue();
        await Assert.That(material).IsEqualTo((ushort)17);
    }

    [Test]
    public async Task LegacyHeightOnlySupportDoesNotInventAFlatNormal()
    {
        var physics = new ZoneOwnedPlayerPhysics(new HeightOnlyQuery());
        var actor = new ZoneActorHandle(1303, 1);
        physics.AddActor(actor, Vector3.Zero, Quaternion.Identity);
        physics.Step(TimeSpan.FromMilliseconds(10));
        await Assert.That(physics.TryReadGrounded(actor, out var grounded)).IsTrue();
        await Assert.That(grounded).IsTrue();
        await Assert.That(physics.TryReadGroundNormal(actor, out _)).IsFalse();
    }

    [Test]
    public async Task AProviderCannotCommitSupportToAReplacementBodyOrStepANewBodyInTheSameFrame()
    {
        var query = new SupportQuery { Support = new(Vector3.Zero, Vector3.UnitZ, 17) };
        var physics = new ZoneOwnedPlayerPhysics(query);
        var actor = new ZoneActorHandle(1304, 1);
        var added = new ZoneActorHandle(1305, 1);
        physics.AddActor(actor, Vector3.Zero, Quaternion.Identity, gravity: 0);
        var replacement = new Vector3(10, 20, 30);
        query.OnQuery = () =>
        {
            physics.RemoveActor(actor);
            physics.AddActor(actor, replacement, Quaternion.Identity, gravity: 0);
            physics.AddActor(added, replacement, Quaternion.Identity, gravity: 0);
            query.OnQuery = null;
        };
        await Assert.That(physics.Step(TimeSpan.FromMilliseconds(20))).IsEqualTo(0);
        physics.TryReadPosition(actor, out var position);
        await Assert.That(position).IsEqualTo(replacement);
        await Assert.That(physics.TryReadGroundMaterial(actor, out _)).IsFalse();
        await Assert.That(physics.TryReadGroundMaterial(added, out _)).IsFalse();
        await Assert.That(physics.Step(TimeSpan.FromMilliseconds(20))).IsEqualTo(2);
    }

    [Test]
    public async Task AProviderCannotReenterTheStepOrOverwriteItsStagedMotion()
    {
        var query = new SupportQuery { Support = new(Vector3.Zero, Vector3.UnitZ, 17) };
        var physics = new ZoneOwnedPlayerPhysics(query);
        var actor = new ZoneActorHandle(1306, 1);
        physics.AddActor(actor, Vector3.Zero, Quaternion.Identity, gravity: 0);
        query.OnQuery = () => physics.ApplyVelocity(actor, Vector3.UnitX);
        await Assert.That(() => physics.Step(TimeSpan.FromMilliseconds(20))).Throws<InvalidOperationException>();
        physics.TryReadVelocity(actor, out var velocity);
        await Assert.That(velocity).IsEqualTo(Vector3.Zero);
        query.OnQuery = () => physics.Step(TimeSpan.FromMilliseconds(20));
        await Assert.That(() => physics.Step(TimeSpan.FromMilliseconds(20))).Throws<InvalidOperationException>();
        query.OnQuery = null;
        await Assert.That(physics.Step(TimeSpan.FromMilliseconds(20))).IsEqualTo(1);
    }

    private sealed class SupportQuery : ZoneOwnedPlayerPhysics.IGroundQuery
    {
        public ZonePhysicsCharacterSupport Support { get; set; }
        public bool Available { get; set; } = true;
        public Action? OnQuery { get; set; }
        public bool TryGround(Vector3 from, Vector3 to, out Vector3 position, out ushort material)
        { position = default; material = 0; return false; }
        public bool TryCharacterSupport(ZoneActorHandle actor, Vector3 previous, Vector3 next, bool grounded,
            out ZonePhysicsCharacterSupport support)
        { OnQuery?.Invoke(); support = Support; return Available; }
    }

    private sealed class HeightOnlyQuery : ZoneOwnedPlayerPhysics.IGroundQuery
    {
        public bool TryGround(Vector3 from, Vector3 to, out Vector3 position, out ushort material)
        { position = Vector3.Zero; material = 7; return true; }
    }
}
