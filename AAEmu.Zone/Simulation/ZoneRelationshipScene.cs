using AAEmu.Zone.Content;
using AAEmu.Zone.Protocol.Messages;

namespace AAEmu.Zone.Simulation;

/// <summary>Content and join-derived scene state, with connection-local World conflict updates.</summary>
public sealed class ZoneRelationshipScene : IZoneRelationshipWorld
{
    private readonly ZoneSceneContent _content;
    private readonly IZoneRelationshipRuntime _runtime;
    private readonly IReadOnlySet<ushort> _activeFestivals;
    public int ZoneKey { get; }
    public ZoneRelationshipArea? Area { get; }
    public bool IsBattlefield { get; }
    public bool BlockOwnedUnitCombat { get; }
    public ZoneConflictState Conflict { get; }
    public ZoneSiegeState SiegeState { get; }
    public ushort ConflictZoneGroupId => Conflict.GroupId;
    public int ConflictState => Conflict.State;

    public ZoneRelationshipScene(ZoneSceneContent content, int zoneKey, ZoneJoinResponse join,
        ulong contentLoadedAtUnixSeconds, int contentDateOffsetMinutes, ZoneWorldContent worldContent,
        WorldContentNameComparer? contentNameComparer = null)
        : this(content, zoneKey, join, contentLoadedAtUnixSeconds, contentDateOffsetMinutes,
            new ZoneWorldContentPolicy(worldContent, WorldContentFilter.Read(join.Filter.Span, contentNameComparer)))
    {
    }

    public ZoneRelationshipScene(ZoneSceneContent content, int zoneKey, ZoneJoinResponse join,
        ulong contentLoadedAtUnixSeconds, int contentDateOffsetMinutes, IZoneRelationshipRuntime runtime)
    {
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        ZoneKey = zoneKey;
        if (content.Areas.TryGetValue(zoneKey, out var area)) Area = new(area.GroupId, area.FactionId, area.Integration);
        var groupId = Area?.GroupId ?? 0;
        IsBattlefield = content.BattlefieldKeys.Contains(zoneKey);
        // 3932C580: a matching nonzero join group overrides the indun definition.
        BlockOwnedUnitCombat = groupId != 0 && join.Type == groupId ? !join.Pvp
            : content.PvpPolicies.TryGetValue(groupId, out var policy) && !policy.Pvp;
        Conflict = new(groupId);
        SiegeState = new(groupId);
        _activeFestivals = content.ActiveFestivalGroups(contentLoadedAtUnixSeconds, contentDateOffsetMinutes);
    }

    public bool ChangeConflictState(ConflictZoneState message) => Conflict.Apply(message);
    public bool IsWarChaos(ushort groupId) => _content.Conflicts.TryGetValue(groupId, out var policy) && policy.WarChaos;
    public bool IsGuardProtectionActive(ushort groupId) => !_runtime.IsFestivalGuardExcluded(groupId) &&
        _activeFestivals.Contains(groupId) && _content.Festivals.ContainsKey(groupId);
    public ZoneRelation GetSiegeRelation(ZoneUnit source, ZoneUnit target) => SiegeState.GetRelation(source, target);
}
