using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.WorldLink;
using AAEmu.Zone.WorldState;

namespace AAEmu.Zone.Tests;

public sealed class ZoneDoodadTests
{
    // Real WZ 0x0073 bodies (115 bytes) from D:\aaemu-runtime\captures\native-baseline.bin, zone 129.
    private const string CreateA =
        "1b630201400100000000000000000000fffbeadf00ae429500fb620500000000270b0000803f000000000000000000000000000000006b000000" +
        "d4370e00000000000000000000000000ffffffffff0000000000000000000000000d20e16a0000000000000000000000000000000000000000";
    private const string CreateB =
        "22630201890100000000000000000000ff66d54a00b0627400e7bc050000000000003333333f00000000000000000000000000000000960b0000" +
        "58e05900000000000000000000000000ffffffffff0000000000000000000000000d20e16a0000000000000000000000000000000000000000";
    // Real WZ 0x0075 body: objId 0x01B480 -> func group 0x0BE2.
    private const string Phase = "80b401e20b0000";

    private static byte[] Bytes(string hex) => Convert.FromHexString(hex);

    private static ZoneSession NewSession() => ZoneSession.CreateDetached(new MemoryStream());

    [Test]
    public async Task CreateDecodesRealNativeBaselineBody()
    {
        var a = DoodadCreate.Read(Bytes(CreateA));
        await Assert.That(a.ObjectId).IsEqualTo(0x02631Bu);
        await Assert.That(a.TemplateId).IsEqualTo(0x0140u);
        await Assert.That(a.ModelId).IsEqualTo(0u);
        await Assert.That(a.ParentObjectId).IsEqualTo(0u);
        await Assert.That(a.AttachPoint).IsEqualTo((byte)0xFF);
        await Assert.That(a.Position.X).IsEqualTo(28661.49f).Within(0.01f);
        await Assert.That(a.Position.Y).IsEqualTo(19105.34f).Within(0.01f);
        await Assert.That(a.Position.Z).IsEqualTo(253.16f).Within(0.01f);
        await Assert.That(a.Yaw).IsEqualTo((short)0x0B27);
        await Assert.That(a.Scale).IsEqualTo(1f);
        await Assert.That(a.FuncGroupId).IsEqualTo(107u);
        await Assert.That(a.PuzzleGroup).IsEqualTo(-1);
        await Assert.That(a.OwnerType).IsEqualTo((byte)0xFF);
        await Assert.That(a.UpdatedTime).IsEqualTo(0x6AE1200DUL);
        await Assert.That(a.Freshness).IsNull();

        var b = DoodadCreate.Read(Bytes(CreateB));
        await Assert.That(b.ObjectId).IsEqualTo(0x026322u);
        await Assert.That(b.TemplateId).IsEqualTo(0x0189u);
        await Assert.That(b.Scale).IsEqualTo(0.7f).Within(0.0001f);
        await Assert.That(b.FuncGroupId).IsEqualTo(2966u);
        await Assert.That(b.Position.X).IsEqualTo(9578.70f).Within(0.01f);
    }

    [Test]
    public async Task CreateWithGoodsFreshnessBlockIsAccepted()
    {
        var plain = Bytes(CreateA);
        var body = plain[..^16].Concat(new byte[18]).Concat(plain[^16..]).ToArray();
        body[^34] = 0x2A; // freshness time low byte
        var parsed = DoodadCreate.Read(body);
        await Assert.That(parsed.Freshness).IsNotNull();
        await Assert.That(parsed.Freshness!.Value.FreshnessTime).IsEqualTo(0x2AL);
    }

    [Test]
    public async Task ShortAndTrailingBodiesAreRejected()
    {
        var plain = Bytes(CreateA);
        await Assert.That(() => DoodadCreate.Read(plain[..^1])).Throws<InvalidDataException>();
        await Assert.That(() => DoodadCreate.Read([.. plain, 0])).Throws<InvalidDataException>();
        await Assert.That(() => DoodadChangePhase.Read(Bytes(Phase)[..^1])).Throws<InvalidDataException>();
        await Assert.That(() => DoodadChangePhase.Read([.. Bytes(Phase), 0])).Throws<InvalidDataException>();
        await Assert.That(() => DoodadRemove.Read([1, 2, 3, 4])).Throws<InvalidDataException>();
        await Assert.That(() => VegetationCutdown.Read([1, 2])).Throws<InvalidDataException>();
    }

    [Test]
    public async Task SmallMessagesRoundTrip()
    {
        var phase = DoodadChangePhase.Read(Bytes(Phase));
        await Assert.That(phase).IsEqualTo(new DoodadChangePhase(0x01B480, 0x0BE2));
        await Assert.That(phase.ToFrame().Body.ToArray()).IsEquivalentTo(Bytes(Phase));
        await Assert.That(DoodadRemove.Read(new DoodadRemove(0x123456).ToFrame().Body.Span).ObjectId).IsEqualTo(0x123456u);
        await Assert.That(VegetationCutdown.Read(new VegetationCutdown(77).ToFrame().Body.Span).Id).IsEqualTo(77u);
    }

    [Test]
    public async Task RegistryCreatesChangesPhaseAndRemoves()
    {
        var registry = new ZoneDoodadRegistry();
        registry.Create(Bytes(CreateA));
        registry.Create(Bytes(CreateB));
        await Assert.That(registry.Doodads.Count).IsEqualTo(2);
        registry.Create(Bytes(CreateA));
        await Assert.That(registry.Doodads.Count).IsEqualTo(2);
        await Assert.That(registry.Replaced).IsEqualTo(1);

        registry.ChangePhase(new DoodadChangePhase(0x02631B, 555).ToFrame().Body.Span);
        await Assert.That(registry.Doodads[0x02631B].FuncGroupId).IsEqualTo(555u);
        await Assert.That(registry.Doodads[0x026322].FuncGroupId).IsEqualTo(2966u);
        registry.ChangePhase(Bytes(Phase));
        await Assert.That(registry.UnknownPhaseChanges).IsEqualTo(1);

        registry.Remove(new DoodadRemove(0x02631B).ToFrame().Body.Span);
        await Assert.That(registry.Doodads.ContainsKey(0x02631B)).IsFalse();
        registry.Remove(new DoodadRemove(0x02631B).ToFrame().Body.Span);
        await Assert.That(registry.UnknownRemoves).IsEqualTo(1);

        registry.CutDownVegetation(new VegetationCutdown(9).ToFrame().Body.Span);
        await Assert.That(registry.CutVegetation.Contains(9)).IsTrue();
    }

    [Test]
    public async Task SessionDispatchesDoodadFramesWithoutCountingThemUnimplemented()
    {
        var session = NewSession();
        session.ReceiveForTest(new ZoneFrame(WzOpcodes.CreateDoodad, Bytes(CreateA)));
        session.ReceiveForTest(new ZoneFrame(WzOpcodes.DoodadChangePhase, new DoodadChangePhase(0x02631B, 4).ToFrame().Body));
        session.ReceiveForTest(new ZoneFrame(WzOpcodes.VegetationCutdown, new VegetationCutdown(5).ToFrame().Body));
        session.ReceiveForTest(new ZoneFrame(WzOpcodes.RemoveDoodad, new DoodadRemove(0x999).ToFrame().Body));
        await Assert.That(session.Doodads.Doodads[0x02631B].FuncGroupId).IsEqualTo(4u);
        await Assert.That(session.Doodads.UnknownRemoves).IsEqualTo(1);
        await Assert.That(session.Doodads.CutVegetation.Contains(5)).IsTrue();
        await Assert.That(session.UnimplementedOpcodes.ContainsKey(WzOpcodes.CreateDoodad)).IsFalse();
        session.ReceiveForTest(new ZoneFrame(WzOpcodes.CreateDoodad, new byte[] { 1, 2, 3 }));
        await Assert.That(session.HandlerFaults[WzOpcodes.CreateDoodad]).IsEqualTo(1);
        await Assert.That(session.IsClosed).IsFalse();
    }
}
