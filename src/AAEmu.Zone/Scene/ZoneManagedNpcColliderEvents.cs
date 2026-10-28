namespace AAEmu.Zone.Scene;

/// <summary>The signed mode is sign-extended into SEntityEvent.nParam[0].</summary>
public readonly record struct ZoneManagedNpcColliderEvent(int Event, long Parameter0);

/// <summary>
/// The owned entity's event43 receiver, not a substitute for the general entity-proxy system.
///320363C0 sends43;320142E0 has no special case43; animated receiver30662840 requests
///the mode through virtual+304/3065FB80 at layer3. This owns that actual receiver.
/// </summary>
public sealed class ZoneManagedNpcColliderEvents : IDisposable
{
    private readonly ZoneManagedNpcActor _actor;
    private readonly ZoneAnimatedColliderController _animated;
    private bool _sending;
    public bool IsDisposed { get; private set; }
    public ulong DeliveredCount { get; private set; }
    public ZoneManagedNpcColliderEvent? LastDelivered { get; private set; }
    public ZoneManagedNpcColliderEvents(ZoneManagedNpcActor actor, ZoneAnimatedColliderController animated)
    { _actor = actor ?? throw new ArgumentNullException(nameof(actor)); _animated = animated ?? throw new ArgumentNullException(nameof(animated)); }

    public ZoneAnimatedColliderUpdateOutcome SetColliderMode(int mode)
    {
        Require();
        if (_sending) throw new InvalidOperationException("The owned collider receiver cannot dispatch recursively.");
        _sending = true;
        try
        {
            var message = new ZoneManagedNpcColliderEvent(43, mode);
            var result = _animated.Request(unchecked((int)message.Parameter0), 3);
            Require(); LastDelivered = message;
            if (DeliveredCount != ulong.MaxValue) DeliveredCount++;
            return result;
        }
        finally { _sending = false; }
    }
    private void Require()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (!_actor.HasActor) throw new ObjectDisposedException(nameof(ZoneManagedNpcColliderEvents), "The exact entity generation retired.");
    }
    public void Dispose() => IsDisposed = true;
}
