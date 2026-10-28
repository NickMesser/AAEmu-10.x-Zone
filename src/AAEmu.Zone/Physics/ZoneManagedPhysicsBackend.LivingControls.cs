using System.Numerics;
using AAEmu.Zone.Actors;

namespace AAEmu.Zone.Physics;

/// <summary>Copied applied living controller state; pending requests are not applied wishes.</summary>
public readonly record struct ZoneManagedLivingControlState(Vector3 RequestedVelocity,
    ZonePhysicsLivingVelocityMode LastVelocityMode, bool HasPendingVelocityRequest,
    float CachedGroundHeight, Vector3 CachedGroundNormal, bool IdleStateKnown,
    bool LastStepUsedIdleShortcut, int ConstraintCount, bool LivingFlag2,
    bool ForceRequestedVelocityOnce, float ImpulseRecoveryTimeRemaining,
    float SuspensionOffset, float SuspensionVelocity, float SuspensionAcceleration, float SuspensionTime);

public sealed partial class ZoneManagedPhysicsBackend
{
    private sealed class LivingControl
    {
        internal Vector3 Requested;
        internal ZonePhysicsLivingVelocityMode LastMode;
        internal bool ForceRequestedOnce;
        // Action3502A300 mode3 sets body952 mask0x2; consuming1120 only clears the
        // one-step override. The flag persists until a subsequent mode0 request.
        internal bool Flag2;
        // 35025530 fresh-body cache defaults; these are not reported as real surface samples.
        internal float CachedGroundHeight = -1e-10f;
        internal Vector3 CachedGroundNormal = Vector3.UnitZ;
        internal float ImpulseRecoveryTimer;
        internal float StepImpulseRecoveryTimer;
        internal float SuspensionOffset, SuspensionVelocity, SuspensionAcceleration;
        internal float SuspensionTime = 1;
        internal bool PriorBlocked;
        internal bool IdleStateKnown = true;
        internal bool LastIdle;
        internal int Constraints;
        internal LivingControl Copy() => (LivingControl)MemberwiseClone();
        internal void ResetMotion()
        {
            Requested = default;
            ForceRequestedOnce = false;
            LastMode = ZonePhysicsLivingVelocityMode.Normal;
            Flag2 = false;
            // Explicit scene teleport resets motion requests and clears the public support
            // hit. Auxiliary native floor/normal caches survive; network flags49 poses use
            // their separate correction boundary and preserve requests/mode instead.
        }
    }
    private readonly Dictionary<ZoneActorHandle, LivingControl> _livingControls = [];
    private readonly List<(ZoneActorHandle Actor, Vector3 Requested, ZonePhysicsLivingVelocityMode Mode)> _pendingLivingVelocity = [];

    public void ApplyLivingVelocityRequest(ZoneActorHandle actor, Vector3 requestedVelocity,
        ZonePhysicsLivingVelocityMode mode = ZonePhysicsLivingVelocityMode.Normal)
    {
        Require(actor);
        ZoneManagedTerrainGeometry.ValidateFinite(requestedVelocity, nameof(requestedVelocity));
        if (!float.IsFinite(requestedVelocity.LengthSquared()))
            throw new ArgumentOutOfRangeException(nameof(requestedVelocity), "Living wish exceeds finite speed precision.");
        if (!_livingControls.ContainsKey(actor))
            throw new NotSupportedException("Living wishes require a registered authored living body.");
        if (mode is not (ZonePhysicsLivingVelocityMode.Normal or ZonePhysicsLivingVelocityMode.Immediate))
            throw new NotSupportedException("Only the verified original living velocity modes0 and3 are supported.");
        _pendingLivingVelocity.Add((actor, requestedVelocity, mode));
    }

    public void CopyLivingControlState(ZoneActorHandle actor, out ZoneManagedLivingControlState state)
    {
        Require(actor);
        if (!_livingControls.TryGetValue(actor, out var control))
            throw new NotSupportedException("This actor has no authored living controller state.");
        state = new(control.Requested, control.LastMode, _pendingLivingVelocity.Exists(item => item.Actor == actor),
            control.CachedGroundHeight, control.CachedGroundNormal, control.IdleStateKnown, control.LastIdle,
            control.Constraints, control.Flag2, control.ForceRequestedOnce, control.ImpulseRecoveryTimer,
            control.SuspensionOffset, control.SuspensionVelocity, control.SuspensionAcceleration, control.SuspensionTime);
    }

    private void DrainLivingVelocityRequests()
    {
        foreach (var request in _pendingLivingVelocity)
        {
            if (!_livingControls.TryGetValue(request.Actor, out var control)) continue;
            control.Requested = request.Requested;
            control.LastMode = request.Mode;
            control.ForceRequestedOnce = request.Mode == ZonePhysicsLivingVelocityMode.Immediate;
            control.Flag2 = control.ForceRequestedOnce;
            // Both modes write the wish, not actual velocity. Mode3's actual override
            // occurs in movement preparation, after the early recovery/idle phase.
        }
        _pendingLivingVelocity.Clear();
    }

    private void RetireLivingControls(ZoneActorHandle actor)
    {
        _livingControls.Remove(actor);
        _livingPrior.Remove(actor);
        _livingNextControls.Remove(actor);
        _pendingLivingVelocity.RemoveAll(item => item.Actor == actor);
        _pendingLivingCorrectionPoses.RemoveAll(item => item.Actor == actor);
    }
}
