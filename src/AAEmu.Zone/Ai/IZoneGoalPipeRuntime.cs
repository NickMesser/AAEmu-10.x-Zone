using System.Numerics;

namespace AAEmu.Zone.Ai;

/// <summary>Actor-bound services for pipe instantiation and traversal. All calls run in the actor phase.</summary>
public interface IZoneGoalPipeRuntime
{
    /// <summary>Create a fresh operation with this actor's services; unsupported operations must fail explicitly.</summary>
    IZoneGoalOperation CreateOperation(ZoneGoalDefinition definition);
    Vector3? GetAttentionTargetPosition();
    void RestoreArgument(object argument);
    void Notify(ZoneGoalPipeInstance pipe, ZoneGoalPipeEvent kind);
    void Diagnostic(string message);
}

public enum ZoneGoalPipeEvent { Deselected = 1, Finished = 2, Suspended = 3, Resumed = 4, Canceled = 5 }
public enum ZoneGoalPopStatus { End = 0, Goal = 1, Yield = 2 }

/// <summary>Actor services are bound at construction. Execute returns the native EGoalOpResult value.</summary>
public interface IZoneGoalOperation : IDisposable
{
    int Execute();
    void Reset();
}
