using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AAEmu.Zone.Content;

namespace AAEmu.Zone.Tests;

public sealed class CellNonBrushCollisionContentTests
{
    [Test]
    public async Task OriginalCellAccountsForAllRowsAndCertifiesOnlyItsTenRoadsAndNinetySevenDecals()
    {
        AssertNoEngineModules();
        using var fixture = Fixture(); var root = fixture.RootElement;
        var bytes = OriginalObjects(root); using var source = OriginalMaterials(root);
        var objects = ZoneCellObjectContent.Read(bytes, ZoneCellObjectFileKind.Octree);
        using var owner = new ZoneCellNonBrushCollisionContent(source);
        var results = objects.Objects.Select(row => owner.Classify(objects, row, 0, 0)).ToArray();
        await Assert.That(objects.Objects.Count).IsEqualTo(1651);
        await Assert.That(results.Count(result => result.HasNoPhysicalContribution)).IsEqualTo(107);
        await Assert.That(results.Count(result => result.Road is not null)).IsEqualTo(10);
        await Assert.That(results.Count(result => result.Decal is not null)).IsEqualTo(97);
        await Assert.That(results.Count(result => result.Outcome == ZoneCellNonBrushCollisionOutcome.RequiresAnotherOwner)).IsEqualTo(1544);
        await Assert.That(results.Where(result => result.HasNoPhysicalContribution).All(result =>
            result.Dependencies.Count == 1 && result.Dependencies[0].Sha256.Length == 64 &&
            result.CertifiedMask == (ZoneCellNonBrushCollisionMask.Player | ZoneCellNonBrushCollisionMask.Ray) && !result.RenderingReady)).IsTrue();
        await Assert.That(owner.MaterialSnapshotCount).IsEqualTo(8);
        await Assert.That(source.Opened.Count).IsEqualTo(8);
        await Assert.That(source.Closed).IsEqualTo(8);
        foreach (var certificate in results)
        {
            await Assert.That(ReferenceEquals(certificate.SourceRow, objects.Objects[certificate.SourceOrder])).IsTrue();
            await Assert.That(ReferenceEquals(certificate.SourceContent, objects)).IsTrue();
            await Assert.That(certificate.SourceOffset).IsEqualTo(certificate.SourceRow.SourceOffset);
        }
        AssertNoEngineModules();
    }

    [Test]
    public async Task OriginalInstructionFactoryControlsMatchTranslatedRoadAndDecalParametersBitForBit()
    {
        using var fixture = Fixture(); var root = fixture.RootElement;
        var objects = ZoneCellObjectContent.Read(OriginalObjects(root), ZoneCellObjectFileKind.Octree);
        using var source = OriginalMaterials(root);
        using var owner = new ZoneCellNonBrushCollisionContent(source);
        var count = 0;
        foreach (var native in root.GetProperty("native").GetProperty("controls").EnumerateArray())
        {
            var row = objects.Objects[native.GetProperty("sourceOrder").GetInt32()];
            var cell = native.GetProperty("engineCell").EnumerateArray().Select(value => value.GetInt32()).ToArray();
            var result = owner.Classify(objects, row, cell[0], cell[1]);
            await Assert.That(result.HasNoPhysicalContribution).IsTrue();
            await Assert.That(result.PayloadSha256).IsEqualTo(native.GetProperty("payloadSha256").GetString());
            await Assert.That(result.SourceOffset).IsEqualTo(native.GetProperty("sourceOffset").GetInt32());
            await Assert.That(row.RawPayload.Length).IsEqualTo(native.GetProperty("consumed").GetInt32());
            await Assert.That(native.GetProperty("physics").GetInt64()).IsEqualTo(0);
            await Assert.That(native.GetProperty("physicalizeNoMutation").GetBoolean()).IsTrue();
            if (result.Road is { } road)
            {
                var expected = native.GetProperty("vertices").EnumerateArray().Select(Vector).ToArray();
                await Assert.That(road.Vertices.Count).IsEqualTo(expected.Length);
                for (var index = 0; index < expected.Length; index++) Exact(road.Vertices[index], expected[index]);
                var parameters = native.GetProperty("parameters").EnumerateArray().Select(value => value.GetSingle()).ToArray();
                Exact(new(road.Parameters.X, road.Parameters.Y, road.Parameters.Z), new(parameters[0], parameters[1], parameters[2]));
                await Assert.That(BitConverter.SingleToInt32Bits(road.Parameters.W)).IsEqualTo(BitConverter.SingleToInt32Bits(parameters[3]));
                await Assert.That(road.SortPriority).IsEqualTo(native.GetProperty("sortPriority").GetByte());
                await Assert.That(native.GetProperty("physicalEntity").GetInt64()).IsEqualTo(0);
                await Assert.That(native.GetProperty("physicalGeometry").GetInt64()).IsEqualTo(0);
            }
            else
            {
                var decal = result.Decal!;
                Exact(decal.Position, Vector(native.GetProperty("position")));
                Exact(decal.Direction, Vector(native.GetProperty("direction")));
                var orientation = native.GetProperty("orientation").EnumerateArray().Select(value => value.GetSingle()).ToArray();
                Exact(decal.OrientationRow0, new(orientation[0], orientation[1], orientation[2]));
                Exact(decal.OrientationRow1, new(orientation[3], orientation[4], orientation[5]));
                Exact(decal.OrientationRow2, new(orientation[6], orientation[7], orientation[8]));
                await Assert.That(decal.AuthoredProjectionType).IsEqualTo(((ZoneCellDecalObject)row).ProjectionType);
                await Assert.That(decal.Radius).IsEqualTo(native.GetProperty("radius").GetSingle());
            }
            count++;
        }
        await Assert.That(count).IsEqualTo(117);
    }

    [Test]
    public async Task OriginalCompilerAndSpecialCreatorControlsKeepTheirDifferentPhysicalLifecyclesExplicit()
    {
        using var fixture = Fixture(); var native = fixture.RootElement.GetProperty("native");
        foreach (var control in native.GetProperty("compilerFullDegenerateControls").EnumerateArray())
        {
            var events = control.GetProperty("events").EnumerateArray().Select(value => value.GetString()).ToArray();
            await Assert.That(control.GetProperty("physicalEntity").GetInt64()).IsEqualTo(0);
            await Assert.That(events.Count(value => value == "DestroyPhysicalEntity")).IsEqualTo(control.GetProperty("initialBody").GetBoolean() ? 1 : 0);
            await Assert.That(events.Contains("CreatePhysicalEntity")).IsFalse();
        }
        var special = native.GetProperty("specialCreatorPositiveControl");
        await Assert.That(special.GetProperty("events").EnumerateArray().Any(value => value.GetString() == "CreatePhysicalEntity")).IsTrue();
        await Assert.That(special.GetProperty("physicalEntity").GetInt64()).IsGreaterThan(0);
        await Assert.That(native.GetProperty("specialCreatorDirectCallOrJumpReferences").GetArrayLength()).IsEqualTo(0);
        await Assert.That(native.GetProperty("specialCreatorDataPointerReferences").GetArrayLength()).IsEqualTo(0);
        await Assert.That(native.GetProperty("engineModuleLoads").GetInt32()).IsEqualTo(0);
        var external = native.GetProperty("externalSetPhysicsControls").EnumerateArray().ToArray();
        await Assert.That(external[0].GetProperty("getPhysicsAfterExternalSet").GetInt64()).IsGreaterThan(0);
        await Assert.That(external[1].GetProperty("getPhysicsAfterExternalSet").GetInt64()).IsEqualTo(0);
        foreach (var gate in native.GetProperty("skippedFactoryGateControls").EnumerateArray())
        {
            await Assert.That(gate.GetProperty("result").GetInt64()).IsEqualTo(0);
            await Assert.That(gate.GetProperty("consumed").GetInt32()).IsEqualTo(gate.GetProperty("expectedBytes").GetInt32());
            await Assert.That(gate.GetProperty("events").GetArrayLength()).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments(0)] [Arguments(2)] [Arguments(3)] [Arguments(5)]
    public async Task AnIncompleteRoadStripCannotBecomeACompleteCertificate(int count)
    {
        var objects = Objects(13, Road(count)); using var assets = ValidAssets();
        using var owner = new ZoneCellNonBrushCollisionContent(assets);
        await Assert.That(owner.Classify(objects, objects.Objects[0], 0, 0).Outcome).IsEqualTo(ZoneCellNonBrushCollisionOutcome.Unsupported);
    }

    [Test]
    [Arguments(1)] [Arguments(3)] [Arguments(255)]
    public async Task UnownedDecalProjectionProfilesStayExplicit(int projection)
    {
        var bytes = Decal(); bytes[0x27] = (byte)projection;
        var objects = Objects(9, bytes); using var assets = ValidAssets();
        using var owner = new ZoneCellNonBrushCollisionContent(assets);
        await Assert.That(owner.Classify(objects, objects.Objects[0], 0, 0).Outcome).IsEqualTo(ZoneCellNonBrushCollisionOutcome.Unsupported);
    }

    [Test]
    [Arguments(13, 0x3f)] [Arguments(13, 0x2b)] [Arguments(9, 0x2b)] [Arguments(9, 0x67)]
    public async Task NonfiniteRoadOrDecalValuesNeverEstablishClearance(int type, int offset)
    {
        var bytes = type == 13 ? Road(4) : Decal(); Float(bytes, offset, float.NaN);
        var objects = Objects((uint)type, bytes); using var assets = ValidAssets();
        using var owner = new ZoneCellNonBrushCollisionContent(assets);
        await Assert.That(owner.Classify(objects, objects.Objects[0], 0, 0).HasNoPhysicalContribution).IsFalse();
    }

    [Test]
    [Arguments("<notmaterial/>", ZoneCellNonBrushCollisionOutcome.InvalidData)]
    [Arguments("<Material", ZoneCellNonBrushCollisionOutcome.InvalidData)]
    [Arguments("<!DOCTYPE Material [<!ENTITY e SYSTEM 'file:///not-opened'>]><Material>&e;</Material>", ZoneCellNonBrushCollisionOutcome.InvalidData)]
    [Arguments("<Material><SubMaterials><Ref Name='unowned'/></SubMaterials></Material>", ZoneCellNonBrushCollisionOutcome.Unsupported)]
    [Arguments("<Material><DualMaterial Name='unowned'/></Material>", ZoneCellNonBrushCollisionOutcome.Unsupported)]
    [Arguments("<Material MtlFlags='256'/>", ZoneCellNonBrushCollisionOutcome.Unsupported)]
    [Arguments("<Material><Textures><Unknown Source='unowned'/></Textures></Material>", ZoneCellNonBrushCollisionOutcome.Unsupported)]
    [Arguments("<Material><PublicParams><Geometry File='solid.cgf'/></PublicParams></Material>", ZoneCellNonBrushCollisionOutcome.Unsupported)]
    public async Task UnownedOrMalformedMaterialDependenciesRemainFailures(string xml, ZoneCellNonBrushCollisionOutcome outcome)
    {
        using var assets = new Assets(); assets.Files["game/materials/source.mtl"] = Encoding.UTF8.GetBytes(xml);
        var objects = Objects(13, Road(4)); using var owner = new ZoneCellNonBrushCollisionContent(assets);
        var result = owner.Classify(objects, objects.Objects[0], 0, 0);
        await Assert.That(result.Outcome).IsEqualTo(outcome);
        await Assert.That(result.CertifiedMask).IsEqualTo(ZoneCellNonBrushCollisionMask.None);
        await Assert.That(result.Dependencies.Single().Sha256.Length).IsEqualTo(64);
        await Assert.That(result.Diagnostics.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task MissingMaterialAndFailedAssetReadKeepTheirActualDependencyIdentity()
    {
        var objects = Objects(13, Road(4)); using var assets = new Assets();
        using var missing = new ZoneCellNonBrushCollisionContent(assets);
        var result = missing.Classify(objects, objects.Objects[0], 0, 0);
        await Assert.That(result.Outcome).IsEqualTo(ZoneCellNonBrushCollisionOutcome.MissingDependency);
        await Assert.That(result.Dependencies.Single().AssetPath).IsEqualTo("game/materials/source.mtl");
        using var failedAssets = new Assets { ReadError = new IOException("Actual controlled source failure") };
        using var failed = new ZoneCellNonBrushCollisionContent(failedAssets);
        result = failed.Classify(objects, objects.Objects[0], 0, 0);
        await Assert.That(result.Outcome).IsEqualTo(ZoneCellNonBrushCollisionOutcome.InvalidData);
        await Assert.That(result.Dependencies.Single().Diagnostic).Contains("Actual controlled source failure");
    }

    [Test]
    public async Task MaterialByteAndStructureBoundsRefuseBeforePublishingACompleteDependency()
    {
        var objects = Objects(13, Road(4)); using var assets = new Assets();
        assets.Files["game/materials/source.mtl"] = new byte[ZoneCellNonBrushCollisionContent.MaximumMaterialBytes + 1];
        using (var owner = new ZoneCellNonBrushCollisionContent(assets))
            await Assert.That(owner.Classify(objects, objects.Objects[0], 0, 0).Outcome).IsEqualTo(ZoneCellNonBrushCollisionOutcome.InvalidData);
        var xml = "<Material>" + string.Concat(Enumerable.Repeat("<Nested>", 66)) +
            string.Concat(Enumerable.Repeat("</Nested>", 66)) + "</Material>";
        using var deepAssets = new Assets(); deepAssets.Files["game/materials/source.mtl"] = Encoding.UTF8.GetBytes(xml);
        using var deep = new ZoneCellNonBrushCollisionContent(deepAssets);
        await Assert.That(deep.Classify(objects, objects.Objects[0], 0, 0).Outcome).IsEqualTo(ZoneCellNonBrushCollisionOutcome.InvalidData);
    }

    [Test]
    public async Task InvalidResourceIndicesAndUnknownSerializedFamiliesFailAtTheContentBoundary()
    {
        var road = Road(4); BinaryPrimitives.WriteUInt32LittleEndian(road.AsSpan(0x3b), 1);
        await Assert.That(() => Objects(13, road)).Throws<InvalidDataException>();
        await Assert.That(() => Objects(99, Road(4))).Throws<InvalidDataException>();
    }

    [Test]
    public async Task ExactRecordMembershipAndEngineCellAdmissionCannotBeReplacedByEquivalentBytes()
    {
        var first = Objects(13, Road(4)); var second = Objects(13, Road(4));
        using var assets = ValidAssets(); using var owner = new ZoneCellNonBrushCollisionContent(assets);
        await Assert.That(() => owner.Classify(first, second.Objects[0], 0, 0)).Throws<ArgumentException>();
        await Assert.That(() => owner.Classify(first, first.Objects[0], 8, 0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => owner.Classify(first, first.Objects[0], 0, -1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(owner.MaterialSnapshotCount).IsEqualTo(0);
    }

    [Test]
    public async Task WaterAndOtherCollisionFamiliesRequireTheirActualOwners()
    {
        var water = new byte[119]; var content = Objects(11, water);
        using var assets = ValidAssets(); using var owner = new ZoneCellNonBrushCollisionContent(assets);
        var result = owner.Classify(content, content.Objects[0], 0, 0);
        await Assert.That(result.Outcome).IsEqualTo(ZoneCellNonBrushCollisionOutcome.RequiresAnotherOwner);
        await Assert.That(result.CertifiedMask).IsEqualTo(ZoneCellNonBrushCollisionMask.None);
        await Assert.That(owner.MaterialSnapshotCount).IsEqualTo(0);
    }

    [Test]
    public async Task CertificatesRemainCopiedSourceProofWhileOwnerAndBorrowedSourceHaveSeparateLifetimes()
    {
        using var source = ValidAssets(); var objects = Objects(13, Road(4)); using var owner = new ZoneCellNonBrushCollisionContent(source);
        var result = owner.Classify(objects, objects.Objects[0], 1, 2);
        var hash = result.Dependencies.Single().Sha256;
        source.Files["game/materials/source.mtl"] = Encoding.UTF8.GetBytes("<Material><Unknown/></Material>");
        await Assert.That(ReferenceEquals(result, owner.Classify(objects, objects.Objects[0], 1, 2))).IsTrue();
        owner.Dispose();
        await Assert.That(source.Disposed).IsFalse();
        await Assert.That(() => owner.Classify(objects, objects.Objects[0], 1, 2)).Throws<ObjectDisposedException>();
        await Assert.That(result.HasNoPhysicalContribution).IsTrue();
        await Assert.That(result.Dependencies.Single().Sha256).IsEqualTo(hash);
        await Assert.That(() => ((IList<Vector3>)result.Road!.Vertices).Clear()).Throws<NotSupportedException>();
        using var replacement = new ZoneCellNonBrushCollisionContent(source);
        await Assert.That(replacement.Classify(objects, objects.Objects[0], 1, 2).Outcome).IsEqualTo(ZoneCellNonBrushCollisionOutcome.Unsupported);
    }

    [Test]
    public async Task DisposalInsideTheBorrowedSourceCallbackCannotPublishAfterRetirement()
    {
        using var source = ValidAssets(); var objects = Objects(13, Road(4)); using var owner = new ZoneCellNonBrushCollisionContent(source);
        source.Opening = owner.Dispose;
        await Assert.That(() => owner.Classify(objects, objects.Objects[0], 0, 0)).Throws<ObjectDisposedException>();
        await Assert.That(source.Closed).IsEqualTo(1);
        await Assert.That(owner.MaterialSnapshotCount).IsEqualTo(0);
        await Assert.That(source.Disposed).IsFalse();
    }

    private static JsonDocument Fixture() => JsonDocument.Parse(typeof(CellNonBrushCollisionContentTests).Assembly.GetManifestResourceStream(
        typeof(CellNonBrushCollisionContentTests).Assembly.GetName().Name + ".Fixtures.cell-nonbrush-009014-controls.json")!);
    private static byte[] OriginalObjects(JsonElement root)
    {
        using var compressed = new MemoryStream(Convert.FromBase64String(root.GetProperty("objectGzipBase64").GetString()!));
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress); using var output = new MemoryStream(); gzip.CopyTo(output);
        var bytes = output.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (hash != root.GetProperty("sourceSha256").GetString()) throw new InvalidDataException("The original object.dat fixture fingerprint changed.");
        return bytes;
    }
    private static Assets OriginalMaterials(JsonElement root)
    {
        var assets = new Assets();
        foreach (var row in root.GetProperty("materials").EnumerateArray())
            assets.Files.Add(row.GetProperty("assetPath").GetString()!, Convert.FromBase64String(row.GetProperty("base64").GetString()!));
        return assets;
    }
    private static Assets ValidAssets()
    { var assets = new Assets(); assets.Files.Add("game/materials/source.mtl", Encoding.UTF8.GetBytes("<Material MtlFlags='0' Shader='Illum'><Textures/><PublicParams/></Material>")); return assets; }
    private static ZoneCellObjectContent Objects(uint type, byte[] payload)
    {
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        writer.Write(0); writer.Write(1); writer.Write(0); var name = new byte[256]; Encoding.ASCII.GetBytes("materials/source").CopyTo(name, 0); writer.Write(name);
        writer.Write(type); writer.Write(payload); return ZoneCellObjectContent.Read(output.ToArray(), ZoneCellObjectFileKind.BigObjects);
    }
    private static byte[] Road(int count)
    {
        var data = new byte[63 + 12 * count]; Float(data, 12, 3); Float(data, 16, 3); Float(data, 20, 3);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0x27), count); Float(data, 0x2f, 1); Float(data, 0x37, 1);
        for (var index = 0; index < count; index++) { Float(data, 63 + 12 * index, index % 2); Float(data, 67 + 12 * index, index / 2); Float(data, 71 + 12 * index, 1); }
        return data;
    }
    private static byte[] Decal()
    {
        var data = new byte[111]; Float(data, 12, 3); Float(data, 16, 3); Float(data, 20, 3); Float(data, 0x3f, 1);
        Float(data, 0x43, 1); Float(data, 0x53, 1); Float(data, 0x63, 1); Float(data, 0x67, 1); return data;
    }
    private static void Float(byte[] data, int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset), value);
    private static Vector3 Vector(JsonElement value) { var v = value.EnumerateArray().Select(row => row.GetSingle()).ToArray(); return new(v[0], v[1], v[2]); }
    private static void Exact(Vector3 actual, Vector3 expected)
    {
        if (BitConverter.SingleToInt32Bits(actual.X) != BitConverter.SingleToInt32Bits(expected.X) ||
            BitConverter.SingleToInt32Bits(actual.Y) != BitConverter.SingleToInt32Bits(expected.Y) ||
            BitConverter.SingleToInt32Bits(actual.Z) != BitConverter.SingleToInt32Bits(expected.Z))
            throw new InvalidOperationException($"Native copied vector {expected} != managed {actual}.");
    }
    private static void AssertNoEngineModules()
    {
        if (!OperatingSystem.IsWindows()) return; using var process = Process.GetCurrentProcess();
        var forbidden = process.Modules.Cast<ProcessModule>().Select(module => module.ModuleName)
            .Where(name => name.StartsWith("cry", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("crypt", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("x2game", StringComparison.OrdinalIgnoreCase) || name.StartsWith("tbb", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("xlcommon.dll", StringComparison.OrdinalIgnoreCase) || name.Equals("lua51.dll", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("lua5.1.dll", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (forbidden.Length != 0) throw new InvalidOperationException("Non-brush certificates loaded forbidden engine modules: " + string.Join(", ", forbidden));
    }
    private sealed class Assets : IZoneAssetSource, IDisposable
    {
        internal readonly Dictionary<string, byte[]> Files = new(StringComparer.Ordinal);
        internal readonly List<string> Opened = [];
        internal Action? Opening; internal Exception? ReadError; internal int Closed; internal bool Disposed;
        public Stream? OpenRead(string virtualPath)
        {
            Opening?.Invoke(); Opened.Add(virtualPath); if (ReadError is { } error) throw error;
            return Files.TryGetValue(virtualPath, out var bytes) ? new Tracked(this, bytes) : null;
        }
        public void Dispose() => Disposed = true;
        private sealed class Tracked(Assets owner, byte[] bytes) : MemoryStream(bytes, false)
        { protected override void Dispose(bool disposing) { if (disposing) owner.Closed++; base.Dispose(disposing); } }
    }
}
