using System.Numerics;
using AAEmu.Zone.Ai;
using AAEmu.Zone.Combat;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.Npcs;

/// <summary>
/// Actor, spawn, group and spatial services required by NPC ownership. Implementations must
/// use loaded native content and actual actor state. No production implementation is supplied
/// until those systems are ready; callers cannot substitute missing services with permissive defaults.
/// </summary>
public interface IZoneNpcWorld
{
    uint TimeMilliseconds { get; }
    void ValidateCreation(NpcStateSnapshot snapshot);
    // Actor creation and creator-hostility/inherited-target behavior precede concrete spawner acknowledgement.
    void Initialize(ZoneNpc npc, ZoneUnit? creator);
    // Cleanup must tolerate partial initialization and repeated calls after an earlier failure.
    void Remove(ZoneNpc npc);
    void Died(ZoneNpc npc);
    // 3925FA70 writes faction state before updating the unit's AI actor, if one exists.
    void UpdateFaction(ZoneUnit unit);
    Vector3 GetPosition(ZoneUnit unit);
    Vector3 GetEncounterOrigin(ZoneNpc npc);
    // The actor script's current spawnPos; this can differ from the initial wire position.
    Vector3 GetSpawnPosition(ZoneNpc npc);
    void Relocate(ZoneNpc npc, Vector3 position);
    bool ReturnChecksEnabled { get; }
    bool IsInHousingArea(Vector3 position);
    bool IsOutsideTerritory(ZoneNpc npc, Vector3 position);
    bool IsNavigationAbuser(ZoneUnit target);
    float GetTerrainHeight(Vector3 position);
    bool IsSleeping(ZoneUnit unit);
    bool CanAcquire(ZoneNpc npc, ZoneUnit target);
    bool CanRetain(ZoneNpc npc, ZoneUnit target);
    bool ShouldCancel(ZoneNpc npc, ZoneAggroEntry entry, ZoneUnit target, uint now);
    void CheckAbusers(ZoneNpc npc, ZoneUnit target);
    // Preserve native traversal order. Return [npc.Id] when ungrouped; otherwise return the
    // current native sharing group's NPC members (aggro rule 2), including the owner if present.
    IReadOnlyList<uint> GetSharedAggroNpcIds(ZoneNpc npc);
    // 39321540: current summoned/group targets in range, including the slave attachment exclusion.
    IReadOnlyList<uint> GetAdditionalAggroTargetIds(ZoneNpc npc, ZoneUnit character);
    // 39321C80: spatial query, AI state, content link rules and faction/group checks.
    IReadOnlyList<uint> GetLinkedAggroNpcIds(ZoneUnit skillTarget, ZoneUnit? source, ZoneUnit unitInCharge);
    void AggroChanged(ZoneNpc npc, uint targetId);
    void AggroTargetChanged(ZoneNpc npc, uint previousTargetId);
    // 393482D0: entity and AI actor must both exist before any scheduled NPC work.
    bool HasAiActor(ZoneNpc npc);
    // A script/constructor-ready actor can exist before every gameplay-manager leaf is available.
    // Existing worlds keep their single actor gate; managed composition can report these separately.
    bool HasSignalActor(ZoneNpc npc) => HasAiActor(npc);
    bool HasPartialUpdateStep(ZoneNpc npc, ZoneNpcUpdateStep step) => step == ZoneNpcUpdateStep.AiTimeEvent;
    bool CanReplicateMovement(ZoneNpc npc) => HasAiActor(npc);
    // Required actor subsystems; the scheduler never replaces a missing subsystem with a no-op.
    void AcquireAggroTargets(ZoneNpc npc); // 39345770, including Timers.IgnoredTargets and current activation
    // Inputs for the concrete proximity/activation state owned by this exact NPC generation.
    IZoneNpcActivationServices GetActivationServices(ZoneNpc npc);
    IZoneNpcSignalRuntime SignalRuntime { get; }
    IZoneNpcAiRuntime AiRuntime { get; }
    // 392264D0 / 39CE6AA0: distance from the owner's physical proxy to the target's shapes.
    // The physics query may stop once a distance below stopDistance is found; never use unit centers as a fallback.
    float GetInteractionDistance(ZoneUnit owner, ZoneUnit target, float stopDistance);
    void UpdateFollowTarget(ZoneNpc npc); // 3933C270
    void UpdateCrowdBuff(ZoneNpc npc); // 39347060
    void CheckPosition(ZoneNpc npc); // 3933D0E0
    bool MovementReplicationEnabled { get; }
    void ReplicateMovement(ZoneNpc npc); // 393405B0, sampled after all other updates
}
