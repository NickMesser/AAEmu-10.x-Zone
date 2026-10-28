using AAEmu.Zone.Content;
using AAEmu.Zone.Units;
using AAEmu.Zone.WorldLink;

namespace AAEmu.Zone.Scene;

/// <summary>
/// Managed level completion over authored data. Terrain is required; unsupported scene systems
/// are reported separately and never represented as a successful native scene activation.
/// The resource scope owns the activation and the physics backend created by the ready callback.
/// </summary>
public sealed class ZoneManagedSceneLoadingServices(
    ZoneUnitRegistry registry, ZoneStartupState startup, ZoneManagedSceneActivation activation,
    ZoneSceneContent content, int zoneKey, ZoneSceneReadinessNotes notes,
    Action terrainReady, Action<string>? trace = null, Func<string>? scriptingStatus = null,
    Func<string>? navigationStatus = null) : IZoneSceneLoadingServices
{
    private bool _loaded, _disposed;
    public ZoneUnitRegistry Registry { get; } = registry ?? throw new ArgumentNullException(nameof(registry));
    public int ServerType => startup.WorldName is { } world ? ZoneServerType.For(world) : 2;
    public bool EnvironmentFlag150 => false;
    public ZoneSpatialRegionBounds? AiBounds { get; private set; }

    public ValueTask LoadLevelAsync(ZoneWorldMapLayout layout, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_loaded) throw new InvalidOperationException("This managed level already loaded.");
        activation.Create();
        var report = activation.Load();
        if (!report.TerrainReady)
            throw new InvalidDataException($"Managed terrain did not complete for zone {zoneKey}.");
        cancellationToken.ThrowIfCancellationRequested();
        // Spawner completion queries the backend, so publish it before those phases begin.
        terrainReady();
        _loaded = true;
        notes.AddOnce("managed-static-collision",
            $"Managed scene: terrain and supported default brush proxies are loaded; " +
            $"{activation.Collision!.Report.UnsupportedBrushCount} brushes remain unsupported. Vegetation, entity, rigid-body and vehicle collision remain incomplete.");
        notes.AddOnce("managed-scripting",
            scriptingStatus?.Invoke() ?? "Managed scene: authored Lua behavior and FlowGraph execution are not attached; no Lua or CryEngine DLL is loaded.");
        if (navigationStatus is not null) notes.AddOnce("managed-navigation", navigationStatus());
        trace?.Invoke($"Managed level loaded: zone={layout.Zone.Id}, cells={layout.Cells.Count}, terrainReady={report.TerrainReady}");
        return ValueTask.CompletedTask;
    }

    public void RequestStaticGimmickSpawns()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        notes.AddOnce("managed-static-gimmicks",
            "Static gimmick placement and execution from level entity data remain unimplemented.");
    }

    public void InitializeDetailedTimeOfDay()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (startup.TimeOfDay is not null)
            Registry.QueueNotification(startup.TimeOfDayState.CreateInitialReport());
        else notes.AddOnce("time-of-day-join", "Initial time report awaits World's WZTimeOfDay packet.");
    }

    public void InitializeAreas()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        trace?.Invoke(content.Areas.TryGetValue(zoneKey, out var area)
            ? $"Managed area initialized: zone={zoneKey}, area={area.Id}, group={area.GroupId}, faction={area.FactionId}"
            : $"Managed area initialized: zone={zoneKey}, no content area row; relationship policy uses group 0.");
    }

    public void InitializeAiBounds(ZoneSpatialRegionBounds bounds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AiBounds = bounds;
        trace?.Invoke($"Managed scene bounds published for zone {zoneKey}; " +
            (navigationStatus?.Invoke() ?? "navigation mesh queries remain unavailable."));
    }

    public void Dispose() => _disposed = true;
}
