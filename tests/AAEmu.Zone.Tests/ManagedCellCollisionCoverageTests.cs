using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json;
using AAEmu.Zone.Content;
using AAEmu.Zone.Hosting;
using AAEmu.Zone.Navigation;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Tests;

public sealed class ManagedCellCollisionCoverageTests
{
    [Test]
    public async Task OriginalWaterInAnotherCellIsExcludedByTheSelectedClassWithoutClaimingNoWaterAreas()
    {
        var fixture = Terrain(twoCells: true);
        using var stream = typeof(ManagedCellCollisionCoverageTests).Assembly.GetManifestResourceStream(
            "AAEmu.Zone.Tests.Fixtures.water-query-class-controls.json")!;
        using var controls = JsonDocument.Parse(stream);
        var file = controls.RootElement.GetProperty("files").EnumerateArray().First(file => file.GetProperty("rows").GetArrayLength() > 0);
        using var compressed = new MemoryStream(Convert.FromBase64String(file.GetProperty("waterRowsGzipBase64").GetString()!));
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress); using var bytes = new MemoryStream(); gzip.CopyTo(bytes);
        fixture.Assets.Files["game/worlds/authored/cells/010_014/client/big_object.dat"] = bytes.ToArray();
        using var activation = new ZoneManagedSceneActivation(fixture.Layout, fixture.Assets);
        activation.Create(); var report = activation.Load(); var coverage = activation.CellCollisionCoverage!;
        var other = report.Cells.Single(cell => cell.Placement.EngineCell == new ZoneMapCoordinate(1, 0));
        var proof = coverage.CopyCell(other);
        await Assert.That(proof.WaterRows).IsGreaterThan(0);
        await Assert.That(proof.NonBrushRows).IsEqualTo(proof.WaterRows);
        await Assert.That(proof.EntitySelectionMask).IsEqualTo(0x107u);
        await Assert.That(proof.IsCompleteFor(ZoneManagedCollisionQueryMask.CharactersAndRays)).IsTrue();
        await Assert.That(proof.Water.All(row => row.CanContributeTo(0x20) && !row.CanContributeTo(proof.EntitySelectionMask))).IsTrue();
        await Assert.That(coverage.Check(Bounds, ZoneManagedCollisionQueryMask.CharactersAndRays).Outcome)
            .IsEqualTo(ZoneNavigationSegmentOutcome.Clear);
        var revision = activation.Collision!.Geometry.PublicationRevision;
        coverage.RetireCell(other);
        await Assert.That(coverage.IsCurrent(proof)).IsFalse();
        await Assert.That(activation.Collision.Geometry.PublicationRevision).IsEqualTo(revision);
        await Assert.That(coverage.Check(Bounds, ZoneManagedCollisionQueryMask.Rays).Outcome)
            .IsEqualTo(ZoneNavigationSegmentOutcome.MissingData);
    }

    [Test]
    public async Task EmptyAuthoritativeInputsAreAdmittedAndBodylessRetirementInvalidatesTheirPublication()
    {
        var fixture = Terrain();
        using var activation = new ZoneManagedSceneActivation(fixture.Layout, fixture.Assets);
        activation.Create(); var scene = activation.Load(); var input = scene.Cells.Single();
        var coverage = activation.CellCollisionCoverage!; var collision = activation.Collision!;
        var snapshot = coverage.CopyCell(input);
        await Assert.That(snapshot.IsCompleteFor(ZoneManagedCollisionQueryMask.CharactersAndRays)).IsTrue();
        await Assert.That(coverage.Check(Bounds, ZoneManagedCollisionQueryMask.CharactersAndRays).Outcome)
            .IsEqualTo(ZoneNavigationSegmentOutcome.Clear);
        var characterRevision = collision.Geometry.PublicationRevision; var rayRevision = collision.RayGeometry.PublicationRevision;
        await Assert.That(coverage.RetireCell(input)).IsTrue();
        await Assert.That(coverage.IsCurrent(snapshot)).IsFalse();
        await Assert.That(collision.Geometry.PublicationRevision).IsEqualTo(characterRevision);
        await Assert.That(collision.RayGeometry.PublicationRevision).IsEqualTo(rayRevision);
        await Assert.That(coverage.Check(Bounds, ZoneManagedCollisionQueryMask.CharactersAndRays).Outcome)
            .IsEqualTo(ZoneNavigationSegmentOutcome.MissingData);
    }

    [Test]
    public async Task AnUnreadEmptyInventoryDoesNotBecomeClearFromTheAbsenceOfManagedBodies()
    {
        var fixture = Terrain(); var placement = fixture.Layout.Cells[0];
        var input = new ZoneManagedSceneCellLoadReport(placement, new(ZoneTerrainCellLoadStatus.Loaded),
            new(null, null, null, null, ["Objects: controlled read failure.", "BigObjects: authored asset is absent.",
                "Vegetation: authored asset is absent.", "Entities: authored asset is absent."]));
        var report = new ZoneManagedSceneLoadReport([input], true, 1, 0, []);
        var materials = ZoneTerrainMaterialResolver.Load(fixture.Assets);
        using var collision = new ZoneManagedSceneCollision(fixture.Assets, materials, [input]);
        using var coverage = new ZoneManagedCellCollisionCoverage(fixture.Layout, fixture.Assets, report, collision, materials, ZoneEngineCVars.Empty);
        await Assert.That(collision.Geometry.InstanceCount).IsEqualTo(0);
        await Assert.That(coverage.CopyCell(input).HasMissingInputs).IsTrue();
        await Assert.That(coverage.Check(Bounds, ZoneManagedCollisionQueryMask.CharactersAndRays).Outcome)
            .IsEqualTo(ZoneNavigationSegmentOutcome.MissingData);
    }

    [Test]
    public async Task UnknownPhysicsInAnotherLoadedCellCannotBeExcludedByItsAuthoredCellOrCurrentPosition()
    {
        var fixture = Terrain(twoCells: true);
        fixture.Text("game/worlds/authored/cells/010_014/client/entities.xml",
            "<Mission><Objects><Entity EntityClass='UnownedPhysicsProducer' Name='far' Pos='100000,100000,100000'/></Objects></Mission>");
        using var activation = new ZoneManagedSceneActivation(fixture.Layout, fixture.Assets);
        activation.Create(); var report = activation.Load(); var coverage = activation.CellCollisionCoverage!;
        var target = report.Cells.Single(cell => cell.Placement.EngineCell == new ZoneMapCoordinate(0, 0));
        await Assert.That(coverage.CopyCell(target).IsCompleteFor(ZoneManagedCollisionQueryMask.CharactersAndRays)).IsTrue();
        var result = coverage.Check(Bounds, ZoneManagedCollisionQueryMask.CharactersAndRays);
        await Assert.That(result.Outcome).IsEqualTo(ZoneNavigationSegmentOutcome.Unsupported);
        await Assert.That(result.Diagnostic!.Contains("Loaded cell(1,0)", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task RemovingOnlyOwnedCharacterGeometryPreservesRayClassificationAndInvalidatesOldSnapshots()
    {
        var fixture = Terrain();
        fixture.Assets.Files["game/objects/wall.cgf"] = CgfTestAssets.BoxCgf();
        fixture.Text("game/objects/wall_material.mtl", "<Material SurfaceType='mat_stone'/>");
        fixture.Assets.Files["game/worlds/authored/cells/009_014/client/big_object.dat"] = Brush();
        using var activation = new ZoneManagedSceneActivation(fixture.Layout, fixture.Assets);
        activation.Create(); var report = activation.Load(); var coverage = activation.CellCollisionCoverage!;
        var input = report.Cells.Single(); var initial = coverage.CopyCell(input);
        await Assert.That(initial.IsCompleteFor(ZoneManagedCollisionQueryMask.CharactersAndRays)).IsTrue();
        activation.Collision!.Geometry.Clear();
        await Assert.That(coverage.IsCurrent(initial)).IsFalse();
        var current = coverage.CopyCell(input);
        await Assert.That(current.IsCompleteFor(ZoneManagedCollisionQueryMask.Characters)).IsFalse();
        await Assert.That(current.IsCompleteFor(ZoneManagedCollisionQueryMask.Rays)).IsTrue();
        await Assert.That(coverage.Check(Bounds, ZoneManagedCollisionQueryMask.Characters).Outcome)
            .IsEqualTo(ZoneNavigationSegmentOutcome.Unsupported);
        await Assert.That(coverage.Check(Bounds, ZoneManagedCollisionQueryMask.Rays).Outcome)
            .IsEqualTo(ZoneNavigationSegmentOutcome.Clear);
    }

    [Test]
    public async Task NavigationBindsExactClassificationOwnerAndTracksBodylessCellRetirement()
    {
        var fixture = Terrain(twoCells: true);
        using var activation = new ZoneManagedSceneActivation(fixture.Layout, fixture.Assets);
        activation.Create(); var report = activation.Load();
        var backend = new ZoneManagedPhysicsBackend(new(activation.Heights, activation.Surfaces), staticGeometry: activation.Collision!.Geometry);
        using var water = new ZoneManagedWaterQueries(80, report.Cells, ZoneManagedWaterQueryProfile.StaticServerWithoutOceanVortex);
        var policy = new ZoneManagedNpcNavigationPolicy(new(128, .25f, 1024, .75f, 2, .7f), 2, 65536);
        var queries = new ZoneManagedNpcNavigationQueries(backend, activation.Heights, report, activation.Collision, water, policy,
            activation.CellCollisionCoverage);
        using var wrongOcean = new ZoneManagedWaterQueries(0, report.Cells, ZoneManagedWaterQueryProfile.StaticServerWithoutOceanVortex);
        await Assert.That(() => new ZoneManagedNpcNavigationQueries(backend, activation.Heights,
            report, activation.Collision, wrongOcean, policy, activation.CellCollisionCoverage)).Throws<ArgumentException>();
        using var otherInputs = new ZoneManagedWaterQueries(80, report.Cells.Select(cell => cell with { }).ToArray(),
            ZoneManagedWaterQueryProfile.StaticServerWithoutOceanVortex);
        await Assert.That(() => new ZoneManagedNpcNavigationQueries(backend, activation.Heights,
            report, activation.Collision, otherInputs, policy, activation.CellCollisionCoverage)).Throws<ArgumentException>();
        using var reversedInputs = new ZoneManagedWaterQueries(80, report.Cells.Reverse().ToArray(),
            ZoneManagedWaterQueryProfile.StaticServerWithoutOceanVortex);
        await Assert.That(() => new ZoneManagedNpcNavigationQueries(backend, activation.Heights,
            report, activation.Collision, reversedInputs, policy, activation.CellCollisionCoverage)).Throws<ArgumentException>();
        var publication = queries.CaptureCoverage();
        await Assert.That(() => new ZoneManagedNpcNavigationQueries(backend, activation.Heights,
            report with { }, activation.Collision, water, policy, activation.CellCollisionCoverage)).Throws<ArgumentException>();
        activation.CellCollisionCoverage!.RetireCell(report.Cells[0]);
        await Assert.That(queries.IsCurrent(publication)).IsFalse();
        await Assert.That(queries.CheckCollisionCoverage(Bounds).Outcome).IsEqualTo(ZoneNavigationSegmentOutcome.MissingData);
    }

    private static readonly ZonePhysicsQueryBounds Bounds = new(new(10, 10, 100), new(20, 20, 150));
    private static ManagedSceneActivationTests.Fixture Terrain(bool twoCells = false)
    {
        var fixture = twoCells ? new ManagedSceneActivationTests.Fixture((9, 14), (10, 14)) :
            new ManagedSceneActivationTests.Fixture((9, 14));
        foreach (var placement in fixture.Layout.Cells)
            fixture.Cell(placement.Cell.Coordinate.X, placement.Cell.Coordinate.Y, ManagedSceneActivationTests.Heightmap(24, 100));
        return fixture;
    }
    private static byte[] Brush()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(1); writer.Write(0); var name = new byte[256]; Encoding.UTF8.GetBytes("game/objects/wall.cgf").CopyTo(name, 0);
        writer.Write(name); writer.Write(0); writer.Write((uint)ZoneCellObjectType.Brush);
        var payload = new byte[128]; CgfTestAssets.Vector(payload, 0, new(-10)); CgfTestAssets.Vector(payload, 12, new(10));
        float[] matrix = [1, 0, 0, 10, 0, 1, 0, 20, 0, 0, 1, 100];
        for (var index = 0; index < matrix.Length; index++) BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(0x43 + index * 4), matrix[index]);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0x73), -1); writer.Write(payload); return stream.ToArray();
    }
}
