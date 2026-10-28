using AAEmu.Zone.Physics;

namespace AAEmu.Zone.Actors;

/// <summary>
/// One admitted actor generation's accepted and desired stance slots. SetStance (392D4F50)
/// records a wish; 392DEFB0 retains the current slot until the dimension request is accepted.
/// The original request queues physical application for the next world step; current stance
/// is request state, not an observation of applied collider geometry. The backend owns that
/// outcome. All requests stay on the movement owner's prephase, with one attempt per frame.
/// </summary>
public sealed class ZoneActorLivingStanceState
{
    private readonly ZonePlayerActor _actor;
    private readonly IZonePhysicsLivingStances _backend;
    private bool _applying;
    internal bool AttemptedInFrame { get; private set; }

    internal ZoneActorLivingStanceState(ZonePlayerActor actor, IZonePhysicsLivingStances backend)
    {
        _actor = actor;
        _backend = backend;
        Catalog = new(actor.Model, actor.Scale);
    }

    public ZoneActorStanceCatalog Catalog { get; }
    public int CurrentSlot { get; private set; } = ZoneActorStanceCatalog.InitialSlot;
    public int DesiredSlot { get; private set; } = ZoneActorStanceCatalog.InitialSlot;
    /// <summary>Content for the accepted actor request. Applied body dimensions remain backend-owned.</summary>
    public ZoneActorStanceDefinition Current => Catalog.ForSlot(CurrentSlot);
    public bool HasPendingChange => CurrentSlot != DesiredSlot;
    /// <summary>Last refusal for the retained wish; a replacement or accepted wish clears it.</summary>
    public string? LastRefusal { get; private set; }

    internal void BeginFrame()
    {
        if (_applying) throw new InvalidOperationException("An actor stance update cannot reenter its frame owner.");
        AttemptedInFrame = false;
    }

    internal void Request(int slot)
    {
        RequireLive();
        // The request consumer at 392E10D0 skips -1 without replacing its retained wish.
        if (slot == ZoneActorStanceCatalog.UnchangedRequest) return;
        if (DesiredSlot != slot) LastRefusal = null;
        DesiredSlot = slot;
    }

    internal bool TryApplyPending(out string refusal)
    {
        RequireLive();
        if (_applying) throw new InvalidOperationException("An actor stance update cannot reenter its request writer.");
        refusal = string.Empty;
        if (!HasPendingChange) return true;
        if (AttemptedInFrame)
        {
            refusal = LastRefusal ?? "The actor's stance update was already attempted in this frame.";
            return false;
        }
        AttemptedInFrame = true;
        _applying = true;
        try
        {
            var requested = DesiredSlot;
            ZoneActorStanceDefinition selected;
            try { selected = Catalog.ForSlot(requested); }
            catch (NotSupportedException error)
            {
                refusal = LastRefusal = error.Message;
                return false;
            }
            if (!_backend.TryChangeLivingStance(_actor.Handle, selected.Physicalization, out refusal))
            {
                if (string.IsNullOrWhiteSpace(refusal)) refusal = "The physical backend rejected the authored stance dimensions.";
                LastRefusal = refusal;
                return false;
            }
            // The backend call may retire an actor in a diagnostic/custom provider. Never let
            // completion publish into a retired generation or its replacement numeric identity.
            if (!_actor.IsLive)
            {
                refusal = LastRefusal = "The actor generation retired during its stance update.";
                return false;
            }
            CurrentSlot = requested;
            LastRefusal = null;
            refusal = string.Empty;
            return true;
        }
        finally { _applying = false; }
    }

    private void RequireLive()
    {
        if (!_actor.IsLive) throw new ObjectDisposedException(nameof(ZonePlayerActor),
            $"Actor generation {_actor.Handle} is retired; its stance cannot change.");
    }
}
