using AAEmu.Zone.Content;

namespace AAEmu.Zone.Physics;

/// <summary>
/// Required physical-world service for CPhysicalWorld351F0AF8+E8 (35077020). The native consumer
/// accepts IDs0..511 and stores elasticity, friction and flags; it computes its second friction
/// coefficient as (float)((double)friction * 0.6666666666666666d) and resets four extended parameter
/// tables to zero. Returning false does not stop the native surface loader. No production physical
/// world implementation is supplied by this interface.
/// </summary>
public interface IZonePhysicalSurfaceBackend
{
    bool SetSurfaceParameters(int surfaceId, float elasticity, float friction, uint flags);
}

public static class ZonePhysicalSurfaceInstaller
{
    /// <summary>
    /// Replays every authored Physics registration in order, including duplicates (301CC130).
    /// Constructor-only and Physics-absent rows issue no calls; their backend values are retained.
    /// Call this during physical-world setup with the same content used for material surface IDs.
    /// </summary>
    public static void Install(ZonePhysicalSurfaceContent content, IZonePhysicalSurfaceBackend backend)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(backend);
        foreach (var registration in content.Registrations)
            backend.SetSurfaceParameters(registration.SurfaceId, registration.Elasticity,
                registration.Friction, registration.Flags);
    }
}
