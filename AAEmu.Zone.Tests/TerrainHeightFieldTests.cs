using System.Buffers.Binary;
using AAEmu.Zone.Content;
using AAEmu.Zone.Simulation;

namespace AAEmu.Zone.Tests;

public sealed class TerrainHeightFieldTests
{
    [Test]
    public async Task ContinuousHeightUsesTheNativeTriangleDiagonal()
    {
        var content = CreateCell(100, 1, sample: (x, y, sx, sy) =>
            (ushort)(x == 64 && y == 64 ? (sx, sy) switch
            {
                (0, 0) => 32,
                (1, 0) => 64,
                (0, 1) => 96,
                (1, 1) => 640,
                _ => 32
            } : 32));
        var terrain = new ZoneTerrainHeightField();
        terrain.LoadCell(1, 1, content);
        await Assert.That(terrain.GetHeight(1088.5f, 1088.5f)).IsEqualTo(156f);
        await Assert.That(terrain.GetHeight(1089.5f, 1089.5f)).IsEqualTo(460f);
        await Assert.That(terrain.GetHeight(1089, 1089)).IsEqualTo(180f);
        await Assert.That(terrain.GetHeight(1024, 1024)).IsEqualTo(132f);
    }

    [Test]
    public async Task ReducedResolutionCornersMatchExecutedNativeFloatBits()
    {
        ushort[] samples = [7412, 12004, 11124, 47324];
        var content = CreateCell(BitConverter.UInt32BitsToSingle(1132183552),
            BitConverter.UInt32BitsToSingle(987016471), width: 2,
            sample: (x, y, sx, sy) => x is 0 or 960 || y is 0 or 960 ? (ushort)32 : samples[sx * 2 + sy]);
        var terrain = new ZoneTerrainHeightField();
        terrain.LoadCell(1, 1, content);
        // 30136B60 ->300D4E00 ->300D4670; fractions cross both triangle halves and coarse edges.
        (float X, float Y, uint HeightBits)[] expected =
        [
            (1088, 1088, 1132715842),
            (1088.25f, 1088.5f, 1132718526),
            (1089, 1089, 1132722754),
            (1089.5f, 1089.5f, 1132727029)
        ];
        foreach (var (x, y, heightBits) in expected)
            await Assert.That(BitConverter.SingleToUInt32Bits(terrain.GetHeight(x, y))).IsEqualTo(heightBits);
    }

    [Test]
    public async Task ZeroCornerRejectsEvenTheUnusedTriangleAndNegativeHeightClamps()
    {
        var terrain = new ZoneTerrainHeightField();
        terrain.LoadCell(1, 1, CreateCell(0, 1, sample: (x, y, sx, sy) =>
            x == 64 && y == 64 && sx == 1 && sy == 1 ? (ushort)0 : (ushort)32));
        await Assert.That(terrain.GetHeight(1088.5f, 1088.5f)).IsEqualTo(0f);
        await Assert.That(terrain.GetHeight(1092.5f, 1092.5f)).IsEqualTo(32f);
        terrain.LoadCell(1, 1, CreateCell(-100, 0));
        await Assert.That(terrain.GetHeight(1088.5f, 1088.5f)).IsEqualTo(0f);
        terrain.LoadCell(1, 1, CreateCell(float.NaN, 0));
        await Assert.That(float.IsNaN(terrain.GetHeight(1088.5f, 1088.5f))).IsTrue();
    }

    [Test]
    public async Task SeamCopiesUseDestinationQuantizationAndDoNotModifyAuthoredOrSiblingCells()
    {
        var low = CreateCell(100, 1);
        var high = CreateCell(300, 1);
        var terrain = new ZoneTerrainHeightField();
        terrain.LoadCell(1, 1, low);
        await Assert.That(terrain.GetHeight(2047, 1056)).IsEqualTo(132f);
        terrain.LoadCell(2, 1, high);
        // Native (332 - 100) truncates to 232, clears five height bits to224, then decodes as324.
        await Assert.That(terrain.GetHeight(2047, 1056)).IsEqualTo(228f);
        await Assert.That(terrain.GetHeight(2048, 1056)).IsEqualTo(332f);
        await Assert.That(low.LeafNodes.Single(node => node.LocalX == 960 && node.LocalY == 0)
            .GetSampleHeight(32, 16)).IsEqualTo(132f);
        terrain.LoadCell(3, 3, low);
        await Assert.That(terrain.GetHeight(4095, 3104)).IsEqualTo(132f);

        var oppositeOrder = new ZoneTerrainHeightField();
        oppositeOrder.LoadCell(2, 1, high);
        oppositeOrder.LoadCell(1, 1, low);
        await Assert.That(oppositeOrder.GetHeight(2047, 1056)).IsEqualTo(228f);
        var vertical = new ZoneTerrainHeightField();
        vertical.LoadCell(1, 2, high);
        vertical.LoadCell(1, 1, low);
        await Assert.That(vertical.GetHeight(1056, 2047)).IsEqualTo(228f);
    }

    [Test]
    public async Task RetirementKeepsCopiedNeighborEdgesAndReloadReplacesOnlyItsOwnedSamples()
    {
        var low = CreateCell(100, 1);
        var terrain = new ZoneTerrainHeightField();
        terrain.LoadCell(1, 1, low);
        terrain.LoadCell(2, 1, CreateCell(300, 1));
        terrain.LoadCell(1, 1, EmptyCell());
        await Assert.That(terrain.GetHeight(2047, 1056)).IsEqualTo(228f);
        await Assert.That(terrain.UnloadCell(2, 1)).IsTrue();
        await Assert.That(terrain.UnloadCell(2, 1)).IsFalse();
        await Assert.That(terrain.GetHeight(2048, 1056)).IsEqualTo(0f);
        await Assert.That(terrain.GetHeight(2047, 1056)).IsEqualTo(228f);
        terrain.LoadCell(1, 1, low);
        await Assert.That(terrain.GetHeight(2047, 1056)).IsEqualTo(132f);
    }

    [Test]
    public async Task EngineBoundariesAndMissingCellsUseTheNativeZeroResult()
    {
        var terrain = new ZoneTerrainHeightField();
        var content = CreateCell(100, 1);
        terrain.LoadCell(0, 0, content);
        terrain.LoadCell(7, 7, content);
        await Assert.That(terrain.GetHeight(2, 2)).IsEqualTo(132f);
        await Assert.That(terrain.GetHeight(8191.999f, 8191.999f)).IsEqualTo(132f);
        foreach (var coordinate in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -1, 0, 1.999f, 8192 })
        {
            await Assert.That(terrain.GetHeight(coordinate, 10)).IsEqualTo(0f);
            await Assert.That(terrain.GetHeight(10, coordinate)).IsEqualTo(0f);
        }
        await Assert.That(terrain.GetHeight(2048, 2048)).IsEqualTo(0f);
    }

    [Test]
    public async Task UnsupportedSeamStorageFailsBeforeReplacingTheLoadedCell()
    {
        var terrain = new ZoneTerrainHeightField();
        terrain.LoadCell(1, 1, CreateCell(100, 1));
        var invalidBorders = CreateCell(300, 1, width: 2, fullBorders: false);
        await Assert.That(() => terrain.LoadCell(1, 1, invalidBorders)).Throws<InvalidDataException>();
        await Assert.That(terrain.GetHeight(1024, 1024)).IsEqualTo(132f);
        await Assert.That(() => terrain.LoadCell(8, 1, invalidBorders)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => terrain.UnloadCell(-1, 1)).Throws<ArgumentOutOfRangeException>();
    }

    internal static ZoneTerrainHeightmapContent EmptyCell()
    {
        var bytes = new byte[160];
        uint[] header = [24, 0, 4096, 2, 64, 128];
        for (var i = 0; i < header.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), header[i]);
        return ZoneTerrainHeightmapContent.Read(bytes);
    }

    internal static ZoneTerrainHeightmapContent CreateCell(float offset, float range, int width = 33,
        Func<int, int, int, int, ushort>? sample = null, bool fullBorders = true, float[]? lodErrors = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach (var value in new uint[] { 24, 0, 4096, 2, 64, 128 }) writer.Write(value);
        writer.Write(.0625f);
        writer.Write(100f);
        writer.Write(new byte[128]);
        WriteNode(0, 0, 1024);
        var bytes = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)bytes.Length);
        return ZoneTerrainHeightmapContent.Read(bytes);

        void WriteNode(int x, int y, int size)
        {
            writer.Write(5u);
            foreach (var value in new float[] { x, y, 0, x + size, y + size, 4096 }) writer.Write(value);
            writer.Write((byte)1); // The height API does not suppress a node for its hole flag.
            writer.Write(offset);
            writer.Write(range);
            var samples = size != 64 ? 0 : fullBorders && (x is 0 or 960 || y is 0 or 960) ? 33 : width;
            writer.Write(samples);
            writer.Write(0);
            for (var sx = 0; sx < samples; sx++)
                for (var sy = 0; sy < samples; sy++) writer.Write(sample?.Invoke(x, y, sx, sy) ?? (ushort)32);
            if (lodErrors is null) writer.Write(new byte[20]);
            else foreach (var error in lodErrors) writer.Write(error);
            writer.Write(new byte[36]);
            if (size == 64) return;
            var half = size / 2;
            WriteNode(x, y, half);
            WriteNode(x + half, y, half);
            WriteNode(x, y + half, half);
            WriteNode(x + half, y + half, half);
        }
    }
}
