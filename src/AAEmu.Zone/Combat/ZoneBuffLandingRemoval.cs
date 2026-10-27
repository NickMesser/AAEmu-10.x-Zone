using AAEmu.Zone.Content;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Protocol.Units;

namespace AAEmu.Zone.Combat;

/// <summary>CActor ground/water timers (+94/+9C) and gliding startup seconds (+1BC).</summary>
public readonly record struct ZoneBuffLandingPhysicalState(float GroundedSeconds, float InWaterSeconds,
    float GlidingStartupSeconds);

public interface IZoneBuffLandingPhysics
{
    /// <summary>
    /// Query the owning unit model's current actor state. Null means its native physical getter has
    /// no actor; a configured implementation must supply actual physics rather than inferred contact.
    /// </summary>
    ZoneBuffLandingPhysicalState? GetPhysicalState(uint objectId);
}

/// <summary>The remove-on-land decision and request branch of ZoneBuffMan::Update, 3944FC90.</summary>
public static class ZoneBuffLandingRemoval
{
    /// <summary>
    /// Evaluate one buff after its elapsed/tick update and modifier invalidations. This neither advances
    /// timers nor removes the retained instance; World acknowledges removal through WZBuffRemoved.
    /// </summary>
    public static BuffRemovalRequest? Evaluate(uint objectId, ZoneUnitKind kind, UnitBuff alreadyAdvancedBuff,
        ZoneBuffContent content, IZoneBuffLandingPhysics physics)
    {
        ArgumentNullException.ThrowIfNull(alreadyAdvancedBuff);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(physics);
        if (!content.Templates.TryGetValue(alreadyAdvancedBuff.TemplateId, out var template) ||
            !template.RemoveOnLand || kind != ZoneUnitKind.Npc) return null;

        // 39258830 -> ActorUnitModel virtual +488 (392A57E0). Native queries even below the time limit.
        var state = physics.GetPhysicalState(objectId);
        if (state is not { } physical) return null;
        var threshold = template.Gliding ? GlidingThreshold(physical.GlidingStartupSeconds) : 200u;
        if (alreadyAdvancedBuff.Elapsed <= threshold ||
            !(physical.InWaterSeconds > 0.1f || physical.GroundedSeconds > 0.1f)) return null;
        return new(objectId, alreadyAdvancedBuff.Index, alreadyAdvancedBuff.TemplateId, 9);
    }

    /// <summary>
    /// Request removals from already-advanced good, bad, then hidden instances, querying and sending
    /// separately for each candidate. This is only the landing pass. A combined native update must call
    /// Evaluate between each buff's timer invalidations and the next buff, rather than batch both phases.
    /// </summary>
    public static int RequestRemovals(uint objectId, ZoneUnitKind kind, UnitBuffs alreadyAdvancedBuffs,
        ZoneBuffContent content, IZoneBuffLandingPhysics physics, Action<BuffRemovalRequest> send)
    {
        ArgumentNullException.ThrowIfNull(alreadyAdvancedBuffs);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(physics);
        ArgumentNullException.ThrowIfNull(send);
        var count = 0;
        foreach (var buff in alreadyAdvancedBuffs.Good.Concat(alreadyAdvancedBuffs.Bad).Concat(alreadyAdvancedBuffs.Hidden))
        {
            if (Evaluate(objectId, kind, buff, content, physics) is not { } request) continue;
            send(request);
            count++;
        }
        return count;
    }

    private static uint GlidingThreshold(float seconds)
    {
        // MULSS then CVTTSS2SI to signed 64 bits, with only ECX compared unsigned. Invalid conversions
        // produce 8000000000000000, whose low 32 bits are zero; direct float-to-uint changes that behavior.
        var milliseconds = seconds * 1000f;
        if (float.IsNaN(milliseconds) || milliseconds >= 9223372036854775808f || milliseconds < -9223372036854775808f)
            return 0;
        return unchecked((uint)(long)milliseconds);
    }
}
