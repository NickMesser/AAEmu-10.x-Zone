using AAEmu.Zone.Content;

namespace AAEmu.Zone.Simulation;

/// <summary>Clock, requirement and skill services used by the native NPC signal dispatcher.</summary>
public interface IZoneNpcSignalRuntime
{
    // Native current-time float seconds, not a conversion from the wrapping millisecond timer.
    float TimeSeconds { get; }
    uint DebugNpcObjectId { get; }
    string GetDebugUnitName(ZoneUnit unit);
    // 39361350 supplies the resolved signal target as subject and the NPC as the second unit.
    bool CheckRequirement(ZoneUnitRequirement requirement, ZoneUnit subject, ZoneUnit npc);
    // Eligibility runs before temporary targeting; execution sees npc.Unit.TargetId after the change.
    bool CanUseSkill(ZoneNpc npc, uint skillId);
    bool UseSkill(ZoneNpc npc, uint skillId);
    uint NextRandomUInt32();
}
