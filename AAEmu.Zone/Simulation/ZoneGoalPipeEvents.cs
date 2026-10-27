namespace AAEmu.Zone.Simulation;

/// <summary>One dispatcher per AI system; native 3120C048 suppresses nested notifications across all actors.</summary>
public sealed class ZoneGoalPipeEventDispatcher
{
    private bool _dispatching;
    internal void Dispatch(ZoneGoalPipeController owner, int eventId, ZoneGoalPipeEvent kind)
    {
        if (eventId == 0 || _dispatching) return;
        _dispatching = true;
        try { owner.DeliverEvent(eventId, kind); }
        finally { _dispatching = false; }
    }
}

/// <summary>An owned listener that unregisters its recorded registrations when disposed.</summary>
public sealed class ZoneGoalPipeListener(Action<ZoneGoalPipeController, ZoneGoalPipeEvent, int> callback) : IDisposable
{
    private readonly Action<ZoneGoalPipeController, ZoneGoalPipeEvent, int> _callback = callback ?? throw new ArgumentNullException(nameof(callback));
    private readonly List<(ZoneGoalPipeController Owner, int EventId)> _owners = [];
    public bool IsDisposed { get; private set; }
    internal void Attach(ZoneGoalPipeController owner, int eventId)
    { ObjectDisposedException.ThrowIf(IsDisposed, this); _owners.Add((owner, eventId)); }
    internal void Detach(ZoneGoalPipeController owner, int eventId) => _owners.Remove((owner, eventId));
    internal void Notify(ZoneGoalPipeController owner, ZoneGoalPipeEvent kind, int eventId)
    { if (!IsDisposed) _callback(owner, kind, eventId); }
    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        foreach (var registration in _owners.ToArray()) registration.Owner.UnregisterListener(this, registration.EventId);
    }
}
