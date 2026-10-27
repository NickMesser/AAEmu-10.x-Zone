namespace AAEmu.Zone.Terrain;

/// <summary>
/// Dedicated texture-pool initialization/reconfiguration from 300E00B0. The configured
/// CNULLRenderer vtable3815AB48 has upload+758/38017720 returning zero and release+778/380171E0
/// doing nothing. Owns the resulting zero handles and their native available/in-use/pending-return
/// transitions without loading a renderer DLL. Node assignment belongs to the terrain owner.
/// </summary>
internal sealed class ZoneDedicatedTerrainTexturePool : IDisposable
{
    private List<int> _available = [];
    private List<int> _inUse = [];
    private List<int> _pending = [];
    private bool _disposed;

    public string Name { get; private set; } = "";
    public int Count => _available.Count + _inUse.Count + _pending.Count;
    public ushort Dimension { get; private set; }
    public uint TextureFormat { get; private set; }

    /// <summary>Failed-upload clearing size for a nonempty rebuild; other formats require no scratch.</summary>
    internal static int GetRequiredScratchBytes(ushort dimension, uint textureFormat)
    {
        if (textureFormat is not (22 or 24)) return 0;
        var area = checked((int)dimension * dimension);
        return textureFormat == 22 ? area >> 1 : area;
    }

    /// <summary>
    /// Updates the name on every admitted call. Rebuilds only when count, dimension or format
    /// changes. Failed null-renderer uploads clear dimension-squared/2 bytes for format22 or
    /// dimension-squared bytes for format24. Other formats leave scratch unchanged. Validation
    /// and replacement allocation complete before existing state or scratch is modified.
    /// </summary>
    public void Configure(string name, int desiredCount, Span<byte> scratch, ushort dimension,
        uint textureFormat)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentOutOfRangeException.ThrowIfNegative(desiredCount);

        // The native producer copies the name through strlen, including its embedded-NUL boundary.
        var terminator = name.IndexOf('\0');
        var updatedName = terminator < 0 ? name : name[..terminator];
        if (Count == desiredCount && Dimension == dimension && TextureFormat == textureFormat)
        {
            Name = updatedName;
            return;
        }

        _ = checked(desiredCount * sizeof(int));
        var clearLength = desiredCount > 0 ? GetRequiredScratchBytes(dimension, textureFormat) : 0;
        if (scratch.Length < clearLength)
            throw new ArgumentException("Texture-pool scratch storage is too short for the failed upload clear.", nameof(scratch));

        // Each native upload returns zero, and releasing every previous handle is a no-op.
        // Keep actual handle storage, including each zero returned by the null renderer.
        var replacement = new List<int>(desiredCount);
        for (var i = 0; i < desiredCount; i++) replacement.Add(0);
        // Upload never reads scratch: its identical clear after each zero return has this same
        // final effect, with no intermediate observer or renderer state to preserve.
        scratch[..clearLength].Clear();
        Name = updatedName;
        _available = replacement;
        _inUse.Clear();
        _pending.Clear();
        Dimension = dimension;
        TextureFormat = textureFormat;
    }

    /// <summary>
    /// Consumes the last available entry, refilling from pending only when available is empty.
    /// True means an entry was consumed even though the actual null-renderer update returns zero.
    /// False is the native cache-full diagnostic boundary. Updating never clears staging bytes.
    /// </summary>
    public bool TryAcquire(out int handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        handle = 0;
        if (_available.Count == 0 && _pending.Count == 0) return false;
        _inUse.EnsureCapacity(checked(_inUse.Count + 1));
        if (_available.Count == 0)
        {
            //300DFFC0 appends pending to the empty available vector, then clears pending.
            (_available, _pending) = (_pending, _available);
        }
        _available.RemoveAt(_available.Count - 1);
        // The real dedicated renderer ignores both the previous ID and source bytes on update.
        _inUse.Add(handle);
        return true;
    }

    /// <summary>
    /// Removes every matching in-use value, retaining the order of other entries, and appends
    /// one pending return on any match. False is the native invalid-return diagnostic boundary.
    /// </summary>
    public bool Return(int handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_inUse.Contains(handle)) return false;
        _pending.EnsureCapacity(checked(_pending.Count + 1));
        _inUse.RemoveAll(value => value == handle);
        _pending.Add(handle);
        return true;
    }

    public int[] CopyAvailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _available.ToArray();
    }

    public int[] CopyInUse()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _inUse.ToArray();
    }

    public int[] CopyPending()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _pending.ToArray();
    }

    public int[] CopyHandles()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var handles = new int[Count];
        _available.CopyTo(handles, 0);
        _inUse.CopyTo(handles, _available.Count);
        _pending.CopyTo(handles, _available.Count + _inUse.Count);
        return handles;
    }

    public void Dispose()
    {
        if (_disposed) return;
        // Renderer+778 has no effects for any ID, including the retained zero handles.
        _available = [];
        _inUse = [];
        _pending = [];
        _disposed = true;
    }
}
