using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;

namespace AAEmu.Zone.Tests;

public sealed class ZoneFrameTests
{
    [Test]
    public async Task JoinMatchesNativeGoldenFrame()
    {
        // 0x39368A00: p_from, p_to, id, ip, port, accountId, iid, dev.
        var join = new ZoneJoin(1203, 1203, 0x01020304, 0x05060708, 0x1234,
            0x0102030405060708, 0x10203040, true);
        var expected = Convert.FromHexString(
            "21000000B3040000B30400000403020108070605341208070605040302014030201001");
        await Assert.That(join.ToFrame().Encode().SequenceEqual(expected)).IsTrue();
        await Assert.That(ZoneJoin.Read(expected.AsSpan(4))).IsEqualTo(join);
    }

    [Test]
    public async Task HeartbeatAndLoadedHaveDifferentNativeBodies()
    {
        await Assert.That(new ZoneHeartbeat(true).ToFrame().Encode().SequenceEqual(new byte[] { 3, 0, 0x3C, 0, 1 })).IsTrue();
        await Assert.That(new ZoneFrame(ZwOpcodes.ZoneLoaded, ReadOnlyMemory<byte>.Empty).Encode()
            .SequenceEqual(new byte[] { 2, 0, 0x1F, 0 })).IsTrue();
        await Assert.That(() => ZoneHeartbeat.Read([])).Throws<InvalidDataException>();
        await Assert.That(() => ZoneHeartbeat.Read([1, 0])).Throws<InvalidDataException>();
    }

    [Test]
    public async Task JoinRejectsEveryTruncationAndTrailingBytes()
    {
        var body = new ZoneJoin(1203, 1203, 1, 0, 0, 0, 0, false).ToFrame().Body.ToArray();
        for (var length = 0; length < body.Length; length++)
        {
            var truncated = body[..length];
            await Assert.That(() => ZoneJoin.Read(truncated)).Throws<InvalidDataException>();
        }
        await Assert.That(() => ZoneJoin.Read([.. body, 0])).Throws<InvalidDataException>();
    }

    [Test]
    public async Task DecoderPreservesEveryTcpSplitAndPacketOrder()
    {
        var first = new ZoneJoin(1203, 1203, 1, 0, 0, 0, 0, false).ToFrame();
        var second = new ZoneFrame(ZwOpcodes.ZoneLoaded, ReadOnlyMemory<byte>.Empty);
        var third = new ZoneHeartbeat(false).ToFrame();
        var wire = first.Encode().Concat(second.Encode()).Concat(third.Encode()).ToArray();
        for (var split = 0; split <= wire.Length; split++)
        {
            var decoder = new ZoneFrameDecoder();
            var packets = new List<ZoneFrame>();
            decoder.Feed(wire.AsSpan(0, split), packets.Add);
            decoder.Feed(wire.AsSpan(split), packets.Add);
            decoder.Complete();
            await Assert.That(packets.Select(p => p.Opcode).SequenceEqual(new ushort[] { 0, 0x1F, 0x3C })).IsTrue();
            await Assert.That(packets[0].Body.Span.SequenceEqual(first.Body.Span)).IsTrue();
            await Assert.That(decoder.BufferedBytes).IsEqualTo(0);
        }
    }

    [Test]
    public async Task DecoderAcceptsMaximumFrameByteByByteAndRetainsBodyOwnership()
    {
        var body = Enumerable.Range(0, ZoneFrame.MaxBodySize).Select(i => (byte)i).ToArray();
        var wire = new ZoneFrame(0x1234, body).Encode();
        var decoder = new ZoneFrameDecoder();
        var packets = new List<ZoneFrame>();
        for (var i = 0; i < wire.Length; i++)
            decoder.Feed(wire.AsSpan(i, 1), packets.Add);
        decoder.Feed(new ZoneHeartbeat(true).ToFrame().Encode(), packets.Add);
        decoder.Complete();
        await Assert.That(packets.Count).IsEqualTo(2);
        await Assert.That(packets[0].Body.Span.SequenceEqual(body)).IsTrue();
        await Assert.That(packets[0].Opcode).IsEqualTo((ushort)0x1234);
    }

    [Test]
    public async Task InvalidFrameLengthsAndOversizedBodiesAreRejected()
    {
        await Assert.That(() => new ZoneFrameDecoder().Feed([0, 0], _ => { })).Throws<InvalidDataException>();
        await Assert.That(() => new ZoneFrameDecoder().Feed([1, 0], _ => { })).Throws<InvalidDataException>();
        await Assert.That(() => new ZoneFrame(1, new byte[ZoneFrame.MaxBodySize + 1]).Encode()).Throws<InvalidDataException>();
    }

    [Test]
    public async Task PartialFrameAtEofIsRejected()
    {
        var decoder = new ZoneFrameDecoder();
        decoder.Feed([2, 0, 0x1F], _ => { });
        await Assert.That(() => decoder.Complete()).Throws<EndOfStreamException>();
        using var stream = new MemoryStream([2, 0, 0x1F]);
        await Assert.That(async () => await ZoneFrame.ReadAsync(stream)).Throws<EndOfStreamException>();
    }

    [Test]
    public async Task StreamReaderDistinguishesCleanEofFromTruncation()
    {
        using var stream = new MemoryStream(new ZoneHeartbeat(true).ToFrame().Encode());
        var frame = await ZoneFrame.ReadAsync(stream);
        await Assert.That(frame.HasValue).IsTrue();
        await Assert.That(ZoneHeartbeat.Read(frame!.Value.Body.Span).RequestGameTime).IsTrue();
        await Assert.That(await ZoneFrame.ReadAsync(stream)).IsNull();
        using var invalid = new MemoryStream([1, 0]);
        await Assert.That(async () => await ZoneFrame.ReadAsync(invalid)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task BcRejectsOutOfRangeInsteadOfTruncatingObjectIdentity()
    {
        var writer = new ZoneWriter();
        writer.WriteBc(0x123456);
        await Assert.That(writer.ToArray().SequenceEqual(new byte[] { 0x56, 0x34, 0x12 })).IsTrue();
        await Assert.That(() => writer.WriteBc(0x1000000)).Throws<ArgumentOutOfRangeException>();
    }
}
