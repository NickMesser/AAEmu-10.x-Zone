using AAEmu.Zone.Content;

namespace AAEmu.Zone.Simulation;

public sealed record ZoneTerrainTextureLoadResult(bool Succeeded,
    ZoneTerrainTextureIndexResult? IndexAssignment, IReadOnlyList<string> Diagnostics);

public sealed record ZoneTerrainTexturePoolSnapshot(string Name, ushort Dimension, uint TextureFormat,
    IReadOnlyList<int> Handles)
{
    public IReadOnlyList<int> Available { get; init; } = Array.Empty<int>();
    public IReadOnlyList<int> InUse { get; init; } = Array.Empty<int>();
    public IReadOnlyList<int> PendingReturn { get; init; } = Array.Empty<int>();
}

/// <summary>
/// 300D9F20's cell metadata, shared staging allocation and two pools for the proven dedicated
/// null renderer. Owned by one terrain tree and serialized by its heightfield gate. Sample unload
/// preserves this metadata and the authored node indices. Its stream connection owns node reads,
/// completion and texture retirement. Render/procedural resources and scheduling remain separate.
/// </summary>
public sealed partial class ZoneDedicatedTerrainTextures : IDisposable
{
    private static readonly ZoneTerrainTextureLayer EmptyLayer = new(0, 0, 0, 0);
    private readonly ZoneTerrainNodeTree _tree;
    private readonly ZoneTerrainTextureContent?[] _cells = new ZoneTerrainTextureContent?[64];
    private readonly ZoneDedicatedTerrainTexturePool _basePool = new();
    private readonly ZoneDedicatedTerrainTexturePool _aoPool = new();
    private byte[] _scratch = [];
    private uint _cellScanCount;
    private ushort _layerCount, _flags;
    private float _sunShadowIntensity;
    private bool _disposed;

    internal ZoneDedicatedTerrainTextures(ZoneTerrainNodeTree tree) => _tree = tree;

    // 300DAF10 initializes these globals to zero. 300D9F20 increments the counter for each
    // occupied metadata slot on every load; it is not the number of currently loaded cells.
    public uint CellScanCount { get { lock (_tree.Gate) return _cellScanCount; } }
    public ushort LayerCount { get { lock (_tree.Gate) return _layerCount; } }
    public ushort Flags { get { lock (_tree.Gate) return _flags; } }
    public float SunShadowIntensity { get { lock (_tree.Gate) return _sunShadowIntensity; } }
    public int ScratchCapacity { get { lock (_tree.Gate) return _scratch.Length; } }

    public ZoneTerrainTextureContent? GetCellMetadata(int cellX, int cellY)
    {
        _ = _tree.GetCellRoot(cellX, cellY);
        lock (_tree.Gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _cells[cellX * 8 + cellY];
        }
    }

    public ZoneTerrainTexturePoolSnapshot GetPool(bool ambientOcclusion)
    {
        lock (_tree.Gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var pool = ambientOcclusion ? _aoPool : _basePool;
            return new(pool.Name, pool.Dimension, pool.TextureFormat, Array.AsReadOnly(pool.CopyHandles()))
            {
                Available = Array.AsReadOnly(pool.CopyAvailable()),
                InUse = Array.AsReadOnly(pool.CopyInUse()),
                PendingReturn = Array.AsReadOnly(pool.CopyPending())
            };
        }
    }

    /// <summary>
    /// Open authored cover.ctc metadata and close the file before pool setup. Missing or invalid
    /// assets produce diagnostics; the native enclosing terrain loader continues after failure.
    /// The pool size is a required runtime setting (native configuration+1E4), not a guessed default.
    /// </summary>
    public ZoneTerrainTextureLoadResult TryOpenCell(ZoneWorldMapCellPlacement placement,
        ZoneTerrainAssetFiles assets, int texturePoolSize)
    {
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentOutOfRangeException.ThrowIfNegative(texturePoolSize);
        _ = _tree.GetCellRoot(placement.EngineCell.X, placement.EngineCell.Y);
        lock (_tree.Gate) ObjectDisposedException.ThrowIf(_disposed, this);
        ZoneTerrainTextureContent content;
        try
        {
            using var input = assets.OpenCoverTexture(placement.Cell);
            if (input is null) return Failed("The cell cover.ctc could not be opened.");
            content = ZoneTerrainTextureContent.Read(input);
        }
        catch (IOException error) { return Failed(error.Message); }
        catch (InvalidDataException error) { return Failed(error.Message); }
        catch (UnauthorizedAccessException error) { return Failed(error.Message); }
        lock (_tree.Gate)
        {
            var result = ApplyCell(placement.EngineCell.X, placement.EngineCell.Y, content, texturePoolSize);
            if (result.Succeeded)
                _cellPaths[placement.EngineCell.X * 8 + placement.EngineCell.Y] = assets.GetCoverTexturePath(placement.Cell);
            return result;
        }
    }

    /// <summary>
    /// Publish parsed metadata and configure dedicated pools. Unsupported allocation profiles
    /// fail before mutation. Index structural/truncation diagnostics retain their assigned prefix
    /// and do not cancel metadata publication, matching the native ignored assignment result.
    /// </summary>
    public ZoneTerrainTextureLoadResult ApplyCell(int cellX, int cellY, ZoneTerrainTextureContent content,
        int texturePoolSize)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegative(texturePoolSize);
        _ = _tree.GetCellRoot(cellX, cellY);
        lock (_tree.Gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            VerifyStreamThread();
            if (_activeRead is not null)
                throw new InvalidOperationException("Complete the terrain node read before replacing shared metadata or staging storage.");
            // Native reads three layer descriptions into a two-layer cell stride, overwriting the
            // next cell (or the staging pointer in the last slot). Do not reproduce memory corruption.
            if (content.Layers.Count > 2)
                return Failed("Three-layer terrain texture metadata overlaps native cell storage and is unsupported.");
            var baseLayer = content.Layers.Count > 0 ? content.Layers[0] : EmptyLayer;
            var aoLayer = content.Layers.Count > 1 ? content.Layers[1] : EmptyLayer;
            var index = cellX * 8 + cellY;
            ushort flags = 1;
            for (var i = 0; i < _cells.Length; i++)
            {
                var cell = i == index ? content : _cells[i];
                if (cell is not null) flags &= cell.Flags;
            }
            int capacity;
            try
            {
                _ = checked(texturePoolSize * sizeof(int));
                capacity = Math.Max(_scratch.Length, checked((int)checked(baseLayer.ByteCount + aoLayer.ByteCount)));
                if (RequiredClear(_basePool, baseLayer, texturePoolSize) > capacity ||
                    (flags == 1 && RequiredClear(_aoPool, aoLayer, texturePoolSize) > capacity))
                    return Failed("Terrain texture layer byte counts do not provide enough storage for pool initialization.");
            }
            catch (OverflowException)
            {
                return Failed("Terrain texture allocation dimensions exceed supported storage.");
            }
            // Native staging storage starts undefined. Neither proven renderer leaf reads it;
            // only the format-dependent failed-upload clears are defined. Never expose its bytes.
            var scratch = capacity > _scratch.Length ? GC.AllocateUninitializedArray<byte>(capacity) : _scratch;
            var assignment = _tree.ApplyTextureIndices(cellX, cellY, content.NodeIndices);
            _cells[index] = content;
            _cellPaths[index] = null; // Only TryOpenCell binds metadata to its validated authored path.
            _scratch = scratch;
            var diagnostics = new List<string>();
            if (assignment.Status is ZoneTerrainTextureIndexStatus.MissingChild or ZoneTerrainTextureIndexStatus.Truncated)
                diagnostics.Add($"Terrain texture indices ended with {assignment.Status} after {assignment.ConsumedIndices} entries.");
            var shadow = 0f;
            for (var i = 0; i < _cells.Length; i++)
            {
                if (_cells[i] is not { } cell) continue;
                if (shadow != 0f && shadow != cell.SunShadowIntensity)
                    diagnostics.Add($"Terrain cell ({i / 8}, {i % 8}) has a different sun shadow intensity.");
                shadow = cell.SunShadowIntensity;
                _cellScanCount = unchecked(_cellScanCount + 1);
            }
            _sunShadowIntensity = shadow;
            _flags = flags;
            _layerCount = 2;
            _basePool.Configure("base", texturePoolSize, _scratch, baseLayer.Dimension, baseLayer.TextureFormat);
            // Turning this aggregate flag off does not clear the existing AO pool in 300D9F20.
            if (flags == 1)
                _aoPool.Configure("ao", texturePoolSize, _scratch, aoLayer.Dimension, aoLayer.TextureFormat);
            return new(true, assignment, diagnostics.AsReadOnly());
        }
    }

    private static int RequiredClear(ZoneDedicatedTerrainTexturePool pool, ZoneTerrainTextureLayer layer, int count) =>
        count == 0 || (pool.Count == count && pool.Dimension == layer.Dimension && pool.TextureFormat == layer.TextureFormat)
            ? 0 : ZoneDedicatedTerrainTexturePool.GetRequiredScratchBytes(layer.Dimension, layer.TextureFormat);

    private static ZoneTerrainTextureLoadResult Failed(string diagnostic) => new(false, null, Array.AsReadOnly(new[] { diagnostic }));

    public void Dispose()
    {
        lock (_tree.Gate)
        {
            if (_disposed) return;
            VerifyStreamThread();
            RetireStreaming();
            _basePool.Dispose();
            _aoPool.Dispose();
            _scratch = [];
            Array.Clear(_cells);
            _disposed = true;
        }
    }
}
