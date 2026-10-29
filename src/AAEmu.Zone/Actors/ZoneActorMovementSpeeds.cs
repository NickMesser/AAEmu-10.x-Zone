using System.Numerics;

namespace AAEmu.Zone.Actors;

public readonly record struct ZoneActorMovementSpeeds(float Desired, float Minimum, float Maximum);

/// <summary>CPuppet31169B4C's copied seven-stance/four-urgency table and directional scaling.</summary>
public sealed class ZoneActorMovementSpeedTable
{
    private readonly ZoneActorMovementSpeeds[] _speeds;

    public ZoneActorMovementSpeedTable(IEnumerable<ZoneActorMovementSpeeds> speeds)
    {
        ArgumentNullException.ThrowIfNull(speeds);
        _speeds = speeds.ToArray();
        if (_speeds.Length != 28)
            throw new ArgumentException("Movement speeds require the actual seven stance groups and four urgency entries.", nameof(speeds));
    }

    public ZoneActorMovementSpeeds Select(float urgency, int proxyStance)
    {
        if (urgency == 0) return default;
        var movement = urgency < .305f ? 0 : urgency < .7f ? 1 : urgency < 1.2f ? 2 : 3;
        //The original reversed COMISS branches select Sprint for unordered urgency.
        if (float.IsNaN(urgency)) movement = 3;
        var stance = proxyStance switch { 1 => 0, 2 => 5, 5 => 2, 6 => 3, 7 => 4, 8 => 6, _ => 1 };
        return _speeds[stance * 4 + movement];
    }

    public ZoneActorMovementSpeeds Calculate(float urgency, int proxyStance, float directionalReferenceMinimum,
        float directionalReferenceMaximum, float bodyTurnRate, Vector3 bodyDirection, Vector3 moveDirection,
        float copiedControllerAngle)
    {
        var result = Select(urgency, proxyStance);
        if (!(directionalReferenceMinimum > 0)) return result;
        var speed = result.Desired;
        var fraction = (speed - directionalReferenceMinimum) / (directionalReferenceMaximum - directionalReferenceMinimum);
        var capped = Minimum(fraction, 1);
        var positive = Maximum(capped, 0);
        var squared = positive * positive;
        var factor = (1 - squared) * .8f + squared * .1f;
        var lower = Maximum(Minimum(factor, 1), .3f);
        var turnScale = Minimum(1 - MathF.Abs(bodyTurnRate * .4f) / MathF.PI, 1);
        var turn = Maximum(turnScale, lower);
        var cross = moveDirection.X * bodyDirection.Y - bodyDirection.X * moveDirection.Y;
        var dot = moveDirection.X * bodyDirection.X + bodyDirection.Y * moveDirection.Y;
        var angle = MathF.Atan2(cross, dot);
        var alignment = Maximum(Minimum((MathF.PI - MathF.Abs(angle * .6f)) / MathF.PI, 1), lower);
        var slope = Maximum(Minimum((MathF.PI - MathF.Abs(copiedControllerAngle * .017453292f / 12)) / MathF.PI, 1), lower);
        //The source picks min(turn, alignment, slope) using ordered comparisons.
        var combined = alignment <= slope ? alignment : slope;
        if (turn <= combined) combined = turn;
        speed = combined * speed;
        if (result.Minimum > speed) speed = result.Minimum;
        else if (speed > result.Maximum) speed = result.Maximum;
        return result with { Desired = speed };
    }

    //Preserve the source fabs-based min/max arithmetic, including signed zero/NaN behavior.
    private static float Minimum(float value, float maximum) => (value + maximum - MathF.Abs(value - maximum)) * .5f;
    private static float Maximum(float value, float minimum) => (MathF.Abs(value - minimum) + (value + minimum)) * .5f;
}
