namespace AAEmu.Zone.Ai;

/// <summary>310DAC48 / 310D6B58 / 310D6BB8: COPTimeout with the native microsecond clock and RNG.</summary>
public sealed class ZoneTimeoutGoal : IZoneGoalOperation
{
    private readonly Func<long> _frameTimeMicroseconds;
    private readonly Func<uint> _random;
    private readonly float _minimum, _maximum;
    private bool _disposed;
    public long StartTimeMicroseconds { get; private set; }
    public float DurationSeconds { get; private set; }

    public ZoneTimeoutGoal(float minimum, float maximum, Func<long> frameTimeMicroseconds, Func<uint> random)
    {
        _frameTimeMicroseconds = frameTimeMicroseconds ?? throw new ArgumentNullException(nameof(frameTimeMicroseconds));
        _random = random ?? throw new ArgumentNullException(nameof(random));
        _minimum = minimum; _maximum = maximum <= 0 ? minimum : maximum;
        Reset();
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DurationSeconds = (_maximum - _minimum) * ((float)_random() / 32767f) + _minimum;
        StartTimeMicroseconds = 0;
    }

    public int Execute()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var now = _frameTimeMicroseconds();
        if ((float)StartTimeMicroseconds * 0.000001f < 0.001f) StartTimeMicroseconds = now;
        var elapsed = (float)unchecked(now - StartTimeMicroseconds) * 0.000001f;
        return elapsed <= DurationSeconds ? 1 : 4;
    }

    public void Dispose() => _disposed = true;
}
