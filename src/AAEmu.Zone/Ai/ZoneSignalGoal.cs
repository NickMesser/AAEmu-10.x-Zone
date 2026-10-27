namespace AAEmu.Zone.Ai;

/// <summary>Actor identity and signal routing services. Submit and Route receive value-owned extra data.</summary>
public interface IZoneSignalGoalServices
{
    object Actor { get; }
    object? Argument { get; }
    bool IsActor(object recipient);
    uint GetEntityId();
    void Submit(object recipient, int kind, string name, uint senderEntityId, ZoneAiSignalData data);
    void Route(int filter, int kind, string name, ZoneAiSignalData data);
}

/// <summary>310D71D8: COPSignal sends once, then completes on its following execution.</summary>
public sealed class ZoneSignalGoal : IZoneGoalOperation
{
    private readonly IZoneSignalGoalServices _services;
    private readonly int _kind, _filter, _value;
    private readonly string _name;
    private readonly object? _recipient;
    private bool _disposed;
    public bool Sent { get; private set; }

    public ZoneSignalGoal(int kind, string name, int filter, int value, IZoneSignalGoalServices services, object? recipient = null)
    {
        _kind = kind; _name = ZoneAiSignalName.CString(name); _filter = unchecked((byte)filter); _value = value;
        _services = services ?? throw new ArgumentNullException(nameof(services)); _recipient = recipient;
    }

    public int Execute()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Sent) { Sent = false; return 4; }
        var data = new ZoneAiSignalData(Value: _value);
        if (_filter == 0) _services.Submit(_services.Actor, _kind, _name, _services.GetEntityId(), data);
        else if (_filter is 1 or 5)
        {
            var recipient = _filter == 1 ? _services.Argument : _recipient;
            // Native filter 5 dereferences a null explicit recipient. Unroutable value data can be released safely.
            if (recipient is not null && _services.IsActor(recipient))
                _services.Submit(recipient, _kind, _name, _services.GetEntityId(), data);
        }
        else _services.Route(_filter, _kind, _name, data);
        Sent = true;
        return 1;
    }

    // Native Reset is a no-op: interruption must not cause a signal to be emitted twice.
    public void Reset() => ObjectDisposedException.ThrowIf(_disposed, this);
    public void Dispose() => _disposed = true;
}
