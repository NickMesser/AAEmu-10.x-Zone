using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AAEmu.Zone.Content;

namespace AAEmu.Zone.Tests;

public sealed class WaterQueryClassContentTests
{
    [Test]
    public async Task AllOriginalWaterRowsRetainTheirPayloadIdentityAndClassUpperBoundWithoutInventingBodyCounts()
    {
        using var fixture = Fixture(); using var owner = new ZoneCellWaterQueryClassContent();
        var count = 0; var lakes = 0; var rivers = 0; var emptyContours = 0;
        foreach (var file in fixture.RootElement.GetProperty("files").EnumerateArray())
        {
            using var compressed = new MemoryStream(Convert.FromBase64String(file.GetProperty("waterRowsGzipBase64").GetString()!));
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress); using var bytes = new MemoryStream(); gzip.CopyTo(bytes);
            var content = ZoneCellObjectContent.Read(bytes.ToArray(), ZoneCellObjectFileKind.BigObjects);
            var provenance = file.GetProperty("rows").EnumerateArray().ToArray();
            await Assert.That(content.Objects.Count).IsEqualTo(provenance.Length);
            foreach (var row in content.Objects.Cast<ZoneCellWaterVolumeObject>())
            {
                var certificate = owner.Classify(content, row);
                await Assert.That(certificate.Outcome).IsEqualTo(ZoneCellWaterQueryClassOutcome.CertifiedClassUpperBound);
                await Assert.That(certificate.PayloadSha256).IsEqualTo(provenance[row.SourceOrder].GetProperty("payloadSha256").GetString());
                await Assert.That(ReferenceEquals(certificate.SourceContent, content)).IsTrue();
                await Assert.That(ReferenceEquals(certificate.SourceRow, row)).IsTrue();
                await Assert.That(certificate.SimulationClass).IsEqualTo(5);
                await Assert.That(certificate.EntitySelectionClass).IsEqualTo(0x20u);
                await Assert.That(certificate.CanContributeTo(0x107)).IsFalse();
                await Assert.That(certificate.CanContributeTo(0x50006)).IsFalse();
                await Assert.That(certificate.CanContributeTo(0x20)).IsTrue();
                await Assert.That(certificate.CanContributeTo(0x127)).IsTrue();
                count++; if (row.VolumeType == 2) lakes++; else rivers++;
                if (row.PhysicsVertices.Count == 0) emptyContours++;
            }
        }
        await Assert.That(count).IsEqualTo(127);
        await Assert.That(lakes).IsEqualTo(19);
        await Assert.That(rivers).IsEqualTo(108);
        await Assert.That(emptyContours).IsGreaterThan(0);
    }

    [Test]
    public async Task CopiedNativeAreaConstructorAndWorldMaskInstructionsMatchTheTypedClassBound()
    {
        using var fixture = Fixture(); var native = fixture.RootElement.GetProperty("native");
        var content = Content(Water()); var row = (ZoneCellWaterVolumeObject)content.Objects[0];
        using var owner = new ZoneCellWaterQueryClassContent(); var result = owner.Classify(content, row);
        foreach (var control in native.GetProperty("controls").EnumerateArray())
        {
            await Assert.That(control.GetProperty("constructorSimulationClass").GetInt32()).IsEqualTo(5);
            await Assert.That(control.GetProperty("simulationClass").GetInt32()).IsEqualTo(5);
            await Assert.That(control.GetProperty("materialSetterPreservesArea").GetBoolean()).IsTrue();
            var creates = control.GetProperty("events").EnumerateArray().Count(value => value.GetString() == "AddArea");
            await Assert.That(creates).IsEqualTo(control.GetProperty("areaCreated").GetBoolean() ? 1 : 0);
        }
        foreach (var control in native.GetProperty("maskControls").EnumerateArray().Where(row => row.GetProperty("simulationClass").GetInt32() == 5))
            await Assert.That(result.CanContributeTo(control.GetProperty("mask").GetUInt32()))
                .IsEqualTo(control.GetProperty("nativeSelectedBits").GetUInt32() != 0);
        await Assert.That(native.GetProperty("engineModuleLoads").GetInt32()).IsEqualTo(0);
    }

    [Test]
    [Arguments(2)] [Arguments(3)]
    public async Task EmptyPhysicalContoursStillHaveAClassBoundRatherThanAPretendPhysicalInstance(int type)
    {
        var content = Content(Water((byte)type, physicsCount: 0));
        using var owner = new ZoneCellWaterQueryClassContent(); var result = owner.Classify(content, (ZoneCellWaterVolumeObject)content.Objects[0]);
        await Assert.That(result.HasClassUpperBound).IsTrue();
        await Assert.That(result.CanContributeTo(0x20)).IsTrue();
        await Assert.That(result.CanContributeTo(0x107)).IsFalse();
    }

    [Test]
    [Arguments(0)] [Arguments(1)] [Arguments(4)] [Arguments(255)]
    public async Task AnUnownedWaterFamilyNeverReturnsFalseForAnUnclassifiedSelectionMask(int type)
    {
        var content = Content(Water((byte)type)); using var owner = new ZoneCellWaterQueryClassContent();
        var result = owner.Classify(content, (ZoneCellWaterVolumeObject)content.Objects[0]);
        await Assert.That(result.Outcome).IsEqualTo(ZoneCellWaterQueryClassOutcome.Unsupported);
        await Assert.That(result.EntitySelectionClass).IsNull();
        await Assert.That(() => result.CanContributeTo(0)).Throws<NotSupportedException>();
        await Assert.That(() => result.CanContributeTo(0x107)).Throws<NotSupportedException>();
    }

    [Test]
    [Arguments(0)] [Arguments(0x47)] [Arguments(0x6b)] [Arguments(0x6f)] [Arguments(0x77)]
    public async Task MalformedNumericInputsCannotAcquireAClassCertificate(int offset)
    {
        var data = Water(); Float(data, offset, float.NaN); var content = Content(data); using var owner = new ZoneCellWaterQueryClassContent();
        var result = owner.Classify(content, (ZoneCellWaterVolumeObject)content.Objects[0]);
        await Assert.That(result.Outcome).IsEqualTo(ZoneCellWaterQueryClassOutcome.InvalidData);
        await Assert.That(() => result.CanContributeTo(0x107)).Throws<NotSupportedException>();
    }

    [Test]
    public async Task MaterialNullNamedAndEmptyFallbackBranchesDoNotChangeThePhysicalEntityClass()
    {
        using var owner = new ZoneCellWaterQueryClassContent();
        foreach (var (name, branch) in new[] { ((string?)null, ZoneCellWaterMaterialBranch.NegativeIndexUsesNull),
            ("materials/not-present-on-disk", ZoneCellWaterMaterialBranch.NamedTableReference), ("", ZoneCellWaterMaterialBranch.EmptyNameMayUseNativeFallback) })
        {
            var payload = Water(); BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0x33), name is null ? -1 : 0);
            var content = Content(payload, name); var result = owner.Classify(content, (ZoneCellWaterVolumeObject)content.Objects[0]);
            await Assert.That(result.MaterialBranch).IsEqualTo(branch);
            await Assert.That(result.HasClassUpperBound).IsTrue();
            await Assert.That(result.CanContributeTo(0x20)).IsTrue();
            await Assert.That(result.CanContributeTo(0x107)).IsFalse();
        }
    }

    [Test]
    public async Task UnsupportedMaterialMountReferencesRemainExplicitEvenThoughTheNormalMaterialSetterIsRenderOnly()
    {
        var payload = Water(); BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0x33), 0);
        var content = Content(payload, "../unowned"); using var owner = new ZoneCellWaterQueryClassContent();
        var result = owner.Classify(content, (ZoneCellWaterVolumeObject)content.Objects[0]);
        await Assert.That(result.Outcome).IsEqualTo(ZoneCellWaterQueryClassOutcome.Unsupported);
        await Assert.That(() => result.CanContributeTo(0x107)).Throws<NotSupportedException>();
    }

    [Test]
    public async Task MissingInputsForeignRowsAndInvalidSerializedReferencesRefuseBeforeClassSelection()
    {
        var first = Content(Water()); var second = Content(Water()); using var owner = new ZoneCellWaterQueryClassContent();
        var row = (ZoneCellWaterVolumeObject)first.Objects[0];
        await Assert.That(() => owner.Classify(null!, row)).Throws<ArgumentNullException>();
        await Assert.That(() => owner.Classify(first, null!)).Throws<ArgumentNullException>();
        await Assert.That(() => owner.Classify(second, row)).Throws<ArgumentException>();
        var data = Water(); BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0x33), 0);
        await Assert.That(() => Content(data)).Throws<InvalidDataException>();
        data = Water(); BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0x67), -1);
        await Assert.That(() => Content(data)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task RetirementRejectsNewClassificationsWhileCopiedSourceProofDoesNotClaimCurrentAreaLifetime()
    {
        var content = Content(Water()); var row = (ZoneCellWaterVolumeObject)content.Objects[0]; var owner = new ZoneCellWaterQueryClassContent();
        var result = owner.Classify(content, row);
        await Assert.That(ReferenceEquals(result, owner.Classify(content, row))).IsTrue(); owner.Dispose();
        await Assert.That(() => owner.Classify(content, row)).Throws<ObjectDisposedException>();
        await Assert.That(result.CanContributeTo(0x20)).IsTrue();
        await Assert.That(result.CanContributeTo(0x107)).IsFalse();
    }

    private static JsonDocument Fixture() => JsonDocument.Parse(typeof(WaterQueryClassContentTests).Assembly.GetManifestResourceStream(
        typeof(WaterQueryClassContentTests).Assembly.GetName().Name + ".Fixtures.water-query-class-controls.json")!);
    private static ZoneCellObjectContent Content(byte[] payload, string? material = null)
    {
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes, Encoding.UTF8, true);
        writer.Write(0); writer.Write(material is null ? 0 : 1);
        if (material is not null) { writer.Write(0); var name = new byte[256]; Encoding.ASCII.GetBytes(material).CopyTo(name, 0); writer.Write(name); }
        writer.Write(11); writer.Write(payload); return ZoneCellObjectContent.Read(bytes.ToArray(), ZoneCellObjectFileKind.BigObjects);
    }
    private static byte[] Water(byte type = 2, int physicsCount = 4)
    {
        var renderCount = type == 3 ? 4 : 3; var data = new byte[119 + 12 * (renderCount + physicsCount)];
        data[0x27] = type; Float(data, 12, 3); Float(data, 16, 3); Float(data, 20, 3);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0x33), -1); Float(data, 0x4f, 1); Float(data, 0x53, -1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0x67), renderCount); Float(data, 0x6b, 2);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0x73), physicsCount);
        for (var index = 0; index < renderCount + physicsCount; index++)
        { Float(data, 119 + 12 * index, index % 2); Float(data, 123 + 12 * index, index / 2); Float(data, 127 + 12 * index, 1); }
        return data;
    }
    private static void Float(byte[] data, int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset), value);
}
