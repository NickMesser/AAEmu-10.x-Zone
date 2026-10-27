using AAEmu.Zone.Content;
using AAEmu.Zone.Simulation;

namespace AAEmu.Zone.Startup;

/// <summary>Owns configured read-only sources in explicit priority order; no extraction or native startup.</summary>
public sealed class ZoneConfiguredAssets : IZoneAssetSource, IDisposable
{
    private readonly object _gate = new();
    private readonly ZoneRuntimeConfiguration _configuration;
    private readonly List<IDisposable> _owned = [];
    private readonly ZoneAssetSourceStack _sources;
    private bool _disposed;

    internal ZoneConfiguredAssets(ZoneRuntimeConfiguration configuration)
    {
        _configuration = configuration;
        var sources = new List<IZoneAssetSource>();
        try
        {
            foreach (var source in configuration.AssetSources)
            {
                IZoneAssetSource created = source.Kind == "directory"
                    ? new ZoneDirectoryAssetSource(source.Path) : new ZonePakAssetSource(source.Path);
                if (created is IDisposable owner) _owned.Add(owner);
                sources.Add(created);
            }
            _sources = new(sources.ToArray());
        }
        catch (Exception failure)
        {
            try { ReleaseSources(); }
            catch (Exception cleanup) { throw new AggregateException("Asset setup and cleanup failed.", failure, cleanup); }
            throw;
        }
    }

    public Stream? OpenRead(string virtualPath)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _sources.OpenRead(virtualPath);
        }
    }

    /// <summary>Match the database-resolved identity against exactly one authored world row.</summary>
    public ZoneWorldMapLayout LoadLayout(ZoneContent zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (zone.Key != _configuration.ZoneKey || !StringComparer.OrdinalIgnoreCase.Equals(zone.Name, _configuration.ZoneName))
            throw new ArgumentException("The resolved Zone does not match the configured asset owner.", nameof(zone));
        using var stream = OpenRead(_configuration.WorldMapAsset) ??
            throw new FileNotFoundException("The configured world map asset is missing.", _configuration.WorldMapAsset);
        var world = ZoneWorldMapContent.Load(stream, _configuration.WorldName);
        var matches = world.Zones.Where(row => row.Name == zone.Name && row.Id == zone.Key).ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException("World XML must contain exactly one Zone matching the resolved content name and key.");
        return new(world, matches[0]);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseSources();
        }
    }

    private void ReleaseSources()
    {
        List<Exception>? failures = null;
        for (var i = _owned.Count - 1; i >= 0; i--)
            try { _owned[i].Dispose(); }
            catch (Exception error) { (failures ??= []).Add(error); }
        _owned.Clear();
        if (failures is not null) throw new AggregateException("Asset-source cleanup failed.", failures);
    }
}
