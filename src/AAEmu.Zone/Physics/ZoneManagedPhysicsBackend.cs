using System.Numerics;

using AAEmu.Zone.Actors;
using AAEmu.Zone.Content;

namespace AAEmu.Zone.Physics;

/// <summary>
/// Engine-independent character physics for one scene generation. Reuses the owned gravity and
/// ground landing integrator, with continuous terrain/static and registered-body convex queries.
/// Actor transforms are authored pivot origins; public shape query positions are centers.
/// </summary>
/// <remarks>
/// This is a character controller, not a replacement rigid-body solver. Vehicle dynamics,
/// swimming, ragdolls, actor stacking and friction response are not implemented.
/// Character colliders stay upright; arbitrary body rotation, stance transitions and the native
/// living suspension/step solver remain separate movement-fidelity work.
/// </remarks>
public sealed class ZoneManagedPhysicsBackend : IZonePhysicsBackend, IZonePhysicsMotion
{
    public const float MinimumWalkableNormalZ = .7f;
    /// <summary>Original CPhysicalWorld's default unprojection clearance (world+244).</summary>
    public const float RecoveryClearance = .01f;
    /// <summary>Native early-overlap per-geometry unprojection bound, in living collider radii.</summary>
    public const float RecoveryRadiusLimit = 2.5f;
    /// <summary>A replacement numerical work bound; the native overlap pass has no equivalent fixed cap.</summary>
    public const int RecoveryIterationLimit = 96;
    private readonly Dictionary<ZoneActorHandle, ZonePhysicsActorDescription> _actors = [];
    private readonly Dictionary<uint, ZoneActorHandle> _liveIdentities = [];
    private readonly Dictionary<uint, uint> _lastGeneration = [];
    private readonly HashSet<ZoneActorHandle> _retired = [];
    private readonly Dictionary<ZoneActorHandle, ZoneManagedActorRecovery> _lastRecovery = [];
    private readonly IZoneCollisionSurfaces? _surfaces;
    private readonly ZoneManagedSceneGroundQuery _groundQuery;

    public ZoneManagedPhysicsBackend(ZoneManagedTerrainGeometry geometry, IZoneCollisionSurfaces? surfaces = null,
        ZoneManagedStaticGeometry? staticGeometry = null)
    {
        Geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
        StaticGeometry = staticGeometry;
        _groundQuery = new(geometry, staticGeometry,
            groundPivotHeight: actor => _actors.TryGetValue(actor, out var description) ? description.GroundPivotHeight : 0,
            actorDescription: actor =>
            {
                Require(actor);
                return _actors[actor];
            });
        OwnedPhysics = new(_groundQuery);
        _surfaces = surfaces;
    }

    public ZoneManagedTerrainGeometry Geometry { get; }
    public ZoneManagedStaticGeometry? StaticGeometry { get; }
    public ZoneOwnedPlayerPhysics OwnedPhysics { get; }
    public bool HasStaticMeshCollision => StaticGeometry is { InstanceCount: > 0 };
    public bool HasVehicleDynamics => false;
    public bool CapsulesUseBoundingBoxes => false;
    public ZonePhysicalSurfaceDefinition? LastFootfallSurface { get; private set; }

    public IReadOnlyList<string> Limitations { get; } = Array.AsReadOnly(new[]
    {
        "Only decoded and admitted static collision proxies are present; scene coverage reports retain unsupported resources.",
        "Character colliders stay upright; arbitrary body rotation, stance transitions, the authored IgnoreCollision flag and native suspension/step dynamics are not implemented.",
        "Living overlap recovery is proactive; the original idle, fall and contact-cache gate is not implemented.",
        "Vehicle, rigid-body, ragdoll, swimming and actor stacking dynamics are not implemented.",
        "Material ids are resolved, but friction and elasticity response are not implemented.",
    });

    public void AddActor(ZoneActorHandle actor, ZonePhysicsActorDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        if (!Enum.IsDefined(description.Profile)) throw new ArgumentOutOfRangeException(nameof(description), "Unknown actor physics profile.");
        if (description.Profile == ZonePhysicsActorProfile.Living && description.Shape.Kind == ZonePhysicsShapeKind.Box)
            throw new ArgumentException("Authored living bodies require a capsule or cylinder shape.", nameof(description));
        ZoneManagedTerrainGeometry.ValidateShape(description.Shape);
        if (!float.IsFinite(description.Mass) || description.Mass <= 0)
            throw new ArgumentOutOfRangeException(nameof(description), "An actor requires a finite positive mass.");
        if (!float.IsFinite(description.CenterOffsetZ) || !float.IsFinite(description.GroundPivotHeight) ||
            !float.IsFinite(description.Gravity))
            throw new ArgumentOutOfRangeException(nameof(description), "Actor collider offsets, pivot height and gravity must be finite.");
        if (actor.Generation == 0 || _retired.Contains(actor) ||
            (_lastGeneration.TryGetValue(actor.UnitObjectId, out var previous) && actor.Generation <= previous))
            throw new InvalidOperationException($"Managed physics rejects retired or invalid actor generation {actor}.");
        if (_liveIdentities.ContainsKey(actor.UnitObjectId))
            throw new InvalidOperationException($"Managed physics already registers live identity {actor.UnitObjectId}; retire it before re-entry.");
        OwnedPhysics.AddActor(actor, Vector3.Zero, Quaternion.Identity, description.Gravity);
        _actors.Add(actor, description);
        _liveIdentities.Add(actor.UnitObjectId, actor);
        _lastGeneration[actor.UnitObjectId] = actor.Generation;
    }

    public void RemoveActor(ZoneActorHandle actor)
    {
        _retired.Add(actor);
        _lastRecovery.Remove(actor);
        if (!_actors.Remove(actor)) return;
        _liveIdentities.Remove(actor.UnitObjectId);
        OwnedPhysics.RemoveActor(actor);
    }

    public void Step(TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(elapsed), "Physics requires a positive explicit step.");
        // Normal scene periods use at most 50 ms per integration. Catch-up is bounded to 256
        // substeps; the complete elapsed time is still integrated rather than silently discarded.
        var count = (int)Math.Clamp(Math.Ceiling(elapsed.TotalSeconds / .05), 1, 256);
        count = (int)Math.Min(count, elapsed.Ticks);
        var ticks = elapsed.Ticks / count;
        var remainder = elapsed.Ticks % count;
        for (var i = 0; i < count; i++)
        {
            var period = TimeSpan.FromTicks(ticks + (i < remainder ? 1 : 0));
            foreach (var actor in _actors.Keys) RecoverActor(actor);
            BlockHorizontalMotion((float)period.TotalSeconds);
            var ceilings = BlockStaticMotion((float)period.TotalSeconds);
            OwnedPhysics.Step(period);
            foreach (var actor in ceilings)
            {
                OwnedPhysics.TryReadVelocity(actor, out var velocity);
                OwnedPhysics.ApplyVelocity(actor, new(velocity.X, velocity.Y, MathF.Min(velocity.Z, 0)));
            }
        }
    }

    public void Teleport(ZoneActorHandle actor, Vector3 position, Quaternion orientation)
    {
        Require(actor);
        OwnedPhysics.Teleport(actor, position, orientation);
        _lastRecovery.Remove(actor);
    }

    public void CopyStatus(ZoneActorHandle actor, out ZonePhysicsActorStatus status)
    {
        Require(actor);
        OwnedPhysics.TryReadPosition(actor, out var position);
        OwnedPhysics.TryReadOrientation(actor, out var orientation);
        OwnedPhysics.TryReadVelocity(actor, out var velocity);
        OwnedPhysics.TryReadGrounded(actor, out var grounded);
        // Native status2 (35029A30) reports airborne state for collision-active living
        // bodies; the native adapter exposes grounded as its exact complement.
        status = new(position, orientation, velocity, grounded, !grounded);
    }

    public bool TrySweep(ZonePhysicsShape shape, Vector3 origin, Vector3 displacement,
        ZoneActorHandle? ignoreActor, out ZonePhysicsHit hit)
    {
        ZoneManagedTerrainGeometry.ValidateShape(shape);
        ZoneManagedTerrainGeometry.ValidateSegment(origin, displacement);
        hit = default;
        if (displacement == Vector3.Zero) return false;
        var found = Geometry.TrySweep(shape, origin, displacement, out hit);
        if (StaticGeometry is not null && StaticGeometry.TrySweep(shape, origin, displacement, out var staticHit) &&
            (!found || staticHit.Distance < hit.Distance))
        {
            hit = staticHit;
            found = true;
        }
        if (TrySweepBodies(shape, origin, displacement, ignoreActor, out var bodyHit) && (!found || bodyHit.Distance < hit.Distance))
        {
            hit = bodyHit;
            found = true;
        }
        return found;
    }

    public bool TryCollide(ZonePhysicsShape shape, Vector3 position, ZoneActorHandle? ignoreActor)
    {
        ZoneManagedTerrainGeometry.ValidateShape(shape);
        ZoneManagedTerrainGeometry.ValidateFinite(position, nameof(position));
        if (Geometry.TryCollide(shape, position)) return true;
        if (StaticGeometry?.TryCollide(shape, position) == true) return true;
        foreach (var (actor, description) in _actors)
        {
            if (actor == ignoreActor) continue;
            if (ZoneManagedShapeQueries.Overlaps(shape, position, description.Shape, Center(actor, description))) return true;
        }
        return false;
    }

    public bool TryGround(Vector3 origin, float maxDistance, out ZonePhysicsGroundHit ground)
    {
        var found = Geometry.TryGround(origin, maxDistance, out ground);
        if (StaticGeometry is not null && StaticGeometry.TryGround(origin, maxDistance, out var support) &&
            (!found || support.Distance < ground.Distance))
        {
            ground = support;
            found = true;
        }
        LastFootfallSurface = found ? _surfaces?.Resolve(ground.PhysicalMaterialId) : null;
        return found;
    }

    public void ApplyVelocity(ZoneActorHandle actor, Vector3 velocity)
    {
        Require(actor);
        OwnedPhysics.ApplyVelocity(actor, velocity);
    }

    public void ApplyImpulse(ZoneActorHandle actor, Vector3 impulse)
    {
        Require(actor);
        OwnedPhysics.ApplyImpulse(actor, impulse);
    }

    public void ApplyPose(ZoneActorHandle actor, Vector3 position, Quaternion orientation)
    {
        Require(actor);
        ZoneManagedTerrainGeometry.ValidateFinite(position, nameof(position));
        if (!float.IsFinite(orientation.X) || !float.IsFinite(orientation.Y) ||
            !float.IsFinite(orientation.Z) || !float.IsFinite(orientation.W) ||
            !float.IsFinite(orientation.LengthSquared()) || orientation.LengthSquared() <= 0)
            throw new ArgumentException("A pose correction requires a finite nonzero orientation.", nameof(orientation));
        RecoverActor(actor);
        OwnedPhysics.TryReadPosition(actor, out var previous);
        var description = _actors[actor];
        var shape = description.Shape;
        var displacement = position - previous;
        var center = Center(actor, description);
        // Ordinary client pose corrections arrive after the physics step. They must traverse
        // the same collision scene or each new input could place the actor past a blocked wall.
        // Explicit scene-authoritative repositioning uses Teleport and bypasses this correction.
        for (var iteration = 0; iteration < 3 && displacement.LengthSquared() > 1e-12f; iteration++)
        {
            var found = Geometry.TrySweepSteepTerrain(shape, center, displacement, MinimumWalkableNormalZ, out var hit);
            if (StaticGeometry is not null && StaticGeometry.TrySweep(shape, center, displacement,
                out var staticHit, -1, displacement.Z < 0 ? 1 : MinimumWalkableNormalZ - .0001f) &&
                (!found || staticHit.Distance < hit.Distance))
            {
                hit = staticHit;
                found = true;
            }
            if (TrySweepBodies(shape, center, displacement, actor, out var bodyHit) && (!found || bodyHit.Distance < hit.Distance))
            {
                hit = bodyHit;
                found = true;
            }
            if (!found) break;
            var into = Vector3.Dot(displacement, hit.Normal);
            if (into >= 0) break;
            var fraction = Math.Clamp((hit.Distance - .001f) / displacement.Length(), 0, 1);
            displacement -= hit.Normal * (into * (1 - fraction));
        }
        OwnedPhysics.ApplyPose(actor, previous + displacement, orientation);
    }

    public ushort? GroundMaterialFor(ZoneActorHandle actor)
    {
        Require(actor);
        return OwnedPhysics.TryReadGroundMaterial(actor, out var material) ? material : null;
    }

    /// <summary>Last successful nonzero recovery for this exact actor generation; copied diagnostics only.</summary>
    public bool TryReadLastRecovery(ZoneActorHandle actor, out ZoneManagedActorRecovery recovery)
    {
        Require(actor);
        return _lastRecovery.TryGetValue(actor, out recovery);
    }

    public bool TryReadHeight(ZoneActorHandle actor, out float height)
    {
        Require(actor);
        OwnedPhysics.TryReadPosition(actor, out var position);
        height = position.Z;
        return true;
    }

    private void BlockHorizontalMotion(float seconds)
    {
        // Compute corrections before integrating any actor, so every query sees one frame snapshot.
        foreach (var (actor, description) in _actors)
        {
            OwnedPhysics.TryReadVelocity(actor, out var velocity);
            var horizontal = new Vector3(velocity.X, velocity.Y, 0);
            if ((horizontal * seconds).LengthSquared() <= 1e-12f) continue;
            var center = Center(actor, description);
            // Recheck the shortened/sliding segment so a second wall at a corner cannot be
            // crossed after the first normal changes the intended motion.
            for (var iteration = 0; iteration < 3; iteration++)
            {
                var displacement = horizontal * seconds;
                if (displacement.LengthSquared() <= 1e-12f) break;
                var found = Geometry.TrySweepSteepTerrain(description.Shape, center, displacement, MinimumWalkableNormalZ, out var hit);
                if (TrySweepBodies(description.Shape, center, displacement, actor, out var bodyHit) && (!found || bodyHit.Distance < hit.Distance))
                {
                    hit = bodyHit;
                    found = true;
                }
                if (!found) break;
                var horizontalNormal = new Vector3(hit.Normal.X, hit.Normal.Y, 0);
                if (horizontalNormal.LengthSquared() < 1e-10f) break;
                horizontalNormal = Vector3.Normalize(horizontalNormal);
                var into = Vector3.Dot(horizontal, horizontalNormal);
                if (into >= 0) break;
                var travel = displacement.Length();
                var fraction = Math.Clamp((hit.Distance - .001f) / travel, 0, 1);
                horizontal -= horizontalNormal * (into * (1 - fraction));
            }
            OwnedPhysics.ApplyVelocity(actor, new(horizontal.X, horizontal.Y, velocity.Z));
        }
    }

    private List<ZoneActorHandle> BlockStaticMotion(float seconds)
    {
        var blocked = new List<ZoneActorHandle>();
        if (StaticGeometry is null) return blocked;
        foreach (var (actor, description) in _actors)
        {
            OwnedPhysics.TryReadVelocity(actor, out var velocity);
            OwnedPhysics.TryReadGrounded(actor, out var grounded);
            if (!grounded && OwnedPhysics.TryReadPosition(actor, out var origin))
            {
                var supportOrigin = origin - new Vector3(0, 0, description.GroundPivotHeight);
                grounded = _groundQuery.TryCharacterGround(supportOrigin, supportOrigin, false, out var support, out _) &&
                    MathF.Abs(supportOrigin.Z - support.Z) <= _groundQuery.ContactTolerance;
            }
            var displacement = (velocity + new Vector3(0, 0, description.Gravity * seconds)) * seconds;
            var changed = false;
            // Sweep the actual combined trajectory. Separate horizontal and vertical casts can
            // both miss a finite overhang crossed diagonally, or block a jump over a low wall.
            for (var iteration = 0; iteration < 3 && displacement.LengthSquared() > 1e-12f; iteration++)
            {
                var maximumNormalZ = !grounded && displacement.Z <= 0 ? 1 : MinimumWalkableNormalZ - .0001f;
                if (!StaticGeometry.TrySweep(description.Shape, Center(actor, description),
                    displacement, out var hit, -1, maximumNormalZ)) break;
                var into = Vector3.Dot(displacement, hit.Normal);
                if (into >= 0) break;
                var fraction = Math.Clamp((hit.Distance - .001f) / displacement.Length(), 0, 1);
                displacement -= hit.Normal * (into * (1 - fraction));
                changed = true;
                if (hit.Normal.Z < -.01f && !blocked.Contains(actor)) blocked.Add(actor);
            }
            if (changed)
                OwnedPhysics.ApplyVelocity(actor, displacement / seconds - new Vector3(0, 0, description.Gravity * seconds));
        }
        return blocked;
    }

    private bool TrySweepBodies(ZonePhysicsShape shape, Vector3 origin, Vector3 displacement,
        ZoneActorHandle? ignoreActor, out ZonePhysicsHit hit)
    {
        hit = default;
        var best = float.PositiveInfinity;
        foreach (var (actor, description) in _actors)
        {
            if (actor == ignoreActor) continue;
            var center = Center(actor, description);
            if (!ZoneManagedShapeQueries.Sweep(shape, origin, displacement, description.Shape, center,
                out var fraction, out var normal, out var point) || fraction >= best) continue;
            best = fraction;
            hit = new(fraction * displacement.Length(), point, normal, 0);
        }
        return float.IsFinite(best);
    }

    private void RecoverActor(ZoneActorHandle actor)
    {
        var description = _actors[actor];
        OwnedPhysics.TryReadPosition(actor, out var origin);
        OwnedPhysics.TryReadVelocity(actor, out var velocity);
        var offset = new Vector3(0, 0, description.CenterOffsetZ);
        var center = origin + offset;
        if (!Finite(center)) throw RecoveryFailure(actor, "the authored collider center is non-finite");
        var correctedOrigin = origin;
        var visited = new HashSet<Vector3> { center };
        var ambiguousChoices = 0;
        for (var iteration = 0; iteration <= RecoveryIterationLimit; iteration++)
        {
            if (!TryDeepestPenetration(description, center, out var contact))
            {
                if (iteration != 0)
                {
                    // Stage the entire solve before touching authority state. The center and entity
                    // origin receive the same displacement; authored pivot/part offsets stay intact.
                    var translation = correctedOrigin - origin;
                    if (!Finite(translation) || !Finite(correctedOrigin))
                        throw RecoveryFailure(actor, "the staged entity-origin correction is non-finite");
                    OwnedPhysics.CorrectContactPose(actor, correctedOrigin, velocity);
                    _lastRecovery[actor] = new(translation, iteration, ambiguousChoices);
                }
                return;
            }
            if (iteration == RecoveryIterationLimit)
                throw RecoveryFailure(actor, "the bounded contact solve did not reach clearance");
            // Explicit box bodies keep the backend's generic kinematic profile and numerical
            // guard. A native living radius bound only exists for capsule/cylinder bodies.
            if (description.Profile == ZonePhysicsActorProfile.Living &&
                contact.Depth - ZoneManagedStaticGeometry.PenetrationTolerance > RecoveryRadiusLimit * description.Shape.Radius)
                throw RecoveryFailure(actor, "the contact exceeds the verified 2.5-radius per-geometry unprojection bound");

            var correction = contact.Normal * (contact.Depth + RecoveryClearance);
            var squared = correction.LengthSquared();
            if (!Finite(correction) || !float.IsFinite(squared) || squared <= 0)
                throw RecoveryFailure(actor, "the contact requires an unsupported or non-finite unprojection");
            var nextOrigin = correctedOrigin + correction;
            var next = nextOrigin + offset;
            if (!Finite(nextOrigin) || !Finite(next) || next == center)
                throw RecoveryFailure(actor, "the correction exceeds finite coordinate precision");
            if (!visited.Add(next))
                throw RecoveryFailure(actor, "incompatible contacts produced a recovery cycle");

            // Static early unprojection is pose-only in the original living step. Preserve all
            // velocity here; subsequent ordinary sweeps and ground support own its response.
            center = next;
            correctedOrigin = nextOrigin;
            if (contact.IsAmbiguous) ambiguousChoices++;
        }
    }

    private bool TryDeepestPenetration(ZonePhysicsActorDescription description, Vector3 center, out ZonePhysicsPenetration deepest)
    {
        var shape = description.Shape;
        var found = false;
        var best = default(ZonePhysicsPenetration);
        var support = default(ZonePhysicsGroundHit);
        var reference = center - new Vector3(0, 0, description.CenterOffsetZ + description.GroundPivotHeight);
        var preservePointSupport = description.Profile == ZonePhysicsActorProfile.Kinematic &&
            _groundQuery.TrySupportContact(reference, out support, out _);
        foreach (var contact in Geometry.GetPenetrations(shape, center)) Consider(contact);
        if (StaticGeometry is not null)
            foreach (var contact in StaticGeometry.GetPenetrations(shape, center)) Consider(contact);
        // The original early pass admits other PE_LIVING only with prior-contact byte flag 0x80.
        // Its state producer is unowned. Actor bodies still participate in ordinary sweeps
        // and overlap queries; forcing them into this solve would change native admission.
        deepest = best;
        return found;

        void Consider(ZonePhysicsPenetration contact)
        {
            // The existing generic kinematic profile follows a point support, so its footprint
            // can intersect an uphill surface while the reference point already rests on that
            // same plane. Preserve that contract without calling it native living floor policy.
            // This never suppresses closed volumes, remote floors or steep wall constraints.
            if (preservePointSupport && contact.IsSurfaceContact && contact.Normal.Z >= MinimumWalkableNormalZ &&
                MathF.Abs(Vector3.Dot(contact.Position - support.Position, support.Normal)) <= _groundQuery.ContactTolerance)
                return;
            // Exact MTDs exclude clearance. The certified numerical bracket permits at most this
            // residual; repeatedly treating it as a new penetration would move resting actors.
            if (contact.Depth <= ZoneManagedStaticGeometry.PenetrationTolerance || found && contact.Depth <= best.Depth) return;
            best = contact;
            found = true;
        }
    }

    private static InvalidOperationException RecoveryFailure(ZoneActorHandle actor, string detail) =>
        new($"Managed character recovery refused for {actor}: {detail}. Staged pose and velocity were not committed.");

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private Vector3 Center(ZoneActorHandle actor, ZonePhysicsActorDescription description)
    {
        OwnedPhysics.TryReadPosition(actor, out var origin);
        return origin + new Vector3(0, 0, description.CenterOffsetZ);
    }

    private void Require(ZoneActorHandle actor)
    {
        if (!_actors.ContainsKey(actor))
            throw new InvalidOperationException($"Managed physics does not own actor {actor}; unknown and retired generations cannot be used.");
    }

}

/// <summary>Copied last successful controller unprojection; deterministic tie choices are visible.</summary>
public readonly record struct ZoneManagedActorRecovery(Vector3 Translation, int Passes, int AmbiguousContactCount);
