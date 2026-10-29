using System.Numerics;
using AAEmu.Zone.Actors;

namespace AAEmu.Zone.Physics;

public sealed partial class ZoneManagedPhysicsBackend
{
    /// <summary>
    /// Queue the absolute upright rotation produced by the owned CActor/animated movement path.
    /// This is orientation only: velocity, requested motion, support and earlier queued commands survive.
    /// The scene still owns its sole Step and Unit/model pose publication.
    /// </summary>
    public void SetLivingOrientation(ZoneActorHandle actor, Quaternion absoluteOrientation)
    {
        Require(actor);
        if (!_livingControls.ContainsKey(actor) || _actors[actor].Profile != ZonePhysicsActorProfile.Living ||
            _actors[actor].LivingMovement is null || _actors[actor].Shape.Kind is not
                (ZonePhysicsShapeKind.Capsule or ZonePhysicsShapeKind.Cylinder))
            throw new NotSupportedException("Living orientation requests require the owned upright cylinder/capsule actor profile.");
        if (!float.IsFinite(absoluteOrientation.X) || !float.IsFinite(absoluteOrientation.Y) ||
            !float.IsFinite(absoluteOrientation.Z) || !float.IsFinite(absoluteOrientation.W))
            throw new ArgumentException("Living orientation must be finite.", nameof(absoluteOrientation));
        var squared = absoluteOrientation.LengthSquared();
        if (!float.IsFinite(squared) || squared <= 0)
            throw new ArgumentException("Living orientation must be a finite nonzero rotation.", nameof(absoluteOrientation));
        if (absoluteOrientation.X != 0 || absoluteOrientation.Y != 0)
            throw new NotSupportedException("Living orientation supports the actual upright yaw profile; tilted colliders require their own owner.");
        //CActor supplies its source-normalized view orientation. Do not silently change that
        //value/sign at a second owner, or treat a scaled quaternion as authored body scale.
        if (MathF.Abs(squared - 1) > .000001f)
            throw new NotSupportedException("Living orientation requires the source-produced unit quaternion; arbitrary quaternion normalization is not a physical request.");
        QueueLivingCommand(actor, LivingCommandKind.Orientation, () =>
            OwnedPhysics.SetOrientationPreservingMotion(actor, absoluteOrientation), absoluteOrientation);
    }

    /// <summary>The last queued orientation for this exact generation; it is not applied body status.</summary>
    public bool TryReadPendingLivingOrientation(ZoneActorHandle actor, out Quaternion orientation)
    {
        Require(actor);
        for (var index = _pendingLivingCommands.Count - 1; index >= 0; index--)
        {
            var command = _pendingLivingCommands[index];
            if (command.Actor == actor && command.Kind == LivingCommandKind.Orientation && command.Orientation is { } pending)
            { orientation = pending; return true; }
        }
        orientation = default;
        return false;
    }
}
