using AAEmu.Zone.Protocol.Messages;

namespace AAEmu.Zone.Units;

/// <summary>3935E7E0: unit_location_debug gates one World-position report per registered unit per frame.</summary>
public sealed class ZoneUnitLocationReporter(ZoneUnitRegistry registry, bool enabled)
{
    public bool Enabled { get; set; } = enabled;

    public int Update()
    {
        if (!Enabled) return 0;
        var count = 0;
        foreach (var unit in registry.Units.Values.ToArray())
            if (registry.IsCurrent(unit) && registry.QueueNotification(
                new ZoneUnitLocation(unit.Snapshot.ObjectId, unit.Pose.Position).ToFrame())) count++;
        return count;
    }
}
