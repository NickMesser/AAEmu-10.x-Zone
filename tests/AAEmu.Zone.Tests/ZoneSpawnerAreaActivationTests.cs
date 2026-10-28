using System.Numerics;
using AAEmu.Zone.Npcs;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.WorldLink;

namespace AAEmu.Zone.Tests;

public sealed class ZoneSpawnerAreaActivationTests
{
    // Real native-baseline bodies (WZ 0x0042 x14, all identical; WZ 0x0036 x5).
    private static readonly byte[] AreaBody = [0x00, 0x00, 0xd0, 0x44, 0x00, 0xb0, 0x83, 0x44, 0xcd, 0x4c, 0xb2, 0x43, 0xd5, 0x22, 0x31, 0x45, 0x01];
    private static readonly byte[] FlyingBody = [0x0b, 0x02, 0x00, 0x01];

    [Test]
    public async Task ParsesRealAreaBody()
    {
        var message = ActivateNpcSpawnersInArea.Read(AreaBody);
        await Assert.That(message.Center.X).IsEqualTo(1664f);
        await Assert.That(message.Center.Y).IsEqualTo(1053.5f);
        await Assert.That(message.Radius).IsEqualTo(BitConverter.ToSingle(AreaBody, 12));
        await Assert.That(message.Activate).IsTrue();
        await Assert.That(message.ToFrame().Body.ToArray()).IsEquivalentTo(AreaBody);
        await Assert.That(message.ToFrame().Opcode).IsEqualTo(WzOpcodes.ActivateNpcSpawnersInArea);
        await Assert.That(() => ActivateNpcSpawnersInArea.Read(AreaBody[..16])).Throws<Exception>();
    }

    [Test]
    public async Task ParsesRealFlyingBody()
    {
        var message = UnitFlyingStateChanged.Read(FlyingBody);
        await Assert.That(message.ObjectId).IsEqualTo(523u);
        await Assert.That(message.Flying).IsTrue();
        await Assert.That(message.ToFrame().Body.ToArray()).IsEquivalentTo(FlyingBody);
        await Assert.That(() => UnitFlyingStateChanged.Read([1, 2, 3])).Throws<Exception>();
    }

    [Test]
    public async Task AreaTestIsPlanarAndStrict()
    {
        var center = new Vector3(100, 100, 0);
        // Z is ignored; exactly on the circle is outside (native continues while r^2 <= d^2).
        await Assert.That(ZoneNpcSpawnerFactory.AnyPointInsideArea([new(100, 150, 9999)], center, 100)).IsTrue();
        await Assert.That(ZoneNpcSpawnerFactory.AnyPointInsideArea([new(200, 100, 0)], center, 100)).IsFalse();
        await Assert.That(ZoneNpcSpawnerFactory.AnyPointInsideArea([new(1000, 1000, 0), new(101, 101, 0)], center, 100)).IsTrue();
        await Assert.That(ZoneNpcSpawnerFactory.AnyPointInsideArea([], center, 100)).IsFalse();
    }

    [Test]
    public async Task SessionWithoutScenePacketsAreAbsorbed()
    {
        var session = ZoneSession.CreateDetached(new MemoryStream());
        session.ReceiveForTest(new ZoneFrame(WzOpcodes.ActivateNpcSpawnersInArea, AreaBody));
        // Unknown unit: ignored, neither a fault nor an unimplemented-opcode count.
        session.ReceiveForTest(new ZoneFrame(WzOpcodes.UnitFlyingStateChanged, FlyingBody));
        await Assert.That(session.IsClosed).IsFalse();
        await Assert.That(session.UnimplementedOpcodes.ContainsKey(WzOpcodes.ActivateNpcSpawnersInArea)).IsFalse();
        await Assert.That(session.UnimplementedOpcodes.ContainsKey(WzOpcodes.UnitFlyingStateChanged)).IsFalse();
        await Assert.That(session.HandlerFaults.ContainsKey(WzOpcodes.UnitFlyingStateChanged)).IsFalse();
    }
}
