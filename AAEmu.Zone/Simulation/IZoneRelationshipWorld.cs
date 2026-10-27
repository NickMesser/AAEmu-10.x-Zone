using AAEmu.Zone.Protocol.Messages;

namespace AAEmu.Zone.Simulation;

public sealed record ZoneRelationshipArea(ushort GroupId, uint FactionId, bool Integration);

/// <summary>
/// Scene state required by the native ServerUnit relationship evaluator. Implementations must
/// resolve content, current World configuration and siege geometry; unknown state must throw.
/// ZoneRelationshipScene supplies content, join and siege state; runtime exclusions remain required.
/// </summary>
public interface IZoneRelationshipWorld
{
    // ServerUnit's 3935C690 reads the current scene key, never a unit's world/region ID.
    int ZoneKey { get; }
    ZoneRelationshipArea? Area { get; }
    bool IsBattlefield { get; }
    // 3932C580: current join PVP configuration, otherwise the group's content policy.
    bool BlockOwnedUnitCombat { get; }
    ushort ConflictZoneGroupId { get; }
    int ConflictState { get; }
    ZoneSiegeState SiegeState { get; }
    bool ChangeConflictState(ConflictZoneState message);
    // 39B3D830: conflict_zones.war_chaos for this group; false for a missing definition.
    bool IsWarChaos(ushort groupId);
    // 39C53B50 and 39B3D6D0: content membership, runtime exclusions and festival-zone presence.
    bool IsGuardProtectionActive(ushort groupId);
    // 3932C290: siege period, both positions, participation and attacker/defender rules.
    ZoneRelation GetSiegeRelation(ZoneUnit source, ZoneUnit target);
}
