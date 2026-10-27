namespace AAEmu.Zone.Terrain;

public enum ZoneTerrainTextureIndexStatus { Skipped, Applied, Truncated, MissingChild }

public readonly record struct ZoneTerrainTextureIndexResult(ZoneTerrainTextureIndexStatus Status,
    int ConsumedIndices);

public sealed partial class ZoneTerrainNodeTree
{
    /// <summary>
    /// 300D9F20/300E28B0: assign signed file indices in native quadrant order. Empty input or a
    /// negative first index preserves existing state. A structural error preserves earlier writes
    /// and does not consume its offending marker. Trailing indices are allowed. Unlike native,
    /// truncation returns a diagnostic instead of reading beyond the supplied span.
    /// </summary>
    public ZoneTerrainTextureIndexResult ApplyTextureIndices(int cellX, int cellY, ReadOnlySpan<short> indices)
    {
        var root = GetCellRoot(cellX, cellY);
        lock (Gate)
        {
            if (indices.IsEmpty || indices[0] < 0) return new(ZoneTerrainTextureIndexStatus.Skipped, 0);
            var cursor = 0;
            var status = Assign(root, indices, ref cursor);
            return new(status, cursor);
        }
    }

    private static ZoneTerrainTextureIndexStatus Assign(ZoneTerrainNode node, ReadOnlySpan<short> indices,
        ref int cursor)
    {
        if (cursor == indices.Length) return ZoneTerrainTextureIndexStatus.Truncated;
        node.TextureIndexValue = indices[cursor++];
        // Every represented node has four positions in the file, including structural leaves.
        for (var quadrant = 0; quadrant < 4; quadrant++)
        {
            if (cursor == indices.Length) return ZoneTerrainTextureIndexStatus.Truncated;
            var child = quadrant < node.Children.Count ? node.Children[quadrant] : null;
            if (indices[cursor] < 0)
            {
                cursor++;
                if (child is not null) ClearTextureIndices(child);
            }
            else
            {
                if (child is null) return ZoneTerrainTextureIndexStatus.MissingChild;
                var status = Assign(child, indices, ref cursor);
                if (status != ZoneTerrainTextureIndexStatus.Applied) return status;
            }
        }
        node.TextureAssignmentFlags &= 0xfd;
        return ZoneTerrainTextureIndexStatus.Applied;
    }

    // 300E2150 changes only the authored index, including every existing descendant. In
    // particular, it does not clear assignment flag 0x02 or retire a texture/streaming handle.
    private static void ClearTextureIndices(ZoneTerrainNode node)
    {
        node.TextureIndexValue = -1;
        foreach (var child in node.Children) ClearTextureIndices(child);
    }
}
