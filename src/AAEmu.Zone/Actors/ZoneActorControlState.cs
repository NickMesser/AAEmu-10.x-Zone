using System.Numerics;

namespace AAEmu.Zone.Actors;

public readonly record struct ZoneStrafeDistances(float Start, float End, bool MeansDirect);

// Character-system records store the end distance before the start distance and use flag bit zero.
public readonly record struct ZoneCharacterStrafeSettings(float End, float Start, byte Flags);

/// <summary>
/// Mutable actor controls shared by goals and the enclosing actor update. The owning actor supplies its initial state.
/// Field meanings follow 3117147C (puppet serialization) and 311606D4 (pipe-user serialization).
/// </summary>
public sealed class ZoneActorControlState
{
    public bool SignalsEnabled { get; set; }
    public bool LooseAttention { get; set; }
    public int BodyState { get; set; }
    public float MovementUrgency { get; set; }
    // COPRun writes both actor +4A8 and +4AC; keep their independent storage for subsequent actor updates.
    public float GoalMovementUrgency { get; set; }
    public Vector3 MovementDirection { get; set; }
    public float DesiredSpeed { get; set; }
    public bool AllowStrafing { get; set; }
    public float DistanceToPathEnd { get; set; }
    public Vector3 PathStartPosition { get; set; }
    public ZoneStrafeDistances AllowedStrafeDistances { get; set; }
    public float StrafeStartDistance { get; set; }
    public float AdaptiveUrgencyMin { get; set; }
    public float AdaptiveUrgencyMax { get; set; }
    public float AdaptiveUrgencyScaleDownPathLength { get; set; }
    public float AdaptiveUrgencyMaxPathLength { get; set; }
    public int DelayedStance { get; set; }
    public int DelayedStanceMovementCounter { get; set; }

    // 3116771C, 3116783C and 3116786C also reset the progress tracked by the actor update.
    public void SetStrafeDistances(ZoneStrafeDistances distances)
    { AllowedStrafeDistances = distances; StrafeStartDistance = 0; }

    public void SetAdaptiveUrgency(float minimum, float maximum, float scaleDownPathLength)
    {
        AdaptiveUrgencyMin = minimum; AdaptiveUrgencyMax = maximum;
        AdaptiveUrgencyScaleDownPathLength = scaleDownPathLength; AdaptiveUrgencyMaxPathLength = 0;
    }

    public void SetDelayedStance(int stance)
    { DelayedStance = stance; DelayedStanceMovementCounter = 0; }
}

/// <summary>Required scene lookups and target ownership for actor-control goals.</summary>
public interface IZoneActorControlGoalServices
{
    ZoneActorControlState Controls { get; }
    bool IsPuppet { get; }
    object? Argument { get; }
    // Actor virtual +178 with its third (float) argument zero; preserve scene-specific target resolution.
    object? ResolveTarget(string name);
    // Pipe-user virtual +48 with its final flag false, including when target is null.
    void SetAttentionTarget(object? target);
    object? GetCharacterSystem();
    object? GetActorProxy();
    uint GetCharacterKey(object proxy);
    ZoneCharacterStrafeSettings? GetCharacterStrafeSettings(object characterSystem, uint key);
}
