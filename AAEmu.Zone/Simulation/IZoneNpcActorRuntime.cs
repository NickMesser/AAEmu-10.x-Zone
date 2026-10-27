using System.Numerics;
using AAEmu.Zone.Scripting;

namespace AAEmu.Zone.Simulation;

/// <summary>Required actor and entity services, separate from Lua behavior execution.</summary>
public interface IZoneNpcActorRuntime
{
    // Distinct native flags: AI object +68 and +29 respectively.
    bool IsEnabled(ZoneNpc npc);
    bool CanReceiveSignals(ZoneNpc npc);
    bool HasSignalHandler(ZoneNpc npc);
    bool HasPipeUser(ZoneNpc npc);
    uint? GetEntityId(ZoneUnit unit);
    uint GetUnitObjectId(uint entityId);
    /// <summary>Returns an owned reference, or null when the entity has no script proxy/table.</summary>
    ZoneLuaReference? GetEntityScript(uint entityId);
    Vector3? GetEntityPosition(uint entityId);
    // 3100756C: actor event 2, recording, then waiting goal notification, without extra data.
    void SignalReceived(ZoneNpc npc, string name);
    float FrameTimeSeconds { get; }
    bool StartAuxiliaryBehavior(ZoneNpc npc, string name, int kind);
    bool IsAuxiliaryBehaviorFinished(ZoneNpc npc);
}
