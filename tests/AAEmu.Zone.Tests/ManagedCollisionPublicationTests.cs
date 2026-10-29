using System.Numerics;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Tests;

/// <summary>Actual sample/instance publication lifetime, independently of native numeric algorithms.</summary>
public sealed class ManagedCollisionPublicationTests
{
    [Test]
    public async Task TerrainReplacementAndRetirementInvalidateOldTokensEvenAtTheSameCellAddress()
    {
        var heights = new ZoneTerrainHeightField();
        var initial = heights.PublicationRevision;
        heights.LoadCell(1, 1, TerrainHeightFieldTests.CreateCell(100, 1));
        var loaded = heights.PublicationRevision;
        await Assert.That(loaded).IsGreaterThan(initial);
        await Assert.That(heights.GetHeight(1100, 1100)).IsEqualTo(132f);
        heights.LoadCell(1, 1, TerrainHeightFieldTests.CreateCell(300, 1));
        var replaced = heights.PublicationRevision;
        await Assert.That(replaced).IsGreaterThan(loaded);
        await Assert.That(heights.GetHeight(1100, 1100)).IsEqualTo(332f);
        await Assert.That(heights.UnloadCell(1, 1)).IsTrue();
        var retired = heights.PublicationRevision;
        await Assert.That(retired).IsGreaterThan(replaced);
        await Assert.That(heights.HasLoadedCell(1, 1)).IsFalse();
        await Assert.That(heights.GetHeight(1100, 1100)).IsEqualTo(0f);
        heights.LoadCell(1, 1, TerrainHeightFieldTests.CreateCell(100, 1));
        await Assert.That(heights.PublicationRevision).IsGreaterThan(retired);
    }

    [Test]
    public async Task EngineSampleInstallAndLaterSeamWritesHaveSeparatePublicationBoundaries()
    {
        var heights = new ZoneTerrainHeightField();
        var initial = heights.PublicationRevision;
        heights.InitializeNodeTree(0);
        await Assert.That(heights.PublicationRevision).IsGreaterThan(initial);
        heights.LoadCell(1, 1, TerrainHeightFieldTests.CreateCell(100, 1));
        var before = heights.PublicationRevision;
        heights.InstallCellSamples(2, 1, TerrainHeightFieldTests.CreateCell(300, 1));
        var installed = heights.PublicationRevision;
        await Assert.That(installed).IsGreaterThan(before);
        await Assert.That(heights.GetHeight(2048, 1056)).IsEqualTo(332f);
        await Assert.That(heights.GetHeight(2047, 1056)).IsEqualTo(132f);
        heights.CopyCellSeams(2, 1);
        var copied = heights.PublicationRevision;
        await Assert.That(copied).IsGreaterThan(installed);
        await Assert.That(heights.GetHeight(2047, 1056)).IsEqualTo(228f);
        //A later eligible write pass is conservatively another publication fence,
        //including when the source quantization leaves all copied bytes unchanged.
        heights.CopyCellSeams(2, 1);
        await Assert.That(heights.PublicationRevision).IsGreaterThan(copied);
        await Assert.That(heights.GetHeight(2047, 1056)).IsEqualTo(228f);
    }

    [Test]
    public async Task EmptyChunksAbsentRetirementAndReadOrCallbackCacheOperationsDoNotPublishGeometry()
    {
        var heights = new ZoneTerrainHeightField();
        heights.LoadCell(1, 1, TerrainHeightFieldTests.CreateCell(100, 1));
        var current = heights.PublicationRevision;
        heights.LoadCell(1, 1, TerrainHeightFieldTests.EmptyCell());
        heights.InstallCellSamples(1, 1, TerrainHeightFieldTests.EmptyCell());
        await Assert.That(heights.UnloadCell(6, 6)).IsFalse();
        heights.CopyCellSeams(6, 6);
        heights.CopyCellSeams(1, 1); //No loaded neighbor can receive or provide an edge.
        _ = heights.GetHeight(1100, 1100); _ = heights.GetPhysicsHeight(550, 550);
        _ = heights.GetPhysicsSurfaceIndex(550, 550); heights.ResetPhysicsCache();
        await Assert.That(heights.PublicationRevision).IsEqualTo(current);
        await Assert.That(heights.HasLoadedCell(1, 1)).IsTrue();
        await Assert.That(heights.GetHeight(1100, 1100)).IsEqualTo(132f);
    }

    [Test]
    public async Task RejectedTerrainImportsKeepBothThePublishedSamplesAndTheirToken()
    {
        var heights = new ZoneTerrainHeightField();
        heights.LoadCell(1, 1, TerrainHeightFieldTests.CreateCell(100, 1));
        var current = heights.PublicationRevision;
        var malformed = TerrainHeightFieldTests.CreateCell(300, 1, width: 2, fullBorders: false);
        await Assert.That(() => heights.LoadCell(1, 1, malformed)).Throws<InvalidDataException>();
        await Assert.That(() => heights.InitializeNodeTree(0)).Throws<InvalidOperationException>();
        await Assert.That(heights.PublicationRevision).IsEqualTo(current);
        await Assert.That(heights.GetHeight(1100, 1100)).IsEqualTo(132f);
    }

    [Test]
    public async Task StaticAddsRemovesAndClearRetireCachedPhysicalAdmissionButQueriesAndTrueNoOpsDoNot()
    {
        var geometry = new ZoneManagedStaticGeometry();
        var initial = geometry.PublicationRevision;
        var mesh = Mesh();
        var plane = geometry.AddInstance(mesh, Matrix4x4.Identity);
        var first = geometry.PublicationRevision;
        await Assert.That(first).IsGreaterThan(initial);
        var sphere = geometry.AddInstance(new ZoneManagedPrimitiveGeometry(ZoneManagedPrimitiveKind.Sphere, Vector3.Zero, Vector3.Zero, 1, 0, 17),
            Matrix4x4.CreateTranslation(10, 0, 0));
        var second = geometry.PublicationRevision;
        await Assert.That(second).IsGreaterThan(first);
        await Assert.That(geometry.Raycast(new(1, 1, 5), new(0, 0, -10), out _)).IsTrue();
        await Assert.That(geometry.Contains(plane)).IsTrue();
        await Assert.That(geometry.RemoveInstance(new(ulong.MaxValue))).IsFalse();
        await Assert.That(geometry.PublicationRevision).IsEqualTo(second);
        await Assert.That(geometry.RemoveInstance(plane)).IsTrue();
        var removed = geometry.PublicationRevision;
        await Assert.That(removed).IsGreaterThan(second);
        await Assert.That(geometry.Raycast(new(1, 1, 5), new(0, 0, -10), out _)).IsFalse();
        await Assert.That(geometry.Contains(sphere)).IsTrue();
        geometry.Clear(); var cleared = geometry.PublicationRevision;
        await Assert.That(cleared).IsGreaterThan(removed);
        await Assert.That(geometry.InstanceCount).IsEqualTo(0);
        geometry.Clear();
        await Assert.That(geometry.RemoveInstance(sphere)).IsFalse();
        await Assert.That(geometry.PublicationRevision).IsEqualTo(cleared);
        var replacement = geometry.AddInstance(mesh, Matrix4x4.Identity);
        await Assert.That(geometry.PublicationRevision).IsGreaterThan(cleared);
        await Assert.That(replacement.Value).IsGreaterThan(sphere.Value);
    }

    [Test]
    public async Task InvalidStaticTransformsDoNotRetireAnOtherwiseCurrentPublication()
    {
        var geometry = new ZoneManagedStaticGeometry(); var mesh = Mesh();
        geometry.AddInstance(mesh, Matrix4x4.Identity); var current = geometry.PublicationRevision;
        await Assert.That(() => geometry.AddInstance(mesh, Matrix4x4.CreateScale(0))).Throws<ArgumentException>();
        await Assert.That(geometry.PublicationRevision).IsEqualTo(current);
        await Assert.That(geometry.InstanceCount).IsEqualTo(1);
        await Assert.That(geometry.Raycast(new(1, 1, 5), new(0, 0, -10), out _)).IsTrue();
    }

    [Test]
    public async Task ExhaustedTokensRefuseWritesBeforeTheyCanReuseOrPartiallyReplaceAnOldPublication()
    {
        var heights = new ZoneTerrainHeightField(); var content = TerrainHeightFieldTests.CreateCell(100, 1);
        heights.LoadCell(1, 1, content); Exhaust(heights);
        await Assert.That(() => heights.UnloadCell(1, 1)).Throws<InvalidOperationException>();
        await Assert.That(() => heights.LoadCell(1, 1, content)).Throws<InvalidOperationException>();
        await Assert.That(heights.HasLoadedCell(1, 1)).IsTrue();
        await Assert.That(heights.GetHeight(1100, 1100)).IsEqualTo(132f);
        var geometry = new ZoneManagedStaticGeometry(); var mesh = Mesh();
        var instance = geometry.AddInstance(mesh, Matrix4x4.Identity); Exhaust(geometry);
        await Assert.That(() => geometry.RemoveInstance(instance)).Throws<InvalidOperationException>();
        await Assert.That(() => geometry.AddInstance(mesh, Matrix4x4.Identity)).Throws<InvalidOperationException>();
        await Assert.That(geometry.Clear).Throws<InvalidOperationException>();
        await Assert.That(geometry.Contains(instance)).IsTrue();
        await Assert.That(geometry.InstanceCount).IsEqualTo(1);
        await Assert.That(geometry.PublicationRevision).IsEqualTo(ulong.MaxValue);

        static void Exhaust(object owner) => owner.GetType().GetField("_publicationRevision",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(owner, ulong.MaxValue);
    }

    private static ZoneManagedTriangleMesh Mesh() => new([new(0, 0, 0), new(4, 0, 0), new(0, 4, 0)], [0, 1, 2]);
}
