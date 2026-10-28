using System.Numerics;
using AAEmu.Zone.Physics;

namespace AAEmu.Zone.Scene;

/// <summary>
/// Actual ActorUnitModel39278AC0 outputs before wire quantization. These values must come
/// from the CActor/model producers; desired Puppet wishes and physical grounded alone do
/// not establish the independent actor flags, request delta or controller state.
/// </summary>
public sealed record ZoneManagedNpcReplicationSample(ZoneManagedNpcBodySnapshot Body,
    Vector3 ActorVelocity, Vector3 Delta, ushort ActorFlags, sbyte Stance, sbyte Alertness,
    uint ControllerId = 0, byte ControllerPhase = 0, bool KeepSyncing = false,
    bool? GuardedAllowStrafing = null, bool ModelCanReplicate = true, byte MovementFlags = 0,
    uint Timestamp = 0, float FallVelocity = 0);
