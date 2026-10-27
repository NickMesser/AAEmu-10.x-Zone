namespace AAEmu.Zone.Actors;

/// <summary>
/// Required native state for 39263AC0/392285B0. The environment and model field meanings are
/// intentionally opaque until their owners are implemented; these values must come from those owners.
/// This snapshot does not supply a scheduler or acquire physical state.
/// </summary>
public readonly record struct ZoneActorMovementEligibilitySnapshot(
    uint ModelFlags, byte EnvironmentFlag120, byte EnvironmentFlag121, byte EnvironmentFlag150,
    uint ModelState210, bool HasModelObject1A0);

public static class ZoneActorMovementEligibility
{
    /// <summary>Actor model flags 1/2 block its update phase; the controller gate then evaluates 392285B0.</summary>
    public static bool AllowsUpdate(float lastGameSeconds, bool directMovementRecentlyApplied,
        ZoneActorMovementEligibilitySnapshot snapshot) =>
        (snapshot.ModelFlags & 3) == 0 && !directMovementRecentlyApplied &&
        (snapshot.EnvironmentFlag120 == 0 || snapshot.EnvironmentFlag121 == 0 || snapshot.EnvironmentFlag150 != 0) &&
        lastGameSeconds != 0 && snapshot.ModelState210 == 0 && !snapshot.HasModelObject1A0;
}

public sealed partial class ZoneActorMovementController
{
    /// <summary>Read-only native phase gate. Callers must still supply actual sampling timing and physical prerequisites.</summary>
    public bool IsSamplingEligible(ZoneActorMovementEligibilitySnapshot snapshot) =>
        Current && ZoneActorMovementEligibility.AllowsUpdate(LastGameSeconds, DirectMovementRecentlyApplied, snapshot);
}
