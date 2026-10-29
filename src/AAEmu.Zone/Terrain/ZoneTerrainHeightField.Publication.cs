namespace AAEmu.Zone.Terrain;

public sealed partial class ZoneTerrainHeightField
{
    private ulong _publicationRevision;

    /// <summary>
    /// Managed lifetime token for this exact heightfield's structural/sample publication.
    /// Nonempty imports/replacements, writable seam passes and present-cell retirement advance
    /// it under the sample gate. Empty chunks, absent retirement and read/cache-only operations
    /// do not. This is replacement lifetime bookkeeping, not a native serialization field.
    /// </summary>
    public ulong PublicationRevision { get { lock (_gate) return _publicationRevision; } }

    //Advance before the first write so a failed/partial publication cannot retain an old token.
    private void AdvancePublicationCore()
    {
        if (_publicationRevision == ulong.MaxValue)
            throw new InvalidOperationException("Terrain publication revisions are exhausted; sample ownership cannot change without a new heightfield.");
        _publicationRevision++;
    }

    private bool HasWritableSeamsCore(int x, int y, Cell? cell) =>
        x > 0 && WritableX(_cells[(x - 1) * 8 + y], cell) ||
        x < 7 && WritableX(cell, _cells[(x + 1) * 8 + y]) ||
        y > 0 && WritableY(_cells[x * 8 + y - 1], cell) ||
        y < 7 && WritableY(cell, _cells[x * 8 + y + 1]);

    private static bool WritableX(Cell? left, Cell? right)
    {
        if (left is null || right is null) return false;
        for (var y = 0; y < 16; y++)
            if (left.Leaves[15 * 16 + y].Samples.Length != 0 && right.Leaves[y].Samples.Length != 0) return true;
        return false;
    }
    private static bool WritableY(Cell? bottom, Cell? top)
    {
        if (bottom is null || top is null) return false;
        for (var x = 0; x < 16; x++)
            if (bottom.Leaves[x * 16 + 15].Samples.Length != 0 && top.Leaves[x * 16].Samples.Length != 0) return true;
        return false;
    }
}
