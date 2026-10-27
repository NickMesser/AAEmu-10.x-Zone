using AAEmu.Zone.Protocol;

namespace AAEmu.Zone.Tests;

public sealed class PackedIntegerTests
{
    [Test]
    public async Task NativeGoldenWidthsAndGroupBoundaryArePreserved()
    {
        // Width header E4 => 1,2,3,4 bytes; next partial group starts with its own header.
        byte[] golden = [0xE4, 0x7F, 0, 1, 0, 0, 1, 0xFF, 0xFF, 0xFF, 0xFF, 0, 42];
        uint[] expected = [127, 256, 65536, uint.MaxValue, 42];
        var values = Read(golden, 5);
        await Assert.That(values.SequenceEqual(expected)).IsTrue();
        var writer = new ZoneWriter();
        writer.WritePisc(expected);
        await Assert.That(writer.ToArray().SequenceEqual(golden)).IsTrue();
        for (var length = 0; length < golden.Length; length++)
        {
            var prefix = golden[..length];
            await Assert.That(() => Read(prefix, 5)).Throws<InvalidDataException>();
        }
    }

    [Test]
    public async Task EmptyGroupsConsumeNothingAndHostileCountsAreRejectedBeforeAllocation()
    {
        await Assert.That(Read([], 0).Length).IsEqualTo(0);
        await Assert.That(() => Read([], int.MaxValue)).Throws<InvalidDataException>();
        await Assert.That(() => Read([], -1)).Throws<InvalidDataException>();
        var writer = new ZoneWriter();
        writer.WritePisc([]);
        await Assert.That(writer.ToArray().Length).IsEqualTo(0);
    }

    private static uint[] Read(byte[] bytes, int count)
    {
        var reader = new ZoneReader(bytes);
        var values = reader.ReadPisc(count);
        reader.EnsureFullyConsumed();
        return values;
    }
}
