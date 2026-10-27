using System.Numerics;
using System.Runtime.CompilerServices;
using AAEmu.Zone.Ai;
using AAEmu.Zone.Content;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Scripting;

namespace AAEmu.Zone.Npcs;

/// <summary>3934B7A0 pathName/pointNo; pointNo is matched against spawn metadata's PartIndex.</summary>
public sealed record ZoneNpcNamedSpawnPath(string Name, int PointNumber);
/// <summary>The complete meaningful116-byte anchor row serialized by3934B7A0.</summary>
public sealed record ZoneNpcSpawnAnchor(string Name, int AnchorType, Vector3 Position);

public sealed class ZoneNpcInitialSpawnContext
{
    public static ZoneNpcInitialSpawnContext Empty { get; } = new([], []);
    public IReadOnlyList<ZoneNpcNamedSpawnPath> Paths { get; }
    public IReadOnlyList<ZoneNpcSpawnAnchor> Anchors { get; }

    public ZoneNpcInitialSpawnContext(IEnumerable<ZoneNpcNamedSpawnPath> paths, IEnumerable<ZoneNpcSpawnAnchor> anchors)
    {
        ArgumentNullException.ThrowIfNull(paths); ArgumentNullException.ThrowIfNull(anchors);
        Paths = Array.AsReadOnly(paths.Select(path =>
        {
            ArgumentNullException.ThrowIfNull(path); ArgumentNullException.ThrowIfNull(path.Name);
            return path with { Name = ZoneAiSignalName.CString(path.Name) };
        }).ToArray());
        Anchors = Array.AsReadOnly(anchors.Select(anchor =>
        {
            ArgumentNullException.ThrowIfNull(anchor); ArgumentNullException.ThrowIfNull(anchor.Name);
            return anchor with { Name = ZoneAiSignalName.CString(anchor.Name) };
        }).ToArray());
    }
}

public interface IZoneNpcInitialSpawnServices
{
    // Entity -> AI object -> virtual208. Null represents a missing entity or AI object;
    // this metadata can differ from the NPC's original spawn group.
    NpcSpawnGroup? GetCurrentAiGroup(ZoneNpc npc);
    uint NextRandom();
    uint FindPathType(string name); // AI virtual1C8,31058598.
    /// <summary>Returns an owned reference to the current entity's script table, or null.</summary>
    ZoneLuaReference? RetainEntityScript(ZoneNpc npc);
    void ScriptError(ZoneNpc npc, string reason);
}

/// <summary>
///39347B20 initial path/anchor behavior after member admission. Matching named paths take
/// precedence even for a group member that cannot lead movement. An absent matching path appends
/// fallback anchors and runs the concrete type5 command. Anchor selection/movement after the
/// script calls NpcGotoAnchor is a separate phase, not an opaque operation implemented here.
/// </summary>
public sealed class ZoneNpcInitialSpawnBehavior : IDisposable
{
    private readonly ZoneNpcSimulation _npcs;
    private readonly ZoneNpcContent _content;
    private readonly IZoneNpcInitialSpawnServices _services;
    private readonly Dictionary<ZoneNpc, List<ZoneNpcSpawnAnchor>> _anchors = [];
    private readonly Dictionary<ZoneNpc, object> _applications = [];
    private readonly ConditionalWeakTable<ZoneNpc, object> _retired = new();
    public bool IsDisposed { get; private set; }

    /// <summary>
    /// Named AI path-type refusals (TODO(path-finder)) counted per spawn. The dependency stays
    /// unresolved and visible; the count is reported at readiness beside the spawner outcome.
    /// </summary>
    public int PathResolutionRefusalCount { get; private set; }

    /// <summary>The first named path-type refusal message, reported verbatim at readiness.</summary>
    public string? FirstPathResolutionRefusal { get; private set; }

    public ZoneNpcInitialSpawnBehavior(ZoneNpcSimulation npcs, ZoneNpcContent content, IZoneNpcInitialSpawnServices services)
    {
        _npcs = npcs ?? throw new ArgumentNullException(nameof(npcs));
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    internal bool BelongsTo(ZoneNpcSimulation npcs) => ReferenceEquals(_npcs, npcs);
    private bool IsCurrent(ZoneNpc npc) => !IsDisposed && !_npcs.IsStopped && !npc.Activation.IsDisposed && !_retired.TryGetValue(npc, out _) &&
        ReferenceEquals(_npcs.FindByObjectId(npc.ObjectId), npc);
    public IReadOnlyList<ZoneNpcSpawnAnchor> GetAnchors(ZoneNpc npc) =>
        _anchors.TryGetValue(npc, out var anchors) ? anchors.ToArray() : [];

    public void Apply(ZoneNpcSpawnerState scheduler, ZoneNpc npc, ZoneNpcInitialSpawnContext context, Func<bool> current)
    {
        ArgumentNullException.ThrowIfNull(scheduler); ArgumentNullException.ThrowIfNull(npc);
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(current);
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (!scheduler.BelongsTo(_npcs)) throw new ArgumentException("The scheduler belongs to another NPC scene.", nameof(scheduler));
        if (!IsCurrent(npc) || scheduler.IsDisposed || !current() || !IsCurrent(npc) || scheduler.IsDisposed) return;
        var application = new object();
        _applications[npc] = application;
        bool IsOwned() => IsCurrent(npc) && !scheduler.IsDisposed &&
            _applications.TryGetValue(npc, out var active) && ReferenceEquals(active, application);
        bool IsActive() => IsOwned() && current() && IsOwned();
        try
        {
            var matches = context.Paths.Where(path => path.PointNumber == npc.Spawn.PartIndex).ToArray();
            if (matches.Length != 0)
            {
                var group = _services.GetCurrentAiGroup(npc);
                if (!IsActive()) return;
                if (group is { Type: not 0 } metadata && _content.Groups.TryGetValue(metadata.Type, out var definition))
                {
                    // Native indexes directly. Reject invalid scene metadata rather than reading outside the row vector.
                    if (metadata.MemberIndex >= definition.Members.Count)
                        throw new InvalidDataException("The current AI group member is outside its content definition.");
                    if (!definition.Members[metadata.MemberIndex].IsMoveLeader) return;
                }
                var random = _services.NextRandom();
                if (!IsActive()) return;
                var chosen = matches[(int)(((ulong)random * (uint)matches.Length) >> 32)];
                uint type;
                try
                {
                    type = _services.FindPathType(chosen.Name);
                }
                catch (NotSupportedException refusal)
                {
                    // An unresolved path-type ABI is a NAMED dependency refusal for this one spawn,
                    // not a session fault: the original run proved that letting it escape tears down
                    // the whole zone (the first frame's initial spawn retired the scene and World
                    // logged "Zone TCP disconnect" 40 s after ZWZoneLoaded). The refusal is counted
                    // and its first message is kept for the readiness partial-leaf table, so the
                    // dependency stays visible and nothing is stubbed or silently skipped.
                    PathResolutionRefusalCount++;
                    FirstPathResolutionRefusal ??= refusal.Message;
                    return;
                }
                if (!IsActive()) return;
                scheduler.QueueDeferredCommand(npc, chosen.Name, type == 0 ? 3u : type, npc.Spawn.SpawningEffectTime);
                return;
            }
            if (context.Anchors.Count == 0) return;
            if (!_anchors.TryGetValue(npc, out var owned)) _anchors.Add(npc, owned = []);
            //39345470 appends; repeated calls retain the previous anchors, including duplicates.
            owned.AddRange(context.Anchors);
            RunCommand(npc, false, "", 0, IsActive);
        }
        finally
        {
            if (_applications.TryGetValue(npc, out var active) && ReferenceEquals(active, application))
                _applications.Remove(npc);
        }
    }

    /// <summary>39341BD0, used by the actual scheduler once its owned countdown expires.</summary>
    public void RunDeferredAiCommand(ZoneNpc npc, string text, uint parameter, Func<bool> current)
    {
        ArgumentNullException.ThrowIfNull(npc); ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(current);
        bool IsActive() => IsCurrent(npc) && current() && IsCurrent(npc);
        if (IsActive()) RunCommand(npc, true, text, parameter, IsActive);
    }

    private void RunCommand(ZoneNpc npc, bool path, string text, uint parameter, Func<bool> current)
    {
        // Managed callback policy: capture the actor at execution, so a later actor may still
        // receive a queued command, but retiring this actor stops its active script operation.
        var actorGeneration = npc.AiSignals.ActorGeneration;
        bool IsActorCurrent() => !npc.AiSignals.IsDisposed && npc.AiSignals.ActorGeneration == actorGeneration;
        bool IsActive() => IsActorCurrent() && current() && IsActorCurrent();
        using var entity = _services.RetainEntityScript(npc);
        if (!IsActive()) return;
        if (entity is null) { _services.ScriptError(npc, "The NPC has no entity script table."); return; }
        if (!entity.IsTable) throw new InvalidDataException("The current NPC entity script reference must be a table.");
        ZoneNpcSpawnCommands.Run(entity, path, text, parameter, IsActive, reason => _services.ScriptError(npc, reason));
    }

    public void Remove(ZoneNpc npc)
    {
        ArgumentNullException.ThrowIfNull(npc);
        _anchors.Remove(npc);
        _applications.Remove(npc);
        _retired.GetValue(npc, _ => new());
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        _anchors.Clear();
        _applications.Clear();
        _retired.Clear();
    }
}
