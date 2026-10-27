using System.Numerics;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Protocol.Units;

namespace AAEmu.Zone.Units;

/// <summary>Current world placement, separate from the immutable initial snapshot.</summary>
public readonly record struct ZoneUnitPose(UnitWorldPosition Position, Quaternion Rotation)
{
    public static ZoneUnitPose FromSnapshot(UnitStateSnapshot snapshot) =>
        new(new(snapshot.Position.FixedX, snapshot.Position.FixedY, snapshot.Position.Z), snapshot.Heading.ToQuaternion());

    public static ZoneUnitPose FromResurrection(UnitResurrection message) => new(message.Position, UnitHeading.FromYaw(message.Yaw));
}
