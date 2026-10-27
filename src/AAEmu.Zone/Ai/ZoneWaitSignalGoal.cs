namespace AAEmu.Zone.Ai;

public enum ZoneSignalMatchKind { Name = 0, ObjectName = 1, Value = 2, EntityId = 3 }

/// <summary>310E85D8 execution, 310DE0A8 matching and 310E40F8 reset, bound to one actor's registry.</summary>
public sealed class ZoneWaitSignalGoal : IZoneGoalOperation
{
    private readonly ZoneSignalWaitRegistry _registry;
    private readonly Func<long> _frameTime;
    private readonly string _name, _objectName;
    private readonly ZoneSignalMatchKind _match;
    private readonly int _value;
    private bool _disposed;
    public float TimeoutSeconds { get; }
    public long StartMicroseconds { get; private set; }
    public bool SignalReceived { get; private set; }

    public ZoneWaitSignalGoal(string name, ZoneSignalMatchKind match, string objectName, int value,
        float timeoutSeconds, ZoneSignalWaitRegistry registry, Func<long> frameTimeMicroseconds)
    {
        if (match is < ZoneSignalMatchKind.Name or > ZoneSignalMatchKind.EntityId) throw new ArgumentOutOfRangeException(nameof(match));
        _name = ZoneAiSignalName.CString(name); _objectName = ZoneAiSignalName.CString(objectName);
        _match = match; _value = value; TimeoutSeconds = timeoutSeconds;
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _frameTime = frameTimeMicroseconds ?? throw new ArgumentNullException(nameof(frameTimeMicroseconds));
    }

    public int Execute()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (SignalReceived) { SignalReceived = false; return 4; }
        if (TimeoutSeconds > 0)
        {
            var now = _frameTime();
            if (StartMicroseconds == 0) { StartMicroseconds = now; _registry.Register(this); }
            var elapsed = (float)unchecked(now - StartMicroseconds) * 0.000001f;
            if (TimeoutSeconds < elapsed) return 4;
        }
        else if (StartMicroseconds == 0)
        {
            _registry.Register(this); StartMicroseconds = 1000;
        }
        return 1;
    }

    internal bool Receive(string name, ZoneAiSignalData? data)
    {
        if (SignalReceived) return true;
        if (_name != name) return false;
        SignalReceived = _match switch
        {
            ZoneSignalMatchKind.Name => true,
            ZoneSignalMatchKind.ObjectName => data is not null && _objectName == ZoneAiSignalName.CString(data.ObjectName),
            ZoneSignalMatchKind.Value => data is not null && _value == data.Value,
            ZoneSignalMatchKind.EntityId => data is not null && unchecked((uint)_value) == data.EntityId,
            _ => false
        };
        return SignalReceived;
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _registry.Unregister(this); StartMicroseconds = 0;
        // Reset with a bound actor preserves a received signal until the next execution consumes it.
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Native relies on active-goal Reset. Also remove registrations when a detached owner is disposed.
        _registry.Unregister(this);
    }
}
