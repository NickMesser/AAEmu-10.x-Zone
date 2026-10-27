using System.Numerics;

namespace AAEmu.Zone.Simulation;

/// <summary>Required actor services for the goal scheduling portion of CPipeUser::Update.</summary>
public interface IZoneGoalExecutionServices
{
    /// <summary>Borrowed from the actor's pipe controller, including changes made by goal callbacks.</summary>
    ZoneGoalPipeInstance? CurrentPipe { get; }
    /// <summary>The independent operation at native actor +FA0, borrowed from its owner.</summary>
    IZoneGoalOperation? AuxiliaryGoal { get; }
    uint NextRandom();
    /// <summary>3115AAA4 actor/world predicates. The executor handles active-goal and last-result predicates itself.</summary>
    bool EvaluateBranch(ZoneGoalParameters parameters);
    Vector3? GetAttentionTargetPosition();
    // Nonzero clear: pipe-user +48 with null target, then actor +290, in this order.
    void ClearAttentionTarget();
    void ResetActorAfterClear();
    void ReportSubPipeCount(int count, string rootName);
    void ReportActiveGoalCount(int count, string? rootName, ZoneGoalDefinition lastGoal);
}
