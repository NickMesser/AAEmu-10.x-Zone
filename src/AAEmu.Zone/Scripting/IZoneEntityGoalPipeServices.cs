using AAEmu.Zone.Ai;

namespace AAEmu.Zone.Scripting;

/// <summary>Borrowed entity view; its AI object is also the identity passed as a pipe argument.</summary>
public interface IZoneGoalPipeScriptEntity
{
    string Name { get; }
    object? GetAiObject();
}

/// <summary>Required scene lookups for entity pipe methods. Calls run in the owning actor phase.</summary>
public interface IZoneEntityGoalPipeServices
{
    IZoneGoalPipeScriptEntity? FindEntity(uint entityId);
    ZoneGoalPipeController? GetPipeUser(object aiObject);
    // CPipeUser +70, with its native float argument zero. May update formation/target state.
    object? ResolvePipeTarget(ZoneGoalPipeController pipeUser, string name);
    // AISystem +A8, object type zero, after the actor's special-target lookup fails.
    object? FindAiObject(string name);
    void Warning(string message);
}
