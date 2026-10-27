using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using AAEmu.Zone.Content;

namespace AAEmu.Zone.Simulation;

/// <summary>
/// One admitted dedicated navigation modifier. Authored parameters and geometry stay
/// available through Source; RuntimeId replaces the authored +0x14 value (3107AB58).
/// </summary>
public sealed class ZoneNavigationRuntimeModifier
{
    public int RuntimeId { get; }
    public ZoneNavigationAreaModifierRow Source { get; }

    /// <summary>
    /// Native +0x18, the low 32 bits of the first authored u64 (31060AB8).
    /// This is the modifier type tested by 310515F8, not a graph node type or filter mask.
    /// </summary>
    public uint NativeType { get; }

    internal ZoneNavigationRuntimeModifier(int runtimeId, ZoneNavigationAreaModifierRow source)
    {
        RuntimeId = runtimeId;
        Source = source;
        NativeType = BinaryPrimitives.ReadUInt32LittleEndian(source.ParameterBytes);
    }
}

public enum ZoneNavigationModifierLoadStatus
{
    LoadedAreas,
    UnavailableAsset,
    UnsupportedVersion
}

/// <summary>Only the area/modifier load result; it does not establish navigation readiness.</summary>
public sealed record ZoneNavigationModifierLoadResult(
    ZoneNavigationModifierLoadStatus Status,
    ZoneNavigationLegacyAreaContent? Content,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// Dedicated NavigationManager modifier ownership: its exact byte-name map (+0x2C8),
/// runtime-ID index (+0x2E8), and reusable region pool (+0x308). Spatial modifier
/// selection and the remaining area containers belong to their respective owners.
/// </summary>
public sealed class ZoneNavigationModifierRuntime
{
    // Hex encoding preserves native unsigned byte lexicographic order, including
    // invalid UTF-8 and case distinctions. Names end at their first NUL.
    private readonly SortedDictionary<string, ZoneNavigationRuntimeModifier> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<int, ZoneNavigationRuntimeModifier> _byId = [];
    private readonly ZoneNavigationRegionIdPool _ids = new();
    private bool _loading;

    public int Count => _byName.Count;
    public int AllocatedIdCount => _ids.AllocatedCount;
    public int AvailableIdCount => _ids.AvailableCount;

    /// <summary>Current records in native name-map traversal order, not source order.</summary>
    public IReadOnlyCollection<ZoneNavigationRuntimeModifier> Modifiers => _byName.Values;

    public bool TryGetByName(ReadOnlySpan<byte> name,
        [NotNullWhen(true)] out ZoneNavigationRuntimeModifier? modifier) =>
        _byName.TryGetValue(NameKey(name), out modifier);

    public bool TryGetByRuntimeId(int runtimeId,
        [NotNullWhen(true)] out ZoneNavigationRuntimeModifier? modifier) =>
        _byId.TryGetValue(runtimeId, out modifier);

    /// <summary>Distinguishes a retained old record from a current record after ID reuse.</summary>
    public bool IsCurrent(ZoneNavigationRuntimeModifier modifier)
    {
        ArgumentNullException.ThrowIfNull(modifier);
        return _byId.TryGetValue(modifier.RuntimeId, out var current) && ReferenceEquals(current, modifier);
    }

    /// <summary>
    /// 3107AB58 mode2 clears the modifier map/index and then its pool BEFORE opening
    /// the area file. Missing or rejected input cannot retain the previous generation.
    /// The existing content reader validates complete documents atomically; malformed
    /// or truncated bodies throw explicitly rather than emulate native unchecked reads.
    /// </summary>
    public ZoneNavigationModifierLoadResult LoadLegacy(IZoneAssetSource assets, string areaPath)
    {
        ArgumentNullException.ThrowIfNull(assets);
        areaPath = ZoneAssetPath.Validate(areaPath);
        EnsureNotLoading();
        _loading = true;
        try
        {
            ResetCore();
            Stream? stream;
            try { stream = assets.OpenRead(areaPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Unavailable(areaPath, ex.Message);
            }

            if (stream is null) return Unavailable(areaPath, null);
            try
            {
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                ZoneNavigationLegacyAreaContent content;
                try
                {
                    content = ZoneNavigationLegacyAreaContent.Read(
                        copy.GetBuffer().AsSpan(0, checked((int)copy.Length)));
                }
                catch (NotSupportedException ex)
                {
                    return new(ZoneNavigationModifierLoadStatus.UnsupportedVersion, null,
                        Array.AsReadOnly(new[] { ex.Message }));
                }

                var diagnostics = new List<string>();
                foreach (var row in content.NavigationModifierRows)
                {
                    // 3107AB58 allocates for EVERY physical row before 3108AB5C's
                    // name lookup. Rejected duplicate IDs stay allocated until reset.
                    var id = _ids.Allocate();
                    var key = NameKey(row.Name.Bytes);
                    if (_byName.ContainsKey(key))
                    {
                        diagnostics.Add($"Duplicate navigation modifier name bytes {key} at row {row.SourceIndex}.");
                        continue;
                    }

                    var modifier = new ZoneNavigationRuntimeModifier(id, row);
                    _byName.Add(key, modifier);
                    _byId.Add(id, modifier);
                }

                return new(ZoneNavigationModifierLoadStatus.LoadedAreas, content, diagnostics.AsReadOnly());
            }
            finally
            {
                // 31089288 ignores both fclose and backend virtual+0x40 return
                // errors after publication. Keep capability and validation failures
                // explicit; only ordinary asset-close failures follow that behavior.
                try { stream.Dispose(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        finally { _loading = false; }
    }

    public void Reset()
    {
        EnsureNotLoading();
        ResetCore();
    }

    private void ResetCore()
    {
        // 3113AA0C clears modifier records and their ID index, then 3113AA6C
        // reaches 31049028 to clear both pool collections.
        _byName.Clear();
        _byId.Clear();
        _ids.Reset();
    }

    private void EnsureNotLoading()
    {
        if (_loading)
            throw new InvalidOperationException("A navigation modifier load is already active on this owner.");
    }

    private static string NameKey(ReadOnlySpan<byte> name)
    {
        var zero = name.IndexOf((byte)0);
        return Convert.ToHexString(zero < 0 ? name : name[..zero]);
    }

    private static ZoneNavigationModifierLoadResult Unavailable(string areaPath, string? reason) =>
        new(ZoneNavigationModifierLoadStatus.UnavailableAsset, null,
            Array.AsReadOnly(new[] { reason is null ? $"Unable to open areas file {areaPath}." :
                $"Unable to open areas file {areaPath}: {reason}" }));
}
