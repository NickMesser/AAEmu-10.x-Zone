using AAEmu.Zone.Actors;

namespace AAEmu.Zone.Ai;

/// <summary>Backend-neutral exclusivity for live NPC generations. Claims do not construct an actor.</summary>
public static class ZoneAiBackendBindings
{
    private static readonly object Gate = new();
    private static readonly Dictionary<ZoneActorHandle, object> Owners = [];

    public static bool TryClaim(ZoneActorHandle actor, object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (Gate)
        {
            if (Owners.ContainsKey(actor)) return false;
            Owners.Add(actor, owner);
            return true;
        }
    }

    public static bool IsBound(ZoneActorHandle actor)
    {
        lock (Gate) return Owners.ContainsKey(actor);
    }

    /// <summary>A different owner cannot release another backend's live generation.</summary>
    public static void Release(ZoneActorHandle actor, object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (Gate)
            if (Owners.TryGetValue(actor, out var current) && ReferenceEquals(current, owner))
                Owners.Remove(actor);
    }
}
