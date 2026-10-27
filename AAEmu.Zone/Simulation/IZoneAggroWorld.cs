using System.Numerics;

namespace AAEmu.Zone.Simulation;

public sealed record ZoneAggroTarget(uint ObjectId, Vector3 Position, int OutgoingModifier, bool Sleeping);

/// <summary>
/// Required actor, group and notification services for the native aggro table.
/// There is deliberately no permissive default for missing physics or eligibility.
/// Calls belong to the sequential simulation loop. Group synchronization must guard reentry.
/// </summary>
public interface IZoneAggroWorld
{
    uint TimeMilliseconds { get; }
    ZoneAggroTarget? FindTarget(uint objectId);
    Vector3 GetEncounterOrigin(ZoneAggroTable table);
    Vector3 GetOwnerPosition(ZoneAggroTable table);
    Vector3 GetSpawnPosition(ZoneAggroTable table);
    void RelocateOwner(ZoneAggroTable table, Vector3 position);
    // 393211E0: AI navigation service absent or nonzero state enables ordinary return checks.
    bool ReturnChecksEnabled { get; }
    bool IsInHousingArea(Vector3 position);
    bool IsOutsideTerritory(ZoneAggroTable table, Vector3 position);
    // 3931D890: navigation-type, forbidden-area and path-query checks on the target actor.
    bool IsNavigationAbuser(ZoneAggroTarget target);
    float GetTerrainHeight(Vector3 position);
    int GetIncomingModifier(ZoneAggroTable table);
    // 3931F040: exclude group members and apply 39261710, including stealth detection.
    bool CanAcquire(ZoneAggroTable table, ZoneAggroTarget target);
    // 393204B0: at least one NPC in the owner's group must pass 39261710.
    bool CanRetain(ZoneAggroTable table, ZoneAggroTarget target);
    // Terrain and actor-dependent cancellation from 393204B0; entry timestamps remain available.
    bool ShouldCancel(ZoneAggroTable table, ZoneAggroEntry entry, ZoneAggroTarget target, uint now);
    // Create/remove maintain the target's reverse NPC membership. Creation also emits hostility.
    void EntryCreated(ZoneAggroTable table, ZoneAggroEntry entry);
    void EntryRemoved(ZoneAggroTable table, uint targetId);
    // Selection flag changes only notify region observers; ordinary changes also synchronize groups.
    void EntryChanged(ZoneAggroTable table, uint targetId, bool synchronizeGroup);
    void TargetChanged(ZoneAggroTable table, uint previousTargetId);
    // Called with native one-second throttling; the actor service decides which abuser category applies.
    void CheckAbusers(ZoneAggroTable table, ZoneAggroTarget target);
    void AbuserRegistered(ZoneAggroTable table, uint targetId, int category);
    void AbusersCleared(ZoneAggroTable table);
    void AbusersUnregistered(ZoneAggroTable table, IReadOnlyList<uint> targetIds);
}
