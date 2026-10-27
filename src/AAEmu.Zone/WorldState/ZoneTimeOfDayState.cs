using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;

namespace AAEmu.Zone.WorldState;

/// <summary>
/// Scalar TimeOfDayManager state (ctor 0x300B1590), World corrections and Zone reports.
/// Advance belongs to the host's engine phase; reporting does not advance the clock.
/// Regional lighting, environment interpolation and renderer ownership remain with the scene.
/// </summary>
public sealed class ZoneTimeOfDayState : IDisposable
{
    private float _reportedStart;
    private float _reportedEnd;
    private float _reportedSpeed;

    public float Hour { get; private set; }
    public float Start { get; private set; }
    public float End { get; private set; } = 24f;
    public float Speed { get; private set; } = 0.001f;
    public bool HasWorldTime { get; private set; }
    public float LastReportedHour { get; private set; }
    public float ReportElapsedSeconds { get; private set; }
    public bool IsDisposed { get; private set; }

    /// <summary>The independent native advancement gates at manager +0x38 and +0x39.</summary>
    public bool AdvancementSuspended { get; set; }
    public bool UpdatesPaused { get; set; }

    /// <summary>Optional scene notifications. The clock owns no renderer or observer lifetime.</summary>
    public event Action<float, bool>? TimeApplied;
    public event Action<float, float, float>? AdvancedTimeApplied;

    public void SetTime(float hour, bool force = false)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        Hour = hour;
        // Managed observer-retirement policy: closing this owner from one callback also
        // retires callbacks still pending in the captured invocation list.
        if (TimeApplied is { } observers)
            foreach (Action<float, bool> observer in observers.GetInvocationList())
            {
                if (IsDisposed) break;
                observer(hour, force);
            }
    }

    public void SetAdvancedTime(float start, float end, float speed)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        Start = start;
        End = end;
        Speed = speed;
        if (AdvancedTimeApplied is { } observers)
            foreach (Action<float, float, float> observer in observers.GetInvocationList())
            {
                if (IsDisposed) break;
                observer(start, end, speed);
            }
    }

    /// <summary>0x300AD690: positive speed only; crossing a bound resets to the opposite bound.</summary>
    public void Advance(float frameSeconds)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (AdvancementSuspended || UpdatesPaused) return;
        var hour = Hour;
        if (Speed > 0f) hour += frameSeconds * Speed;
        if (hour > End) hour = Start;
        if (Start > hour) hour = End;
        SetTime(hour);
    }

    /// <summary>0x39332600: first update forces time; subsequent small backward corrections wait.</summary>
    public void Receive(ZoneTimeOfDay message)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var hour = message.Hour;
        if (!HasWorldTime)
        {
            HasWorldTime = true;
            SetTime(hour, true);
            return;
        }
        var current = Hour;
        if (current < 2f && hour > 22f) current += 24f;
        else if (hour < 2f && current > 22f) hour += 24f;
        var difference = hour - current;
        var magnitude = (float)Math.Abs((double)difference);
        if (difference < 0f && magnitude < 0.1f) return;
        if (magnitude < 1f)
        {
            hour = (current - hour) * 0.05f + hour;
            if (hour > 24f) hour -= 24f;
        }
        SetTime(hour);
    }

    /// <summary>0x39331DC0: advanced settings, forced hour, then the first-update flag.</summary>
    public void Receive(ZoneDetailedTimeOfDay message)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        SetAdvancedTime(message.Start, message.End, message.Speed);
        if (IsDisposed) return;
        SetTime(message.Hour, true);
        if (!IsDisposed) HasWorldTime = true;
    }

    /// <summary>0x39332130's initial report, after scene-specific command-line overrides.</summary>
    public ZoneFrame CreateInitialReport()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        LastReportedHour = Hour;
        return new ZoneDetailedTimeOfDay(Hour, Speed, Start, End).ToWorldFrame();
    }

    /// <summary>0x393322F0 compares the advanced settings by bits, including signed zero and NaNs.</summary>
    public ZoneFrame? CreateDetailedReport()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (Bits(Start) == Bits(_reportedStart) && Bits(End) == Bits(_reportedEnd) && Bits(Speed) == Bits(_reportedSpeed))
            return null;
        _reportedStart = Start;
        _reportedEnd = End;
        _reportedSpeed = Speed;
        LastReportedHour = Hour;
        ReportElapsedSeconds = 0f;
        return new ZoneDetailedTimeOfDay(Hour, Speed, Start, End).ToWorldFrame();
    }

    /// <summary>0x39332400. Call only when the detailed report did not fire in this Zone tick.</summary>
    public ZoneFrame? CreateTimeReport(float frameSeconds)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        // COMISS/JA chooses the two-second cap for unordered frame values as well.
        ReportElapsedSeconds += frameSeconds < 2f ? frameSeconds : 2f;
        if (ReportElapsedSeconds < 2f) return null;
        ReportElapsedSeconds -= 2f;
        if (Hour == LastReportedHour) return null;
        LastReportedHour = Hour;
        return new ZoneTimeOfDay(Hour).ToWorldFrame();
    }

    private static int Bits(float value) => BitConverter.SingleToInt32Bits(value);

    public void Dispose()
    {
        IsDisposed = true;
        TimeApplied = null;
        AdvancedTimeApplied = null;
    }
}
