using System.Numerics;

namespace AAEmu.Zone.Scene;

/// <summary>
/// Copied owned SOBJECTSTATE inputs consumed by CAIProxy30518020. Direction40 is the
/// produced AI movement vector, not a physical velocity. DesiredSpeed960 and urgency952
/// remain independent; the concrete Puppet/navigation/speed owners supply this record.
/// </summary>
public sealed record ZoneManagedNpcProxyMovementOutput(
    int BodyState,
    Vector3 LookTarget,
    bool LookTargetEnabled,
    Vector3 BodyTarget,
    Vector3 MoveDirection,
    float DesiredSpeed,
    float MovementUrgency,
    bool AllowStrafing,
    float DistanceToPathEnd,
    int MovementContext);
