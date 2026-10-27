namespace AAEmu.Zone.Npcs;

public enum ZoneNpcUpdateStep
{
    RefreshAggro, ProcessReturn, RefreshAbusers, SelectTarget, AiTimeEvent, AcquireTargets,
    Proximity, Activation, Cooldowns, IgnoredTargets, Creator, Talks, FollowTarget, CrowdBuff, Position
}

/// <summary>393482D0 / 3933B490: ordered work for one engine frame, after the actor gate.</summary>
public static class ZoneNpcUpdateSchedule
{
    public static IEnumerable<ZoneNpcUpdateStep> Steps(uint frame, byte phase, bool dead, bool aggroEnabled)
    {
        var offset = (byte)frame ^ phase;
        if (!dead && aggroEnabled)
        {
            if ((offset & 7) == 0) yield return ZoneNpcUpdateStep.RefreshAggro;
            if ((offset & 63) == 0)
            {
                yield return ZoneNpcUpdateStep.ProcessReturn;
                yield return ZoneNpcUpdateStep.RefreshAbusers;
            }
            yield return ZoneNpcUpdateStep.SelectTarget;
        }
        if ((offset & 3) == 0 && !dead)
        {
            yield return ZoneNpcUpdateStep.AiTimeEvent;
            yield return ZoneNpcUpdateStep.AcquireTargets;
        }
        if ((offset & 7) == 0)
        {
            yield return ZoneNpcUpdateStep.Proximity;
            yield return ZoneNpcUpdateStep.Activation;
            yield return ZoneNpcUpdateStep.Cooldowns;
        }
        if ((offset & 15) == 0 && !dead) yield return ZoneNpcUpdateStep.IgnoredTargets;
        if ((offset & 63) == 0 && !dead)
        {
            yield return ZoneNpcUpdateStep.Creator;
            yield return ZoneNpcUpdateStep.Talks;
            yield return ZoneNpcUpdateStep.FollowTarget;
            yield return ZoneNpcUpdateStep.CrowdBuff;
        }
        if ((offset & 127) == 0) yield return ZoneNpcUpdateStep.Position;
    }
}
