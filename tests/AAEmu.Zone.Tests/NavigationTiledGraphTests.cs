using System.Buffers.Binary;
using System.Numerics;
using AAEmu.Zone.Content;
using AAEmu.Zone.Navigation;

namespace AAEmu.Zone.Tests;

public sealed class NavigationTiledGraphTests
{
    [Test]
    public async Task StreamedTileCoordinatesAndNodeOneLinksAreNotDedicatedRootReplacement()
    {
        var files = new NavigationTileFixture(); var tile = new ZoneMapCoordinate(37, 58);
        files.Add(tile, [new(1, new(2, 3, 4)), new(7, new(10, 3, 4))], [new(1, 7, .75f, new(6, 3, 4))]);
        var loaded = files.Load(tile);
        await Assert.That(loaded.Status).IsEqualTo(ZoneNavigationTileLoadStatus.Loaded);
        await Assert.That(loaded.Content!.Offset).IsEqualTo(new ZoneMapCoordinate(256, 512));
        using var owner = new ZoneNavigationTiledGraph(files.Origin, [loaded]); var graph = owner.Snapshot;
        var ids = graph.Tiles[0].AuthoredNodeIds;
        await Assert.That(graph.Nodes[ids[1]].Position).IsEqualTo(new Vector3(258, 515, 4));
        await Assert.That(graph.Nodes[ids[1]].Links.Count).IsEqualTo(1);
        await Assert.That(graph.Nodes[ids[1]].Links[0].TargetId).IsEqualTo(ids[7]);
        await Assert.That(graph.Nodes[ids[1]].Links[0].Midpoint).IsEqualTo(new Vector3(262, 515, 4));
        await Assert.That(graph.Nodes[ids[1]].Links[0].QuantizedTraversalLimit).IsEqualTo((short)75);
        await Assert.That(graph.Nodes[ids[7]].Links[0].QuantizedTraversalLimit).IsEqualTo((short)0);
    }

    [Test]
    public async Task TiledIdsAreIndependentAndSeamWeldKeepsTheFirstXyVertexAcrossDifferentHeights()
    {
        var files = new NavigationTileFixture(); var a = new ZoneMapCoordinate(36, 56); var b = new ZoneMapCoordinate(37, 56);
        files.Add(a, [new(1, new(256, 10, 3)), new(2, new(250, 10, 3))], [new(1, 2, 1)], [new(256, 10, 4), new(250, 10, 4), new(256, 15, 4)]);
        files.Add(b, [new(1, new(0, 10, 3)), new(2, new(6, 10, 3))], [new(1, 2, 2)], [new(0, 10, 99), new(6, 10, 99), new(0, 15, 99)]);
        using var owner = new ZoneNavigationTiledGraph(files.Origin, [files.Load(a), files.Load(b)]); var graph = owner.Snapshot;
        var first = graph.Tiles[0]; var second = graph.Tiles[1];
        await Assert.That(first.AuthoredNodeIds[1]).IsEqualTo(second.AuthoredNodeIds[1]);
        await Assert.That(first.AuthoredNodeIds[2] == second.AuthoredNodeIds[2]).IsFalse();
        await Assert.That(graph.Nodes.Count).IsEqualTo(3);
        await Assert.That(graph.Nodes[first.AuthoredNodeIds[1]].Links.Count).IsEqualTo(2);
        await Assert.That(first.VertexIndices[0]).IsEqualTo(second.VertexIndices[0]);
        await Assert.That(graph.Vertices[first.VertexIndices[0]].Position.Z).IsEqualTo(4f);
        await Assert.That(graph.Vertices.Count).IsEqualTo(4);
    }

    [Test]
    public async Task RetiringATileRebuildsSharedOwnershipAndLeavesOldSnapshotsOnlyAsCopiedData()
    {
        var files = new NavigationTileFixture(); var a = new ZoneMapCoordinate(36, 56); var b = new ZoneMapCoordinate(37, 56);
        files.Add(a, [new(1, new(256, 10, 3)), new(2, new(250, 10, 3))], [new(1, 2, 1)]);
        files.Add(b, [new(1, new(0, 10, 3)), new(2, new(6, 10, 3))], [new(1, 2, 2)]);
        var owner = new ZoneNavigationTiledGraph(files.Origin, [files.Load(a), files.Load(b)]); var old = owner.Snapshot;
        await Assert.That(owner.RetireTile(a)).IsTrue(); var current = owner.Snapshot;
        await Assert.That(current.Revision).IsEqualTo(2ul);
        await Assert.That(current.Tiles.Count).IsEqualTo(1);
        await Assert.That(current.Nodes.Count).IsEqualTo(2);
        await Assert.That(current.Nodes.Values.SelectMany(n => n.Links).All(l => current.Nodes.ContainsKey(l.TargetId))).IsTrue();
        await Assert.That(old.Nodes.Count).IsEqualTo(3);
        await Assert.That(owner.RetireTile(a)).IsFalse();
        owner.Dispose(); owner.Dispose();
        await Assert.That(() => owner.Snapshot).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task MissingAndWrongVersionTripletsCannotPublishPartialAreasOrTopology()
    {
        var files = new NavigationTileFixture(); var tile = new ZoneMapCoordinate(36, 56);
        files.Add(tile, [new(5, new(2, 3, 4)), new(7, new(10, 3, 4))], [new(5, 7, 1)]);
        // Streaming has no global authored-key1 gate; both real nodes remain available.
        var valid = files.Load(tile); await Assert.That(valid.Status).IsEqualTo(ZoneNavigationTileLoadStatus.Loaded);
        files.Files.Remove(files.Path(tile) + "/netmission0.bai");
        var missing = files.Load(tile); await Assert.That(missing.Status).IsEqualTo(ZoneNavigationTileLoadStatus.MissingAsset);
        await Assert.That(missing.Content is null).IsTrue();
        files.Files[files.Path(tile) + "/netmission0.bai"] = BitConverter.GetBytes(54);
        var wrong = files.Load(tile); await Assert.That(wrong.Status).IsEqualTo(ZoneNavigationTileLoadStatus.UnsupportedFormat);
        using var owner = new ZoneNavigationTiledGraph(files.Origin, [wrong]);
        await Assert.That(owner.Snapshot.Nodes.Count).IsEqualTo(0);
        await Assert.That(owner.Snapshot.AllRequestedTilesLoaded).IsFalse();
        await Assert.That(owner.Snapshot.Diagnostics.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ASceneCannotCombineTilesFromDifferentWorldsWithTheSameOrigin()
    {
        var files = new NavigationTileFixture(); var a = new ZoneMapCoordinate(36, 56); var b = new ZoneMapCoordinate(37, 56);
        files.Add(a, [new(1, new(2, 3, 0))], []); files.Add(b, [new(1, new(2, 3, 0))], []);
        foreach (var pair in files.Files.Where(pair => pair.Key.StartsWith(files.Path(b), StringComparison.Ordinal)).ToArray())
            files.Files.Add(pair.Key.Replace("worlds/test/", "worlds/other/", StringComparison.Ordinal), pair.Value);
        var other = ZoneNavigationTileContent.Load(files, "other", files.Origin, b);
        await Assert.That(other.Status).IsEqualTo(ZoneNavigationTileLoadStatus.Loaded);
        await Assert.That(() => new ZoneNavigationTiledGraph(files.Origin, [files.Load(a), other])).Throws<ArgumentException>();
    }

    [Test]
    public async Task FirstNamedAreasAndModifiersSurviveDuplicateRowsWithoutDroppingTypedNodes()
    {
        var files = new NavigationTileFixture(); var tile = new ZoneMapCoordinate(36, 56);
        files.Add(tile, [new(1, new(1, 1, 3), 4), new(2, new(8, 8, 3), 8), new(3, new(15, 15, 3), 16)], [new(1, 2, 2), new(2, 3, 2)]);
        files.Files[files.Path(tile) + "/areasmission0.bai"] = NavigationTileFixture.Areas(
            [("wall", new Vector3(20, 20, 0)), ("wall", new Vector3(40, 40, 0))],
            [("room", 0u, Vector3.Zero), ("room", 0u, new Vector3(40, 40, 0)), ("surface", 4u, new Vector3(7, 7, 0))]);
        using var owner = new ZoneNavigationTiledGraph(files.Origin, [files.Load(tile)]); var graph = owner.Snapshot;
        await Assert.That(graph.ForbiddenAreas.Count).IsEqualTo(1);
        await Assert.That(graph.ForbiddenAreas[0].Contains(new(21, 21, 0))).IsTrue();
        await Assert.That(graph.ForbiddenAreas[0].Contains(new(41, 41, 0))).IsFalse();
        await Assert.That(graph.Modifiers.Count).IsEqualTo(2);
        await Assert.That(graph.Modifiers.Single(m => m.Source.Name.Text == "surface").RuntimeId).IsEqualTo(2);
        var ids = graph.Tiles[0].AuthoredNodeIds;
        await Assert.That(graph.Nodes[ids[1]].RegionId).IsEqualTo(0);
        await Assert.That(graph.Nodes[ids[2]].RegionId).IsEqualTo(2);
        await Assert.That(graph.Nodes[ids[3]].NavigationType).IsEqualTo(16u);
        await Assert.That(graph.Nodes[ids[2]].Links.Count).IsEqualTo(2);
    }
}

internal sealed class NavigationTileFixture : IZoneAssetSource
{
    internal readonly record struct Node(uint Id, Vector3 Position, ushort Type = 2, int A = 0, int B = 1, int C = 2);
    internal readonly record struct Link(uint From, uint To, float Limit, Vector3 Midpoint = default);
    internal ZoneMapCoordinate Origin { get; } = new(9, 14);
    internal Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
    public Stream? OpenRead(string path) => Files.TryGetValue(path, out var bytes) ? new MemoryStream(bytes, writable: false) : null;
    internal string Path(ZoneMapCoordinate tile) => $"game/worlds/test/paths/{tile.X:D3}_{tile.Y:D3}";
    internal ZoneNavigationTileLoadResult Load(ZoneMapCoordinate tile) => ZoneNavigationTileContent.Load(this, "TEST", Origin, tile);
    internal void Add(ZoneMapCoordinate tile, Node[] nodes, Link[] links, Vector3[]? vertices = null)
    {
        vertices ??= [Vector3.Zero, new(300, 0, 0), new(0, 300, 0)];
        using var vertex = new MemoryStream(); using var vw = new BinaryWriter(vertex);
        vw.Write(2); vw.Write(vertices.Length);
        foreach (var position in vertices) { var row = new byte[32]; Write(row, 0, position.X); Write(row, 4, position.Y); Write(row, 8, position.Z); vw.Write(row); }
        Files[Path(tile) + "/vertsmission0.bai"] = vertex.ToArray();
        using var net = new MemoryStream(); using var nw = new BinaryWriter(net);
        nw.Write(55); nw.Write(new byte[24]); nw.Write(nodes.Length);
        foreach (var node in nodes)
        {
            var row = new byte[60]; BinaryPrimitives.WriteUInt32LittleEndian(row, node.Id);
            Write(row, 28, node.Position.X); Write(row, 32, node.Position.Y); Write(row, 36, node.Position.Z);
            BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(44), node.A); BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(48), node.B); BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(52), node.C);
            BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan(56), node.Type); nw.Write(row);
        }
        nw.Write(links.Length);
        foreach (var link in links)
        {
            var row = new byte[44]; BinaryPrimitives.WriteUInt32LittleEndian(row, link.From); BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(4), link.To);
            Write(row, 8, link.Midpoint.X); Write(row, 12, link.Midpoint.Y); Write(row, 16, link.Midpoint.Z); Write(row, 20, link.Limit); nw.Write(row);
        }
        Files[Path(tile) + "/netmission0.bai"] = net.ToArray();
        Files[Path(tile) + "/areasmission0.bai"] = Areas([], []);
    }
    internal static byte[] Areas((string Name, Vector3 Origin)[] forbidden, (string Name, uint Type, Vector3 Origin)[] modifiers)
    {
        using var data = new MemoryStream(); using var writer = new BinaryWriter(data);
        writer.Write(22); writer.Write(forbidden.Length);
        foreach (var row in forbidden) { Name(row.Name); Points(row.Origin); }
        writer.Write(modifiers.Length);
        foreach (var row in modifiers)
        { Name(row.Name); var parameters = new byte[38]; BinaryPrimitives.WriteUInt32LittleEndian(parameters, row.Type); writer.Write(parameters); Points(row.Origin); }
        writer.Write(0); writer.Write(0); return data.ToArray();
        void Name(string value) { var bytes = System.Text.Encoding.UTF8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }
        void Points(Vector3 origin)
        {
            writer.Write(4);
            foreach (var p in new[] { origin, origin + new Vector3(3, 0, 0), origin + new Vector3(3, 3, 0), origin + new Vector3(0, 3, 0) })
            { writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z); }
        }
    }
    private static void Write(byte[] row, int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(row.AsSpan(offset), value);
}
