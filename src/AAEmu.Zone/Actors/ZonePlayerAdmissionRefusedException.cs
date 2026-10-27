namespace AAEmu.Zone.Actors;

/// <summary>
/// A PLAYER's admission failed for a reason that belongs to that player alone, and the zone must
/// keep serving everyone else.
///
/// Why this type exists: admission failures used to propagate out of the UnitState handler and take
/// the whole zone host down with them. The first real login proved it - the IGame+0xE8 refusal
/// retired the host seconds after load, and later the physical-ID exhaustion on a logout/re-entry
/// cycle did the same, dropping zone 129 offline for every player because ONE character tried to log
/// back in.
///
/// It is deliberately NOT derived from <see cref="NotSupportedException"/>: an exhausted retry loop
/// is a runtime capacity failure, not a missing capability, and conflating them would make the
/// readiness report lie about which dependency is unresolved. The session catches this type and
/// contains it per player; every OTHER exception still propagates, because a genuine bug must not be
/// swallowed to keep a server up.
/// </summary>
public sealed class ZonePlayerAdmissionRefusedException : Exception
{
    public ZonePlayerAdmissionRefusedException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
