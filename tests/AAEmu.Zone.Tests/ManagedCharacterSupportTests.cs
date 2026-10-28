using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Content;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Tests;

public sealed class ManagedCharacterSupportTests
{
    [Test]
    public async Task PerActorPivotsShiftTheEntityOriginWhileGroundHeightStaysOnTheSurface()
    {
        var first = new ZoneActorHandle(801, 1);
        var second = new ZoneActorHandle(802, 1);
        var ground = new PivotGround(new() { [first] = 1.25f, [second] = -.25f }, 12);
        var physics = new ZoneOwnedPlayerPhysics(ground);
        physics.AddActor(first, new(10, 20, 14.25f), Quaternion.Identity);
        physics.AddActor(second, new(30, 40, 12.75f), Quaternion.Identity);
        physics.Step(TimeSpan.FromSeconds(1));

        physics.TryReadPosition(first, out var firstPosition);
        physics.TryReadPosition(second, out var secondPosition);
        await Assert.That(firstPosition).IsEqualTo(new Vector3(10, 20, 13.25f));
        await Assert.That(secondPosition).IsEqualTo(new Vector3(30, 40, 11.75f));
        await Assert.That(physics.TryReadGroundHeight(first, out var height)).IsTrue();
        await Assert.That(height).IsEqualTo(12f);
        await Assert.That(physics.TryReadGroundMaterial(first, out var material)).IsTrue();
        await Assert.That(material).IsEqualTo((ushort)37);
        await Assert.That(physics.TryReadGrounded(second, out var grounded)).IsTrue();
        await Assert.That(grounded).IsTrue();
        await Assert.That(ground.PreviousSupportHeights).IsEquivalentTo(new[] { 13f, 13f });
        physics.Step(TimeSpan.FromMilliseconds(33));
        physics.TryReadPosition(first, out var resting);
        await Assert.That(resting).IsEqualTo(firstPosition);
    }

    [Test]
    public async Task EachOwnedActorIntegratesItsAuthoredGravityWithoutChangingTheLegacyDefault()
    {
        var physics = new ZoneOwnedPlayerPhysics(new EmptyGround());
        var slow = new ZoneActorHandle(803, 1);
        var fast = new ZoneActorHandle(804, 1);
        var legacy = new ZoneActorHandle(805, 1);
        physics.AddActor(slow, new(0, 0, 100), Quaternion.Identity, gravity: -3);
        physics.AddActor(fast, new(0, 0, 100), Quaternion.Identity, gravity: -12);
        physics.AddActor(legacy, new(0, 0, 100), Quaternion.Identity);
        physics.Step(TimeSpan.FromSeconds(.5));
        physics.TryReadPosition(slow, out var slowPosition);
        physics.TryReadPosition(fast, out var fastPosition);
        physics.TryReadVelocity(legacy, out var legacyVelocity);
        await Assert.That(slowPosition.Z).IsEqualTo(99.25f);
        await Assert.That(fastPosition.Z).IsEqualTo(97f);
        await Assert.That(legacyVelocity.Z).IsEqualTo(ZoneOwnedPlayerPhysics.Gravity * .5f);
        await Assert.That(() => physics.AddActor(new(806, 1), Vector3.Zero, Quaternion.Identity,
            float.NaN)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task InvalidGroundPivotsFailBeforeTheActorIsIntegrated()
    {
        var actor = new ZoneActorHandle(807, 1);
        var physics = new ZoneOwnedPlayerPhysics(new PivotGround(new() { [actor] = float.NaN }, 0));
        var position = new Vector3(1, 2, 3);
        physics.AddActor(actor, position, Quaternion.Identity);
        await Assert.That(() => physics.Step(TimeSpan.FromSeconds(.5))).Throws<InvalidOperationException>();
        physics.TryReadPosition(actor, out var unchanged);
        physics.TryReadVelocity(actor, out var velocity);
        await Assert.That(unchanged).IsEqualTo(position);
        await Assert.That(velocity).IsEqualTo(Vector3.Zero);
    }

    [Test]
    public async Task AuthoredJumpSpeedAndGravityReachTheAuthoredHeightAboveThePivot()
    {
        var content = new ZoneActorModelContent(
            [new() { Id = 1, PhysicsLivingMass = 80, PhysicsLivingGravity = 25, GameJumpHeight = 1 }],
            [new() { Id = 1, ActorModelId = 1, StanceId = 2, UseCapsule = true,
                SizeX = .4f, SizeY = .4f, SizeZ = .32f, HeightCollider = 1.2f, HeightPivot = .15f }]);
        var model = content.Models[1];
        var initial = ZoneActorPhysicalizationRequest.BuildInitial(model, 1, false, _ => null);
        var description = ZonePhysicsActorDescription.FromLivingStance(initial.Stance,
            initial.Dynamics.ReadSingle(0x28)!.Value, -initial.Dynamics.ReadSingle(0x1C)!.Value);
        var terrain = new ZoneTerrainHeightField();
        terrain.LoadCell(0, 0, TerrainHeightFieldTests.CreateCell(0, 0));
        var backend = new ZoneManagedPhysicsBackend(new(terrain));
        var actor = new ZoneActorHandle(808, 1);
        backend.AddActor(actor, description);
        backend.Teleport(actor, new(20, 30, .65f), Quaternion.Identity);
        backend.Step(TimeSpan.FromSeconds(1));
        backend.CopyStatus(actor, out var standing);
        await Assert.That(standing.IsGrounded).IsTrue();
        await Assert.That(MathF.Abs(standing.Position.Z - .15f)).IsLessThan(.001f);

        backend.ApplyImpulse(actor, new(0, 0, ZoneMovementInputLimits.FromActorModel(model).JumpSpeed));
        var maximum = standing.Position.Z;
        for (var frame = 0; frame < 600; frame++)
        {
            backend.Step(TimeSpan.FromMilliseconds(1));
            backend.CopyStatus(actor, out var status);
            maximum = MathF.Max(maximum, status.Position.Z);
        }
        // Semi-implicit integration's 1ms step loses <6mm against h = v²/(2g) = 1m.
        await Assert.That(MathF.Abs(maximum - standing.Position.Z - 1f)).IsLessThan(.006f);
        backend.CopyStatus(actor, out var landed);
        await Assert.That(landed.IsGrounded).IsTrue();
        await Assert.That(MathF.Abs(landed.Position.Z - .15f)).IsLessThan(.001f);
    }

    private sealed class PivotGround(Dictionary<ZoneActorHandle, float> pivots, float height)
        : ZoneOwnedPlayerPhysics.IGroundQuery
    {
        public List<float> PreviousSupportHeights { get; } = [];
        public float GetGroundPivotHeight(ZoneActorHandle actor) => pivots[actor];
        public bool TryGround(Vector3 from, Vector3 to, out Vector3 position, out ushort material)
        { position = new(to.X, to.Y, height); material = 37; return true; }
        public bool TryCharacterGround(Vector3 previous, Vector3 next, bool grounded,
            out Vector3 position, out ushort material)
        {
            PreviousSupportHeights.Add(previous.Z);
            return TryGround(previous, next, out position, out material);
        }
    }

    private sealed class EmptyGround : ZoneOwnedPlayerPhysics.IGroundQuery
    {
        public bool TryGround(Vector3 from, Vector3 to, out Vector3 position, out ushort material)
        { position = default; material = 0; return false; }
    }
}
