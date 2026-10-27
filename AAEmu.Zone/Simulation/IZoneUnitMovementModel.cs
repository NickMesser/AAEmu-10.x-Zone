using AAEmu.Zone.Protocol.Messages;

namespace AAEmu.Zone.Simulation;

public interface IZoneUnitMovementModel
{
    ZoneUnit Owner { get; }
    void Receive(UnitMovementData movement);
    void Retire();
}

/// <summary>39362E80's actual scene switch, unsmoothed clock, and optional native movement recording path.</summary>
public interface IZoneUnitMovementServices
{
    bool ServerControlledMovement { get; }
    uint ServerMilliseconds { get; }
    void RecordMovement(ZoneUnit owner, UnitMovementData movement);
}
