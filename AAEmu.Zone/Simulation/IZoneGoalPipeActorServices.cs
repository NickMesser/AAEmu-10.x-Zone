using System.Numerics;

namespace AAEmu.Zone.Simulation;

[Flags]
public enum ZoneGoalPipeFlags
{
    Loop = 0, RunOnce = 1, NoDuplicate = 2, HighPriority = 4, SamePriority = 8,
    DontResetAnimationGraph = 16, KeepLastSubPipe = 32, DontResetStrafe = 64
}

/// <summary>Engine services used by the owning pipe controller and its executor. Calls run in the actor phase.</summary>
public interface IZoneGoalPipeActorServices
{
    IZoneGoalOperation CreateOperation(ZoneGoalDefinition definition, ZoneGoalExecutor executor);
    IZoneGoalOperation? AuxiliaryGoal { get; }
    uint NextRandom();
    bool EvaluateBranch(ZoneGoalParameters parameters);
    Vector3? GetAttentionTargetPosition();
    void SetArgument(object? argument);
    void ClearAttentionTarget();
    void ResetActorAfterClear();
    // SelectPipe: entity lookup and 310018AC precede even the same-name early return.
    void PreparePipeSelection();
    // Puppet strafe/movement resets, optional animation-graph reset, then 3115D2B4 path cleanup.
    void ResetForPipeSelection(ZoneGoalPipeFlags flags);
    // 31160594: enable signals, clear actor +C74, then pipe-user virtual +B0.
    void ResetAfterPipeClear();
    void EnableSignals();
    // SelectPipe clears actor +948 and, after actor event delivery, the map at actor +910.
    void ClearSelectionTarget();
    void ClearSelectionControls();
    void RecordPipeEvent(int kind, string? name);
    void ActorPipeEvent(int kind, string? name);
    void Diagnostic(string message);
    void ReportSubPipeCount(int count, string rootName, string? insertingName);
    void ReportActiveGoalCount(int count, string? rootName, ZoneGoalDefinition lastGoal);
    void ReportInsertionChain(IReadOnlyList<ZoneGoalPipeInstance> chain);
}
