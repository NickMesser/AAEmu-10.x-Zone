using System.Numerics;
using AAEmu.Zone.Scripting;

namespace AAEmu.Zone.Simulation;

/// <summary>A borrowed live entity view, including entities outside the NPC actor registry.</summary>
public interface IZoneActorEntityView : IZoneGoalPipeScriptEntity
{
    uint EntityId { get; }
    ZoneUnit? Unit { get; }
    Vector3? GetPosition();
    /// <summary>Returns an owned Lua reference, or null when this entity has no script table.</summary>
    ZoneLuaReference? RetainScript();
}

/// <summary>
/// Required scene operations around adopted NPC actors. Entity allocation, native actor construction,
/// generated AI objects, physics, pathfinding and phase scheduling remain the scene's responsibility.
/// External entity lookups must exclude actors owned by this registry and preserve exact Unit identity.
/// </summary>
public interface IZoneNpcActorScene
{
    IZoneActorEntityView? FindExternalEntity(uint entityId);
    IZoneActorEntityView? FindExternalEntity(ZoneUnit unit);
    ZoneGoalPipeController? GetExternalPipeUser(object aiObject);
    Vector3? GetEntityPosition(ZoneNpcActorEntry actor);
    bool HasSignalHandler(ZoneNpcActorEntry actor);
    // 3100756C: actor event 2, then recorder event 2, then the owned executor's wait notification.
    void ActorSignalEvent(ZoneNpcActorEntry actor, string name);
    void RecordSignal(ZoneNpcActorEntry actor, string name);
    // CAIProxy 30519920 reads the actual handler's alertness before deciding its distance cadence.
    int GetActorAlertness(ZoneNpcActorEntry actor);
    // CAISystem virtual+38, sampled after the proxy's positive countdown decreases.
    bool GlobalAiUpdateAlways { get; }
    void SetEntityUpdatePolicy(ZoneNpcActorEntry actor, int policy);
    // IAIObject::Event 6/7 with null data. The complete actor event implementation remains a scene service.
    void ActorActivationEvent(ZoneNpcActorEntry actor, int eventId);
    float FrameTimeSeconds { get; }
    bool StartAuxiliaryBehavior(ZoneNpcActorEntry actor, string name, int kind);
    bool IsAuxiliaryBehaviorFinished(ZoneNpcActorEntry actor);
    object? ResolvePipeTarget(ZoneGoalPipeController pipeUser, string name);
    object? FindAiObject(string name);
    void Warning(string message);
}
