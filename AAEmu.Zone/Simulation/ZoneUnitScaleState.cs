namespace AAEmu.Zone.Simulation;

/// <summary>
/// Unit scale interpolation at 8554/855C/8560. The constructor starts at multiplier one;
/// 3935D260 supplies the separate base scale from the World snapshot.
/// </summary>
public sealed class ZoneUnitScaleState(float baseScale)
{
    public float BaseScale { get; } = baseScale;
    public float Multiplier { get; private set; } = 1f;
    public float Target { get; private set; } = 1f;
    public float Velocity { get; private set; } = 1f;
    public float Scale => BaseScale * Multiplier;

    /// <summary>3925A390: retarget from the current multiplier; the model changes during the next update.</summary>
    public void SetTarget(float multiplier, float durationSeconds)
    {
        Target = multiplier;
        // COMISS/JA maps negative durations to the lower bound, but unordered NaN reaches the upper bound.
        var duration = 0.01f;
        if (!(duration > durationSeconds)) duration = durationSeconds < 10f ? durationSeconds : 10f;
        Velocity = (multiplier - Multiplier) / duration;
    }

    /// <summary>
    /// 3925A3D0 -> 39259050: sample engine frame time (timer virtual60, timer0), independently
    /// of the rolling unit's elapsed milliseconds. Commit before applying the scale to the real model.
    /// An unchanged multiplier reads no clock and performs no model callback.
    /// </summary>
    public bool Update(Func<float> readFrameTimeSeconds, Action<float> applyModelScale)
    {
        ArgumentNullException.ThrowIfNull(readFrameTimeSeconds);
        ArgumentNullException.ThrowIfNull(applyModelScale);
        if (Multiplier == Target) return false;
        var remaining = Target - Multiplier;
        var step = readFrameTimeSeconds() * Velocity;
        Multiplier = MathF.Abs(step) > MathF.Abs(remaining) ? Target : Multiplier + step;
        applyModelScale(Scale);
        return true;
    }
}
