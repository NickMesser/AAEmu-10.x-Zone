using System.Numerics;

namespace AAEmu.Zone.Simulation;

/// <summary>Physical application state belonging to one movement controller, separate from received and sampled states.</summary>
public sealed partial class ZoneActorMovementApplicationState
{
    public int JumpMode { get; internal set; }
    public bool JumpLatch { get; internal set; }
    /// <summary>Bounded movement-sample traces (see the jump-arrival evidence in the input path).</summary>
    internal static int MovementTraces;
    /// <summary>Bounded traces of jump bits actually observed in the client's movement stream.</summary>
    internal static int JumpTraces;
    public bool FacingLatch { get; internal set; }
    public uint SkillControllerType { get; internal set; }
    public byte SkillControllerPhase { get; internal set; }
    public uint InstalledSkillControllerId { get; internal set; }
    /// <summary>Native controller +5C4 selects the owner unit's target before the model target.</summary>
    public bool UseOwnerUnitTarget { get; set; }

    internal void RetireInput()
    {
        JumpMode = 0; JumpLatch = false; FacingLatch = false;
        SkillControllerType = 0; SkillControllerPhase = 0; InstalledSkillControllerId = 0;
        UseOwnerUnitTarget = false;
    }
}

public sealed partial class ZoneActorMovementController
{
    public ZoneActorMovementApplicationState Application { get; } = new();
    private static readonly ZoneActorMovementState InitialInputState = new()
    {
        Pose = new(default, Quaternion.Identity)
    };

    /// <summary>
    /// 39245A30: produce and apply gameplay input, then reconcile skill-controller selection.
    /// The enclosing physical caller supplies its working copy of Latest and owns phase eligibility.
    /// This method performs the native cache selection independently, including repeated mode reads.
    /// It does not move the model, clear pending movement, or replace Latest.
    /// </summary>
    public bool TryApplyPhysicalInput(ZoneActorMovementState working, IZoneActorMovementInputServices services)
    {
        ArgumentNullException.ThrowIfNull(working); ArgumentNullException.ThrowIfNull(services);
        if (!Current) return false;
        if (_receiving || _sampling) throw new InvalidOperationException("Actor movement input cannot reenter its owning controller.");
        // Share the history-operation guard so service callbacks cannot receive or sample recursively.
        _sampling = true;
        try { return ApplyPhysicalInputCore(working, services); }
        finally { _sampling = false; }
    }

    private bool ApplyPhysicalInputCore(ZoneActorMovementState working, IZoneActorMovementInputServices services)
    {
        var actor = services.GetPhysicalActor(Owner);
        if (!Current || actor is null) return false;
        var entity = services.GetModelEntity(Owner);
        if (!Current || entity is null) return false;
        var input = actor.GetInputController();
        if (!Current || input is null) return false;
        var mode = _services.InterpolationMode;
        if (!Current) return false;
        ZoneActorMovementState selected;
        if (mode == 2) selected = VectorHistory.SampledState ?? InitialInputState;
        else
        {
            mode = _services.InterpolationMode;
            if (!Current) return false;
            selected = mode == 3 ? LinkedHistory.SampledState ?? InitialInputState : working;
        }
        var inputState = input.GetState();
        if (!Current) return false;
        if (inputState is null) throw new InvalidOperationException("The physical input controller has no retained state.");
        var jump = (selected.ActorFlags & 8) != 0;
        var jumpEnded = (selected.ActorFlags & 16) != 0;
        // Bounded evidence for the one movement question that could not be answered from outside:
        // does a JUMP ever ARRIVE from the client? Walking already proves packets flow, so logging
        // the first few arrivals and the first few jump bits separates "the client never sent a jump"
        // from "the zone mishandled one". Bounded because an unbounded per-packet line is the log
        // flood this file's neighbours already paid for once.
        if (ZoneActorMovementApplicationState.MovementTraces < 3)
        {
            ZoneActorMovementApplicationState.MovementTraces++;
            Console.WriteLine($"MOVEMENT sample unit={Owner.Snapshot.ObjectId} flags=0x{selected.ActorFlags:X} " +
                $"jump={jump} jumpEnded={jumpEnded} delta=({selected.Delta.X}, {selected.Delta.Y}, {selected.Delta.Z})");
        }
        if (jump && ZoneActorMovementApplicationState.JumpTraces < 3)
        {
            ZoneActorMovementApplicationState.JumpTraces++;
            Console.WriteLine($"MOVEMENT JUMP ARRIVED unit={Owner.Snapshot.ObjectId} " +
                $"attempt={ZoneActorMovementApplicationState.JumpTraces} flags=0x{selected.ActorFlags:X}");
        }
        var movementFlag = (selected.ActorFlags & 1) != 0;
        if (!Application.JumpLatch) Application.JumpLatch = jump;
        else if (jumpEnded) Application.JumpLatch = false;
        if (Application.JumpMode == 1 && !Application.JumpLatch)
        {
            var contact = actor.NativeGroundContact1A4;
            if (!Current) return false;
            if (contact == 0) Application.JumpMode = 0;
        }
        var flags = 0x40000020u;
        if (jump) { flags = 0xC0000020u; Application.JumpMode = 1; }
        if (jumpEnded)
        {
            services.NotifyJumpEnded(Owner);
            if (!Current) return false;
        }
        var delta = selected.Delta;
        var movementScalar = .01f < delta.X * delta.X + delta.Y * delta.Y + delta.Z * delta.Z
            ? movementFlag ? .5f : 1f : 0f;
        flags = (flags & 0xFEFDFFFF) | 0x20000808;
        actor.SetMovementFlag(movementFlag);
        if (!Current) return false;
        var ground = FindInputGround(selected, services);
        if (!Current) return false;
        var groundRotation = Quaternion.Identity;
        if (ground is not null)
        {
            groundRotation = ground.GetRotation();
            if (!Current) return false;
        }
        ZoneActorMovementInputUnit? target = null;
        if (Application.UseOwnerUnitTarget)
        {
            var modelUnitId = services.GetModelUnitId(Owner);
            if (!Current) return false;
            var owner = modelUnitId == 0 ? null : services.FindUnit(modelUnitId);
            if (!Current) return false;
            if (owner is not null && owner.TargetId != 0 && owner.TargetId != owner.ObjectId)
            {
                target = services.FindUnit(owner.TargetId);
                if (!Current) return false;
            }
        }
        if (target is null)
        {
            var targetId = services.GetModelTargetId(Owner);
            if (!Current) return false;
            if (targetId != 0)
            {
                target = services.FindUnit(targetId);
                if (!Current) return false;
            }
        }
        var facing = services.GetModelFacingDirection(Owner);
        if (!Current) return false;
        Quaternion desiredRotation;
        if (target is null && MathF.Abs(facing.X) <= 0 && MathF.Abs(facing.Y) <= 0 && MathF.Abs(facing.Z) <= 0)
        {
            desiredRotation = ZoneActorMovementInputMath.Multiply(groundRotation, selected.Pose.Rotation);
            if (Application.FacingLatch)
            {
                Application.FacingLatch = false;
                services.SetFacingControl(Owner, false, 3, "net actor controller");
                if (!Current) return false;
            }
        }
        else
        {
            if (target is not null)
            {
                var position = entity.GetPosition();
                if (!Current) return false;
                var targetPosition = services.GetUnitPosition(target);
                if (!Current) return false;
                facing = ZoneActorMovementInputMath.TargetDirection(position, targetPosition);
            }
            desiredRotation = ZoneActorMovementInputMath.DirectionRotation(facing);
            if (!Application.FacingLatch)
            {
                Application.FacingLatch = true;
                services.SetFacingControl(Owner, true, 3, "net actor controller");
                if (!Current) return false;
            }
        }
        var rotation = entity.GetRotation();
        if (!Current) return false;
        desiredRotation = ZoneActorMovementInputMath.InterpolateRotation(rotation, desiredRotation);
        var relative = ZoneActorMovementInputMath.Multiply(new(-rotation.X, -rotation.Y, -rotation.Z, rotation.W), desiredRotation);
        var angles = ZoneActorMovementInputMath.RotationAngles(relative);
        var extra = selected.NativeVector3C;
        var inputVector = inputState.NativeVector24;
        if (!Current) return false;
        var request = new ZoneActorMovementInputRequest
        {
            Flags = (flags & 0xF7FFFFFF) | 0x41,
            NativeVector04 = new(extra.X * 10f + inputVector.X, extra.Y * 10f + inputVector.Y, extra.Z * 10f + inputVector.Z),
            Stance = selected.Stance, Alertness = selected.Alertness, Delta = delta,
            RotationAngles = angles, MovementScalar = movementScalar
        };
        input.Apply(request);
        if (!Current) return false;
        if (selected.SkillControllerType != Application.SkillControllerType || selected.Phase != Application.SkillControllerPhase)
        {
            if (Application.InstalledSkillControllerId != 0)
            {
                services.RemoveSkillController(Owner, Application.InstalledSkillControllerId);
                if (!Current) return false;
                Application.InstalledSkillControllerId = 0;
            }
            var definition = services.FindSkillController(selected.SkillControllerType == 0
                ? Application.SkillControllerType : selected.SkillControllerType);
            if (!Current) return false;
            uint descriptorId = 0, parameter = 0;
            if (definition is not null)
            {
                if (selected.SkillControllerType == 0) descriptorId = definition.Native08;
                else if (selected.Phase == 0) { descriptorId = definition.Native14; parameter = definition.Native04; }
                else if (selected.Phase == 1) descriptorId = definition.Native20;
                else if (selected.Phase == 2) descriptorId = definition.Native24;
            }
            if (descriptorId != 0)
            {
                var installed = services.InstallSkillController(Owner, new(descriptorId, parameter));
                if (!Current) return false;
                Application.InstalledSkillControllerId = installed;
            }
            Application.SkillControllerType = selected.SkillControllerType;
            Application.SkillControllerPhase = selected.Phase;
        }
        return true;
    }

    private IZoneActorMovementInputEntity? FindInputGround(ZoneActorMovementState state, IZoneActorMovementInputServices services)
    {
        IZoneActorMovementInputEntity? entity = null;
        if (state.GroundUnitId != 0)
        {
            var unit = services.FindUnit(state.GroundUnitId);
            if (!Current) return null;
            if (unit is not null) { entity = services.GetUnitEntity(unit); if (!Current) return null; }
            else { services.ReportMissingGround(ZoneActorMovementGroundKind.Unit, state.GroundUnitId); if (!Current) return null; }
        }
        if (state.GroundGimmickId != 0)
        {
            var gimmick = services.FindGimmick(state.GroundGimmickId);
            if (!Current) return null;
            if (gimmick is not null) entity = gimmick.Entity;
            if (entity is null) { services.ReportMissingGround(ZoneActorMovementGroundKind.Gimmick, state.GroundGimmickId); if (!Current) return null; }
        }
        if (state.GroundDoodadId != 0)
        {
            var doodad = services.FindDoodad(state.GroundDoodadId);
            if (!Current) return null;
            if (doodad is not null) entity = doodad.Entity;
            if (entity is null) services.ReportMissingGround(ZoneActorMovementGroundKind.Doodad, state.GroundDoodadId);
        }
        return entity;
    }
}
