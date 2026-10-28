using System.Numerics;
using AAEmu.Zone.Actors;

namespace AAEmu.Zone.Physics;

public sealed partial class ZoneManagedPhysicsBackend
{
    private readonly Dictionary<ZoneActorHandle, ZoneOwnedPhysicsStepState> _livingPrior = [];
    private readonly Dictionary<ZoneActorHandle, LivingControl> _livingNextControls = [];

    private void PrepareLivingStep(TimeSpan elapsed)
    {
        _livingPrior.Clear();
        _livingNextControls.Clear();
        foreach (var (actor, applied) in _livingControls)
        {
            var control = applied.Copy();
            var current = OwnedPhysics.CopyStepState(actor);
            _livingPrior.Add(actor, current);
            // Native selects inertia with the prior timer, then ages it even if the
            // subsequent idle/inactive branch returns before movement preparation.
            control.StepImpulseRecoveryTimer = control.ImpulseRecoveryTimer;
            var remainingRecovery = control.ImpulseRecoveryTimer - (float)elapsed.TotalSeconds;
            control.ImpulseRecoveryTimer = (MathF.Abs(-remainingRecovery) + remainingRecovery) * .5f;
            control.LastIdle = CanIdle(_actors[actor], current, control);
            control.Constraints = 0;
            _livingNextControls.Add(actor, control);
        }
    }

    private void CommitLivingControl(ZoneActorHandle actor)
    {
        // Called only after the owned body validated and committed this exact generation.
        if (_livingNextControls.Remove(actor, out var staged) && _livingControls.ContainsKey(actor))
            _livingControls[actor] = staged;
    }

    private static bool CanIdle(ZonePhysicsActorDescription description, ZoneOwnedPhysicsStepState current,
        LivingControl control)
    {
        if (!description.CollisionActive) return false;
        var movement = description.LivingMovement!;
        return control.IdleStateKnown && current.Velocity.LengthSquared() == 0 && control.Requested.LengthSquared() == 0 &&
            (current.Grounded || description.Gravity == 0) &&
            current.Position.Z - movement.HeightPivot >= control.CachedGroundHeight - movement.SupportHalfHeight * .1f &&
            control.SuspensionVelocity == 0 && control.SuspensionAcceleration == 0 && !control.PriorBlocked &&
            control.CachedGroundNormal.Z >= movement.MinimumSlideCosine;
    }

    private ZoneOwnedPhysicsStepState? IntegrateLivingActor(ZoneActorHandle actor,
        ZoneOwnedPhysicsStepState current, TimeSpan elapsed)
    {
        var description = _actors[actor];
        if (description.Profile != ZonePhysicsActorProfile.Living) return null;
        var control = _livingNextControls[actor];
        var prior = _livingPrior[actor];
        var seconds = (float)elapsed.TotalSeconds;
        if (!description.CollisionActive)
            return current with { Position = current.Position + control.Requested * seconds };
        if (control.LastIdle) return current;

        var settings = description.LivingMovement!;
        var gravity = new Vector3(0, 0, description.Gravity);
        var prepared = ZoneLivingMovementMath.PrepareVelocity(current.Velocity, control.Requested, gravity,
            control.CachedGroundNormal, seconds, settings, !prior.Grounded, control.Flag2,
            control.ForceRequestedOnce, control.StepImpulseRecoveryTimer);
        var actual = prepared.Velocity;
        var start = current.Position;
        var endpoint = start + actual * seconds;

        // 3502DED0 probes the PROPOSED endpoint before the sweep. It can adjust the
        // displacement to reachable rising support, or follow a short downward transition.
        if (_groundQuery.TryLivingSupport(endpoint, settings, out var before) && before.Normal is { } preNormal &&
            preNormal.Z > settings.MinimumFallCosine)
        {
            var foot = endpoint.Z - settings.HeightPivot;
            var floor = before.Position.Z;
            var rise = (float)((double)settings.SupportHeight * 1.01);
            var below = foot < floor && foot > floor - rise;
            var gap = foot - floor;
            var shortDescent = foot > floor && MathF.Abs(gap) * gap < actual.LengthSquared() * (seconds * seconds) &&
                prior.Grounded && !control.Flag2 && !description.LivingFlag4;
            if (below || shortDescent)
            {
                var suppress = below && control.CachedGroundNormal.Z < settings.MinimumSlideCosine &&
                    Vector3.Dot(preNormal, control.CachedGroundNormal) < settings.MinimumSlideCosine &&
                    preNormal.Z < settings.MinimumSlideCosine && control.Requested.LengthSquared() == 0;
                if (suppress) { endpoint = start; actual = Vector3.Zero; }
                else endpoint.Z = floor + settings.HeightPivot;
            }
        }

        var motion = SweepLivingMotion(actor, description, control, start, endpoint - start, actual);
        var position = motion.Position;
        actual = motion.Velocity;
        var blocked = motion.Blocked;
        control.Constraints = motion.Constraints;
        var hasSupport = _groundQuery.TryLivingSupport(position, settings, out var support);
        var floorHeight = hasSupport ? support.Position.Z : -1e10f;
        var normal = control.CachedGroundNormal;
        if (hasSupport && support.Normal is { } candidate && candidate.Z >= .087f) normal = candidate;
        var heightBand = settings.SupportHalfHeight * .01f;
        var tolerance = (float)(Math.Abs((double)(.004f - heightBand)) + (heightBand + .004f)) * .5f;
        var footHeight = position.Z - settings.HeightPivot;
        var gapTerm = footHeight - (tolerance + floorHeight);
        var angleTerm = settings.MinimumFallCosine - normal.Z;
        var maximum = (float)(Math.Abs((double)(gapTerm - angleTerm)) + (angleTerm + gapTerm)) * .5f;
        var supportOnlyGrounded = BitConverter.SingleToInt32Bits(maximum) < 0;
        var grounded = description.Gravity <= 0 && !description.LivingFlag4 && (supportOnlyGrounded || blocked);
        if (grounded)
        {
            var contact = ZoneLivingMovementMath.PrepareGroundContact(actual, control.Requested, normal, Vector3.UnitZ,
                position.Z, floorHeight, settings, !prior.Grounded, description.LivingFlag4, blocked,
                supportOnlyGrounded, false, settings.MinimumLandingAcceleration);
            actual = contact.Velocity;
            if (contact.RaiseAlongUp is { } raise) position.Z += raise;
            if (contact.LandingResponse is { } landing)
            {
                control.SuspensionVelocity = landing.UpVelocity;
                control.SuspensionAcceleration = landing.Acceleration;
            }
            var response = ZoneLivingMovementMath.ApplyGroundVelocityResponse(actual, control.Requested, gravity,
                normal, Vector3.UnitZ, seconds, prepared.EffectiveInertia, settings, description.LivingFlag4,
                blocked, position.Z - start.Z);
            actual = response.Velocity;
            grounded &= !contact.RequestsFlying && !response.RequestsFlying;
        }
        else
        {
            actual = ZoneLivingMovementMath.ApplyAirGravityAndResistance(actual, control.Requested, gravity,
                Vector3.Zero, prepared.EffectiveInertia, seconds, settings, !prior.Grounded,
                false, description.LivingFlag4).Velocity;
        }
        ZoneManagedTerrainGeometry.ValidateFinite(position, nameof(position));
        ZoneManagedTerrainGeometry.ValidateFinite(actual, nameof(actual));
        control.CachedGroundHeight = floorHeight;
        control.CachedGroundNormal = normal;
        control.PriorBlocked = blocked;
        control.ForceRequestedOnce = false;
        AdvanceSuspension(control, grounded, position.Z - start.Z, seconds, settings.SupportHalfHeight);
        if (!float.IsFinite(control.SuspensionOffset) || !float.IsFinite(control.SuspensionVelocity) ||
            !float.IsFinite(control.SuspensionAcceleration) || !float.IsFinite(control.SuspensionTime) ||
            !float.IsFinite(control.ImpulseRecoveryTimer))
            throw new InvalidOperationException("Living movement produced non-finite staged suspension or recovery state.");
        return new(position, actual, grounded, hasSupport ? support : null);
    }

    private readonly record struct LivingMotion(Vector3 Position, Vector3 Velocity, bool Blocked, int Constraints);

    private LivingMotion SweepLivingMotion(ZoneActorHandle actor, ZonePhysicsActorDescription description,
        LivingControl control, Vector3 origin, Vector3 displacement, Vector3 velocity)
    {
        var length = displacement.Length();
        if (length <= description.Shape.Radius * .0001f) return new(origin + displacement, velocity, false, 0);
        var position = origin;
        var remaining = displacement;
        var traveled = 0f;
        var normals = new List<Vector3>(3);
        var count = 0;
        for (; count < 3 && remaining.LengthSquared() > 1e-12f; count++)
        {
            var total = remaining.Length();
            var direction = remaining / total;
            // 3502DED0 extends the query by world clearance, then subtracts it from
            // the selected hit travel. A cast ending at the intended endpoint alone
            // sees close contacts one frame late during falls and shallow approaches.
            var queryDisplacement = direction * (total + RecoveryClearance);
            if (!TrySweepWithTarget(description.Shape, position + new Vector3(0, 0, description.CenterOffsetZ),
                queryDisplacement, actor, out var hit, out var staticTarget))
            { position += remaining; traveled += remaining.Length(); remaining = default; break; }
            var travel = Math.Clamp(hit.Distance - RecoveryClearance, 0, total);
            position += direction * travel;
            traveled += travel;
            remaining = direction * MathF.Max(0, total - travel);
            if (!normals.Any(normal => Vector3.Dot(normal, hit.Normal) >= .95f)) normals.Add(hit.Normal);
            var into = Vector3.Dot(remaining, hit.Normal);
            if (into >= 0) { position += remaining; traveled += remaining.Length(); remaining = default; break; }
            remaining -= hit.Normal * into;
            var velocityInto = Vector3.Dot(velocity, hit.Normal);
            if (velocityInto < 0)
            {
                if (staticTarget)
                {
                    var settings = description.LivingMovement!;
                    // Static v539 gate: min(cosClimb-nUp,nUp+cosFall,vUp+1)<0.
                    // Sliding the displacement does not imply rewriting actual velocity.
                    if (hit.Normal.Z > settings.MaximumClimbCosine ||
                        hit.Normal.Z < -settings.MinimumFallCosine || velocity.Z < -1)
                    {
                        velocity = ZoneLivingMovementMath.ApplyStaticContactImpulse(velocity, hit.Normal, description.Mass).Velocity;
                        if (settings.Inertia == 0) control.ImpulseRecoveryTimer = settings.ImpulseRecoveryTime;
                    }
                }
                else velocity -= hit.Normal * velocityInto;
            }
            // Original one-face slide tilts the remaining tangent slightly out of contact.
            if (normals.Count == 1 && remaining.LengthSquared() > 0)
                remaining = remaining * .99980003f + hit.Normal * (remaining.Length() * .02f);
        }
        var sum = Vector3.Zero;
        foreach (var normal in normals) sum += normal;
        var squared = sum.LengthSquared();
        var blocked = traveled < length * .001f &&
            (MathF.Abs(sum.Z) * sum.Z > squared * .16f || squared < .99f);
        // 3502DED0 rechecks the resulting pose after a non-tiny movement. For the
        // admitted fresh living flags, primary part2 is active and entity flag8 is clear.
        // Its BODY velocity=-up requests +Z unprojection, as in the stance helper.
        // This catches a body already intersecting a face while its raw sweep moves out.
        var center = position + new Vector3(0, 0, description.CenterOffsetZ);
        var integrity = Geometry.GetUpwardUnprojections(description.Shape, center).ToList();
        if (StaticGeometry is not null)
            integrity.AddRange(StaticGeometry.GetUpwardUnprojections(description.Shape, center));
        if (integrity.Count != 0)
        {
            var contact = integrity[0];
            var depth = contact.Depth <= description.LivingMovement!.SupportHalfHeight * 10
                ? contact.Depth : contact.NormalFallbackDepth;
            if (depth > RecoveryClearance) { blocked = true; velocity = Vector3.Zero; }
            // Even a shallower contact restores the pre-movement origin. Complete native
            // part/contact order and geometry fallback remain separate fidelity work.
            return new(origin, velocity, blocked, normals.Count + 1);
        }
        return new(position, velocity, blocked, normals.Count);
    }

    private static void AdvanceSuspension(LivingControl control, bool grounded, float rise, float seconds, float halfHeight)
    {
        if (grounded && rise > halfHeight * .01f)
        {
            var riseVelocity = rise / control.SuspensionTime;
            control.SuspensionVelocity = (MathF.Abs(control.SuspensionVelocity - riseVelocity) +
                (riseVelocity + control.SuspensionVelocity)) * .5f;
            control.SuspensionOffset = MathF.Min(halfHeight, control.SuspensionOffset + rise);
            control.SuspensionTime = 0;
        }
        var nextTime = control.SuspensionTime + seconds;
        var upperTime = (float)((double)nextTime + .5);
        control.SuspensionTime = (float)((double)upperTime - Math.Abs((double)nextTime - .5)) * .5f;
        var velocity = seconds * control.SuspensionAcceleration + control.SuspensionVelocity;
        var acceleration = control.SuspensionAcceleration;
        var offset = control.SuspensionOffset;
        if (acceleration == 0 && velocity * offset < 0 || offset * acceleration < 0 ||
            (offset - velocity * seconds) * offset < 0)
        { control.SuspensionOffset = control.SuspensionVelocity = control.SuspensionAcceleration = 0; }
        else
        { control.SuspensionVelocity = velocity; control.SuspensionOffset = offset - velocity * seconds; }
    }
}
