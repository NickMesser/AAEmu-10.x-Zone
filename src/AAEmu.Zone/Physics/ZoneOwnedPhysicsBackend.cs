using System.Numerics;

using AAEmu.Zone.Actors;
using AAEmu.Zone.Content;

namespace AAEmu.Zone.Physics;

/// <summary>
/// The scene's OWNED physics backend (Agent 8): it makes this host the movement authority for the
/// player instead of a writer into a body the host cannot move.
///
/// Why this type is needed, in one line: the native motion path is unreachable from here — the
/// admitted player body's <c>body+0x47</c> is <c>0x03</c>, so the recovered living action defers to
/// a native frame pump this host does not own, and every motion gate correctly refuses. Rather than
/// fake the native write, this backend OWNS the transform and reports it through
/// <see cref="CopyStatus"/>, which is the same channel the frame phases already publish from, so
/// walk, jump and collision become observable end to end.
///
/// Honesty rules this type keeps:
/// <list type="bullet">
/// <item>It never claims the native body moved. Static queries are answered by the REAL scene
/// geometry supplied at construction (the native terrain world), while actor status comes from the
/// owned integration. Those are different truths and are never conflated.</item>
/// <item>Queries against registered bodies are refused by name: a managed sphere sweep is not the
/// engine's collision, and pretending otherwise is the failure mode this whole effort avoids.</item>
/// <item>Registration mirrors the lifecycle contract: one authority per actor, removal is final.</item>
/// </list>
/// </summary>
public sealed class ZoneOwnedPhysicsBackend : IZonePhysicsBackend, IZonePhysicsMotion
{
    private readonly ZoneOwnedPlayerPhysics _physics;
    private readonly ZoneOwnedPlayerPhysics.IGroundQuery? _geometry;
    private readonly IZonePhysicsBackend? _shapeQueries;
    private readonly Dictionary<ZoneActorHandle, ZonePhysicsActorDescription> _actors = [];
    private readonly Dictionary<ZoneActorHandle, Vector3> _velocity = [];
    private readonly Dictionary<ZoneActorHandle, bool> _flying = [];
    private readonly Dictionary<(ZoneActorHandle Actor, string Kind), int> _traceCounts = [];

    /// <param name="physics">The owned authority; this backend is its scene-facing surface.</param>
    /// <param name="geometry">
    /// The scene's REAL static geometry (the native terrain world adapted to a ground query), used
    /// for the owned ground resolution and for <see cref="TryGround"/>. Null leaves the authority
    /// refusing to invent a floor, exactly as the authority documents.
    /// </param>
    /// <param name="shapeQueries">
    /// The native adapter, used ONLY for shape sweeps/overlaps, which are the engine's own collision
    /// and must never be approximated. The owned authority still owns the actor transform and the
    /// step; the queries stay real. Without it those two refuse by name.
    /// </param>
    /// <param name="surfaces">
    /// Agent 7's neutral surface table, shared with the native-AI route. It turns a ground hit's
    /// material id into the AUTHORED surface (friction, footstep radii, collision flags) so the
    /// owned physics applies the same rules the game authors wrote. Null leaves footfall surfaces
    /// unclassified - the authority still lands, but it reports the default surface rather than
    /// pretending to know which one it hit.
    /// </param>
    public ZoneOwnedPhysicsBackend(ZoneOwnedPlayerPhysics physics, ZoneOwnedPlayerPhysics.IGroundQuery? geometry = null,
        IZonePhysicsBackend? shapeQueries = null, IZoneCollisionSurfaces? surfaces = null)
    {
        ArgumentNullException.ThrowIfNull(physics);
        _physics = physics;
        _geometry = geometry;
        _shapeQueries = shapeQueries;
        _surfaces = surfaces;
    }

    /// <inheritdoc/>
    public Content.ZonePhysicalSurfaceDefinition? LastFootfallSurface { get; private set; }

    private readonly IZoneCollisionSurfaces? _surfaces;

    /// <inheritdoc/>
    public void AddActor(ZoneActorHandle actor, ZonePhysicsActorDescription description)
    {
        if (_actors.ContainsKey(actor))
            throw new InvalidOperationException($"Owned backend already registers actor {actor}; one authority per actor.");
        if (description.Shape.HalfExtents.X <= 0 || description.Shape.HalfExtents.Y <= 0 || description.Shape.HalfExtents.Z <= 0)
            throw new ArgumentException("Owned backend requires a positive actor shape.", nameof(description));
        if (!float.IsFinite(description.Mass) || description.Mass <= 0)
            throw new ArgumentException("Owned backend requires a finite positive mass.", nameof(description));
        _actors.Add(actor, description);
        _velocity[actor] = Vector3.Zero;
        _flying[actor] = false;
        // The authority needs a starting pose; the scene supplies it through Teleport/ApplyPose on
        // the same frame the actor is admitted, so registration starts at the origin and is then
        // posed authoritatively. Origin is never published: PostPhase only emits after a step.
        _physics.AddActor(actor, Vector3.Zero, Quaternion.Identity);
    }

    /// <inheritdoc/>
    public void RemoveActor(ZoneActorHandle actor)
    {
        if (!_actors.Remove(actor)) return; // Idempotent for cleanup paths (contract).
        _velocity.Remove(actor);
        _flying.Remove(actor);
        if (_physics.Owns(actor)) _physics.RemoveActor(actor);
    }

    /// <inheritdoc/>
    public void Step(TimeSpan elapsed)
    {
        // Horizontal blocking is resolved BEFORE integration, from the engine's own sweep, so the
        // authority never integrates through a wall. Without this the actor walked straight through
        // solid geometry: the owned authority resolved GROUND but nothing consulted the shape query
        // the backend already holds, so "collision" was only true for terrain.
        BlockHorizontalMotion((float)elapsed.TotalSeconds);
        _physics.Step(elapsed);
        // Integration evidence: a periodic owned-position line proves the authority is the thing
        // stepping, without tracing every frame (which is the flooding mistake again).
        _steps++;
        if (_steps <= 3)
            Console.WriteLine($"OWNED backend stepped (step {_steps}, actors={_actors.Count}) - the scene frame is driving this authority");
        if (_steps % StepTraceEvery == 0 && _actors.Count > 0)
        {
            foreach (var actor in _actors.Keys)
            {
                if (!_physics.TryReadPosition(actor, out var position)) continue;
                _physics.TryReadGrounded(actor, out var grounded);
                Console.WriteLine($"OWNED step {_steps} unit={actor.UnitObjectId} position=({position.X}, {position.Y}, {position.Z}) grounded={grounded}");
            }
        }
    }

    /// <summary>
    /// Cancels the component of each actor's horizontal velocity that points into real geometry.
    ///
    /// The query is the engine's own <see cref="IZonePhysicsBackend.TrySweep"/> through the attached
    /// native adapter, so the answer is the game's real collision data, never an approximation. Only
    /// HORIZONTAL motion is adjusted: vertical belongs to gravity and the ground clamp, and a sweep
    /// that starts inside geometry is skipped rather than resolved by invention.
    /// </summary>
    private void BlockHorizontalMotion(float elapsedSeconds)
    {
        if (_shapeQueries is null) return; // no engine query attached: refuse to invent a wall
        foreach (var (actor, description) in _actors)
        {
            if (!_physics.TryReadPosition(actor, out var position)) continue;
            _physics.TryReadVelocity(actor, out var velocity);
            var horizontal = new Vector3(velocity.X, velocity.Y, 0f);
            if (horizontal.LengthSquared() <= 1e-8f) continue;
            if (!_shapeQueries.TrySweep(description.Shape, position, horizontal * elapsedSeconds, actor, out var hit)) continue;
            // Slide, not stop: remove only the INTO-SURFACE component so the actor keeps moving
            // along the wall, which is what a character controller does and what the game expects.
            var into = Vector3.Dot(horizontal, hit.Normal);
            if (into >= 0f) continue;   // the hit is behind us; nothing to block
            var corrected = horizontal - hit.Normal * into;
            _physics.ApplyVelocity(actor, new Vector3(corrected.X, corrected.Y, velocity.Z));
            _velocity[actor] = new Vector3(corrected.X, corrected.Y, velocity.Z);
            Trace("Blocked", actor, hit.Normal);
        }
    }

    private int _steps;
    private const int StepTraceEvery = 120;

    /// <inheritdoc/>
    public void Teleport(ZoneActorHandle actor, Vector3 position, Quaternion orientation)
    {
        Require(actor);
        _physics.ApplyPose(actor, position, orientation);
        Trace("Teleport", actor, position);
    }

    /// <inheritdoc/>
    public void CopyStatus(ZoneActorHandle actor, out ZonePhysicsActorStatus status)
    {
        Require(actor);
        _physics.TryReadPosition(actor, out var position);
        _physics.TryReadGrounded(actor, out var grounded);
        _physics.TryReadVelocity(actor, out var velocity);
        _physics.TryReadOrientation(actor, out var orientation);
        status = new ZonePhysicsActorStatus(position, orientation, velocity, grounded, _flying[actor]);
    }

    /// <inheritdoc/>
    public bool TrySweep(ZonePhysicsShape shape, Vector3 origin, Vector3 displacement,
        ZoneActorHandle? ignoreActor, out ZonePhysicsHit hit)
    {
        RequireFinite(origin, nameof(origin));
        RequireFinite(displacement, nameof(displacement));
        // A shape sweep is the ENGINE's collision, not ours. When the native adapter is attached the
        // answer is the engine's own; without it there is nothing honest to return, and a managed
        // approximation would silently disagree with the world it queries.
        if (_shapeQueries is null)
            throw new NotSupportedException(
                "Owned backend refuses TrySweep by name: no engine shape query is attached, and a managed approximation " +
                "would disagree with the engine's own collision. TODO(owned-physics): keep the native adapter attached.");
        return _shapeQueries.TrySweep(shape, origin, displacement, ignoreActor, out hit);
    }

    /// <inheritdoc/>
    public bool TryCollide(ZonePhysicsShape shape, Vector3 position, ZoneActorHandle? ignoreActor)
    {
        RequireFinite(position, nameof(position));
        if (_shapeQueries is null)
            throw new NotSupportedException(
                "Owned backend refuses TryCollide by name: no engine shape query is attached, so actor overlap would " +
                "have to be invented. TODO(owned-physics).");
        return _shapeQueries.TryCollide(shape, position, ignoreActor);
    }

    /// <inheritdoc/>
    public bool TryGround(Vector3 origin, float maxDistance, out ZonePhysicsGroundHit ground)
    {
        RequireFinite(origin, nameof(origin));
        if (!float.IsFinite(maxDistance) || maxDistance <= 0)
            throw new ArgumentException("Owned backend requires a finite positive ground distance.", nameof(maxDistance));
        if (_geometry is null)
            throw new NotSupportedException("Owned backend refuses TryGround by name: no scene geometry is attached, so a ground hit would have to be invented.");
        var to = new Vector3(origin.X, origin.Y, origin.Z - maxDistance);
        if (!_geometry.TryGround(origin, to, out var position, out var materialId))
        {
            ground = default;
            return false;
        }
        var distance = origin.Z - position.Z;
        // Agent 7: the hit carries the AUTHORED surface id, and the scene's neutral table resolves
        // it to the surface the game wrote (friction, footstep radii, collision flags). Both the
        // owned physics and the native-AI route read that one table, so they cannot disagree.
        LastFootfallSurface = _surfaces?.Resolve(materialId);
        ground = new ZonePhysicsGroundHit(distance, position, Vector3.UnitZ, materialId);
        return true;
    }

    /// <inheritdoc/>
    public void ApplyVelocity(ZoneActorHandle actor, Vector3 velocity)
    {
        Require(actor);
        _physics.ApplyVelocity(actor, velocity);
        _velocity[actor] = velocity;
        Trace("ApplyVelocity", actor, velocity);
    }

    /// <inheritdoc/>
    public void ApplyImpulse(ZoneActorHandle actor, Vector3 impulse)
    {
        Require(actor);
        _physics.ApplyImpulse(actor, impulse);
        _physics.TryReadVelocity(actor, out var velocity);
        _velocity[actor] = velocity;
        Trace("ApplyImpulse", actor, impulse);
    }

    /// <inheritdoc/>
    public void ApplyPose(ZoneActorHandle actor, Vector3 position, Quaternion orientation)
    {
        Require(actor);
        _physics.ApplyPose(actor, position, orientation);
        _velocity[actor] = Vector3.Zero;
        Trace("ApplyPose", actor, position);
    }

    /// <summary>
    /// The AUTHORED surface id under this actor right now, for Agent 7's consumers (the native-AI
    /// route reads it through the same table the player physics does). Null means the actor has no
    /// ground hit at all - airborne, or over terrain that is not loaded - never a made-up surface.
    /// </summary>
    public ushort? GroundMaterialFor(ZoneActorHandle actor) =>
        _physics.TryReadGroundMaterial(actor, out var material) ? material : null;

    /// <summary>
    /// The authority's current HEIGHT for an actor, for the horizontal-only pose writers.
    ///
    /// The movement correction carries a stale vertical component: it keeps publishing the height
    /// the client was admitted at (measured live: Z=98.46622 for a player standing on terrain at
    /// Z=321.1), which dragged the body off the ground and showed up as `grounded=False` with the
    /// position falling to 98.4 mid-walk. With this host as the authority, HORIZONTAL motion is the
    /// correction's business and VERTICAL is physics' — so a pose writer that only means "you moved
    /// this way" can keep its own height.
    /// </summary>
    public bool TryReadHeight(ZoneActorHandle actor, out float height)
    {
        if (_physics.TryReadPosition(actor, out var position))
        {
            height = position.Z;
            return true;
        }
        height = 0f;
        return false;
    }

    /// <summary>
    /// Evidence for the owned path: the first few writes of each kind per actor are traced, with the
    /// OWNED position after the write. A standing trace per write would flood the log (the same
    /// mistake the separation refusals already made once), so it is bounded and then counted only.
    /// </summary>
    private void Trace(string kind, ZoneActorHandle actor, Vector3 value)
    {
        var key = (actor, kind);
        _traceCounts.TryGetValue(key, out var seen);
        _traceCounts[key] = seen + 1;
        if (seen >= 3) return;
        _physics.TryReadPosition(actor, out var ownedPosition);
        Console.WriteLine($"OWNED {kind} unit={actor.UnitObjectId} gen={actor.Generation} requested=({value.X}, {value.Y}, {value.Z}) " +
            $"ownedPosition=({ownedPosition.X}, {ownedPosition.Y}, {ownedPosition.Z}) (trace {seen + 1}/3)");
    }

    private void Require(ZoneActorHandle actor)
    {
        if (!_actors.ContainsKey(actor))
            throw new InvalidOperationException($"Owned backend has no actor {actor}; a write must never reach a retired generation.");
    }

    private static void RequireFinite(Vector3 value, string name)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            throw new ArgumentException($"Owned backend refused non-finite {name}.", name);
    }
}
