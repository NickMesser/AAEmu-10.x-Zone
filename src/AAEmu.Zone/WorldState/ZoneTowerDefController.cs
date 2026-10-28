using AAEmu.Zone.Content;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;

namespace AAEmu.Zone.WorldState;

/// <summary>
/// Native TowerDef manager (globals qword_3A638E60 = playability map, qword_3A638E80 = running map).
/// The playability map is rebuilt by Reload (3935A140): for every tower with at least one phase and a
/// nonzero primary spawner type it holds one "spot" per physical spawner of that type in this zone.
/// World asks with QueryPlayability (393591D0); the zone answers ZWTowerDefReportPlayability only when
/// the tower has spots AND is not already running, otherwise it stays silent (logging "playing already").
/// Start (39359690) / End (39359A50) / WaveStart (39359340) validate the spot index and then drive the
/// spot's spawner; this replacement records the running set and current wave but does not yet fire the
/// native spawner event (type 5) into the spot's spawner.
/// </summary>
public sealed class ZoneTowerDefController
{
    private readonly Func<uint, int> _spotCounter;
    private readonly Dictionary<TowerDefKey, int> _running = [];
    private readonly HashSet<string> _logged = [];
    private ZoneNpcSpawnerControllerContent? _content;
    private Dictionary<uint, uint> _primaryByTower = [];

    /// <param name="spotCounter">Number of physical spawners of a spawner type in the current scene generation.</param>
    public ZoneTowerDefController(Func<uint, int> spotCounter)
    { _spotCounter = spotCounter ?? throw new ArgumentNullException(nameof(spotCounter)); }

    public bool IsReady { get; private set; }

    /// <summary>Tower defs currently running, with the last wave step World opened (-1 before any WaveStart).</summary>
    public IReadOnlyDictionary<TowerDefKey, int> Running => _running;

    /// <summary>Last diagnostic, kept for evidence and tests.</summary>
    public string? LastDiagnostic { get; private set; }

    /// <summary>Native Reload: rebuild the tower index from controller content. Running towers are untouched.</summary>
    public void Reload(TowerDefReload _) => Reload(_content);

    private void Reload(ZoneNpcSpawnerControllerContent? content)
    {
        _content = content;
        _primaryByTower = content is null ? [] : content.Towers
            .Where(tower => tower.Phases.Count >= 1 && tower.Definition.PrimarySpawnerType != 0)
            .ToDictionary(tower => tower.Definition.Id, tower => tower.Definition.PrimarySpawnerType);
    }

    internal void BeginReload(ZoneNpcSpawnerControllerContent? content) { IsReady = false; Reload(content); }
    internal void CompletePostLoad() => IsReady = true;
    internal void Retire() { IsReady = false; _running.Clear(); }

    /// <summary>The playable spot count of a tower in this zone; zero when unknown or without spots.</summary>
    public int PlayableSpots(uint towerDefId) =>
        IsReady && _primaryByTower.TryGetValue(towerDefId, out var type) ? _spotCounter(type) : 0;

    /// <summary>Native 393591D0. Returns the ZW reply, or null when the zone stays silent.</summary>
    public ZoneFrame? QueryPlayability(TowerDefQueryPlayability query)
    {
        var spots = PlayableSpots(query.Key.TowerDefId);
        if (spots == 0) return null;
        if (_running.ContainsKey(query.Key))
        {
            Note($"TowerDef({query.Key.TowerDefId},{query.Key.ZoneGroup}): The tower def is playing already!");
            return null;
        }
        return new TowerDefReportPlayability(query.Key, (uint)spots).ToFrame();
    }

    /// <summary>Native 39359690. Returns true when the tower was recorded as running.</summary>
    public bool Start(TowerDefSpotMessage message)
    {
        var key = message.Key;
        if (_running.ContainsKey(key)) return Note($"TowerDef({key.TowerDefId},{key.ZoneGroup}): The tower def is playing already!");
        if (!_primaryByTower.ContainsKey(key.TowerDefId))
            return Note($"TowerDef({key.TowerDefId},{key.ZoneGroup}): Failed to find playability info");
        if (message.SpotIndex < 0 || message.SpotIndex >= PlayableSpots(key.TowerDefId))
            return Note($"TowerDef({key.TowerDefId},{key.ZoneGroup}): spot {message.SpotIndex} is out of range");
        _running[key] = -1;
        return true;
    }

    /// <summary>Native 39359A50. Returns true when a running tower was ended.</summary>
    public bool End(TowerDefSpotMessage message) =>
        _running.Remove(message.Key) || Note($"TowerDef({message.Key.TowerDefId},{message.Key.ZoneGroup}): is not playing");

    /// <summary>Native 39359340. Returns true when a running tower advanced to the wave step.</summary>
    public bool WaveStart(TowerDefWaveStart message)
    {
        if (!_running.ContainsKey(message.Key))
            return Note($"TowerDef({message.Key.TowerDefId},{message.Key.ZoneGroup}): is not playing");
        _running[message.Key] = message.Step;
        return true;
    }

    private bool Note(string text)
    {
        LastDiagnostic = text;
        if (_logged.Add(text)) Console.WriteLine(text);
        return false;
    }
}
