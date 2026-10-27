using System.Numerics;
using AAEmu.Zone.Protocol.Messages;

namespace AAEmu.Zone.Simulation;

/// <summary>3922E1C0: actor network state, before interpolation or physical application.</summary>
public sealed record ZoneActorMovementState
{
    public uint Time { get; init; }
    public ZoneUnitPose Pose { get; init; }
    public Vector3 Velocity { get; init; }
    /// <summary>Native state +3C: zeroed by network projection and blended by both samplers; its physical meaning is not established.</summary>
    public Vector3 NativeVector3C { get; init; }
    public Vector3 Delta { get; init; }
    public float FallVelocity { get; init; }
    public sbyte Stance { get; init; }
    public sbyte Alertness { get; init; } = -1;
    public byte ActorFlags { get; init; }
    /// <summary>Native state +D9, copied into the physical correction controller. Network projection leaves this byte at its initialized zero value.</summary>
    public byte NativeStoppedByte { get; init; }
    public bool ResetHistory { get; init; }
    public bool BypassHistory => (ActorFlags & 2) != 0;
    public uint SkillControllerType { get; init; }
    public byte Phase { get; init; }
    public uint GroundUnitId { get; init; }
    public uint GroundGimmickId { get; init; }
    public uint GroundDoodadId { get; init; }
    public uint GroundPart { get; init; }
    public uint GroundPartId { get; init; }
    public ZoneUnitPose GroundPose { get; init; } = new(default, Quaternion.Identity);
    public uint HangingObjectId { get; init; }
    public Vector3 Climb { get; init; }
    public byte PushTag { get; init; }
    public UnitPushMovement? Push { get; init; }

    public static ZoneActorMovementState FromMovement(UnitMovementData movement)
    {
        if (!movement.HasPosition)
            throw new NotSupportedException("A ship helm request has no initialized actor position and requires a ship model.");
        var position = movement.Position;
        var result = new ZoneActorMovementState
        {
            Time = movement.Time,
            Pose = new(new(position.FixedX, position.FixedY, position.Z), movement.DecodedRotation),
            Velocity = movement.DecodedVelocity,
            ResetHistory = (movement.Flags & 0x20) != 0
        };
        if (movement.Kind != 1) return result;
        var actor = movement.Details as UnitActorMovement ?? throw new InvalidDataException("Actor movement details are required.");
        result = result with
        {
            Delta = actor.Delta.Decode(), FallVelocity = actor.DecodedFallVelocity,
            Stance = actor.Stance, Alertness = actor.Alertness, ActorFlags = (byte)(actor.Flags & 0x1F),
            GroundPart = actor.Ground?.Part ?? (ushort)0, GroundPartId = actor.Ground?.PartId ?? (ushort)0,
            SkillControllerType = (movement.Flags & 0x10) != 0 ? movement.SkillControllerType : 0,
            Phase = (movement.Flags & 0x10) != 0 ? movement.Phase : (byte)0
        };
        // These branches are deliberately exclusive: ground contact wins over hanging, then push.
        if ((actor.Flags & 0x20) != 0)
        {
            var ground = actor.Ground ?? throw new InvalidDataException("Ground contact data is required.");
            return result with
            {
                GroundUnitId = (ground.Flags & 1) != 0 ? actor.GroundObjectId : 0,
                GroundGimmickId = (ground.Flags & 3) == 2 ? actor.GroundObjectId : 0,
                GroundDoodadId = (ground.Flags & 7) == 4 ? actor.GroundObjectId : 0,
                GroundPose = new(new(ground.Position.FixedX, ground.Position.FixedY, ground.Position.Z), ground.Heading.ToQuaternion())
            };
        }
        var climb = actor.ClimbData;
        if ((actor.Flags & 0x40) != 0)
            return result with
            {
                HangingObjectId = actor.GroundObjectId,
                Climb = new((climb & 0x3FFF) / 16383f * 100f,
                    ((climb >> 14) & 0x3FF) / 1023f * 20f,
                    (climb >> 24) / 255f * 6.2831855f - 3.1415927f)
            };
        if ((actor.Flags & 0x8000) != 0)
            return result with
            {
                PushTag = (byte)(climb >> 24),
                Climb = new(unchecked((sbyte)(climb >> 16)), unchecked((sbyte)(climb >> 8)), unchecked((sbyte)climb)),
                Push = actor.Push ?? throw new InvalidDataException("Push data is required.")
            };
        return result;
    }
}
