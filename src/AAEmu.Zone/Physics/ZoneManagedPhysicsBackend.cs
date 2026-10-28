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
    private readonly Dictionary<ZoneActorHandle, ZonePhysicsActorDescription> _actors = [];
    private readonly Dictionary<uint, ZoneActorHandle> _liveIdentities = [];
    private readonly Dictionary<uint, uint> _lastGeneration = [];
    private readonly HashSet<ZoneActorHandle> _retired = [];
    private readonly IZoneCollisionSurfaces? _surfaces;
    private readonly ZoneManagedSceneGroundQuery _groundQuery;

    public ZoneManagedPhysicsBackend(ZoneManagedTerrainGeometry geometry, IZoneCollisionSurfaces? surfaces = null,
        ZoneManagedStaticGeometry? staticGeometry = null)
    {
        Geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
        StaticGeometry = staticGeometry;
        _groundQuery = new(geometry, staticGeometry,
            groundPivotHeight: actor => _actors.TryGetValue(actor, out var description) ? description.GroundPivotHeight : 0);
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
        "Vehicle, rigid-body, ragdoll, swimming and actor stacking dynamics are not implemented.",
        "Material ids are resolved, but friction and elasticity response are not implemented.",
    });

    public void AddActor(ZoneActorHandle actor, ZonePhysicsActorDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
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
        OwnedPhysics.ApplyPose(actor, position, orientation);
    }

    public void CopyStatus(ZoneActorHandle actor, out ZonePhysicsActorStatus status)
    {
        Require(actor);
        OwnedPhysics.TryReadPosition(actor, out var position);
        OwnedPhysics.TryReadOrientation(actor, out var orientation);
        OwnedPhysics.TryReadVelocity(actor, out var velocity);
        OwnedPhysics.TryReadGrounded(actor, out var grounded);
        status = new(position, orientation, velocity, grounded, false);
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
