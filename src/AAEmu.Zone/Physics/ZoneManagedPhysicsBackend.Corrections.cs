using System.Numerics;
using AAEmu.Zone.Actors;

namespace AAEmu.Zone.Physics;

public sealed partial class ZoneManagedPhysicsBackend
{
    private readonly List<(ZoneActorHandle Actor, Vector3 Position)> _pendingLivingCorrectionPoses = [];

    /// <summary>
    /// 3502AA60, CLiving virtual0xC0: copied no-retained-platform network state.
    /// Moving-support anchors require their own owner and are explicitly refused.
    /// </summary>
    public bool TryApplyLivingCorrection(ZoneActorHandle actor, ZoneActorMovementCorrectionAction correction,
        out string refusal)
    {
        Require(actor);
        if (!_livingControls.TryGetValue(actor, out var control))
        { refusal = "Network living correction requires an authored living body."; return false; }
        ZoneManagedTerrainGeometry.ValidateFinite(correction.Position, nameof(correction));
        ZoneManagedTerrainGeometry.ValidateFinite(correction.Velocity, nameof(correction));
        if (correction.OriginalVelocity is { } original)
            ZoneManagedTerrainGeometry.ValidateFinite(original, nameof(correction));
        if (!float.IsFinite(correction.Velocity.LengthSquared()) ||
            correction.OriginalVelocity is { } originalSpeed && !float.IsFinite(originalSpeed.LengthSquared()))
            throw new ArgumentOutOfRangeException(nameof(correction), "Correction velocities exceed finite speed precision.");
        // Native returns0 before any tail writes when SameGround requests a missing entity.
        if (correction.SameGround)
        { refusal = "Living same-ground correction requires an owned retained support entity and part anchor."; return false; }

        OwnedPhysics.TryReadPosition(actor, out var current);
        var position = LivingCorrectionPosition(current, correction.Position, correction.Velocity,
            _actors[actor].Shape.Radius, correction.Native46);
        ZoneManagedTerrainGeometry.ValidateFinite(position, nameof(correction));
        // Forced corrections still traverse managed collision at drain. Validate that
        // segment before any immediate velocity/wish write or queue insertion.
        var offset = new Vector3(0, 0, _actors[actor].CenterOffsetZ);
        ZoneManagedTerrainGeometry.ValidateSegment(current + offset, position - current);
        ZoneManagedTerrainGeometry.ValidateFinite(position + offset, nameof(correction));
        // The tuple's vectors are fully defined in this managed contract. OriginalVelocity's
        // absence represents the sentinel-X branch; filtered velocity Z still updates wish Z.
        var requested = correction.OriginalVelocity ?? control.Requested;
        requested.Z = correction.Velocity.Z;
        OwnedPhysics.ApplyVelocity(actor, correction.Velocity);
        control.Requested = requested;
        _pendingLivingCorrectionPoses.Add((actor, position));
        // GroundTransition detaches a retained platform; this admitted profile retains none.
        // The original leaves952/1120 untouched: neither flag2 nor the one-shot mode changes.
        refusal = string.Empty;
        return true;
    }

    private void DrainLivingCorrectionPoses()
    {
        foreach (var request in _pendingLivingCorrectionPoses)
        {
            if (!_actors.TryGetValue(request.Actor, out var description)) continue;
            if (!description.CollisionActive)
            { OwnedPhysics.CommitLivingDimensionsPose(request.Actor, request.Position); continue; }
            OwnedPhysics.TryReadOrientation(request.Actor, out var orientation);
            OwnedPhysics.TryReadVelocity(request.Actor, out var velocity);
            // Retain the scene's existing traversed-correction guard. The original queues a
            // flags49 type0 pose directly; exact collision policy/queue interleaving remains
            // separate parity work. This guard must not turn velocity restoration into a wish.
            ApplyPose(request.Actor, request.Position, orientation);
            OwnedPhysics.ApplyVelocity(request.Actor, velocity);
        }
        _pendingLivingCorrectionPoses.Clear();
    }

    internal static Vector3 LivingCorrectionPosition(Vector3 current, Vector3 target,
        Vector3 velocity, float radius, bool force)
    {
        if (force) return target;
        var speed = MathF.Sqrt((velocity.X * velocity.X + velocity.Y * velocity.Y) + velocity.Z * velocity.Z);
        var speedBound = (float)((double)speed * .1);
        var radiusBound = (float)((double)radius * .5);
        var smaller = ((speedBound + radiusBound) - MathF.Abs(speedBound - radiusBound)) * .5f;
        var lower = (float)((double)radius * .1);
        var cap = (MathF.Abs(smaller - lower) + (smaller + lower)) * .5f;
        var delta = target - current;
        var distanceSquared = (delta.X * delta.X + delta.Y * delta.Y) + delta.Z * delta.Z;
        if (!float.IsFinite(distanceSquared))
            throw new ArgumentOutOfRangeException(nameof(target), "Correction displacement exceeds finite distance precision.");
        var distance = MathF.Sqrt(distanceSquared);
        if (distance <= cap)
        {
            var inverse = (float)(1.0 / cap);
            return new(
                current.X + (float)((double)((delta.X * distance) * inverse) * .033),
                current.Y + (float)((double)((delta.Y * distance) * inverse) * .033),
                current.Z + (float)((double)((delta.Z * distance) * inverse) * .033));
        }
        var denominator = MathF.Sqrt(((-delta.Y * -delta.Y) + (-delta.X * -delta.X)) + (-delta.Z * -delta.Z) +
            BitConverter.Int32BitsToSingle(0x00800000));
        var reciprocal = (float)(1.0 / denominator);
        return new(target.X + ((-delta.X * reciprocal) * cap),
            target.Y + ((-delta.Y * reciprocal) * cap), target.Z + ((-delta.Z * reciprocal) * cap));
    }
}
