using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Navigation;

namespace AAEmu.Zone.Physics;

internal readonly record struct ZoneManagedNavigationPhysicalEntity(ZoneActorHandle Actor, object Identity,
    uint EntityClass, ZoneNavigationObstacleBounds Bounds);

public sealed partial class ZoneManagedPhysicsBackend
{
    /// <summary>
    /// Copy the registered bodies of this backend, never infer an unknown body's native
    /// class from its velocity or mass. Terrain and admitted brush proxies are class256/1
    /// and cannot enter the native sleeping/rigid mask0x50006.
    /// </summary>
    internal IReadOnlyList<ZoneManagedNavigationPhysicalEntity> CopyNavigationPhysicalEntities()
    {
        List<ZoneManagedNavigationPhysicalEntity> values = [];
        foreach (var (actor, description) in _actors)
        {
            CopyStatus(actor, out var status);
            var center = status.Position + new Vector3(0, 0, description.CenterOffsetZ);
            //This backend's registered query shapes remain upright, including its generic
            //kinematic profile. These are the same bounds used by its shape-query owner.
            var extent = description.Shape.BoundingHalfExtents;
            values.Add(new(actor, description, description.Profile == ZonePhysicsActorProfile.Living ? 8u : 0u,
                new(center - extent, center + extent)));
        }
        return values.AsReadOnly();
    }
}
